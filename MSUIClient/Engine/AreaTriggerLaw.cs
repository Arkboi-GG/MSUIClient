namespace MSUIClient.Engine;

public readonly record struct AreaTriggerLatchStep(int LatchedId, int? ReportId);

/// <summary>The reference's single exit-latch transition for AreaTrigger volumes.</summary>
public static class AreaTriggerLaw
{
    public static AreaTriggerLatchStep Step(
        int latchedId, bool stillInsideLatched, int? firstContainingId)
    {
        if (latchedId != 0 && stillInsideLatched)
            return new(latchedId, null);
        return firstContainingId is int entered
            ? new(entered, entered)
            : new(0, null);
    }
}
