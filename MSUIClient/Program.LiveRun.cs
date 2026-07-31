using System.Globalization;
using System.Text.Json;
using MSUIClient.Engine;
using MSUIClient.Net;

namespace MSUIClient;

public sealed record LiveRunOptions(string OutputDirectory, string? Protocol, double TimeoutSeconds, string? Character);

public static partial class Program
{
    private static bool TryParseLiveRunArgs(string[] args, out LiveRunOptions? options,
        out string? configPath, out string? error)
    {
        options = null; configPath = null; error = null;
        string output = "live-runs"; string? protocol = null, character = null; double timeout = 120;
        for (int i=0;i<args.Length;i++)
        {
            string arg=args[i];
            if (arg=="--live-bootstrap") continue;
            if (arg is "--live-protocol" or "--out" or "--timeout" or "--character")
            {
                if (++i>=args.Length) { error=$"missing value for {arg}"; return false; }
                if (arg=="--live-protocol") protocol=args[i];
                else if (arg=="--out") output=args[i];
                else if (arg=="--character") character=args[i];
                else if (!double.TryParse(args[i], NumberStyles.Float, CultureInfo.InvariantCulture, out timeout) || timeout<=0)
                { error="--timeout must be positive"; return false; }
                continue;
            }
            if (arg.StartsWith('-')) { error=$"unknown option {arg}"; return false; }
            if (configPath is not null) { error=$"unexpected argument {arg}"; return false; }
            configPath=arg;
        }
        options=new(output,protocol,timeout,character); return true;
    }
}

public sealed partial class GameLoop
{
    private readonly LiveRunOptions? _liveRunOptions;
    private double _liveRunElapsed;
    private bool _liveTeleportSent;
    private readonly HashSet<string> _liveHeld = new(StringComparer.OrdinalIgnoreCase);
    private List<string>? _liveSteps;
    private int _liveStep;
    private double _liveWaitUntil;
    private string? _liveWaitPattern;
    private double _liveWaitTimeout;
    private readonly List<string> _liveLog = [];
    private string _liveStamp = "";
    public int LiveRunExitCode { get; private set; } = 1;

    private void AdvanceLiveRun(float dt)
    {
        if (_liveRunOptions is null) return;
        _liveRunElapsed += dt;
        if (_liveRunElapsed > _liveRunOptions.TimeoutSeconds)
        { FinishLiveBootstrap("TIMEOUT", "world did not become ready"); return; }
        if (_net is not { IsInWorld:true } || _worldLoading || _controller is null || _character is null) return;
        if (_liveTeleportSent)
        {
            if (_liveRunOptions.Protocol is null) { FinishLiveBootstrap("READY", "world+wire+verdict ready"); return; }
            AdvanceProtocol(); return;
        }
        var arena=VantageStore.Load(_config.RepoRoot).Find("movement-arena");
        if (arena is null) { FinishLiveBootstrap("NO_VANTAGE", "movement-arena missing"); return; }
        string command=string.Create(CultureInfo.InvariantCulture,
            $".go xyz {arena.X:R} {arena.Y:R} {arena.Z:R} {arena.Map}");
        _liveTeleportSent=SendGmCommand(command,"live-bootstrap");
        if (!_liveTeleportSent) FinishLiveBootstrap("GM_SEND_FAILED", command);
    }

    private void AdvanceProtocol()
    {
        if (_liveSteps is null)
        {
            string path=Path.GetFullPath(Path.IsPathRooted(_liveRunOptions!.Protocol!) ? _liveRunOptions.Protocol! :
                Path.Combine(_config.RepoRoot,_liveRunOptions.Protocol!));
            _liveSteps=File.ReadLines(path).Select(x=>x.Split('#')[0].Trim()).Where(x=>x.Length>0).ToList();
            _liveStamp=DateTime.Now.ToString("yyyyMMdd-HHmmss",CultureInfo.InvariantCulture);
            _liveLog.Add($"START protocol={path}");
        }
        double now=NowSeconds();
        if (_liveWaitUntil>now) return;
        if (_liveWaitPattern is not null)
        {
            if (VerdictLines().Any(x=>x.Contains(_liveWaitPattern,StringComparison.OrdinalIgnoreCase)))
            { Log(true,$"waitfor {_liveWaitPattern}"); _liveWaitPattern=null; _liveStep++; }
            else if (now>=_liveWaitTimeout)
            { Log(false,$"waitfor {_liveWaitPattern} timeout"); _liveWaitPattern=null; _liveStep++; }
            else return;
        }
        if (_liveStep>=_liveSteps!.Count) { FinishProtocol(); return; }
        string line=_liveSteps[_liveStep];
        try
        {
            string[] p=line.Split(' ',3,StringSplitOptions.RemoveEmptyEntries);
            switch(p[0].ToLowerInvariant())
            {
                case "gm": Log(SendGmCommand(line[3..],"protocol-runner"),line); break;
                case "wait": _liveWaitUntil=now+double.Parse(p[1],CultureInfo.InvariantCulture); Log(true,line); break;
                case "waitfor":
                    string[] w=line[8..].Split(' '); double timeout=double.Parse(w[^1],CultureInfo.InvariantCulture);
                    _liveWaitPattern=string.Join(' ',w[..^1]); _liveWaitTimeout=now+timeout; return;
                case "assert": Log(VerdictLines().Any(x=>x.Contains(line[7..],StringComparison.OrdinalIgnoreCase)),line); break;
                case "select":
                    int ordinal=int.Parse(p[1].Split(':')[^1],CultureInfo.InvariantCulture);
                    ulong guid=_entities.Units.Where(x=>x.IsCreature && x.Guid!=_net!.PlayerGuid).OrderBy(x=>x.Guid).Skip(ordinal-1).FirstOrDefault()?.Guid ?? 0;
                    if(guid!=0) CommitSelection(guid,false); Log(guid!=0,$"{line} guid=0x{guid:X16}"); break;
                case "attack": if(p[1]=="start") CommitSelection(_selectionGuid,true); else StopAttack("user-cancel"); Log(true,line); break;
                case "trace": if(p[1]=="start") { _combatTraceName=p[2]; StartCombatTrace(); } else StopCombatTrace(); Log(true,line); break;
                case "dump": _currentVantage=p[1]; ArmGameplayDump(); Log(true,line); break;
                case "press": _liveHeld.Add(NormalizeMovementKey(p[1])); Log(true,line); break;
                case "release": _liveHeld.Remove(NormalizeMovementKey(p[1])); Log(true,line); break;
                default: Log(false,$"unknown {line}"); break;
            }
        }
        catch(Exception ex) { Log(false,$"{line} error={ex.GetType().Name}:{ex.Message}"); }
        _liveStep++;
    }

    private IEnumerable<string> VerdictLines()=>_verdicts.SnapshotAll().Select(v=>$"[{v.Channel}] {v.ToLine()}");
    private void Log(bool pass,string text)
    { string line=$"{_liveStep+1},{(pass?"PASS":"FAIL")},{text}"; _liveLog.Add(line); Console.WriteLine($"[protocol] {line}"); }

    private void FinishProtocol()
    {
        StopCombatTrace(); _liveHeld.Clear();
        string dir=Path.GetFullPath(Path.IsPathRooted(_liveRunOptions!.OutputDirectory)?_liveRunOptions.OutputDirectory:Path.Combine(_config.RepoRoot,_liveRunOptions.OutputDirectory));
        Directory.CreateDirectory(dir);
        string log=Path.Combine(dir,$"runner-{_liveStamp}.csv"), verdict=Path.Combine(dir,$"verdicts-{_liveStamp}.txt");
        File.WriteAllLines(log,new[]{"step,result,detail"}.Concat(_liveLog)); File.WriteAllLines(verdict,VerdictLines());
        int failures=_liveLog.Count(x=>x.Contains(",FAIL,"));
        Console.WriteLine($"[live-run] PROTOCOL_DONE failures={failures}; log={log}; verdicts={verdict}");
        LiveRunExitCode=failures==0?0:1; _window.Close();
    }

    private void FinishLiveBootstrap(string result, string detail)
    {
        string dir=Path.GetFullPath(Path.IsPathRooted(_liveRunOptions!.OutputDirectory)
            ? _liveRunOptions.OutputDirectory : Path.Combine(_config.RepoRoot,_liveRunOptions.OutputDirectory));
        Directory.CreateDirectory(dir);
        string stamp=DateTime.Now.ToString("yyyyMMdd-HHmmss",CultureInfo.InvariantCulture);
        string path=Path.Combine(dir,$"bootstrap-{stamp}.json");
        File.WriteAllText(path,JsonSerializer.Serialize(new { result,detail,
            account=_config.Server.Account,character=_config.Server.Character,realm=_config.Server.Realm,
            elapsed=_liveRunElapsed },new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"[live-run] {result}: {detail}; artifact={path}");
        LiveRunExitCode=result=="READY"?0:1;
        _window.Close();
    }
}
