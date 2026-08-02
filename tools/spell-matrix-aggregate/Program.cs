using Microsoft.VisualBasic.FileIO;
using System.Text;

if (args.Length < 3)
{
    Console.Error.WriteLine("usage: spell-matrix-aggregate <expected.csv> <output.csv> <evidence-dir> [...]");
    return 2;
}

Dictionary<string, Dictionary<string, string>> expected = Read(args[0])
    .ToDictionary(row => row["spell_id"], StringComparer.Ordinal);
var cells = new Dictionary<(string Spell, string Cell), Dictionary<string, string>>();
foreach (string rawDirectory in args.Skip(2))
{
    string directory = Path.GetFullPath(rawDirectory);
    string sequence = Directory.GetFiles(directory, "spell-animation-sequence-*.csv").Single();
    List<Dictionary<string, string>> sweep = Read(Directory.GetFiles(directory, "spell-sweep-*.csv").Single());
    List<Dictionary<string, string>> castbar = Read(Directory.GetFiles(directory, "cast-bar-*.csv").Single());
    List<Dictionary<string, string>> auras = Read(Directory.GetFiles(directory, "spell-aura-*.csv").Single());
    List<Dictionary<string, string>> channels = Read(Directory.GetFiles(directory, "spell-channel-*.csv").Single());
    List<Dictionary<string, string>> errors = Read(Directory.GetFiles(directory, "spell-error-*.csv").Single());
    foreach (Dictionary<string, string> row in Read(sequence).Where(row => row["row_kind"] == "CELL"))
    {
        string spell = row["spell_id"];
        Dictionary<string, string>[] attempts = sweep.Where(item => item["spell_id"] == spell &&
            !item["result_enum"].StartsWith("ROSTER", StringComparison.Ordinal)).ToArray();
        Dictionary<string, string>? first = attempts.FirstOrDefault();
        bool go = attempts.Any(item => item["result_enum"] == "SMSG_SPELL_GO");
        string terminal = go ? "SMSG_SPELL_GO" : attempts.LastOrDefault()?["result_enum"] ?? "NO_CAST_ROW";
        string castType = expected[spell]["cast_type"];
        bool moving = row["cell"].Equals("MOVING", StringComparison.OrdinalIgnoreCase);
        bool clientGap = attempts.Any(item => item["result_enum"] == "LOCAL_UnsupportedTargetShape");
        row["cast_result"] = terminal;
        row["movement_verdict"] = clientGap ? "CLIENT-GAP-UNSUPPORTED-TARGET-SHAPE" :
            !moving ? (go ? "STANDING-CAST" : "STANDING-REFUSED-FINDING") :
            castType == "INSTANT" ? (go ? "MOVING-INSTANT-CAST" : "MOVING-INSTANT-REFUSED-FINDING") :
            go ? "MOVING-CASTTIME-COMPLETED-FINDING" : "MOVING-CASTTIME-REFUSED";
        row["gcd_ready"] = first?.GetValueOrDefault("gcd_ready", "") ?? "";
        row["cooldown_ready"] = first?.GetValueOrDefault("cooldown_ready", "") ?? "";
        row["resource_type"] = first?.GetValueOrDefault("resource_type", "") ?? "";
        row["resource_before"] = first?.GetValueOrDefault("resource_before", "") ?? "";
        row["resource_cost"] = first?.GetValueOrDefault("resource_cost", "") ?? "";
        double castAt = first is null ? double.MaxValue : double.Parse(first["time"], System.Globalization.CultureInfo.InvariantCulture);
        Dictionary<string, string>[] auraDelta = auras.Where(item => item["spell_id"] == spell &&
            double.Parse(item["time"], System.Globalization.CultureInfo.InvariantCulture) >= castAt).ToArray();
        Dictionary<string, string>[] channelDelta = channels.Where(item => item["spell_id"] == spell &&
            double.Parse(item["time"], System.Globalization.CultureInfo.InvariantCulture) >= castAt).ToArray();
        Dictionary<string, string>[] errorDelta = errors.Where(item => item["spell_id"] == spell &&
            double.Parse(item["time"], System.Globalization.CultureInfo.InvariantCulture) >= castAt).ToArray();
        row["castbar_events"] = string.Join('|', castbar.Where(item => item["spell_id"] == spell)
            .Select(item => item["event"] + ":" + item["classification"]));
        row["aura_events"] = string.Join('|', auraDelta.Select(item => item["event"]));
        row["channel_events"] = string.Join('|', channelDelta.Select(item => item["event"]));
        row["error_events"] = string.Join('|', errorDelta.Select(item => item["reason"]));
        bool health = row.GetValueOrDefault("health_changed") == "True" || row.GetValueOrDefault("target_health_changed") == "True";
        bool units = row.GetValueOrDefault("unit_count_changed") == "True";
        bool aura = row.GetValueOrDefault("aura_changed") == "True" || auraDelta.Length > 0;
        bool inventory = row.GetValueOrDefault("inventory_changed") == "True";
        bool position = row.GetValueOrDefault("position_changed") == "True";
        bool power = row.GetValueOrDefault("power_changed") == "True";
        HashSet<int> effects = ParseInts(expected[spell].GetValueOrDefault("effect_ids", ""));
        var expectedSignals = new List<string>();
        if (effects.Overlaps([2, 9, 17, 31, 58, 121])) expectedSignals.Add("HEALTH");
        if (effects.Overlaps([10, 67, 136])) expectedSignals.Add("HEALTH");
        if (effects.Overlaps([6, 27, 35, 65, 119, 128, 129, 143])) expectedSignals.Add("AURA");
        if (effects.Contains(24)) expectedSignals.Add("INVENTORY");
        if (effects.Overlaps([28, 41, 42, 50, 56, 73, 74, 76, 87, 97, 104, 105, 106, 107])) expectedSignals.Add("UNIT_COUNT");
        if (effects.Overlaps([5, 43])) expectedSignals.Add("POSITION");
        if (effects.Overlaps([8, 30, 62])) expectedSignals.Add("POWER");
        expectedSignals = expectedSignals.Distinct().ToList();
        bool signalPresent = expectedSignals.Any(signal => signal switch {
            "HEALTH" => health, "AURA" => aura, "INVENTORY" => inventory, "UNIT_COUNT" => units,
            "POSITION" => position, "POWER" => power, _ => false });
        bool anyDelta = health || units || aura || inventory || position || power;
        row["mechanical_expected_signals"] = string.Join('|', expectedSignals);
        row["mechanical_observed_signals"] = string.Join('|', new[] {
            health ? "HEALTH" : "", aura ? "AURA" : "", inventory ? "INVENTORY" : "",
            units ? "UNIT_COUNT" : "", position ? "POSITION" : "", power ? "POWER" : ""
        }.Where(value => value.Length > 0));
        row["mechanical_coverage"] = row["coverage"] == "MEASURED" ? "MEASURED" : "NOT-INSTRUMENTED";
        row["mechanical_verdict"] = row["mechanical_coverage"] != "MEASURED" ? "MECHANICAL-NOT-INSTRUMENTED" :
            clientGap ? "MECHANICAL-CLIENT-GAP" : !go ? "MECHANICAL-CAST-REFUSED" :
            expectedSignals.Count == 0 ? (anyDelta ? "MECHANICAL-OBSERVED-UNCLASSIFIED" : "MECHANICAL-NO-STATE-ORACLE") :
            signalPresent ? "MECHANICAL-PRESENT" : "MECHANICAL-EXPECTED-DELTA-ABSENT";
        row["cell_verdict"] = row.GetValueOrDefault("caster_animation_verdict") == "ANIM-EXACT" &&
            row.GetValueOrDefault("spell_visual_verdict") == "SPELL-VISUAL-PRESENT" ? "PASS" : "FINDING";
        row["evidence_directory"] = directory;
        row["sequence_csv"] = sequence;
        cells[(row["spell_id"], row["cell"])] = row;
    }
}

string[] actualColumns = ["coverage", "sample_index", "actual_stage", "expected_animation_id",
    "requested_animation_id", "played_animation_id", "resolution", "renderer_state", "base_animation",
    "previous_base_animation", "action_animation", "hold_animation", "blend_weight", "moving",
    "player_health", "player_power", "selection_health", "player_x", "player_y", "player_z", "unit_count",
    "player_auras", "selection_auras", "inventory_fingerprint", "health_changed", "target_health_changed",
    "position_changed", "unit_count_changed", "aura_changed", "inventory_changed", "power_changed",
    "precast_visual", "cast_visual", "missile_visual", "impact_visual", "visual_instances",
    "spell_visual_verdict", "active_models", "asset_sources", "caster_animation_verdict", "blend_verdict", "gm_mode", "source",
    "cast_result", "movement_verdict", "gcd_ready", "cooldown_ready", "resource_type", "resource_before",
    "resource_cost", "castbar_events", "aura_events", "channel_events", "error_events", "mechanical_coverage",
    "mechanical_expected_signals", "mechanical_observed_signals", "mechanical_verdict", "cell_verdict",
    "evidence_directory", "sequence_csv"];
string[] columns = ["class", "spell_id", "name", "rank", "school", "cast_type", "cell", ..actualColumns];
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
using var writer = new StreamWriter(Path.GetFullPath(args[1]), false, new UTF8Encoding(false));
writer.WriteLine(string.Join(',', columns));
foreach (var pair in cells.OrderBy(pair => uint.Parse(pair.Key.Spell)).ThenBy(pair => pair.Key.Cell))
{
    Dictionary<string, string> reference = expected[pair.Key.Spell];
    Dictionary<string, string> actual = pair.Value;
    if (reference["coverage"] == "NOT-PRESENT")
    {
        actual["coverage"] = "NOT-PRESENT";
        actual["source"] = actual.GetValueOrDefault("source", "") + "|DBC_REFERENCE_VISUAL_NOT_PRESENT";
    }
    string[] values = [reference["class"], pair.Key.Spell, reference["name"], reference["rank"],
        reference["school"], reference["cast_type"], pair.Key.Cell,
        ..actualColumns.Select(column => actual.GetValueOrDefault(column, ""))];
    writer.WriteLine(string.Join(',', values.Select(Quote)));
}
writer.Flush();

int applicable = expected.Count * 2;
int measured = cells.Values.Count(row => row["coverage"] == "MEASURED");
int notPresent = cells.Values.Count(row => row["coverage"] == "NOT-PRESENT");
Console.WriteLine($"[spell-matrix-aggregate] measured={measured}/{applicable}; not-present={notPresent}; rows={cells.Count}");
foreach (var group in cells.Values.GroupBy(row => row.GetValueOrDefault("caster_animation_verdict", "MISSING")).OrderBy(group => group.Key))
    Console.WriteLine($"[spell-matrix-aggregate] CASTER-{group.Key}={group.Count()}");
foreach (var group in cells.Values.GroupBy(row => row.GetValueOrDefault("spell_visual_verdict", "MISSING")).OrderBy(group => group.Key))
    Console.WriteLine($"[spell-matrix-aggregate] {group.Key}={group.Count()}");
Console.WriteLine($"[spell-matrix-aggregate] VISUAL-RESOLVED-NOT-DRAWN=" +
    cells.Values.Count(row => row.GetValueOrDefault("spell_visual_verdict") == "VISUAL-RESOLVED-NOT-DRAWN"));
Console.WriteLine($"[spell-matrix-aggregate] CLIENT-GAP-UNSUPPORTED-TARGET-SHAPE=" +
    cells.Values.Count(row => row.GetValueOrDefault("movement_verdict") == "CLIENT-GAP-UNSUPPORTED-TARGET-SHAPE"));
Console.WriteLine($"[spell-matrix-aggregate] PASS=" + cells.Values.Count(row => row.GetValueOrDefault("cell_verdict") == "PASS"));
return cells.Count == applicable ? 0 : 1;

static HashSet<int> ParseInts(string value) => value.Split('|', StringSplitOptions.RemoveEmptyEntries)
    .Select(text => int.TryParse(text, out int parsed) ? parsed : 0).Where(parsed => parsed != 0).ToHashSet();

static List<Dictionary<string, string>> Read(string path)
{
    var result = new List<Dictionary<string, string>>();
    using var parser = new TextFieldParser(Path.GetFullPath(path)) { TextFieldType = FieldType.Delimited };
    parser.SetDelimiters(",");
    string[] header = parser.ReadFields() ?? throw new InvalidDataException(path);
    while (!parser.EndOfData)
    {
        string[] fields = parser.ReadFields() ?? [];
        if (fields.Length != header.Length) continue;
        result.Add(header.Zip(fields).ToDictionary(pair => pair.First, pair => pair.Second,
            StringComparer.OrdinalIgnoreCase));
    }
    return result;
}

static string Quote(string value) => value.IndexOfAny([',', '"', '\r', '\n']) < 0 ? value :
    $"\"{value.Replace("\"", "\"\"")}\"";
