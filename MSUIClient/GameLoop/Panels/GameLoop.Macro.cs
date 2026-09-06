using System.Numerics;
using System.Text;
using System.Text.Json;
using ImGuiNET;
using MSUIClient.Engine;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

/// <summary>
/// The Macro Book: the reimagined macro window (owner decision 2026-09-04, Discord thread with
/// MightyDorf and cam). Two books (account / character), collapsible sections, a 4000-character
/// editor with a linter strip, a reference shelf of templates and the Core's command tree, Run
/// in place, and the same drag-to-hotbar as before. Stable macro ids are the action-bar identity;
/// see <see cref="MacroBookLaw"/> for the id ranges and the legacy migration.
/// </summary>
public sealed partial class GameLoop
{
    private sealed class MacroDefinition
    {
        public uint Id { get; set; }
        public string Name { get; set; } = "";
        public string Body { get; set; } = "";
        public string IconPath { get; set; } = "";
        public string Section { get; set; } = "";
    }

    private sealed class MacroSection
    {
        public string Name { get; set; } = "";
        public bool Collapsed { get; set; }
    }

    private sealed class MacroBook(MacroBookLaw.Scope scope)
    {
        public MacroBookLaw.Scope Scope { get; } = scope;
        public List<MacroSection> Sections { get; } = [];
        public List<MacroDefinition> Macros { get; } = [];
        public uint NextId { get; set; } = MacroBookLaw.FirstId(scope);

        public MacroDefinition? Find(uint id) => Macros.FirstOrDefault(macro => macro.Id == id);

        public MacroSection? FindSection(string name) => name.Length == 0 ? null :
            Sections.FirstOrDefault(section =>
                section.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        public IReadOnlyList<MacroBookLaw.StoredSection> StoredSections() => Sections
            .Select(section => new MacroBookLaw.StoredSection(section.Name, section.Collapsed))
            .ToArray();

        public IReadOnlyList<MacroBookLaw.StoredMacro> StoredMacros() => Macros
            .Select(macro => new MacroBookLaw.StoredMacro(macro.Id, macro.Name, macro.Body,
                macro.IconPath, macro.Section))
            .ToArray();

        public MacroBookLaw.StoredBook ToStored() =>
            new(StoredSections(), StoredMacros(), NextId, Legacy: false);

        public void Load(MacroBookLaw.StoredBook stored)
        {
            Sections.Clear();
            Macros.Clear();
            foreach (MacroBookLaw.StoredSection section in stored.Sections)
                Sections.Add(new MacroSection { Name = section.Name, Collapsed = section.Collapsed });
            foreach (MacroBookLaw.StoredMacro macro in stored.Macros)
                Macros.Add(new MacroDefinition
                {
                    Id = macro.Id,
                    Name = macro.Name,
                    Body = macro.Body,
                    IconPath = macro.IconPath,
                    Section = macro.Section,
                });
            NextId = stored.NextId;
        }
    }

    private bool _macroOpen;
    private readonly MacroBook[] _macroBooks =
        [new(MacroBookLaw.Scope.Account), new(MacroBookLaw.Scope.Character)];
    private bool _macrosLoaded;
    private string _loadedMacroAccountPath = "";
    private string _loadedMacroCharacterPath = "";
    private bool _macroCharacterSpecific;
    private uint _selectedMacroId;
    private bool _macroSectionSelected;
    private string _selectedMacroSection = "";
    private readonly byte[] _macroName = new byte[MacroBookLaw.NameCapacity + 1];
    private string _macroBody = "";
    /// <summary>
    /// The macro whose name/body the editor buffers mirror, or 0. CommitMacroEditor copies the
    /// buffers BACK into that macro; committing from buffers that were never seeded (an
    /// action-bar press before the book was ever opened, a store path change at login) wiped a
    /// macro in memory and the next save wrote that out. Owner report 2026-09-03.
    /// </summary>
    private uint _macroEditorBoundId;
    private float _macroBodyScroll;
    private readonly byte[] _macroSearch = new byte[64];
    private int _macroListScroll;
    private uint _pressedMacroId;
    private uint _draggingMacroId;
    private Vector2 _macroPressPosition;
    private string _pressedSectionName = "";
    private string _draggingSectionName = "";
    private Vector2 _macroSectionPressPosition;
    /// <summary>Where the current drag would land, from the last list draw. The hotbar's
    /// FinishActionDrag runs BEFORE the book each frame, so a macro release is answered from
    /// here (one frame stale, which is a pixel or two).</summary>
    private MacroBookLaw.Drop _macroDrop = MacroBookLaw.Drop.None;
    private uint _macroDeletePendingId;
    private bool _macroSectionMenuOpen;
    private bool _macroIconPickerOpen;
    private readonly byte[] _macroIconFilter = new byte[64];
    private string _macroIconFilterApplied = "";
    private IReadOnlyList<string> _macroIconsFiltered = [];
    private int _macroIconRowOffset;
    private int _macroIconPickerSelection = -1;
    private IReadOnlyList<string> _macroIcons = [];
    private bool _macroIconsLoaded;
    private bool _macroShelfCommands;
    private readonly byte[] _macroShelfFilter = new byte[64];
    private int _macroShelfScroll;
    private MacroLintLaw.CommandCatalog? _macroCommandCatalog;
    private string? _macroLintedBody;
    private IReadOnlyList<MacroLintLaw.Diagnostic> _macroLint = [];
    private string _macroStatus = "";

    private MacroBook CurrentMacroBook => _macroBooks[_macroCharacterSpecific ? 1 : 0];

    private MacroBook MacroBookOf(uint id) =>
        _macroBooks[MacroBookLaw.ScopeOfId(id) == MacroBookLaw.Scope.Character ? 1 : 0];

    private MacroDefinition? FindMacro(uint id) => id == 0 ? null : MacroBookOf(id).Find(id);

    private MacroDefinition? SelectedMacro =>
        _macroSectionSelected ? null : CurrentMacroBook.Find(_selectedMacroId);

    private void OpenMacros()
    {
        EnsureMacrosLoaded();
        if (SelectedMacro is null && !_macroSectionSelected) SelectFirstMacro();
        if (!_macroOpen) PlayUiSound(MacroBookUiLaw.OpenSound, "ui.macro");
        _macroOpen = true;
    }

    private void CloseMacros(bool playSound = true)
    {
        if (!_macroOpen) return;
        _macroSectionMenuOpen = false;
        _macroIconPickerOpen = false;
        _draggingSectionName = "";
        _pressedSectionName = "";
        _macroDrop = MacroBookLaw.Drop.None;
        SaveMacros();
        _macroOpen = false;
        if (playSound) PlayUiSound(MacroBookUiLaw.CloseSound, "ui.macro");
    }

    // ── store ────────────────────────────────────────────────────────────────────────────

    private void EnsureMacrosLoaded()
    {
        (string accountPath, string characterPath) = MacroStorePaths();
        if (_macrosLoaded && _loadedMacroAccountPath == accountPath &&
            _loadedMacroCharacterPath == characterPath) return;
        if (_macrosLoaded)
        {
            CommitMacroEditor();
            TryWriteMacroStores(_loadedMacroAccountPath, _loadedMacroCharacterPath);
        }
        _macroEditorBoundId = 0;
        _selectedMacroId = 0;
        _macroSectionSelected = false;
        _selectedMacroSection = "";
        _macroListScroll = 0;
        bool rewrite = false;
        try
        {
            bool mayReadLegacyJson = _net?.IsInWorld == true && MacroStore.MigrateLegacy(
                _config.RepoRoot, _net.AccountStorageKey, _net.RealmName, _net.PlayerName);
            bool anyStore = File.Exists(accountPath) || File.Exists(characterPath);
            rewrite |= LoadMacroBook(_macroBooks[0], accountPath);
            rewrite |= LoadMacroBook(_macroBooks[1], characterPath);
            if (!anyStore)
            {
                // The oldest store: macros.json, 36 positional entries (account 18 + character 18).
                string legacyPath = Path.Combine(_config.RepoRoot, "macros.json");
                if (mayReadLegacyJson && File.Exists(legacyPath))
                {
                    List<MacroDefinition> legacy = JsonSerializer.Deserialize<List<MacroDefinition>>(
                        File.ReadAllText(legacyPath)) ?? [];
                    for (int index = 0; index < legacy.Count; index++)
                    {
                        MacroDefinition macro = legacy[index];
                        if (macro.Name.Length == 0 && macro.Body.Length == 0) continue;
                        bool character = index >= MacroBookLaw.LegacyMacrosPerSet;
                        MacroBook book = _macroBooks[character ? 1 : 0];
                        int slot = character ? index - MacroBookLaw.LegacyMacrosPerSet : index;
                        if (slot >= MacroBookLaw.LegacyMacrosPerSet) break;
                        macro.Id = MacroBookLaw.LegacyFirstId(book.Scope) + (uint)slot;
                        macro.Section = "";
                        book.Macros.Add(macro);
                        rewrite = true;
                    }
                }
            }
        }
        catch (Exception ex) { Console.WriteLine($"[macros] load failed: {ex.Message}"); }
        _macrosLoaded = true;
        _loadedMacroAccountPath = accountPath;
        _loadedMacroCharacterPath = characterPath;
        if (rewrite) TryWriteMacroStores(accountPath, characterPath);
    }

    /// <summary>Returns true when the file was a legacy shape that should be rewritten.</summary>
    private static bool LoadMacroBook(MacroBook book, string path)
    {
        if (!File.Exists(path))
        {
            book.Load(MacroBookLaw.StoredBook.Empty(book.Scope));
            return false;
        }
        MacroBookLaw.StoredBook stored = MacroBookLaw.ParseStore(File.ReadAllText(path),
            book.Scope);
        book.Load(stored);
        return stored.Legacy && stored.Macros.Count > 0;
    }

    private void SaveMacros()
    {
        EnsureMacrosLoaded();
        CommitMacroEditor();
        TryWriteMacroStores(_loadedMacroAccountPath, _loadedMacroCharacterPath);
    }

    private (string AccountPath, string CharacterPath) MacroStorePaths()
    {
        string accountKey;
        if (_net is not null) accountKey = _net.AccountStorageKey;
        else
        {
            NetSettings settings = _config.ToNetSettings();
            accountKey = MacroStore.AccountKey(settings.RealmdHost, settings.RealmdPort, settings.Account);
        }
        return MacroStore.Paths(_config.RepoRoot, accountKey,
            _net?.RealmName ?? "Realm", _net?.PlayerName ?? "Character");
    }

    private void TryWriteMacroStores(string accountPath, string characterPath)
    {
        try
        {
            WriteMacroStoreAtomic(accountPath, _macroBooks[0]);
            WriteMacroStoreAtomic(characterPath, _macroBooks[1]);
        }
        catch (Exception ex) { Console.WriteLine($"[macros] save failed: {ex.Message}"); }
    }

    private static void WriteMacroStoreAtomic(string path, MacroBook book)
    {
        string text = MacroBookLaw.WriteStore(book.ToStored());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write,
                   FileShare.None, 4096, FileOptions.WriteThrough))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            writer.Write(text);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }

    private void EnsureMacroIconsLoaded()
    {
        if (_macroIconsLoaded) return;
        _macroIconsLoaded = true;
        try
        {
            if (_mpq is not null) _macroIcons = MacroIconCatalog.Load(_mpq);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[macros] icon catalog failed: {ex.Message}");
            _macroIcons = [];
        }
        _macroIconsFiltered = _macroIcons;
        _macroIconFilterApplied = "";
    }

    private MacroLintLaw.CommandCatalog MacroCommandCatalog =>
        _macroCommandCatalog ??= MacroLintLaw.LoadEmbedded();

    // ── selection / editing ──────────────────────────────────────────────────────────────

    private void SelectMacro(uint id)
    {
        EnsureMacrosLoaded();
        CommitMacroEditor();
        MacroDefinition? macro = CurrentMacroBook.Find(id);
        _macroSectionMenuOpen = false;
        _macroIconPickerOpen = false;
        _macroSectionSelected = false;
        _selectedMacroSection = "";
        if (macro is null)
        {
            _selectedMacroId = 0;
            _macroEditorBoundId = 0;
            Array.Clear(_macroName);
            _macroBody = "";
            return;
        }
        _selectedMacroId = macro.Id;
        Array.Clear(_macroName);
        WriteBuffer(_macroName, macro.Name);
        _macroBody = macro.Body;
        _macroBodyScroll = 0;
        _macroEditorBoundId = macro.Id;
    }

    private void SelectSection(string name)
    {
        EnsureMacrosLoaded();
        CommitMacroEditor();
        _macroSectionMenuOpen = false;
        _macroIconPickerOpen = false;
        _macroEditorBoundId = 0;
        _selectedMacroId = 0;
        _macroSectionSelected = true;
        _selectedMacroSection = name;
        Array.Clear(_macroName);
        WriteBuffer(_macroName, name);
        _macroBody = "";
    }

    private void SelectFirstMacro()
    {
        MacroBook book = CurrentMacroBook;
        IReadOnlyList<MacroBookLaw.Row> rows = MacroBookLaw.BuildRows(book.StoredSections(),
            book.StoredMacros(), "");
        MacroBookLaw.Row first = rows.FirstOrDefault(row => row.Kind == MacroBookLaw.RowKind.Macro);
        if (first.MacroId != 0) SelectMacro(first.MacroId);
        else if (rows.Count > 0) SelectSection(rows[0].Section);
        else SelectMacro(0);
    }

    private void CommitMacroEditor()
    {
        if (!_macrosLoaded || _macroEditorBoundId == 0) return;
        MacroDefinition? macro = FindMacro(_macroEditorBoundId);
        if (macro is null) return;
        string name = MacroBookLaw.ClampName(ReadBuffer(_macroName), MacroBookLaw.NameCapacity);
        macro.Name = name.Length == 0 ? MacroBookLaw.DefaultMacroName : name;
        macro.Body = _macroBody.Length <= MacroBookLaw.BodyCapacity
            ? _macroBody : _macroBody[..MacroBookLaw.BodyCapacity];
    }

    private void CreateMacro()
    {
        MacroBook book = CurrentMacroBook;
        if (book.Macros.Count >= MacroBookLaw.MacrosPerBook) return;
        CommitMacroEditor();
        string section = _macroSectionSelected ? _selectedMacroSection
            : SelectedMacro?.Section ?? "";
        var macro = new MacroDefinition
        {
            Id = MacroBookLaw.AllocateId(book.Scope, book.NextId,
                book.Macros.Select(existing => existing.Id)),
            Name = MacroBookLaw.DefaultMacroName,
            IconPath = MacroBookLaw.DefaultIconPath,
            Section = section,
        };
        book.NextId = macro.Id + 1;
        book.Macros.Add(macro);
        if (book.FindSection(section) is { } owner) owner.Collapsed = false;
        SelectMacro(macro.Id);
        SaveMacros();
        _macroStatus = "";
        PlayUiSound(MacroBookUiLaw.ClickSound, "ui.macro");
    }

    private void CreateSection()
    {
        MacroBook book = CurrentMacroBook;
        CommitMacroEditor();
        string name = MacroBookLaw.UniqueSectionName(
            book.Sections.Select(section => section.Name), MacroBookLaw.DefaultSectionName);
        book.Sections.Add(new MacroSection { Name = name });
        SelectSection(name);
        SaveMacros();
        PlayUiSound(MacroBookUiLaw.ClickSound, "ui.macro");
    }

    private void RenameSelectedSection(string requested)
    {
        if (!_macroSectionSelected) return;
        MacroBook book = CurrentMacroBook;
        MacroSection? section = book.FindSection(_selectedMacroSection);
        if (section is null) return;
        string name = MacroBookLaw.UniqueSectionName(
            book.Sections.Where(other => other != section).Select(other => other.Name), requested);
        if (name == section.Name) return;
        foreach (MacroDefinition macro in book.Macros)
            if (macro.Section.Equals(section.Name, StringComparison.OrdinalIgnoreCase))
                macro.Section = name;
        section.Name = name;
        _selectedMacroSection = name;
    }

    /// <summary>Delete: a section ungroups its macros at once (nothing is lost); a macro is
    /// asked about first through the stock StaticPopup and deleted by ConfirmDeleteMacro.</summary>
    private void RequestDeleteSelection()
    {
        MacroBook book = CurrentMacroBook;
        if (_macroSectionSelected)
        {
            MacroSection? section = book.FindSection(_selectedMacroSection);
            if (section is null) return;
            foreach (MacroDefinition macro in book.Macros)
                if (macro.Section.Equals(section.Name, StringComparison.OrdinalIgnoreCase))
                    macro.Section = "";
            book.Sections.Remove(section);
            _macroEditorBoundId = 0;
            SelectFirstMacro();
            SaveMacros();
            PlayUiSound(MacroBookUiLaw.ClickSound, "ui.macro");
            return;
        }
        MacroDefinition? selected = SelectedMacro;
        if (selected is null) return;
        CommitMacroEditor();
        _macroDeletePendingId = selected.Id;
        bool dead = _entities.TryGet(ControlledGuid, out WorldEntity player) && player.IsDead;
        ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Show(_staticPopupSlots,
            ConfirmPopupUiLaw.DeleteMacroDefinition, dead, dataToken: selected.Name));
    }

    /// <summary>The popup's Delete. The macro may have been re-selected or deleted meanwhile,
    /// so it is looked up again by id.</summary>
    private void ConfirmDeleteMacro()
    {
        uint id = _macroDeletePendingId;
        _macroDeletePendingId = 0;
        if (id == 0) return;
        MacroBook book = MacroBookOf(id);
        MacroDefinition? macro = book.Find(id);
        if (macro is null) return;
        if (_macroEditorBoundId == id) _macroEditorBoundId = 0;
        book.Macros.Remove(macro);
        if (_selectedMacroId == id && book == CurrentMacroBook) SelectFirstMacro();
        SaveMacros();
        _macroStatus = $"Deleted {macro.Name}.";
        PlayUiSound(MacroBookUiLaw.ClickSound, "ui.macro");
    }

    // ── drag and drop inside the book ────────────────────────────────────────────────────

    /// <summary>Called by the hotbar's FinishActionDrag when a macro is released anywhere but a
    /// bar slot: the book's last computed drop target decides whether it was a move.</summary>
    private void TryDropDraggedMacroInBook(uint id)
    {
        MacroBookLaw.Drop drop = _macroDrop;
        _macroDrop = MacroBookLaw.Drop.None;
        if (!_macroOpen || id == 0 || drop.Kind == MacroBookLaw.DropKind.None) return;
        MacroBook book = CurrentMacroBook;
        MacroDefinition? macro = book.Find(id);
        if (macro is null) return;
        switch (drop.Kind)
        {
            case MacroBookLaw.DropKind.BesideMacro:
                if (drop.MacroId == id) return;
                macro.Section = drop.Section;
                ApplyMacroOrder(book, MacroBookLaw.ReorderBeside(
                    book.Macros.Select(existing => existing.Id).ToArray(), id, drop.MacroId,
                    drop.After));
                break;
            case MacroBookLaw.DropKind.IntoSection:
                macro.Section = drop.Section;
                book.Macros.Remove(macro);
                book.Macros.Add(macro);
                if (book.FindSection(drop.Section) is { } owner) owner.Collapsed = false;
                break;
            case MacroBookLaw.DropKind.Ungrouped:
                macro.Section = "";
                book.Macros.Remove(macro);
                book.Macros.Add(macro);
                break;
            default:
                return;
        }
        SaveMacros();
        _macroStatus = drop.Section.Length == 0 ? $"Moved {macro.Name}."
            : $"Moved {macro.Name} to {drop.Section}.";
        PlayUiSound(MacroBookUiLaw.ClickSound, "ui.macro");
    }

    private static void ApplyMacroOrder(MacroBook book, IReadOnlyList<uint> order)
    {
        Dictionary<uint, MacroDefinition> byId = book.Macros.ToDictionary(macro => macro.Id);
        book.Macros.Clear();
        foreach (uint id in order)
            if (byId.Remove(id, out MacroDefinition? macro)) book.Macros.Add(macro);
        book.Macros.AddRange(byId.Values);
    }

    private void ApplySectionDrop(MacroBookLaw.Drop drop)
    {
        MacroBook book = CurrentMacroBook;
        if (drop.Kind != MacroBookLaw.DropKind.BesideSection) return;
        IReadOnlyList<string> order = MacroBookLaw.ReorderSectionBeside(
            book.Sections.Select(section => section.Name).ToArray(), _draggingSectionName,
            drop.Section, drop.After);
        Dictionary<string, MacroSection> byName = book.Sections.ToDictionary(
            section => section.Name, StringComparer.OrdinalIgnoreCase);
        book.Sections.Clear();
        foreach (string name in order)
            if (byName.Remove(name, out MacroSection? section)) book.Sections.Add(section);
        book.Sections.AddRange(byName.Values);
        SaveMacros();
        PlayUiSound(MacroBookUiLaw.ClickSound, "ui.macro");
    }

    private static void DrawMacroDropIndicator(ImDrawListPtr dl, Vector2 min, Vector2 size,
        MacroBookLaw.Drop drop, float s)
    {
        float thickness = MacroBookUiLaw.DropLineThickness * s;
        switch (drop.Kind)
        {
            case MacroBookLaw.DropKind.BesideMacro:
            case MacroBookLaw.DropKind.BesideSection:
            case MacroBookLaw.DropKind.Ungrouped:
                float y = drop.After || drop.Kind == MacroBookLaw.DropKind.Ungrouped
                    ? min.Y + size.Y : min.Y;
                dl.AddRectFilled(new Vector2(min.X, y - thickness * .5f),
                    new Vector2(min.X + size.X, y + thickness * .5f), MacroBookUiLaw.DropLineColor);
                break;
            case MacroBookLaw.DropKind.IntoSection:
                dl.AddRect(min, min + size, MacroBookUiLaw.DropLineColor, 0f, ImDrawFlags.None,
                    thickness);
                break;
        }
    }

    private void MoveSelectedMacroToSection(string section)
    {
        MacroDefinition? macro = SelectedMacro;
        if (macro is null) return;
        macro.Section = section;
        if (CurrentMacroBook.FindSection(section) is { } owner) owner.Collapsed = false;
        SaveMacros();
    }

    private void ToggleSectionCollapsed(string name)
    {
        if (CurrentMacroBook.FindSection(name) is not { } section) return;
        section.Collapsed = !section.Collapsed;
        PlayUiSound(MacroBookUiLaw.ClickSound, "ui.macro");
    }

    private void SwitchMacroSet(bool characterSpecific)
    {
        if (_macroCharacterSpecific == characterSpecific) return;
        CommitMacroEditor();
        _macroCharacterSpecific = characterSpecific;
        _macroListScroll = 0;
        _macroSectionMenuOpen = false;
        _macroIconPickerOpen = false;
        SelectFirstMacro();
    }

    private string MacroCharacterTabLabel()
    {
        ulong guid = _net?.PlayerGuid ?? 0;
        string name = _playerNames.GetValueOrDefault(guid, "Character");
        return string.Format(MacroBookUiLaw.CharacterTabFormat, name);
    }

    private bool TryAppendMacroLines(IEnumerable<string> lines)
    {
        if (SelectedMacro is null) return false;
        if (!MacroTemplateLaw.TryAppend(_macroBody, lines, MacroBookLaw.BodyCapacity,
                out string result))
        {
            _macroStatus = "The macro is full.";
            return false;
        }
        _macroBody = result;
        _macroBodyScroll = MacroBookUiLaw.MaximumBodyScroll(_macroBody);
        return true;
    }

    private void EnsureMacroLint()
    {
        if (ReferenceEquals(_macroLintedBody, _macroBody)) return;
        _macroLintedBody = _macroBody;
        _macroLint = MacroLintLaw.Lint(_macroBody, MacroCommandCatalog,
            spellKnown: name => _spellCatalog is null ||
                _spellCatalog.FindKnownByName(name, _actions.KnownSpells) is not null,
            itemKnown: name => _items is null || _items.FindByName(name) is not null);
    }

    // ── action-bar side ──────────────────────────────────────────────────────────────────

    private void ExecuteMacro(uint id)
    {
        EnsureMacrosLoaded();
        CommitMacroEditor();
        MacroDefinition? macro = FindMacro(id);
        if (macro is null) return;
        IReadOnlyList<string> lines = MacroBookLaw.RunnableLines(macro.Body);
        foreach (string line in lines) SubmitChatLine(line);
        _macroStatus = lines.Count == 1 ? $"Ran {macro.Name}: 1 line."
            : $"Ran {macro.Name}: {lines.Count} lines.";
    }

    private string MacroName(uint id) => FindMacro(id)?.Name ?? "Macro";

    private string MacroIcon(uint id)
    {
        EnsureMacrosLoaded();
        MacroDefinition? macro = FindMacro(id);
        if (macro is null) return MacroBookLaw.DefaultIconPath;
        if (!string.IsNullOrWhiteSpace(macro.IconPath)) return macro.IconPath;
        foreach (string raw in macro.Body.Replace("\r", "").Split('\n'))
        {
            string line = raw.Trim();
            int separator = line.IndexOf(' ');
            string command = separator < 0 ? line : line[..separator];
            if (command.Equals("/cast", StringComparison.OrdinalIgnoreCase) ||
                command.Equals("/spell", StringComparison.OrdinalIgnoreCase))
            {
                string name = separator < 0 ? "" : line[(separator + 1)..].Trim();
                if (_spellCatalog?.FindKnownByName(name, _actions.KnownSpells) is { } spell)
                {
                    WorldEntity? player = _net is not null && _entities.TryGet(_net.PlayerGuid,
                        out WorldEntity owner) ? owner : null;
                    return ResolveSpellActionIcon(spell, player);
                }
            }
            if (line.StartsWith("/use ", StringComparison.OrdinalIgnoreCase) &&
                _items?.FindByName(line[5..].Trim()) is { } item) return item.IconPath;
        }
        return MacroBookLaw.DefaultIconPath;
    }

    // ── drawing ──────────────────────────────────────────────────────────────────────────

    private void DrawMacroFrame()
    {
        if (!_macroOpen || _gameplayArt is null) return;
        EnsureMacrosLoaded();
        // Mirror the editor into its macro every frame so the list shows a name as it is typed;
        // the bound-id guard keeps this from ever writing unseeded buffers.
        CommitMacroEditor();
        if (SelectedMacro is null && !_macroSectionSelected && CurrentMacroBook.Macros.Count > 0)
            SelectFirstMacro();
        if (!BeginVanillaWindow("##macro", UiPanelFrameLogicalOrigin(UiPanelOwnershipRegistry[15]),
                MacroBookUiLaw.FrameSize, out ImDrawListPtr dl, out Vector2 origin, out float s,
                movable: true)) { ImGui.End(); return; }

        // The header plaque hangs 12 px above the frame, so it needs the full-screen clip.
        dl.PushClipRectFullScreen();
        _skin?.DrawBackdrop(dl, origin, origin + MacroBookUiLaw.FrameSize * s, WowSkin.Dialog);
        DrawArt(dl, MacroBookUiLaw.HeaderArt,
            MacroBookUiLaw.HeaderPlaque.Minimum(origin, s),
            MacroBookUiLaw.HeaderPlaque.LogicalSize, s);
        GameText.DrawCentered(dl, MacroBookUiLaw.TitleFont, MacroBookUiLaw.Title,
            origin + MacroBookUiLaw.TitleCenter * s, s);
        dl.PopClipRect();

        DrawMacroBookTabs(dl, origin, s);
        DrawMacroBookList(dl, origin, s);
        MacroBookUiLaw.Rect divider = MacroBookUiLaw.Divider;
        dl.AddRectFilled(divider.Minimum(origin, s),
            divider.Minimum(origin, s) + divider.Size(s), MacroBookUiLaw.DividerColor);
        DrawMacroBookEditorColumn(dl, origin, s);
        DrawMacroBookBottomRow(dl, origin, s);
        if (_macroSectionMenuOpen) DrawMacroSectionMenu(dl, origin, s);

        DrawImageButton(dl, "##macro-close",
            MacroBookUiLaw.CloseButton.Minimum(origin, s),
            MacroBookUiLaw.CloseButton.Size(s),
            @"Interface\Buttons\UI-Panel-MinimizeButton-Up", @"Interface\Buttons\UI-Panel-MinimizeButton-Down",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");
        if (ImGui.IsItemClicked()) CloseMacros();

        if (_draggingSectionName.Length > 0)
            GameText.Draw(ImGui.GetForegroundDrawList(), MacroBookUiLaw.SectionDragFont,
                _draggingSectionName,
                ImGui.GetIO().MousePos + MacroBookUiLaw.SectionDragPreviewOffset * s, s);
        if (_draggingMacroId != 0)
        {
            uint icon = _gameplayArt.Handle(MacroIcon(_draggingMacroId));
            if (icon != 0)
            {
                Vector2 min = ImGui.GetIO().MousePos + MacroBookUiLaw.DragPreviewOffset * s;
                ImGui.GetForegroundDrawList().AddImage((nint)icon, min,
                    min + MacroBookUiLaw.DragPreviewSize * s);
            }
        }
        ImGui.End();
    }

    private void DrawMacroBookTabs(ImDrawListPtr dl, Vector2 origin, float s)
    {
        string generalLabel = MacroBookUiLaw.GeneralTabText;
        string characterLabel = MacroCharacterTabLabel();
        float generalWidth = MacroBookUiLaw.TabWidth(
            GameText.MeasureWidth(MacroBookUiLaw.TabFont, generalLabel, s) / s);
        float characterWidth = MacroBookUiLaw.CharacterTabWidth(
            GameText.MeasureWidth(MacroBookUiLaw.TabFont, characterLabel, s) / s);
        Vector2 firstTab = origin + MacroBookUiLaw.GeneralTab.Min * s;
        if (VanillaInsetTab(dl, "##macro-general-tab", firstTab, generalLabel,
                generalWidth, s, !_macroCharacterSpecific))
            SwitchMacroSet(false);
        if (VanillaInsetTab(dl, "##macro-character-tab",
                firstTab + MacroBookUiLaw.CharacterTabOffset(generalWidth) * s, characterLabel,
                characterWidth, s, _macroCharacterSpecific))
            SwitchMacroSet(true);
    }

    private void DrawMacroBookList(ImDrawListPtr dl, Vector2 origin, float s)
    {
        MacroBook book = CurrentMacroBook;
        MacroBookUiLaw.Rect search = MacroBookUiLaw.SearchBox;
        VanillaInputText(dl, "##macro-search", _macroSearch, search.Minimum(origin, s),
            search.LogicalSize, s);
        bool searchActive = ImGui.IsItemActive();
        string filter = ReadBuffer(_macroSearch);
        if (filter.Length == 0 && !searchActive)
            GameText.Draw(dl, MacroBookUiLaw.ShelfHintFont, MacroBookUiLaw.SearchHint,
                search.Minimum(origin, s) + new Vector2(8, 5) * s, s);

        IReadOnlyList<MacroBookLaw.Row> rows = MacroBookLaw.BuildRows(book.StoredSections(),
            book.StoredMacros(), filter);
        int visible = MacroBookUiLaw.VisibleListRows;
        MacroBookUiLaw.Rect list = MacroBookUiLaw.List;
        if (list.Contains(origin, s, ImGui.GetIO().MousePos) && ImGui.GetIO().MouseWheel != 0)
            _macroListScroll -= Math.Sign(ImGui.GetIO().MouseWheel);
        _macroListScroll = MacroBookLaw.ClampScroll(_macroListScroll, rows.Count, visible);

        uint highlight = _gameplayArt?.Handle(MacroBookUiLaw.RowHighlightPath) ?? 0;
        // While something is being dragged the pressed row holds ImGui's active id and no other
        // row can report hover, so drop targets come from geometry alone.
        Vector2 mouse = ImGui.GetIO().MousePos;
        bool draggingMacro = _draggingMacroId != 0;
        bool draggingSection = _draggingSectionName.Length > 0;
        MacroBookLaw.Drop drop = MacroBookLaw.Drop.None;
        int shownRows = 0;
        for (int i = 0; i < visible; i++)
        {
            int rowIndex = _macroListScroll + i;
            if (rowIndex >= rows.Count) break;
            shownRows++;
            MacroBookLaw.Row row = rows[rowIndex];
            MacroBookUiLaw.Rect rect = MacroBookUiLaw.ListRow(i);
            Vector2 min = rect.Minimum(origin, s);
            Vector2 size = rect.Size(s);
            if ((draggingMacro || draggingSection) && rect.Contains(origin, s, mouse))
            {
                bool after = MacroBookUiLaw.DropAfter(min.Y, mouse.Y, s);
                MacroBookLaw.Drop candidate = draggingMacro
                    ? MacroBookLaw.MacroDropOn(row, after) : MacroBookLaw.SectionDropOn(row, after);
                bool self = draggingMacro ? candidate.MacroId == _draggingMacroId
                    : candidate.Section.Equals(_draggingSectionName, StringComparison.OrdinalIgnoreCase);
                if (!self && candidate.Kind != MacroBookLaw.DropKind.None)
                {
                    drop = candidate;
                    DrawMacroDropIndicator(dl, min, size, drop, s);
                }
            }
            if (row.Kind == MacroBookLaw.RowKind.Section)
            {
                // The QuestLog plus/minus owns the left 20 px of the row; the row button
                // starts after it, so the two hit areas never overlap.
                Vector2 toggleMin = min + MacroBookUiLaw.SectionToggleOffset * s;
                Vector2 toggleSize = new Vector2(MacroBookUiLaw.SectionToggleSize) * s;
                ImGui.SetCursorScreenPos(toggleMin);
                ImGui.InvisibleButton($"##macro-toggle-{rowIndex}", toggleSize);
                bool toggleHovered = ImGui.IsItemHovered();
                bool toggleClicked = ImGui.IsItemClicked();
                float rowInset = MacroBookUiLaw.SectionLabelOffset.X * s;
                ImGui.SetCursorScreenPos(min + new Vector2(rowInset, 0));
                ImGui.InvisibleButton($"##macro-row-{rowIndex}", size - new Vector2(rowInset, 0));
                bool sectionHovered = ImGui.IsItemHovered() || toggleHovered;
                bool selected = _macroSectionSelected && row.Section.Equals(
                    _selectedMacroSection, StringComparison.OrdinalIgnoreCase);
                if (ImGui.IsItemClicked()) SelectSection(row.Section);
                if (ImGui.IsItemActivated())
                {
                    _pressedSectionName = row.Section;
                    _macroSectionPressPosition = mouse;
                }
                if (ImGui.IsItemActive() && _pressedSectionName == row.Section &&
                    MacroBookUiLaw.DragStarted(_macroSectionPressPosition, mouse, s))
                    _draggingSectionName = row.Section;
                if ((selected || sectionHovered) && highlight != 0)
                    dl.AddImage((nint)highlight, min, min + size, Vector2.Zero, Vector2.One,
                        selected ? 0xffffffffu : 0x99ffffffu);
                uint toggle = _gameplayArt?.Handle(row.Collapsed
                    ? MacroBookUiLaw.PlusPath : MacroBookUiLaw.MinusPath) ?? 0;
                if (toggle != 0) dl.AddImage((nint)toggle, toggleMin, toggleMin + toggleSize);
                if (toggleHovered)
                {
                    uint toggleHighlight = _gameplayArt?.AdditiveHandle(
                        MacroBookUiLaw.ToggleHighlightPath) ?? 0;
                    if (toggleHighlight != 0)
                        dl.AddImage((nint)toggleHighlight, toggleMin, toggleMin + toggleSize);
                }
                if (toggleClicked) ToggleSectionCollapsed(row.Section);
                float labelTop = GameText.BoxCenteredTop(MacroBookUiLaw.SectionFont, min.Y,
                    MacroBookUiLaw.ListRowHeight, s);
                Vector2 labelMin = new(min.X + MacroBookUiLaw.SectionLabelOffset.X * s, labelTop);
                string label = GameText.EllipsizeToBox(MacroBookUiLaw.SectionFont, row.Label,
                    rect.Width - MacroBookUiLaw.SectionLabelOffset.X - 30,
                    MacroBookUiLaw.ListRowHeight, s);
                GameText.Draw(dl, MacroBookUiLaw.SectionFont, label, labelMin, s);
                float labelWidth = GameText.MeasureWidth(MacroBookUiLaw.SectionFont, label, s);
                GameText.Draw(dl, MacroBookUiLaw.SectionCountFont, $"({row.Count})",
                    new Vector2(labelMin.X + labelWidth + 4 * s,
                        GameText.BoxCenteredTop(MacroBookUiLaw.SectionCountFont, min.Y,
                            MacroBookUiLaw.ListRowHeight, s)), s);
                continue;
            }

            ImGui.SetCursorScreenPos(min);
            ImGui.InvisibleButton($"##macro-row-{rowIndex}", size);
            bool hovered = ImGui.IsItemHovered();
            bool clicked = ImGui.IsItemClicked();
            bool macroSelected = !_macroSectionSelected && row.MacroId == _selectedMacroId;
            if (ImGui.IsItemActivated())
            {
                _pressedMacroId = row.MacroId;
                _macroPressPosition = ImGui.GetIO().MousePos;
            }
            if (ImGui.IsItemActive() && _pressedMacroId == row.MacroId &&
                MacroBookUiLaw.DragStarted(_macroPressPosition, ImGui.GetIO().MousePos, s))
                _draggingMacroId = _pressedMacroId;
            if (clicked) SelectMacro(row.MacroId);
            if (hovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                ExecuteMacro(row.MacroId);
            if ((macroSelected || hovered) && highlight != 0)
                dl.AddImage((nint)highlight, min, min + size, Vector2.Zero, Vector2.One,
                    macroSelected ? 0xffffffffu : 0x99ffffffu);
            uint icon = _gameplayArt?.Handle(MacroIcon(row.MacroId)) ?? 0;
            Vector2 iconMin = min + MacroBookUiLaw.MacroIconOffset(row.Indented) * s;
            if (icon != 0)
                dl.AddImage((nint)icon, iconMin,
                    iconMin + new Vector2(MacroBookUiLaw.MacroRowIconSize) * s);
            string font = macroSelected ? MacroBookUiLaw.MacroSelectedFont : MacroBookUiLaw.MacroFont;
            float labelX = MacroBookUiLaw.MacroLabelOffset(row.Indented).X;
            GameText.Draw(dl, font, GameText.EllipsizeToBox(font, row.Label,
                    rect.Width - labelX - 4, MacroBookUiLaw.ListRowHeight, s),
                new Vector2(min.X + labelX * s,
                    GameText.BoxCenteredTop(font, min.Y, MacroBookUiLaw.ListRowHeight, s)), s);
        }

        // Below the last row, still inside the list: a macro becomes ungrouped, last.
        if (draggingMacro && drop.Kind == MacroBookLaw.DropKind.None &&
            list.Contains(origin, s, mouse) && shownRows > 0 &&
            mouse.Y >= MacroBookUiLaw.ListRow(shownRows - 1).Minimum(origin, s).Y +
                MacroBookUiLaw.ListRowHeight * s)
        {
            drop = new MacroBookLaw.Drop(MacroBookLaw.DropKind.Ungrouped, "", 0, true);
            MacroBookUiLaw.Rect last = MacroBookUiLaw.ListRow(shownRows - 1);
            DrawMacroDropIndicator(dl, last.Minimum(origin, s), last.Size(s), drop, s);
        }
        _macroDrop = draggingMacro ? drop : MacroBookLaw.Drop.None;
        if (draggingSection && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            if (drop.Kind == MacroBookLaw.DropKind.BesideSection) ApplySectionDrop(drop);
            _draggingSectionName = "";
            _pressedSectionName = "";
        }
        else if (!draggingSection && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
            _pressedSectionName = "";

        MacroBookUiLaw.Rect bar = MacroBookUiLaw.ListScrollBar;
        DrawVanillaScrollBar(dl, "##macro-list-scroll", bar.Minimum(origin, s), bar.Height, s,
            _macroListScroll, MacroBookLaw.MaximumScroll(rows.Count, visible),
            value => _macroListScroll = value);

        bool hasSelection = _macroSectionSelected || SelectedMacro is not null;
        if (VanillaButton(dl, "New##macro", MacroBookUiLaw.NewText,
                MacroBookUiLaw.NewButton.Minimum(origin, s), MacroBookUiLaw.NewButton.LogicalSize,
                s, book.Macros.Count < MacroBookLaw.MacrosPerBook))
            CreateMacro();
        if (VanillaButton(dl, "Section##macro", MacroBookUiLaw.NewSectionText,
                MacroBookUiLaw.NewSectionButton.Minimum(origin, s),
                MacroBookUiLaw.NewSectionButton.LogicalSize, s))
            CreateSection();
        if (VanillaButton(dl, "Delete##macro", MacroBookUiLaw.DeleteText,
                MacroBookUiLaw.DeleteButton.Minimum(origin, s),
                MacroBookUiLaw.DeleteButton.LogicalSize, s, hasSelection))
            RequestDeleteSelection();
    }

    private void DrawMacroBookEditorColumn(ImDrawListPtr dl, Vector2 origin, float s)
    {
        MacroBook book = CurrentMacroBook;
        MacroDefinition? macro = SelectedMacro;
        if (macro is null && !_macroSectionSelected)
        {
            GameText.DrawCentered(dl, MacroBookUiLaw.SectionSummaryFont,
                "Press New to create a macro.",
                origin + MacroBookUiLaw.SectionSummaryCenter * s, s);
            return;
        }

        GameText.Draw(dl, MacroBookUiLaw.LabelFont, MacroBookUiLaw.NameLabelText,
            origin + MacroBookUiLaw.NameLabel * s, s);
        MacroBookUiLaw.Rect nameField = MacroBookUiLaw.NameField;
        bool nameChanged = VanillaInputText(dl, "##macro-name", _macroName,
            nameField.Minimum(origin, s), nameField.LogicalSize, s);
        if (nameChanged && _macroSectionSelected) RenameSelectedSection(ReadBuffer(_macroName));

        if (_macroSectionSelected)
        {
            int count = book.Macros.Count(member => member.Section.Equals(
                _selectedMacroSection, StringComparison.OrdinalIgnoreCase));
            GameText.DrawCentered(dl, MacroBookUiLaw.SectionSummaryFont,
                MacroBookUiLaw.SectionSummary(_selectedMacroSection, count),
                origin + MacroBookUiLaw.SectionSummaryCenter * s, s);
            GameText.DrawCentered(dl, MacroBookUiLaw.SectionSummaryHintFont,
                MacroBookUiLaw.SectionSummaryHint,
                origin + MacroBookUiLaw.SectionSummaryHintCenter * s, s);
            return;
        }

        // Icon button: MacroFrameButtonTemplate socket + icon, click for the picker.
        MacroBookUiLaw.Rect iconButton = MacroBookUiLaw.IconButton;
        MacroBookUiLaw.Rect socket = MacroBookUiLaw.IconSocket;
        DrawArt(dl, MacroBookUiLaw.SelectedSocketPath, socket.Minimum(origin, s),
            socket.LogicalSize, s);
        Vector2 iconMin = iconButton.Minimum(origin, s) + MacroBookUiLaw.IconOffset * s;
        uint selectedIcon = _gameplayArt?.Handle(MacroIcon(macro!.Id)) ?? 0;
        if (selectedIcon != 0)
            dl.AddImage((nint)selectedIcon, iconMin, iconMin + iconButton.Size(s));
        ImGui.SetCursorScreenPos(iconButton.Minimum(origin, s));
        ImGui.InvisibleButton("##macro-icon", iconButton.Size(s));
        if (ImGui.IsItemHovered())
        {
            uint hover = _gameplayArt?.AdditiveHandle(MacroBookUiLaw.HoverSquarePath) ?? 0;
            if (hover != 0)
                dl.AddImage((nint)hover, iconButton.Minimum(origin, s),
                    iconButton.Minimum(origin, s) + iconButton.Size(s));
        }
        if (ImGui.IsItemClicked()) OpenMacroIconPicker(macro);

        GameText.Draw(dl, MacroBookUiLaw.LabelFont, MacroBookUiLaw.SectionLabelText,
            origin + MacroBookUiLaw.SectionLabel * s, s);
        string sectionCaption = macro.Section.Length == 0 || book.FindSection(macro.Section) is null
            ? MacroBookUiLaw.NoSectionText : macro.Section;
        if (VanillaButton(dl, "Section-of##macro", sectionCaption,
                MacroBookUiLaw.SectionButton.Minimum(origin, s),
                MacroBookUiLaw.SectionButton.LogicalSize, s, !_macroIconPickerOpen,
                "GameFontNormalSmall", "GameFontHighlightSmall", "GameFontDisableSmall"))
            _macroSectionMenuOpen = !_macroSectionMenuOpen;

        if (_macroIconPickerOpen)
        {
            DrawMacroIconPicker(dl, origin, s, macro);
            return;
        }

        GameText.Draw(dl, MacroBookUiLaw.LabelFont, MacroBookUiLaw.BodyLabelText,
            origin + MacroBookUiLaw.BodyLabel * s, s);
        MacroBookUiLaw.Rect bodyBackground = MacroBookUiLaw.BodyBackground;
        _skin?.DrawBackdrop(dl, bodyBackground.Minimum(origin, s),
            bodyBackground.Minimum(origin, s) + bodyBackground.Size(s), WowSkin.Tooltip);
        MacroBookUiLaw.Rect body = MacroBookUiLaw.BodyEditor;
        if (body.Contains(origin, s, ImGui.GetIO().MousePos) && ImGui.GetIO().MouseWheel != 0)
            _macroBodyScroll = MacroBookUiLaw.WheelBodyScroll(_macroBodyScroll, _macroBody,
                ImGui.GetIO().MouseWheel);
        _macroBodyScroll = MacroBookUiLaw.ClampBodyScroll(_macroBodyScroll, _macroBody);
        if (!_macroSectionMenuOpen)
            VanillaBareMultilineText("##macro-text", ref _macroBody,
                (uint)MacroBookLaw.BodyCapacity + 1, body.Minimum(origin, s), body.LogicalSize,
                s, _macroBodyScroll, MacroBookUiLaw.BodyContentHeight(_macroBody));
        _macroBodyScroll = MacroBookUiLaw.ClampBodyScroll(_macroBodyScroll, _macroBody);
        MacroBookUiLaw.Rect bodyBar = MacroBookUiLaw.BodyScrollBar;
        DrawVanillaScrollBar(dl, "##macro-body-scroll", bodyBar.Minimum(origin, s), bodyBar.Height,
            s, MacroBookUiLaw.BodyScrollStepOf(_macroBodyScroll, _macroBody),
            MacroBookUiLaw.BodyScrollSteps(_macroBody),
            step => _macroBodyScroll = MacroBookUiLaw.BodyScrollFromStep(step, _macroBody));
        GameText.DrawRightAligned(dl, MacroBookUiLaw.CounterFont,
            $"{_macroBody.Length}/{MacroBookLaw.BodyCapacity}",
            origin + MacroBookUiLaw.CounterRight * s, s);

        DrawMacroDiagnostics(dl, origin, s);
        DrawMacroShelf(dl, origin, s);
    }

    private void DrawMacroDiagnostics(ImDrawListPtr dl, Vector2 origin, float s)
    {
        EnsureMacroLint();
        Vector2 top = MacroBookUiLaw.Diagnostics.Minimum(origin, s);
        float pitch = MacroBookUiLaw.DiagnosticPitch * s;
        if (_macroLint.Count == 0)
        {
            if (_macroBody.Trim().Length > 0)
                GameText.Draw(dl, MacroBookUiLaw.DiagnosticFont, MacroBookUiLaw.CleanText, top, s,
                    MacroBookUiLaw.CleanColor);
            return;
        }
        IReadOnlyList<MacroLintLaw.Diagnostic> ordered = _macroLint
            .OrderByDescending(diagnostic => diagnostic.Severity)
            .ThenBy(diagnostic => diagnostic.Line).ToArray();
        int shown = Math.Min(ordered.Count, MacroBookUiLaw.DiagnosticRows);
        bool overflow = ordered.Count > MacroBookUiLaw.DiagnosticRows;
        if (overflow) shown = MacroBookUiLaw.DiagnosticRows - 1;
        for (int i = 0; i < shown; i++)
        {
            MacroLintLaw.Diagnostic diagnostic = ordered[i];
            GameText.Draw(dl, MacroBookUiLaw.DiagnosticFont,
                GameText.EllipsizeToBox(MacroBookUiLaw.DiagnosticFont,
                    MacroBookUiLaw.DiagnosticText(diagnostic), MacroBookUiLaw.Diagnostics.Width,
                    MacroBookUiLaw.DiagnosticPitch, s),
                top + new Vector2(0, i * pitch), s, MacroBookUiLaw.DiagnosticColor(diagnostic.Severity));
        }
        if (overflow)
            GameText.Draw(dl, MacroBookUiLaw.DiagnosticFont,
                MacroBookUiLaw.OverflowText(ordered.Count - shown),
                top + new Vector2(0, shown * pitch), s, MacroBookUiLaw.InfoColor);
    }

    private void DrawMacroShelf(ImDrawListPtr dl, Vector2 origin, float s)
    {
        if (VanillaButton(dl, "Templates##macro", MacroBookUiLaw.TemplatesText,
                MacroBookUiLaw.TemplatesTab.Minimum(origin, s),
                MacroBookUiLaw.TemplatesTab.LogicalSize, s, true,
                _macroShelfCommands ? "GameFontNormalSmall" : "GameFontHighlightSmall",
                "GameFontHighlightSmall", "GameFontDisableSmall"))
        { _macroShelfCommands = false; _macroShelfScroll = 0; }
        if (VanillaButton(dl, "Commands##macro", MacroBookUiLaw.CommandsText,
                MacroBookUiLaw.CommandsTab.Minimum(origin, s),
                MacroBookUiLaw.CommandsTab.LogicalSize, s, true,
                _macroShelfCommands ? "GameFontHighlightSmall" : "GameFontNormalSmall",
                "GameFontHighlightSmall", "GameFontDisableSmall"))
        { _macroShelfCommands = true; _macroShelfScroll = 0; }
        MacroBookUiLaw.Rect filterBox = MacroBookUiLaw.ShelfFilter;
        if (VanillaInputText(dl, "##macro-shelf-filter", _macroShelfFilter,
                filterBox.Minimum(origin, s), filterBox.LogicalSize, s))
            _macroShelfScroll = 0;
        bool filterActive = ImGui.IsItemActive();
        string filter = ReadBuffer(_macroShelfFilter);
        if (filter.Length == 0 && !filterActive)
            GameText.Draw(dl, MacroBookUiLaw.ShelfHintFont, MacroBookUiLaw.ShelfFilterHint,
                filterBox.Minimum(origin, s) + new Vector2(8, 5) * s, s);

        // Rows are (label, hint, lines-to-insert) whichever shelf is showing.
        (string Label, string Hint, IReadOnlyList<string> Lines)[] entries = _macroShelfCommands
            ? MacroLintLaw.Search(MacroCommandCatalog, filter, MacroBookUiLaw.ShelfSearchLimit)
                .Select(command => ("." + command.Name, MacroLintLaw.SecurityLabel(command.Security),
                    (IReadOnlyList<string>)[MacroLintLaw.InsertionText(command)]))
                .ToArray()
            : MacroTemplateLaw.Search(filter)
                .Select(template => (template.Name, template.Hint, template.Lines))
                .ToArray();
        int visible = MacroBookUiLaw.VisibleShelfRows;
        MacroBookUiLaw.Rect shelf = MacroBookUiLaw.Shelf;
        if (shelf.Contains(origin, s, ImGui.GetIO().MousePos) && ImGui.GetIO().MouseWheel != 0)
            _macroShelfScroll -= Math.Sign(ImGui.GetIO().MouseWheel);
        _macroShelfScroll = MacroBookLaw.ClampScroll(_macroShelfScroll, entries.Length, visible);
        if (entries.Length == 0)
            GameText.Draw(dl, MacroBookUiLaw.ShelfHintFont, MacroBookUiLaw.ShelfEmptyText,
                shelf.Minimum(origin, s) + new Vector2(6, 4) * s, s);
        for (int i = 0; i < visible; i++)
        {
            int index = _macroShelfScroll + i;
            if (index >= entries.Length) break;
            (string label, string hint, IReadOnlyList<string> lines) = entries[index];
            MacroBookUiLaw.Rect rect = MacroBookUiLaw.ShelfRow(i);
            Vector2 min = rect.Minimum(origin, s);
            bool clicked = VanillaListRow(dl, $"##macro-shelf-{index}", min, rect.LogicalSize, s,
                "", selected: false, highlightPath: MacroBookUiLaw.RowHighlightPath);
            float hintWidth = GameText.MeasureWidth(MacroBookUiLaw.ShelfHintFont, hint, s);
            GameText.Draw(dl, MacroBookUiLaw.ShelfFont,
                GameText.EllipsizeToBox(MacroBookUiLaw.ShelfFont, label,
                    rect.Width - 10 - hintWidth / s - MacroBookUiLaw.ShelfHintRightInset,
                    MacroBookUiLaw.ShelfRowHeight, s),
                new Vector2(min.X + 5 * s,
                    GameText.BoxCenteredTop(MacroBookUiLaw.ShelfFont, min.Y,
                        MacroBookUiLaw.ShelfRowHeight, s)), s);
            if (hint.Length > 0)
                GameText.DrawRightAligned(dl, MacroBookUiLaw.ShelfHintFont, hint,
                    new Vector2(min.X + (rect.Width - MacroBookUiLaw.ShelfHintRightInset) * s,
                        GameText.BoxCenteredTop(MacroBookUiLaw.ShelfHintFont, min.Y,
                            MacroBookUiLaw.ShelfRowHeight, s)), s);
            if (clicked && TryAppendMacroLines(lines))
            {
                _macroStatus = $"Inserted {label}.";
                PlayUiSound(MacroBookUiLaw.ClickSound, "ui.macro");
            }
        }
        MacroBookUiLaw.Rect bar = MacroBookUiLaw.ShelfScrollBar;
        DrawVanillaScrollBar(dl, "##macro-shelf-scroll", bar.Minimum(origin, s), bar.Height, s,
            _macroShelfScroll, MacroBookLaw.MaximumScroll(entries.Length, visible),
            value => _macroShelfScroll = value);
    }

    private void DrawMacroBookBottomRow(ImDrawListPtr dl, Vector2 origin, float s)
    {
        MacroDefinition? macro = SelectedMacro;
        bool runnable = macro is not null && !_macroIconPickerOpen &&
            MacroBookLaw.RunnableLines(_macroBody).Count > 0;
        if (VanillaButton(dl, "Run##macro", MacroBookUiLaw.RunText,
                MacroBookUiLaw.RunButton.Minimum(origin, s), MacroBookUiLaw.RunButton.LogicalSize,
                s, runnable))
        {
            PlayUiSound(MacroBookUiLaw.RunSound, "ui.macro");
            ExecuteMacro(macro!.Id);
        }
        if (VanillaButton(dl, "Exit##macro", MacroBookUiLaw.ExitText,
                MacroBookUiLaw.ExitButton.Minimum(origin, s), MacroBookUiLaw.ExitButton.LogicalSize, s))
            CloseMacros();
        string status = _macroStatus.Length > 0 ? _macroStatus
            : MacroBookUiLaw.CountStatus(CurrentMacroBook.Macros.Count, MacroBookLaw.MacrosPerBook);
        GameText.Draw(dl, MacroBookUiLaw.StatusFont,
            GameText.EllipsizeToBox(MacroBookUiLaw.StatusFont, status,
                MacroBookUiLaw.ExitButton.X - MacroBookUiLaw.StatusLeft.X - 8, 14, s),
            origin + MacroBookUiLaw.StatusLeft * s, s);
    }

    private void DrawMacroSectionMenu(ImDrawListPtr dl, Vector2 origin, float s)
    {
        MacroBook book = CurrentMacroBook;
        MacroDefinition? macro = SelectedMacro;
        if (macro is null) { _macroSectionMenuOpen = false; return; }
        var choices = new List<(string Caption, string Section, bool Create)>
        {
            (MacroBookUiLaw.NoSectionText, "", false),
        };
        choices.AddRange(book.Sections.Take(MacroBookUiLaw.SectionMenuMaxRows - 2)
            .Select(section => (section.Name, section.Name, false)));
        choices.Add((MacroBookUiLaw.NewSectionMenuText, "", true));
        MacroBookUiLaw.Rect menu = MacroBookUiLaw.SectionMenu(choices.Count);
        Vector2 menuMin = menu.Minimum(origin, s);
        _skin?.DrawBackdrop(dl, menuMin, menuMin + menu.Size(s), WowSkin.Tooltip);
        for (int i = 0; i < choices.Count; i++)
        {
            (string caption, string section, bool create) = choices[i];
            MacroBookUiLaw.Rect rowRect = MacroBookUiLaw.SectionMenuRow(i);
            bool current = !create && macro.Section.Equals(section, StringComparison.OrdinalIgnoreCase);
            if (!VanillaListRow(dl, $"##macro-section-menu-{i}", rowRect.Minimum(origin, s),
                    rowRect.LogicalSize, s, caption, current,
                    highlightPath: MacroBookUiLaw.RowHighlightPath,
                    fontObject: current ? MacroBookUiLaw.MacroSelectedFont : MacroBookUiLaw.MacroFont))
                continue;
            _macroSectionMenuOpen = false;
            if (create)
            {
                string name = MacroBookLaw.UniqueSectionName(
                    book.Sections.Select(existing => existing.Name), MacroBookLaw.DefaultSectionName);
                book.Sections.Add(new MacroSection { Name = name });
                MoveSelectedMacroToSection(name);
            }
            else MoveSelectedMacroToSection(section);
            PlayUiSound(MacroBookUiLaw.ClickSound, "ui.macro");
        }
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left) &&
            !menu.Contains(origin, s, ImGui.GetIO().MousePos) &&
            !MacroBookUiLaw.SectionButton.Contains(origin, s, ImGui.GetIO().MousePos))
            _macroSectionMenuOpen = false;
    }

    // ── icon picker ──────────────────────────────────────────────────────────────────────

    private void OpenMacroIconPicker(MacroDefinition macro)
    {
        EnsureMacroIconsLoaded();
        _macroIconPickerOpen = true;
        _macroSectionMenuOpen = false;
        Array.Clear(_macroIconFilter);
        _macroIconFilterApplied = "";
        _macroIconsFiltered = _macroIcons;
        _macroIconPickerSelection = _macroIconsFiltered
            .Select((path, index) => (path, index))
            .FirstOrDefault(pair => pair.path.Equals(macro.IconPath,
                StringComparison.OrdinalIgnoreCase), (path: "", index: -1)).index;
        _macroIconRowOffset = _macroIconPickerSelection >= 0
            ? MacroBookUiLaw.ClampIconRowOffset(
                _macroIconPickerSelection / MacroBookUiLaw.IconColumns, _macroIconsFiltered.Count)
            : 0;
    }

    private void DrawMacroIconPicker(ImDrawListPtr dl, Vector2 origin, float s, MacroDefinition macro)
    {
        MacroBookUiLaw.Rect filterBox = MacroBookUiLaw.IconFilter;
        VanillaInputText(dl, "##macro-icon-filter", _macroIconFilter,
            filterBox.Minimum(origin, s), filterBox.LogicalSize, s);
        bool filterActive = ImGui.IsItemActive();
        string filter = ReadBuffer(_macroIconFilter);
        if (filter.Length == 0 && !filterActive)
            GameText.Draw(dl, MacroBookUiLaw.ShelfHintFont, MacroBookUiLaw.IconFilterHint,
                filterBox.Minimum(origin, s) + new Vector2(8, 5) * s, s);
        if (filter != _macroIconFilterApplied)
        {
            _macroIconFilterApplied = filter;
            _macroIconsFiltered = MacroBookUiLaw.FilterIcons(_macroIcons, filter);
            _macroIconPickerSelection = -1;
            _macroIconRowOffset = 0;
        }

        int iconCount = _macroIconsFiltered.Count;
        _macroIconRowOffset = MacroBookUiLaw.ClampIconRowOffset(_macroIconRowOffset, iconCount);
        bool accept = false;
        for (int visible = 0; visible < MacroBookUiLaw.VisibleIcons; visible++)
        {
            int catalogIndex = MacroBookUiLaw.IconCatalogIndex(_macroIconRowOffset, visible, iconCount);
            if (catalogIndex < 0) continue;
            MacroBookUiLaw.Rect cell = MacroBookUiLaw.IconCell(visible);
            Vector2 min = cell.Minimum(origin, s);
            MacroBookUiLaw.Rect socketRect = MacroBookUiLaw.IconCellSocket(cell);
            DrawArt(dl, MacroBookUiLaw.SocketPath, socketRect.Minimum(origin, s),
                socketRect.LogicalSize, s);
            uint icon = _gameplayArt?.Handle(_macroIconsFiltered[catalogIndex]) ?? 0;
            Vector2 iconMin = min + MacroBookUiLaw.IconOffset * s;
            if (icon != 0) dl.AddImage((nint)icon, iconMin, iconMin + cell.Size(s));
            ImGui.SetCursorScreenPos(min);
            ImGui.InvisibleButton($"##macro-icon-{visible}", cell.Size(s));
            if (ImGui.IsItemClicked()) _macroIconPickerSelection = catalogIndex;
            if (ImGui.IsItemHovered())
            {
                if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                {
                    _macroIconPickerSelection = catalogIndex;
                    accept = true;
                }
                uint hover = _gameplayArt?.AdditiveHandle(MacroBookUiLaw.HoverSquarePath) ?? 0;
                if (hover != 0) dl.AddImage((nint)hover, min, min + cell.Size(s));
            }
            if (_macroIconPickerSelection == catalogIndex)
            {
                uint check = _gameplayArt?.AdditiveHandle(MacroBookUiLaw.CheckedSquarePath) ?? 0;
                if (check != 0) dl.AddImage((nint)check, min, min + cell.Size(s));
            }
        }
        MacroBookUiLaw.Rect first = MacroBookUiLaw.IconCell(0);
        MacroBookUiLaw.Rect last = MacroBookUiLaw.IconCell(MacroBookUiLaw.VisibleIcons - 1);
        var grid = new MacroBookUiLaw.Rect(first.X, first.Y, last.X + last.Width - first.X,
            last.Y + last.Height - first.Y);
        if (grid.Contains(origin, s, ImGui.GetIO().MousePos) && ImGui.GetIO().MouseWheel != 0)
            _macroIconRowOffset = MacroBookUiLaw.ClampIconRowOffset(
                _macroIconRowOffset - Math.Sign(ImGui.GetIO().MouseWheel), iconCount);
        MacroBookUiLaw.Rect bar = MacroBookUiLaw.IconScrollBar;
        DrawVanillaScrollBar(dl, "##macro-icon-scroll", bar.Minimum(origin, s), bar.Height, s,
            _macroIconRowOffset, MacroBookUiLaw.MaximumIconRowOffset(iconCount),
            value => _macroIconRowOffset = value);

        bool okayEnabled = _macroIconPickerSelection >= 0 && _macroIconPickerSelection < iconCount;
        if (VanillaButton(dl, "Okay##macro-icon", MacroBookUiLaw.OkayText,
                MacroBookUiLaw.IconOkayButton.Minimum(origin, s),
                MacroBookUiLaw.IconOkayButton.LogicalSize, s, okayEnabled) || accept && okayEnabled)
        {
            macro.IconPath = _macroIconsFiltered[_macroIconPickerSelection];
            _macroIconPickerOpen = false;
            SaveMacros();
            PlayUiSound(MacroBookUiLaw.ClickSound, "ui.macro");
        }
        if (VanillaButton(dl, "Cancel##macro-icon", MacroBookUiLaw.CancelText,
                MacroBookUiLaw.IconCancelButton.Minimum(origin, s),
                MacroBookUiLaw.IconCancelButton.LogicalSize, s))
            _macroIconPickerOpen = false;
    }
}
