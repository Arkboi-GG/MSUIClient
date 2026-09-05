using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private sealed record DeleteItemConfirmation(
        int Container, int Slot, string Name, byte Count, uint Quality)
    {
        public bool NeedsTypedConfirmation =>
            DeleteItemUiLaw.RequiresTypedConfirmation(Quality);
    }

    private DeleteItemConfirmation? _deleteItemConfirmation;

    /// <summary>What has been typed into the guarded popup's field this time round.</summary>
    private string _deleteItemTypedConfirm = "";
    private bool _deleteItemConfirmFocusRequested;

    private void TryOpenDeleteItemConfirmation()
    {
        if (!CanAuthorControlledOrSelf || _deleteItemConfirmation is not null ||
            !HasCarriedItem ||
            !ImGui.IsMouseReleased(ImGuiMouseButton.Left) || ImGui.IsAnyItemHovered() ||
            ResolveCarriedItem() is not { } instance ||
            _items?.TryGet(instance.Entry, out ItemTemplate? item) != true || item is null)
            return;

        byte count = (byte)Math.Clamp(_carriedCount ?? 0, 0, byte.MaxValue);
        _deleteItemConfirmation =
            new(_carriedContainer, _carriedSlot, item.Name, count, item.Quality);
        _deleteItemTypedConfirm = "";
        bool dead = _entities.TryGet(ControlledGuid, out WorldEntity player) && player.IsDead;
        // Epic and above ask for the word; everything else keeps the plain 1.12 Yes/No.
        bool guarded = _deleteItemConfirmation.NeedsTypedConfirmation;
        _deleteItemConfirmFocusRequested = guarded;
        ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Show(
            _staticPopupSlots,
            guarded ? DeleteItemUiLaw.ConfirmDefinition : DeleteItemUiLaw.Definition,
            dead, dataToken: item.Name));
    }

    private void AcceptDeleteItem()
    {
        // Recheck at the destructive send edge: the popup may have been opened immediately
        // before Ctrl+F detached the observer rig from the gameplay body.
        if (!CanAuthorControlledOrSelf ||
            _deleteItemConfirmation is not { } pending || _net is null ||
            InventoryUiLaw.ToWire(pending.Container, pending.Slot) is not { } wire)
        {
            CancelDeleteItem();
            return;
        }

        // Belt and braces. The button is already withheld until the word matches, but the
        // accept effect can also arrive from the field's own Enter key, and this is the last
        // edge before an irreversible destroy.
        if (pending.NeedsTypedConfirmation &&
            !DeleteItemUiLaw.ConfirmSatisfied(_deleteItemTypedConfirm))
        {
            EmitInterface("inventory", "destroy", "REFUSED_UNCONFIRMED",
                ResolveCarriedItem()?.Guid ?? 0,
                $"quality={pending.Quality};name={SanitizeEvidence(pending.Name)}");
            return;
        }

        _net.DestroyItem(wire.Bag, wire.Slot, pending.Count);
        AddPendingBagLock(pending.Container, pending.Slot, ++_pendingBagOperation);
        EmitInterface("inventory", "destroy", "SENT", ResolveCarriedItem()?.Guid ?? 0,
            $"bag={wire.Bag};slot={wire.Slot};count={pending.Count};name={SanitizeEvidence(pending.Name)}");
        ClearCarriedItem();
        _deleteItemConfirmation = null;
    }

    private void CancelDeleteItem()
    {
        ClearCarriedItem();
        _deleteItemConfirmation = null;
        _deleteItemTypedConfirm = "";
        _deleteItemConfirmFocusRequested = false;
    }

    private float StaticPopupFirstHeight(float scale)
    {
        if (_staticPopupSlots.First is not { } first) return 0;
        string text = first.Definition.Type switch
        {
            PartyInvitePopupType => $"{first.DataToken ?? ""} invites you to a group.",
            DeleteItemUiLaw.PopupType => DeleteItemUiLaw.Text(first.DataToken ?? ""),
            DeleteItemUiLaw.ConfirmPopupType =>
                DeleteItemUiLaw.ConfirmText(first.DataToken ?? ""),
            DuelFrameUiLaw.RequestedPopupType =>
                DuelFrameUiLaw.RequestedText(first.DataToken ?? ""),
            DuelFrameUiLaw.OutOfBoundsPopupType =>
                DuelFrameUiLaw.OutOfBoundsText(first.TimeLeft),
            ConfirmPopupUiLaw.SummonPopupType => SummonPromptText(),
            ConfirmPopupUiLaw.QuestAcceptPopupType => QuestConfirmPromptText(),
            ConfirmPopupUiLaw.ReadyCheckPopupType => ReadyCheckPromptText(),
            ConfirmPopupUiLaw.PartyFlightPopupType => PartyFlightPromptText(),
            ConfirmPopupUiLaw.DeleteMacroPopupType =>
                ConfirmPopupUiLaw.DeleteMacroText(first.DataToken ?? ""),
            FriendsFrameUiLaw.AddFriendPopupType => FriendsFrameUiLaw.AddFriendPopupText,
            FriendsFrameUiLaw.AddIgnorePopupType => FriendsFrameUiLaw.AddIgnorePopupText,
            CharacterBindingsUiLaw.PopupType => CharacterBindingsUiLaw.ConfirmText,
            GuildFrameUiLaw.AddMemberPopupType => GuildFrameUiLaw.AddMemberLabel,
            GuildFrameUiLaw.RemoveMemberPopupType =>
                GuildFrameUiLaw.RemoveMemberText(first.DataToken ?? ""),
            GuildFrameUiLaw.SetPublicNotePopupType => "Set Player Note:",
            GuildFrameUiLaw.SetOfficerNotePopupType => "Set Officer Note:",
            GuildFrameUiLaw.AddRankPopupType => GuildFrameUiLaw.AddRankLabel,
            PetMenuUiLaw.AbandonPopupType => PetMenuUiLaw.AbandonText,
            PetMenuUiLaw.RenamePopupType => PetMenuUiLaw.RenameLabel,
            PetMenuUiLaw.RenameConfirmPopupType =>
                PetMenuUiLaw.RenameConfirmation(first.DataToken ?? ""),
            _ => "",
        };
        if (text.Length == 0) return StaticPopupCoordinatorLaw.BaseHeight;
        int lines = WrapTooltipText(text, "GameFontHighlight", scale,
            DeleteItemUiLaw.TextWidth * scale).Count();
        float textHeight = lines * GameText.LinePitch("GameFontHighlight", 1);
        float buttonHeight = first.Definition.Type == DuelFrameUiLaw.OutOfBoundsPopupType
            ? 0 : DeleteItemUiLaw.ButtonHeight;
        return StaticPopupCoordinatorLaw.Height(textHeight, buttonHeight,
            StaticPopupCoordinatorLaw.NarrowEditBoxHeight, first.Definition.HasEditBox);
    }

    private Vector2 StaticPopupOrigin(int slot, float logicalWidth, float scale)
    {
        float firstHeight = slot == 2 ? StaticPopupFirstHeight(scale) : 0;
        return StaticPopupCoordinatorLaw.ScreenOrigin(
            ImGui.GetIO().DisplaySize, logicalWidth, scale, slot, firstHeight);
    }

    private void DrawDeleteItemConfirmation()
    {
        (int Slot, StaticPopupCoordinatorLaw.Instance Instance)? popup =
            DeleteItemUiLaw.Visible(_staticPopupSlots);
        if (popup is not { } visible || _deleteItemConfirmation is null || _skin is null) return;

        float scale = GameplayUiScale();
        bool guarded = visible.Instance.Definition.HasEditBox;
        string text = guarded
            ? DeleteItemUiLaw.ConfirmText(visible.Instance.DataToken ?? "")
            : DeleteItemUiLaw.Text(visible.Instance.DataToken ?? "");
        string[] lines = WrapTooltipText(text, "GameFontHighlight", scale,
            DeleteItemUiLaw.TextWidth * scale).ToArray();
        float logicalTextHeight = lines.Length * GameText.LinePitch("GameFontHighlight", 1);
        DeleteItemUiLaw.PopupLayout layout =
            DeleteItemUiLaw.Layout(logicalTextHeight, guarded);
        Vector2 origin = StaticPopupOrigin(visible.Slot, DeleteItemUiLaw.Width, scale);
        Vector2 size = layout.Size * scale;

        ImGui.SetNextWindowPos(origin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav;
        bool begun = ImGui.Begin($"##delete-item-popup-{visible.Slot}", flags);
        ImGui.PopStyleVar(2);
        if (!begun) { ImGui.End(); return; }

        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        draw.PushClipRectFullScreen();
        _skin.DrawBackdrop(draw, origin, origin + size, WowSkin.Dialog);
        Vector2 alertMin = origin + layout.Alert.Min * scale;
        _skin.GlueImage(draw, "dialog.alert", alertMin,
            alertMin + layout.Alert.Size * scale);
        for (int i = 0; i < lines.Length; i++)
            GameText.DrawCentered(draw, "GameFontHighlight", lines[i],
                origin + DeleteItemUiLaw.TextLineCenter(layout,
                    GameText.LinePitch("GameFontHighlight", 1), i) * scale, scale);

        bool entered = false;
        if (guarded)
        {
            Vector2 editMin = origin + layout.EditBox.Min * scale;
            DrawStaticPopupEditBoxBorder(draw, editMin, scale);
            ImGui.SetCursorScreenPos(editMin + GuildFrameUiLaw.NarrowPopupEditTextOffset * scale);
            ImGui.SetNextItemWidth(layout.EditBox.Width * scale);
            ImGui.PushStyleColor(ImGuiCol.FrameBg, Vector4.Zero);
            ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Vector4.Zero);
            ImGui.PushStyleColor(ImGuiCol.FrameBgActive, Vector4.Zero);
            ImGui.PushStyleColor(ImGuiCol.Border, Vector4.Zero);
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, Vector2.Zero);
            if (_deleteItemConfirmFocusRequested)
            {
                ImGui.SetKeyboardFocusHere();
                _deleteItemConfirmFocusRequested = false;
            }
            entered = ImGui.InputText("##delete-item-confirm-edit",
                ref _deleteItemTypedConfirm, DeleteItemUiLaw.ConfirmMaxLetters,
                ImGuiInputTextFlags.EnterReturnsTrue);
            ImGui.PopStyleVar();
            ImGui.PopStyleColor(4);
        }

        // The accept button is WITHHELD, not merely ignored, until the word is there: a
        // clickable Yes on a legendary is the whole thing this popup exists to prevent.
        bool armed = !guarded || DeleteItemUiLaw.ConfirmSatisfied(_deleteItemTypedConfirm);
        bool yes = DrawPartyInviteButton(draw, "StaticPopup1Button1", "Yes",
            origin + layout.Button1.Min * scale,
            scale, capture: false, default, enabled: armed);
        bool no = DrawPartyInviteButton(draw, "StaticPopup1Button2", "No",
            origin + layout.Button2.Min * scale,
            scale, capture: false, default);
        draw.PopClipRect();
        ImGui.End();

        if (entered && armed)
            ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Click(
                _staticPopupSlots, visible.Slot, buttonIndex: 1));
        else if (yes)
            ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Click(
                _staticPopupSlots, visible.Slot, buttonIndex: 1));
        else if (no)
            ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Click(
                _staticPopupSlots, visible.Slot, buttonIndex: 2));
    }
}
