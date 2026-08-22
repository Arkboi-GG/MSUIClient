namespace MSUIClient.Formats;

/// <summary>The build-5875 macro chooser catalog, byte-derived from BuildMacroIconList.</summary>
public static class MacroIconCatalog
{
    public const string IconDirectory = @"Interface\Icons\";

    public static IReadOnlyList<string> Load(MpqMount mpq)
    {
        ArgumentNullException.ThrowIfNull(mpq);
        return Build(mpq.ListedFiles());
    }

    public static IReadOnlyList<string> Build(IEnumerable<string> listedFiles)
    {
        ArgumentNullException.ThrowIfNull(listedFiles);
        var stems = new List<string>();
        foreach (string listed in listedFiles)
        {
            string path = listed.Replace('/', '\\');
            if (!path.StartsWith(IconDirectory, StringComparison.OrdinalIgnoreCase) ||
                path.Length <= IconDirectory.Length)
                continue;
            string file = path[IconDirectory.Length..];
            if (file.Contains('\\')) continue;
            int dot = file.LastIndexOf('.');
            if (dot <= 0) continue;
            string extension = file[(dot + 1)..];
            if (!extension.Equals("blp", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals("tga", StringComparison.OrdinalIgnoreCase))
                continue;
            string stem = file[..dot];
            if (!stem.StartsWith("Spell_", StringComparison.OrdinalIgnoreCase) &&
                !stem.StartsWith("Ability_", StringComparison.OrdinalIgnoreCase))
                continue;
            stems.Add(stem);
        }

        stems.Sort(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(stems.Count);
        foreach (string stem in stems)
            if (result.Count == 0 ||
                !result[^1].AsSpan(IconDirectory.Length).Equals(stem,
                    StringComparison.OrdinalIgnoreCase))
                result.Add(IconDirectory + stem);
        return result;
    }
}
