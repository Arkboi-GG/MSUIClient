using System.Globalization;

namespace MSUIClient.Engine;

/// <summary>
/// Current desktop Benilla's per-character camera-settings text contract. MSUI's live pitch uses
/// the reference convention already (positive means the camera is above the target, looking down),
/// so only radians/degrees conversion is required; no sign change belongs here.
/// </summary>
public static class CameraPoseLaw
{
    public const string DistanceKey = "cameraDistance";
    public const string PitchKey = "cameraPitch";

    public readonly record struct Pose(float? Distance, float? PitchRadians);

    public static string Render(float distance, float pitchRadians) =>
        FormattableString.Invariant(
            $"{DistanceKey} {distance:F6}\n{PitchKey} {pitchRadians * 180f / MathF.PI:F6}\n");

    public static Pose Parse(string text, float minimumDistance, float maximumDistance,
        float pitchLimit)
    {
        float? distance = null;
        float? pitch = null;
        foreach (string line in text.Split('\n'))
        {
            string[] parts = line.Split((char[]?)null,
                StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 ||
                !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture,
                    out float value) || !float.IsFinite(value))
                continue;
            if (parts[0].Equals(DistanceKey, StringComparison.OrdinalIgnoreCase))
                distance = Math.Clamp(value, minimumDistance, maximumDistance);
            else if (parts[0].Equals(PitchKey, StringComparison.OrdinalIgnoreCase))
                pitch = Math.Clamp(value * MathF.PI / 180f, -pitchLimit, pitchLimit);
        }
        return new(distance, pitch);
    }

    /// <summary>Benilla local-state path component: ASCII letters/digits survive, all else '_'.</summary>
    public static string FileToken(string value)
    {
        string token = new(value.Select(character => char.IsAsciiLetterOrDigit(character)
            ? character : '_').ToArray());
        return token.Length == 0 ? "unknown" : token;
    }

    public static string CharacterFileName(string realm, string character) =>
        $"{FileToken(realm)}-{FileToken(character)}.txt";
}
