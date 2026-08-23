using System.Text.RegularExpressions;
using MSUIClient;

internal static class KeyBindingRegistryClinicalChecks
{
    public static void Run()
    {
        string root = ClientConfig.FindRepoRoot();
        string source = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop",
            "Panels", "GameLoop.Bindings.cs"));
        MatchCollection matches = Regex.Matches(source,
            "\\(\\\"(?<category>[^\\\"]+)\\\", GameBinding\\.(?<binding>[A-Za-z0-9]+),");
        var rows = matches.Select(match => (
            Category: match.Groups["category"].Value,
            Binding: match.Groups["binding"].Value)).ToArray();
        string[] categories = rows.Select(row => row.Category).Distinct().ToArray();
        Check(categories.SequenceEqual([
                "Movement", "Chat", "Action Bar", "Targeting", "Interface",
                "Miscellaneous", "Camera", "MultiActionBar", "Raid Targeting",
            ]),
            "Key Bindings visible category order drifted from current Benilla");
        Check(rows.Select(row => row.Binding).Distinct().Count() == rows.Length,
            "Key Bindings registry exposes one command in more than one visible category");
        Check(rows.Contains(("Movement", "Sheath")) &&
              rows.Contains(("Miscellaneous", "ToggleUi")) &&
              rows.Count(row => row.Category == "Action Bar") == 33 &&
              rows.Contains(("Action Bar", "ShapeshiftButton10")) &&
              rows.Contains(("Action Bar", "BonusActionButton10")) &&
              rows.Contains(("Action Bar", "ToggleActionBarLock")) &&
              rows.Count(row => row.Category == "MultiActionBar") == 24 &&
              rows.Count(row => row.Category == "Raid Targeting") == 9 &&
              !source.Contains("\"MultiActionBar 1\"", StringComparison.Ordinal) &&
              !source.Contains("\"MultiActionBar 2\"", StringComparison.Ordinal),
            "Key Bindings movement/misc seats or unified multibar header drifted");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
