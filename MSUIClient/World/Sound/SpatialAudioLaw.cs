using System.Numerics;

namespace MSUIClient.World.Sound;

/// <summary>
/// The reference-owned half of 3-D audio before the backend: character listener pose,
/// FMOD-style inverse rolloff, the final ten-percent cutoff fade, and a stereo balance
/// projection for the Windows waveOut backend.
/// </summary>
public static class SpatialAudioLaw
{
    public const float CharacterEarHeight = 1.7f;
    public const float RolloffFactor = 4f;

    public static Vector3 CharacterListener(Vector3 feet) =>
        feet + new Vector3(0f, 0f, CharacterEarHeight);

    /// <summary>Linear amplitude for one positional SoundEntries row.</summary>
    public static float Gain(float authoredVolume, float minDistance, float cutoffDistance,
        Vector3 source, Vector3 listener)
    {
        float volume = float.IsFinite(authoredVolume)
            ? Math.Clamp(authoredVolume, 0f, 1f) : 1f;
        if (!(cutoffDistance > 0f)) return volume;

        float distanceSquared = Vector3.DistanceSquared(source, listener);
        if (!(cutoffDistance * cutoffDistance > distanceSquared)) return 0f;
        float distance = MathF.Sqrt(distanceSquared);
        float rolloff = minDistance <= 0f || distance <= minDistance
            ? 1f
            : minDistance / (minDistance + RolloffFactor * (distance - minDistance));
        float band = cutoffDistance * .1f;
        float nearField = band <= 0f
            ? 1f
            : 1f - Math.Clamp(distance - cutoffDistance * .9f, 0f, band) / band;
        return volume * rolloff * nearField;
    }

    /// <summary>
    /// -1 is full left and +1 full right relative to character facing. WoW world facing zero
    /// points +X; its right vector is therefore (sin(yaw), -cos(yaw)).
    /// </summary>
    public static float Pan(Vector3 source, Vector3 listener, float listenerYaw)
    {
        Vector2 offset = new(source.X - listener.X, source.Y - listener.Y);
        if (offset.LengthSquared() <= 1e-8f) return 0f;
        offset = Vector2.Normalize(offset);
        Vector2 right = new(MathF.Sin(listenerYaw), -MathF.Cos(listenerYaw));
        return Math.Clamp(Vector2.Dot(offset, right), -1f, 1f);
    }

    /// <summary>
    /// waveOut exposes per-channel levels rather than a spatial track. Preserve the authored
    /// gain in the nearer ear and linearly attenuate the opposite ear toward a hard side.
    /// </summary>
    public static (float Left, float Right) StereoLevels(float gain, float pan)
    {
        gain = Math.Clamp(gain, 0f, 1f);
        pan = Math.Clamp(pan, -1f, 1f);
        return pan >= 0f
            ? (gain * (1f - pan), gain)
            : (gain, gain * (1f + pan));
    }
}
