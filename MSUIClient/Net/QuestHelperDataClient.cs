using System.Globalization;
using System.Text;
using MSUIClient.Formats;

namespace MSUIClient.Net;

/// <summary>
/// Background, read-only feed for native Quest Helper data. Every refresh is rebuilt from the
/// realm's current MangosSuperUI database exports; nothing is embedded or copied from an addon.
/// A successful snapshot remains usable while the next refresh is in flight.
/// </summary>
public sealed class QuestHelperDataClient
{
    public const int ContentPatch = 10; // VMaNGOS 1.12 content patch.
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);

    private static readonly string[] RequiredTables =
    [
        "quest_template",
        "creature_questrelation",
        "creature_involvedrelation",
        "gameobject_questrelation",
        "gameobject_involvedrelation",
        "creature",
        "gameobject",
        "creature_template",
        "gameobject_template",
        "creature_loot_template",
        "gameobject_loot_template",
        "npc_vendor",
    ];

    private readonly HttpClient _http = WebAppHttp.Create(TimeSpan.FromMinutes(2));
    private volatile QuestHelperDataCatalog? _data;
    private Task? _fetching;
    private DateTime _nextFetchUtc;

    public QuestHelperDataCatalog? Data => _data;
    public bool Fetching => _fetching is { IsCompleted: false };
    public bool RefreshDue => DateTime.UtcNow >= _nextFetchUtc;
    public string? Error { get; private set; }

    public void BeginFetch(string baseUrl)
    {
        if (Fetching || !RefreshDue || string.IsNullOrWhiteSpace(baseUrl)) return;
        _nextFetchUtc = DateTime.UtcNow + RetryInterval;
        _fetching = Task.Run(() => Fetch(baseUrl));
    }

    private async Task Fetch(string baseUrl)
    {
        try
        {
            string root = baseUrl.TrimEnd('/');
            var raw = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string table in RequiredTables)
            {
                string url = $"{root}/Database/Export/mangos/{table}";
                raw[table] = await _http.GetStringAsync(url).ConfigureAwait(false);
            }

            // Reference loot is useful when present, but older schemas may omit it.
            try
            {
                raw["reference_loot_template"] = await _http.GetStringAsync(
                    $"{root}/Database/Export/mangos/reference_loot_template").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[quest-helper] reference loot unavailable: {ex.Message}");
            }

            QuestHelperDataCatalog snapshot = ParseExports(raw, DateTime.UtcNow);
            _data = snapshot;
            Error = null;
            _nextFetchUtc = DateTime.UtcNow + RefreshInterval;
            Console.WriteLine("[quest-helper] live realm data loaded: " +
                $"{snapshot.UnitEntryCount} units, {snapshot.ObjectEntryCount} objects, " +
                $"{snapshot.ItemSourceCount} item sources, {snapshot.TurnInCount} turn-ins, " +
                $"{snapshot.AvailableQuestCount} available quests");
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            _nextFetchUtc = DateTime.UtcNow + RetryInterval;
            Console.WriteLine($"[quest-helper] live data refresh failed: {ex.Message}");
        }
    }

    /// <summary>Build an immutable snapshot from live CSV exports. Public for clinical checks.</summary>
    public static QuestHelperDataCatalog ParseExports(
        IReadOnlyDictionary<string, string> exports, DateTime? fetchedUtc = null)
    {
        CsvTable Table(string name) => new(exports.TryGetValue(name, out string? csv) ? csv : "");

        Dictionary<uint, CsvRow> quests = LatestRows(Table("quest_template"), "entry", "patch");
        Dictionary<uint, CsvRow> creatureTemplates =
            LatestRows(Table("creature_template"), "entry", "patch");
        Dictionary<uint, CsvRow> objectTemplates =
            LatestRows(Table("gameobject_template"), "entry", "patch");

        var unitSpawns = ParseCreatureSpawns(Table("creature"));
        var objectSpawns = ParseObjectSpawns(Table("gameobject"));

        var starts = new Dictionary<uint, MutableSources>();
        AddRelations(Table("creature_questrelation"), starts, objects: false);
        AddRelations(Table("gameobject_questrelation"), starts, objects: true);
        var turnIns = new Dictionary<uint, MutableSources>();
        AddRelations(Table("creature_involvedrelation"), turnIns, objects: false);
        AddRelations(Table("gameobject_involvedrelation"), turnIns, objects: true);

        var wantedItems = new HashSet<uint>();
        foreach (CsvRow quest in quests.Values)
        for (int index = 1; index <= 4; index++)
        {
            uint item = quest.U32($"ReqItemId{index}");
            if (item != 0) wantedItems.Add(item);
            uint source = quest.U32($"ReqSourceId{index}");
            if (source != 0) wantedItems.Add(source);
        }

        Dictionary<uint, HashSet<uint>> referenceItems =
            ParseLootRows(Table("reference_loot_template"), null);
        Dictionary<uint, HashSet<uint>> creatureLoot =
            ParseLootRows(Table("creature_loot_template"), referenceItems);
        Dictionary<uint, HashSet<uint>> objectLoot =
            ParseLootRows(Table("gameobject_loot_template"), referenceItems);

        var itemSources = new Dictionary<uint, MutableSources>();
        foreach ((uint entry, CsvRow template) in creatureTemplates)
        {
            uint lootId = template.U32("loot_id");
            if (lootId == 0 || !creatureLoot.TryGetValue(lootId, out HashSet<uint>? items)) continue;
            foreach (uint item in items)
                if (wantedItems.Contains(item)) Sources(itemSources, item).Units.Add(entry);
        }
        foreach ((uint entry, CsvRow template) in objectTemplates)
        {
            uint lootId = template.U32("data1");
            if (lootId == 0 || !objectLoot.TryGetValue(lootId, out HashSet<uint>? items)) continue;
            foreach (uint item in items)
                if (wantedItems.Contains(item)) Sources(itemSources, item).Objects.Add(entry);
        }
        foreach (CsvRow vendor in Table("npc_vendor").Rows)
        {
            uint entry = vendor.U32("entry"), item = vendor.U32("item");
            if (entry != 0 && wantedItems.Contains(item)) Sources(itemSources, item).Units.Add(entry);
        }

        var available = new List<QuestHelperAvailableQuest>();
        foreach ((uint questId, CsvRow quest) in quests.OrderBy(pair => pair.Key))
        {
            if (!starts.TryGetValue(questId, out MutableSources? sources) || sources.Empty) continue;
            int previous = quest.I32("PrevQuestId");
            uint[] previousQuests = previous == 0 ? [] : [(uint)Math.Abs((long)previous)];
            available.Add(new(
                questId,
                Byte(quest.I32("QuestLevel")),
                Byte(quest.I32("MinLevel")),
                quest.U32("RequiredRaces"),
                quest.U32("RequiredClasses"),
                quest.Text("Title") is { Length: > 0 } title ? title : $"Quest {questId}",
                previousQuests,
                sources.Freeze()));
        }

        // Giver factions: the helper hides quests whose every giver is hostile to the driven
        // body (owner 2026-09-03: "the quest helper shows you quests for both factions" —
        // RequiredRaces is 0 on most quests, the giver's faction is what separates them).
        var unitFactions = new Dictionary<uint, uint>();
        foreach ((uint entry, CsvRow template) in creatureTemplates)
        {
            uint faction = template.U32("faction");
            if (faction != 0) unitFactions[entry] = faction;
        }
        return new QuestHelperDataCatalog(
            FreezeSpawns(unitSpawns),
            FreezeSpawns(objectSpawns),
            itemSources.ToDictionary(pair => pair.Key, pair => pair.Value.Freeze()),
            turnIns.ToDictionary(pair => pair.Key, pair => pair.Value.Freeze()),
            [.. available], fetchedUtc ?? DateTime.UtcNow, unitFactions);
    }

    private static byte Byte(int value) => (byte)Math.Clamp(value, 0, byte.MaxValue);

    private static Dictionary<uint, CsvRow> LatestRows(CsvTable table, string key, string patch)
    {
        var result = new Dictionary<uint, CsvRow>();
        var best = new Dictionary<uint, int>();
        foreach (CsvRow row in table.Rows)
        {
            uint id = row.U32(key);
            int rowPatch = row.I32(patch);
            if (id == 0 || rowPatch > ContentPatch ||
                best.TryGetValue(id, out int seen) && rowPatch < seen) continue;
            best[id] = rowPatch;
            result[id] = row;
        }
        return result;
    }

    private static bool Active(CsvRow row)
    {
        int minimum = row.I32("patch_min");
        int maximum = row.I32("patch_max", ContentPatch);
        return minimum <= ContentPatch && (maximum == 0 || maximum >= ContentPatch);
    }

    private static Dictionary<uint, HashSet<QuestHelperSpawn>> ParseCreatureSpawns(CsvTable table)
    {
        var result = new Dictionary<uint, HashSet<QuestHelperSpawn>>();
        foreach (CsvRow row in table.Rows)
        {
            if (!Active(row)) continue;
            QuestHelperSpawn spawn = Spawn(row);
            if (!Valid(spawn)) continue;
            for (int index = 1; index <= 5; index++)
            {
                uint entry = row.U32(index == 1 ? "id" : $"id{index}");
                if (entry != 0) Spawns(result, entry).Add(spawn);
            }
        }
        return result;
    }

    private static Dictionary<uint, HashSet<QuestHelperSpawn>> ParseObjectSpawns(CsvTable table)
    {
        var result = new Dictionary<uint, HashSet<QuestHelperSpawn>>();
        foreach (CsvRow row in table.Rows)
        {
            if (!Active(row)) continue;
            uint entry = row.U32("id");
            QuestHelperSpawn spawn = Spawn(row);
            if (entry != 0 && Valid(spawn)) Spawns(result, entry).Add(spawn);
        }
        return result;
    }

    private static QuestHelperSpawn Spawn(CsvRow row) => new(
        row.U32("map"), row.F32("position_x"), row.F32("position_y"));

    private static bool Valid(QuestHelperSpawn spawn) =>
        float.IsFinite(spawn.X) && float.IsFinite(spawn.Y);

    private static HashSet<QuestHelperSpawn> Spawns(
        Dictionary<uint, HashSet<QuestHelperSpawn>> values, uint entry)
    {
        if (!values.TryGetValue(entry, out HashSet<QuestHelperSpawn>? result))
            values[entry] = result = [];
        return result;
    }

    private static Dictionary<uint, QuestHelperSpawn[]> FreezeSpawns(
        Dictionary<uint, HashSet<QuestHelperSpawn>> values) => values.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.OrderBy(spawn => spawn.MapId).ThenBy(spawn => spawn.X)
                .ThenBy(spawn => spawn.Y).ToArray());

    private static void AddRelations(
        CsvTable table, Dictionary<uint, MutableSources> values, bool objects)
    {
        foreach (CsvRow row in table.Rows)
        {
            if (!Active(row)) continue;
            uint quest = row.U32("quest"), entry = row.U32("id");
            if (quest == 0 || entry == 0) continue;
            MutableSources sources = Sources(values, quest);
            if (objects) sources.Objects.Add(entry); else sources.Units.Add(entry);
        }
    }

    private static MutableSources Sources(
        Dictionary<uint, MutableSources> values, uint key)
    {
        if (!values.TryGetValue(key, out MutableSources? result))
            values[key] = result = new MutableSources();
        return result;
    }

    private static Dictionary<uint, HashSet<uint>> ParseLootRows(
        CsvTable table, Dictionary<uint, HashSet<uint>>? references)
    {
        var result = new Dictionary<uint, HashSet<uint>>();
        foreach (CsvRow row in table.Rows)
        {
            if (!Active(row)) continue;
            uint entry = row.U32("entry");
            if (entry == 0) continue;
            if (!result.TryGetValue(entry, out HashSet<uint>? items))
                result[entry] = items = [];
            int reference = row.I32("mincountOrRef");
            if (reference < 0 && references is not null &&
                references.TryGetValue((uint)Math.Abs((long)reference), out HashSet<uint>? inherited))
                items.UnionWith(inherited);
            else
            {
                uint item = row.U32("item");
                if (item != 0) items.Add(item);
            }
        }
        return result;
    }

    private sealed class MutableSources
    {
        public HashSet<uint> Units { get; } = [];
        public HashSet<uint> Objects { get; } = [];
        public bool Empty => Units.Count == 0 && Objects.Count == 0;
        public QuestHelperSources Freeze() => new([.. Units.Order()], [.. Objects.Order()]);
    }

    private sealed class CsvTable
    {
        private readonly Dictionary<string, int> _header;
        public List<CsvRow> Rows { get; } = [];

        public CsvTable(string text)
        {
            List<List<string>> rows = ParseCsv(text);
            _header = rows.Count == 0
                ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                : rows[0].Select((name, index) => (name, index)).ToDictionary(
                    pair => pair.name.TrimStart('\uFEFF').Trim(), pair => pair.index,
                    StringComparer.OrdinalIgnoreCase);
            for (int index = 1; index < rows.Count; index++)
                Rows.Add(new CsvRow(_header, rows[index]));
        }

        private static List<List<string>> ParseCsv(string text)
        {
            var rows = new List<List<string>>();
            var row = new List<string>();
            var field = new StringBuilder();
            bool quoted = false;

            void EndField() { row.Add(field.ToString()); field.Clear(); }
            void EndRow()
            {
                EndField();
                if (row.Count > 1 || row[0].Length > 0) rows.Add(row);
                row = [];
            }

            for (int index = 0; index < text.Length; index++)
            {
                char value = text[index];
                if (quoted)
                {
                    if (value != '"') { field.Append(value); continue; }
                    if (index + 1 < text.Length && text[index + 1] == '"')
                    { field.Append('"'); index++; }
                    else quoted = false;
                }
                else if (value == '"') quoted = true;
                else if (value == ',') EndField();
                else if (value == '\r') { }
                else if (value == '\n') EndRow();
                else field.Append(value);
            }
            if (field.Length > 0 || row.Count > 0) EndRow();
            return rows;
        }
    }

    private readonly struct CsvRow(Dictionary<string, int> header, List<string> cells)
    {
        private string Raw(string column)
        {
            if (!header.TryGetValue(column, out int at) || at >= cells.Count) return "";
            string value = cells[at];
            return value.Length > 0 && value[0] == '\'' ? value[1..] : value;
        }

        public string Text(string column) => Raw(column);
        public uint U32(string column, uint fallback = 0) =>
            uint.TryParse(Raw(column), NumberStyles.Integer, CultureInfo.InvariantCulture,
                out uint value) ? value : fallback;
        public int I32(string column, int fallback = 0) =>
            int.TryParse(Raw(column), NumberStyles.Integer, CultureInfo.InvariantCulture,
                out int value) ? value : fallback;
        public float F32(string column, float fallback = 0) =>
            float.TryParse(Raw(column), NumberStyles.Float, CultureInfo.InvariantCulture,
                out float value) ? value : fallback;
    }
}