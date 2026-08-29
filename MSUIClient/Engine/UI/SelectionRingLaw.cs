using System.Numerics;
using MSUIClient.Formats;

namespace MSUIClient.Engine.UI;

/// <summary>Rule-owned state edges for the selected-unit ground ring.</summary>
public static class SelectionRingLaw
{
    public static readonly Vector3 FriendlyBlue = new(0.25f, 0.60f, 1.00f);
    public static readonly Vector3 NeutralYellow = new(1.00f, 1.00f, 0.00f);
    public static readonly Vector3 HostileRed = new(1.00f, 0.00f, 0.00f);

    /// <summary>
    /// Target-ring palette. Friendly units are always blue and neutral units yellow; this is
    /// deliberately separate from overhead-name colours, where friendly NPC names remain green.
    /// Combat flash and dead gray retain their existing first-priority behavior.
    /// </summary>
    public static Vector3 TargetRgb(FactionReaction reaction, bool isDead,
        bool combatFlash, uint uptimeMs)
    {
        if (combatFlash)
            return new Vector3(1f, NameplateUiLaw.CombatFlashGreen(uptimeMs) / 255f, 0f);
        if (isDead) return new Vector3(127f / 255f);
        return reaction switch
        {
            FactionReaction.Hostile => HostileRed,
            FactionReaction.Friendly => FriendlyBlue,
            _ => NeutralYellow,
        };
    }

    /// <summary>
    /// The 1.12 death handler clears a target only on an alive-to-dead transition
    /// of the same GUID. Selecting an already-dead corpse remains valid.
    /// </summary>
    public static bool DiedWhileSelected(
        ulong previousGuid, bool previousDead, ulong currentGuid, bool currentDead) =>
        currentGuid != 0 && previousGuid == currentGuid && !previousDead && currentDead;

    /// <summary>
    /// Decal-frame sine/cosine for a texture square whose faded edge tracks the
    /// camera yaw. The projector maps world positions back into this local frame.
    /// </summary>
    public static (float Sin, float Cos) ProjectorRotation(float cameraYaw) =>
        (-MathF.Sin(cameraYaw), MathF.Cos(cameraYaw));
}
