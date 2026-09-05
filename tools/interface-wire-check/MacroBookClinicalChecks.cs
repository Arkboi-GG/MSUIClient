using System.Numerics;
using MSUIClient;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;

/// <summary>
/// The Macro Book (2026-09-04): stable ids and the legacy ranges, the v2 store round trip and the
/// pre-book migration, the list projection, the frozen geometry, the linter against a fixture
/// catalog AND the real embedded Core export (every template must resolve there), and the
/// runtime source fences. Run standalone: interface-wire-check --macro-book-only
/// </summary>
internal static class MacroBookClinicalChecks
{
    public static void Run()
    {
        CheckIdsAndStore();
        CheckRows();
        CheckGeometry();
        CheckLint();
        CheckEmbeddedCatalogAndTemplates();
        CheckArchiveCatalogLaw();
        CheckDragDropAndConfirm();
        CheckRuntimeSourceFence();
    }

    /// <summary>Owner QoL round 2026-09-05: drag macros into sections and to reorder, drag
    /// section headers to reorder, ask before deleting a macro, name label on the hotbar.</summary>
    private static void CheckDragDropAndConfirm()
    {
        uint[] order = [1, 2, 3, 4];
        Check(MacroBookLaw.ReorderBeside(order, 4, 2, after: false).SequenceEqual([1u, 4u, 2u, 3u]) &&
              MacroBookLaw.ReorderBeside(order, 1, 3, after: true).SequenceEqual([2u, 3u, 1u, 4u]) &&
              MacroBookLaw.ReorderBeside(order, 2, 2, after: true).SequenceEqual(order) &&
              MacroBookLaw.ReorderBeside(order, 9, 2, after: true).SequenceEqual(order) &&
              MacroBookLaw.ReorderBeside(order, 1, 4, after: true).SequenceEqual([2u, 3u, 4u, 1u]),
            "Macro reorder-beside law drifted");
        string[] sections = ["A", "B", "C"];
        Check(MacroBookLaw.ReorderSectionBeside(sections, "C", "A", after: false)
                  .SequenceEqual(["C", "A", "B"]) &&
              MacroBookLaw.ReorderSectionBeside(sections, "A", "c", after: true)
                  .SequenceEqual(["B", "C", "A"]) &&
              MacroBookLaw.ReorderSectionBeside(sections, "B", "B", after: true)
                  .SequenceEqual(sections) &&
              MacroBookLaw.ReorderSectionBeside(sections, "Z", "A", after: true)
                  .SequenceEqual(sections),
            "Section reorder-beside law drifted");
        var sectionRow = new MacroBookLaw.Row(MacroBookLaw.RowKind.Section, "S", 0, "S", false, 2, false);
        var macroRow = new MacroBookLaw.Row(MacroBookLaw.RowKind.Macro, "S", 7, "m", false, 0, true);
        Check(MacroBookLaw.MacroDropOn(sectionRow, true) ==
                  new MacroBookLaw.Drop(MacroBookLaw.DropKind.IntoSection, "S", 0, false) &&
              MacroBookLaw.MacroDropOn(macroRow, true) ==
                  new MacroBookLaw.Drop(MacroBookLaw.DropKind.BesideMacro, "S", 7, true) &&
              MacroBookLaw.SectionDropOn(sectionRow, false) ==
                  new MacroBookLaw.Drop(MacroBookLaw.DropKind.BesideSection, "S", 0, false) &&
              MacroBookLaw.SectionDropOn(macroRow, false).Kind == MacroBookLaw.DropKind.None &&
              !MacroBookUiLaw.DropAfter(100, 109, 1f) && MacroBookUiLaw.DropAfter(100, 110, 1f),
            "Drop target projection drifted");
        Check(MacroBookUiLaw.HotbarLabel("Caster Sixty Gear", label => label.Length * 6) == "Caster" &&
              MacroBookUiLaw.HotbarLabel("  ab  ", label => label.Length * 6) == "ab" &&
              MacroBookUiLaw.HotbarLabel("", _ => 0) == "" &&
              MacroBookUiLaw.HotbarNameFont == "GameFontHighlightSmallOutline" &&
              MacroBookUiLaw.HotbarNameBoxTop(100, 1f) == 124 &&
              MacroBookUiLaw.HotbarNameMaxCharacters >= 6,
            "Hotbar macro name label law drifted (36x10 box at BOTTOM (0,2), first characters that fit)");
        Check(ConfirmPopupUiLaw.IsConfirmPopup(ConfirmPopupUiLaw.DeleteMacroPopupType) &&
              ConfirmPopupUiLaw.Captions(ConfirmPopupUiLaw.DeleteMacroPopupType) == ("Delete", "Cancel") &&
              ConfirmPopupUiLaw.DeleteMacroText("Kit").StartsWith("Delete the macro \"Kit\"?", StringComparison.Ordinal) &&
              ConfirmPopupUiLaw.DeleteMacroDefinition.HasAccept &&
              ConfirmPopupUiLaw.DeleteMacroDefinition.HasCancel &&
              ConfirmPopupUiLaw.DeleteMacroDefinition.HideOnEscape,
            "Delete-macro StaticPopup definition drifted");

        string root = ClientConfig.FindRepoRoot();
        string macro = File.ReadAllText(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Macro.cs"));
        string bars = File.ReadAllText(Path.Combine(root, "MSUIClient", "GameLoop", "Hud",
            "GameLoop.ActionBars.cs"));
        string confirms = File.ReadAllText(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Confirms.cs"));
        Check(macro.Contains("TryDropDraggedMacroInBook(uint id)", StringComparison.Ordinal) &&
              macro.Contains("MacroBookLaw.ReorderBeside(", StringComparison.Ordinal) &&
              macro.Contains("MacroBookLaw.ReorderSectionBeside(", StringComparison.Ordinal) &&
              macro.Contains("MacroBookLaw.MacroDropOn(row, after)", StringComparison.Ordinal) &&
              macro.Contains("MacroBookLaw.SectionDropOn(row, after)", StringComparison.Ordinal) &&
              macro.Contains("ConfirmPopupUiLaw.DeleteMacroDefinition", StringComparison.Ordinal) &&
              macro.Contains("private void ConfirmDeleteMacro()", StringComparison.Ordinal) &&
              macro.Contains("RequestDeleteSelection();", StringComparison.Ordinal) &&
              !macro.Contains("private void DeleteSelection()", StringComparison.Ordinal),
            "Macro Book drag/drop or delete-confirmation seams escaped their laws");
        // The hotbar answers a macro release it does not receive by asking the book (it runs
        // first in the frame), and paints the name on every macro button on every bar.
        Check(bars.Contains("else TryDropDraggedMacroInBook(_draggingMacroId);", StringComparison.Ordinal) &&
              CountOf(bars, "DrawActionMacroName(dl, buttonMin, MacroName(action.ActionId), scale);") == 2 &&
              bars.Contains("MacroBookUiLaw.HotbarLabel(name,", StringComparison.Ordinal),
            "Action bars must hand an off-bar macro release to the book and label macro buttons");
        Check(confirms.Contains("DrawConfirmPopup(ConfirmPopupUiLaw.DeleteMacroPopupType);", StringComparison.Ordinal) &&
              confirms.Contains("ConfirmDeleteMacro();", StringComparison.Ordinal) &&
              confirms.Contains("ConfirmPopupUiLaw.DeleteMacroText(visible.Instance.DataToken", StringComparison.Ordinal),
            "The delete-macro popup must be drawn and routed by the shared confirm popups");
    }

    private static int CountOf(string text, string needle)
    {
        int count = 0;
        for (int at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0;
             at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static void CheckIdsAndStore()
    {
        Check(MacroBookLaw.ScopeOfId(1) == MacroBookLaw.Scope.Account &&
              MacroBookLaw.ScopeOfId(18) == MacroBookLaw.Scope.Account &&
              MacroBookLaw.ScopeOfId(19) == MacroBookLaw.Scope.Character &&
              MacroBookLaw.ScopeOfId(36) == MacroBookLaw.Scope.Character &&
              MacroBookLaw.ScopeOfId(37) == MacroBookLaw.Scope.Account &&
              MacroBookLaw.ScopeOfId(0x7FFFFF) == MacroBookLaw.Scope.Account &&
              MacroBookLaw.ScopeOfId(0x800000) == MacroBookLaw.Scope.Character &&
              MacroBookLaw.MaxId == 0xFFFFFF &&
              !MacroBookLaw.IdBelongs(MacroBookLaw.Scope.Account, 0) &&
              !MacroBookLaw.IdBelongs(MacroBookLaw.Scope.Account, 0x1000000),
            "Macro id scope ranges drifted (legacy 1..18 account / 19..36 character; 37+ / 0x800000+)");
        Check(MacroBookLaw.AllocateId(MacroBookLaw.Scope.Account, 1, []) == 37 &&
              MacroBookLaw.AllocateId(MacroBookLaw.Scope.Account, 37, [37u, 38u]) == 39 &&
              MacroBookLaw.AllocateId(MacroBookLaw.Scope.Account, 19, []) == 37 &&
              MacroBookLaw.AllocateId(MacroBookLaw.Scope.Character, 0, [0x800000u]) == 0x800001 &&
              MacroBookLaw.AllocateId(MacroBookLaw.Scope.Character, 20, []) == 0x800000,
            "Macro id allocation must skip used ids and the other book's ranges");

        // The pre-book file: dense MACRO n blocks, n is the slot -> legacy id, flagged Legacy.
        const string legacyStore = "MACRO 3 \"ns\" Ability_BackStab\n/say three\nEND\n" +
            "MACRO 1 \"say \"hi\"\" Ability_Ambush\n/say one\nEND\n" +
            "MACRO bad \"skip\" Ability_Ambush\nEND\n" +
            "MACRO 2 \"bare\" \nEND\n";
        MacroBookLaw.StoredBook legacy = MacroBookLaw.ParseStore(legacyStore,
            MacroBookLaw.Scope.Character);
        Check(legacy.Legacy && legacy.Sections.Count == 0 && legacy.Macros.Count == 3 &&
              legacy.Macros[0].Id == 19 && legacy.Macros[0].Name == "say \"hi\"" &&
              legacy.Macros[0].IconPath == @"Interface\Icons\Ability_Ambush" &&
              legacy.Macros[1].Id == 20 && legacy.Macros[1].Name == "bare" &&
              legacy.Macros[1].IconPath == "" &&
              legacy.Macros[2].Id == 21 && legacy.Macros[2].Body == "/say three" &&
              legacy.NextId == MacroBookLaw.CharacterFirstId,
            "Legacy MACRO/END migration must map slot n to LegacyFirstId + n - 1");

        var sections = new List<MacroBookLaw.StoredSection>
        {
            new("Gear Sets", true),
            new("Rotation", false),
        };
        var macros = new List<MacroBookLaw.StoredMacro>
        {
            new(37, "A", "x", @"Interface\Icons\Foo", ""),
            new(38, "B", ".additem 1\n.additem 2", @"Interface\Icons\Bar", "Gear Sets"),
            new(40, "C", "/say hi", "", "Rotation"),
        };
        var book = new MacroBookLaw.StoredBook(sections, macros, 41, Legacy: false);
        string text = MacroBookLaw.WriteStore(book);
        MacroBookLaw.StoredBook back = MacroBookLaw.ParseStore(text, MacroBookLaw.Scope.Account);
        Check(text.StartsWith("MACROBOOK 2\nNEXT 41\n", StringComparison.Ordinal) &&
              text.Contains("MACRO 37 \"A\" Foo\nx\nEND\nSECTION \"Gear Sets\" COLLAPSED\n" +
                  "MACRO 38 \"B\" Bar\n.additem 1\n.additem 2\nEND\nSECTION \"Rotation\"\n" +
                  "MACRO 40 \"C\" \n/say hi\nEND\n", StringComparison.Ordinal) &&
              !back.Legacy && back.NextId == 41 &&
              back.Sections.SequenceEqual(sections) && back.Macros.SequenceEqual(macros),
            "v2 store round trip drifted (header/NEXT/SECTION/MACRO shape)");
        // A v2 file keeps its NEXT above every id; a foreign-scope id is dropped.
        MacroBookLaw.StoredBook foreign = MacroBookLaw.ParseStore(
            "MACROBOOK 2\nNEXT 5\nMACRO 20 \"char\" \nEND\nMACRO 50 \"acct\" \nEND\n",
            MacroBookLaw.Scope.Account);
        Check(foreign.Macros.Count == 1 && foreign.Macros[0].Id == 50 && foreign.NextId == 51,
            "v2 parse must drop other-scope ids and lift NEXT past the highest id");
        Check(MacroBookLaw.UniqueSectionName(["Gear Sets"], "Gear Sets") == "Gear Sets 2" &&
              MacroBookLaw.UniqueSectionName([], "   ") == MacroBookLaw.DefaultSectionName &&
              MacroBookLaw.ClampName("  ab\ncd  ", 3) == "ab " &&
              MacroBookLaw.RunnableLines("/cast Fireball\r\n\r\n# c\n  /say pew  \n")
                  .SequenceEqual(["/cast Fireball", "/say pew"]) &&
              MacroBookLaw.StoreFileToken("Hydraxian Waterlords/a") == "Hydraxian_Waterlords_a" &&
              MacroBookLaw.StoreFileToken("") == "unknown" &&
              MacroBookLaw.IconToken(@"Interface\Icons\Foo") == "Foo" &&
              MacroBookLaw.NormalizeIconPath("Foo") == @"Interface\Icons\Foo" &&
              MacroBookLaw.BodyCapacity >= 1000 && MacroBookLaw.MacrosPerBook >= 200 &&
              MacroBookLaw.ChatLineLimit == 255,
            "Macro Book naming/tokenization/limit law drifted");
    }

    private static void CheckRows()
    {
        var sections = new List<MacroBookLaw.StoredSection> { new("S", true) };
        var macros = new List<MacroBookLaw.StoredMacro>
        {
            new(1, "u", "", "", ""),
            new(2, "m", ".additem 5", "", "S"),
        };
        IReadOnlyList<MacroBookLaw.Row> rows = MacroBookLaw.BuildRows(sections, macros, "");
        Check(rows.Count == 2 && rows[0].Kind == MacroBookLaw.RowKind.Macro && !rows[0].Indented &&
              rows[1].Kind == MacroBookLaw.RowKind.Section && rows[1].Collapsed &&
              rows[1].Count == 1,
            "Row projection: ungrouped first, collapsed section hides its macros");
        IReadOnlyList<MacroBookLaw.Row> searched = MacroBookLaw.BuildRows(sections, macros, "m");
        Check(searched.Count == 2 && searched[0].Kind == MacroBookLaw.RowKind.Section &&
              !searched[0].Collapsed && searched[1].MacroId == 2 && searched[1].Indented &&
              MacroBookLaw.BuildRows(sections, macros, "zzz").Count == 0 &&
              MacroBookLaw.BuildRows(sections, macros, "5").Count == 2,
            "Row projection: a search expands sections, matches bodies and hides empty sections");
        Check(MacroBookLaw.MaximumScroll(30, 17) == 13 &&
              MacroBookLaw.ClampScroll(99, 30, 17) == 13 &&
              MacroBookLaw.ScrollToReveal(0, 20, 30, 17) == 4 &&
              MacroBookLaw.ScrollToReveal(10, 3, 30, 17) == 3 &&
              MacroBookLaw.ScrollToReveal(2, 5, 30, 17) == 2,
            "List scroll law drifted");
    }

    private static void CheckGeometry()
    {
        Check(MacroBookUiLaw.FrameSize == new Vector2(640, 512) &&
              MacroBookUiLaw.TitleFont == "GameFontNormal" &&
              MacroBookUiLaw.HeaderPlaque == new MacroBookUiLaw.Rect(192, -12, 256, 64) &&
              MacroBookUiLaw.CloseButton == new MacroBookUiLaw.Rect(604, 4, 32, 32) &&
              MacroBookUiLaw.TabWidth(60) == 77 &&
              MacroBookUiLaw.CharacterTabWidth(400) == 170 - 15 + 32 &&
              MacroBookUiLaw.List == new MacroBookUiLaw.Rect(20, 100, 206, 340) &&
              MacroBookUiLaw.VisibleListRows == 17 &&
              MacroBookUiLaw.ListRow(0) == new MacroBookUiLaw.Rect(20, 100, 206, 20) &&
              MacroBookUiLaw.ListRow(16) == new MacroBookUiLaw.Rect(20, 420, 206, 20) &&
              MacroBookUiLaw.ListScrollBar == new MacroBookUiLaw.Rect(228, 100, 16, 340) &&
              MacroBookUiLaw.NewButton == new MacroBookUiLaw.Rect(20, 448, 70, 22) &&
              MacroBookUiLaw.DeleteButton == new MacroBookUiLaw.Rect(174, 448, 70, 22),
            "Macro Book frame/list geometry drifted");
        Check(MacroBookUiLaw.NameField == new MacroBookUiLaw.Rect(300, 76, 200, 20) &&
              MacroBookUiLaw.IconButton == new MacroBookUiLaw.Rect(588, 72, 36, 36) &&
              MacroBookUiLaw.IconSocket == new MacroBookUiLaw.Rect(574, 59, 64, 64) &&
              MacroBookUiLaw.IconOffset == new Vector2(0, 1) &&
              MacroBookUiLaw.SectionButton == new MacroBookUiLaw.Rect(300, 102, 200, 22) &&
              MacroBookUiLaw.SectionMenu(3) == new MacroBookUiLaw.Rect(300, 126, 200, 60) &&
              MacroBookUiLaw.SectionMenuRow(1) == new MacroBookUiLaw.Rect(303, 147, 194, 18) &&
              MacroBookUiLaw.BodyBackground == new MacroBookUiLaw.Rect(256, 146, 368, 156) &&
              MacroBookUiLaw.BodyEditor == new MacroBookUiLaw.Rect(263, 151, 338, 146) &&
              MacroBookUiLaw.BodyScrollBar == new MacroBookUiLaw.Rect(605, 151, 16, 146) &&
              MacroBookUiLaw.Diagnostics == new MacroBookUiLaw.Rect(256, 308, 368, 42) &&
              MacroBookUiLaw.Shelf == new MacroBookUiLaw.Rect(256, 378, 352, 90) &&
              MacroBookUiLaw.VisibleShelfRows == 5 &&
              MacroBookUiLaw.ShelfRow(4) == new MacroBookUiLaw.Rect(256, 450, 352, 18) &&
              MacroBookUiLaw.RunButton == new MacroBookUiLaw.Rect(256, 478, 110, 22) &&
              MacroBookUiLaw.ExitButton == new MacroBookUiLaw.Rect(514, 478, 110, 22),
            "Macro Book editor-column geometry drifted");
        string overflowBody = new('x', MacroBookLaw.BodyCapacity);
        Check(MacroBookUiLaw.BodyContentHeight("") == 146 &&
              MacroBookUiLaw.BodyContentHeight("one\ntwo") == 146 &&
              MacroBookUiLaw.BodyContentHeight(overflowBody) == 78 * 14 &&
              MacroBookUiLaw.MaximumBodyScroll(overflowBody) == 78 * 14 - 146 &&
              MacroBookUiLaw.BodyScrollSteps(overflowBody) == 34 &&
              MacroBookUiLaw.BodyScrollFromStep(34, overflowBody) == 78 * 14 - 146 &&
              MacroBookUiLaw.BodyScrollStepOf(28, overflowBody) == 1 &&
              MacroBookUiLaw.WheelBodyScroll(0, overflowBody, -1) == 28 &&
              MacroBookUiLaw.ClampBodyScroll(-5, "") == 0,
            "Macro Book editor sizing/scroll law drifted");
        Check(MacroBookUiLaw.IconCell(0) == new MacroBookUiLaw.Rect(270, 160, 36, 36) &&
              MacroBookUiLaw.IconCell(6) == new MacroBookUiLaw.Rect(546, 160, 36, 36) &&
              MacroBookUiLaw.IconCell(41) == new MacroBookUiLaw.Rect(546, 380, 36, 36) &&
              MacroBookUiLaw.IconCellSocket(MacroBookUiLaw.IconCell(0)) ==
                  new MacroBookUiLaw.Rect(256, 147, 64, 64) &&
              MacroBookUiLaw.VisibleIcons == 42 &&
              MacroBookUiLaw.MaximumIconRowOffset(42) == 0 &&
              MacroBookUiLaw.MaximumIconRowOffset(43) == 1 &&
              MacroBookUiLaw.IconCatalogIndex(1, 3, 100) == 10 &&
              MacroBookUiLaw.IconCatalogIndex(20, 41, 100) == -1 &&
              MacroBookUiLaw.FilterIcons([@"Interface\Icons\Spell_Fire", @"Interface\Icons\INV_Sword"],
                  "fire").SequenceEqual([@"Interface\Icons\Spell_Fire"]) &&
              MacroBookUiLaw.IconOkayButton == new MacroBookUiLaw.Rect(256, 432, 80, 22) &&
              MacroBookUiLaw.DragStarted(Vector2.Zero, new Vector2(7, 0), 1f) &&
              !MacroBookUiLaw.DragStarted(Vector2.Zero, new Vector2(5, 0), 1f),
            "Macro Book icon picker geometry/projection drifted");
        Check(MacroBookUiLaw.DiagnosticColor(MacroLintLaw.Severity.Error) == MacroBookUiLaw.ErrorColor &&
              MacroBookUiLaw.DiagnosticText(new MacroLintLaw.Diagnostic(3, MacroLintLaw.Severity.Warning,
                  "x")) == "Line 3: x" &&
              MacroBookUiLaw.SectionSummary("S", 1) == "S: 1 macro" &&
              MacroBookUiLaw.SectionSummary("S", 2) == "S: 2 macros" &&
              MacroBookUiLaw.CountStatus(3, 500) == "3 / 500 macros",
            "Macro Book text law drifted");
    }

    private const string FixtureCatalog =
        "name\tsecurity\trunnable\thas_subcommands\n" +
        "additem\tSEC_GAMEMASTER\t1\t0\n" +
        "npc\tSEC_MODERATOR\t0\t1\n" +
        "npc add\tSEC_DEVELOPER\t1\t0\n" +
        "npc addweapon\tSEC_GAMEMASTER\t1\t0\n" +
        "gm\tSEC_PLAYER\t0\t1\n" +
        "gm\tSEC_TICKETMASTER\t1\t0\n" +
        "gm fly\tSEC_GAMEMASTER\t1\t0\n" +
        "learn\tSEC_DEVELOPER\t1\t0\n" +
        "learn all\tSEC_ADMINISTRATOR\t1\t0\n";

    private static void CheckLint()
    {
        MacroLintLaw.CommandCatalog catalog = MacroLintLaw.Parse(FixtureCatalog);
        MacroLintLaw.ServerCommand gm = catalog.ServerCommands.Single(command => command.Name == "gm");
        Check(catalog.ServerCommands.Count == 8 && gm.Runnable && gm.HasSubcommands &&
              gm.Security == "SEC_TICKETMASTER" &&
              MacroLintLaw.SecurityLabel("SEC_GAMEMASTER") == "GM" &&
              MacroLintLaw.SecurityLabel("SEC_PLAYER") == "" &&
              MacroLintLaw.Search(catalog, "gm", 10).Select(command => command.Name)
                  .SequenceEqual(["gm", "gm fly"]) &&
              MacroLintLaw.Search(catalog, "all", 10).Select(command => command.Name)
                  .SequenceEqual(["learn all"]) &&
              MacroLintLaw.Search(catalog, "", 100).All(command => command.Name != "npc") &&
              MacroLintLaw.InsertionText(gm) == ".gm ",
            "Command catalog parse/merge/search law drifted");
        Check(Resolve(catalog, "additem 14460") is { State: MacroLintLaw.MatchState.Resolved, Resolved: "additem", Arguments: "14460" } &&
              Resolve(catalog, "addi 14460") is { State: MacroLintLaw.MatchState.Resolved, Resolved: "additem" } &&
              Resolve(catalog, "npc") is { State: MacroLintLaw.MatchState.NeedsSubcommand, Resolved: "npc" } &&
              Resolve(catalog, "npc add 1") is { State: MacroLintLaw.MatchState.Resolved, Resolved: "npc add", Arguments: "1" } &&
              Resolve(catalog, "npc ad 1") is { State: MacroLintLaw.MatchState.Ambiguous } &&
              Resolve(catalog, "gm on") is { State: MacroLintLaw.MatchState.Resolved, Resolved: "gm", Arguments: "on" } &&
              Resolve(catalog, "gm f") is { State: MacroLintLaw.MatchState.Resolved, Resolved: "gm fly" } &&
              Resolve(catalog, "bogus 1") is { State: MacroLintLaw.MatchState.Unknown, Detail: "bogus" },
            "Server command resolution (exact / unique prefix / group / ambiguous) drifted");
        string body = string.Join('\n',
        [
            ".additem 14460",          // 1 clean
            ".additem",                // 2 error: needs id
            ".additem x",              // 3 error: not an id
            ".aditem 1",               // 4 warning: unknown
            "/cast",                   // 5 error: needs spell
            "/cast Fireball",          // 6 clean (known)
            "/frobnicate",             // 7 warning: unknown slash
            "hello there",             // 8 info: /say
            "# note",                  // 9 clean
            ".additem <item id>",      // 10 error: placeholder
            "/cast Frostbolt",         // 11 warning: unknown spell
            new string('y', 300),      // 12 error: too long (then info: /say)
            "/wave",                   // 13 clean (emote)
            "/w Bob",                  // 14 warning: needs message
        ]);
        IReadOnlyList<MacroLintLaw.Diagnostic> lint = MacroLintLaw.Lint(body, catalog,
            spellKnown: name => name == "Fireball", itemKnown: _ => true);
        Check(!lint.Any(d => d.Line is 1 or 6 or 9 or 13) &&
              Has(lint, 2, MacroLintLaw.Severity.Error) && Has(lint, 3, MacroLintLaw.Severity.Error) &&
              Has(lint, 4, MacroLintLaw.Severity.Warning) && Has(lint, 5, MacroLintLaw.Severity.Error) &&
              Has(lint, 7, MacroLintLaw.Severity.Warning) && Has(lint, 8, MacroLintLaw.Severity.Info) &&
              Has(lint, 10, MacroLintLaw.Severity.Error) && Has(lint, 11, MacroLintLaw.Severity.Warning) &&
              Has(lint, 12, MacroLintLaw.Severity.Error) && Has(lint, 14, MacroLintLaw.Severity.Warning) &&
              lint.Single(d => d.Line == 10).Message == "Fill in <item id>." &&
              MacroLintLaw.Lint("", catalog).Count == 0,
            "Macro linter verdicts drifted");
        Check(MacroLintLaw.ClientVerbs.Contains("/cast") && MacroLintLaw.ClientVerbs.Contains("/use") &&
              MacroLintLaw.ClientVerbs.Contains("/startattack") &&
              !MacroLintLaw.ClientVerbs.Contains("/target") &&
              MacroLintLaw.IsItemArgument("14460") && MacroLintLaw.IsItemArgument("[Foo]") &&
              !MacroLintLaw.IsItemArgument("0") && !MacroLintLaw.IsItemArgument("abc"),
            "Client verb roster / item-argument law drifted");
    }

    private static MacroLintLaw.Match Resolve(MacroLintLaw.CommandCatalog catalog, string line) =>
        MacroLintLaw.ResolveServerCommand(catalog, line);

    private static bool Has(IReadOnlyList<MacroLintLaw.Diagnostic> lint, int line,
        MacroLintLaw.Severity severity) =>
        lint.Any(d => d.Line == line && d.Severity == severity);

    private static void CheckEmbeddedCatalogAndTemplates()
    {
        MacroLintLaw.CommandCatalog catalog = MacroLintLaw.LoadEmbedded();
        Check(catalog.ServerCommands.Count > 800 &&
              catalog.ServerCommands.Any(command => command.Name == "additem" && command.Runnable) &&
              catalog.ServerCommands.Any(command => command.Name == "reload creature_template"),
            "Embedded vmangos command export missing or thin (Data/vmangos-commands.tsv; " +
            "regenerate with tools/macro-commands/export-commands.py)");
        foreach (string name in MacroTemplateLaw.ServerCommandNames())
        {
            MacroLintLaw.Match match = MacroLintLaw.ResolveServerCommand(catalog, name);
            Check(match.State == MacroLintLaw.MatchState.Resolved,
                $"Template command '.{name}' does not resolve on the Core ({match.State})");
        }
        Check(MacroTemplateLaw.All.Count >= 20 &&
              MacroTemplateLaw.Search("gear").Any(template => template.Name == "Gear kit") &&
              MacroTemplateLaw.Search("").Count == MacroTemplateLaw.All.Count &&
              MacroTemplateLaw.TryAppend("a", ["b"], 10, out string joined) && joined == "a\nb" &&
              MacroTemplateLaw.TryAppend("a\n", ["b", "c"], 10, out joined) && joined == "a\nb\nc" &&
              MacroTemplateLaw.TryAppend("", ["b"], 10, out joined) && joined == "b" &&
              !MacroTemplateLaw.TryAppend("abc", ["defgh"], 5, out joined) && joined == "abc",
            "Template shelf search/append law drifted");
        // Every template with a placeholder is refused by the linter until filled in.
        foreach (MacroTemplateLaw.Template template in MacroTemplateLaw.All)
        {
            string body = string.Join('\n', template.Lines);
            bool placeholder = template.Lines.Any(line =>
                line.Contains('<') && !line.StartsWith('#'));
            IReadOnlyList<MacroLintLaw.Diagnostic> lint = MacroLintLaw.Lint(body, catalog);
            Check(placeholder == lint.Any(d => d.Severity == MacroLintLaw.Severity.Error) &&
                  !lint.Any(d => d.Severity == MacroLintLaw.Severity.Warning),
                $"Template '{template.Name}' lints unexpectedly");
        }
    }

    private static void CheckArchiveCatalogLaw()
    {
        string[] listed =
        [
            @"Interface\Icons\Spell_Fire.blp",
            @"interface/icons/ability_Z.tga",
            @"Interface\Icons\Ability_Z.blp",
            @"Interface\Icons\Ability_Druid_Mangle.tga.blp",
            @"Interface\Icons\INV_Sword_04.blp",
            @"Interface\Icons\Sub\Spell_Bad.blp",
            @"Interface\Icons\Spell_NotTexture.txt",
            @"Other\Icons\Ability_Bad.blp",
        ];
        IReadOnlyList<string> icons = MacroIconCatalog.Build(listed);
        Check(icons.SequenceEqual(
            [
                @"Interface\Icons\Ability_Druid_Mangle.tga",
                @"Interface\Icons\ability_Z",
                @"Interface\Icons\Spell_Fire",
            ], StringComparer.OrdinalIgnoreCase),
            "Macro chooser did not apply the archive prefix/extension/subdirectory/filter/sort/dedup law");
    }

    private static void CheckRuntimeSourceFence()
    {
        string root = ClientConfig.FindRepoRoot();
        string client = Path.Combine(root, "MSUIClient");
        string macro = File.ReadAllText(Path.Combine(client, "GameLoop", "Panels",
            "GameLoop.Macro.cs"));
        string bars = File.ReadAllText(Path.Combine(client, "GameLoop", "Hud",
            "GameLoop.ActionBars.cs"));
        string mount = File.ReadAllText(Path.Combine(client, "Formats", "MpqMount.cs"));
        string csproj = File.ReadAllText(Path.Combine(client, "MSUIClient.csproj"));
        Check(macro.Contains("UiPanelFrameLogicalOrigin(UiPanelOwnershipRegistry[15])", StringComparison.Ordinal) &&
              macro.Contains("movable: true", StringComparison.Ordinal) &&
              macro.Contains("WowSkin.Dialog", StringComparison.Ordinal) &&
              macro.Contains("MacroBookUiLaw.HeaderArt", StringComparison.Ordinal) &&
              macro.Contains("VanillaInsetTab(dl, \"##macro-general-tab\"", StringComparison.Ordinal) &&
              macro.Contains("MacroBookLaw.BuildRows(", StringComparison.Ordinal) &&
              macro.Contains("DrawVanillaScrollBar(dl, \"##macro-list-scroll\"", StringComparison.Ordinal) &&
              macro.Contains("VanillaBareMultilineText(\"##macro-text\"", StringComparison.Ordinal) &&
              macro.Contains("DrawVanillaScrollBar(dl, \"##macro-body-scroll\"", StringComparison.Ordinal) &&
              macro.Contains("MacroLintLaw.Lint(", StringComparison.Ordinal) &&
              macro.Contains("MacroTemplateLaw.TryAppend(", StringComparison.Ordinal) &&
              macro.Contains("MacroLintLaw.Search(MacroCommandCatalog", StringComparison.Ordinal) &&
              macro.Contains("MacroBookLaw.AllocateId(", StringComparison.Ordinal) &&
              macro.Contains("MacroBookLaw.RunnableLines(macro.Body)", StringComparison.Ordinal) &&
              macro.Contains("SubmitChatLine(line)", StringComparison.Ordinal) &&
              macro.Contains("MacroBookLaw.ParseStore(", StringComparison.Ordinal) &&
              macro.Contains("MacroBookLaw.WriteStore(", StringComparison.Ordinal) &&
              macro.Contains("\"account.txt\"", StringComparison.Ordinal) &&
              macro.Contains("FileOptions.WriteThrough", StringComparison.Ordinal) &&
              macro.Contains("File.Move(temporary, path, overwrite: true)", StringComparison.Ordinal) &&
              macro.Contains("MacroIconCatalog.Load(_mpq)", StringComparison.Ordinal) &&
              // Commit only from buffers that mirror a real macro (owner report 2026-09-03).
              macro.Contains("if (!_macrosLoaded || _macroEditorBoundId == 0) return;", StringComparison.Ordinal) &&
              macro.Contains("stored.Legacy && stored.Macros.Count > 0", StringComparison.Ordinal) &&
              !macro.Contains("ImGui.InputText", StringComparison.Ordinal) &&
              !macro.Contains("BeginPopupModal", StringComparison.Ordinal) &&
              !macro.Contains("ImGui.OpenPopup", StringComparison.Ordinal),
            "Macro Book escaped its law-owned store/geometry/linter seams");
        Check(bars.Contains("MacroName(action.ActionId)", StringComparison.Ordinal) &&
              !bars.Contains("_macros[", StringComparison.Ordinal) &&
              bars.Contains("new ActionSlot(ActionSlot.Macro, _draggingMacroId)", StringComparison.Ordinal),
            "Action bars must resolve macro titles by stable id, never by list position");
        Check(csproj.Contains("MSUIClient.Data.vmangos-commands.tsv", StringComparison.Ordinal) &&
              File.Exists(Path.Combine(client, "Data", "vmangos-commands.tsv")) &&
              File.Exists(Path.Combine(root, "tools", "macro-commands", "export-commands.py")) &&
              mount.Contains("archive.ReadFile(\"(listfile)\")", StringComparison.Ordinal),
            "Command export must be embedded and regenerable (Data/vmangos-commands.tsv)");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
