using System.Numerics;
using MSUIClient.World.Encounters;

namespace MSUIClient.Engine.UI;

public static class CommanderEncounterSelectionLaw
{
    public static CommanderEncounterDefinition Select(IReadOnlyList<CommanderEncounterDefinition> definitions,
        CommanderBossFact fact, uint mapId, Vector3 position, int groupSize)
    {
        var authored = definitions.FirstOrDefault(d => d.MapId == mapId && d.ObjectiveEntries.Contains(fact.Entry));
        if (authored is not null) return authored;
        float x = position.X, y = position.Y, z = position.Z;
        CommanderEncounterTeam[] teams = groupSize > 5
            ? [new("Left", [x - 8, y + 18, z]), new("Right", [x - 8, y - 18, z])]
            : [new("Raid", [x - 8, y + 18, z])];
        return new(1, $"basic-{mapId}-{fact.Entry}", fact.Name + " (basic plan)", mapId, fact.Entry,
            new([x - 50, y - 50, z - 30], [x + 50, y + 50, z + 30]), [x + 15, y, z],
            teams, 0, 1, Math.Max(teams.Length, (int)Math.Ceiling(groupSize / 5.0)), [],
            [new(1, "Combat", 0, 0, 100, -1, 0, false, true, true, .8f)], [])
        { ImmuneSchools = fact.ImmuneSchools, Objectives = [fact.Entry], Coverage = "basic" };
    }
}
