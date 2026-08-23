using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private ulong _duelArbiter;
    private ulong _duelPendingChallenger;
    private uint _duelCountdownRemaining;
    private double _duelCountdownNextAt;

    private void ApplyDuelRequested(byte[] body)
    {
        DuelRequestedWire wire = DuelPackets.ParseRequested(body);
        if (_ignored.Contains(wire.Challenger))
        {
            _net?.DuelCancelled(wire.Arbiter);
            return;
        }
        _duelArbiter = wire.Arbiter;
        _duelPendingChallenger = 0;
        if (wire.Challenger == LocalPlayerGuid)
        {
            AddChatMessage(DuelFrameUiLaw.OwnRequestLine);
            _net?.DuelAccepted(wire.Arbiter);
            return;
        }
        _duelPendingChallenger = wire.Challenger;
        if (!_playerNames.ContainsKey(wire.Challenger)) _net?.NameQuery(wire.Challenger);
        TryShowPendingDuelRequest();
    }

    private void ApplyDuelComplete(byte[] body)
    {
        bool started = DuelPackets.ParseComplete(body);
        _duelCountdownRemaining = 0;
        if (_duelArbiter == 0) return;
        if (!started) AddChatMessage(DuelFrameUiLaw.CancelledLine);
        _duelArbiter = 0;
        _duelPendingChallenger = 0;
        HideDuelPopups();
    }

    private void ApplyDuelWinner(byte[] body)
    {
        DuelWinnerWire wire = DuelPackets.ParseWinner(body);
        AddChatMessage(DuelFrameUiLaw.WinnerLine(wire.Fled, wire.Winner, wire.Loser));
    }

    private void ApplyDuelCountdown(byte[] body)
    {
        uint seconds = DuelPackets.ParseCountdownSeconds(body);
        _duelCountdownRemaining = seconds;
        if (_duelCountdownRemaining == 0) return;
        AddChatMessage(DuelFrameUiLaw.CountdownLine(_duelCountdownRemaining));
        _duelCountdownRemaining--;
        _duelCountdownNextAt = NowSeconds() + 1;
    }

    private void ApplyDuelBounds(byte[] body, bool outside)
    {
        DuelPackets.ParseEmpty(body, outside
            ? Op.SMSG_DUEL_OUTOFBOUNDS : Op.SMSG_DUEL_INBOUNDS);
        if (outside)
        {
            bool dead = _entities.TryGet(ControlledGuid, out WorldEntity player) && player.IsDead;
            ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Show(
                _staticPopupSlots, DuelFrameUiLaw.OutOfBoundsDefinition, dead));
        }
        else
        {
            ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.HideByType(
                _staticPopupSlots, DuelFrameUiLaw.OutOfBoundsPopupType));
        }
    }

    private void UpdateDuel()
    {
        TryShowPendingDuelRequest();
        double now = NowSeconds();
        while (_duelCountdownRemaining > 0 && now >= _duelCountdownNextAt)
        {
            AddChatMessage(DuelFrameUiLaw.CountdownLine(_duelCountdownRemaining));
            _duelCountdownRemaining--;
            _duelCountdownNextAt += 1;
        }
    }

    private void TryShowPendingDuelRequest()
    {
        if (_duelPendingChallenger == 0 ||
            !_playerNames.TryGetValue(_duelPendingChallenger, out string? challenger)) return;
        bool dead = _entities.TryGet(ControlledGuid, out WorldEntity player) && player.IsDead;
        ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Show(
            _staticPopupSlots, DuelFrameUiLaw.RequestedDefinition, dead,
            dataToken: challenger));
        _duelPendingChallenger = 0;
    }

    private void StartDuelWith(ulong guid)
    {
        if (guid == 0 || guid == ControlledGuid || _spellCatalog is null) return;
        uint spellId = _actions.KnownSpells.FirstOrDefault(id =>
            _spellCatalog.TryGet(id, out SpellInfo spell) && DuelFrameUiLaw.IsDuelSpell(spell));
        if (spellId == 0) return;
        if (_selectionGuid != guid) CommitSelection(guid, beginAttack: false);
        TryCast(spellId);
    }

    private void ResetDuel()
    {
        _duelArbiter = 0;
        _duelPendingChallenger = 0;
        _duelCountdownRemaining = 0;
        _duelCountdownNextAt = 0;
        HideDuelPopups();
    }

    private void HideDuelPopups()
    {
        ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.HideByType(
            _staticPopupSlots, DuelFrameUiLaw.RequestedPopupType));
        ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.HideByType(
            _staticPopupSlots, DuelFrameUiLaw.OutOfBoundsPopupType));
    }

    private void DrawDuelPopups()
    {
        DrawDuelPopup(DuelFrameUiLaw.RequestedPopupType, buttons: true);
        DrawDuelPopup(DuelFrameUiLaw.OutOfBoundsPopupType, buttons: false);
    }

    private void DrawDuelPopup(string type, bool buttons)
    {
        (int Slot, StaticPopupCoordinatorLaw.Instance Instance)? popup =
            DuelFrameUiLaw.Visible(_staticPopupSlots, type);
        if (popup is not { } visible || _skin is null) return;
        float scale = GameplayUiScale();
        string text = buttons
            ? DuelFrameUiLaw.RequestedText(visible.Instance.DataToken ?? "")
            : DuelFrameUiLaw.OutOfBoundsText(visible.Instance.TimeLeft);
        string[] lines = WrapTooltipText(text, "GameFontHighlight", scale,
            DuelFrameUiLaw.PopupTextWidth * scale).ToArray();
        float logicalTextHeight = lines.Length * GameText.LinePitch("GameFontHighlight", 1);
        Vector2 origin = StaticPopupOrigin(visible.Slot, DuelFrameUiLaw.PopupWidth, scale);
        Vector2 size = DuelFrameUiLaw.PopupSize(logicalTextHeight, buttons) * scale;
        ImGui.SetNextWindowPos(origin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav;
        bool begun = ImGui.Begin($"##duel-popup-{visible.Slot}-{type}", flags);
        ImGui.PopStyleVar(2);
        if (!begun) { ImGui.End(); return; }
        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        draw.PushClipRectFullScreen();
        _skin.DrawBackdrop(draw, origin, origin + size, WowSkin.Dialog);
        for (int i = 0; i < lines.Length; i++)
            GameText.DrawCentered(draw, "GameFontHighlight", lines[i],
                origin + DuelFrameUiLaw.TextLineCenter(i) * scale, scale);
        bool accept = false, decline = false;
        if (buttons)
        {
            accept = DrawPartyInviteButton(draw, $"StaticPopup{visible.Slot}Button1",
                DuelFrameUiLaw.AcceptText,
                origin + DuelFrameUiLaw.ButtonMin(1, logicalTextHeight) * scale,
                scale, capture: false, clip: Vector4.Zero);
            decline = DrawPartyInviteButton(draw, $"StaticPopup{visible.Slot}Button2",
                DuelFrameUiLaw.DeclineText,
                origin + DuelFrameUiLaw.ButtonMin(2, logicalTextHeight) * scale,
                scale, capture: false, clip: Vector4.Zero);
        }
        draw.PopClipRect();
        ImGui.End();
        if (accept)
            ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Click(
                _staticPopupSlots, visible.Slot, buttonIndex: 1));
        else if (decline)
            ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Click(
                _staticPopupSlots, visible.Slot, buttonIndex: 2));
    }
}
