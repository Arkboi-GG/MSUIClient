using System.Numerics;
using ImGuiNET;
using MSUIClient.Creator;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// Spell workshop: BONES (shared_docs/SPELL_CREATOR_IDE.md §2.6)
//
// The rotation story, honestly told: an emitter, a ribbon and every mesh vertex
// ride a bone, and the bone's pose is what points them. So the workshop edits
// bones directly (pivot, rotation, translation; a static pose that overrides an
// animated track when asked), re-parents emitters and ribbons to any bone, and
// draws the skeleton in the world (b<n> labels) so the choice is made by sight.
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    private static readonly string[] CreatorHeadTailModes = { "0 Head quads", "1 Tail (stretched)", "2 Head + tail" };

    /// <summary>The bone the phase's BONES section is looking at, per model path.</summary>
    private readonly Dictionary<string, int> _creatorBonePick = new(StringComparer.OrdinalIgnoreCase);

    private int CreatorPickedBone(CreatorModelDoc model) =>
        model.Bones.Count == 0 ? 0 : Math.Clamp(_creatorBonePick.GetValueOrDefault(model.Path, 0), 0, model.Bones.Count - 1);

    private static string CreatorBoneLabel(BoneSnapshot b) =>
        $"b{b.Index}" + (b.KeyBoneId >= 0 ? $"  key {b.KeyBoneId}" : "") + $"  parent {b.Parent}" +
        (b.RotationKeys > 1 ? $"  rot x{b.RotationKeys}" : b.RotationKeys == 1 ? "  rot 1" : "  no rot") +
        (b.Children > 0 ? $"  {b.Children} child" : "");

    /// <summary>0xAARRGGBB (the M2's inline colour word) as an ImGui RGBA vector.</summary>
    private static Vector4 CreatorArgbToVector(uint argb) => new(
        ((argb >> 16) & 0xFF) / 255f, ((argb >> 8) & 0xFF) / 255f, (argb & 0xFF) / 255f, (argb >> 24) / 255f);

    private static uint CreatorVectorToArgb(Vector4 rgba)
    {
        uint C(float v) => (uint)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);
        return (C(rgba.W) << 24) | (C(rgba.X) << 16) | (C(rgba.Y) << 8) | C(rgba.Z);
    }

    /// <summary>The BONES editor for a phase model. Returns true when a rebuild is due.</summary>
    private bool DrawCreatorBonesBody(CreatorModelDoc model)
    {
        float cs = CreatorUiScale;
        bool dirty = false;
        if (model.Bones.Count == 0)
        {
            ImGui.TextDisabled("(this model has no bones)");
            return false;
        }
        int pick = CreatorPickedBone(model);

        // WHOSE skeleton is this? Two are in play and confusing them is the single easiest
        // mistake to make here: the CASTER has ~110 bones and the attachment points (head,
        // chest, base, hands, breath) - and this panel is not showing those. It shows the
        // EFFECT MODEL's own skeleton, which for a sketch is one root plus one bone per piece.
        // "Only 2 bones" for a one-piece sketch is correct, and the panel should say why
        // instead of leaving it to be inferred.
        bool sketchModel = _creatorSpell is { } boneOwner &&
                           TryGetSketchForModel(boneOwner, model.Path, out Creator.Sketch.SketchDoc boneSketch);
        Creator.Sketch.SketchDoc? namedSketch = null;
        if (_creatorSpell is { } owner2) TryGetSketchForModel(owner2, model.Path, out namedSketch!);

        string[] names = sketchModel && namedSketch is not null
            ? Enumerable.Range(0, model.Bones.Count).Select(i => SketchBoneName(namedSketch, i)).ToArray()
            : model.Bones.Select(CreatorBoneLabel).ToArray();

        if (sketchModel)
        {
            ImGui.TextDisabled($"this EFFECT's own skeleton: 1 root + 1 per piece = {model.Bones.Count}");
            CreatorHelp("Two different skeletons are involved and this panel shows the second one.\n\n" +
                "The CASTER's skeleton is the character's own - about a hundred bones, including " +
                "the attachment points named head, chest, base (feet), left hand, right hand and " +
                "breath. You do not edit those here; you pick ONE of them with the Slot dropdown " +
                "in the Sketch window, and that decides where the whole effect hangs and which " +
                "way its \"forward\" points.\n\n" +
                "This model has its OWN, much smaller skeleton: a root, plus one bone for each " +
                "piece you have drawn. One crescent = 2 bones. Three crescents = 4. Each piece's " +
                "bone is what carries its placement, its travel and its spin, which is why moving " +
                "a piece moves exactly one bone.\n\n" +
                "Blizzard's own effects look bigger for the same reason: Cleave's cast model has " +
                "10 bones because it carries three painted planes AND three particle emitters.");
        }

        ImGui.SetNextItemWidth(CreatorControlWidth);
        if (ImGui.Combo("Bone", ref pick, names, names.Length)) _creatorBonePick[model.Path] = pick;
        CreatorHelp("Every emitter, ribbon and mesh vertex rides a bone; the bone's pose is what " +
            "points it. Pick one here (turn on 'Show bones' in the Void stage row to see b<n> " +
            "labels in the world), then move its pivot or pose it. Re-parent an emitter or " +
            "ribbon to a bone in its own editor.");

        BoneSnapshot bone = model.Bones[pick];
        BonePatch edit = model.BoneEdits.TryGetValue(pick, out var found) ? found : new BonePatch { BoneIndex = pick };
        void Keep()
        {
            if (edit.IsEmpty) model.BoneEdits.Remove(pick);
            else model.BoneEdits[pick] = edit;
        }
        ImGui.TextDisabled($"flags 0x{bone.Flags:X}, submesh id {bone.SubmeshId}, translation keys " +
                           $"{bone.TranslationKeys}, scale keys {bone.ScaleKeys}" +
                           (bone.Animated ? "  (animated)" : "  (static)"));

        var pivot = new Vector3(edit.PivotX ?? bone.PivotX, edit.PivotY ?? bone.PivotY, edit.PivotZ ?? bone.PivotZ);
        ImGui.SetNextItemWidth(CreatorControlWidth);
        if (ImGui.DragFloat3("Pivot", ref pivot, 0.02f, -50f, 50f, "%.3f"))
        {
            edit.PivotX = pivot.X; edit.PivotY = pivot.Y; edit.PivotZ = pivot.Z;
            dirty = true;
        }
        if (CreatorResetKnob("bonepivot") && (edit.PivotX is not null || edit.PivotY is not null || edit.PivotZ is not null))
        {
            edit.PivotX = edit.PivotY = edit.PivotZ = null;
            dirty = true;
        }
        CreatorHelp("The bone's pivot in WoW's Z-up local frame: the point it rotates about, and " +
            $"the origin an emitter riding it is born relative to. Authored: {bone.PivotX:0.###}, " +
            $"{bone.PivotY:0.###}, {bone.PivotZ:0.###}.");

        if (bone.RotationFirst is { } rq)
        {
            Vector3 euler = edit.RotationEulerDegrees ?? M2BoneParser.QuaternionToEuler(edit.Rotation ?? rq);
            ImGui.SetNextItemWidth(CreatorControlWidth);
            if (ImGui.DragFloat3(bone.RotationKeys > 1 ? "Rotation (deg) *" : "Rotation (deg)", ref euler, 0.5f, -360f, 360f, "%.1f"))
            {
                edit.RotationEulerDegrees = euler;
                edit.Rotation = null;   // the dial takes over from a ring drag's exact pose
                dirty = true;
            }
            if (CreatorResetKnob("bonerot") && (edit.RotationEulerDegrees is not null || edit.Rotation is not null))
            {
                edit.RotationEulerDegrees = null;
                edit.Rotation = null;
                dirty = true;
            }
            CreatorHelp("Roll / pitch / yaw in degrees about the raw X / Y / Z axes, written as the " +
                "rotation track's first key. With 'Show bones' on, the picked bone wears three rings " +
                "in the world: drag one to rotate by sight (Shift snaps to 5 degrees)" +
                (bone.RotationKeys > 1
                    ? $". Animated ({bone.RotationKeys} keys): tick 'Pose overrides animation' below to write every key and freeze the sweep in this pose."
                    : "."));
        }
        else ImGui.TextDisabled("(no rotation keys - this bone cannot be rotated; re-parent to one that has them)");

        if (bone.TranslationFirst is { } authoredTranslation)
        {
            var translation = new Vector3(edit.TranslationX ?? authoredTranslation.X,
                edit.TranslationY ?? authoredTranslation.Y, edit.TranslationZ ?? authoredTranslation.Z);
            ImGui.SetNextItemWidth(CreatorControlWidth);
            if (ImGui.DragFloat3(bone.TranslationKeys > 1 ? "Translation *" : "Translation", ref translation, 0.02f, -50f, 50f, "%.3f"))
            {
                edit.TranslationX = translation.X; edit.TranslationY = translation.Y; edit.TranslationZ = translation.Z;
                dirty = true;
            }
            if (CreatorResetKnob("bonetrans") &&
                (edit.TranslationX is not null || edit.TranslationY is not null || edit.TranslationZ is not null))
            {
                edit.TranslationX = edit.TranslationY = edit.TranslationZ = null;
                dirty = true;
            }
            CreatorHelp("The bone's translation key (raw frame): an offset from its rest position.");
        }

        if (bone.Animated)
        {
            bool all = edit.AllKeys;
            if (ImGui.Checkbox("Pose overrides animation (write every key)", ref all))
            {
                edit.AllKeys = all;
                dirty = true;
            }
            CreatorHelp("Off: your rotation/translation sets the FIRST key and the authored motion " +
                "continues from there. On: every key is written, so the bone holds your pose " +
                "for the whole animation (the sweep stops).");
        }

        if (ImGui.SmallButton("Reset bone") && model.BoneEdits.Remove(pick)) dirty = true;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Clear every override on this bone.");
        if (model.BoneEdits.Count > 0)
            ImGui.TextDisabled($"{model.BoneEdits.Count} bone(s) edited: " +
                               string.Join(", ", model.BoneEdits.Keys.OrderBy(k => k).Select(k => $"b{k}")));
        Keep();
        return dirty;
    }
}
