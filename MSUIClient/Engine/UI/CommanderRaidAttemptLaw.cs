using System.Numerics;

namespace MSUIClient.Engine.UI;

public enum CommanderRaidAttemptStage { None, Recovery, Preparation, Ready, PullPending, Fighting, Finished, Failed }

/// <summary>QA provenance only. Ordinary gameplay and server encounter decisions do not depend on it.</summary>
public static class CommanderRaidAttemptLaw
{
    // The opt-in human tank driver must acquire ordinary contact before wall backing.
    public static Vector3 TankMovementGoal(Vector3 position, Vector3 boss, Vector3 station, float reach, bool tanking, Vector3 roomCenter)
    {
        float contact = MathF.Max(1, reach - .75f);
        float dx = boss.X - position.X, dy = boss.Y - position.Y;
        float separation = MathF.Sqrt(dx * dx + dy * dy);
        Vector2 stationDelta = new(station.X - position.X, station.Y - position.Y);
        Vector2 stationOutward = new(station.X - roomCenter.X, station.Y - roomCenter.Y);
        // Align with the actual enemy-to-station direction after lateral
        // displacement. If the enemy has passed the anchor, retain the room's
        // outward axis so the tank never crosses back through it.
        Vector2 bossToStation = new(station.X - boss.X, station.Y - boss.Y);
        if (Vector2.Dot(bossToStation, stationOutward) > 0) stationOutward = bossToStation;
        // Once facing outward with the enemy's attention, station acquisition
        // may cross our attack radius. Otherwise contact recovery cancels every
        // retreat before the enemy reaches its own pursuit threshold.
        Vector2 radialOffset = new(-dx, -dy);
        if (tanking && separation >= 1.5f && stationOutward.LengthSquared() > .01f &&
            Vector2.Dot(radialOffset / separation, Vector2.Normalize(stationOutward)) >= .98480775f)
        {
            float remaining = stationDelta.Length();
            if (remaining <= 2) return position;
            if (Vector2.Dot(stationDelta, radialOffset) > 0)
            {
                Vector2 step = stationDelta * (MathF.Min(2, remaining - 2) / remaining);
                return new(position.X + step.X, position.Y + step.Y, position.Z);
            }
        }
        // Match the driver's .2-yard arrival tolerance. A smaller remaining
        // contact gap must not suppress station acquisition forever.
        if (separation > contact + .2f)
            return new(boss.X - dx / separation * contact, boss.Y - dy / separation * contact, position.Z);
        if (!tanking) return position;
        // Establish outward facing before backing toward the authored station.
        // A station behind the current tank must never pull it through the boss.
        Vector2 outward = stationOutward;
        if (outward.LengthSquared() < .01f) return position;
        outward = Vector2.Normalize(outward);
        Vector2 offset = new(position.X - boss.X, position.Y - boss.Y);
        float distance = offset.Length();
        if (distance >= 1.5f && Vector2.Dot(offset / distance, outward) >= .98480775f) return position;
        // Acquiring the opposite side is a crossing move. A long orbit lets a
        // chasing target drag the opening away from the support formation.
        if (distance > .01f && Vector2.Dot(offset / distance, outward) < -.5f)
            return new(boss.X + outward.X * 2, boss.Y + outward.Y * 2, position.Z);
        // Correct facing tangentially at the current separation. An inward
        // shortcut can let a chasing boss cross the tank and turn on the raid.
        Vector2 radial = distance > .01f ? offset / distance : outward;
        float angle = Math.Clamp(MathF.Atan2(radial.X * outward.Y - radial.Y * outward.X,
            Vector2.Dot(radial, outward)), -.34906585f, .34906585f);
        float c = MathF.Cos(angle), sin = MathF.Sin(angle);
        float radius = MathF.Min(contact, MathF.Max(MathF.Max(1.5f, distance), distance / c));
        return new(boss.X + (radial.X * c - radial.Y * sin) * radius,
            boss.Y + (radial.X * sin + radial.Y * c) * radius, position.Z);
    }

    public static bool RangedPullRouteClear(Vector3 position, Vector3 goal, Vector3 outsider, float margin)
    {
        Vector2 start = new(position.X, position.Y), end = new(goal.X, goal.Y), point = new(outsider.X, outsider.Y);
        Vector2 segment = end - start;
        float t = segment.LengthSquared() > .001f ? Math.Clamp(Vector2.Dot(point - start, segment) / segment.LengthSquared(), 0, 1) : 0;
        return Vector2.DistanceSquared(point, start + segment * t) >= margin * margin;
    }

    public static Vector3 RangedPullReturnGoal(Vector3 position, Vector3 target, Vector3 station)
    {
        Vector2 toStation = new(station.X - position.X, station.Y - position.Y);
        Vector2 toTarget = new(target.X - position.X, target.Y - position.Y);
        float remaining = toStation.Length();
        // Return only away from the pulled enemy; do not cross it to reach an anchor.
        if (remaining <= 2 || Vector2.Dot(toStation, toTarget) >= 0) return position;
        Vector2 step = toStation * (MathF.Min(2, remaining - 2) / remaining);
        return new(position.X + step.X, position.Y + step.Y, position.Z);
    }

    public static Vector3 RangedPullGoal(Vector3 position, Vector3 target, float range)
    {
        Vector2 delta = new(target.X - position.X, target.Y - position.Y);
        float distance = delta.Length();
        float contact = MathF.Max(1, range - 2);
        if (distance <= contact + .2f) return position;
        Vector2 step = delta * ((distance - contact) / distance);
        return new(position.X + step.X, position.Y + step.Y, position.Z);
    }

    public static bool ConsumablePreparationAllowed(CommanderRaidAttemptStage stage, ulong owner, bool combat) =>
        stage == CommanderRaidAttemptStage.Recovery && !combat && (owner == 787 || owner is >= 115 and <= 142 or >= 150 and <= 160);

    public static bool ConsumedExactlyOne(uint before, uint after, bool auraObserved) =>
        before > after && before - after == 1 && auraObserved;

    public static bool AuthorizedRoster(IEnumerable<ulong> guids)
    {
        var actual = guids.ToArray();
        return actual.Length == 40 && actual.Distinct().Count() == 40 && actual.Contains(787UL) &&
            actual.All(g => g == 787 || g is >= 115 and <= 142 or >= 150 and <= 160);
    }

    public static bool ReadOnlyCommand(string command) => command.Trim().ToLowerInvariant() is
        ".list threat" or ".gm" or ".gps";

    public static string? CommandRefusal(CommanderRaidAttemptStage stage, string command, bool combat)
    {
        if (stage == CommanderRaidAttemptStage.None || ReadOnlyCommand(command)) return null;
        if (stage is CommanderRaidAttemptStage.PullPending or CommanderRaidAttemptStage.Fighting)
            return "Administrative commands invalidate ordinary combat; enter recovery to end this attempt first.";
        if (stage == CommanderRaidAttemptStage.Failed)
            return "Enter explicit recovery before changing a failed attempt.";
        if (stage is CommanderRaidAttemptStage.Preparation or CommanderRaidAttemptStage.Ready && combat)
            return "Combat started during preparation. Stop this attempt before any further setup command.";
        if (stage == CommanderRaidAttemptStage.Ready && !command.Trim().Equals(".gm off", StringComparison.OrdinalIgnoreCase))
            return "Preparation is sealed. Restart preparation before changing the raid.";
        return null;
    }

    public static string? StartRefusal(CommanderRaidAttemptStage stage, IEnumerable<ulong> roster,
        bool freshAndFull, bool armed, bool gmPreparation, bool bossAliveAndFull, bool combat)
    {
        if (stage != CommanderRaidAttemptStage.Ready) return "A sealed preparation record is required.";
        if (!AuthorizedRoster(roster)) return "The exact authorized forty-character roster is required.";
        if (!freshAndFull) return "All forty characters need fresh facts, full health and no ghost state.";
        if (!armed || !gmPreparation) return "An acknowledged armed plan and confirmed GM preparation state are required.";
        if (!bossAliveAndFull || combat) return "The boss must be alive, full health and out of combat before the pull.";
        return null;
    }
}
