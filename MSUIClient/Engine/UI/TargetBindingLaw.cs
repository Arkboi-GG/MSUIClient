namespace MSUIClient.Engine.UI;

/// <summary>
/// Rule-owned unit-token and raid-marker semantics for the 1.12 targeting bindings.
/// The host supplies the live GUIDs; this law decides whether a command targets the
/// primary unit, its pet, or performs no action for an absent token.
/// </summary>
public static class TargetBindingLaw
{
    /// <summary>
    /// TARGETSELF/TARGETPARTYMEMBERn: first press selects the unit; pressing again
    /// while that unit is selected selects its pet. An absent unit/pet token is a no-op.
    /// </summary>
    public static ulong? ResolveToggle(ulong selected, ulong unit, ulong pet)
    {
        if (unit == 0) return null;
        if (selected != unit) return unit;
        return pet == 0 ? null : pet;
    }

    /// <summary>TARGETPET/TARGETPARTYPETn: an absent token is a no-op.</summary>
    public static ulong? ResolveDirect(ulong unit) => unit == 0 ? null : unit;

    /// <summary>
    /// SetRaidTargetIcon's binding contract. Marker values are the Lua-facing 1..8
    /// range (zero means clear). Asking for the marker already on the target toggles
    /// it off; asking for a different marker assigns it directly.
    /// </summary>
    public static RaidMarkerIntent ResolveRaidMarker(
        IReadOnlyList<ulong> board, ulong target, byte requested)
    {
        if (target == 0 || requested > 8) return default;
        byte current = GroupUiLaw.RaidTargetIndex(board, target);
        if (requested == 0)
            return current == 0 ? default : new(true, checked((byte)(current - 1)), 0);
        if (current == requested)
            return new(true, checked((byte)(requested - 1)), 0);
        return new(true, checked((byte)(requested - 1)), target);
    }
}

public readonly record struct RaidMarkerIntent(bool Send, byte WireIcon, ulong Guid);
