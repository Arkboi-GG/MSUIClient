using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: quest-helper-data <pfQuest checkout> <output.bin.gz>");
    return 2;
}

string root = Path.GetFullPath(args[0]);
string output = Path.GetFullPath(args[1]);
string db = Path.Combine(root, "db");
foreach (string required in new[] { "quests.lua", "items.lua", "units.lua", "objects.lua" })
    if (!File.Exists(Path.Combine(db, required)))
        throw new FileNotFoundException($"pfQuest database file is missing: {required}");

var objectiveUnits = new HashSet<uint>();
var objectiveObjects = new HashSet<uint>();
var objectiveItems = new HashSet<uint>();
var turnIns = new Dictionary<uint, Sources>();
var starts = new Dictionary<uint, QuestStart>();
ReadQuests(Path.Combine(db, "quests.lua"), objectiveUnits, objectiveObjects,
    objectiveItems, turnIns, starts);

Dictionary<uint, string> titles = ReadQuestTitles(
    Path.Combine(db, "enUS", "quests.lua"));
foreach ((uint questId, QuestStart start) in starts)
    start.Title = titles.GetValueOrDefault(questId, $"Quest {questId}");

Dictionary<uint, Sources> itemSources = ReadItems(
    Path.Combine(db, "items.lua"), objectiveItems);
var neededUnits = new HashSet<uint>(objectiveUnits);
var neededObjects = new HashSet<uint>(objectiveObjects);
foreach (Sources sources in itemSources.Values.Concat(turnIns.Values))
{
    neededUnits.UnionWith(sources.Units);
    neededObjects.UnionWith(sources.Objects);
}
foreach (QuestStart start in starts.Values)
{
    neededUnits.UnionWith(start.Sources.Units);
    neededObjects.UnionWith(start.Sources.Objects);
}

Dictionary<uint, List<Spawn>> unitSpawns = ReadSpawns(
    Path.Combine(db, "units.lua"), neededUnits);
Dictionary<uint, List<Spawn>> objectSpawns = ReadSpawns(
    Path.Combine(db, "objects.lua"), neededObjects);

Directory.CreateDirectory(Path.GetDirectoryName(output)!);
using (FileStream file = File.Create(output))
using (var gzip = new GZipStream(file, CompressionLevel.SmallestSize))
using (var writer = new BinaryWriter(gzip, Encoding.UTF8, leaveOpen: false))
{
    writer.Write(Encoding.ASCII.GetBytes("MSQH"));
    writer.Write(2);
    WriteSpawns(writer, unitSpawns);
    WriteSpawns(writer, objectSpawns);
    WriteSources(writer, itemSources);
    WriteSources(writer, turnIns);
    WriteQuestStarts(writer, starts);
}

Console.WriteLine($"quest-helper-data: {unitSpawns.Count} unit entries, " +
    $"{objectSpawns.Count} object entries, {itemSources.Count} item mappings, " +
    $"{turnIns.Count} turn-in mappings, {starts.Count} available quest starts -> " +
    $"{output} ({new FileInfo(output).Length:N0} bytes)");
return 0;

static void ReadQuests(string path, HashSet<uint> units, HashSet<uint> objects,
    HashSet<uint> items, Dictionary<uint, Sources> turnIns,
    Dictionary<uint, QuestStart> starts)
{
    uint quest = 0;
    string section = "";
    string expandedType = "";
    Sources? ends = null;
    QuestStart? start = null;
    foreach (string line in File.ReadLines(path))
    {
        Match top = Regex.Match(line, @"^  \[(\d+)\] = \{$");
        if (top.Success)
        {
            quest = uint.Parse(top.Groups[1].Value, CultureInfo.InvariantCulture);
            section = expandedType = "";
            ends = new Sources();
            start = new QuestStart(quest);
            continue;
        }
        if (quest == 0) continue;

        Match scalar = Regex.Match(line,
            @"^    \[""(lvl|min|race|class)""\] = (-?\d+),?$");
        if (scalar.Success && start is not null)
        {
            uint value = uint.Parse(scalar.Groups[2].Value, CultureInfo.InvariantCulture);
            switch (scalar.Groups[1].Value)
            {
                case "lvl": start.Level = checked((byte)Math.Min(value, byte.MaxValue)); break;
                case "min": start.MinimumLevel = checked((byte)Math.Min(value, byte.MaxValue)); break;
                case "race": start.RaceMask = value; break;
                case "class": start.ClassMask = value; break;
            }
            continue;
        }

        Match previous = Regex.Match(line, @"^    \[""pre""\] = \{([^}]*)\},?$");
        if (previous.Success && start is not null)
        {
            start.Previous.UnionWith(Numbers(previous.Groups[1].Value));
            continue;
        }

        Match sectionStart = Regex.Match(line, @"^    \[""(obj|end|start)""\] = \{$");
        if (sectionStart.Success)
        {
            section = sectionStart.Groups[1].Value;
            expandedType = "";
            continue;
        }

        Match inline = Regex.Match(line,
            @"^      \[""([UIO])""\] = \{([^}]*)\},?$");
        if (inline.Success)
        {
            foreach (uint id in Numbers(inline.Groups[2].Value))
                AddQuestRelation(section, inline.Groups[1].Value, id,
                    units, objects, items, ends!, start!);
            continue;
        }

        Match expanded = Regex.Match(line, @"^      \[""([UIO])""\] = \{$");
        if (expanded.Success)
        {
            expandedType = expanded.Groups[1].Value;
            continue;
        }

        if (expandedType.Length > 0)
        {
            Match member = Regex.Match(line, @"^        \[\d+\] = (\d+),?$");
            if (member.Success)
            {
                AddQuestRelation(section, expandedType,
                    uint.Parse(member.Groups[1].Value, CultureInfo.InvariantCulture),
                    units, objects, items, ends!, start!);
                continue;
            }
            if (line == "      },")
            {
                expandedType = "";
                continue;
            }
        }

        if (line == "    },")
        {
            section = "";
            continue;
        }
        if (line == "  },")
        {
            if (ends is { Empty: false }) turnIns[quest] = ends;
            if (start is { Sources.Empty: false }) starts[quest] = start;
            quest = 0;
            section = expandedType = "";
            ends = null;
            start = null;
        }
    }
}

static void AddQuestRelation(string section, string type, uint id,
    HashSet<uint> units, HashSet<uint> objects, HashSet<uint> items, Sources ends,
    QuestStart start)
{
    if (id == 0) return;
    if (section == "obj")
    {
        if (type == "U") units.Add(id);
        else if (type == "O") objects.Add(id);
        else if (type == "I") items.Add(id);
    }
    else if (section == "end")
    {
        if (type == "U") ends.Units.Add(id);
        else if (type == "O") ends.Objects.Add(id);
    }
    else if (section == "start")
    {
        // Item-started quests do not have a stable giver punctuation site. Keep the native
        // helper's available markers to authored unit/object questgivers.
        if (type == "U") start.Sources.Units.Add(id);
        else if (type == "O") start.Sources.Objects.Add(id);
    }
}

static Dictionary<uint, string> ReadQuestTitles(string path)
{
    var result = new Dictionary<uint, string>();
    uint quest = 0;
    foreach (string line in File.ReadLines(path))
    {
        Match top = Regex.Match(line, @"^  \[(\d+)\] = \{$");
        if (top.Success)
        {
            quest = uint.Parse(top.Groups[1].Value, CultureInfo.InvariantCulture);
            continue;
        }
        if (quest == 0) continue;
        Match title = Regex.Match(line, @"^    \[""T""\] = ""(.*)"",?$");
        if (title.Success)
        {
            string value = title.Groups[1].Value
                .Replace(@"\'", "'", StringComparison.Ordinal)
                .Replace("\\\"", "\"", StringComparison.Ordinal)
                .Replace(@"\\", @"\", StringComparison.Ordinal);
            result[quest] = value;
            continue;
        }
        if (line == "  },") quest = 0;
    }
    return result;
}

static Dictionary<uint, Sources> ReadItems(string path, HashSet<uint> wanted)
{
    var result = new Dictionary<uint, Sources>();
    uint item = 0;
    string type = "";
    Sources? sources = null;
    foreach (string line in File.ReadLines(path))
    {
        Match top = Regex.Match(line, @"^  \[(\d+)\] = \{$");
        if (top.Success)
        {
            item = uint.Parse(top.Groups[1].Value, CultureInfo.InvariantCulture);
            sources = wanted.Contains(item) ? new Sources() : null;
            type = "";
            continue;
        }
        if (item == 0) continue;
        Match typeStart = Regex.Match(line, @"^    \[""([UOV])""\] = \{$");
        if (typeStart.Success)
        {
            type = typeStart.Groups[1].Value;
            continue;
        }
        if (type.Length > 0 && sources is not null)
        {
            Match source = Regex.Match(line, @"^      \[(\d+)\] = ");
            if (source.Success)
            {
                uint id = uint.Parse(source.Groups[1].Value, CultureInfo.InvariantCulture);
                // Vendors are units too; retaining them lets collection objectives point at
                // legitimately purchasable quest items instead of pretending every item drops.
                if (type is "U" or "V") sources.Units.Add(id); else sources.Objects.Add(id);
                continue;
            }
        }
        if (line == "    },") { type = ""; continue; }
        if (line == "  },")
        {
            if (sources is { Empty: false }) result[item] = sources;
            item = 0;
            type = "";
            sources = null;
        }
    }
    return result;
}

static Dictionary<uint, List<Spawn>> ReadSpawns(string path, HashSet<uint> wanted)
{
    var result = new Dictionary<uint, List<Spawn>>();
    uint entry = 0;
    bool coords = false;
    List<Spawn>? points = null;
    foreach (string line in File.ReadLines(path))
    {
        Match top = Regex.Match(line, @"^  \[(\d+)\] = \{$");
        if (top.Success)
        {
            entry = uint.Parse(top.Groups[1].Value, CultureInfo.InvariantCulture);
            points = wanted.Contains(entry) ? [] : null;
            coords = false;
            continue;
        }
        if (entry == 0) continue;
        if (line == "    [\"coords\"] = {") { coords = true; continue; }
        if (coords && points is not null)
        {
            Match coordinate = Regex.Match(line,
                @"^      \[\d+\] = \{ (-?\d+(?:\.\d+)?), (-?\d+(?:\.\d+)?), (\d+),");
            if (coordinate.Success)
            {
                float x = float.Parse(coordinate.Groups[1].Value, CultureInfo.InvariantCulture);
                float y = float.Parse(coordinate.Groups[2].Value, CultureInfo.InvariantCulture);
                uint area = uint.Parse(coordinate.Groups[3].Value, CultureInfo.InvariantCulture);
                if (area != 0 && x is >= 0 and <= 100 && y is >= 0 and <= 100)
                    points.Add(new(area, Quantize(x), Quantize(y)));
                continue;
            }
        }
        if (coords && line == "    },") { coords = false; continue; }
        if (line == "  },")
        {
            if (points is { Count: > 0 })
                result[entry] = points.Distinct().OrderBy(p => p.AreaId)
                    .ThenBy(p => p.X10).ThenBy(p => p.Y10).ToList();
            entry = 0;
            coords = false;
            points = null;
        }
    }
    return result;
}

static ushort Quantize(float value) =>
    checked((ushort)Math.Clamp((int)MathF.Round(value * 10f), 0, 1000));

static IEnumerable<uint> Numbers(string text) =>
    Regex.Matches(text, @"\d+").Select(match =>
        uint.Parse(match.Value, CultureInfo.InvariantCulture));

static void WriteSpawns(BinaryWriter writer, Dictionary<uint, List<Spawn>> values)
{
    writer.Write(values.Count);
    foreach ((uint entry, List<Spawn> points) in values.OrderBy(pair => pair.Key))
    {
        writer.Write(entry);
        writer.Write(points.Count);
        foreach (Spawn point in points)
        {
            writer.Write(point.AreaId);
            writer.Write(point.X10);
            writer.Write(point.Y10);
        }
    }
}

static void WriteSources(BinaryWriter writer, Dictionary<uint, Sources> values)
{
    writer.Write(values.Count);
    foreach ((uint id, Sources sources) in values.OrderBy(pair => pair.Key))
    {
        writer.Write(id);
        WriteIds(writer, sources.Units);
        WriteIds(writer, sources.Objects);
    }
}

static void WriteQuestStarts(BinaryWriter writer, Dictionary<uint, QuestStart> values)
{
    writer.Write(values.Count);
    foreach ((uint questId, QuestStart start) in values.OrderBy(pair => pair.Key))
    {
        writer.Write(questId);
        writer.Write(start.Level);
        writer.Write(start.MinimumLevel);
        writer.Write(start.RaceMask);
        writer.Write(start.ClassMask);
        writer.Write(start.Title);
        WriteIds(writer, start.Previous);
        WriteIds(writer, start.Sources.Units);
        WriteIds(writer, start.Sources.Objects);
    }
}

static void WriteIds(BinaryWriter writer, HashSet<uint> ids)
{
    writer.Write(ids.Count);
    foreach (uint id in ids.Order()) writer.Write(id);
}

internal readonly record struct Spawn(uint AreaId, ushort X10, ushort Y10);

internal sealed class Sources
{
    public HashSet<uint> Units { get; } = [];
    public HashSet<uint> Objects { get; } = [];
    public bool Empty => Units.Count == 0 && Objects.Count == 0;
}

internal sealed class QuestStart(uint questId)
{
    public uint QuestId { get; } = questId;
    public byte Level { get; set; }
    public byte MinimumLevel { get; set; }
    public uint RaceMask { get; set; }
    public uint ClassMask { get; set; }
    public string Title { get; set; } = $"Quest {questId}";
    public HashSet<uint> Previous { get; } = [];
    public Sources Sources { get; } = new();
}
