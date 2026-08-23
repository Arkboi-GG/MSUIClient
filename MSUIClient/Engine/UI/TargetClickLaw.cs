namespace MSUIClient.Engine.UI;

/// <summary>
/// Player-facing selection semantics for an ordinary world left-click. The 1.12
/// "Sticky Targeting" checkbox is the inverse of deselectOnClick: it changes only
/// the empty-world leg and never blocks a real picked unit from becoming selected.
/// </summary>
public static class TargetClickLaw
{
    public static ulong LeftClickSelection(
        ulong currentSelection, ulong pickedGuid, bool stickyTargeting) =>
        pickedGuid == 0 && stickyTargeting ? currentSelection : pickedGuid;
}
