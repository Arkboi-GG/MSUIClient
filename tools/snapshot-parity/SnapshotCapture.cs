using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace SnapshotParity;

internal static class SnapshotCapture
{
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".vscode", ".idea", ".claude", "bin", "obj", "target",
        "GameData", "dumps", "live-runs", "scratch", "portrait-batch", "variant-batch",
        "node_modules", "parity",
    };

    public static SnapshotManifest Capture(string kind, string root, string? bundlePath)
    {
        kind = kind.Trim().ToLowerInvariant();
        if (kind is not ("benilla" or "msui"))
            throw new ArgumentException("--kind must be benilla or msui");
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);

        string[] relativePaths = kind == "msui" && Directory.Exists(Path.Combine(root, ".git"))
            ? GitWorkingTreePaths(root)
            : RecursivePaths(root);
        relativePaths = relativePaths.Where(path => !ExcludedForKind(kind, path)).ToArray();
        var files = new List<SnapshotFile>(relativePaths.Length);
        foreach (string relative in relativePaths.Order(StringComparer.Ordinal))
        {
            string full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full)) continue;
            byte[] bytes = File.ReadAllBytes(full);
            files.Add(new SnapshotFile
            {
                Path = Normalize(relative),
                Sha256 = Hex(SHA256.HashData(bytes)),
                Size = bytes.LongLength,
                Role = Role(relative),
            });
        }

        string aggregate = Aggregate(files);
        var manifest = new SnapshotManifest
        {
            Kind = kind,
            Id = $"{kind}-{aggregate[..16]}",
            AggregateSha256 = aggregate,
            Root = root,
            CapturedUtc = DateTimeOffset.UtcNow,
            Exclusions = ExcludedDirectories.Order().Concat(KindExclusions(kind)).ToList(),
            Files = files,
        };
        if (!string.IsNullOrWhiteSpace(bundlePath)) WriteBundle(manifest, bundlePath!);
        return manifest;
    }

    public static void Verify(SnapshotManifest manifest)
    {
        foreach (SnapshotFile file in manifest.Files)
        {
            string full = Path.Combine(manifest.Root, file.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full)) throw new InvalidDataException($"snapshot source missing: {file.Path}");
            string actual = Hex(SHA256.HashData(File.ReadAllBytes(full)));
            if (!actual.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"snapshot source changed after capture: {file.Path}");
        }
        string aggregate = Aggregate(manifest.Files);
        if (!aggregate.Equals(manifest.AggregateSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("snapshot manifest aggregate is invalid");
    }

    private static string[] GitWorkingTreePaths(string root)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in new[] { "ls-files", "--cached", "--others", "--exclude-standard", "-z" })
            start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("could not start git");
        using var output = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(output);
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"git ls-files failed: {error}");
        return Encoding.UTF8.GetString(output.ToArray())
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(Normalize)
            .Where(path => !HasExcludedSegment(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string[] RecursivePaths(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Select(path => Normalize(Path.GetRelativePath(root, path)))
        .Where(path => !HasExcludedSegment(path))
        .ToArray();

    private static bool HasExcludedSegment(string relative)
    {
        string[] segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < segments.Length; i++)
        {
            if (!ExcludedDirectories.Contains(segments[i])) continue;

            // Rust workspaces conventionally build into target/, but Benilla also has a real
            // source module at crates/benilla-app/src/target/. Treating every segment named
            // "target" as generated output silently removed that module from snapshots and
            // made the drift gate report its live files as REMOVED. A target directly below
            // src is source; every other target directory remains excluded.
            if (segments[i].Equals("target", StringComparison.OrdinalIgnoreCase) &&
                i > 0 && segments[i - 1].Equals("src", StringComparison.OrdinalIgnoreCase))
                continue;
            return true;
        }
        return false;
    }

    private static bool ExcludedForKind(string kind, string relative) =>
        kind == "msui" && (relative.StartsWith("tools/snapshot-parity/", StringComparison.OrdinalIgnoreCase) ||
            relative.Equals("docs/current/project-context/SNAPSHOT_PARITY_WORKFLOW.md", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> KindExclusions(string kind)
    {
        if (kind != "msui") yield break;
        yield return "tools/snapshot-parity/ (comparison observer)";
        yield return "docs/current/project-context/SNAPSHOT_PARITY_WORKFLOW.md (comparison observer)";
    }

    private static void WriteBundle(SnapshotManifest manifest, string bundlePath)
    {
        string fullBundle = Path.GetFullPath(bundlePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullBundle)!);
        using FileStream stream = File.Create(fullBundle);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false);
        foreach (SnapshotFile file in manifest.Files)
        {
            string source = Path.Combine(manifest.Root, file.Path.Replace('/', Path.DirectorySeparatorChar));
            archive.CreateEntryFromFile(source, file.Path, CompressionLevel.Optimal);
        }
    }

    private static string Aggregate(IEnumerable<SnapshotFile> files)
    {
        var text = new StringBuilder();
        foreach (SnapshotFile file in files.OrderBy(file => file.Path, StringComparer.Ordinal))
            text.Append(file.Path).Append('\0').Append(file.Size).Append('\0').Append(file.Sha256).Append('\n');
        return Hex(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    internal static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
    internal static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    private static string Role(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".rs" or ".cs" => "source",
        ".xml" => "ui",
        ".wgsl" or ".vert" or ".frag" => "shader",
        ".toml" or ".csproj" or ".sln" or ".props" or ".targets" or ".lock" => "manifest",
        ".md" => "documentation",
        ".json" or ".yml" or ".yaml" or ".ini" or ".example" => "configuration",
        ".png" or ".jpg" or ".blp" or ".m2" or ".wav" or ".ogg" => "asset",
        _ => "other",
    };
}
