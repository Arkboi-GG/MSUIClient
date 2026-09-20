using ImGuiNET;
using System.Numerics;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

/// <summary>Commander rotation/raid entry point and shared vanilla panel chrome.</summary>
public sealed partial class GameLoop
{
    private bool _partyTacticsOpen;
    private ulong _partyTacticsGuid;

    private void OpenPartyTactics(ulong guid)
    {
        _partyTacticsOpen = true;
        _commanderRaidPositionPending = true;
        _partyTacticsGuid = guid;
        // Member-facts server: make sure every member's spellbook is fresh
        // before the raid planner selects learned spells (rate-limited).
        RequestPartyMemberFacts("party tactics opened");
    }

    /// <summary>Dialog backdrop + header plaque + vanilla close button around the
    /// current NoTitleBar/NoBackground ImGui window.</summary>
    private void DrawVanillaPanelChrome(string title, float scale, ref bool open)
    {
        Vector2 min = ImGui.GetWindowPos();
        Vector2 max = min + ImGui.GetWindowSize();
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        dl.PushClipRectFullScreen();
        _skin?.DrawBackdrop(dl, min, max, WowSkin.Dialog);
        _skin?.HeaderPlaque(dl, min, max.X - min.X, title);
        uint closeArt = _gameplayArt?.Handle(@"Interface\Buttons\UI-Panel-MinimizeButton-Up") ?? 0;
        var closeSize = new Vector2(28f, 28f) * scale;
        Vector2 closeMin = new(max.X - closeSize.X - 4f * scale, min.Y + 2f * scale);
        if (closeArt != 0) dl.AddImage((nint)closeArt, closeMin, closeMin + closeSize);
        dl.PopClipRect();
        Vector2 keep = ImGui.GetCursorScreenPos();
        ImGui.SetCursorScreenPos(closeMin);
        ImGui.InvisibleButton($"##chrome-close-{title}", closeSize);
        if (ImGui.IsItemClicked()) open = false;
        ImGui.SetCursorScreenPos(keep);
    }

    private string QuickSlotPolicy(string botName, int slot)
    {
        BotBarsDocument doc = LoadBotBars();
        return doc.BotSlotPolicies.TryGetValue(botName, out Dictionary<string, string>? slots) &&
               slots.TryGetValue(slot.ToString(), out string? policy)
            ? policy : "By tactics";
    }

    private void SetQuickSlotPolicy(string botName, int slot, string policy)
    {
        BotBarsDocument doc = LoadBotBars();
        if (!doc.BotSlotPolicies.TryGetValue(botName, out Dictionary<string, string>? slots))
            doc.BotSlotPolicies[botName] = slots = [];
        slots[slot.ToString()] = policy;
        SaveBotBars();
    }

    private void DrawPartyTacticsPanel() => DrawCommanderRaidPlanner();
}
