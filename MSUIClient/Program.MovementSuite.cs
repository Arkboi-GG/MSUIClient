using System.Globalization;
using MSUIClient.Engine;

namespace MSUIClient;

public sealed record MovementSuiteOptions(string ScriptsDirectory, string OutputDirectory, int Repeat, string? Only);

public static partial class Program
{
    private static bool TryParseMovementSuiteArgs(string[] args, out MovementSuiteOptions? options,
        out string? configPath, out string? error)
    {
        options = null; configPath = null; error = null;
        string scripts = "movement-scripts", output = "movement-scenarios/runs";
        string? only = null; int repeat = 1;
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg.Equals("--movement-suite", StringComparison.OrdinalIgnoreCase)) continue;
            if (arg is "--scripts" or "--out" or "--repeat" or "--only")
            {
                if (++i >= args.Length) { error = $"missing value for {arg}"; return false; }
                string value = args[i];
                if (arg == "--scripts") scripts = value;
                else if (arg == "--out") output = value;
                else if (arg == "--only") only = value;
                else if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out repeat) || repeat < 1)
                { error = "--repeat must be a positive integer"; return false; }
                continue;
            }
            if (arg.StartsWith('-')) { error = $"unknown option {arg}"; return false; }
            if (configPath is not null) { error = $"unexpected argument {arg}"; return false; }
            configPath = arg;
        }
        options = new(scripts, output, repeat, only);
        return true;
    }

    private static void PrintMovementSuiteUsage() => Console.Error.WriteLine(
        "usage: MSUIClient [config.json] --movement-suite [--scripts <dir>] [--out <dir>] [--repeat <n>] [--only <name>]");
}

public sealed partial class GameLoop
{
    private sealed record ScriptEdge(double Time, string Action, string Key);
    private sealed record MovementScript(string Name, float FixedDt, double Duration, List<ScriptEdge> Edges);

    private readonly MovementSuiteOptions? _movementSuiteOptions;
    private List<MovementScript>? _movementScripts;
    private MovementScript? _movementScript;
    private readonly HashSet<string> _movementHeld = new(StringComparer.OrdinalIgnoreCase);
    private int _movementScriptIndex, _movementRepeatIndex, _movementEdgeIndex;
    private double _movementScriptTime;
    private string? _movementFirstTrace;
    private bool _movementSuiteFinished;
    public int MovementSuiteExitCode { get; private set; } = 1;

    private bool EnsureMovementSuiteStarted()
    {
        if (_movementScript is not null) return true;
        try
        {
            string root = _config.RepoRoot;
            string scriptsDir = Path.GetFullPath(Path.IsPathRooted(_movementSuiteOptions!.ScriptsDirectory)
                ? _movementSuiteOptions.ScriptsDirectory : Path.Combine(root, _movementSuiteOptions.ScriptsDirectory));
            _movementScripts = Directory.GetFiles(scriptsDir, "*.txt", SearchOption.TopDirectoryOnly)
                .OrderBy(Path.GetFileName, StringComparer.Ordinal)
                .Select(ParseMovementScript)
                .Where(s => _movementSuiteOptions.Only is null || s.Name.Equals(_movementSuiteOptions.Only, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (_movementScripts.Count == 0) throw new InvalidOperationException("no movement scripts selected");
            StartMovementScript();
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[movement-suite] initialization failed: {ex.Message}");
            _movementSuiteFinished = true;
            return false;
        }
    }

    private MovementScript ParseMovementScript(string path)
    {
        float dt = 1f / 60f; double duration = 0; var edges = new List<ScriptEdge>();
        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.Split('#')[0].Trim(); if (line.Length == 0) continue;
            string[] p = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (p[0].Equals("fixed-dt", StringComparison.OrdinalIgnoreCase))
            { dt = float.Parse(p[1], CultureInfo.InvariantCulture) / 1000f; continue; }
            if (p.Length != 3) throw new FormatException($"{Path.GetFileName(path)}: expected '<seconds> press|release <key>'");
            double t = double.Parse(p[0], CultureInfo.InvariantCulture);
            if (p[1] is not ("press" or "release")) throw new FormatException($"{Path.GetFileName(path)}: bad action {p[1]}");
            edges.Add(new(t, p[1], NormalizeMovementKey(p[2]))); duration = Math.Max(duration, t);
        }
        return new(Path.GetFileNameWithoutExtension(path), dt, duration, edges.OrderBy(e => e.Time).ToList());
    }

    private static string NormalizeMovementKey(string key) => key.ToUpperInvariant() switch
    {
        "W" => "W", "S" => "S", "Q" or "STRAFE-LEFT" => "STRAFE-LEFT",
        "E" or "STRAFE-RIGHT" => "STRAFE-RIGHT", "A" or "TURN-LEFT" => "TURN-LEFT",
        "D" or "TURN-RIGHT" => "TURN-RIGHT", "SPACE" => "SPACE", "SHIFT" => "SHIFT",
        _ => throw new FormatException($"unknown movement key {key}")
    };

    private void StartMovementScript()
    {
        _movementScript = _movementScripts![_movementScriptIndex];
        _movementHeld.Clear(); _movementEdgeIndex = 0; _movementScriptTime = 0;
        var arena = VantageStore.Load(_config.RepoRoot).Find("movement-arena")
            ?? throw new InvalidOperationException("movement-arena is missing from vantages.json");
        ApplyVantage(arena);
        string outDir = Path.GetFullPath(Path.IsPathRooted(_movementSuiteOptions!.OutputDirectory)
            ? _movementSuiteOptions.OutputDirectory : Path.Combine(_config.RepoRoot, _movementSuiteOptions.OutputDirectory));
        Directory.CreateDirectory(outDir);
        StartMovementTrace(Path.Combine(outDir, $"{_movementScript.Name}-run{_movementRepeatIndex + 1}.csv"), exactPath: true);
        Console.WriteLine($"[movement-suite] {_movementScript.Name} run {_movementRepeatIndex + 1}/{_movementSuiteOptions.Repeat}");
    }

    private void OverrideMovementInput(ref float forward, ref float strafe, ref float turn, ref bool shift, ref bool jump)
    {
        if (_movementScript is null) return;
        while (_movementEdgeIndex < _movementScript.Edges.Count && _movementScript.Edges[_movementEdgeIndex].Time <= _movementScriptTime + 1e-7)
        {
            ScriptEdge e = _movementScript.Edges[_movementEdgeIndex++];
            if (e.Action == "press") _movementHeld.Add(e.Key); else _movementHeld.Remove(e.Key);
        }
        forward = (_movementHeld.Contains("W") ? 1 : 0) - (_movementHeld.Contains("S") ? 1 : 0);
        strafe = (_movementHeld.Contains("STRAFE-RIGHT") ? 1 : 0) - (_movementHeld.Contains("STRAFE-LEFT") ? 1 : 0);
        turn = (_movementHeld.Contains("TURN-LEFT") ? 1 : 0) - (_movementHeld.Contains("TURN-RIGHT") ? 1 : 0);
        shift = _movementHeld.Contains("SHIFT"); jump = _movementHeld.Contains("SPACE");
    }

    private void AdvanceMovementSuiteAfterSample()
    {
        if (_movementScript is null) return;
        _movementScriptTime += _movementScript.FixedDt;
        if (_movementScriptTime <= _movementScript.Duration + _movementScript.FixedDt * 0.5) return;
        StopMovementTrace();
        string completed = _movementTracePath;
        if (_movementRepeatIndex == 0) _movementFirstTrace = completed;
        else if (!KinematicsEqual(_movementFirstTrace!, completed, out string mismatch))
        {
            Console.Error.WriteLine($"[movement-suite] NONDETERMINISM {_movementScript.Name}: {mismatch}");
            MovementSuiteExitCode = 1; _movementSuiteFinished = true; _movementScript = null; return;
        }
        _movementRepeatIndex++;
        if (_movementRepeatIndex < _movementSuiteOptions!.Repeat) { _movementScript = null; StartMovementScript(); return; }
        _movementRepeatIndex = 0; _movementFirstTrace = null; _movementScriptIndex++;
        if (_movementScriptIndex < _movementScripts!.Count) { _movementScript = null; StartMovementScript(); return; }
        MovementSuiteExitCode = 0; _movementSuiteFinished = true; _movementScript = null;
        Console.WriteLine("[movement-suite] PASS: all selected scripts completed; repeated kinematic columns identical");
    }

    private static bool KinematicsEqual(string first, string second, out string mismatch)
    {
        string[] a = File.ReadAllLines(first), b = File.ReadAllLines(second);
        if (a.Length != b.Length) { mismatch = $"row count {a.Length} != {b.Length}"; return false; }
        int[] columns = [2,3,4,5,6,7,8,9,10,12,13,14,15];
        for (int row = 1; row < a.Length; row++)
        {
            string[] ac = a[row].Split(','), bc = b[row].Split(',');
            foreach (int col in columns) if (ac[col] != bc[col])
            { mismatch = $"row {row + 1}, column {a[0].Split(',')[col]}: {ac[col]} != {bc[col]}"; return false; }
        }
        mismatch = "none"; return true;
    }
}
