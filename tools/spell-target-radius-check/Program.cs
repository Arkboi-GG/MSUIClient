using MSUIClient;
using MSUIClient.Formats;
using MSUIClient.Net;

if (args.Length != 1)
{
    Console.Error.WriteLine("usage: spell-target-radius-check <client-config.json>");
    return 2;
}

int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}

ClientConfig config = ClientConfig.Load(args[0]);
using var mpq = new MpqMount(config.ClientDataPath);
SpellCatalog catalog = SpellCatalog.Load(mpq)
    ?? throw new InvalidOperationException("Spell catalog unavailable");
DbcFile spells = ReadDbc(SpellCatalog.SpellPath);
DbcFile radii = ReadDbc(SpellCatalog.RadiusPath);

Check((spells.RecordCount, spells.FieldCount, spells.RecordSize) == (22357, 173, 692),
    $"Spell.dbc is not the pinned build-5875 table: " +
    $"{spells.RecordCount}x{spells.FieldCount}x{spells.RecordSize}");
Check((radii.RecordCount, radii.FieldCount, radii.RecordSize) == (24, 4, 16),
    "SpellRadius.dbc is not the pinned build-5875 table");

var radiusRows = new Dictionary<uint, SpellRadiusRow>();
for (int row = 0; row < radii.RecordCount; row++)
{
    uint id = radii.GetUInt(row, 0);
    var value = new SpellRadiusRow(radii.GetFloat(row, 1), radii.GetFloat(row, 2),
        radii.GetFloat(row, 3));
    Check(float.IsFinite(value.Radius) && value.Radius >= 0f &&
          float.IsFinite(value.RadiusPerLevel) && float.IsFinite(value.RadiusMax),
        $"radius row {id} is non-finite or negative");
    Check(value.RadiusPerLevel == 0f,
        $"radius row {id} gained a level term; targeting law must implement scaling");
    Check(value.RadiusMax == value.Radius,
        $"radius row {id} gained a distinct cap; targeting law must implement scaling");
    Check(radiusRows.TryAdd(id, value), $"duplicate radius row {id}");
    if (id != 0)
        Check(catalog.TryGetRadius(id, out SpellRadiusRow loaded) && loaded == value,
            $"catalog did not preserve radius row {id}");
}

var rawSpellRows = Enumerable.Range(0, spells.RecordCount)
    .ToDictionary(row => spells.GetUInt(row, 0));
var probes = new Dictionary<uint, float>
{
    [10] = 8f,       // Blizzard
    [2120] = 5f,     // Flamestrike
    [5740] = 8f,     // Rain of Fire
    [1543] = 10f,    // Flare
    [1725] = 10f,    // Distract
    [26540] = 15f,   // Starfall
    [4064] = 3f,     // Rough Copper Bomb
    [4065] = 5f,     // Large Copper Bomb
    [12421] = 8f,    // Mithril Frag Bomb
    [6196] = 100f,   // Far Sight
    [30091] = 8f,    // mixed 8-yard impact / 5-yard persistent area
    [31364] = 20f,   // mixed 10-yard impact / 20-yard effect
};

int groundSpells = 0, authored = 0, fallback = 0;
var distinctAuthored = new SortedSet<float>();
var fallbackExamples = new List<string>();
foreach (SpellInfo spell in catalog.Spells.OrderBy(s => s.Id))
{
    if (CastTargetLaw.Resolve(spell, null, null).Kind != CastTargetKind.Ground) continue;
    groundSpells++;
    int row = rawSpellRows[spell.Id];
    float expected = 0f;
    for (int lane = 0; lane < 3; lane++)
    {
        if (spells.GetUInt(row, 61 + lane) == 0) continue;
        uint radiusId = spells.GetUInt(row, 88 + lane);
        if (radiusRows.TryGetValue(radiusId, out SpellRadiusRow radius) &&
            float.IsFinite(radius.Radius) && radius.Radius > 0f)
            expected = Math.Max(expected, radius.Radius);
    }

    bool resolved = catalog.TryGetTargetingRadius(spell, out float actual);
    Check(resolved == (expected > 0f),
        $"spell {spell.Id} radius presence differs from its populated effect lanes");
    if (resolved)
    {
        authored++;
        distinctAuthored.Add(actual);
        Check(actual == expected, $"spell {spell.Id} expected {expected:R} yd, got {actual:R}");
        Check(catalog.TargetingRadius(spell) == expected,
            $"spell {spell.Id} public targeting radius disagrees with exact law");
    }
    else
    {
        fallback++;
        if (fallbackExamples.Count < 8) fallbackExamples.Add($"{spell.Id}:{spell.Name}");
        Check(catalog.TargetingRadius(spell) == SpellCatalog.MissingTargetRadiusFallback,
            $"spell {spell.Id} did not take the explicit missing-radius fallback");
    }
}

Check(groundSpells == 218, "ground-target spell census differs from mounted build 5875");
foreach ((uint id, float expected) in probes)
{
    Check(catalog.TryGet(id, out SpellInfo spell), $"probe spell {id} is missing");
    Check(CastTargetLaw.Resolve(spell, null, null).Kind == CastTargetKind.Ground,
        $"probe spell {id} no longer enters location-target mode");
    Check(catalog.TargetingRadius(spell) == expected,
        $"probe spell {id} expected {expected:R} yd, got {catalog.TargetingRadius(spell):R}");
    Console.WriteLine($"[radius-probe] spell={id} name={spell.Name} radius={expected:R}yd");
}

Check(catalog.TryGet(30091, out SpellInfo flamestrike) &&
      flamestrike.EffectRadiusIndices!.SequenceEqual([14u, 8u, 0u]),
    "mixed-radius Flamestrike lanes were not retained");
Check(catalog.TryGet(31364, out SpellInfo mortar) &&
      mortar.EffectRadiusIndices!.SequenceEqual([13u, 9u, 0u]),
    "mixed-radius Spice Mortar lanes were not retained");

Console.WriteLine($"[radius-census] ground={groundSpells} authored={authored} fallback={fallback} " +
    $"distinct={string.Join('/', distinctAuthored.Select(r => r.ToString("R")))}");
Console.WriteLine($"[radius-fallback] size={SpellCatalog.MissingTargetRadiusFallback:R}yd " +
    $"examples={string.Join(" | ", fallbackExamples)}");
Console.WriteLine($"spell-target-radius-check: PASS checks={checks}");
return 0;

DbcFile ReadDbc(string path)
    => mpq.ReadFile(path) is { } bytes && DbcFile.Parse(bytes) is { } dbc
        ? dbc : throw new InvalidOperationException($"Missing or invalid {path}");
