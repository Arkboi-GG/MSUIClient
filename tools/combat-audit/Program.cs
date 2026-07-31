using System.Globalization;
using System.Text;

if (args.Length is < 1 or > 2)
{
    Console.Error.WriteLine("usage: combat-audit <combattrace.csv> [output-directory]");
    return 2;
}

string trace = Path.GetFullPath(args[0]);
if (!File.Exists(trace)) { Console.Error.WriteLine($"trace not found: {trace}"); return 2; }
string outputDir = args.Length == 2 ? Path.GetFullPath(args[1]) : Path.GetDirectoryName(trace)!;
Directory.CreateDirectory(outputDir);
string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
string output = Path.Combine(outputDir,
    $"combat-verdicts-{Path.GetFileNameWithoutExtension(trace)}-{stamp}.csv");

List<string[]> csv = File.ReadLines(trace).Select(ParseCsv).ToList();
if (csv.Count == 0) { Console.Error.WriteLine("empty trace"); return 2; }
string[] header = csv[0];
int Col(string name) => Array.FindIndex(header, x => x.Equals(name, StringComparison.Ordinal));
int eventCol = Col("event"), tCol = Col("t"), causeCol = Col("cause"), targetCol = Col("targetGuid");
int weaponCol = Col("weaponSpeedMs"), rangeCol = Col("rangeEligibility"), arcCol = Col("arcEligibility");
int choiceCol = Col("animChoice"), clipBCol = Col("clipB"), clipCol = Col("clipName");
if (eventCol < 0 || tCol < 0 || causeCol < 0 || targetCol < 0)
{ Console.Error.WriteLine("trace lacks required event/t/cause/targetGuid columns"); return 2; }
var rows = csv.Skip(1).Select(c => new Row(
    Text(c,eventCol), Number(c,tCol), Text(c,causeCol), Text(c,targetCol),
    NumberOrNull(c,weaponCol), Text(c,rangeCol), Text(c,arcCol), Text(c,choiceCol),
    Text(c,clipBCol), Text(c,clipCol))).ToList();

var results = new List<Result>();
bool on = false; int starts = 0, stops = 0, sends = 0, stopSends = 0, illegal = 0, swingOff = 0;
var swingTimes = new List<double>();
foreach (Row row in rows)
{
    switch (row.Event)
    {
        case "IntentOn":
            if (on) { illegal++; Add("legalTransitions", "FAIL", row, "IntentOn while On"); }
            else { on = true; starts++; }
            break;
        case "IntentOff":
            if (!on) { illegal++; Add("legalTransitions", "FAIL", row, "IntentOff while Off"); }
            else { on = false; stops++; }
            break;
        case "AttackSwingSend": sends++; break;
        case "AttackStopSend": stopSends++; break;
        case "SwingReceive":
            if (!on) { swingOff++; Add("swingInsideIntent", "FAIL", row, "SwingReceive while Off"); }
            swingTimes.Add(row.T);
            break;
    }
}
if (illegal == 0) results.Add(new("legalTransitions", "PASS", "Off --IntentOn--> On; On --IntentOff--> Off; no unknown edge"));
if (swingOff == 0) results.Add(new("swingInsideIntent", "PASS", "every SMSG_ATTACKERSTATEUPDATE was inside an intent-on window"));
results.Add(new("oneSwingSendPerStart", sends == starts ? "PASS" : "FAIL", $"intentStarts={starts}; attackSwingSends={sends}"));
int localCancels = rows.Count(r => r.Event == "IntentOff" && r.Cause is "user-cancel" or "target-switch");
results.Add(new("oneStopSendPerCancel", stopSends == localCancels ? "PASS" : "FAIL",
    $"localCancels={localCancels}; attackStopSends={stopSends}"));

List<double> speeds = rows.Where(r => r.WeaponSpeedMs is > 0).Select(r => r.WeaponSpeedMs!.Value).ToList();
bool hasEligibility = rows.Any(r => r.Range is "true" or "false" || r.Arc is "true" or "false");
if (speeds.Count == 0 || !hasEligibility || swingTimes.Count < 2)
    results.Add(new("cadenceVsWeaponSpeed", "NO_DATA",
        $"weaponSpeedSamples={speeds.Count}; eligibilitySamples={(hasEligibility ? 1 : 0)}; swingSamples={swingTimes.Count}"));
else
{
    double expected = speeds.Average();
    double worst = swingTimes.Zip(swingTimes.Skip(1), (a,b) => Math.Abs((b-a)*1000-expected)).Max();
    double tickMs = rows.Zip(rows.Skip(1),(a,b)=>b.T-a.T).Where(x=>x>0).DefaultIfEmpty(1.0/60).Min()*1000;
    results.Add(new("cadenceVsWeaponSpeed", worst <= tickMs ? "PASS" : "FAIL",
        $"weaponSpeedMs={expected:F3}; worstDeltaMs={worst:F3}; tickMs={tickMs:F3}"));
}

int swingRows = rows.Count(r => r.Event == "SwingReceive");
bool sawAttackChoice = rows.Any(r => r.Event == "AnimChoice");
bool sawReturn = rows.Any(r => r.Event == "Tick" && r.ClipB.Length == 0 && r.Choice.Length > 0);
results.Add(swingRows == 0 || !sawAttackChoice
    ? new("oneShotReturn", "NO_DATA", $"swingEvents={swingRows}; attackAnimChoices={(sawAttackChoice?1:0)}")
    : new("oneShotReturn", sawReturn ? "PASS" : "FAIL",
        $"attackAnimChoices=1; movementBaseReturn={(sawReturn?1:0)}"));

var lines = new List<string> { "check,result,detail" };
lines.AddRange(results.Select(r => $"{Csv(r.Check)},{Csv(r.Status)},{Csv(r.Detail)}"));
File.WriteAllLines(output, lines, new UTF8Encoding(false));
foreach (string line in lines) Console.WriteLine(line);
Console.WriteLine($"[combat-audit] wrote {output}");
return results.Any(r => r.Status == "FAIL") ? 1 : 0;

void Add(string check, string status, Row row, string detail) =>
    results.Add(new(check, status, $"t={row.T:F6}; event={row.Event}; target={row.Target}; {detail}"));
static string Text(string[] row, int i) => i >= 0 && i < row.Length ? row[i] : "";
static double Number(string[] row, int i) => double.Parse(Text(row,i), CultureInfo.InvariantCulture);
static double? NumberOrNull(string[] row, int i) => double.TryParse(Text(row,i), NumberStyles.Float,
    CultureInfo.InvariantCulture, out double value) ? value : null;
static string Csv(string value) => value.IndexOfAny([',','"','\r','\n']) >= 0
    ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
static string[] ParseCsv(string line)
{
    var fields = new List<string>(); var field = new StringBuilder(); bool quoted = false;
    for (int i=0;i<line.Length;i++)
    {
        char ch=line[i];
        if (ch=='"') { if (quoted && i+1<line.Length && line[i+1]=='"') { field.Append('"'); i++; } else quoted=!quoted; }
        else if (ch==',' && !quoted) { fields.Add(field.ToString()); field.Clear(); }
        else field.Append(ch);
    }
    fields.Add(field.ToString()); return fields.ToArray();
}

record Row(string Event, double T, string Cause, string Target, double? WeaponSpeedMs,
    string Range, string Arc, string Choice, string ClipB, string Clip);
record Result(string Check, string Status, string Detail);
