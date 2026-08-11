using System.Numerics;

namespace MSUIClient.Net;

// ─────────────────────────────────────────────────────────────────────────────
// The NPC dev window's immutable DB-truth snapshot: spawn rows + waypoint paths
// for one map, however they were fetched (JSON snapshot endpoint or CSV export
// fallback — see DevDataClient). PURE DATA + lookup logic only: no I/O, no
// ImGui, no GL. Everything here is immutable after construction, which is what
// makes the background-fetch → volatile-publish → game-thread-read handoff safe.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>One mangos.creature row (the dev-window subset).</summary>
public sealed record DevSpawnRow(
    uint Guid,               // creature.guid PK == low 24 bits of a live unit guid
    uint Entry,              // creature.id (primary entry)
    uint[] EntryPool,        // id..id5 non-zero (random-pick pool)
    int Map,
    Vector3 Position,        // authored spawn point (raw WoW space)
    float Orientation,
    uint SpawnSecsMin,       // spawntimesecsmin
    uint SpawnSecsMax,       // spawntimesecsmax
    float WanderDistance,    // used when MovementType == 1
    uint MovementType,       // 0 idle, 1 random(wander), 2 waypoint
    uint SpawnFlags);

/// <summary>One waypoint node (creature_movement or creature_movement_template row).</summary>
public sealed record DevWaypointRow(
    uint Point,              // 1-based, must stay gapless per path (WaypointManager::Cleanup)
    Vector3 Position,
    float Orientation,
    uint WaitMs,             // waittime: pause at this node, milliseconds
    float WanderDistance,
    uint ScriptId,
    uint PathId);

/// <summary>Where a resolved patrol path came from — mirrors vmangos WaypointPathOrigin.</summary>
public enum DevPathOrigin { None, Guid, Template }

public sealed class DevWorldData
{
    public required int Map { get; init; }

    /// <summary>True when the snapshot covers the whole map (CSV export path); false for
    /// the area-limited JSON snapshot. Drives how "M in DB nearby" counts are phrased.</summary>
    public required bool WholeMap { get; init; }

    public required DateTime FetchedUtc { get; init; }

    /// <summary>"snapshot" (JSON endpoint), "csv" (fresh export), or "csv-cache".</summary>
    public required string Source { get; init; }

    public required IReadOnlyDictionary<uint, DevSpawnRow> SpawnsByGuid { get; init; }

    /// <summary>creature_movement, grouped by id (== creature.guid), point-ordered.</summary>
    public required IReadOnlyDictionary<uint, DevWaypointRow[]> GuidPaths { get; init; }

    /// <summary>creature_movement_template: entry → pathId → point-ordered nodes.</summary>
    public required IReadOnlyDictionary<uint, Dictionary<uint, DevWaypointRow[]>> TemplatePaths { get; init; }

    /// <summary>
    /// vmangos WaypointManager::GetDefaultPath resolution order: the per-GUID table wins,
    /// else the per-entry template (pathId 0 preferred, else the lowest present).
    /// </summary>
    public (DevPathOrigin Origin, uint Key, uint PathId, DevWaypointRow[]? Nodes) ResolvePath(
        uint guid, uint entry)
    {
        if (GuidPaths.TryGetValue(guid, out DevWaypointRow[]? byGuid) && byGuid.Length > 0)
            return (DevPathOrigin.Guid, guid, 0, byGuid);
        if (TemplatePaths.TryGetValue(entry, out Dictionary<uint, DevWaypointRow[]>? byEntry) &&
            byEntry.Count > 0)
        {
            uint pathId = byEntry.ContainsKey(0) ? 0 : byEntry.Keys.Min();
            return (DevPathOrigin.Template, entry, pathId, byEntry[pathId]);
        }
        return (DevPathOrigin.None, 0, 0, null);
    }

    public static readonly DevWorldData Empty = new()
    {
        Map = -1,
        WholeMap = false,
        FetchedUtc = DateTime.MinValue,
        Source = "none",
        SpawnsByGuid = new Dictionary<uint, DevSpawnRow>(),
        GuidPaths = new Dictionary<uint, DevWaypointRow[]>(),
        TemplatePaths = new Dictionary<uint, Dictionary<uint, DevWaypointRow[]>>(),
    };
}
