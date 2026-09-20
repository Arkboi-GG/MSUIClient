using MSUIClient.Net;
using MSUIClient.World.Encounters;

namespace MSUIClient.Engine.UI;

/// <summary>Fresh, bounded server advice is shared by the normal planner and opt-in QA driver.</summary>
public static class CommanderRaidGuidanceLaw
{
    public const double MaxAgeSeconds = 2.5;
    public static bool Fresh(double ageSeconds) => double.IsFinite(ageSeconds) && ageSeconds >= 0 && ageSeconds <= MaxAgeSeconds;

    public static CommanderRaidGuidance? Current(CommanderRaidStatus? status, ulong actor,
        CommanderEncounterDefinition definition, double ageSeconds)
    {
        if (status is not { State: 2 } || !Fresh(ageSeconds) || definition.Tuning.TryGetValue("adviceAgeSeconds", out var maximumAge) && ageSeconds > maximumAge) return null;
        var row = status.Actors.FirstOrDefault(a => a.Guid == actor);
        if (row is not { Known: true, Alive: true } || row.Guidance.State is < 1 or > 4) return null;
        var guidance = row.Guidance;
        // Reserved index: server-authoritative ordinary positioning hold. It
        // cannot authorize movement or masquerade as a timed encounter rule.
        bool ordinaryHold = guidance.RuleIndex == ushort.MaxValue && guidance.State == 1 && guidance.ImpactMs == 0;
        bool observedHazard = guidance.RuleIndex >= 64 && guidance.RuleIndex - 64 < definition.Mechanics.Hazards.Length;
        if ((!ordinaryHold && !observedHazard && (guidance.RuleIndex >= definition.Rules.Length || definition.Rules.OrderByDescending(r => r.Priority).ElementAt(guidance.RuleIndex).Action is not ("avoidPoints" or "avoidCones" or "spread" or "isolate"))) ||
            !float.IsFinite(guidance.Waypoint.X) || !float.IsFinite(guidance.Waypoint.Y) || !float.IsFinite(guidance.Waypoint.Z) ||
            (!ordinaryHold && !CommanderEncounterLaw.Inside(definition, guidance.Waypoint))) return null;
        // A knockback may leave the actor outside authored movement bounds.
        // An ordinary hold has no movement destination: the driver keeps its
        // current pose and may still use normally validated combat actions.
        return guidance;
    }

    public static string Text(byte state) => state switch
    {
        1 => "Safe: hold position", 2 => "Move to safety", 3 => "No safe route", 4 => "Move now: impact near", _ => "",
    };
}
