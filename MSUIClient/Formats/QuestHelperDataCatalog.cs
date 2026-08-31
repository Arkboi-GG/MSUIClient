using System.IO.Compression;
using System.Reflection;
using System.Text;

namespace MSUIClient.Formats;

public readonly record struct QuestHelperSpawn(uint AreaId, float XPercent, float YPercent);

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
/// Compact, read-only Vanilla quest-location data. The shipped blob is generated offline from
/// pfQuest's database; no Lua or addon runtime is loaded by the client. Runtime lookups are exact
/// entry-id joins against the authoritative quest template already received from the server.
/// </summary>
public sealed class QuestHelperDataCatalog
{
    private const string ResourceSuffix = "Assets.QuestHelperData.bin.gz";
    private readonly Dictionary<uint, QuestHelperSpawn[]> _units;
    private readonly Dictionary<uint, QuestHelperSpawn[]> _objects;
    private readonly Dictionary<uint, QuestHelperSources> _items;
    private readonly Dictionary<uint, QuestHelperSources> _turnIns;
    private readonly QuestHelperAvailableQuest[] _availableQuests;

    private QuestHelperDataCatalog(Dictionary<uint, QuestHelperSpawn[]> units,
        Dictionary<uint, QuestHelperSpawn[]> objects,
        Dictionary<uint, QuestHelperSources> items,
        Dictionary<uint, QuestHelperSources> turnIns,
        QuestHelperAvailableQuest[] availableQuests)
    {
        _units = units;
        _objects = objects;
        _items = items;
        _turnIns = turnIns;
        _availableQuests = availableQuests;
    }

    public int UnitEntryCount => _units.Count;
    public int ObjectEntryCount => _objects.Count;
    public int ItemSourceCount => _items.Count;
    public int TurnInCount => _turnIns.Count;
    public int AvailableQuestCount => _availableQuests.Length;
    public IReadOnlyList<QuestHelperAvailableQuest> AvailableQuests => _availableQuests;

    public IReadOnlyList<QuestHelperSpawn> UnitSpawns(uint entry) =>
        _units.TryGetValue(entry, out QuestHelperSpawn[]? value) ? value : [];

    public IReadOnlyList<QuestHelperSpawn> ObjectSpawns(uint entry) =>
        _objects.TryGetValue(entry, out QuestHelperSpawn[]? value) ? value : [];

    public QuestHelperSources ItemSources(uint itemId) =>
        _items.GetValueOrDefault(itemId, QuestHelperSources.Empty);

    public QuestHelperSources TurnInSources(uint questId) =>
        _turnIns.GetValueOrDefault(questId, QuestHelperSources.Empty);

    public static QuestHelperDataCatalog LoadEmbedded()
    {
        Assembly assembly = typeof(QuestHelperDataCatalog).Assembly;
        string? name = assembly.GetManifestResourceNames()
            .FirstOrDefault(candidate => candidate.EndsWith(ResourceSuffix,
                StringComparison.Ordinal));
        if (name is null)
            throw new InvalidDataException($"embedded quest-helper data '{ResourceSuffix}' is missing");
        using Stream raw = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidDataException($"embedded quest-helper data '{name}' could not be opened");
        using var gzip = new GZipStream(raw, CompressionMode.Decompress);
        using var reader = new BinaryReader(gzip, Encoding.UTF8, leaveOpen: false);
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "MSQH" || reader.ReadInt32() != 2)
            throw new InvalidDataException("unsupported quest-helper data header");
        return new(ReadSpawns(reader), ReadSpawns(reader),
            ReadSources(reader), ReadSources(reader), ReadAvailableQuests(reader));
    }

    private static Dictionary<uint, QuestHelperSpawn[]> ReadSpawns(BinaryReader reader)
    {
        int count = SafeCount(reader.ReadInt32(), 100_000, "spawn entry");
        var result = new Dictionary<uint, QuestHelperSpawn[]>(count);
        for (int row = 0; row < count; row++)
        {
            uint entry = reader.ReadUInt32();
            int points = SafeCount(reader.ReadInt32(), 1_000_000, "spawn point");
            var values = new QuestHelperSpawn[points];
            for (int point = 0; point < points; point++)
                values[point] = new(reader.ReadUInt32(),
                    reader.ReadUInt16() / 10f, reader.ReadUInt16() / 10f);
            result.Add(entry, values);
        }
        return result;
    }

    private static Dictionary<uint, QuestHelperSources> ReadSources(BinaryReader reader)
    {
        int count = SafeCount(reader.ReadInt32(), 100_000, "source entry");
        var result = new Dictionary<uint, QuestHelperSources>(count);
        for (int row = 0; row < count; row++)
        {
            uint id = reader.ReadUInt32();
            result.Add(id, new(ReadIds(reader), ReadIds(reader)));
        }
        return result;
    }

    private static uint[] ReadIds(BinaryReader reader)
    {
        int count = SafeCount(reader.ReadInt32(), 100_000, "source id");
        var result = new uint[count];
        for (int i = 0; i < count; i++) result[i] = reader.ReadUInt32();
        return result;
    }

    private static QuestHelperAvailableQuest[] ReadAvailableQuests(BinaryReader reader)
    {
        int count = SafeCount(reader.ReadInt32(), 100_000, "available quest");
        var result = new QuestHelperAvailableQuest[count];
        for (int i = 0; i < count; i++)
        {
            uint questId = reader.ReadUInt32();
            byte level = reader.ReadByte();
            byte minimumLevel = reader.ReadByte();
            uint raceMask = reader.ReadUInt32();
            uint classMask = reader.ReadUInt32();
            string title = reader.ReadString();
            uint[] previous = ReadIds(reader);
            result[i] = new(questId, level, minimumLevel, raceMask, classMask,
                title, previous, new(ReadIds(reader), ReadIds(reader)));
        }
        return result;
    }

    private static int SafeCount(int value, int maximum, string label)
    {
        if (value < 0 || value > maximum)
            throw new InvalidDataException($"quest-helper {label} count {value} is invalid");
        return value;
    }
}
