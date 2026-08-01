using System.Diagnostics;
using System.Text.Json;
using System.Net.Sockets;
using System.Security.Cryptography;

if (args.Length < 1) { Console.Error.WriteLine("usage: live-run <config.json> [client live-run options]"); return 2; }
string config=Path.GetFullPath(args[0]);
using JsonDocument doc=JsonDocument.Parse(File.ReadAllText(config));
JsonElement server=doc.RootElement.GetProperty("server");
string host=doc.RootElement.GetProperty("realmdHost").GetString() ?? "";
int port=doc.RootElement.GetProperty("realmdPort").GetInt32();
string configuredCharacter=server.GetProperty("character").GetString() ?? "";
string character=configuredCharacter;
for(int i=1;i<args.Length-1;i++) if(args[i]=="--character") character=args[i+1];
string root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..","..",".."));
string output=Path.Combine(root,"live-runs"); Directory.CreateDirectory(output);
string stamp=DateTime.Now.ToString("yyyyMMdd-HHmmss");
try { using var tcp=new TcpClient(); await tcp.ConnectAsync(host,port).WaitAsync(TimeSpan.FromSeconds(3)); }
catch (Exception ex)
{
    string artifact=Path.Combine(output,$"bootstrap-preflight-{stamp}.json");
    File.WriteAllText(artifact,JsonSerializer.Serialize(new { result="SERVER_UNREACHABLE",host,port,error=ex.GetType().Name },new JsonSerializerOptions{WriteIndented=true}));
    string hash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(artifact))).ToLowerInvariant();
    File.WriteAllText(Path.Combine(output,$"bootstrap-preflight-{stamp}.sha256"),$"{hash}  {Path.GetFileName(artifact)}\n");
    Console.Error.WriteLine($"[live-run] SERVER_UNREACHABLE {host}:{port}; artifact={artifact}"); return 4;
}
bool legacyTest = configuredCharacter.Equals("TEST",StringComparison.OrdinalIgnoreCase);
string nightRoster = Path.Combine(root,"NIGHT_03","roster.csv");
bool nightOwned = string.IsNullOrWhiteSpace(configuredCharacter) &&
    character.StartsWith("NB",StringComparison.OrdinalIgnoreCase) && File.Exists(nightRoster) &&
    File.ReadLines(nightRoster).Skip(1).Any(line =>
        line.StartsWith($"\"{character}\",",StringComparison.OrdinalIgnoreCase) &&
        line.Contains(",AGENT_CREATED,",StringComparison.OrdinalIgnoreCase));
if (!legacyTest && !nightOwned)
{
    string artifact=Path.Combine(output,$"bootstrap-refused-{stamp}.json");
    File.WriteAllText(artifact,JsonSerializer.Serialize(new { result="REFUSED_NON_TEST_ACCOUNT",
        requirement="dedicated configuration must be bound to TEST or target an NB-prefixed NIGHT-owned character from character-select", character },new JsonSerializerOptions{WriteIndented=true}));
    string hash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(artifact))).ToLowerInvariant();
    File.WriteAllText(Path.Combine(output,$"bootstrap-refused-{stamp}.sha256"),$"{hash}  {Path.GetFileName(artifact)}\n");
    Console.Error.WriteLine($"[live-run] REFUSED_NON_TEST_ACCOUNT; artifact={artifact}"); return 3;
}
var psi=new ProcessStartInfo("dotnet") { UseShellExecute=false, WorkingDirectory=root };
foreach(string value in new[]{"run","--no-restore","--project",Path.Combine(root,"MSUIClient","MSUIClient.csproj"),"--",config,"--live-bootstrap"}) psi.ArgumentList.Add(value);
foreach(string value in args.Skip(1)) psi.ArgumentList.Add(value);
using Process child=Process.Start(psi)!; await child.WaitForExitAsync(); return child.ExitCode;
