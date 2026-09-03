namespace MSUIClient.Formats;

/// <summary>One live realm spawn in native WoW world coordinates.</summary>
public readonly record struct QuestHelperSpawn(uint MapId, float X, float Y);

public sealed record QuestHelperSources(uint[] Units, uint[] Objects)
{
    public static readonly QuestHelperSources Empty = new([], []);
}

public sealed record QuestHelperAvailableQuest(
    uint QuestId,
    byte Level,
    byte MinimumLevel,
    uint RaceMask,
    uint ClassMask,
    string Title,
    uint[] PreviousQuests,
    QuestHelperSources Sources);

/// <summary>
/// Immutable Quest Helper snapshot assembled from the realm's live database exports. Quest state
/// and quest text still come from the game protocol; this catalog supplies current relations and
/// world positions that the stock query opcodes do not carry.
/// </summary>
public sealed class QuestHelperDataCatalog
{
    private readonly IReadOnlyDictionary<uint, QuestHelperSpawn[]> _units;
    private readonly IReadOnlyDictionary<uint, QuestHelperSpawn[]> _objects;
    private readonly IReadOnlyDictionary<uint, QuestHelperSources> _items;
    private readonly IReadOnlyDictionary<uint, QuestHelperSources> _turnIns;
    private readonly IReadOnlyDictionary<uint, uint> _unitFactions;

    internal QuestHelperDataCatalog(
        IReadOnlyDictionary<uint, QuestHelperSpawn[]> units,
        IReadOnlyDictionary<uint, QuestHelperSpawn[]> objects,
        IReadOnlyDictionary<uint, QuestHelperSources> items,
        IReadOnlyDictionary<uint, QuestHelperSources> turnIns,
        QuestHelperAvailableQuest[] availableQuests,
        DateTime fetchedUtc,
        IReadOnlyDictionary<uint, uint>? unitFactions = null)
    {
        _unitFactions = unitFactions ?? new Dictionary<uint, uint>();
        _units = units;
        _objects = objects;
        _items = items;
        _turnIns = turnIns;
        AvailableQuests = availableQuests;
        FetchedUtc = fetchedUtc;
    }

    public DateTime FetchedUtc { get; }
    public int UnitEntryCount => _units.Count;
    public int ObjectEntryCount => _objects.Count;
    public int ItemSourceCount => _items.Count;
    public int TurnInCount => _turnIns.Count;
    public int AvailableQuestCount => AvailableQuests.Count;
    public IReadOnlyList<QuestHelperAvailableQuest> AvailableQuests { get; }

    public IReadOnlyList<QuestHelperSpawn> UnitSpawns(uint entry) =>
        _units.TryGetValue(entry, out QuestHelperSpawn[]? value) ? value : [];

    public IReadOnlyList<QuestHelperSpawn> ObjectSpawns(uint entry) =>
        _objects.TryGetValue(entry, out QuestHelperSpawn[]? value) ? value : [];

    public QuestHelperSources ItemSources(uint itemId) =>
        _items.GetValueOrDefault(itemId, QuestHelperSources.Empty);

    public QuestHelperSources TurnInSources(uint questId) =>
        _turnIns.GetValueOrDefault(questId, QuestHelperSources.Empty);

    /// <summary>creature_template.faction (a FactionTemplate id) for a giver entry, 0 unknown.</summary>
    public uint UnitFaction(uint entry) => _unitFactions.GetValueOrDefault(entry, 0u);
}