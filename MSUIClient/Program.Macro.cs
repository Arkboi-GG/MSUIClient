using System.Numerics;
using System.Text;
using System.Text.Json;
using ImGuiNET;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private sealed class MacroDefinition
    {
        public string Name { get; set; } = "";
        public string Body { get; set; } = "";
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

    private void OpenMacros()
    {
        EnsureMacrosLoaded();
        SelectMacro(Math.Clamp(_selectedMacro, 0, _macros.Count - 1));
        _macroOpen = true;
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
        VanillaInputText(dl,"##macro-name",_macroName,origin+new Vector2(31,230)*s,new Vector2(302,22),s);
        dl.AddText(ImGui.GetFont(),10*s,origin+new Vector2(31,255)*s,VanillaGold,"Enter Macro Commands:");
        VanillaInputText(dl,"##macro-text",ref _macroBody,255,origin+new Vector2(31,270)*s,new Vector2(302,125),s,true);
        if (VanillaButton(dl, "New##macro", "New", origin + new Vector2(31, 410) * s, new Vector2(80, 22), s))
        { CommitMacroEditor(); int empty = _macros.FindIndex(x => x.Name.Length == 0 && x.Body.Length == 0); SelectMacro(empty < 0 ? _selectedMacro : empty); }
        if (VanillaButton(dl, "Delete##macro", "Delete", origin + new Vector2(119, 410) * s, new Vector2(80, 22), s))
        { _macros[_selectedMacro] = new MacroDefinition(); SelectMacro(_selectedMacro); }
        if (VanillaButton(dl, "Run##macro", "Run", origin + new Vector2(207, 410) * s, new Vector2(80, 22), s))
            ExecuteMacro((uint)_selectedMacro + 1);
        if (VanillaButton(dl, "Exit##macro", "Exit", origin + new Vector2(295, 410) * s, new Vector2(60, 22), s))
        { SaveMacros(); _macroOpen = false; }
        DrawImageButton(dl, "##macro-close", origin + new Vector2(326, 14) * s, new Vector2(32) * s,
            @"Interface\Buttons\UI-Panel-MinimizeButton-Up", @"Interface\Buttons\UI-Panel-MinimizeButton-Down",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");
        if (ImGui.IsItemClicked()) { SaveMacros(); _macroOpen = false; }
        if (_draggingMacroId != 0)
        {
            uint icon = _gameplayArt.Handle(MacroIcon(_draggingMacroId));
            if (icon != 0) { Vector2 min = ImGui.GetIO().MousePos + new Vector2(10) * s; ImGui.GetForegroundDrawList().AddImage((nint)icon, min, min + new Vector2(32) * s); }
        }
        ImGui.End();
    }
}
