namespace MSUIClient.Engine.UI;

/// <summary>
/// The Macro Book's template shelf (cam, 2026-09-04: "a template style so that you could
/// quickly insert the lines of code required and then fill in the variables"). Every template
/// is a few lines with &lt;placeholders&gt;; inserting one appends the lines to the body, and the
/// linter refuses to call the macro clean until every &lt;placeholder&gt; is replaced - that IS
/// the fill-in-the-variables flow, with no widget beyond the editor. Command names here are
/// checked against the embedded Core export by the clinical check so a renamed command cannot
/// leave a dead template behind.
/// </summary>
public static class MacroTemplateLaw
{
    public readonly record struct Template(string Name, string Hint, IReadOnlyList<string> Lines);

    public static IReadOnlyList<Template> All { get; } =
    [
        new("Add item", "Give yourself one item by id", [".additem <item id>"]),
        new("Add item stack", "Give yourself N of an item", [".additem <item id> <count>"]),
        new("Gear kit", "One .additem per piece; add lines as needed",
        [
            "# gear kit",
            ".additem <item id>",
            ".additem <item id>",
            ".additem <item id>",
            ".additem <item id>",
            ".additem <item id>",
        ]),
        new("Item set", "Give yourself a whole item set", [".additemset <itemset id>"]),
        new("Learn spell", "Teach yourself a spell by id", [".learn <spell id>"]),
        new("Learn my class", "Every spell for your class", [".learn all_myclass"]),
        new("Learn my talents", "Every talent for your class", [".learn all_mytalents"]),
        new("Level up", "Gain levels", [".levelup <levels>"]),
        new("Money", "Add copper to your purse", [".modify money <copper>"]),
        new("Max weapon skills", "Weapon skills to your level's cap", [".maxskill"]),
        new("Repair", "Repair everything you wear", [".repairitems"]),
        new("Teleport", "Go to a named location (.lookup tele)", [".tele <location>"]),
        new("Go to xyz", "Go to coordinates on the current map", [".go xyz <x> <y> <z>"]),
        new("Revive", "Revive yourself or your target", [".revive"]),
        new("Clear cooldowns", "Wipe every cooldown", [".cooldown clear"]),
        new("Apply aura", "Put a spell aura on your target", [".aura <spell id>"]),
        new("Reload creatures", "Re-read creature_template on the Core",
            [".reload creature_template"]),
        new("GM mode on", "Toggle GM mode", [".gm on"]),
        new("Cast spell", "Cast a spell you know by name", ["/cast <spell name>"]),
        new("Use item", "Use an item you carry by name", ["/use <item name>"]),
        new("Say", "Say something", ["/say <text>"]),
        new("Party chat", "Tell the party something", ["/p <text>"]),
        new("Emote", "Any text emote works", ["/wave"]),
        new("Attack target", "Start auto-attacking the target", ["/startattack"]),
        new("Follow", "Follow a party member by name", ["/follow <name>"]),
        new("Comment", "A note the macro skips", ["# <note>"]),
    ];

    public static IReadOnlyList<Template> Search(string filter)
    {
        filter = filter.Trim();
        if (filter.Length == 0) return All;
        return All.Where(template =>
            template.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            template.Hint.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            template.Lines.Any(line => line.Contains(filter, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

    /// <summary>
    /// Append lines to a body on their own line(s). Returns the body unchanged, and false, if
    /// the result would exceed the capacity - a template never silently truncates a macro.
    /// </summary>
    public static bool TryAppend(string body, IEnumerable<string> lines, int capacity,
        out string result)
    {
        string joined = string.Join('\n', lines);
        string prefix = body.Length == 0 || body.EndsWith('\n') ? body : body + "\n";
        result = prefix + joined;
        if (result.Length > capacity)
        {
            result = body;
            return false;
        }
        return true;
    }

    /// <summary>Every dot command a template uses, for the clinical check against the export.</summary>
    public static IEnumerable<string> ServerCommandNames() => All
        .SelectMany(template => template.Lines)
        .Where(line => line.StartsWith('.'))
        .Select(line => line[1..]);
}
