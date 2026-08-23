namespace MSUIClient.Engine.UI;

/// <summary>Rule-owned state edges for the selected-unit ground ring.</summary>
public static class SelectionRingLaw
{
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
