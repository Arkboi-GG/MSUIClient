using System.Globalization;

record Row(double T, double Dt, double X, double Y, double Z, double Speed, double Aim,
    string Flags, bool Grounded, string Clip, double ClipTime, double Rate, string Choice);
record Band(string Name, double CurrentMin, double CurrentMax, double LawMin, double LawMax, string Citation);

static class MoveAudit
{
    static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--check") return Check(args[1], args[2]);
        if (args.Length is < 1 or > 3) { Console.Error.WriteLine("usage: move-audit <trace.csv> [expected.csv] [verdicts.csv] | --check <baseline-dir> <expected-dir>"); return 2; }
        var metrics = Measure(args[0]);
        if (args.Length == 1) { foreach (var m in metrics) Console.WriteLine($"{m.Key},{F(m.Value)}"); return 0; }
        string output = args.Length == 3 ? args[2] : Path.Combine(Path.GetDirectoryName(args[0])!, Path.GetFileNameWithoutExtension(args[0]) + "-verdicts.csv");
        return Audit(metrics, args[1], output, currentOnly: false);
    }

    static int Check(string baseline, string expected)
    {
        int failed = 0;
        foreach (string trace in Directory.GetFiles(baseline, "*-run1.csv").OrderBy(x => x))
        {
            string scenario = Path.GetFileNameWithoutExtension(trace).Replace("-run1", "");
            string bands = Path.Combine(expected, scenario + ".csv");
            string verdict = Path.Combine(baseline, scenario + "-verdicts.csv");
            failed += Audit(Measure(trace), bands, verdict, currentOnly: true);
        }
        Console.WriteLine(failed == 0 ? "move-audit-check passed" : $"move-audit-check FAILED: {failed} current-tree row(s)");
        return failed == 0 ? 0 : 1;
    }

    static Dictionary<string,double> Measure(string path)
    {
        string[] lines = File.ReadAllLines(path); string[] h = lines[0].Split(',');
        int I(string n) => Array.IndexOf(h, n);
        var rows = lines.Skip(1).Select(line => { var c = line.Split(','); return new Row(
            D(c[I("t")]), D(c[I("dt")]), D(c[I("posX")]), D(c[I("posY")]), D(c[I("posZ")]),
            D(c[I("horizSpeed")]), D(c[I("aimYaw")]), c[I("inputFlags")], bool.Parse(c[I("grounded")]),
            c[I("clipName")], D(c[I("clipTime")]), D(c[I("playbackRate")]), c[I("lastAnimChoice")]); }).ToList();
        bool Moving(Row r) => r.Flags.Contains("fwd") || r.Flags.Contains("back") || r.Flags.Contains("strafe");
        int firstIntent = rows.FindIndex(Moving); if (firstIntent < 0) firstIntent = 0;
        int firstMoved = rows.FindIndex(firstIntent, r => Math.Abs(r.X - rows[firstIntent].X) > 1e-5 || Math.Abs(r.Y - rows[firstIntent].Y) > 1e-5);
        int firstClip = rows.FindIndex(firstIntent, r => r.Clip != "Stand");
        int release = rows.FindIndex(firstIntent, r => !Moving(r));
        double stop = release < 0 ? 0 : Dist(rows[release], rows[^1]);
        double maxSpeed = rows.Max(r => r.Speed);
        var turnRows = rows.Where(r => r.Flags.Contains("turn")).ToList();
        double turnDistance = 0; for (int i=1;i<turnRows.Count;i++) turnDistance += Math.Abs(Unwrap(turnRows[i].Aim-turnRows[i-1].Aim));
        double turnRate = turnRows.Count < 2 ? 0 : turnDistance / Math.Max(1e-9, turnRows[^1].T - turnRows[0].T);
        int takeoff = rows.FindIndex(r => !r.Grounded); int land = takeoff < 0 ? -1 : rows.FindIndex(takeoff + 1, r => r.Grounded);
        double apexHeight = takeoff < 0 ? 0 : rows.Skip(takeoff).Take((land < 0 ? rows.Count : land) - takeoff).Max(r => r.Z) - rows[Math.Max(0, takeoff - 1)].Z;
        int apex = takeoff < 0 ? -1 : rows.FindIndex(takeoff, r => Math.Abs(r.Z - rows.Max(x => x.Z)) < 1e-5);
        int stalls = 0; double stall = 0;
        foreach (var r in rows) { if (Moving(r) && (r.Clip == "Stand" || Math.Abs(r.Rate) < 1e-5)) { stall += r.Dt; if (stall > .15 && stall-r.Dt <= .15) stalls++; } else stall = 0; }
        int resets = 0; for (int i=1;i<rows.Count;i++) if (rows[i].Clip==rows[i-1].Clip && rows[i].ClipTime+1e-5 < rows[i-1].ClipTime) resets++;
        int subs = rows.Count(r => r.Choice.Contains("Substituted", StringComparison.OrdinalIgnoreCase));
        return new(StringComparer.Ordinal) {
            ["maxSpeed"] = maxSpeed, ["stopDistance"] = stop, ["turnRate"] = turnRate,
            ["startDisplacementTicks"] = firstMoved < 0 ? 999 : firstMoved-firstIntent,
            ["startClipLatencyMs"] = firstClip < 0 ? 999999 : (rows[firstClip].T-rows[firstIntent].T)*1000,
            ["jumpApexHeight"] = apexHeight,
            ["jumpApexTime"] = takeoff < 0 || apex < 0 ? 0 : rows[apex].T-rows[takeoff].T,
            ["jumpAirtime"] = takeoff < 0 || land < 0 ? 0 : rows[land].T-rows[takeoff].T,
            ["stallWindows"] = stalls, ["phaseResets"] = resets, ["substitutedEvents"] = subs
        };
    }

    static int Audit(Dictionary<string,double> metrics, string expected, string output, bool currentOnly)
    {
        var bands = File.ReadLines(expected).Where(l => l.Length > 0 && !l.StartsWith('#') && !l.StartsWith("name,"))
            .Select(l => { var c=l.Split(',',6); return new Band(c[0],D(c[1]),D(c[2]),D(c[3]),D(c[4]),c[5]); }).ToList();
        var lines = new List<string>{"name,measured,currentMin,currentMax,currentResult,vanillaMin,vanillaMax,vanillaResult,citation"}; int fail=0;
        foreach(var b in bands) { double m=metrics[b.Name]; bool cp=m>=b.CurrentMin&&m<=b.CurrentMax, lp=m>=b.LawMin&&m<=b.LawMax; if(!cp)fail++;
            string lawResult = b.LawMin == -999 && b.LawMax == 999 ? "N/A" : lp ? "PASS" : "FAIL";
            lines.Add(string.Join(',', b.Name,F(m),F(b.CurrentMin),F(b.CurrentMax),cp?"PASS":"FAIL",F(b.LawMin),F(b.LawMax),lawResult,b.Citation.Replace(',',';'))); }
        Directory.CreateDirectory(Path.GetDirectoryName(output)!); File.WriteAllLines(output, lines);
        if (!currentOnly) foreach(var l in lines) Console.WriteLine(l);
        return fail;
    }
    static double D(string s)=>double.Parse(s,CultureInfo.InvariantCulture);
    static string F(double d)=>d.ToString("0.######",CultureInfo.InvariantCulture);
    static double Dist(Row a,Row b)=>Math.Sqrt((a.X-b.X)*(a.X-b.X)+(a.Y-b.Y)*(a.Y-b.Y));
    static double Unwrap(double a){while(a>Math.PI)a-=2*Math.PI;while(a<-Math.PI)a+=2*Math.PI;return a;}
}
