using System.Globalization;
using System.Numerics;
using System.Text;

namespace MSUIClient.Net;

// ─────────────────────────────────────────────────────────────────────────────
// The Encounter Lab's data feed. Five behaviour tables over MangosSuperUI's
// EXISTING CSV export endpoint — no new server endpoint, no deploy, so the Lab
// works against today's box.
//
// THREADING CONTRACT (same as the NPC dev window's, and just as binding): fetch
// and parse run on a background Task and publish ONE immutable snapshot through
// a volatile field. The game thread only ever reads it. Nothing here may touch
// EntityStore, Settings, ImGui or GL.
//
// Deliberately a separate class from DevDataClient rather than an extension of
// it: that file belongs to the in-flight NPC dev work, and two features sharing
// one fetcher would couple their release cycles for no benefit.
// ─────────────────────────────────────────────────────────────────────────────

public sealed class EncounterDataClient(string repoRoot)
{
    /// <summary>Client build 5875. spell_target_position rows are build-ranged and a
    /// naive read would pick up rows meant for other client versions.</summary>
    public const int ContentBuild = 5875;

    private static readonly string[] Tables =
    [
        "creature_spells",
        "creature_ai_events",
        "creature_ai_scripts",
        "spell_target_position",
        "spell_cone",
        // ~2 MB, cached 12 h like the rest. Needed to answer "which of the three
        // behaviour tiers is this creature in" — without it a creature picked in the
        // world has no spell_list_id and no script_name, so its C++ hole never shows.
        "creature_template",
    ];

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly string _cacheDirectory = Path.Combine(repoRoot, "dev-cache");
    private volatile EncounterWorldData? _data;
    private Task? _fetching;

    public EncounterWorldData? Data => _data;
    public bool Fetching => _fetching is { IsCompleted: false };

    public TimeSpan? CacheAge
    {
        get
        {
            string probe = CachePath(Tables[0]);
            return File.Exists(probe) ? DateTime.UtcNow - File.GetLastWriteTimeUtc(probe) : null;
        }
    }

    private string CachePath(string table) => Path.Combine(_cacheDirectory, $"{table}.csv");

    /// <summary>Kick a background load of every behaviour table. No-op while one is
    /// in flight. Fresh cache (&lt; 12 h) parses from disk without touching HTTP.</summary>
    public void BeginFetch(string baseUrl, bool forceRefresh = false)
    {
        if (Fetching) return;
        _fetching = Task.Run(() => Fetch(baseUrl, forceRefresh));
    }

    private async Task Fetch(string baseUrl, bool forceRefresh)
    {
        var raw = new Dictionary<string, string>(Tables.Length);
        bool anyHttp = false, anyCache = false;
        string? error = null;

        foreach (string table in Tables)
        {
            string cachePath = CachePath(table);
            bool cacheFresh = File.Exists(cachePath) &&
                              DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath) < TimeSpan.FromHours(12);
            if (!forceRefresh && cacheFresh)
            {
                try
                {
                    raw[table] = await File.ReadAllTextAsync(cachePath, Encoding.UTF8).ConfigureAwait(false);
                    anyCache = true;
                    continue;
                }
                catch (Exception ex) { error ??= $"{table} cache read failed: {ex.Message}"; }
            }

            try
            {
                string url = $"{baseUrl.TrimEnd('/')}/Database/Export/mangos/{table}";
                string csv = await _http.GetStringAsync(url).ConfigureAwait(false);
                Directory.CreateDirectory(_cacheDirectory);
                await File.WriteAllTextAsync(cachePath, csv, Encoding.UTF8).ConfigureAwait(false);
                raw[table] = csv;
                anyHttp = true;
            }
            catch (Exception ex)
            {
                // Any cache age beats no data — the window reports the age itself.
                error ??= $"{table}: {ex.Message}";
                if (File.Exists(cachePath))
                {
                    try
                    {
                        raw[table] = await File.ReadAllTextAsync(cachePath, Encoding.UTF8).ConfigureAwait(false);
                        anyCache = true;
                    }
                    catch { /* leave the table absent; parse degrades to empty */ }
                }
            }
        }

        try
        {
            _data = Parse(raw, anyHttp ? (anyCache ? "csv+cache" : "csv") : "csv-cache", error);
            Console.WriteLine($"[encounter-data] {_data.Describe()} ({_data.Source})");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[encounter-data] parse failed: {ex.Message}");
            _data = EncounterWorldData.Empty($"parse failed: {ex.Message}");
        }
    }

    private static EncounterWorldData Parse(
        IReadOnlyDictionary<string, string> raw, string source, string? error) => new()
    {
        FetchedUtc = DateTime.UtcNow,
        Source = source,
        Error = error,
        SpellListsByEntry = ParseCreatureSpells(raw.GetValueOrDefault("creature_spells")),
        EventsByCreature = ParseEventAi(raw.GetValueOrDefault("creature_ai_events")),
        ScriptsById = ParseAiScripts(raw.GetValueOrDefault("creature_ai_scripts")),
        TargetPositions = ParseTargetPositions(raw.GetValueOrDefault("spell_target_position")),
        ConeDegrees = ParseCones(raw.GetValueOrDefault("spell_cone")),
        Bindings = ParseBindings(raw.GetValueOrDefault("creature_template")),
    };

    /// <summary>creature_template is multi-row per entry (the `patch` column):
    /// highest patch wins, the same rule the NPC dev window and the snapshot
    /// endpoint apply. This is a DIFFERENT trap from spell_template's `build`.</summary>
    private static Dictionary<uint, CreatureBehaviourBinding> ParseBindings(string? csv)
    {
        var result = new Dictionary<uint, CreatureBehaviourBinding>();
        if (csv is null) return result;
        var table = new CsvTable(csv);
        if (!table.HasRows) return result;

        var bestPatch = new Dictionary<uint, uint>();
        foreach (CsvRow row in table.Rows)
        {
            uint entry = row.U32("entry");
            if (entry == 0) continue;
            uint patch = row.U32("patch");
            if (bestPatch.TryGetValue(entry, out uint seen) && patch < seen) continue;
            bestPatch[entry] = patch;

            result[entry] = new CreatureBehaviourBinding(
                entry,
                row.Text("name"),
                row.Text("ai_name"),
                row.Text("script_name"),
                row.U32("spell_list_id"),
                row.U32("health_max", 100000));
        }
        return result;
    }

    // ── parsers (all header-name based: schema drift degrades, never mis-parses) ──

    private static Dictionary<uint, CreatureSpellList> ParseCreatureSpells(string? csv)
    {
        var result = new Dictionary<uint, CreatureSpellList>();
        if (csv is null) return result;
        var table = new CsvTable(csv);
        if (!table.HasRows) return result;

        foreach (CsvRow row in table.Rows)
        {
            uint entry = row.U32("entry");
            if (entry == 0) continue;
            List<CreatureSpellSlot> slots = [];
            for (int i = 1; i <= 8; i++)
            {
                uint spellId = row.U32($"spellId_{i}");
                if (spellId == 0) continue;
                // SECONDS in the DB → milliseconds here. ObjectMgr multiplies by
                // IN_MILLISECONDS at load; matching that is the difference between a
                // 12-second cooldown and a 12-millisecond one.
                slots.Add(new CreatureSpellSlot(
                    i,
                    spellId,
                    row.I32($"probability_{i}", 100),
                    row.I32($"castTarget_{i}", 1),
                    row.U32($"targetParam1_{i}"),
                    row.U32($"targetParam2_{i}"),
                    row.U32($"castFlags_{i}"),
                    row.I32($"delayInitialMin_{i}") * 1000,
                    row.I32($"delayInitialMax_{i}") * 1000,
                    row.I32($"delayRepeatMin_{i}") * 1000,
                    row.I32($"delayRepeatMax_{i}") * 1000,
                    row.U32($"scriptId_{i}")));
            }
            if (slots.Count == 0) continue;
            result[entry] = new CreatureSpellList(entry, row.Text("name"), slots);
        }
        return result;
    }

    private static Dictionary<uint, IReadOnlyList<EventAiEvent>> ParseEventAi(string? csv)
    {
        var byCreature = new Dictionary<uint, List<EventAiEvent>>();
        if (csv is null) return [];
        var table = new CsvTable(csv);
        if (!table.HasRows) return [];

        foreach (CsvRow row in table.Rows)
        {
            uint creature = row.U32("creature_id");
            if (creature == 0) continue;
            var simEvent = new EventAiEvent(
                row.U32("id"), creature, row.U32("condition_id"),
                row.I32("event_type"), row.U32("event_inverse_phase_mask"),
                row.I32("event_chance", 100), row.U32("event_flags"),
                row.I32("event_param1"), row.I32("event_param2"),
                row.I32("event_param3"), row.I32("event_param4"),
                row.U32("action1_script"), row.U32("action2_script"), row.U32("action3_script"),
                row.Text("comment"));
            if (!byCreature.TryGetValue(creature, out List<EventAiEvent>? list))
                byCreature[creature] = list = [];
            list.Add(simEvent);
        }
        return byCreature.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<EventAiEvent>)kv.Value);
    }

    private static Dictionary<uint, IReadOnlyList<AiScriptCommand>> ParseAiScripts(string? csv)
    {
        var byId = new Dictionary<uint, List<AiScriptCommand>>();
        if (csv is null) return [];
        var table = new CsvTable(csv);
        if (!table.HasRows) return [];

        foreach (CsvRow row in table.Rows)
        {
            uint id = row.U32("id");
            if (id == 0) continue;
            var command = new AiScriptCommand(
                id, row.I32("delay"), row.I32("command"),
                row.U32("datalong"), row.U32("datalong2"), row.U32("datalong3"), row.U32("datalong4"),
                row.U32("target_param1"), row.U32("target_param2"), row.I32("target_type"),
                row.I32("dataint"), row.I32("dataint2"),
                new Vector3(row.F32("x"), row.F32("y"), row.F32("z")), row.F32("o"),
                row.Text("comments", "comment"));
            if (!byId.TryGetValue(id, out List<AiScriptCommand>? list)) byId[id] = list = [];
            list.Add(command);
        }
        foreach (List<AiScriptCommand> list in byId.Values)
            list.Sort((a, b) => a.Delay.CompareTo(b.Delay));
        return byId.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<AiScriptCommand>)kv.Value);
    }

    private static Dictionary<uint, SpellTargetPosition> ParseTargetPositions(string? csv)
    {
        var result = new Dictionary<uint, SpellTargetPosition>();
        if (csv is null) return result;
        var table = new CsvTable(csv);
        if (!table.HasRows) return result;

        foreach (CsvRow row in table.Rows)
        {
            uint id = row.U32("id");
            if (id == 0) continue;
            // Build-ranged rows: keep only what this client build would see.
            int buildMin = row.I32("build_min"), buildMax = row.I32("build_max", ContentBuild);
            if (buildMax != 0 && ContentBuild > buildMax) continue;
            if (buildMin != 0 && ContentBuild < buildMin) continue;
            result[id] = new SpellTargetPosition(
                id, row.I32("target_map"),
                new Vector3(row.F32("target_position_x"), row.F32("target_position_y"),
                    row.F32("target_position_z")),
                row.F32("target_orientation"));
        }
        return result;
    }

    private static Dictionary<uint, float> ParseCones(string? csv)
    {
        var result = new Dictionary<uint, float>();
        if (csv is null) return result;
        var table = new CsvTable(csv);
        if (!table.HasRows) return result;
        foreach (CsvRow row in table.Rows)
        {
            uint entry = row.U32("entry");
            if (entry == 0) continue;
            // Sign preserved on purpose: negative = rear arc.
            result[entry] = row.F32("cone_degrees");
        }
        return result;
    }

    // ── minimal RFC 4180 reader ──────────────────────────────────────────────

    private sealed class CsvTable
    {
        private readonly Dictionary<string, int> _header;
        public readonly List<CsvRow> Rows = [];
        public bool HasRows => Rows.Count > 0;

        public CsvTable(string text)
        {
            List<List<string>> rows = ParseCsv(text);
            _header = rows.Count == 0
                ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                : rows[0].Select((name, i) => (name, i))
                    .ToDictionary(x => x.name.Trim(), x => x.i, StringComparer.OrdinalIgnoreCase);
            for (int r = 1; r < rows.Count; r++) Rows.Add(new CsvRow(_header, rows[r]));
        }

        private static List<List<string>> ParseCsv(string text)
        {
            var rows = new List<List<string>>();
            var row = new List<string>();
            var field = new StringBuilder();
            bool quoted = false;
            int i = text.Length > 0 && text[0] == '﻿' ? 1 : 0;   // BOM survives GetStringAsync

            void EndField() { row.Add(field.ToString()); field.Clear(); }
            void EndRow()
            {
                EndField();
                if (row.Count > 1 || row[0].Length > 0) rows.Add(row);
                row = [];
            }

            for (; i < text.Length; i++)
            {
                char c = text[i];
                if (quoted)
                {
                    if (c != '"') { field.Append(c); continue; }
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else quoted = false;
                }
                else if (c == '"') quoted = true;
                else if (c == ',') EndField();
                else if (c == '\r') { }
                else if (c == '\n') EndRow();
                else field.Append(c);
            }
            if (field.Length > 0 || row.Count > 0) EndRow();
            return rows;
        }
    }

    private readonly struct CsvRow(Dictionary<string, int> header, List<string> cells)
    {
        /// <summary>
        /// THE APOSTROPHE TRAP. MangosSuperUI's CSV export writes negative numbers with
        /// a leading apostrophe — `'-215.238`, `'-120` — the Excel "treat this as text"
        /// escape. A plain float.TryParse rejects that and falls back to the default,
        /// so EVERY negative coordinate and every rear-arc cone silently reads as ZERO:
        /// Onyxia's breath lanes collapse onto the origin and Tail Sweep's -120 rear arc
        /// becomes a 0-degree nothing. Strip it before parsing, always.
        /// </summary>
        private string Raw(string column)
        {
            if (!header.TryGetValue(column, out int at) || at >= cells.Count) return "";
            string value = cells[at];
            return value.Length > 0 && value[0] == '\'' ? value[1..] : value;
        }

        public string Text(params string[] columns)
        {
            foreach (string column in columns)
            {
                string value = Raw(column);
                if (value.Length > 0) return value;
            }
            return "";
        }

        public uint U32(string column, uint fallback = 0) =>
            uint.TryParse(Raw(column), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint v)
                ? v : fallback;

        public int I32(string column, int fallback = 0) =>
            int.TryParse(Raw(column), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
                ? v : fallback;

        public float F32(string column, float fallback = 0f) =>
            float.TryParse(Raw(column), NumberStyles.Float, CultureInfo.InvariantCulture, out float v)
                ? v : fallback;
    }
}
