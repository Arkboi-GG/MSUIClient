using System.Numerics;
using MSUIClient.Formats;

namespace MSUIClient.World.Units;

/// <summary>Authored M2 colour-alpha × transparency-weight for an attached item batch.</summary>
public static class AttachedItemMaterialLaw
{
    public readonly record struct Sample(Vector3 Tint, float Alpha, Vector2 UvOffset)
    {
        public bool Visible => Alpha > 0f;
        public bool Translucent => Alpha < 1f - 0.0001f;
    }

    /// <summary>
    /// Attached models rest in sequence zero while their material tracks keep the model clock.
    /// Missing links are identity factors, matching the M2 alpha-combine contract.
    /// </summary>
    public static Sample At(M2Model model, M2Batch? batch, float seconds)
    {
        if (batch is null) return new(Vector3.One, 1f, Vector2.Zero);
        const int restingSequence = 0;
        Vector3 tint = Vector3.One;
        float alpha = 1f;
        Vector2 uvOffset = Vector2.Zero;

        if (batch.ColorIndex >= 0 && batch.ColorIndex < model.Colors.Count)
        {
            M2ColorAnimation color = model.Colors[batch.ColorIndex];
            tint = M2TrackSampling.Vector(color.Color, model, restingSequence,
                seconds, Vector3.One);
            alpha *= M2TrackSampling.Fixed16(color.Alpha, model, restingSequence,
                seconds);
        }

        if (batch.TextureWeightIndex < model.TransparencyLookup.Count)
        {
            int track = model.TransparencyLookup[batch.TextureWeightIndex];
            if (track >= 0 && track < model.TransparencyTracks.Count)
                alpha *= M2TrackSampling.Fixed16(model.TransparencyTracks[track], model,
                    restingSequence, seconds);
        }

        uvOffset = UvOffsetAt(model, batch, 0, seconds);

        if (!float.IsFinite(alpha)) alpha = 0f;
        if (!float.IsFinite(tint.X) || !float.IsFinite(tint.Y) || !float.IsFinite(tint.Z))
            tint = Vector3.One;
        if (!float.IsFinite(uvOffset.X) || !float.IsFinite(uvOffset.Y))
            uvOffset = Vector2.Zero;
        return new(Vector3.Max(tint, Vector3.Zero), Math.Clamp(alpha, 0f, 1f), uvOffset);
    }

    /// <summary>Sample the authored UV translation for one texture unit.</summary>
    public static Vector2 UvOffsetAt(M2Model model, M2Batch batch, int unit, float seconds)
    {
        int textureTransform = model.GetTextureTransformForBatchUnit(batch, unit);
        if (textureTransform < 0 || textureTransform >= model.TextureTransforms.Count)
            return Vector2.Zero;

        Vector3 offset = M2TrackSampling.Vector(
            model.TextureTransforms[textureTransform].Translation, model,
            0, seconds, Vector3.Zero);
        return float.IsFinite(offset.X) && float.IsFinite(offset.Y)
            ? new Vector2(offset.X, offset.Y)
            : Vector2.Zero;
    }

    public static int FogPolicy(int blendMode, bool unfogged) => unfogged ? 4 : blendMode switch
    {
        3 or 4 => 1,
        5 => 2,
        6 => 3,
        _ => 0,
    };

    /// <summary>
    /// Exact build-5875 Model2 generated environment coordinate. The vertex
    /// program reflects the view-space position around the view-space normal,
    /// normalizes that vector, and remaps its XY lanes from -1..1 to 0..1.
    /// </summary>
    public static Vector2 EnvironmentUv(Vector3 viewPosition, Vector3 viewNormal)
    {
        if (viewPosition.LengthSquared() <= 0.0000001f ||
            viewNormal.LengthSquared() <= 0.0000001f)
            return new Vector2(0.5f);

        Vector3 normal = Vector3.Normalize(viewNormal);
        Vector3 reflected = viewPosition -
            2f * Vector3.Dot(viewPosition, normal) * normal;
        if (reflected.LengthSquared() <= 0.0000001f) return new Vector2(0.5f);
        reflected = Vector3.Normalize(reflected);
        return new Vector2(reflected.X, reflected.Y) * 0.5f + new Vector2(0.5f);
    }

    /// <summary>
    /// The Warglaive blade families use an opaque energy base and a matching
    /// ArmorReflect3 overlay. MSUI's directional booth light otherwise makes
    /// that entire authored energy surface brighten and darken as the hand
    /// turns, so these specific blade passes use stable effect lighting.
    /// </summary>
    public static bool UsesSteadyWarglaiveBlade(string modelPath)
    {
        string stem = Path.GetFileNameWithoutExtension(modelPath);
        return stem.Equals("Glave_1H_DualBlade_D_01", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("Glave_1H_DualBlade_D_01Left", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("Glave_1H_Short_B_01", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSteadyWarglaiveBladeBatch(
        string modelPath, M2Model model, M2Batch batch)
    {
        if (!UsesSteadyWarglaiveBlade(modelPath)) return false;
        if (model.UsesEnvironmentMapForBatch(batch)) return true;

        var flags = batch.MaterialIndex < model.RenderFlags.Count
            ? model.RenderFlags[batch.MaterialIndex]
            : null;
        bool transparent = (flags?.BlendingMode ?? 0) >= 2 ||
                           (flags?.NoZWrite ?? false);
        if (transparent) return false;

        return model.Batches.Any(other =>
            other.SubmeshIndex == batch.SubmeshIndex &&
            model.UsesEnvironmentMapForBatch(other));
    }
}
