using System.Text;

namespace MSUIClient.Engine.UI;

/// <summary>
/// The Macro Book's data law: stable macro ids, the two books (account / character), sections,
/// the v2 store text and the legacy MACRO/END migration. Owner decision 2026-09-04 (Discord,
/// MightyDorf / cam / Yafrovon): the vanilla macro window's imported limits (255 characters,
/// 18 slots, no grouping) have no bearing on this client - "this is for us to have fun" - so the
/// window was reimagined. What survives from 1.12 is the SHAPE of the store file (MACRO n "name"
/// icon / body / END) so an old reader still finds the macros, and the account-vs-character split.
///
/// Ids are the action-bar identity (ActionSlot.Macro carries them in the 24-bit action field and
/// the server stores the bars), so they must never move when a macro is added, deleted or
/// re-sectioned. The pre-book client bound bars by LIST POSITION: account slots were ids 1..18
/// and character slots 19..36. Those 36 ids are reserved as the legacy ranges so every hotbar
/// button placed before the book keeps working without a rewrite; new account macros count up
/// from 37 and new character macros from 0x800000 (character files never share an id with the
/// account file, and one character's file is never loaded next to another's).
/// </summary>
public static class MacroBookLaw
{
    public const string StoredIconPrefix = @"Interface\Icons\";
    public const string DefaultIconPath = @"Interface\Icons\INV_Misc_QuestionMark";
    public const string StoreHeader = "MACROBOOK";
    public const int StoreVersion = 2;
    /// <summary>Body cap. The old 255 was the 1.12 edit box; a 17-line gear kit is ~250 characters
    /// on its own. 4000 leaves room for a full raid-prep list and stays a trivial string.</summary>
    public const int BodyCapacity = 4000;
    public const int NameCapacity = 32;
    public const int SectionNameCapacity = 32;
    /// <summary>Upper bound per book ("of course with some sort of upper bound").</summary>
    public const int MacrosPerBook = 500;
    /// <summary>The Core drops a chat line longer than this, so a macro LINE is still capped.</summary>
    public const int ChatLineLimit = 255;
    public const int LegacyMacrosPerSet = 18;
    public const uint LegacyAccountFirstId = 1;
    public const uint LegacyCharacterFirstId = 19;
    public const uint AccountFirstId = 37;
    public const uint CharacterFirstId = 0x800000;
    /// <summary>ActionSlot.Packed keeps 24 bits for the action id.</summary>
    public const uint MaxId = 0xFFFFFF;
    public const string DefaultMacroName = "New Macro";
    public const string DefaultSectionName = "New Section";

    public enum Scope
    {
        Account,
        Character,
    }

    public sealed record StoredMacro(uint Id, string Name, string Body, string IconPath,
        string Section);

    public sealed record StoredSection(string Name, bool Collapsed);

    /// <summary><paramref name="Legacy"/> marks a pre-book file (or the even older macros.json)
    /// whose ids were assigned by position - the caller writes it back in v2 shape at once.</summary>
    public sealed record StoredBook(IReadOnlyList<StoredSection> Sections,
        IReadOnlyList<StoredMacro> Macros, uint NextId, bool Legacy)
    {
        public static StoredBook Empty(Scope scope) => new([], [], FirstId(scope), Legacy: false);
    }

    public static uint FirstId(Scope scope) =>
        scope == Scope.Account ? AccountFirstId : CharacterFirstId;

    public static uint LegacyFirstId(Scope scope) =>
        scope == Scope.Account ? LegacyAccountFirstId : LegacyCharacterFirstId;

    /// <summary>Which book an action-bar macro id belongs to.</summary>
    public static Scope ScopeOfId(uint id) =>
        id >= CharacterFirstId ||
        (id >= LegacyCharacterFirstId && id < LegacyCharacterFirstId + LegacyMacrosPerSet)
            ? Scope.Character : Scope.Account;

    /// <summary>Is <paramref name="id"/> a valid, allocatable id for <paramref name="scope"/>?</summary>
    public static bool IdBelongs(Scope scope, uint id) =>
        id != 0 && id <= MaxId && ScopeOfId(id) == scope;

    /// <summary>
    /// The next free id for a book: never one already in use, never one from the other book's
    /// range, never past the 24-bit ceiling. <paramref name="next"/> is the persisted NEXT hint.
    /// </summary>
    public static uint AllocateId(Scope scope, uint next, IEnumerable<uint> inUse)
    {
        var used = new HashSet<uint>(inUse);
        uint candidate = Math.Max(next, FirstId(scope));
        while (candidate <= MaxId)
        {
            if (!used.Contains(candidate) && IdBelongs(scope, candidate)) return candidate;
            candidate++;
        }
        throw new InvalidOperationException("macro id space exhausted");
    }

    /// <summary>Benilla's path-component law: only ASCII letters and digits survive.</summary>
    public static string StoreFileToken(string value)
    {
        string token = new(value.Select(character => char.IsAsciiLetterOrDigit(character)
            ? character : '_').ToArray());
        return token.Length == 0 ? "unknown" : token;
    }

    /// <summary>
    /// The reference tokenizes on either CR or LF and executes each non-empty line through the
    /// same ChatFrame route as typed input. '#' lines are comments (ours, not 1.12's).
    /// </summary>
    public static IReadOnlyList<string> RunnableLines(string body) => body
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.Trim())
        .Where(line => line.Length > 0 && !line.StartsWith('#'))
        .ToArray();

    public static string NormalizeIconPath(string token) => token.Length == 0 ? "" :
        token.Contains('\\') || token.Contains('/') ? token : StoredIconPrefix + token;

    public static string IconToken(string iconPath) =>
        iconPath.StartsWith(StoredIconPrefix, StringComparison.OrdinalIgnoreCase)
            ? iconPath[StoredIconPrefix.Length..] : iconPath;

    public static string ClampName(string name, int capacity)
    {
        string trimmed = name.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return trimmed.Length <= capacity ? trimmed : trimmed[..capacity];
    }

    /// <summary>"Gear Sets", then "Gear Sets 2", "Gear Sets 3" - section names are the section
    /// identity in the store, so two sections never share one.</summary>
    public static string UniqueSectionName(IEnumerable<string> existing, string requested)
    {
        var taken = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        string stem = ClampName(requested, SectionNameCapacity);
        if (stem.Length == 0) stem = DefaultSectionName;
        if (!taken.Contains(stem)) return stem;
        for (int suffix = 2; ; suffix++)
        {
            string candidate = ClampName($"{stem} {suffix}", SectionNameCapacity);
            if (!taken.Contains(candidate)) return candidate;
        }
    }

    // ── store text ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads either shape. A file without the MACROBOOK header is the pre-book store: dense
    /// MACRO n blocks whose n IS the legacy slot, so the macro gets id LegacyFirstId + n - 1 and
    /// the result is flagged Legacy. Unknown lines are skipped in both shapes, which is what lets
    /// a v2 file (SECTION / NEXT lines) still open in the old reader.
    /// </summary>
    public static StoredBook ParseStore(string text, Scope scope)
    {
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n').Split('\n');
        int lineCount = lines.Length;
        if (lineCount > 0 && lines[^1].Length == 0) lineCount--;
        bool versioned = false;
        uint next = FirstId(scope);
        var sections = new List<StoredSection>();
        var macros = new List<(uint Index, StoredMacro Macro)>();
        string currentSection = "";
        int cursor = 0;
        while (cursor < lineCount)
        {
            string line = lines[cursor++];
            string trimmed = line.Trim();
            if (trimmed.StartsWith(StoreHeader + " ", StringComparison.Ordinal))
            {
                versioned = true;
                continue;
            }
            if (trimmed.StartsWith("NEXT ", StringComparison.Ordinal))
            {
                if (uint.TryParse(trimmed[5..].Trim(), out uint parsed)) next = parsed;
                continue;
            }
            if (trimmed.StartsWith("SECTION ", StringComparison.Ordinal))
            {
                string rest = trimmed[8..];
                bool collapsed = rest.EndsWith(" COLLAPSED", StringComparison.Ordinal);
                if (collapsed) rest = rest[..^" COLLAPSED".Length];
                int open = rest.IndexOf('"');
                int close = rest.LastIndexOf('"');
                if (open < 0 || close <= open) continue;
                string sectionName = rest[(open + 1)..close];
                if (sectionName.Length == 0) continue;
                if (!sections.Any(section => section.Name.Equals(sectionName,
                        StringComparison.OrdinalIgnoreCase)))
                    sections.Add(new StoredSection(sectionName, collapsed));
                currentSection = sectionName;
                continue;
            }
            if (!trimmed.StartsWith("MACRO ", StringComparison.Ordinal)) continue;
            string header = trimmed[6..];
            int firstQuote = header.IndexOf('"');
            int lastQuote = header.LastIndexOf('"');
            if (firstQuote < 0 || lastQuote <= firstQuote ||
                !uint.TryParse(header[..firstQuote].Trim(), out uint index))
                continue;
            string name = header[(firstQuote + 1)..lastQuote];
            string iconToken = header[(lastQuote + 1)..].Trim();
            var body = new List<string>();
            while (cursor < lineCount)
            {
                string bodyLine = lines[cursor];
                if (bodyLine.Trim() == "END")
                {
                    cursor++;
                    break;
                }
                string bodyTrimmed = bodyLine.TrimStart();
                if (bodyTrimmed.StartsWith("MACRO ", StringComparison.Ordinal) ||
                    bodyTrimmed.StartsWith("SECTION ", StringComparison.Ordinal)) break;
                body.Add(bodyLine);
                cursor++;
            }
            macros.Add((index, new StoredMacro(index, name, string.Join('\n', body),
                NormalizeIconPath(iconToken), currentSection)));
        }

        if (!versioned)
        {
            // Legacy: n is the 1-based slot within an 18-entry set.
            uint legacyBase = LegacyFirstId(scope);
            StoredMacro[] legacy = macros.OrderBy(record => record.Index)
                .Where(record => record.Index >= 1 && record.Index <= LegacyMacrosPerSet)
                .Select(record => record.Macro with
                {
                    Id = legacyBase + record.Index - 1,
                    Section = "",
                })
                .GroupBy(macro => macro.Id).Select(group => group.First())
                .ToArray();
            return new StoredBook([], legacy, FirstId(scope), Legacy: true);
        }

        var seen = new HashSet<uint>();
        var kept = new List<StoredMacro>();
        foreach ((uint _, StoredMacro macro) in macros)
        {
            if (!IdBelongs(scope, macro.Id) || !seen.Add(macro.Id)) continue;
            if (kept.Count >= MacrosPerBook) break;
            kept.Add(macro);
        }
        uint highest = kept.Count == 0 ? 0 : kept.Max(macro => macro.Id);
        if (highest >= next) next = highest + 1;
        return new StoredBook(sections, kept, Math.Max(next, FirstId(scope)), Legacy: false);
    }

    /// <summary>
    /// v2 text: header, NEXT, then every section as SECTION "name" [COLLAPSED] followed by its
    /// macros, ungrouped macros first (before any SECTION line, so an old reader and a new one
    /// agree on which section a macro sits in). Empty sections are kept - they are the user's.
    /// </summary>
    public static string WriteStore(StoredBook book)
    {
        var output = new StringBuilder();
        output.Append(StoreHeader).Append(' ').Append(StoreVersion).Append('\n');
        output.Append("NEXT ").Append(book.NextId).Append('\n');
        var known = new HashSet<string>(book.Sections.Select(section => section.Name),
            StringComparer.OrdinalIgnoreCase);
        foreach (StoredMacro macro in book.Macros)
            if (macro.Section.Length == 0 || !known.Contains(macro.Section))
                AppendMacro(output, macro);
        foreach (StoredSection section in book.Sections)
        {
            output.Append("SECTION \"").Append(section.Name).Append('"');
            if (section.Collapsed) output.Append(" COLLAPSED");
            output.Append('\n');
            foreach (StoredMacro macro in book.Macros)
                if (macro.Section.Equals(section.Name, StringComparison.OrdinalIgnoreCase))
                    AppendMacro(output, macro);
        }
        return output.ToString();
    }

    private static void AppendMacro(StringBuilder output, StoredMacro macro)
    {
        output.Append("MACRO ").Append(macro.Id).Append(" \"").Append(macro.Name)
            .Append("\" ").Append(IconToken(macro.IconPath)).Append('\n');
        string body = macro.Body.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        if (body.Length > 0)
        {
            output.Append(body);
            if (body[^1] != '\n') output.Append('\n');
        }
        output.Append("END\n");
    }

    // ── list projection ──────────────────────────────────────────────────────────────────

    public enum RowKind
    {
        Macro,
        Section,
    }

    /// <summary>One visible line of the book list. A Section row carries its name, collapsed
    /// state and macro count; a Macro row carries the id and whether it is indented under a
    /// section.</summary>
    public readonly record struct Row(RowKind Kind, string Section, uint MacroId, string Label,
        bool Collapsed, int Count, bool Indented);

    public static bool MatchesFilter(StoredMacro macro, string filter) =>
        filter.Length == 0 ||
        macro.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        macro.Body.Contains(filter, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Ungrouped macros first, then each section header with its macros under it unless
    /// collapsed. A search filter expands everything, shows only matching macros and hides
    /// sections left empty by the filter (a header with nothing under it is noise mid-search).
    /// </summary>
    public static IReadOnlyList<Row> BuildRows(IReadOnlyList<StoredSection> sections,
        IReadOnlyList<StoredMacro> macros, string filter)
    {
        filter = filter.Trim();
        bool searching = filter.Length > 0;
        var known = new HashSet<string>(sections.Select(section => section.Name),
            StringComparer.OrdinalIgnoreCase);
        var rows = new List<Row>();
        foreach (StoredMacro macro in macros)
            if ((macro.Section.Length == 0 || !known.Contains(macro.Section)) &&
                MatchesFilter(macro, filter))
                rows.Add(new Row(RowKind.Macro, "", macro.Id, macro.Name, false, 0, false));
        foreach (StoredSection section in sections)
        {
            StoredMacro[] members = macros
                .Where(macro => macro.Section.Equals(section.Name,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            StoredMacro[] shown = members.Where(macro => MatchesFilter(macro, filter)).ToArray();
            if (searching && shown.Length == 0) continue;
            bool collapsed = section.Collapsed && !searching;
            rows.Add(new Row(RowKind.Section, section.Name, 0, section.Name, collapsed,
                members.Length, false));
            if (collapsed) continue;
            foreach (StoredMacro macro in shown)
                rows.Add(new Row(RowKind.Macro, section.Name, macro.Id, macro.Name, false, 0,
                    true));
        }
        return rows;
    }

    public static int MaximumScroll(int rowCount, int visibleRows) =>
        Math.Max(0, rowCount - Math.Max(1, visibleRows));

    public static int ClampScroll(int requested, int rowCount, int visibleRows) =>
        Math.Clamp(requested, 0, MaximumScroll(rowCount, visibleRows));

    /// <summary>The scroll that brings <paramref name="row"/> into the visible window.</summary>
    public static int ScrollToReveal(int current, int row, int rowCount, int visibleRows)
    {
        if (row < 0) return ClampScroll(current, rowCount, visibleRows);
        if (row < current) return ClampScroll(row, rowCount, visibleRows);
        if (row >= current + visibleRows)
            return ClampScroll(row - visibleRows + 1, rowCount, visibleRows);
        return ClampScroll(current, rowCount, visibleRows);
    }
}
