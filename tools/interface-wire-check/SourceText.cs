using System.IO;

/// <summary>
/// Source reads for clinical checks, normalized to LF. The checks pin multi-line literals
/// with \n; git materializes working files as CRLF on Windows (eol=crlf), which otherwise
/// fails every such assertion the first time a merge or checkout rewrites the file.
/// </summary>
internal static class SourceText
{
    public static string Read(string path)
    {
        if (!File.Exists(path)) path = ResolveGameLoopMove(path);
        return File.ReadAllText(path).Replace("\r\n", "\n");
    }

    /// <summary>
    /// The runtime partials moved from MSUIClient/Program.*.cs into categorized
    /// MSUIClient/GameLoop/**/GameLoop.*.cs folders. Keep old clinical assertions useful during
    /// that migration: their source target is semantic, and each legacy basename has one exact
    /// replacement. Ambiguous or unrelated missing paths still fail normally.
    /// </summary>
    private static string ResolveGameLoopMove(string path)
    {
        string file = Path.GetFileName(path);
        if (!file.StartsWith("Program.", StringComparison.Ordinal) ||
            !file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return path;
        string? client = Path.GetDirectoryName(path);
        if (client is null) return path;
        string moved = "GameLoop." + file["Program.".Length..];
        string gameLoop = Path.Combine(client, "GameLoop");
        if (!Directory.Exists(gameLoop)) return path;
        string[] matches = Directory.GetFiles(gameLoop, moved, SearchOption.AllDirectories);
        return matches.Length == 1 ? matches[0] : path;
    }
}
