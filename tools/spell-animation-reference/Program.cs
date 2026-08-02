using System.Globalization;
using System.Text;
using Microsoft.VisualBasic.FileIO;
using MSUIClient;
using MSUIClient.Formats;

if (args.Length != 5)
{
    Console.Error.WriteLine("usage: spell-animation-reference <config.json> <spell-sweep.csv> <class> <expected.csv> <cohort-prefix>");
    return 2;
}

ClientConfig config = ClientConfig.Load(args[0]);
string input = Path.GetFullPath(args[1]), className = args[2];
string output = Path.GetFullPath(args[3]), cohortPrefix = Path.GetFullPath(args[4]);
using var mpq = new MpqMount(config.ClientDataPath);
SpellCatalog spells = SpellCatalog.Load(mpq) ?? throw new InvalidDataException("Spell DBC chain unavailable");
SpellVisualCatalog visuals = SpellVisualCatalog.Load(mpq) ?? throw new InvalidDataException("SpellVisual DBC chain unavailable");

var known = new SortedDictionary<uint, string>();
var passives = new SortedDictionary<uint, string>();
using (var parser = new TextFieldParser(input) { TextFieldType = FieldType.Delimited })
{
    parser.SetDelimiters(",");
    string[] header = parser.ReadFields() ?? throw new InvalidDataException("missing spell-sweep header");
    int idColumn = Array.IndexOf(header, "spell_id"), resultColumn = Array.IndexOf(header, "result_enum");
    while (!parser.EndOfData)
    {
        string[] fields = parser.ReadFields() ?? [];
        if (fields.Length != header.Length || !uint.TryParse(fields[idColumn], out uint id) ||
            !spells.TryGet(id, out SpellInfo spell)) continue;
        if (fields[resultColumn] == "ROSTER_KNOWN") known[id] = spell.Name;
        else if (fields[resultColumn] == "ROSTER_PASSIVE") passives[id] = spell.Name;
    }
}

Directory.CreateDirectory(Path.GetDirectoryName(output)!);
var lines = new List<string>
{
    "class,spell_id,name,rank,school,cast_type,cast_time_ms,duration_ms,speed,effect_ids,aura_ids,implicit_targets_a,implicit_targets_b,effect_misc_values,effect_item_types,visual_id,precast_kit,precast_animation,precast_models,cast_kit,cast_animation,cast_models,impact_kit,impact_animation,impact_models,state_kit,state_animation,state_models,channel_kit,channel_animation,channel_models,missile_model,coverage"
};
foreach (uint id in known.Keys)
{
    spells.TryGet(id, out SpellInfo spell);
    SpellVisualStages stages = visuals.TryGetStages(spell.VisualId, out var found) ? found : default;
    string[] precast = Stage(stages.Precast), cast = Stage(stages.Cast), impact = Stage(stages.Impact),
        state = Stage(stages.State), channel = Stage(stages.Channel);
    string missile = visuals.MissilePath(stages) is { } missilePath ? Source(missilePath) : "";
    lines.Add(string.Join(',', Csv(className), id, Csv(spell.Name), Csv(spell.Rank), spell.School,
        spell.CastClassification, spell.CastTimeMs, spell.DurationMs, spell.Speed.ToString("R", CultureInfo.InvariantCulture),
        Csv(Join(spell.EffectIds)), Csv(Join(spell.AuraIds)), Csv(Join(spell.ImplicitTargetsA)),
        Csv(Join(spell.ImplicitTargetsB)), Csv(Join(spell.EffectMiscValues)), Csv(Join(spell.EffectItemTypes)),
        spell.VisualId, stages.Precast, precast[0], Csv(precast[1]),
        stages.Cast, cast[0], Csv(cast[1]), stages.Impact, impact[0], Csv(impact[1]),
        stages.State, state[0], Csv(state[1]), stages.Channel, channel[0], Csv(channel[1]),
        Csv(missile), spell.VisualId == 0 ? "NOT-PRESENT" : "MEASURED"));
}
File.WriteAllLines(output, lines, new UTF8Encoding(false));
WriteKeys(cohortPrefix + "-nonpassive.keys", known, "every non-passive spell rank known at level 60, untalented");
WriteKeys(cohortPrefix + "-passive.keys", passives, "every passive spell known at level 60, untalented; excluded from cast cells");
Console.WriteLine($"[spell-animation-reference] class={className} nonpassive={known.Count} passive={passives.Count} output={output}");
return 0;

static string Join<T>(IEnumerable<T>? values) => values is null ? "" : string.Join('|', values);

string[] Stage(uint kitId)
{
    if (kitId == 0) return ["", ""];
    if (!visuals.TryGetKit(kitId, out SpellVisualKitInfo kit)) return ["", $"MISSING:DBFilesClient\\SpellVisualKit.dbc#{kitId}"];
    string models = string.Join('|', kit.Effects.Select(effect => Source(effect.ModelPath)).Distinct());
    return [kit.AnimationId?.ToString(CultureInfo.InvariantCulture) ?? "", models];
}

string Source(string rawPath)
{
    string path = rawPath.Replace('/', '\\');
    if (path.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
        path = path[..^4] + ".m2";
    var found = mpq.ReadFileWithSupplier(path);
    return found is null ? $"MISSING:{path}" : $"{found.Value.Supplier}:{path}";
}

static void WriteKeys(string path, SortedDictionary<uint, string> values, string rule)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    var rows = new List<string> { $"# selection_rule={rule}", "spell_id,name" };
    rows.AddRange(values.Select(pair => $"{pair.Key},{Csv(pair.Value)}"));
    File.WriteAllLines(path, rows, new UTF8Encoding(false));
}

static string Csv(string value) => '"' + value.Replace("\"", "\"\"") + '"';
