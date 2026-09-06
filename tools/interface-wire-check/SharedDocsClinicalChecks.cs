using System.Text.RegularExpressions;
using MSUIClient;

/// <summary>
/// shared_docs/ is the tracked home of the team's laws and design documents (owner,
/// 2026-09-04); docs/ is git-ignored scratch. This keeps the pointers honest: every document in
/// shared_docs/ is listed in AGENTS.md, AGENTS.md and CODE_STRUCTURE_LAW.md both point at the
/// folder, and the binding laws are actually there. Run standalone:
/// interface-wire-check --shared-docs-only
/// </summary>
internal static class SharedDocsClinicalChecks
{
    private static readonly string[] RequiredDocuments =
    [
        "POSSESS_LAW.md",
        "CRPG_FREEZE_SYSTEM.md",
        "MACRO_BOOK.md",
    ];

    public static void Run()
    {
        string root = ClientConfig.FindRepoRoot();
        string folder = Path.Combine(root, "shared_docs");
        Check(Directory.Exists(folder), "shared_docs/ is missing at the repo root");
        foreach (string document in RequiredDocuments)
            Check(File.Exists(Path.Combine(folder, document)),
                $"shared_docs/{document} is missing - the laws live in shared_docs/, not the root or docs/");
        Check(!File.Exists(Path.Combine(root, "POSSESS_LAW.md")),
            "POSSESS_LAW.md must live in shared_docs/ only (a root copy would drift)");

        string agents = File.ReadAllText(Path.Combine(root, "AGENTS.md"));
        string structure = File.ReadAllText(Path.Combine(root, "CODE_STRUCTURE_LAW.md"));
        Check(agents.Contains("shared_docs/", StringComparison.Ordinal) &&
              agents.Contains("--shared-docs-only", StringComparison.Ordinal),
            "AGENTS.md must send agents to shared_docs/ and name this check");
        Check(structure.Contains("shared_docs/", StringComparison.Ordinal),
            "CODE_STRUCTURE_LAW.md must point at shared_docs/ for the behavioral laws");
        foreach (string path in Directory.EnumerateFiles(folder, "*.md"))
        {
            string name = Path.GetFileName(path);
            Check(agents.Contains("shared_docs/" + name, StringComparison.Ordinal),
                $"shared_docs/{name} is not listed in AGENTS.md - add its line so agents read it");
        }
        string ignore = File.ReadAllText(Path.Combine(root, ".gitignore"));
        Check(!Regex.IsMatch(ignore, @"(?m)^/?shared_docs/?\s*$"),
            "shared_docs/ must not be git-ignored");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
