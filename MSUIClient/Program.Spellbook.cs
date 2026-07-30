using System.Numerics;
using ImGuiNET;
using Silk.NET.Input;
using MSUIClient.Formats;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private bool _spellbookOpen;
    private bool _spellbookKeyWasDown;
    private uint _spellbookLine;
    private int _spellbookPage;
    private uint _pressedSpellId;
    private uint _draggingSpellId;
    private Vector2 _spellPressPosition;

    private void UpdateSpellbookInput(bool typing)
    {
        bool down = _window.IsDown(Key.P);
        if (down && !_spellbookKeyWasDown && !typing && _net is { IsInWorld: true })
        {
            _spellbookOpen = !_spellbookOpen;
            if (_spellbookOpen) _characterOpen = false;
        }
        _spellbookKeyWasDown = down;
    }

    private void DrawSpellbook()
    {
        if (!_spellbookOpen || _gameplayArt is null || _spellCatalog is null) return;
        float s = GameplayUiScale();
        Vector2 p = new(0, 8f * s);
        ImGui.SetNextWindowPos(p, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(416, 512) * s, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        if (!ImGui.Begin("##spellbook", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav))
        { ImGui.End(); return; }
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        DrawSpellbookArt(dl, p, s);

        var known = _actions.KnownSpells
            .Select(id => _spellCatalog.TryGet(id, out SpellInfo spell) ? (Id: id, Spell: spell) : default)
            .Where(x => x.Id != 0 && !x.Spell.Passive)
            .ToList();
        var tabs = known.GroupBy(x => _skillLines?.SpellLine(x.Id) ?? 0)
            .Select(g => (Id: g.Key, Name: g.Key != 0 && _skillLines?.TryGet(g.Key, out SkillLineInfo line) == true
                    ? line.Name : "General",
                Icon: g.Key != 0 && _skillLines?.TryGet(g.Key, out SkillLineInfo iconLine) == true
                    ? iconLine.IconPath : @"Interface\Icons\INV_Misc_Book_09",
                Spells: g.OrderBy(x => x.Spell.Name).ThenBy(x => x.Spell.Rank).ToList()))
            .OrderBy(x => x.Id == 0 ? 0 : 1).ThenBy(x => x.Name).Take(8).ToList();
        if (tabs.Count > 0 && tabs.All(t => t.Id != _spellbookLine)) { _spellbookLine = tabs[0].Id; _spellbookPage = 0; }
        var active = tabs.FirstOrDefault(t => t.Id == _spellbookLine);
        int pages = Math.Max(1, ((active.Spells?.Count ?? 0) + 11) / 12);
        _spellbookPage = Math.Clamp(_spellbookPage, 0, pages - 1);

        for (int i = 0; i < 12; i++)
        {
            int column = i / 6, row = i % 6;
            Vector2 min = p + new Vector2(34 + column * 157, 85 + row * 51) * s;
            int index = _spellbookPage * 12 + i;
            if (active.Spells is null || index >= active.Spells.Count) continue;
            var entry = active.Spells[index];
            DrawSpellButton(dl, min, s, entry.Id, entry.Spell);
        }
        for (int i = 0; i < tabs.Count; i++)
            DrawSpellTab(dl, p + new Vector2(352, 65 + i * 49) * s, s, tabs[i].Id, tabs[i].Name, tabs[i].Icon);

        DrawCenteredText(dl, p + new Vector2(178, 416) * s, $"Page {_spellbookPage + 1}", 12f * s, 0xffffd100);
        DrawPageButton(dl, p + new Vector2(34, 391) * s, true, s, _spellbookPage > 0);
        DrawPageButton(dl, p + new Vector2(298, 391) * s, false, s, _spellbookPage + 1 < pages);

        Vector2 close = p + new Vector2(324, 9) * s;
        DrawImageButton(dl, "##spell-close", close, new Vector2(32) * s,
            @"Interface\Buttons\UI-Panel-MinimizeButton-Up", @"Interface\Buttons\UI-Panel-MinimizeButton-Down",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");
        if (ImGui.IsItemClicked()) _spellbookOpen = false;
        ImGui.End();

        if (_pressedSpellId != 0 && ImGui.IsMouseDown(ImGuiMouseButton.Left) &&
            Vector2.Distance(ImGui.GetIO().MousePos, _spellPressPosition) > 6f * s)
            _draggingSpellId = _pressedSpellId;
        if (_draggingSpellId != 0 && _spellCatalog.TryGet(_draggingSpellId, out SpellInfo dragged))
        {
            uint icon = _gameplayArt.Handle(dragged.IconPath);
            if (icon != 0)
            {
                Vector2 min = ImGui.GetIO().MousePos + new Vector2(10) * s;
                ImGui.GetForegroundDrawList().AddImage((nint)icon, min, min + new Vector2(32) * s,
                    Vector2.Zero, Vector2.One, 0xccffffff);
            }
        }
        if (ImGui.IsMouseReleased(ImGuiMouseButton.Left)) _pressedSpellId = 0;
    }

    private void DrawSpellbookArt(ImDrawListPtr dl, Vector2 p, float s)
    {
        DrawArt(dl, @"Interface\Spellbook\UI-SpellbookPanel-TopLeft", p, new(256, 256), s);
        DrawArt(dl, @"Interface\Spellbook\UI-SpellbookPanel-TopRight", p + new Vector2(256, 0) * s, new(128, 256), s);
        DrawArt(dl, @"Interface\Spellbook\UI-SpellbookPanel-BotLeft", p + new Vector2(0, 256) * s, new(256, 256), s);
        DrawArt(dl, @"Interface\Spellbook\UI-SpellbookPanel-BotRight", p + new Vector2(256, 256) * s, new(128, 256), s);
        DrawArt(dl, @"Interface\Spellbook\Spellbook-Icon", p + new Vector2(10, 8) * s, new(58, 58), s);
        DrawCenteredText(dl, p + new Vector2(198, 26) * s, "Spellbook", 14f * s, 0xffffffff);
    }

    private void DrawSpellButton(ImDrawListPtr dl, Vector2 min, float s, uint id, SpellInfo spell)
    {
        Vector2 max = min + new Vector2(37) * s;
        uint bg = _gameplayArt!.Handle(@"Interface\Spellbook\UI-Spellbook-SpellBackground");
        if (bg != 0) dl.AddImage((nint)bg, min - new Vector2(3, -3) * s, min + new Vector2(61, 61) * s);
        uint icon = _gameplayArt.Handle(spell.IconPath);
        if (icon != 0) dl.AddImage((nint)icon, min, max);
        uint ring = _gameplayArt.Handle(@"Interface\Buttons\UI-Quickslot2");
        if (ring != 0)
        {
            Vector2 center = (min + max) * .5f, half = new(32f * s);
            dl.AddImage((nint)ring, center - half, center + half);
        }
        dl.AddText(ImGui.GetFont(), 11f * s, min + new Vector2(45, 4) * s, 0xffffd100, spell.Name);
        if (!string.IsNullOrWhiteSpace(spell.Rank))
            dl.AddText(ImGui.GetFont(), 9f * s, min + new Vector2(45, 20) * s, 0xffaaaaaa, spell.Rank);
        ImGui.SetCursorScreenPos(min);
        bool clicked = ImGui.InvisibleButton($"##spell-{id}", new Vector2(145, 37) * s);
        if (ImGui.IsItemActivated()) { _pressedSpellId = id; _spellPressPosition = ImGui.GetIO().MousePos; }
        if (clicked && _draggingSpellId == 0) TryCast(id);
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(spell.Name);
            if (!string.IsNullOrWhiteSpace(spell.Rank)) ImGui.TextDisabled(spell.Rank);
            if (!string.IsNullOrWhiteSpace(spell.Description)) ImGui.TextWrapped(spell.Description);
            ImGui.EndTooltip();
        }
    }

    private void DrawSpellTab(ImDrawListPtr dl, Vector2 min, float s, uint id, string name, string iconPath)
    {
        uint back = _gameplayArt!.Handle(@"Interface\SpellBook\SpellBook-SkillLineTab");
        if (back != 0) dl.AddImage((nint)back, min - new Vector2(3, -11) * s, min + new Vector2(61, 53) * s);
        uint icon = _gameplayArt.Handle(iconPath);
        if (icon != 0) dl.AddImage((nint)icon, min, min + new Vector2(32) * s,
            Vector2.Zero, Vector2.One, id == _spellbookLine ? 0xffffffff : 0xffaaaaaa);
        ImGui.SetCursorScreenPos(min);
        ImGui.InvisibleButton($"##spell-tab-{id}", new Vector2(32) * s);
        if (ImGui.IsItemClicked()) { _spellbookLine = id; _spellbookPage = 0; }
        if (ImGui.IsItemHovered()) { ImGui.BeginTooltip(); ImGui.TextUnformatted(name); ImGui.EndTooltip(); }
    }

    private void DrawPageButton(ImDrawListPtr dl, Vector2 min, bool previous, float s, bool enabled)
    {
        string stem = previous ? "UI-SpellbookIcon-PrevPage" : "UI-SpellbookIcon-NextPage";
        DrawImageButton(dl, previous ? "##spell-prev" : "##spell-next", min, new Vector2(32) * s,
            $@"Interface\Buttons\{stem}-{(enabled ? "Up" : "Disabled")}",
            $@"Interface\Buttons\{stem}-Down", @"Interface\Buttons\UI-Common-MouseHilight");
        if (enabled && ImGui.IsItemClicked()) _spellbookPage += previous ? -1 : 1;
    }
}
