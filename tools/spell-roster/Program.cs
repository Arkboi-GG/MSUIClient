using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MSUIClient;
using MSUIClient.Net;

if (args.Length is < 1 or > 3)
{
    Console.Error.WriteLine("usage: spell-roster <client-config.json> [output.csv] [--inspect-only|--reconcile]");
    return 2;
}

ClientConfig config;
try { config = ClientConfig.Load(args[0]); }
catch (Exception exception) { Console.Error.WriteLine(exception.Message); return 2; }
if (!config.Server.Enabled ||
    !string.Equals(config.Server.Character, "TEST", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("spell roster provisioning requires the config bound to dedicated character TEST");
    return 3;
}

string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
string output = Path.GetFullPath(args.Length >= 2 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1] :
    Path.Combine(config.RepoRoot, "live-runs", $"spell-roster-{stamp}.csv"));
if (args.Length >= 2 && args[1].StartsWith("--", StringComparison.Ordinal))
    output = Path.Combine(config.RepoRoot, "live-runs", $"spell-roster-{stamp}.csv");
bool inspectOnly = args.Any(x => x.Equals("--inspect-only", StringComparison.OrdinalIgnoreCase));
bool reconcile = args.Any(x => x.Equals("--reconcile", StringComparison.OrdinalIgnoreCase));
Directory.CreateDirectory(Path.GetDirectoryName(output)!);

RosterSpec[] required =
[
    new("Nbwarhuman", 1, "Warrior", 1, "Human"),
    new("Nbpalhuman", 2, "Paladin", 1, "Human"),
    new("Nbhundwarf", 3, "Hunter", 3, "Dwarf"),
    new("Nbroghuman", 4, "Rogue", 1, "Human"),
    new("Nbprihuman", 5, "Priest", 1, "Human"),
    new("Nbshaorc", 7, "Shaman", 2, "Orc"),
    new("Nbmaghuman", 8, "Mage", 1, "Human"),
    new("Nbwlkgnome", 9, "Warlock", 7, "Gnome"),
    new("Nbdrunelf", 11, "Druid", 4, "NightElf"),
];

var rows = new List<string>
{
    "run_local,planned_name,actual_name,class_id,class_name,planned_race_id,planned_race_name,actual_race_id,method,result,guid,level"
};
int failures = 0;
using var net = await ConnectAtSelectWithRetry(config, TimeSpan.FromSeconds(30));
if (inspectOnly)
{
    rows.Clear();
    rows.Add("run_local,actual_name,class_id,race_id,guid,level");
    foreach (Character c in net.Characters.OrderBy(c => c.Class).ThenBy(c => c.Name))
        rows.Add(string.Join(',', Csv(DateTime.Now.ToString("s", CultureInfo.InvariantCulture)),
            Csv(c.Name), c.Class, c.Race, $"0x{c.Guid:X16}", c.Level));
    File.WriteAllLines(output, rows, new UTF8Encoding(false));
    string inspectHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(output))).ToLowerInvariant();
    File.WriteAllText(Path.ChangeExtension(output, ".sha256"),
        $"{inspectHash}  {Path.GetFileName(output)}\n", new UTF8Encoding(false));
    Console.WriteLine($"[spell-roster] inspected={net.Characters.Count} output={output}");
    return 0;
}
if (reconcile)
{
    var keep = new HashSet<ulong>();
    Character? test = net.Characters.FirstOrDefault(c => c.Name.Equals("TEST", StringComparison.OrdinalIgnoreCase));
    if (test is not null) keep.Add(test.Guid);
    foreach (RosterSpec spec in required)
    {
        Character? representative = net.Characters.Where(c => c.Class == spec.Class)
            .OrderByDescending(c => c.Name.Equals(spec.Name, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(c => c.Level).FirstOrDefault();
        if (representative is not null) keep.Add(representative.Guid);
    }
    foreach (Character stale in net.Characters.Where(c => !keep.Contains(c.Guid)).ToArray())
    {
        net.DeleteCharacter(stale.Guid);
        byte code = await WaitDelete(net, TimeSpan.FromSeconds(30));
        string deleteResult = code == 0x39 ? "DELETED" : $"REFUSED_0x{code:X2}";
        if (code != 0x39) failures++;
        rows.Add(string.Join(',', Csv(DateTime.Now.ToString("s", CultureInfo.InvariantCulture)),
            "\"\"", Csv(stale.Name), stale.Class, "\"\"", "", "\"\"", stale.Race,
            "CMSG_CHAR_DELETE", deleteResult, $"0x{stale.Guid:X16}", stale.Level));
        Console.WriteLine($"[spell-roster] remove stale {stale.Name} {deleteResult}");
    }
}
foreach (RosterSpec spec in required)
{
    Character? character = net.Characters.FirstOrDefault(c =>
        c.Name.Equals(spec.Name, StringComparison.OrdinalIgnoreCase));
    string method = "existing";
    string result = "PRESENT";
    if (character is null)
    {
        character = net.Characters.FirstOrDefault(c => c.Class == spec.Class);
        if (character is not null)
        {
            method = "existing-equivalent";
            result = "PRESENT_EQUIVALENT";
        }
        else
        {
            method = "CMSG_CHAR_CREATE";
            net.CreateCharacter(new CharCreateParams(spec.Name, spec.Race, spec.Class, 0,
                Skin: 0, Face: 0, HairStyle: 0, HairColor: 0, FacialHair: 0));
            byte code = await WaitCreate(net, TimeSpan.FromSeconds(30));
            result = code == 0x2E ? "CREATED" : $"REFUSED_0x{code:X2}";
            character = net.Characters.FirstOrDefault(c =>
                c.Name.Equals(spec.Name, StringComparison.OrdinalIgnoreCase));
            if (code != 0x2E || character is null) failures++;
        }
    }
    rows.Add(string.Join(',', Csv(DateTime.Now.ToString("s", CultureInfo.InvariantCulture)),
        Csv(spec.Name), Csv(character?.Name ?? ""), spec.Class, Csv(spec.ClassName), spec.Race, Csv(spec.RaceName),
        character?.Race.ToString(CultureInfo.InvariantCulture) ?? "",
        method, result, character is null ? "" : $"0x{character.Guid:X16}",
        character?.Level.ToString(CultureInfo.InvariantCulture) ?? ""));
    Console.WriteLine($"[spell-roster] {spec.Name} {result}");
}

File.WriteAllLines(output, rows, new UTF8Encoding(false));
string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(output))).ToLowerInvariant();
File.WriteAllText(Path.ChangeExtension(output, ".sha256"),
    $"{hash}  {Path.GetFileName(output)}\n", new UTF8Encoding(false));
Console.WriteLine($"[spell-roster] output={output} rows={required.Length} failures={failures}");
return failures == 0 ? 0 : 1;

static async Task<NetworkClient> ConnectAtSelect(ClientConfig config, TimeSpan timeout)
{
    var net = new NetworkClient(config.ToNetSettings() with { CharacterName = null });
    net.Start();
    DateTime deadline = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < deadline)
    {
        if (net.State == NetState.CharacterSelect) return net;
        if (net.State is NetState.Failed or NetState.Disconnected)
            throw new InvalidOperationException($"network stopped at {net.State}: {net.Status}");
        await Task.Delay(50);
    }
    net.Dispose();
    throw new TimeoutException("timed out waiting for character select");
}

static async Task<NetworkClient> ConnectAtSelectWithRetry(ClientConfig config, TimeSpan timeout)
{
    Exception? last = null;
    for (int attempt = 1; attempt <= 6; attempt++)
    {
        try { return await ConnectAtSelect(config, timeout); }
        catch (Exception exception)
        {
            last = exception;
            if (attempt == 6) break;
            await Task.Delay(TimeSpan.FromSeconds(attempt * 2));
        }
    }
    throw new InvalidOperationException("could not reach character select after six attempts", last);
}

static async Task<byte> WaitCreate(NetworkClient net, TimeSpan timeout)
{
    DateTime deadline = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < deadline)
    {
        if (net.TryTakeCreateResult(out byte code)) return code;
        if (net.State is NetState.Failed or NetState.Disconnected)
            throw new InvalidOperationException($"network stopped at {net.State}: {net.Status}");
        await Task.Delay(25);
    }
    throw new TimeoutException("timed out waiting for SMSG_CHAR_CREATE");
}

static async Task<byte> WaitDelete(NetworkClient net, TimeSpan timeout)
{
    DateTime deadline = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < deadline)
    {
        if (net.TryTakeDeleteResult(out byte code)) return code;
        if (net.State is NetState.Failed or NetState.Disconnected)
            throw new InvalidOperationException($"network stopped at {net.State}: {net.Status}");
        await Task.Delay(25);
    }
    throw new TimeoutException("timed out waiting for SMSG_CHAR_DELETE");
}

static string Csv(string value) => '"' + value.Replace("\"", "\"\"") + '"';
readonly record struct RosterSpec(string Name, byte Class, string ClassName, byte Race, string RaceName);
