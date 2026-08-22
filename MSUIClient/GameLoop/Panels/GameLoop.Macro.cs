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
    private int _selectedMacro;
    private readonly byte[] _macroName = new byte[32];
    private string _macroBody = "";
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
        SelectMacro(Math.Clamp(_selectedMacro, 0, _macros.Count - 1));
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
        if (_macrosLoaded) return;
        _macrosLoaded = true;
        try
        {
            string path = Path.Combine(_config.RepoRoot, "macros.json");
            if (File.Exists(path)) _macros.AddRange(JsonSerializer.Deserialize<List<MacroDefinition>>(File.ReadAllText(path)) ?? []);
        }
        catch (Exception ex) { Console.WriteLine($"[macros] load failed: {ex.Message}"); }
        while (_macros.Count < 18) _macros.Add(new MacroDefinition());
        if (_macros.Count > 18) _macros.RemoveRange(18, _macros.Count - 18);
    }

    private void SaveMacros()
    {
        EnsureMacrosLoaded();
        CommitMacroEditor();
        File.WriteAllText(Path.Combine(_config.RepoRoot, "macros.json"),
            JsonSerializer.Serialize(_macros, new JsonSerializerOptions { WriteIndented = true }));
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
                target = _macros.FindIndex(macro =>
                    macro.Name.Length == 0 && macro.Body.Length == 0);
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
    }

    private void CommitMacroEditor()
    {
        if (!_macrosLoaded || _selectedMacro < 0 || _selectedMacro >= _macros.Count) return;
        _macros[_selectedMacro].Name = ReadBuffer(_macroName).Trim();
        _macros[_selectedMacro].Body = _macroBody;
    }

    private void ExecuteMacro(uint id)
    {
        EnsureMacrosLoaded();
        int index = checked((int)id - 1);
        if (index < 0 || index >= _macros.Count) return;
        CommitMacroEditor();
        foreach (string raw in _macros[index].Body.Replace("\r", "").Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("/cast ", StringComparison.OrdinalIgnoreCase))
            {
                string name = line[6..].Trim();
                SpellInfo? spell = _spellCatalog?.FindKnownByName(name, _actions.KnownSpells);
                if (spell is { } found) TryCast(found.Id); else AddChatMessage($"Unknown spell: {name}");
            }
            else if (line.StartsWith("/use ", StringComparison.OrdinalIgnoreCase))
            {
                string name = line[5..].Trim();
                ItemTemplate? item = _items?.FindByName(name);
                if (item is not null) UseItemAction(item.Entry); else AddChatMessage($"Unknown item: {name}");
            }
            else if (line.StartsWith("/say ", StringComparison.OrdinalIgnoreCase)) _net?.SendChatSay(line[5..].Trim());
            else if (line.Equals("/startattack", StringComparison.OrdinalIgnoreCase) && _selectionGuid != 0)
                CommitSelection(_selectionGuid, beginAttack: true);
            else AddChatMessage($"Unsupported macro command: {line}");
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
            if (line.StartsWith("/cast ", StringComparison.OrdinalIgnoreCase) &&
                _spellCatalog?.FindKnownByName(line[6..].Trim(), _actions.KnownSpells) is { } spell)
            {
                WorldEntity? player = _net is not null && _entities.TryGet(_net.PlayerGuid,
                    out WorldEntity owner) ? owner : null;
                return ResolveSpellActionIcon(spell, player);
            }
            if (line.StartsWith("/use ", StringComparison.OrdinalIgnoreCase) &&
                _items?.FindByName(line[5..].Trim()) is { } item) return item.IconPath;
        }
        return @"Interface\Icons\INV_Misc_QuestionMark.blp";
    }

    private void DrawMacroFrame()
    {
        if (!_macroOpen || _gameplayArt is null) return;
        EnsureMacrosLoaded();
        float s = GameplayUiScale();
        if (!BeginVanillaWindow("##macro", new Vector2(0, 104), new Vector2(384, 512), out ImDrawListPtr dl,
                out Vector2 origin, out s)) return;
        DrawArt(dl, @"Interface\PaperDollInfoFrame\UI-Character-General-TopLeft", origin, new(256), s);
        DrawArt(dl, @"Interface\PaperDollInfoFrame\UI-Character-General-TopRight", origin + new Vector2(256, 0) * s, new(128, 256), s);
        DrawArt(dl, @"Interface\MacroFrame\MacroFrame-BotLeft", origin + new Vector2(0, 256) * s, new(256), s);
        DrawArt(dl, @"Interface\MacroFrame\MacroFrame-BotRight", origin + new Vector2(256, 256) * s, new(128, 256), s);
        DrawArt(dl, @"Interface\MacroFrame\MacroFrame-Icon", origin + new Vector2(7, 6) * s, new(60), s);
        DrawCenteredText(dl, origin + new Vector2(192, 24) * s, "Create Macros", 14 * s, VanillaGold);

        for (int i = 0; i < 18; i++)
        {
            int col = i % 6, row = i / 6;
            Vector2 min = origin + new Vector2(31 + col * 48, 74 + row * 48) * s;
            DrawArt(dl, @"Interface\Buttons\UI-Quickslot2", min, new(40), s);
            uint icon = _gameplayArt.Handle(MacroIcon((uint)i + 1));
            if (icon != 0) dl.AddImage((nint)icon, min + new Vector2(3) * s, min + new Vector2(37) * s);
            ImGui.SetCursorScreenPos(min);
            ImGui.InvisibleButton($"##macro-slot-{i}", new Vector2(40) * s);
            if (ImGui.IsItemActivated()) { _pressedMacroId = (uint)i + 1; _macroPressPosition = ImGui.GetIO().MousePos; }
            if (ImGui.IsItemActive() && _pressedMacroId == (uint)i + 1 &&
                Vector2.DistanceSquared(_macroPressPosition, ImGui.GetIO().MousePos) > 36 * s * s)
                _draggingMacroId = _pressedMacroId;
            if (ImGui.IsItemClicked()) SelectMacro(i);
            if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) ExecuteMacro((uint)i + 1);
            if (_selectedMacro == i) dl.AddRect(min, min + new Vector2(40) * s, VanillaGold, 0, ImDrawFlags.None, 2 * s);
        }

        dl.AddText(ImGui.GetFont(),10*s,origin+new Vector2(31,218)*s,VanillaGold,"Macro Name");
        if (!_macroPopupOpen)
            VanillaInputText(dl,"##macro-name",_macroName,origin+new Vector2(31,230)*s,new Vector2(302,22),s);
        dl.AddText(ImGui.GetFont(),10*s,origin+new Vector2(31,255)*s,VanillaGold,"Enter Macro Commands:");
        if (!_macroPopupOpen)
            VanillaInputText(dl,"##macro-text",ref _macroBody,255,origin+new Vector2(31,270)*s,new Vector2(302,125),s,true);
        bool hasEmptyMacro = _macros.Any(x => x.Name.Length == 0 && x.Body.Length == 0);
        if (VanillaButton(dl, "New##macro", "New", origin + new Vector2(31, 410) * s,
                new Vector2(80, 22), s, !_macroPopupOpen && hasEmptyMacro))
            OpenMacroPopup(MacroPopupMode.New);
        if (VanillaButton(dl, "Delete##macro", "Delete", origin + new Vector2(119, 410) * s,
                new Vector2(80, 22), s, !_macroPopupOpen))
        { _macros[_selectedMacro] = new MacroDefinition(); SelectMacro(_selectedMacro); }
        if (VanillaButton(dl, "Run##macro", "Run", origin + new Vector2(207, 410) * s,
                new Vector2(80, 22), s, !_macroPopupOpen))
            ExecuteMacro((uint)_selectedMacro + 1);
        if (VanillaButton(dl, "Exit##macro", "Exit", origin + new Vector2(295, 410) * s, new Vector2(60, 22), s))
            CloseMacros();
        if (VanillaButton(dl, "Change##macro", "Change Name/Icon",
                origin + new Vector2(31, 438) * s, new Vector2(170, 22), s,
                !_macroPopupOpen))
            OpenMacroPopup(MacroPopupMode.Edit);
        DrawImageButton(dl, "##macro-close", origin + new Vector2(326, 14) * s, new Vector2(32) * s,
            @"Interface\Buttons\UI-Panel-MinimizeButton-Up", @"Interface\Buttons\UI-Panel-MinimizeButton-Down",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");
        if (ImGui.IsItemClicked()) CloseMacros();
        if (_draggingMacroId != 0)
        {
            uint icon = _gameplayArt.Handle(MacroIcon(_draggingMacroId));
            if (icon != 0) { Vector2 min = ImGui.GetIO().MousePos + new Vector2(10) * s; ImGui.GetForegroundDrawList().AddImage((nint)icon, min, min + new Vector2(32) * s); }
        }
        ImGui.End();
        if (_macroPopupOpen) DrawMacroPopup(origin, s);
    }

    private void DrawMacroPopup(Vector2 macroOrigin, float scale)
    {
        if (_gameplayArt is null) return;
        EnsureMacroIconsLoaded();
        Vector2 origin = MacroFrameUiLaw.PopupMinimum(macroOrigin, scale);
        Vector2 size = new(MacroFrameUiLaw.PopupWidth, MacroFrameUiLaw.PopupHeight);
        ImGui.SetNextWindowPos(origin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size * scale, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoBringToFrontOnFocus;
        if (!ImGui.Begin("##macro-popup", flags))
        {
            ImGui.End();
            return;
        }

        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        draw.PushClipRectFullScreen();
        DrawArt(draw, @"Interface\MacroFrame\MacroPopup-TopLeft", origin,
            new Vector2(256), scale);
        DrawArt(draw, @"Interface\MacroFrame\MacroPopup-TopRight",
            origin + new Vector2(256, 0) * scale, new Vector2(64, 256), scale);
        DrawArt(draw, @"Interface\MacroFrame\MacroPopup-BotLeft",
            origin + new Vector2(0, 256) * scale, new Vector2(256, 64), scale);
        DrawArt(draw, @"Interface\MacroFrame\MacroPopup-BotRight",
            origin + new Vector2(256, 256) * scale, new Vector2(64), scale);
        GameText.Draw(draw, "GameFontHighlightSmall", "Enter Macro Name",
            origin + new Vector2(24, 21) * scale, scale);
        GameText.Draw(draw, "GameFontHighlightSmall", "Choose an Icon",
            origin + new Vector2(24, 69) * scale, scale);

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
                okay.Minimum(origin, scale), new Vector2(okay.Width, okay.Height), scale,
                okayEnabled))
            CloseMacroPopup(accepted: true);
        MacroFrameUiLaw.Rect cancel = MacroFrameUiLaw.CancelButton;
        if (VanillaButton(draw, "Cancel##macro-popup", "Cancel",
                cancel.Minimum(origin, scale), new Vector2(cancel.Width, cancel.Height), scale))
            CloseMacroPopup(accepted: false);

        draw.PopClipRect();
        ImGui.End();
    }

    private void DrawMacroPopupNameEdit(ImDrawListPtr draw, Vector2 origin, float scale)
    {
        const string borderPath = @"Interface\ClassTrainerFrame\UI-ClassTrainer-FilterBorder";
        uint border = _gameplayArt?.Handle(borderPath) ?? 0;
        MacroFrameUiLaw.Rect box = MacroFrameUiLaw.NameEdit;
        Vector2 boxMin = box.Minimum(origin, scale);
        if (border != 0)
        {
            Vector2 artMin = origin + new Vector2(18, 35) * scale;
            draw.AddImage((nint)border, artMin, artMin + new Vector2(12, 29) * scale,
                new Vector2(0, 0), new Vector2(.09375f, 1));
            draw.AddImage((nint)border, artMin + new Vector2(12, 0) * scale,
                artMin + new Vector2(187, 29) * scale,
                new Vector2(.09375f, 0), new Vector2(.90625f, 1));
            draw.AddImage((nint)border, artMin + new Vector2(187, 0) * scale,
                artMin + new Vector2(199, 29) * scale,
                new Vector2(.90625f, 0), Vector2.One);
        }
        ImGui.SetCursorScreenPos(boxMin + new Vector2(3, 0) * scale);
        ImGui.SetNextItemWidth((box.Width - 6) * scale);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.Border, Vector4.Zero);
        ImGui.InputText("##macro-popup-name", _macroPopupName,
            (uint)_macroPopupName.Length);
        ImGui.PopStyleColor(4);
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
                draw.AddImage((nint)socket, min + new Vector2(-14, -15) * scale,
                    min + new Vector2(50, 49) * scale);
            uint icon = _gameplayArt?.Handle(_macroIcons[catalogIndex]) ?? 0;
            if (icon != 0)
                draw.AddImage((nint)icon, min, min + new Vector2(36) * scale);
            ImGui.SetCursorScreenPos(min);
            ImGui.InvisibleButton($"##macro-popup-icon-{visible}", new Vector2(36) * scale);
            bool hovered = ImGui.IsItemHovered();
            if (ImGui.IsItemClicked()) _macroPopupSelectedIcon = catalogIndex;
            if (hovered)
            {
                uint highlight = _gameplayArt?.AdditiveHandle(
                    @"Interface\Buttons\ButtonHilight-Square") ?? 0;
                if (highlight != 0)
                    draw.AddImage((nint)highlight, min, min + new Vector2(36) * scale);
            }
            if (_macroPopupSelectedIcon == catalogIndex)
            {
                uint check = _gameplayArt?.AdditiveHandle(
                    @"Interface\Buttons\CheckButtonHilight") ?? 0;
                if (check != 0)
                    draw.AddImage((nint)check, min, min + new Vector2(36) * scale);
            }
        }

        int maximum = MacroFrameUiLaw.MaximumRowOffset(_macroIcons.Count);
        DrawMacroPopupScrollButton(draw, origin, scale, up: true,
            enabled: _macroPopupRowOffset > 0);
        DrawMacroPopupScrollButton(draw, origin, scale, up: false,
            enabled: _macroPopupRowOffset < maximum);
        MacroFrameUiLaw.Rect track = MacroFrameUiLaw.ScrollTrack;
        float fraction = maximum == 0 ? 0f : (float)_macroPopupRowOffset / maximum;
        Vector2 knobMin = track.Minimum(origin, scale) +
            new Vector2(0, fraction * (track.Height - 24f)) * scale;
        uint knob = _gameplayArt?.Handle(@"Interface\Buttons\UI-ScrollBar-Knob") ?? 0;
        if (knob != 0)
            draw.AddImage((nint)knob, knobMin, knobMin + new Vector2(16, 24) * scale);

        Vector2 popupMax = origin + new Vector2(MacroFrameUiLaw.PopupWidth,
            MacroFrameUiLaw.PopupHeight) * scale;
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
        uint art = _gameplayArt?.Handle(
            $@"Interface\Buttons\UI-ScrollBar-Scroll{direction}Button-{state}") ?? 0;
        if (art != 0) draw.AddImage((nint)art, min, min + rect.Size(scale));
        if (hovered)
        {
            uint highlight = _gameplayArt?.AdditiveHandle(
                $@"Interface\Buttons\UI-ScrollBar-Scroll{direction}Button-Highlight") ?? 0;
            if (highlight != 0)
                draw.AddImage((nint)highlight, min, min + rect.Size(scale));
        }
        if (enabled && clicked)
            _macroPopupRowOffset = MacroFrameUiLaw.ClampRowOffset(
                _macroPopupRowOffset + (up ? -1 : 1), _macroIcons.Count);
    }
}
