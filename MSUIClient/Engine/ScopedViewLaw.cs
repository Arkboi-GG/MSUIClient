namespace MSUIClient.Engine;

/// <summary>
/// Build-5875's client-local SPELL_AURA_FAR_SIGHT camera rule. Despite its name, aura 76 is the
/// spyglass scope; server-authored Bind Sight / PLAYER_FARSIGHT is a separate mechanism.
/// </summary>
public static class ScopedViewLaw
{
    public const uint FarSightAura = 76;
    public const float ReferenceDefaultDegrees = 90f;
    public const float FirstPersonDistance = 0.25f;

    /// <summary>Resolve the zoom fraction from the same effect lane that carries aura 76.</summary>
    public static float? ZoomFraction(uint[]? auraIds, int[]? effectMiscValues)
    {
        if (auraIds is null || effectMiscValues is null) return null;
        int lanes = Math.Min(auraIds.Length, effectMiscValues.Length);
        for (int lane = 0; lane < lanes; lane++)
        {
            if (auraIds[lane] != FarSightAura || effectMiscValues[lane] <= 0) continue;
            return effectMiscValues[lane] / ReferenceDefaultDegrees;
        }
        return null;
    }

    public static float VerticalFieldOfViewRadians(float normalDegrees, float zoomFraction) =>
        normalDegrees * MathF.PI / 180f * zoomFraction;
}
