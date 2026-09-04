using System.Numerics;
using System.Text;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private ulong _petPopupGuid;
    private readonly byte[] _petRenameInput = new byte[PetMenuUiLaw.RenameMaxLetters + 1];
    private string _petRenameCandidate = "";
    private bool _petRenameFocusRequested;
    private bool _petRenameEditFocused;

    private bool PlayerDeadForStaticPopup() =>
        _entities.TryGet(LocalPlayerGuid, out WorldEntity player) && player.IsDead;

    private void ShowPetAbandonPopup(ulong guid)
    {
        if (!CanAuthorControlledGameplay) return;
        _petPopupGuid = guid;
        ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Show(
            _staticPopupSlots, PetMenuUiLaw.AbandonDefinition,
            PlayerDeadForStaticPopup()));
    }

    private void ShowPetRenamePopup(ulong guid)
    {
        if (!CanAuthorControlledGameplay) return;
        _petPopupGuid = guid;
        ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Show(
            _staticPopupSlots, PetMenuUiLaw.RenameDefinition,
            PlayerDeadForStaticPopup()));
    }

    private string PetRenameInput()
    {
        int end = Array.IndexOf(_petRenameInput, (byte)0);
        return Encoding.UTF8.GetString(_petRenameInput, 0,
            end < 0 ? _petRenameInput.Length : end);
    }

    // RENAME_PET's callback opens PETRENAMECONFIRM synchronously while its own slot is still
    // occupied. The coordinator therefore allocates the other authored slot, after which the
    // ordinary edit popup hides.
    private void StartPetRenameConfirmation()
    {
        if (!CanAuthorControlledGameplay) return;
        string name = PetRenameInput();
        _petRenameCandidate = name;
        ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Show(
            _staticPopupSlots, PetMenuUiLaw.RenameConfirmDefinition,
            PlayerDeadForStaticPopup(), dataToken: name));
        ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.HideByType(
            _staticPopupSlots, PetMenuUiLaw.RenamePopupType));
    }

    private bool AnyPetMenuPopupVisible() =>
        (_staticPopupSlots.First is { } first &&
            PetMenuUiLaw.IsPetPopup(first.Definition.Type)) ||
        (_staticPopupSlots.Second is { } second &&
            PetMenuUiLaw.IsPetPopup(second.Definition.Type));

    private void ClearPetMenuPopupState()
    {
        _petPopupGuid = 0;
        _petRenameCandidate = "";
        _petRenameFocusRequested = false;
        _petRenameEditFocused = false;
        Array.Clear(_petRenameInput);
    }

    private void ApplyPetMenuPopupEffect(StaticPopupCoordinatorLaw.Effect effect)
    {
        if (!PetMenuUiLaw.IsPetPopup(effect.Type)) return;
        if (effect.Type == PetMenuUiLaw.RenamePopupType)
        {
            switch (effect.Kind)
            {
                case StaticPopupCoordinatorLaw.EffectKind.ClearEditBox:
                    Array.Clear(_petRenameInput);
                    break;
                case StaticPopupCoordinatorLaw.EffectKind.OnShow:
                    _petRenameFocusRequested = true;
                    break;
                case StaticPopupCoordinatorLaw.EffectKind.Hide:
                case StaticPopupCoordinatorLaw.EffectKind.ClearEditBoxFocus:
                    _petRenameFocusRequested = false;
                    _petRenameEditFocused = false;
                    break;
                case StaticPopupCoordinatorLaw.EffectKind.Accept:
                case StaticPopupCoordinatorLaw.EffectKind.EditBoxEnter:
                    StartPetRenameConfirmation();
                    break;
            }
            return;
        }
        if (effect.Kind != StaticPopupCoordinatorLaw.EffectKind.Accept) return;
        // A popup may have been opened while embodied and accepted after Ctrl+F detached.
        // Revalidate at the irreversible packet tail, not only when the row was clicked.
        if (!CanAuthorControlledGameplay) return;
        if (RefuseTacticalFrozenActor(_petPopupGuid, "change its pet state")) return;
        if (effect.Type == PetMenuUiLaw.AbandonPopupType)
            _net?.PetAbandon(_petPopupGuid);
        else if (effect.Type == PetMenuUiLaw.RenameConfirmPopupType)
        {
            if (_petRenameCandidate.Length == 0)
                ShowUiError(PetGlobalString("ERR_NULL_PETNAME"));
            else
                _net?.PetRename(_petPopupGuid, _petRenameCandidate);
        }
    }

    private void DrawPetMenuPopups()
    {
        DrawPetPlainPopup(PetMenuUiLaw.AbandonPopupType, PetMenuUiLaw.AbandonText,
            PetMenuUiLaw.OkayText, PetMenuUiLaw.CancelText);
        DrawPetRenamePopup();
        (int Slot, StaticPopupCoordinatorLaw.Instance Instance)? confirm =
            PetMenuUiLaw.Visible(_staticPopupSlots, PetMenuUiLaw.RenameConfirmPopupType);
        if (confirm is { } visible)
            DrawPetPlainPopup(PetMenuUiLaw.RenameConfirmPopupType,
                PetMenuUiLaw.RenameConfirmation(visible.Instance.DataToken ?? ""),
                PetMenuUiLaw.YesText, PetMenuUiLaw.NoText);
    }

    private void DrawPetPlainPopup(string type, string text, string buttonOne,
        string buttonTwo)
    {
        (int Slot, StaticPopupCoordinatorLaw.Instance Instance)? popup =
            PetMenuUiLaw.Visible(_staticPopupSlots, type);
        if (popup is not { } visible || _skin is null) return;
        float s = GameplayUiScale();
        string[] lines = WrapTooltipText(text, "GameFontHighlight", s,
            StaticPopupCoordinatorLaw.TextWidth * s).ToArray();
        float linePitch = GameText.LinePitch("GameFontHighlight", 1);
        PetMenuUiLaw.PlainPopupLayout layout =
            PetMenuUiLaw.PlainLayout(lines.Length * linePitch);
        Vector2 origin = StaticPopupOrigin(visible.Slot, layout.Width, s);
        Vector2 size = layout.Size * s;

        ImGui.SetNextWindowPos(origin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoNav;
        bool begun = ImGui.Begin($"##pet-popup-{visible.Slot}-{type}", flags);
        ImGui.PopStyleVar(2);
        if (!begun) { ImGui.End(); return; }

        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        dl.PushClipRectFullScreen();
        _skin.DrawBackdrop(dl, origin, origin + size, WowSkin.Dialog);
        for (int i = 0; i < lines.Length; i++)
            GameText.DrawCentered(dl, "GameFontHighlight", lines[i],
                origin + PetMenuUiLaw.TextLineCenter(layout, linePitch, i) * s, s);
        bool accepted = DrawPartyInviteButton(dl,
            $"StaticPopup{visible.Slot}Button1", buttonOne,
            origin + layout.Button1.Min * s,
            s, capture: false, default);
        bool cancelled = DrawPartyInviteButton(dl,
            $"StaticPopup{visible.Slot}Button2", buttonTwo,
            origin + layout.Button2.Min * s,
            s, capture: false, default);
        dl.PopClipRect();
        ImGui.End();

        if (accepted)
            ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Click(
                _staticPopupSlots, visible.Slot, buttonIndex: 1));
        else if (cancelled)
            ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Click(
                _staticPopupSlots, visible.Slot, buttonIndex: 2));
    }

    private void DrawPetRenamePopup()
    {
        (int Slot, StaticPopupCoordinatorLaw.Instance Instance)? popup =
            PetMenuUiLaw.Visible(_staticPopupSlots, PetMenuUiLaw.RenamePopupType);
        if (popup is not { } visible || _skin is null) return;
        float s = GameplayUiScale();
        float textHeight = GameText.LinePitch("GameFontHighlight", 1);
        StaticPopupCoordinatorLaw.NarrowEditBoxLayout layout =
            StaticPopupCoordinatorLaw.NarrowEditLayout(textHeight);
        Vector2 origin = StaticPopupOrigin(visible.Slot, layout.Width, s);
        Vector2 size = layout.Size * s;

        ImGui.SetNextWindowPos(origin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoNav;
        bool begun = ImGui.Begin($"##pet-rename-popup-{visible.Slot}", flags);
        ImGui.PopStyleVar(2);
        if (!begun) { ImGui.End(); return; }

        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        dl.PushClipRectFullScreen();
        _skin.DrawBackdrop(dl, origin, origin + size, WowSkin.Dialog);
        GameText.DrawCentered(dl, "GameFontHighlight", PetMenuUiLaw.RenameLabel,
            origin + layout.Text.Center * s, s);

        Vector2 editMin = origin + layout.EditBox.Min * s;
        DrawStaticPopupEditBoxBorder(dl, editMin, s);
        ImGui.SetCursorScreenPos(editMin + StaticPopupCoordinatorLaw.EditTextOffset * s);
        ImGui.SetNextItemWidth(layout.EditBox.Width * s);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.Border, Vector4.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, Vector2.Zero);
        if (_petRenameFocusRequested)
        {
            ImGui.SetKeyboardFocusHere();
            _petRenameFocusRequested = false;
        }
        bool entered = ImGui.InputText("##pet-rename-edit", _petRenameInput,
            (uint)_petRenameInput.Length, ImGuiInputTextFlags.EnterReturnsTrue);
        _petRenameEditFocused = ImGui.IsItemActive();
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(4);

        bool accepted = DrawPartyInviteButton(dl,
            $"StaticPopup{visible.Slot}Button1", PetMenuUiLaw.AcceptText,
            origin + layout.Button1.Min * s,
            s, capture: false, default);
        bool cancelled = DrawPartyInviteButton(dl,
            $"StaticPopup{visible.Slot}Button2", PetMenuUiLaw.CancelText,
            origin + layout.Button2.Min * s,
            s, capture: false, default);
        dl.PopClipRect();
        ImGui.End();

        if (entered)
            ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.EditBoxEnter(
                _staticPopupSlots, visible.Slot));
        else if (accepted)
            ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Click(
                _staticPopupSlots, visible.Slot, buttonIndex: 1,
                typeStillSame: false));
        else if (cancelled)
            ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Click(
                _staticPopupSlots, visible.Slot, buttonIndex: 2));
    }
}
