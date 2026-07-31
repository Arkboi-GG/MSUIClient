using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MSUIClient;
using MSUIClient.Net;

const byte CharCreateSuccess = 0x2E;
const byte CharDeleteSuccess = 0x39;

string? configPath = null;
string prefix = "Cold";
string? label = null;
string? existingCharacter = null;
int observeSeconds = 60;
int timeoutSeconds = 180;
for (int i = 0; i < args.Length; i++)
{
    string arg = args[i];
    if (arg is "--prefix" or "--label" or "--existing-character" or
        "--observe-seconds" or "--timeout-seconds")
    {
        if (++i >= args.Length) return Fail($"missing value for {arg}");
        string value = args[i];
        if (arg == "--prefix") prefix = value;
        else if (arg == "--label") label = value;
        else if (arg == "--existing-character") existingCharacter = value;
        else if (arg == "--observe-seconds" && !int.TryParse(value, out observeSeconds))
            return Fail("--observe-seconds must be an integer");
        else if (arg == "--timeout-seconds" && !int.TryParse(value, out timeoutSeconds))
            return Fail("--timeout-seconds must be an integer");
        continue;
    }
    if (arg.StartsWith('-')) return Fail($"unknown option {arg}");
    if (configPath is not null) return Fail($"unexpected argument {arg}");
    configPath = arg;
}

if (prefix.Length is < 1 or > 6 || prefix.Any(ch => !char.IsAsciiLetter(ch)))
    return Fail("--prefix must contain 1-6 ASCII letters");
if (observeSeconds is < 1 or > 600) return Fail("--observe-seconds must be 1-600");
if (timeoutSeconds is < 30 or > 900) return Fail("--timeout-seconds must be 30-900");

ClientConfig config;
try { config = ClientConfig.Load(configPath); }
catch (Exception exception) { return Fail(exception.Message); }
if (!config.Server.Enabled) return Fail("server.enabled must be true");

bool useExistingCharacter = !string.IsNullOrWhiteSpace(existingCharacter);
string name = useExistingCharacter ? existingCharacter! : GenerateName(prefix);
string repo = config.RepoRoot;
string dumpDirectory = Path.Combine(repo, "dumps");
Directory.CreateDirectory(dumpDirectory);
string runLabel = string.IsNullOrWhiteSpace(label)
    ? DateTime.Now.ToString("yyyyMMdd-HHmmss")
    : SanitizeLabel(label);
string consolePath = Path.Combine(dumpDirectory, $"cycle-{runLabel}.log");
string errorPath = Path.Combine(dumpDirectory, $"cycle-{runLabel}.err.log");
string reportPath = Path.Combine(dumpDirectory, $"cycle-{runLabel}.json");
string tempConfigPath = Path.Combine(dumpDirectory, $"cycle-{runLabel}.config.json");
string tempSettingsPath = Path.Combine(dumpDirectory, $"cycle-{runLabel}.settings.json");
string lockPath = Path.Combine(dumpDirectory, $"cycle-{runLabel}.lock");

using FileStream runLock = OpenRunLock(lockPath, runLabel);

DateTime started = DateTime.Now;
string? loadDump = null;
byte createCode = 0xFF;
byte deleteCode = 0xFF;
int appExitCode = -1;
string outcome = "failed";
bool characterCreated = false;
Process? app = null;
Task stdout = Task.CompletedTask;
Task stderr = Task.CompletedTask;

try
{
    using (var net = await ConnectAtSelectWithRetry(config, timeoutSeconds))
    {
        if (useExistingCharacter)
        {
            Character selected = existingCharacter!.Equals("first", StringComparison.OrdinalIgnoreCase)
                ? net.Characters.FirstOrDefault(character => !character.Name.StartsWith(prefix,
                    StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("no non-test existing character is available")
                : net.Characters.FirstOrDefault(character => string.Equals(character.Name,
                    existingCharacter, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException(
                        $"existing character '{existingCharacter}' was not found");
            name = selected.Name;
            Console.WriteLine($"[cycle] existing character {name} selected (will not delete)");
        }
        else
        {
            Console.WriteLine($"[cycle] cleaning stale '{prefix}*' test characters");
            foreach (Character stale in net.Characters
                         .Where(character => character.Name.StartsWith(prefix,
                             StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                Console.WriteLine($"[cycle] delete stale {stale.Name}");
                net.DeleteCharacter(stale.Guid);
                byte code = await WaitDelete(net, timeoutSeconds);
                if (code != CharDeleteSuccess)
                    throw new InvalidOperationException(
                        $"stale delete {stale.Name} refused with 0x{code:X2}");
            }

            Console.WriteLine($"[cycle] create {name}");
            net.CreateCharacter(new CharCreateParams(name, Race: 1, Class: 1, Gender: 0,
                Skin: 0, Face: 0, HairStyle: 0, HairColor: 0, FacialHair: 0));
            createCode = await WaitCreate(net, timeoutSeconds);
            if (createCode != CharCreateSuccess)
                throw new InvalidOperationException($"create refused with 0x{createCode:X2}");
            characterCreated = true;
            if (!net.Characters.Any(character =>
                    string.Equals(character.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("create succeeded but refreshed roster lacks the character");
        }
    }

    config.Server.AutoConnect = true;
    config.Server.Character = name;
    await File.WriteAllTextAsync(tempConfigPath, JsonSerializer.Serialize(config,
        new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        }));

    string appDll = Path.Combine(repo, "MSUIClient", "bin", "Release", "net8.0",
        "MSUIClient.dll");
    if (!File.Exists(appDll))
        throw new FileNotFoundException("Release client is not built", appDll);

    HashSet<string> oldLoads = Directory.GetFiles(dumpDirectory, "load-*.json")
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    var startInfo = new ProcessStartInfo("dotnet")
    {
        WorkingDirectory = repo,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };
    startInfo.ArgumentList.Add(appDll);
    startInfo.ArgumentList.Add(tempConfigPath);
    // PLAN_17 section 8 requires stock settings. Do not inherit or overwrite
    // the developer's settings.json; a missing isolated path gives the client
    // shipped defaults and any selection save remains disposable harness state.
    startInfo.Environment["MSUI_SETTINGS_PATH"] = tempSettingsPath;
    app = Process.Start(startInfo)
        ?? throw new InvalidOperationException("could not start the Release client");
    stdout = Drain(app.StandardOutput, consolePath);
    stderr = Drain(app.StandardError, errorPath);

    Console.WriteLine($"[cycle] entered {name}; waiting for a new load dump");
    DateTime deadline = DateTime.Now.AddSeconds(timeoutSeconds);
    while (DateTime.Now < deadline)
    {
        if (app.HasExited)
            throw new InvalidOperationException($"client exited before load dump ({app.ExitCode})");
        loadDump = Directory.GetFiles(dumpDirectory, "load-*.json")
            .Where(path => !oldLoads.Contains(path))
            .OrderByDescending(File.GetLastWriteTime)
            .FirstOrDefault();
        if (loadDump is not null) break;
        await Task.Delay(250);
    }
    if (loadDump is null) throw new TimeoutException("timed out waiting for the load dump");

    Console.WriteLine($"[cycle] {Path.GetFileName(loadDump)} captured; observing {observeSeconds}s");
    DateTime observeUntil = DateTime.Now.AddSeconds(observeSeconds);
    while (DateTime.Now < observeUntil)
    {
        if (app.HasExited)
            throw new InvalidOperationException($"client exited during observation ({app.ExitCode})");
        await Task.Delay(250);
    }

    app.Kill(entireProcessTree: true);
    await app.WaitForExitAsync();
    appExitCode = app.ExitCode;
    await Task.WhenAll(stdout, stderr);

    // VMaNGOS can retain the just-closed world session for a few seconds and
    // maps the duplicate-account rejection to the same 0x04 text as bad
    // credentials. Backoff keeps cleanup deterministic without weakening its
    // exact generated-name scope.
    await Task.Delay(3000);
    using (var net = await ConnectAtSelectWithRetry(config, timeoutSeconds))
    {
        Character selected = net.Characters.FirstOrDefault(character =>
            string.Equals(character.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("selected character missing after observation");
        if (characterCreated)
        {
            Console.WriteLine($"[cycle] delete {name}");
            net.DeleteCharacter(selected.Guid);
            deleteCode = await WaitDelete(net, timeoutSeconds);
            if (deleteCode != CharDeleteSuccess)
                throw new InvalidOperationException($"cleanup delete refused with 0x{deleteCode:X2}");
            characterCreated = false;
        }
        else Console.WriteLine($"[cycle] existing character {name} still present");
    }
    outcome = "complete";
}
catch (Exception exception)
{
    Console.Error.WriteLine($"[cycle] {exception.Message}");
}
finally
{
    if (app is not null)
    {
        try
        {
            if (!app.HasExited) app.Kill(entireProcessTree: true);
            await app.WaitForExitAsync();
            appExitCode = app.ExitCode;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[cycle] client shutdown: {exception.Message}");
        }
        try { await Task.WhenAll(stdout, stderr); }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[cycle] output drain: {exception.Message}");
        }
        app.Dispose();
    }

    if (characterCreated)
    {
        try
        {
            await Task.Delay(3000);
            using var net = await ConnectAtSelectWithRetry(config, timeoutSeconds);
            Character? created = net.Characters.FirstOrDefault(character =>
                string.Equals(character.Name, name, StringComparison.OrdinalIgnoreCase));
            if (created is not null)
            {
                Console.WriteLine($"[cycle] failure cleanup delete {name}");
                net.DeleteCharacter(created.Guid);
                deleteCode = await WaitDelete(net, timeoutSeconds);
            }
            characterCreated = false;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[cycle] failure cleanup: {exception.Message}");
        }
    }

    var report = new
    {
        outcome,
        mode = useExistingCharacter ? "existing" : "new",
        name,
        prefix,
        startedLocal = started,
        finishedLocal = DateTime.Now,
        observeSeconds,
        createCode = $"0x{createCode:X2}",
        deleteCode = $"0x{deleteCode:X2}",
        appExitCode,
        loadDump = loadDump is null ? null : Path.GetFileName(loadDump),
        console = Path.GetFileName(consolePath),
        error = Path.GetFileName(errorPath),
    };
    await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report,
        new JsonSerializerOptions { WriteIndented = true }));
    try { File.Delete(tempConfigPath); } catch { }
    try { File.Delete(tempSettingsPath); } catch { }
    Console.WriteLine($"[cycle] report {reportPath}");
}

return outcome == "complete" ? 0 : 1;

static FileStream OpenRunLock(string path, string label)
{
    try
    {
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
            FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose);
    }
    catch (IOException exception)
    {
        throw new InvalidOperationException(
            $"another cold-start cycle is already using label '{label}'", exception);
    }
}

static async Task<NetworkClient> ConnectAtSelect(ClientConfig config, int timeoutSeconds)
{
    var settings = config.ToNetSettings() with { CharacterName = null };
    var net = new NetworkClient(settings);
    net.Start();
    DateTime deadline = DateTime.Now.AddSeconds(timeoutSeconds);
    while (DateTime.Now < deadline)
    {
        if (net.State == NetState.CharacterSelect) return net;
        if (net.State is NetState.Failed or NetState.Disconnected)
        {
            string status = net.Status;
            net.Dispose();
            throw new InvalidOperationException($"network stopped at {net.State}: {status}");
        }
        await Task.Delay(50);
    }
    net.Dispose();
    throw new TimeoutException("timed out waiting for character select");
}

static async Task<NetworkClient> ConnectAtSelectWithRetry(
    ClientConfig config, int timeoutSeconds, int attempts = 6)
{
    Exception? last = null;
    for (int attempt = 1; attempt <= attempts; attempt++)
    {
        try { return await ConnectAtSelect(config, timeoutSeconds); }
        catch (Exception exception)
        {
            last = exception;
            if (attempt == attempts) break;
            int delaySeconds = attempt * 2;
            Console.WriteLine($"[cycle] reconnect {attempt} failed; retrying in {delaySeconds}s");
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
        }
    }
    throw new InvalidOperationException(
        $"could not reach character select after {attempts} attempts", last);
}

static async Task<byte> WaitCreate(NetworkClient net, int timeoutSeconds)
{
    DateTime deadline = DateTime.Now.AddSeconds(timeoutSeconds);
    while (DateTime.Now < deadline)
    {
        if (net.TryTakeCreateResult(out byte code)) return code;
        await Task.Delay(25);
    }
    throw new TimeoutException("timed out waiting for SMSG_CHAR_CREATE");
}

static async Task<byte> WaitDelete(NetworkClient net, int timeoutSeconds)
{
    DateTime deadline = DateTime.Now.AddSeconds(timeoutSeconds);
    while (DateTime.Now < deadline)
    {
        if (net.TryTakeDeleteResult(out byte code)) return code;
        await Task.Delay(25);
    }
    throw new TimeoutException("timed out waiting for SMSG_CHAR_DELETE");
}

static async Task Drain(StreamReader reader, string path)
{
    await using var output = new StreamWriter(path, append: false, Encoding.UTF8);
    while (await reader.ReadLineAsync() is { } line)
    {
        await output.WriteLineAsync(line);
        await output.FlushAsync();
    }
}

static string GenerateName(string prefix)
{
    const string alphabet = "abcdefghijklmnopqrstuvwxyz";
    ulong value = (ulong)DateTime.UtcNow.Ticks;
    var suffix = new char[12 - prefix.Length];
    for (int i = 0; i < suffix.Length; i++)
    {
        suffix[i] = alphabet[(int)(value % 26)];
        value /= 26;
    }
    string normalized = char.ToUpperInvariant(prefix[0]) + prefix[1..].ToLowerInvariant();
    return normalized + new string(suffix);
}

static string SanitizeLabel(string value)
{
    string cleaned = new(value.Where(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_').ToArray());
    return cleaned.Length == 0 ? DateTime.Now.ToString("yyyyMMdd-HHmmss") : cleaned;
}

static int Fail(string message)
{
    Console.Error.WriteLine($"[cycle] {message}");
    Console.Error.WriteLine(
        "usage: MSUIColdStartCycle [config.json] [--prefix Cold] [--label s6-5] " +
        "[--existing-character NAME|first] [--observe-seconds 60] [--timeout-seconds 180]");
    return 2;
}
