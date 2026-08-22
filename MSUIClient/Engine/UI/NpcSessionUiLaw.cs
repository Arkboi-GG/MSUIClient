namespace MSUIClient.Engine.UI;

/// <summary>
/// Current Benilla's shared interaction-session gate. Every window bound to a live NPC (and the
/// mailbox game object) uses the same squared center-to-center range as the world service cursor.
/// A temporarily unavailable player transform is not evidence that an established session ended.
/// </summary>
public static class NpcSessionUiLaw
{
    public const float ServiceRangeSquared = 30.864f;
    public const float ServiceRange = 5.5555377f;

    public static bool InRange(float distanceSquared) =>
        distanceSquared <= ServiceRangeSquared;

    public static bool ShouldClose(
        bool sessionOpen,
        bool playerAvailable,
        bool sourceAvailable,
        float distanceSquared) =>
        sessionOpen && playerAvailable &&
        (!sourceAvailable || !InRange(distanceSquared));
}
