using System.Globalization;
using System.Text;
using System.Text.Json;
using MSUIClient.Formats;

namespace MSUIClient.Net;

// ─────────────────────────────────────────────────────────────────────────────
// The NPC dev window's data feed: creature_template pulled from MangosSuperUI's
// existing CSV export endpoint (/Database/Export/mangos/creature_template) —
// the same source creator-creatures.tsv was hand-dumped from, fetched live so
// aggro fields (detection_range etc.) stay current without a manual export.
//
// THREADING CONTRACT: fetch + parse run on a background Task; the game thread
// only ever reads the volatile completed-result reference. Results are
// immutable once published. Nothing here may touch EntityStore or Settings.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>One creature_template row's dev-window subset. Field defaults match
/// vmangos schema defaults so an absent column degrades to server behavior.</summary>
public sealed record NpcTemplateInfo(
    uint Entry,
    string Name,
    int LevelMin,
    int LevelMax,
    uint Faction,
    float DetectionRange,      // vmangos default 18; < 1 = never aggro by proximity
    float CallForHelpRange,    // vmangos default 5
    float LeashRange,          // 0 = config threat radius
    uint MovementType,         // template default: 0 idle, 1 random, 2 waypoint
    uint FlagsExtra,           // CREATURE_FLAG_EXTRA_NO_AGGRO = 0x2
    uint StaticFlags);         // SESSILE 0x100, IGNORE_COMBAT 0x2000000

/// <summary>Per-packet verdict the /NpcDev/Apply endpoint returns.</summary>
public sealed record NpcApplyPacketVerdict(int Id, string Type, string Verdict, string? Message, long? AuditId);

/// <summary>Published result of a background change-set commit (POST /NpcDev/Apply).
/// Ok=false means the round trip failed (see Error); per-packet outcomes live in Results.</summary>
public sealed record NpcApplyResult(bool Ok, int Applied, int Stale, int Failed, string? BatchId,
    IReadOnlyList<NpcApplyPacketVerdict> Results, string? Error, DateTime CompletedUtc);

/// <summary>Whether a spawn (its row/path) and its entry's template differ from the OG baseline.
/// HasBaseline=false ⇒ the creature baselines haven't been captured yet.</summary>
public sealed record NpcDiff(bool HasBaseline, uint Guid, uint Entry, bool SpawnModified,
    bool PathModified, bool TemplateModified, bool BaselineHasSpawn)
{
    public bool Modified => SpawnModified || PathModified || TemplateModified;
}

public sealed class DevDataClient
{
    /// <summary>An immutable published snapshot of the template table.</summary>
    public sealed record TemplateResult(
        IReadOnlyDictionary<uint, NpcTemplateInfo> ByEntry,
        DateTime FetchedUtc,
        string Source,          // "http" or "cache"
        string? Error);         // non-null = the fetch failed (result may be stale cache)

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly string _cachePath;
    private volatile TemplateResult? _templates;
    private Task? _fetching;

    public DevDataClient(string repoRoot)
        => _cachePath = Path.Combine(repoRoot, "dev-cache", "creature_template.csv");

    /// <summary>The latest completed snapshot; null until the first fetch/cache parse lands.</summary>
    public TemplateResult? Templates => _templates;

    public bool Fetching => _fetching is { IsCompleted: false };

    /// <summary>Age of the on-disk cache, or null when none exists.</summary>
    public TimeSpan? CacheAge =>
        File.Exists(_cachePath) ? DateTime.UtcNow - File.GetLastWriteTimeUtc(_cachePath) : null;

    /// <summary>
    /// Kick a background template load. Fresh cache (&lt; 12 h) parses from disk;
    /// otherwise HTTP, falling back to any cache on failure. No-op while a fetch
    /// is already in flight.
    /// </summary>
    public void BeginFetchTemplates(string baseUrl, bool forceRefresh = false)
    {
        if (Fetching) return;
        _fetching = Task.Run(() => FetchTemplates(baseUrl, forceRefresh));
    }

    private async Task FetchTemplates(string baseUrl, bool forceRefresh)
    {
        bool cacheFresh = CacheAge is { } age && age < TimeSpan.FromHours(12);
        if (!forceRefresh && cacheFresh && TryPublishFromCache(error: null)) return;

        try
        {
            string url = $"{baseUrl.TrimEnd('/')}/Database/Export/mangos/creature_template";
            string csv = await _http.GetStringAsync(url).ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            await File.WriteAllTextAsync(_cachePath, csv, Encoding.UTF8).ConfigureAwait(false);
            _templates = new TemplateResult(ParseTemplates(csv), DateTime.UtcNow, "http", null);
            Console.WriteLine($"[dev-data] creature_template fetched: {_templates.ByEntry.Count} entries");
        }
        catch (Exception ex)
        {
            string error = $"fetch failed: {ex.Message}";
            Console.WriteLine($"[dev-data] {error}");
            if (!TryPublishFromCache(error))
                _templates = new TemplateResult(
                    new Dictionary<uint, NpcTemplateInfo>(), DateTime.UtcNow, "none", error);
        }
    }

    private bool TryPublishFromCache(string? error)
    {
        try
        {
            if (!File.Exists(_cachePath)) return false;
            string csv = File.ReadAllText(_cachePath, Encoding.UTF8);
            _templates = new TemplateResult(ParseTemplates(csv),
                File.GetLastWriteTimeUtc(_cachePath), "cache", error);
            Console.WriteLine($"[dev-data] creature_template from cache: {_templates.ByEntry.Count} entries");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[dev-data] cache parse failed: {ex.Message}");
            return false;
        }
    }

    // ── CSV → templates ──────────────────────────────────────────────────────

    /// <summary>Columns are located by header name so schema drift degrades to
    /// defaults instead of mis-parsing. Multi-patch rows: highest patch wins
    /// (same rule creator-creatures.tsv was generated with).</summary>
    private static Dictionary<uint, NpcTemplateInfo> ParseTemplates(string csv)
    {
        var rows = ParseCsv(csv);
        var result = new Dictionary<uint, NpcTemplateInfo>(12000);
        if (rows.Count < 2) return result;

        var header = rows[0].Select((name, i) => (name, i))
            .ToDictionary(x => x.name.Trim(), x => x.i, StringComparer.OrdinalIgnoreCase);
        int Col(params string[] names)
        {
            foreach (string name in names)
                if (header.TryGetValue(name, out int at)) return at;
            return -1;
        }

        int cEntry = Col("entry");
        int cName = Col("name");
        int cLevelMin = Col("level_min", "minlevel");
        int cLevelMax = Col("level_max", "maxlevel");
        int cFaction = Col("faction", "faction_A");
        int cDetection = Col("detection_range");
        int cCallForHelp = Col("call_for_help_range");
        int cLeash = Col("leash_range");
        int cMovement = Col("movement_type", "MovementType");
        int cFlagsExtra = Col("flags_extra");
        int cStaticFlags = Col("static_flags1", "static_flags");   // SESSILE + IGNORE_COMBAT live in word 1
        int cPatch = Col("patch");
        if (cEntry < 0) return result;

        var bestPatch = new Dictionary<uint, uint>(12000);
        for (int r = 1; r < rows.Count; r++)
        {
            List<string> row = rows[r];
            string Get(int col) => col >= 0 && col < row.Count ? row[col] : "";
            uint U(int col, uint fallback = 0) =>
                uint.TryParse(Get(col), NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out uint v) ? v : fallback;
            int I(int col, int fallback = 0) =>
                int.TryParse(Get(col), NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out int v) ? v : fallback;
            float F(int col, float fallback = 0f) =>
                float.TryParse(Get(col), NumberStyles.Float, CultureInfo.InvariantCulture,
                    out float v) ? v : fallback;

            uint entry = U(cEntry);
            if (entry == 0) continue;
            uint patch = U(cPatch);
            if (bestPatch.TryGetValue(entry, out uint seen) && patch < seen) continue;
            bestPatch[entry] = patch;

            result[entry] = new NpcTemplateInfo(
                entry,
                Get(cName),
                I(cLevelMin, 1),
                I(cLevelMax, 1),
                U(cFaction),
                F(cDetection, 18f),
                F(cCallForHelp, 5f),
                F(cLeash),
                U(cMovement),
                U(cFlagsExtra),
                U(cStaticFlags));
        }
        return result;
    }

    // ── World data (spawn rows + waypoint paths for one map) ─────────────────
    //
    // Two transports, one result shape (DevWorldData):
    //   1. GET <sui>/NpcDev/Snapshot?...      — area-limited JSON, one round trip.
    //      Requires the NpcDevController deploy; 404s gracefully until then.
    //   2. GET <sui>/Database/Export/mangos/… — the pre-existing CSV exports:
    //      creature (filtered by map), creature_movement, creature_movement_template.
    //      Whole-map, ~9 MB total, disk-cached 12 h. Works against today's deploy.

    private volatile DevWorldData? _world;
    private Task? _worldFetching;

    /// <summary>The latest published world snapshot; null until the first fetch lands.</summary>
    public DevWorldData? World => _world;

    public bool WorldFetching => _worldFetching is { IsCompleted: false };

    /// <summary>Kick a background world load for one map. No-op while one is in flight.
    /// (nearX, nearY) centre the JSON snapshot's area; the CSV fallback ignores them
    /// (whole map).</summary>
    public void BeginFetchWorld(string baseUrl, int map, float nearX, float nearY,
        bool forceRefresh = false)
    {
        if (WorldFetching) return;
        _worldFetching = Task.Run(() => FetchWorld(baseUrl, map, nearX, nearY, forceRefresh));
    }

    private async Task FetchWorld(string baseUrl, int map, float nearX, float nearY, bool force)
    {
        if (await TryFetchWorldSnapshot(baseUrl, map, nearX, nearY)) return;
        await FetchWorldFromCsv(baseUrl, map, force);
    }

    // ── transport 1: JSON snapshot ───────────────────────────────────────────

    private sealed record SnapCreature(uint Guid, uint Id, uint Id2, uint Id3, uint Id4, uint Id5,
        int Map, float PositionX, float PositionY, float PositionZ, float Orientation,
        uint SpawnTimeSecsMin, uint SpawnTimeSecsMax, float WanderDistance,
        uint MovementType, uint SpawnFlags);

    private sealed record SnapWaypoint(uint Id, uint Entry, uint Point,
        float PositionX, float PositionY, float PositionZ, float Orientation,
        uint Waittime, float WanderDistance, uint ScriptId, uint PathId);

    private sealed record SnapRoot(DateTime FetchedUtc, int Map,
        List<SnapCreature> Creatures, List<SnapWaypoint> Movement,
        List<SnapWaypoint> MovementTemplates);

    private static readonly System.Text.Json.JsonSerializerOptions SnapJson =
        new() { PropertyNameCaseInsensitive = true };

    private async Task<bool> TryFetchWorldSnapshot(string baseUrl, int map, float nearX, float nearY)
    {
        try
        {
            string url = $"{baseUrl.TrimEnd('/')}/NpcDev/Snapshot?map={map}" +
                         $"&nearX={nearX.ToString("0.#", CultureInfo.InvariantCulture)}" +
                         $"&nearY={nearY.ToString("0.#", CultureInfo.InvariantCulture)}&range=400";
            string json = await _http.GetStringAsync(url).ConfigureAwait(false);
            SnapRoot? root = System.Text.Json.JsonSerializer.Deserialize<SnapRoot>(json, SnapJson);
            if (root is null) return false;

            var spawns = new Dictionary<uint, DevSpawnRow>(root.Creatures.Count);
            foreach (SnapCreature c in root.Creatures)
                spawns[c.Guid] = new DevSpawnRow(c.Guid, c.Id,
                    new[] { c.Id, c.Id2, c.Id3, c.Id4, c.Id5 }.Where(e => e != 0).ToArray(),
                    c.Map, new System.Numerics.Vector3(c.PositionX, c.PositionY, c.PositionZ),
                    c.Orientation, c.SpawnTimeSecsMin, c.SpawnTimeSecsMax,
                    c.WanderDistance, c.MovementType, c.SpawnFlags);

            _world = new DevWorldData
            {
                Map = map,
                WholeMap = false,
                FetchedUtc = DateTime.UtcNow,
                Source = "snapshot",
                SpawnsByGuid = spawns,
                GuidPaths = GroupWaypoints(root.Movement, w => w.Id),
                TemplatePaths = GroupTemplateWaypoints(root.MovementTemplates),
            };
            Console.WriteLine($"[dev-data] world snapshot: {spawns.Count} spawns (map {map}, area)");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[dev-data] snapshot endpoint unavailable ({ex.Message}); using CSV export");
            return false;
        }
    }

    // ── transport 2: CSV exports (whole map) ─────────────────────────────────

    private async Task FetchWorldFromCsv(string baseUrl, int map, bool force)
    {
        try
        {
            string root = baseUrl.TrimEnd('/');
            string cacheDir = Path.GetDirectoryName(_cachePath)!;
            var creatureCsv = await GetCsvCached(
                $"{root}/Database/Export/mangos/creature?filterCol=map&filterVal={map}",
                Path.Combine(cacheDir, $"creature-map{map}.csv"), force);
            var movementCsv = await GetCsvCached(
                $"{root}/Database/Export/mangos/creature_movement",
                Path.Combine(cacheDir, "creature_movement.csv"), force);
            var templateCsv = await GetCsvCached(
                $"{root}/Database/Export/mangos/creature_movement_template",
                Path.Combine(cacheDir, "creature_movement_template.csv"), force);

            var spawns = ParseSpawnCsv(creatureCsv.Text, map);
            _world = new DevWorldData
            {
                Map = map,
                WholeMap = true,
                FetchedUtc = creatureCsv.FetchedUtc,
                Source = creatureCsv.FromCache || movementCsv.FromCache ? "csv-cache" : "csv",
                SpawnsByGuid = spawns,
                GuidPaths = GroupWaypoints(ParseWaypointCsv(movementCsv.Text, keyColumn: "id"), w => w.Id),
                TemplatePaths = GroupTemplateWaypoints(ParseWaypointCsv(templateCsv.Text, keyColumn: "entry")),
            };
            Console.WriteLine($"[dev-data] world from CSV: {spawns.Count} spawns (map {map}, whole map)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[dev-data] world fetch failed: {ex.Message}");
            _world ??= DevWorldData.Empty;
        }
    }

    private async Task<(string Text, DateTime FetchedUtc, bool FromCache)> GetCsvCached(
        string url, string cacheFile, bool force)
    {
        bool fresh = File.Exists(cacheFile) &&
                     DateTime.UtcNow - File.GetLastWriteTimeUtc(cacheFile) < TimeSpan.FromHours(12);
        if (!force && fresh)
            return (await File.ReadAllTextAsync(cacheFile, Encoding.UTF8).ConfigureAwait(false),
                File.GetLastWriteTimeUtc(cacheFile), true);
        try
        {
            string csv = await _http.GetStringAsync(url).ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
            await File.WriteAllTextAsync(cacheFile, csv, Encoding.UTF8).ConfigureAwait(false);
            return (csv, DateTime.UtcNow, false);
        }
        catch when (File.Exists(cacheFile))
        {
            // Any cache beats no data; the window shows the age.
            return (await File.ReadAllTextAsync(cacheFile, Encoding.UTF8).ConfigureAwait(false),
                File.GetLastWriteTimeUtc(cacheFile), true);
        }
    }

    private static Dictionary<uint, DevSpawnRow> ParseSpawnCsv(string csv, int map)
    {
        var rows = ParseCsv(csv);
        var result = new Dictionary<uint, DevSpawnRow>(rows.Count);
        if (rows.Count < 2) return result;
        var col = HeaderMap(rows[0]);
        int cGuid = col.GetValueOrDefault("guid", -1);
        if (cGuid < 0) return result;
        int cId = col.GetValueOrDefault("id", -1);
        int[] cPool =
        [
            cId, col.GetValueOrDefault("id2", -1), col.GetValueOrDefault("id3", -1),
            col.GetValueOrDefault("id4", -1), col.GetValueOrDefault("id5", -1),
        ];
        int cX = col.GetValueOrDefault("position_x", -1);
        int cY = col.GetValueOrDefault("position_y", -1);
        int cZ = col.GetValueOrDefault("position_z", -1);
        int cO = col.GetValueOrDefault("orientation", -1);
        int cMin = col.GetValueOrDefault("spawntimesecsmin", -1);
        int cMax = col.GetValueOrDefault("spawntimesecsmax", -1);
        int cWander = col.GetValueOrDefault("wander_distance", -1);
        int cMove = col.GetValueOrDefault("movement_type", -1);
        int cFlags = col.GetValueOrDefault("spawn_flags", -1);

        for (int r = 1; r < rows.Count; r++)
        {
            List<string> row = rows[r];
            uint guid = ParseU(row, cGuid);
            if (guid == 0) continue;
            uint[] pool = cPool.Select(c => ParseU(row, c)).Where(e => e != 0).ToArray();
            result[guid] = new DevSpawnRow(guid, pool.Length > 0 ? pool[0] : 0, pool, map,
                new System.Numerics.Vector3(ParseF(row, cX), ParseF(row, cY), ParseF(row, cZ)),
                ParseF(row, cO), ParseU(row, cMin), ParseU(row, cMax),
                ParseF(row, cWander), ParseU(row, cMove), ParseU(row, cFlags));
        }
        return result;
    }

    private static List<SnapWaypoint> ParseWaypointCsv(string csv, string keyColumn)
    {
        var rows = ParseCsv(csv);
        var result = new List<SnapWaypoint>(rows.Count);
        if (rows.Count < 2) return result;
        var col = HeaderMap(rows[0]);
        int cKey = col.GetValueOrDefault(keyColumn, -1);
        if (cKey < 0) return result;
        int cPoint = col.GetValueOrDefault("point", -1);
        int cX = col.GetValueOrDefault("position_x", -1);
        int cY = col.GetValueOrDefault("position_y", -1);
        int cZ = col.GetValueOrDefault("position_z", -1);
        int cO = col.GetValueOrDefault("orientation", -1);
        int cWait = col.GetValueOrDefault("waittime", -1);
        int cWander = col.GetValueOrDefault("wander_distance", -1);
        int cScript = col.GetValueOrDefault("script_id", -1);
        int cPath = col.GetValueOrDefault("path_id", -1);

        for (int r = 1; r < rows.Count; r++)
        {
            List<string> row = rows[r];
            uint key = ParseU(row, cKey);
            if (key == 0) continue;
            result.Add(new SnapWaypoint(key, key, ParseU(row, cPoint),
                ParseF(row, cX), ParseF(row, cY), ParseF(row, cZ), ParseF(row, cO),
                ParseU(row, cWait), ParseF(row, cWander), ParseU(row, cScript), ParseU(row, cPath)));
        }
        return result;
    }

    private static Dictionary<uint, DevWaypointRow[]> GroupWaypoints(
        IEnumerable<SnapWaypoint> rows, Func<SnapWaypoint, uint> key) =>
        rows.GroupBy(key).ToDictionary(g => g.Key, g => g
            .OrderBy(w => w.Point)
            .Select(ToWaypointRow)
            .ToArray());

    private static Dictionary<uint, Dictionary<uint, DevWaypointRow[]>> GroupTemplateWaypoints(
        IEnumerable<SnapWaypoint> rows) =>
        rows.GroupBy(w => w.Entry).ToDictionary(g => g.Key, g => g
            .GroupBy(w => w.PathId)
            .ToDictionary(p => p.Key, p => p
                .OrderBy(w => w.Point)
                .Select(ToWaypointRow)
                .ToArray()));

    private static DevWaypointRow ToWaypointRow(SnapWaypoint w) => new(w.Point,
        new System.Numerics.Vector3(w.PositionX, w.PositionY, w.PositionZ), w.Orientation,
        w.Waittime, w.WanderDistance, w.ScriptId, w.PathId);

    private static Dictionary<string, int> HeaderMap(List<string> header) =>
        header.Select((name, i) => (name, i))
            .ToDictionary(x => x.name.Trim(), x => x.i, StringComparer.OrdinalIgnoreCase);

    private static uint ParseU(List<string> row, int col) =>
        col >= 0 && col < row.Count &&
        uint.TryParse(row[col], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint v)
            ? v : 0;

    private static float ParseF(List<string> row, int col) =>
        col >= 0 && col < row.Count &&
        float.TryParse(row[col], NumberStyles.Float, CultureInfo.InvariantCulture, out float v)
            ? v : 0f;

    /// <summary>Minimal RFC 4180 parser (the export is quoted CSV with a UTF-8 BOM;
    /// quoted fields may contain commas, quotes and newlines).</summary>
    private static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;
        int i = 0;
        if (text.Length > 0 && text[0] == '﻿') i = 1;   // UTF-8 BOM survives GetStringAsync

        void EndField() { row.Add(field.ToString()); field.Clear(); }
        void EndRow()
        {
            EndField();
            if (row.Count > 1 || row[0].Length > 0) rows.Add(row);
            row = new List<string>();
        }

        for (; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else quoted = false;
                }
                else field.Append(c);
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

    // ── change-set apply (POST /NpcDev/Apply) ────────────────────────────────
    //
    // The write side: POST the accumulated change-set to MangosSuperUI's audited
    // apply endpoint, off the game thread, and publish an immutable result. Making
    // the committed change LIVE is the client's separate owner-clicked reload (GM
    // command) — never done here.

    private static readonly JsonSerializerOptions ApplyJson =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    private volatile NpcApplyResult? _applyResult;
    private Task? _applying;

    /// <summary>The latest completed commit result; null until the first commit lands.</summary>
    public NpcApplyResult? ApplyResult => _applyResult;

    public bool Applying => _applying is { IsCompleted: false };

    /// <summary>POST the change-set to <paramref name="baseUrl"/>/NpcDev/Apply on a background
    /// Task, publishing an immutable <see cref="NpcApplyResult"/>. No-op while one is in flight.</summary>
    public void BeginApply(string baseUrl, DevChangeSet set)
    {
        if (Applying) return;
        _applyResult = null;
        _applying = Task.Run(() => PostApply(baseUrl, set));
    }

    private async Task PostApply(string baseUrl, DevChangeSet set)
    {
        try
        {
            string url = $"{baseUrl.TrimEnd('/')}/NpcDev/Apply";
            string json = JsonSerializer.Serialize(set, ApplyJson);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using HttpResponseMessage resp = await _http.PostAsync(url, content).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _applyResult = new NpcApplyResult(false, 0, 0, 0, null, [],
                    $"HTTP {(int)resp.StatusCode}: {TrimBody(body)}", DateTime.UtcNow);
                Console.WriteLine($"[dev-data] apply failed: {_applyResult.Error}");
                return;
            }
            ApplyDto? dto = JsonSerializer.Deserialize<ApplyDto>(body, ApplyJson);
            var results = (dto?.Results ?? []).Select(r =>
                new NpcApplyPacketVerdict(r.Id, r.Type ?? "", r.Verdict ?? "", r.Message, r.AuditId)).ToList();
            _applyResult = new NpcApplyResult(true, dto?.Applied ?? 0, dto?.Stale ?? 0, dto?.Failed ?? 0,
                dto?.BatchId, results, null, DateTime.UtcNow);
            Console.WriteLine($"[dev-data] apply: {_applyResult.Applied} applied, " +
                $"{_applyResult.Stale} stale, {_applyResult.Failed} failed (batch {_applyResult.BatchId})");
        }
        catch (Exception ex)
        {
            _applyResult = new NpcApplyResult(false, 0, 0, 0, null, [], ex.Message, DateTime.UtcNow);
            Console.WriteLine($"[dev-data] apply error: {ex.Message}");
        }
    }

    private static string TrimBody(string s) => s.Length <= 200 ? s : s[..200] + "…";

    private sealed class ApplyDto
    {
        public string? BatchId { get; set; }
        public int Applied { get; set; }
        public int Stale { get; set; }
        public int Failed { get; set; }
        public List<VerdictDto>? Results { get; set; }
    }

    private sealed class VerdictDto
    {
        public int Id { get; set; }
        public string? Type { get; set; }
        public string? Verdict { get; set; }
        public string? Message { get; set; }
        public long? AuditId { get; set; }
    }

    // ── OG baseline: diff (changed-from-original) + reset ────────────────────

    private volatile NpcDiff? _diff;
    private Task? _diffing;

    /// <summary>Latest OG diff (for the last-requested guid); null until the first lands.</summary>
    public NpcDiff? Diff => _diff;

    public bool Diffing => _diffing is { IsCompleted: false };

    /// <summary>Fetch whether one spawn (+ its entry's template) differs from the OG baseline.</summary>
    public void BeginDiff(string baseUrl, uint guid, uint entry)
    {
        if (Diffing) return;
        _diffing = Task.Run(() => FetchDiff(baseUrl, guid, entry));
    }

    private async Task FetchDiff(string baseUrl, uint guid, uint entry)
    {
        try
        {
            string url = $"{baseUrl.TrimEnd('/')}/NpcDev/Diff?guid={guid}&entry={entry}";
            string json = await _http.GetStringAsync(url).ConfigureAwait(false);
            DiffDto? d = JsonSerializer.Deserialize<DiffDto>(json, ApplyJson);
            _diff = d is null
                ? new NpcDiff(false, guid, entry, false, false, false, false)
                : new NpcDiff(d.HasBaseline, d.Guid == 0 ? guid : d.Guid, d.Entry == 0 ? entry : d.Entry,
                    d.SpawnModified, d.PathModified, d.TemplateModified, d.BaselineHasSpawn);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[dev-data] diff failed: {ex.Message}");
            _diff = new NpcDiff(false, guid, entry, false, false, false, false);
        }
    }

    private volatile NpcApplyResult? _resetResult;
    private Task? _resetting;

    /// <summary>Latest reset-to-original result; null until the first reset lands.</summary>
    public NpcApplyResult? ResetResult => _resetResult;

    public bool Resetting => _resetting is { IsCompleted: false };

    /// <summary>POST a reset-to-original request (restore guids' spawn+path, entries' template
    /// detection) to /NpcDev/Reset on a background Task; publishes an immutable result.</summary>
    public void BeginReset(string baseUrl, string character, IReadOnlyList<uint> guids, IReadOnlyList<uint> entries)
    {
        if (Resetting) return;
        _resetResult = null;
        _resetting = Task.Run(() => PostReset(baseUrl, character, guids, entries));
    }

    private async Task PostReset(string baseUrl, string character, IReadOnlyList<uint> guids, IReadOnlyList<uint> entries)
    {
        try
        {
            string url = $"{baseUrl.TrimEnd('/')}/NpcDev/Reset";
            string json = JsonSerializer.Serialize(new { character, guids, entries }, ApplyJson);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using HttpResponseMessage resp = await _http.PostAsync(url, content).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _resetResult = new NpcApplyResult(false, 0, 0, 0, null, [],
                    $"HTTP {(int)resp.StatusCode}: {TrimBody(body)}", DateTime.UtcNow);
                return;
            }
            ApplyDto? dto = JsonSerializer.Deserialize<ApplyDto>(body, ApplyJson);
            var results = (dto?.Results ?? []).Select(r =>
                new NpcApplyPacketVerdict(r.Id, r.Type ?? "", r.Verdict ?? "", r.Message, r.AuditId)).ToList();
            _resetResult = new NpcApplyResult(true, dto?.Applied ?? 0, dto?.Stale ?? 0, dto?.Failed ?? 0,
                dto?.BatchId, results, null, DateTime.UtcNow);
            Console.WriteLine($"[dev-data] reset: {_resetResult.Applied} applied, {_resetResult.Failed} failed");
        }
        catch (Exception ex)
        {
            _resetResult = new NpcApplyResult(false, 0, 0, 0, null, [], ex.Message, DateTime.UtcNow);
            Console.WriteLine($"[dev-data] reset error: {ex.Message}");
        }
    }

    private sealed class DiffDto
    {
        public bool HasBaseline { get; set; }
        public uint Guid { get; set; }
        public uint Entry { get; set; }
        public bool SpawnModified { get; set; }
        public bool PathModified { get; set; }
        public bool TemplateModified { get; set; }
        public bool BaselineHasSpawn { get; set; }
    }
}
