using ImGuiNET;
using System.Numerics;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

/// <summary>
/// Party Tactics panel — Drawing 3 of CRPG_RTS_MMO_PARTY_COMMAND_UI.md, built
/// honestly against what exists today: preferred role and quick-slot AI policy
/// are CLIENT state (persisted in botbars.json for the Phase B rotation work to
/// consume), and everything the server cannot yet honor is disabled with the
/// reason spelled out rather than pretending.
/// </summary>
public sealed partial class GameLoop
{
    private bool _partyTacticsOpen;
    private ulong _partyTacticsGuid;

    private static readonly string[] TacticsRoles = ["Auto", "Tank", "Healer", "DPS"];
    private static readonly string[] TacticsPolicies = ["By tactics", "Emergency only", "Never"];

    private void OpenPartyTactics(ulong guid)
    {
        _partyTacticsOpen = true;
        _partyTacticsGuid = guid;
        // Member-facts server: make sure every member's spellbook is fresh
        // before the quick-slot policy rows resolve their bars (rate-limited).
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

    private void DrawPartyTacticsPanel()
    {
        if (!_partyTacticsOpen || _net is null) return;
        PartyMember[] bots = [.. _partyMembers.Where(m => IsRtsGroupableBot(m.Guid))];
        if (bots.Length == 0) { _partyTacticsOpen = false; return; }
        if (Array.FindIndex(bots, b => b.Guid == _partyTacticsGuid) < 0)
            _partyTacticsGuid = bots[0].Guid;

        float scale = GameplayUiScale();
        ImGui.SetNextWindowSize(new Vector2(430, 310) * scale, ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowBgAlpha(0f);
        if (!ImGui.Begin("###party-tactics", ref _partyTacticsOpen,
                ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoBackground |
                ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            return;
        }
        DrawVanillaPanelChrome("Party Tactics", scale, ref _partyTacticsOpen);
        ImGui.Dummy(new Vector2(1, 8 * scale));

        // ── Member picker rail ───────────────────────────────────────────────
        ImGui.BeginChild("##tactics-picker", new Vector2(110 * scale, 0), true);
        BotBarsDocument doc = LoadBotBars();
        foreach (PartyMember bot in bots)
        {
            bool active = bot.Guid == _partyTacticsGuid;
            if (ImGui.Selectable($"{bot.Name}##pick", active)) _partyTacticsGuid = bot.Guid;
            string pickRole = doc.BotRoles.GetValueOrDefault(bot.Name, "DPS");
            ImGui.SameLine(ImGui.GetContentRegionAvail().X - 4 * scale);
            ImGui.TextDisabled(RoleGlyph(pickRole));
        }
        ImGui.EndChild();
        ImGui.SameLine();

        PartyMember member = bots[Array.FindIndex(bots, b => b.Guid == _partyTacticsGuid)];
        ImGui.BeginChild("##tactics-main", new Vector2(0, 0));
        // Explicit-position layout: every control sits at fixed offsets from
        // the content origin with UNIFORM vanilla-button rows, so nothing can
        // staircase or ride up over the panel chrome; the child clips + scrolls.
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        Vector2 c0 = ImGui.GetCursorScreenPos();
        float y = 0f;

        // ── Preferred role ───────────────────────────────────────────────────
        GameText.Draw(dl, "GameFontNormal", "Preferred role", c0 + new Vector2(0, y) * scale, scale);
        string role = doc.BotRoles.GetValueOrDefault(member.Name, "DPS");
        float x = 96f;
        foreach (string choice in TacticsRoles)
        {
            bool picked = choice == role || (choice == "Auto" && role.Length == 0);
            Vector2 bmin = c0 + new Vector2(x, y - 3f) * scale;
            var bsize = new Vector2(58f, 20f);
            if (VanillaButton(dl, $"##role-{choice}", choice, bmin, bsize, scale) &&
                choice != role)
            {
                if (choice == "Auto") doc.BotRoles.Remove(member.Name);
                else doc.BotRoles[member.Name] = choice;
                SaveBotBars();
            }
            if (picked)
                dl.AddRect(bmin, bmin + bsize * scale, 0xffd0b060, 3f * scale,
                    ImDrawFlags.None, MathF.Max(1f, scale));
            x += 62f;
        }
        y += 22f;
        ImGui.SetCursorScreenPos(c0 + new Vector2(0, y) * scale);
        ImGui.TextDisabled("Client preference — feeds the rotation work; the server-accepted\n" +
            "effective role arrives with the Phase B member-facts snapshot.");
        y += 34f;

        // ── Four quick commands + AI policy ──────────────────────────────────
        GameText.Draw(dl, "GameFontNormal", "Quick actions", c0 + new Vector2(0, y) * scale, scale);
        ImGui.SetCursorScreenPos(c0 + new Vector2(96f, y + 1f) * scale);
        ImGui.TextDisabled("(the AI policy per quick slot; cycles on click)");
        y += 20f;
        PlayerActions store = ActionsFor(member.Guid);
        if (store.OccupiedCount == 0) EnsureBotBarForViewing(member.Guid);
        int shown = 0;
        for (int slot = 0; slot < 12 && shown < 4; slot++)
        {
            if (store[slot] is not ActionSlot action || action.Kind != 0 ||
                _spellCatalog?.TryGet(action.ActionId, out SpellInfo spell) != true)
                continue;
            shown++;
            GameText.Draw(dl, "GameFontNormalSmall", spell.Name,
                c0 + new Vector2(0, y + 3f) * scale, scale);
            string policy = QuickSlotPolicy(member.Name, slot);
            if (VanillaButton(dl, $"##pol{slot}", policy,
                    c0 + new Vector2(160f, y) * scale, new Vector2(120f, 20f), scale))
            {
                int next = (Array.IndexOf(TacticsPolicies, policy) + 1) % TacticsPolicies.Length;
                SetQuickSlotPolicy(member.Name, slot, TacticsPolicies[next]);
            }
            if (ImGui.IsItemHovered())
                HoverTip("May the AI use this on its own? Cycles By tactics /\n" +
                    "Emergency only / Never. Stored now, enforced by the Phase B\n" +
                    "rotation integration.");
            y += 24f;
        }
        if (shown == 0)
        {
            ImGui.SetCursorScreenPos(c0 + new Vector2(0, y) * scale);
            ImGui.TextDisabled("Abilities unknown — the member-facts server syncs them, or\n" +
                "possess this companion once.");
            y += 34f;
        }
        y += 8f;

        // ── Stances — honest about the missing server hook ───────────────────
        GameText.Draw(dl, "GameFontNormal", "Stance", c0 + new Vector2(0, y) * scale, scale);
        x = 96f;
        foreach (string stance in (string[])["Guard", "Defensive", "Passive"])
        {
            VanillaButton(dl, $"##stance-{stance}", stance,
                c0 + new Vector2(x, y - 3f) * scale, new Vector2(74f, 20f), scale,
                enabled: false);
            x += 78f;
        }
        ImGui.SetCursorScreenPos(c0 + new Vector2(x + 4f, y + 1f) * scale);
        ImGui.TextDisabled("needs the server stance order (Phase B)");
        y += 28f;

        if (VanillaButton(dl, "##from-tactics", "Party Inventory",
                c0 + new Vector2(0, y) * scale, new Vector2(120f, 20f), scale))
            OpenPartyInventory(member.Guid);
        ImGui.SetCursorScreenPos(c0 + new Vector2(128f, y + 4f) * scale);
        ImGui.TextDisabled("everyone's equipment and bags, side by side");
        y += 30f;

        ImGui.SetCursorScreenPos(c0 + new Vector2(0, y) * scale);
        ImGui.TextDisabled("Server law: human control > safety > manual order > engagement >\n" +
            "rotation > fallback. A role badge never becomes client authority.");
        y += 36f;
        ImGui.SetCursorScreenPos(c0 + new Vector2(0, y) * scale);
        ImGui.Dummy(new Vector2(1, 1));
        ImGui.EndChild();
        ImGui.End();
    }
}
