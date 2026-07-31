using System.Globalization;
using System.Text.Json;
using MSUIClient.Engine;

namespace MSUIClient;

public sealed record LiveRunOptions(string OutputDirectory, string? Protocol, double TimeoutSeconds);

public static partial class Program
{
    private static bool TryParseLiveRunArgs(string[] args, out LiveRunOptions? options,
        out string? configPath, out string? error)
    {
        options = null; configPath = null; error = null;
        string output = "live-runs"; string? protocol = null; double timeout = 120;
        for (int i=0;i<args.Length;i++)
        {
            string arg=args[i];
            if (arg=="--live-bootstrap") continue;
            if (arg is "--live-protocol" or "--out" or "--timeout")
            {
                if (++i>=args.Length) { error=$"missing value for {arg}"; return false; }
                if (arg=="--live-protocol") protocol=args[i];
                else if (arg=="--out") output=args[i];
                else if (!double.TryParse(args[i], NumberStyles.Float, CultureInfo.InvariantCulture, out timeout) || timeout<=0)
                { error="--timeout must be positive"; return false; }
                continue;
            }
            if (arg.StartsWith('-')) { error=$"unknown option {arg}"; return false; }
            if (configPath is not null) { error=$"unexpected argument {arg}"; return false; }
            configPath=arg;
        }
        options=new(output,protocol,timeout); return true;
    }
}

public sealed partial class GameLoop
{
    private readonly LiveRunOptions? _liveRunOptions;
    private double _liveRunElapsed;
    private bool _liveTeleportSent;
    public int LiveRunExitCode { get; private set; } = 1;

    private void AdvanceLiveRun(float dt)
    {
        if (_liveRunOptions is null) return;
        _liveRunElapsed += dt;
        if (_liveRunElapsed > _liveRunOptions.TimeoutSeconds)
        { FinishLiveBootstrap("TIMEOUT", "world did not become ready"); return; }
        if (_net is not { IsInWorld:true } || _worldLoading || _controller is null || _character is null) return;
        if (_liveTeleportSent) { FinishLiveBootstrap("READY", "world+wire+verdict ready"); return; }
        var arena=VantageStore.Load(_config.RepoRoot).Find("movement-arena");
        if (arena is null) { FinishLiveBootstrap("NO_VANTAGE", "movement-arena missing"); return; }
        string command=string.Create(CultureInfo.InvariantCulture,
            $".go xyz {arena.X:R} {arena.Y:R} {arena.Z:R} {arena.Map}");
        _liveTeleportSent=SendGmCommand(command,"live-bootstrap");
        if (!_liveTeleportSent) FinishLiveBootstrap("GM_SEND_FAILED", command);
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
