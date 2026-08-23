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

        int textureTransform = model.GetTextureTransformForBatch(batch);
        if (textureTransform >= 0 && textureTransform < model.TextureTransforms.Count)
        {
            Vector3 offset = M2TrackSampling.Vector(
                model.TextureTransforms[textureTransform].Translation, model,
                restingSequence, seconds, Vector3.Zero);
            uvOffset = new Vector2(offset.X, offset.Y);
        }

        if (!float.IsFinite(alpha)) alpha = 0f;
        if (!float.IsFinite(tint.X) || !float.IsFinite(tint.Y) || !float.IsFinite(tint.Z))
            tint = Vector3.One;
        if (!float.IsFinite(uvOffset.X) || !float.IsFinite(uvOffset.Y))
            uvOffset = Vector2.Zero;
        return new(Vector3.Max(tint, Vector3.Zero), Math.Clamp(alpha, 0f, 1f), uvOffset);
    }

    public static int FogPolicy(int blendMode, bool unfogged) => unfogged ? 4 : blendMode switch
    {
        3 or 4 => 1,
        5 => 2,
        6 => 3,
        _ => 0,
    };
}
