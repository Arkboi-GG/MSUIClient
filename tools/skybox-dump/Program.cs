using MSUIClient.Formats;

// Dumps LightSkybox.dbc (id -> M2 path) and the LightParams rows that reference a
// skybox, so PLAN_18 Phase 2 knows the schema, the real model paths, and which
// zones use one (a test vantage). Pure data. Usage: skybox-dump <ClientData\Data>

string dataRoot = args.Length > 0 ? args[0] : FindDefaultData();
Console.WriteLine($"[dump] data root: {dataRoot}");
using var mpq = new MpqMount(dataRoot);

byte[]? sb = mpq.ReadFile(@"DBFilesClient\LightSkybox.dbc");
if (sb is null) { Console.WriteLine("LightSkybox.dbc NOT FOUND"); return 1; }
var sbDbc = DbcFile.Parse(sb);
if (sbDbc is null) { Console.WriteLine("LightSkybox.dbc parse failed"); return 1; }
Console.WriteLine($"LightSkybox.dbc: {sbDbc.RecordCount} records, {sbDbc.FieldCount} fields, {sbDbc.RecordSize} bytes/rec");
var skyboxById = new Dictionary<uint, string>();
for (int r = 0; r < sbDbc.RecordCount; r++)
{
    uint id = sbDbc.GetUInt(r, 0);
    string path = sbDbc.FieldCount > 1 ? sbDbc.GetString(r, 1) : "";
    uint flags = sbDbc.FieldCount > 2 ? sbDbc.GetUInt(r, 2) : 0;
    if (id != 0) skyboxById[id] = path;
    if (id != 0) Console.WriteLine($"  skybox {id,3}: flags {flags}  '{path}'");
}

byte[]? lp = mpq.ReadFile(LightParamsTable.MpqPath);
var lpDbc = lp is not null ? DbcFile.Parse(lp) : null;
if (lpDbc is not null)
{
    Console.WriteLine($"\nLightParams rows with a skybox (field 2 != 0):");
    int n = 0;
    for (int r = 0; r < lpDbc.RecordCount; r++)
    {
        uint id = lpDbc.GetUInt(r, 0);
        uint skyId = lpDbc.GetUInt(r, 2);
        if (id == 0 || skyId == 0) continue;
        n++;
        string model = skyboxById.TryGetValue(skyId, out var m) ? m : "<unknown>";
        Console.WriteLine($"  LightParams {id,4} -> skybox {skyId,3} '{model}'");
    }
    Console.WriteLine($"  ({n} LightParams row(s) reference a skybox)");
}

// Cross-ref to MAP + zone so a test vantage can be chosen.
byte[]? lt = mpq.ReadFile(LightTable.MpqPath);
var lightTable = lt is not null ? LightTable.Parse(lt) : null;
if (lightTable is not null && lpDbc is not null)
{
    Console.WriteLine($"\nMap zones whose CLEAR params carry a skybox (map / light id / params / model):");
    var paramsSkybox = new Dictionary<uint, uint>();
    for (int r = 0; r < lpDbc.RecordCount; r++)
    {
        uint id = lpDbc.GetUInt(r, 0); uint skyId = lpDbc.GetUInt(r, 2);
        if (id != 0 && skyId != 0) paramsSkybox[id] = skyId;
    }
    int shown = 0;
    foreach (var z in lightTable.Zones)
    {
        if (!paramsSkybox.TryGetValue(z.ParamsClear, out uint skyId)) continue;
        string model = skyboxById.TryGetValue(skyId, out var m) ? m : "<unknown>";
        Console.WriteLine($"  map {z.MapId,3} light {z.Id,4} params {z.ParamsClear,4} -> '{model}'" +
            (z.IsMapDefault ? "  (map default)" : ""));
        if (++shown >= 40) { Console.WriteLine("  ... (truncated)"); break; }
    }
}
return 0;

static string FindDefaultData()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        string cand = Path.Combine(dir.FullName, "GameData", "Data");
        if (Directory.Exists(cand)) return cand;
        dir = dir.Parent;
    }
    return @"GameData\Data";
}
