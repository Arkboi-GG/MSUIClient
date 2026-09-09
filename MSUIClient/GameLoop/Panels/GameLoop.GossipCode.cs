using System.Numerics;
using System.Text;
using ImGuiNET;
using MSUIClient.Engine;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private GossipCodeUiLaw.Request? _gossipCode;
    // Keep the UTF-8 input plus the packet GUID/list ID safely below the 16-bit wire ceiling.
    private readonly byte[] _gossipCodeInput = new byte[65000];
    private bool _gossipCodeFocusRequested, _gossipCodeEditFocused;

    private void HideGossipCode()
    {
        ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.HideByType(_staticPopupSlots, GossipCodeUiLaw.PopupType));
        _gossipCode = null;
    }

    private bool ShowGossipCode(GossipOption option)
    {
        if (_gossipMenu is null || !option.Coded ||
            RefuseTacticalFreezeLiveCommand("answering coded gossip") ||
            RefuseTacticalFrozenActor(_gossipMenu.SourceGuid, "answer its coded gossip")) return false;
        HideGossipCode();
        _gossipCode = new(ControlledGuid, _gossipMenu, option.ListId);
        bool dead = _entities.TryGet(ControlledGuid, out WorldEntity actor) && actor.IsDead;
        ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Show(_staticPopupSlots, GossipCodeUiLaw.Definition, dead));
        if (ConfirmPopupUiLaw.Visible(_staticPopupSlots, GossipCodeUiLaw.PopupType) is null)
        { _gossipCode = null; return false; }
        EmitInterface("gossip", "code-open", "DISPLAYED", _gossipMenu.SourceGuid,
            $"actor=0x{ControlledGuid:X16};listId={option.ListId}");
        return true;
    }

    private bool SubmitGossipCode()
    {
        if (_gossipCode is not { } pending ||
            !GossipCodeUiLaw.StillCurrent(pending, ControlledGuid, _gossipMenu) || UpdateGossipLifecycle()) return false;
        int length = Array.IndexOf(_gossipCodeInput, (byte)0);
        if (length < 0) length = _gossipCodeInput.Length;
        string code = Encoding.UTF8.GetString(_gossipCodeInput, 0, length);
        if (code.Length == 0 || RefuseTacticalFreezeLiveCommand("answering coded gossip") ||
            RefuseTacticalFrozenActor(pending.Menu.SourceGuid, "answer its coded gossip")) return false;
        bool sent = _net?.GossipSelect(pending.Menu.SourceGuid, pending.ListId, code) == true;
        if (sent) { _gossipCode = null; RememberPetUnlearnSelection(pending.Menu.SourceGuid); }
        // The code itself is never included in diagnostic or chat output.
        EmitInterface("gossip", "code-submit", sent ? "SENT" : "SEND_FAILED", pending.Menu.SourceGuid,
            $"actor=0x{pending.Actor:X16};listId={pending.ListId};bytes={length}");
        return sent;
    }

    private void ApplyGossipCodePopupEffect(StaticPopupCoordinatorLaw.Effect effect)
    {
        switch (effect.Kind)
        {
            case StaticPopupCoordinatorLaw.EffectKind.ClearEditBox:
                Array.Clear(_gossipCodeInput); break;
            case StaticPopupCoordinatorLaw.EffectKind.OnShow:
                _gossipCodeFocusRequested = true; break;
            case StaticPopupCoordinatorLaw.EffectKind.Accept:
            case StaticPopupCoordinatorLaw.EffectKind.EditBoxEnter:
                SubmitGossipCode(); break;
            case StaticPopupCoordinatorLaw.EffectKind.OnHide:
                Array.Clear(_gossipCodeInput);
                _gossipCodeFocusRequested = _gossipCodeEditFocused = false;
                _gossipCode = null;
                if (_chatEditOpen) _chatEditJustOpened = true;
                break;
        }
    }

    private void DrawGossipCodePopup()
    {
        if (_gossipCode is { } pending &&
            (!GossipCodeUiLaw.StillCurrent(pending, ControlledGuid, _gossipMenu) || UpdateGossipLifecycle())) HideGossipCode();
        var popup = ConfirmPopupUiLaw.Visible(_staticPopupSlots, GossipCodeUiLaw.PopupType);
        if (popup is not { } visible || _skin is null) return;
        float s = GameplayUiScale();
        var layout = StaticPopupCoordinatorLaw.NarrowEditLayout(GameText.LinePitch("GameFontHighlight", 1));
        Vector2 origin = StaticPopupOrigin(visible.Slot, layout.Width, s), size = layout.Size * s;
        ImGui.SetNextWindowPos(origin, ImGuiCond.Always); ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero); ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        bool begun = ImGui.Begin($"##gossip-code-{visible.Slot}", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav);
        ImGui.PopStyleVar(2);
        if (!begun) { ImGui.End(); return; }
        ImDrawListPtr draw = ImGui.GetWindowDrawList(); draw.PushClipRectFullScreen();
        _skin.DrawBackdrop(draw, origin, origin + size, WowSkin.Dialog);
        GameText.DrawCentered(draw, "GameFontHighlight", GossipCodeUiLaw.Prompt, origin + layout.Text.Center * s, s);
        Vector2 editMin = origin + layout.EditBox.Min * s;
        DrawStaticPopupEditBoxBorder(draw, editMin, s);
        if (_gossipCodeFocusRequested) { ImGui.SetKeyboardFocusHere(); _gossipCodeFocusRequested = false; }
        bool entered = VanillaBareInputText("##gossip-code-edit", _gossipCodeInput,
            editMin + StaticPopupCoordinatorLaw.EditTextOffset * s,
            new Vector2(layout.EditBox.Width, GameText.LinePitch("GameFontHighlight", 1)), Vector2.Zero, s,
            ImGuiInputTextFlags.EnterReturnsTrue);
        _gossipCodeEditFocused = ImGui.IsItemActive();
        bool accepted = DrawPartyInviteButton(draw, "##gossip-code-accept", "Accept", origin + layout.Button1.Min * s, s, false, default);
        bool cancelled = DrawPartyInviteButton(draw, "##gossip-code-cancel", "Cancel", origin + layout.Button2.Min * s, s, false, default);
        draw.PopClipRect(); ImGui.End();
        if (entered)
        {
            ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.EditBoxEnter(_staticPopupSlots, visible.Slot));
            HideGossipCode();
        }
        else if (accepted || cancelled)
            ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Click(_staticPopupSlots, visible.Slot, accepted ? 1 : 2));
    }
}
