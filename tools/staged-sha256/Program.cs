using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

if (args.Length != 1)
{
    Console.Error.WriteLine("usage: staged-sha256 <manifest-path>");
    return 2;
}

string manifest = Path.GetFullPath(args[0]);
string[] paths = Encoding.UTF8.GetString(RunGit("diff", "--cached", "--name-only", "-z"))
    .Split('\0', StringSplitOptions.RemoveEmptyEntries);
var lines = new List<string>(paths.Length);
foreach (string path in paths)
{
    byte[] staged = RunGit("show", $":{path}");
    lines.Add($"{Convert.ToHexString(SHA256.HashData(staged)).ToLowerInvariant()}  {path.Replace('\\', '/')}");
}
Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
File.WriteAllText(manifest, string.Join('\n', lines) + '\n', new UTF8Encoding(false));
Console.WriteLine($"[staged-sha256] wrote {lines.Count} entries to {manifest}");
return 0;

static byte[] RunGit(params string[] arguments)
{
    var start = new ProcessStartInfo("git")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    foreach (string argument in arguments) start.ArgumentList.Add(argument);
    using Process process = Process.Start(start) ?? throw new InvalidOperationException("could not start git");
    using var output = new MemoryStream();
    process.StandardOutput.BaseStream.CopyTo(output);
    string error = process.StandardError.ReadToEnd();
    process.WaitForExit();
    if (process.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {error}");
    return output.ToArray();
}
