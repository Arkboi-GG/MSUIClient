using System.Numerics;
using MSUIClient.Formats;

namespace MSUIClient.World.Units;

/// <summary>
/// A unit's render pose for consumers which must ride the same animated M2.
/// The transform is absolute world space (never camera-relative) and the skin
/// matrices are the exact matrices used for the unit's most recent draw.
/// </summary>
public readonly record struct SpellUnitPose(
    bool Found,
    Vector3 Position,
    float Yaw,
    Matrix4x4 UnitTransform,
    M2Model? Model,
    IReadOnlyList<Matrix4x4>? Skin)
{
    public static SpellUnitPose Missing => default;

    public Matrix4x4? BoneMatrix(int index)
    {
        if (Model is null || Skin is null || index < 0 || index >= Skin.Count ||
            index >= Model.Bones.Count) return null;

        // Animator skin matrices include T(-pivot). Attachments need the posed
        // bone's model-space transform, so put the pivot back first.
        return Matrix4x4.CreateTranslation(Model.Bones[index].Pivot) * Skin[index];
    }
}

/// <summary>
/// Where a spell-visual effect actually hangs on a unit.
///
/// WHAT THIS REPLACES, AND WHY IT IS THE HEADLINE FIX
///   SpellEffectSource.AttachmentOffset() was a hardcoded guess table:
///
///       0x15 => new(.28f, .18f, 1.05f)   // "left hand", allegedly
///
///   A FIXED offset from the unit's origin, rotated by yaw. It never read the
///   model's real attachment table and it never moved with the animation. So a
///   cast effect sat at a constant point somewhere near the torso while the
///   hand it was supposed to be in swung through a cast — which is exactly the
///   observed symptom: "no glow at the hands." The effect was being drawn; it
///   was drawn in the wrong place, at a size and position unrelated to the body.
///
///   Every M2 ships an attachment table: an attachment id, the BONE it belongs
///   to, and a position in that bone's local space. The reference client hangs
///   effects off those joints and RE-RESOLVES THEM EVERY FRAME from the live
///   bone matrix, so the effect rides the animating skeleton. That is what this
///   type does.
///
/// THE FALLBACK CASCADE
///   A model that lacks the requested attachment does not drop its effect. The
///   reference retries 0x0F, then 0x13, then falls back to the unit's base
///   transform. Reproduced in <see cref="Resolve"/>.
///
/// NO RENDERING HERE. This is resolution only: it consumes a bone-matrix
/// accessor supplied by whoever owns the posed skeleton and returns a matrix.
/// It is deliberately pure so it can be unit-tested against a loaded M2 without
/// a device, a GPU, or a running client.
/// </summary>
public static class SpellAttachment
{
    /// <summary>The reference client's retry order when an attachment is absent.</summary>
    public static readonly ushort[] Fallbacks = [0x0F, 0x13];

    public readonly record struct Point(int BoneIndex, Vector3 Local, ushort ResolvedId, bool WasFallback);

    /// <summary>
    /// Find exactly one authored attachment id, with no spell-effect fallback cascade.
    /// Some consumers (notably the client's TalkToMe quest marker) deliberately stay
    /// invisible when their required overhead attachment is absent.
    /// </summary>
    public static Point? ResolveExact(M2Model model, ushort attachmentId)
        => Find(model, attachmentId) is { } exact
            ? exact with { ResolvedId = attachmentId, WasFallback = false }
            : null;

    /// <summary>
    /// Find an attachment on a model, applying the fallback cascade.
    ///
    /// Lookup order per id: AttachmentLookup (the id-indexed table, when present
    /// and in range) then a linear scan by Id — vanilla models populate one or
    /// the other inconsistently and a lookup-only implementation silently loses
    /// attachments on the models that only carry the linear table.
    ///
    /// Returns null only when the requested id AND every fallback are absent;
    /// the caller then anchors at the unit's base transform.
    /// </summary>
    public static Point? Resolve(M2Model model, ushort attachmentId)
    {
        if (Find(model, attachmentId) is { } exact)
            return exact with { ResolvedId = attachmentId, WasFallback = false };

        foreach (ushort alt in Fallbacks)
            if (Find(model, alt) is { } fb)
                return fb with { ResolvedId = alt, WasFallback = true };

        return null;
    }

    private static Point? Find(M2Model model, ushort id)
    {
        if (model.AttachmentLookup.Count > id)
        {
            short index = model.AttachmentLookup[id];
            if (index >= 0 && index < model.Attachments.Count)
            {
                M2Attachment a = model.Attachments[index];
                return new Point((int)a.BoneIndex, a.Position, id, false);
            }
        }

        foreach (M2Attachment a in model.Attachments)
            if (a.Id == id)
                return new Point((int)a.BoneIndex, a.Position, id, false);

        return null;
    }

    /// <summary>
    /// The world transform an effect instance should ride this frame.
    ///
    /// <paramref name="boneMatrix"/> returns the unit's CURRENT posed bone
    /// matrix (model space) for a bone index, or null when that bone has no pose
    /// this frame. Call this EVERY FRAME — caching the result is the bug this
    /// type exists to remove, because a cached matrix is exactly the old fixed
    /// offset wearing a better name.
    ///
    /// Falls back, in order: posed bone -> the bone's static pivot (an unanimated
    /// model still puts the effect on the right body part) -> the unit's base.
    /// </summary>
    public static Matrix4x4 World(M2Model model, in Point point, Matrix4x4 unitTransform,
        Func<int, Matrix4x4?> boneMatrix)
    {
        // M2Attachment.Position is MODEL SPACE (M2Reader.ParseAttachments), so it is posed by the
        // bone's RAW skinning matrix and NOTHING is added — exactly like item vertices and item
        // attachments in AttachedItemRenderer:  T(pos) * Skin[bone] * unit.
        //
        // The supplied accessor returns the POSED BONE FRAME  T(pivot)·Skin  (SpellUnitPose
        // .BoneMatrix re-adds the pivot). Composing a model-space point through that frame adds an
        // extra T(pivot), sliding the point off the joint by ~the bone's pivot offset — on a
        // spell-hand point that is the whole hand-to-elbow gap, the "fire on the elbow" bug.
        // Cancel it by subtracting the pivot from the model-space position first:
        //     T(pos − pivot) · (T(pivot)·Skin) · unit  =  T(pos) · Skin · unit.
        Vector3 pivot = point.BoneIndex >= 0 && point.BoneIndex < model.Bones.Count
            ? model.Bones[point.BoneIndex].Pivot : Vector3.Zero;

        if (boneMatrix(point.BoneIndex) is { } posed)
            return Matrix4x4.CreateTranslation(point.Local - pivot) * posed * unitTransform;

        // No live pose this frame (unanimated model): a model-space point rides straight to world.
        return Matrix4x4.CreateTranslation(point.Local) * unitTransform;
    }

    /// <summary>
    /// How long a self-terminating instance lives: one pass of the model's first
    /// sequence. The reference's stage-0/1 completion callback runs on the
    /// sequence clock WHETHER OR NOT that sequence moves a bone — a zero-key
    /// sequence still defines a duration, and effects rely on it.
    ///
    /// A model with no sequence table at all gets <see cref="FallbackSpan"/>:
    /// long enough to read as a flash, short enough never to linger. This
    /// replaces the flat 1.25 s literal, which cut long effects off mid-play and
    /// left short ones hanging.
    /// </summary>
    public const double FallbackSpan = 1.0;

    public static double SelfTerminatingSpan(M2Model model)
    {
        if (model.Sequences.Count == 0) return FallbackSpan;
        M2Sequence first = model.Sequences[0];
        double ms = (double)first.EndTimestamp - first.StartTimestamp;
        return ms > 1 ? ms / 1000.0 : FallbackSpan;
    }

    /// <summary>
    /// Does this instance actually have anything to show?
    ///
    /// The distinction that hid the defect for two nights: "the asset resolved"
    /// and "the renderer drew something" are different claims. These effect
    /// models are largely EMITTER CARRIERS — attaching the mesh alone renders as
    /// nothing at all. An instrument that reports PRESENT because a path
    /// resolved is measuring the wrong thing, so the verdict must ask this.
    /// </summary>
    public static bool HasVisibleContent(M2Model model)
        => model.ParticleEmitters.Count > 0 || model.RibbonEmitters.Count > 0 ||
           (model.IsValid && model.Submeshes.Count > 0);
}
