using System.Numerics;
using System.Text;
using System.Text.Json;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private sealed class MacroDefinition
    {
        public string Name { get; set; } = "";
        public string Body { get; set; } = "";
        public string IconPath { get; set; } = "";
    }

    private bool _macroOpen;
    private readonly List<MacroDefinition> _macros = [];
    private bool _macrosLoaded;
    private string _loadedMacroAccountPath = "";
    private string _loadedMacroCharacterPath = "";
    private int _selectedMacro;
    private bool _macroCharacterSpecific;
    private readonly byte[] _macroName = new byte[32];
    private string _macroBody = "";
    /// <summary>
    /// True once _macroName/_macroBody mirror _macros[_selectedMacro] (SelectMacro, or the
    /// popup creating a macro). CommitMacroEditor copies the buffers BACK into that macro, and
    /// _selectedMacro starts at 0 with empty buffers: any commit before the editor was ever
    /// seeded - an action-bar macro press (ExecuteMacro), a store path change at login - wiped
    /// the first macro's name and body in memory, and the next save wrote that out. The icon
    /// survived because the commit never touches it. Owner report 2026-09-03.
    /// </summary>
    private bool _macroEditorBound;
    private float _macroBodyScroll;
    private uint _pressedMacroId;
    private uint _draggingMacroId;
    private Vector2 _macroPressPosition;
    private bool _macroPopupOpen;
    private MacroPopupMode _macroPopupMode;
    private readonly byte[] _macroPopupName = new byte[MacroFrameUiLaw.NameCapacity + 1];
    private int _macroPopupSelectedIcon = -1;
    private int _macroPopupRowOffset;
    private IReadOnlyList<string> _macroIcons = [];
    private bool _macroIconsLoaded;

    private void OpenMacros()
    {
        EnsureMacrosLoaded();
        int setBase = MacroFrameUiLaw.SetBase(_macroCharacterSpecific);
        SelectMacro(MacroFrameUiLaw.InSet(_macroCharacterSpecific, _selectedMacro)
            ? _selectedMacro : setBase);
        if (!_macroOpen) PlayUiSound(MacroFrameUiLaw.OpenSound, "ui.macro");
        _macroOpen = true;
    }

    private void CloseMacros(bool playSound = true)
    {
        if (!_macroOpen) return;
        _macroPopupOpen = false;
        SaveMacros();
        _macroOpen = false;
        if (playSound) PlayUiSound(MacroFrameUiLaw.CloseSound, "ui.macro");
    }

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
        _macros.Clear();
        _macroEditorBound = false;
        bool migrateLegacy = !File.Exists(accountPath) && !File.Exists(characterPath);
        try
        {
            if (File.Exists(accountPath) || File.Exists(characterPath))
            {
                AppendMacroSet(ReadMacroStore(accountPath));
                AppendMacroSet(ReadMacroStore(characterPath));
            }
            else
            {
                string legacyPath = Path.Combine(_config.RepoRoot, "macros.json");
                if (File.Exists(legacyPath))
                    _macros.AddRange(JsonSerializer.Deserialize<List<MacroDefinition>>(
                        File.ReadAllText(legacyPath)) ?? []);
            }
        }
        catch (Exception ex) { Console.WriteLine($"[macros] load failed: {ex.Message}"); }
        while (_macros.Count < MacroFrameUiLaw.TotalMacros) _macros.Add(new MacroDefinition());
        if (_macros.Count > MacroFrameUiLaw.TotalMacros)
            _macros.RemoveRange(MacroFrameUiLaw.TotalMacros,
                _macros.Count - MacroFrameUiLaw.TotalMacros);
        _macrosLoaded = true;
        _loadedMacroAccountPath = accountPath;
        _loadedMacroCharacterPath = characterPath;
        if (migrateLegacy && _macros.Any(MacroHasContent))
            TryWriteMacroStores(accountPath, characterPath);
    }

    private void SaveMacros()
    {
        EnsureMacrosLoaded();
        CommitMacroEditor();
        TryWriteMacroStores(_loadedMacroAccountPath, _loadedMacroCharacterPath);
    }

    private (string AccountPath, string CharacterPath) MacroStorePaths()
    {
        string directory = Path.Combine(_config.RepoRoot, "macros");
        string realm = MacroFrameUiLaw.StoreFileToken(_net?.RealmName ?? "Realm");
        string character = MacroFrameUiLaw.StoreFileToken(_net?.PlayerName ?? "Character");
        return (Path.Combine(directory, "account.txt"),
            Path.Combine(directory, $"{realm}-{character}.txt"));
    }

    private static IReadOnlyList<MacroDefinition> ReadMacroStore(string path)
    {
        if (!File.Exists(path)) return [];
        return MacroFrameUiLaw.ParseStore(File.ReadAllText(path))
            .Select(macro => new MacroDefinition
            {
                Name = macro.Name,
                Body = macro.Body,
                IconPath = macro.IconPath,
            }).ToArray();
    }

    private void AppendMacroSet(IReadOnlyList<MacroDefinition> macros)
    {
        int end = _macros.Count + MacroFrameUiLaw.MacrosPerSet;
        _macros.AddRange(macros.Take(MacroFrameUiLaw.MacrosPerSet));
        while (_macros.Count < end) _macros.Add(new MacroDefinition());
    }

    private static bool MacroHasContent(MacroDefinition macro) =>
        macro.Name.Length > 0 || macro.Body.Length > 0 || macro.IconPath.Length > 0;

    private void TryWriteMacroStores(string accountPath, string characterPath)
    {
        try
        {
            WriteMacroStoreAtomic(accountPath, 0);
            WriteMacroStoreAtomic(characterPath, MacroFrameUiLaw.MacrosPerSet);
        }
        catch (Exception ex) { Console.WriteLine($"[macros] save failed: {ex.Message}"); }
    }

    private void WriteMacroStoreAtomic(string path, int start)
    {
        string text = MacroFrameUiLaw.WriteStore(_macros.Skip(start)
            .Take(MacroFrameUiLaw.MacrosPerSet).Where(MacroHasContent)
            .Select(macro => new MacroFrameUiLaw.StoredMacro(
                macro.Name, macro.Body, macro.IconPath)));
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
    }

    private void OpenMacroPopup(MacroPopupMode mode)
    {
        EnsureMacrosLoaded();
        EnsureMacroIconsLoaded();
        CommitMacroEditor();
        _macroPopupMode = mode;
        _macroPopupRowOffset = 0;
        _macroPopupSelectedIcon = -1;
        Array.Clear(_macroPopupName);
        if (mode == MacroPopupMode.Edit && _selectedMacro >= 0 &&
            _selectedMacro < _macros.Count)
        {
            MacroDefinition macro = _macros[_selectedMacro];
            WriteBuffer(_macroPopupName, macro.Name);
            _macroPopupSelectedIcon = _macroIcons
                .Select((path, index) => (path, index))
                .FirstOrDefault(pair => pair.path.Equals(macro.IconPath,
                    StringComparison.OrdinalIgnoreCase), (path: "", index: -1)).index;
            if (_macroPopupSelectedIcon >= 0)
                _macroPopupRowOffset = MacroFrameUiLaw.ClampRowOffset(
                    _macroPopupSelectedIcon / MacroFrameUiLaw.IconsPerRow,
                    _macroIcons.Count);
        }
        _macroPopupOpen = true;
        PlayUiSound(MacroFrameUiLaw.OpenSound, "ui.macro-popup");
    }

    private void CloseMacroPopup(bool accepted)
    {
        if (!_macroPopupOpen) return;
        if (accepted)
        {
            string name = ReadBuffer(_macroPopupName).Trim();
            bool existingIcon = _macroPopupMode == MacroPopupMode.Edit &&
                _selectedMacro >= 0 && _selectedMacro < _macros.Count &&
                _macros[_selectedMacro].IconPath.Length > 0;
            if (!MacroFrameUiLaw.OkayEnabled(_macroPopupMode, name,
                    _macroPopupSelectedIcon, existingIcon))
                return;

            int target = _selectedMacro;
            if (_macroPopupMode == MacroPopupMode.New)
            {
                int setBase = MacroFrameUiLaw.SetBase(_macroCharacterSpecific);
                target = _macros.FindIndex(setBase, MacroFrameUiLaw.MacrosPerSet,
                    macro => macro.Name.Length == 0 && macro.Body.Length == 0);
                if (target < 0) return;
                _macros[target] = new MacroDefinition();
            }
            MacroDefinition selected = _macros[target];
            selected.Name = name;
            if (_macroPopupSelectedIcon >= 0 &&
                _macroPopupSelectedIcon < _macroIcons.Count)
                selected.IconPath = _macroIcons[_macroPopupSelectedIcon];
            _selectedMacro = target;
            Array.Clear(_macroName);
            WriteBuffer(_macroName, selected.Name);
            _macroBody = selected.Body;
            _macroEditorBound = true;
            SaveMacros();
        }
        _macroPopupOpen = false;
        PlayUiSound(MacroFrameUiLaw.AcceptSound, "ui.macro-popup");
    }

    private void SelectMacro(int index)
    {
        EnsureMacrosLoaded();
        if (_macros.Count == 0) return;
        CommitMacroEditor();
        _selectedMacro = Math.Clamp(index, 0, _macros.Count - 1);
        Array.Clear(_macroName);
        byte[] bytes = Encoding.UTF8.GetBytes(_macros[_selectedMacro].Name);
        Array.Copy(bytes, _macroName, Math.Min(bytes.Length, _macroName.Length - 1));
        _macroBody = _macros[_selectedMacro].Body;
        _macroBodyScroll = 0;
        _macroEditorBound = true;
    }

    private void CommitMacroEditor()
    {
        if (!_macrosLoaded || !_macroEditorBound ||
            _selectedMacro < 0 || _selectedMacro >= _macros.Count) return;
        _macros[_selectedMacro].Name = ReadBuffer(_macroName).Trim();
        _macros[_selectedMacro].Body = _macroBody;
    }

    private void ExecuteMacro(uint id)
    {
        EnsureMacrosLoaded();
        int index = checked((int)id - 1);
        if (index < 0 || index >= _macros.Count) return;
        CommitMacroEditor();
        foreach (string line in MacroFrameUiLaw.RunnableLines(_macros[index].Body))
        {
            if (line.StartsWith('#')) continue;
            SubmitChatLine(line);
        }
    }

    private string MacroIcon(uint id)
    {
        EnsureMacrosLoaded();
        int index = (int)id - 1;
        if (index < 0 || index >= _macros.Count) return @"Interface\Icons\INV_Misc_QuestionMark.blp";
        if (!string.IsNullOrWhiteSpace(_macros[index].IconPath))
            return _macros[index].IconPath;
        foreach (string raw in _macros[index].Body.Replace("\r", "").Split('\n'))
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
        return @"Interface\Icons\INV_Misc_QuestionMark.blp";
    }

    private void SwitchMacroSet(bool characterSpecific)
    {
        if (_macroCharacterSpecific == characterSpecific) return;
        CommitMacroEditor();
        _macroCharacterSpecific = characterSpecific;
        SelectMacro(MacroFrameUiLaw.SetBase(characterSpecific));
    }

    private string MacroCharacterTabLabel()
    {
        ulong guid = _net?.PlayerGuid ?? 0;
        string name = _playerNames.GetValueOrDefault(guid, "Character");
        return $"{name} Specific Macros";
    }

    private void DrawMacroFrame()
    {
        if (!_macroOpen || _gameplayArt is null) return;
        EnsureMacrosLoaded();
        float s = GameplayUiScale();
        if (!BeginVanillaWindow("##macro", UiPanelFrameLogicalOrigin(UiPanelOwnershipRegistry[15]),
                MacroFrameUiLaw.FrameSize, out ImDrawListPtr dl,
                out Vector2 origin, out s)) { ImGui.End(); return; }
        foreach (MacroFrameUiLaw.ArtPiece piece in MacroFrameUiLaw.FrameArt)
            DrawArt(dl, piece.Path, piece.Rect.Minimum(origin, s),
                piece.Rect.LogicalSize, s);
        DrawArt(dl, @"Interface\MacroFrame\MacroFrame-Icon",
            MacroFrameUiLaw.Portrait.Minimum(origin, s),
            MacroFrameUiLaw.Portrait.LogicalSize, s);
        GameText.DrawCentered(dl, MacroFrameUiLaw.TitleFont, "Create Macros",
            origin + MacroFrameUiLaw.TitleCenter * s, s);

        // MacroFrameTab1/2 inherit TabButtonTemplate (the HelpFrameTab inset art, 16 px caps),
        // NOT the character frame's tab - VanillaInsetTab is that template.
        string generalLabel = MacroFrameUiLaw.GeneralTabText;
        string characterLabel = MacroCharacterTabLabel();
        float generalWidth = MacroFrameUiLaw.GeneralTabWidth(
            GameText.MeasureWidth(MacroFrameUiLaw.TabFont, generalLabel, s) / s);
        float characterWidth = MacroFrameUiLaw.CharacterTabWidth(
            GameText.MeasureWidth(MacroFrameUiLaw.TabFont, characterLabel, s) / s,
            generalWidth);
        Vector2 firstTab = origin + MacroFrameUiLaw.GeneralTab.Min * s;
        if (VanillaInsetTab(dl, "##macro-general-tab", firstTab, generalLabel,
                generalWidth, s, !_macroCharacterSpecific))
            SwitchMacroSet(false);
        if (VanillaInsetTab(dl, "##macro-character-tab",
                firstTab + MacroFrameUiLaw.CharacterTabOffset(generalWidth) * s, characterLabel,
                characterWidth, s, _macroCharacterSpecific))
            SwitchMacroSet(true);

        int setBase = MacroFrameUiLaw.SetBase(_macroCharacterSpecific);
        for (int i = 0; i < MacroFrameUiLaw.MacrosPerSet; i++)
        {
            int macroIndex = MacroFrameUiLaw.AbsoluteIndex(_macroCharacterSpecific, i);
            uint macroId = (uint)macroIndex + 1;
            MacroFrameUiLaw.Rect rect = MacroFrameUiLaw.MacroButton(i);
            Vector2 min = rect.Minimum(origin, s);
            MacroFrameUiLaw.Rect socket = MacroFrameUiLaw.MacroSocket;
            DrawArt(dl, @"Interface\Buttons\UI-EmptySlot-Disabled",
                min + socket.Min * s, socket.LogicalSize, s);
            // $parentIcon is CENTER (0,-1): one pixel below the button, like its socket.
            Vector2 iconMin = min + MacroFrameUiLaw.IconOffset * s;
            uint icon = _gameplayArt.Handle(MacroIcon(macroId));
            if (icon != 0) dl.AddImage((nint)icon, iconMin, iconMin + rect.Size(s));
            ImGui.SetCursorScreenPos(min);
            ImGui.InvisibleButton($"##macro-slot-{macroIndex}", rect.Size(s));
            if (ImGui.IsItemActivated()) { _pressedMacroId = macroId; _macroPressPosition = ImGui.GetIO().MousePos; }
            if (ImGui.IsItemActive() && _pressedMacroId == macroId &&
                Vector2.DistanceSquared(_macroPressPosition, ImGui.GetIO().MousePos) > 36 * s * s)
                _draggingMacroId = _pressedMacroId;
            if (ImGui.IsItemClicked()) SelectMacro(macroIndex);
            if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                ExecuteMacro(macroId);
            if (ImGui.IsItemHovered())
            {
                // MacroFrameButtonTemplate HighlightTexture: ButtonHilight-Square.
                uint hover = _gameplayArt.AdditiveHandle(@"Interface\Buttons\ButtonHilight-Square");
                if (hover != 0) dl.AddImage((nint)hover, min, min + rect.Size(s));
            }
            if (_macros[macroIndex].Name.Length > 0)
                GameText.DrawCentered(dl, "GameFontHighlightSmallOutline",
                    GameText.EllipsizeToBox("GameFontHighlightSmallOutline", _macros[macroIndex].Name,
                        MacroFrameUiLaw.MacroNameWidth, MacroFrameUiLaw.MacroNameHeight, s),
                    min + MacroFrameUiLaw.MacroNameCenter * s, s);
            if (_selectedMacro == macroIndex)
            {
                uint check = _gameplayArt.AdditiveHandle(
                    @"Interface\Buttons\CheckButtonHilight");
                if (check != 0) dl.AddImage((nint)check, min, min + rect.Size(s));
            }
        }

        uint divider = _gameplayArt.Handle(
            @"Interface\ClassTrainerFrame\UI-ClassTrainer-HorizontalBar");
        if (divider != 0)
        {
            MacroFrameUiLaw.Rect left = MacroFrameUiLaw.DividerLeft;
            dl.AddImage((nint)divider, left.Minimum(origin, s),
                left.Minimum(origin, s) + left.Size(s), MacroFrameUiLaw.DividerLeftUvMin,
                MacroFrameUiLaw.DividerLeftUvMax);
            MacroFrameUiLaw.Rect right = MacroFrameUiLaw.DividerRight;
            dl.AddImage((nint)divider, right.Minimum(origin, s),
                right.Minimum(origin, s) + right.Size(s), MacroFrameUiLaw.DividerRightUvMin,
                MacroFrameUiLaw.DividerRightUvMax);
        }
        MacroFrameUiLaw.Rect selectedBackground = MacroFrameUiLaw.SelectedBackground;
        DrawArt(dl, @"Interface\Buttons\UI-EmptySlot",
            selectedBackground.Minimum(origin, s), selectedBackground.LogicalSize, s);
        MacroFrameUiLaw.Rect selectedButton = MacroFrameUiLaw.SelectedButton;
        uint selectedIcon = _gameplayArt.Handle(MacroIcon((uint)_selectedMacro + 1));
        if (selectedIcon != 0)
        {
            Vector2 selectedIconMin = selectedButton.Minimum(origin, s) + MacroFrameUiLaw.IconOffset * s;
            dl.AddImage((nint)selectedIcon, selectedIconMin, selectedIconMin + selectedButton.Size(s));
        }
        GameText.Draw(dl, "GameFontNormalLarge", _macros[_selectedMacro].Name,
            MacroFrameUiLaw.SelectedName.Minimum(origin, s), s);
        GameText.Draw(dl, "GameFontHighlightSmall", "Enter Macro Commands:",
            MacroFrameUiLaw.EnterMacroLabel.Minimum(origin, s), s);
        MacroFrameUiLaw.Rect bodyBackground = MacroFrameUiLaw.BodyBackground;
        _skin?.DrawBackdrop(dl, bodyBackground.Minimum(origin, s),
            bodyBackground.Minimum(origin, s) + bodyBackground.Size(s), WowSkin.Tooltip);
        if (!_macroPopupOpen)
        {
            MacroFrameUiLaw.Rect body = MacroFrameUiLaw.BodyEditor;
            Vector2 bodyMin = body.Minimum(origin, s);
            float bodyContentHeight = MacroFrameUiLaw.BodyContentHeight(_macroBody);
            _macroBodyScroll = MacroFrameUiLaw.ClampBodyScroll(
                _macroBodyScroll, _macroBody);
            if (ImGui.IsMouseHoveringRect(bodyMin, bodyMin + body.Size(s), false) &&
                ImGui.GetIO().MouseWheel != 0)
                _macroBodyScroll = MacroFrameUiLaw.WheelBodyScroll(
                    _macroBodyScroll, _macroBody, ImGui.GetIO().MouseWheel);
            ImGui.SetCursorScreenPos(bodyMin);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Vector2.Zero);
            ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 0);
            ImGui.BeginChild("##macro-text-scroll", body.Size(s), false,
                ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse);
            ImGui.SetScrollY(_macroBodyScroll * s);
            ImGui.SetCursorPos(Vector2.Zero);
            ImGui.PushStyleColor(ImGuiCol.FrameBg, Vector4.Zero);
            ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Vector4.Zero);
            ImGui.PushStyleColor(ImGuiCol.FrameBgActive, Vector4.Zero);
            ImGui.PushStyleColor(ImGuiCol.Border, Vector4.Zero);
            ImGui.InputTextMultiline("##macro-text", ref _macroBody, 255,
                MacroFrameUiLaw.BodyInputSize(bodyContentHeight) * s,
                ImGuiInputTextFlags.NoHorizontalScroll);
            ImGui.PopStyleColor(4);
            ImGui.EndChild();
            ImGui.PopStyleVar(3);
            _macroBodyScroll = MacroFrameUiLaw.ClampBodyScroll(
                _macroBodyScroll, _macroBody);
            DrawMacroBodyScrollBar(dl, origin, s);
        }
        GameText.DrawCentered(dl, "GameFontHighlightSmall", $"{_macroBody.Length}/255",
            origin + MacroFrameUiLaw.CharacterLimitCenter * s, s);

        int empty = _macros.FindIndex(setBase, MacroFrameUiLaw.MacrosPerSet,
            macro => macro.Name.Length == 0 && macro.Body.Length == 0);
        bool hasEmptyMacro = empty >= 0;
        if (VanillaButton(dl, "New##macro", "New",
                MacroFrameUiLaw.NewButton.Minimum(origin, s),
                MacroFrameUiLaw.NewButton.Size(1), s, !_macroPopupOpen && hasEmptyMacro))
            OpenMacroPopup(MacroPopupMode.New);
        if (VanillaButton(dl, "Delete##macro", "Delete",
                MacroFrameUiLaw.DeleteButton.Minimum(origin, s),
                MacroFrameUiLaw.DeleteButton.Size(1), s,
                !_macroPopupOpen && (_macros[_selectedMacro].Name.Length > 0 ||
                    _macros[_selectedMacro].Body.Length > 0)))
        { _macros[_selectedMacro] = new MacroDefinition(); SelectMacro(_selectedMacro); }
        if (VanillaButton(dl, "Exit##macro", "Exit",
                MacroFrameUiLaw.ExitButton.Minimum(origin, s),
                MacroFrameUiLaw.ExitButton.Size(1), s))
            CloseMacros();
        if (VanillaButton(dl, "Change##macro", "Change Name/Icon",
                MacroFrameUiLaw.ChangeButton.Minimum(origin, s),
                MacroFrameUiLaw.ChangeButton.Size(1), s,
                !_macroPopupOpen))
            OpenMacroPopup(MacroPopupMode.Edit);
        DrawImageButton(dl, "##macro-close",
            MacroFrameUiLaw.CloseButton.Minimum(origin, s),
            MacroFrameUiLaw.CloseButton.Size(s),
            @"Interface\Buttons\UI-Panel-MinimizeButton-Up", @"Interface\Buttons\UI-Panel-MinimizeButton-Down",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");
        if (ImGui.IsItemClicked()) CloseMacros();
        if (_draggingMacroId != 0)
        {
            uint icon = _gameplayArt.Handle(MacroIcon(_draggingMacroId));
            if (icon != 0)
            {
                Vector2 min = ImGui.GetIO().MousePos + MacroFrameUiLaw.DragPreviewOffset * s;
                ImGui.GetForegroundDrawList().AddImage((nint)icon, min,
                    min + MacroFrameUiLaw.DragPreviewSize * s);
            }
        }
        ImGui.End();
        if (_macroPopupOpen) DrawMacroPopup(origin, s);
    }

    private void DrawMacroBodyScrollBar(ImDrawListPtr draw, Vector2 origin, float scale)
    {
        float maximum = MacroFrameUiLaw.MaximumBodyScroll(_macroBody);
        if (maximum <= 0 || _gameplayArt is null) return;

        void Arrow(string id, MacroFrameUiLaw.Rect rect, bool up)
        {
            bool enabled = up ? _macroBodyScroll > 0 : _macroBodyScroll < maximum;
            Vector2 min = rect.Minimum(origin, scale);
            Vector2 size = rect.Size(scale);
            ImGui.SetCursorScreenPos(min);
            if (!enabled) ImGui.BeginDisabled();
            ImGui.InvisibleButton(id, size);
            bool active = enabled && ImGui.IsItemActive();
            bool hovered = enabled && ImGui.IsItemHovered();
            bool clicked = enabled && ImGui.IsItemClicked(ImGuiMouseButton.Left);
            if (!enabled) ImGui.EndDisabled();
            string stem = up ? "UI-ScrollBar-ScrollUpButton" :
                "UI-ScrollBar-ScrollDownButton";
            string state = !enabled ? "Disabled" : active ? "Down" : "Up";
            uint art = _gameplayArt.Handle($@"Interface\Buttons\{stem}-{state}");
            if (art != 0)
                draw.AddImage((nint)art, min, min + size,
                    MacroFrameUiLaw.ScrollUvMin, MacroFrameUiLaw.ScrollUvMax);
            if (hovered)
            {
                uint highlight = _gameplayArt.AdditiveHandle(
                    $@"Interface\Buttons\{stem}-Highlight");
                if (highlight != 0)
                    draw.AddImage((nint)highlight, min, min + size,
                        MacroFrameUiLaw.ScrollUvMin, MacroFrameUiLaw.ScrollUvMax);
            }
            if (clicked)
            {
                _macroBodyScroll = MacroFrameUiLaw.ClampBodyScroll(
                    _macroBodyScroll + (up ? -MacroFrameUiLaw.BodyScrollStep :
                        MacroFrameUiLaw.BodyScrollStep), _macroBody);
                PlayUiSound("UChatScrollButton", "ui.macro");
            }
        }

        Arrow("##macro-body-scroll-up", MacroFrameUiLaw.BodyScrollUp, true);
        Arrow("##macro-body-scroll-down", MacroFrameUiLaw.BodyScrollDown, false);
        Vector2 trackMin = MacroFrameUiLaw.BodyScrollTrack.Minimum(origin, scale);
        Vector2 trackSize = MacroFrameUiLaw.BodyScrollTrack.Size(scale);
        MacroFrameUiLaw.Rect knobRect =
            MacroFrameUiLaw.BodyScrollKnob(_macroBodyScroll, _macroBody);
        Vector2 knobSize = knobRect.Size(scale);
        Vector2 knobMin = knobRect.Minimum(origin, scale);
        uint knob = _gameplayArt.Handle(@"Interface\Buttons\UI-ScrollBar-Knob");
        if (knob != 0)
            draw.AddImage((nint)knob, knobMin, knobMin + knobSize,
                MacroFrameUiLaw.ScrollUvMin, MacroFrameUiLaw.ScrollUvMax);
        ImGui.SetCursorScreenPos(trackMin);
        ImGui.InvisibleButton("##macro-body-scroll-track", trackSize);
        if (ImGui.IsItemActive())
        {
            float travel = trackSize.Y - knobSize.Y;
            float localY = ImGui.GetIO().MousePos.Y - trackMin.Y - knobSize.Y * .5f;
            _macroBodyScroll = MacroFrameUiLaw.ClampBodyScroll(
                Math.Clamp(localY / MathF.Max(1, travel), 0, 1) * maximum,
                _macroBody);
        }
    }

    private void DrawMacroPopup(Vector2 macroOrigin, float scale)
    {
        if (_gameplayArt is null) return;
        EnsureMacroIconsLoaded();
        Vector2 origin = MacroFrameUiLaw.PopupMinimum(macroOrigin, scale);
        Vector2 size = MacroFrameUiLaw.PopupSize;
        ImGui.SetNextWindowPos(origin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size * scale, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);
        // "Enter Macro Name" / "Choose an Icon" is a child dialog of MacroFrame and overlaps a
        // 40px column of it. It must not carry NoBringToFrontOnFocus: that flag creates a window
        // at the BOTTOM of the display order, and this popup is always created after its own
        // owner, so it would open buried underneath the frame that spawned it.
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoNav;
        if (!ImGui.Begin("##macro-popup", flags))
        {
            ImGui.End();
            return;
        }

        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        draw.PushClipRectFullScreen();
        foreach (MacroFrameUiLaw.ArtPiece piece in MacroFrameUiLaw.PopupArt)
            DrawArt(draw, piece.Path, piece.Rect.Minimum(origin, scale),
                piece.Rect.LogicalSize, scale);
        GameText.Draw(draw, "GameFontHighlightSmall", MacroFrameUiLaw.PopupNameText,
            origin + MacroFrameUiLaw.PopupNameLabel * scale, scale);
        GameText.Draw(draw, "GameFontHighlightSmall", MacroFrameUiLaw.PopupIconText,
            origin + MacroFrameUiLaw.PopupIconLabel * scale, scale);

        DrawMacroPopupNameEdit(draw, origin, scale);
        DrawMacroPopupIconGrid(draw, origin, scale);

        string name = ReadBuffer(_macroPopupName).Trim();
        bool existingIcon = _macroPopupMode == MacroPopupMode.Edit &&
            _selectedMacro >= 0 && _selectedMacro < _macros.Count &&
            _macros[_selectedMacro].IconPath.Length > 0;
        bool okayEnabled = MacroFrameUiLaw.OkayEnabled(_macroPopupMode, name,
            _macroPopupSelectedIcon, existingIcon);
        MacroFrameUiLaw.Rect okay = MacroFrameUiLaw.OkayButton;
        if (VanillaButton(draw, "Okay##macro-popup", "Okay",
                okay.Minimum(origin, scale), okay.LogicalSize, scale,
                okayEnabled))
            CloseMacroPopup(accepted: true);
        MacroFrameUiLaw.Rect cancel = MacroFrameUiLaw.CancelButton;
        if (VanillaButton(draw, "Cancel##macro-popup", "Cancel",
                cancel.Minimum(origin, scale), cancel.LogicalSize, scale))
            CloseMacroPopup(accepted: false);

        draw.PopClipRect();
        ImGui.End();
    }

    private void DrawMacroPopupNameEdit(ImDrawListPtr draw, Vector2 origin, float scale)
    {
        const string borderPath = @"Interface\ClassTrainerFrame\UI-ClassTrainer-FilterBorder";
        uint border = _gameplayArt?.Handle(borderPath) ?? 0;
        MacroFrameUiLaw.Rect box = MacroFrameUiLaw.NameEdit;
        if (border != 0)
            foreach (MacroFrameUiLaw.TextureSlice slice in MacroFrameUiLaw.NameBorderSlices)
            {
                Vector2 at = slice.Rect.Minimum(origin, scale);
                draw.AddImage((nint)border, at, at + slice.Rect.Size(scale),
                    slice.UvMin, slice.UvMax);
            }
        // MacroPopupEditBox is a bare 200x20 EditBox at (29,35) with no text insets: the text
        // starts at the box's left edge and is centred vertically in the 20 px. The FilterBorder
        // art around it is decoration hung off the same corner. The raw ImGui widget added its
        // own frame padding, which pushed the text right and up (owner, 2026-09-03).
        VanillaBareInputText("##macro-popup-name", _macroPopupName,
            box.Minimum(origin, scale), box.LogicalSize, Vector2.Zero, scale);
    }

    private void DrawMacroPopupIconGrid(ImDrawListPtr draw, Vector2 origin, float scale)
    {
        _macroPopupRowOffset = MacroFrameUiLaw.ClampRowOffset(_macroPopupRowOffset,
            _macroIcons.Count);
        for (int visible = 0; visible < MacroFrameUiLaw.VisibleIcons; visible++)
        {
            int catalogIndex = MacroFrameUiLaw.CatalogIndex(_macroPopupRowOffset, visible,
                _macroIcons.Count);
            if (catalogIndex < 0) continue;
            MacroFrameUiLaw.Rect rect = MacroFrameUiLaw.IconButton(visible);
            Vector2 min = rect.Minimum(origin, scale);
            uint socket = _gameplayArt?.Handle(@"Interface\Buttons\UI-EmptySlot-Disabled") ?? 0;
            if (socket != 0)
            {
                MacroFrameUiLaw.Rect socketRect = MacroFrameUiLaw.PopupIconSocket(rect);
                draw.AddImage((nint)socket, socketRect.Minimum(origin, scale),
                    socketRect.Minimum(origin, scale) + socketRect.Size(scale));
            }
            uint icon = _gameplayArt?.Handle(_macroIcons[catalogIndex]) ?? 0;
            Vector2 iconMin = min + MacroFrameUiLaw.IconOffset * scale;
            if (icon != 0)
                draw.AddImage((nint)icon, iconMin, iconMin + rect.Size(scale));
            ImGui.SetCursorScreenPos(min);
            ImGui.InvisibleButton($"##macro-popup-icon-{visible}", rect.Size(scale));
            bool hovered = ImGui.IsItemHovered();
            if (ImGui.IsItemClicked()) _macroPopupSelectedIcon = catalogIndex;
            if (hovered)
            {
                uint highlight = _gameplayArt?.AdditiveHandle(
                    @"Interface\Buttons\ButtonHilight-Square") ?? 0;
                if (highlight != 0)
                    draw.AddImage((nint)highlight, min, min + rect.Size(scale));
            }
            if (_macroPopupSelectedIcon == catalogIndex)
            {
                uint check = _gameplayArt?.AdditiveHandle(
                    @"Interface\Buttons\CheckButtonHilight") ?? 0;
                if (check != 0)
                    draw.AddImage((nint)check, min, min + rect.Size(scale));
            }
        }

        // ClassTrainerListScrollFrameTemplate: the carved track art hangs off the two buttons
        // and sits under them; without it the arrows and thumb floated on bare parchment.
        uint track = _gameplayArt?.Handle(@"Interface\ClassTrainerFrame\UI-ClassTrainer-ScrollBar") ?? 0;
        if (track != 0)
        {
            MacroFrameUiLaw.Rect trackTop = MacroFrameUiLaw.PopupScrollTrackTop;
            draw.AddImage((nint)track, trackTop.Minimum(origin, scale),
                trackTop.Minimum(origin, scale) + trackTop.Size(scale),
                MacroFrameUiLaw.PopupScrollTrackTopUvMin, MacroFrameUiLaw.PopupScrollTrackTopUvMax);
            MacroFrameUiLaw.Rect trackBottom = MacroFrameUiLaw.PopupScrollTrackBottom;
            draw.AddImage((nint)track, trackBottom.Minimum(origin, scale),
                trackBottom.Minimum(origin, scale) + trackBottom.Size(scale),
                MacroFrameUiLaw.PopupScrollTrackBottomUvMin, MacroFrameUiLaw.PopupScrollTrackBottomUvMax);
        }
        int maximum = MacroFrameUiLaw.MaximumRowOffset(_macroIcons.Count);
        DrawMacroPopupScrollButton(draw, origin, scale, up: true,
            enabled: _macroPopupRowOffset > 0);
        DrawMacroPopupScrollButton(draw, origin, scale, up: false,
            enabled: _macroPopupRowOffset < maximum);
        MacroFrameUiLaw.Rect knobRect =
            MacroFrameUiLaw.PopupScrollKnob(_macroPopupRowOffset, maximum);
        Vector2 knobMin = knobRect.Minimum(origin, scale);
        uint knob = _gameplayArt?.Handle(@"Interface\Buttons\UI-ScrollBar-Knob") ?? 0;
        if (knob != 0)
            draw.AddImage((nint)knob, knobMin, knobMin + knobRect.Size(scale),
                MacroFrameUiLaw.ScrollUvMin, MacroFrameUiLaw.ScrollUvMax);

        Vector2 popupMax = origin + MacroFrameUiLaw.PopupSize * scale;
        if (ImGui.IsMouseHoveringRect(origin, popupMax) && ImGui.GetIO().MouseWheel != 0f)
            _macroPopupRowOffset = MacroFrameUiLaw.ClampRowOffset(
                _macroPopupRowOffset - Math.Sign(ImGui.GetIO().MouseWheel), _macroIcons.Count);
    }

    private void DrawMacroPopupScrollButton(ImDrawListPtr draw, Vector2 origin, float scale,
        bool up, bool enabled)
    {
        MacroFrameUiLaw.Rect rect = up ? MacroFrameUiLaw.ScrollUp : MacroFrameUiLaw.ScrollDown;
        Vector2 min = rect.Minimum(origin, scale);
        ImGui.SetCursorScreenPos(min);
        if (!enabled) ImGui.BeginDisabled();
        bool clicked = ImGui.InvisibleButton(up ? "##macro-popup-scroll-up" :
            "##macro-popup-scroll-down", rect.Size(scale));
        bool held = enabled && ImGui.IsItemActive();
        bool hovered = enabled && ImGui.IsItemHovered();
        if (!enabled) ImGui.EndDisabled();
        string direction = up ? "Up" : "Down";
        string state = !enabled ? "Disabled" : held ? "Down" : "Up";
        // UIPanelScrollBarButton: TexCoords .25..75 - the glyph is the centre half of the sheet.
        uint art = _gameplayArt?.Handle(
            $@"Interface\Buttons\UI-ScrollBar-Scroll{direction}Button-{state}") ?? 0;
        if (art != 0)
            draw.AddImage((nint)art, min, min + rect.Size(scale),
                MacroFrameUiLaw.ScrollUvMin, MacroFrameUiLaw.ScrollUvMax);
        if (hovered)
        {
            uint highlight = _gameplayArt?.AdditiveHandle(
                $@"Interface\Buttons\UI-ScrollBar-Scroll{direction}Button-Highlight") ?? 0;
            if (highlight != 0)
                draw.AddImage((nint)highlight, min, min + rect.Size(scale),
                    MacroFrameUiLaw.ScrollUvMin, MacroFrameUiLaw.ScrollUvMax);
        }
        if (enabled && clicked)
            _macroPopupRowOffset = MacroFrameUiLaw.ClampRowOffset(
                _macroPopupRowOffset + (up ? -1 : 1), _macroIcons.Count);
    }
}
