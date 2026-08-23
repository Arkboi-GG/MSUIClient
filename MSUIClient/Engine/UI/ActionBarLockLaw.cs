namespace MSUIClient.Engine.UI;

/// <summary>
/// The narrow 1.12 LOCK_ACTIONBAR contract. Locking suppresses drag-start and
/// receive-drag on both action bars, but deliberately does not suppress the
/// separate Shift-click pickup path.
/// </summary>
public static class ActionBarLockLaw
{
    public static bool Toggle(bool locked) => !locked;
    public static bool DragGestureAllowed(bool locked) => !locked;
    public static bool ReceiveDragAllowed(bool locked) => !locked;
    public static bool ShiftClickPickupAllowed(bool locked) => true;
}
