using System.Globalization;
using System.Text;

if (args.Length < 4)
{
    Console.Error.WriteLine("usage: spell-matrix-scenario <expected.csv> <output.protocol> <start> <count> [standing|moving|both]");
    return 2;
}

string[] lines = File.ReadAllLines(Path.GetFullPath(args[0]));
string[] header = Csv(lines[0]);
int idColumn = Array.IndexOf(header, "spell_id");
int castColumn = Array.IndexOf(header, "cast_type");
int castTimeColumn = Array.IndexOf(header, "cast_time_ms");
int start = int.Parse(args[2], CultureInfo.InvariantCulture);
int count = int.Parse(args[3], CultureInfo.InvariantCulture);
string mode = args.Length > 4 ? args[4].ToLowerInvariant() : "both";
var spells = lines.Skip(1).Where(line => !string.IsNullOrWhiteSpace(line)).Select(Csv)
    .Select(row => (Id: uint.Parse(row[idColumn], CultureInfo.InvariantCulture), CastType: row[castColumn],
        CastTimeMs: castTimeColumn >= 0 ? int.Parse(row[castTimeColumn], CultureInfo.InvariantCulture) : 0))
    .Skip(start).Take(count).ToArray();

var text = new StringBuilder();
text.AppendLine("gm .gm on");
text.AppendLine("wait 0.25");
text.AppendLine("gm .modify mana 1000");
text.AppendLine("gm .additem 17031 10");
text.AppendLine("gm .additem 17032 10");
text.AppendLine("gm .additem 17056 10");
text.AppendLine("gm .go xyz -8970 -132.493 83.53 0");
text.AppendLine("wait 0.5");
text.AppendLine("gm .npc spawn add 6");
text.AppendLine("wait 0.8");
text.AppendLine("select entry-nearest:6");
text.AppendLine("wait 0.12");
text.AppendLine("gm .npc set movetype idle");
text.AppendLine("gm .npc set reactstate 0");
text.AppendLine("gm .npc allowmove off");
text.AppendLine("gm .npc allowattack off");
text.AppendLine("gm .modify hp 1000000 1000000");

foreach (var spell in spells)
{
    if (mode is "standing" or "both") EmitCell(text, spell.Id, "standing", moving: false, spell.CastType, spell.CastTimeMs);
    if (mode is "moving" or "both") EmitCell(text, spell.Id, "moving", moving: true, spell.CastType, spell.CastTimeMs);
}
text.AppendLine("release w");
text.AppendLine("gm .gm on");
text.AppendLine("wait 0.25");
text.AppendLine("select entry-nearest:6");
text.AppendLine("wait 0.12");
text.AppendLine("gm .npc spawn delete");
text.AppendLine("wait 0.5");
text.AppendLine("gm .gps");
text.AppendLine("waitfor GmChatResponse 3");
File.WriteAllText(Path.GetFullPath(args[1]), text.ToString(), new UTF8Encoding(false));
int cells = spells.Length * (mode == "both" ? 2 : 1);
Console.WriteLine($"[spell-matrix-scenario] wrote {spells.Length} spell ranks ({cells} {mode} cells), start={start}");
return 0;

static void EmitCell(StringBuilder text, uint id, string cell, bool moving, string castType, int castTimeMs)
{
    text.AppendLine("gm .gm on");
    text.AppendLine("wait 1.0");
    text.AppendLine("gm .cooldown clear");
    text.AppendLine("gm .modify mana 1000");
    text.AppendLine("select entry-nearest:6");
    text.AppendLine("wait 0.12");
    text.AppendLine("gm .unaura all");
    text.AppendLine("gm .modify hp 1000000 1000000");
    text.AppendLine("gm .gm off");
    text.AppendLine("wait 0.35");
    text.AppendLine("anchor selected 20");
    text.AppendLine("wait 0.35");
    if (moving) { text.AppendLine("press w"); text.AppendLine("wait 0.35"); }
    else text.AppendLine("release w");
    text.AppendLine($"animation-sequence start {id} {cell}");
    text.AppendLine($"animation-sequence sample n3-mage-{id}-{cell}-00");
    text.AppendLine($"cast {id}");
    double castSeconds = !moving && castType.Equals("CAST_TIME", StringComparison.OrdinalIgnoreCase)
        ? Math.Max(0, castTimeMs) / 1000.0 : 0;
    var sampleTimes = new List<double>();
    if (castSeconds > .2)
    {
        sampleTimes.Add(Math.Min(.25, castSeconds * .2));
        sampleTimes.Add(Math.Max(sampleTimes[^1] + .08, castSeconds * .55));
        sampleTimes.Add(Math.Max(sampleTimes[^1] + .08, castSeconds - .08));
        sampleTimes.Add(Math.Max(sampleTimes[^1] + .08, castSeconds + .08));
    }
    else sampleTimes.Add(.08);
    while (sampleTimes.Count < 24) sampleTimes.Add(sampleTimes[^1] + .12);
    double previous = 0;
    for (int frame = 1; frame <= sampleTimes.Count; frame++)
    {
        double interval = sampleTimes[frame - 1] - previous;
        previous = sampleTimes[frame - 1];
        text.AppendLine($"wait {interval.ToString("0.00", CultureInfo.InvariantCulture)}");
        text.AppendLine($"animation-sequence sample n3-mage-{id}-{cell}-{frame:00}");
    }
    text.AppendLine("animation-sequence stop");
    if (moving) { text.AppendLine("release w"); text.AppendLine("wait 0.15"); }
}

static string[] Csv(string line)
{
    var fields = new List<string>();
    var value = new StringBuilder();
    bool quoted = false;
    for (int i = 0; i < line.Length; i++)
    {
        char c = line[i];
        if (c == '"')
        {
            if (quoted && i + 1 < line.Length && line[i + 1] == '"') { value.Append('"'); i++; }
            else quoted = !quoted;
        }
        else if (c == ',' && !quoted) { fields.Add(value.ToString()); value.Clear(); }
        else value.Append(c);
    }
    fields.Add(value.ToString());
    return fields.ToArray();
}
