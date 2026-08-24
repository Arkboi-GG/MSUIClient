using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private sealed record DeleteItemConfirmation(
        int Container, int Slot, string Name, byte Count);

    private DeleteItemConfirmation? _deleteItemConfirmation;

    private void TryOpenDeleteItemConfirmation()
    {
        if (!CanAuthorSessionInventory || _deleteItemConfirmation is not null ||
            !HasCarriedItem ||
            !ImGui.IsMouseReleased(ImGuiMouseButton.Left) || ImGui.IsAnyItemHovered() ||
            ResolveCarriedItem() is not { } instance ||
            _items?.TryGet(instance.Entry, out ItemTemplate? item) != true || item is null)
            return;

        byte count = (byte)Math.Clamp(_carriedCount ?? 0, 0, byte.MaxValue);
        _deleteItemConfirmation = new(_carriedContainer, _carriedSlot, item.Name, count);
        bool dead = _entities.TryGet(ControlledGuid, out WorldEntity player) && player.IsDead;
        ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Show(
            _staticPopupSlots, DeleteItemUiLaw.Definition, dead, dataToken: item.Name));
    }

    private void AcceptDeleteItem()
    {
        // Recheck at the destructive send edge: the popup may have been opened immediately
        // before Ctrl+F detached the observer rig from the gameplay body.
        if (!CanAuthorSessionInventory ||
            _deleteItemConfirmation is not { } pending || _net is null ||
            InventoryUiLaw.ToWire(pending.Container, pending.Slot) is not { } wire)
        {
            CancelDeleteItem();
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
    }

    private float StaticPopupFirstHeight(float scale)
    {
        if (_staticPopupSlots.First is not { } first) return 0;
        string text = first.Definition.Type switch
        {
            PartyInvitePopupType => $"{first.DataToken ?? ""} invites you to a group.",
            DeleteItemUiLaw.PopupType => DeleteItemUiLaw.Text(first.DataToken ?? ""),
            DuelFrameUiLaw.RequestedPopupType =>
                DuelFrameUiLaw.RequestedText(first.DataToken ?? ""),
            DuelFrameUiLaw.OutOfBoundsPopupType =>
                DuelFrameUiLaw.OutOfBoundsText(first.TimeLeft),
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
        string text = DeleteItemUiLaw.Text(visible.Instance.DataToken ?? "");
        string[] lines = WrapTooltipText(text, "GameFontHighlight", scale,
            DeleteItemUiLaw.TextWidth * scale).ToArray();
        float logicalTextHeight = lines.Length * GameText.LinePitch("GameFontHighlight", 1);
        DeleteItemUiLaw.PopupLayout layout = DeleteItemUiLaw.Layout(logicalTextHeight);
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

        bool yes = DrawPartyInviteButton(draw, "StaticPopup1Button1", "Yes",
            origin + layout.Button1.Min * scale,
            scale, capture: false, default);
        bool no = DrawPartyInviteButton(draw, "StaticPopup1Button2", "No",
            origin + layout.Button2.Min * scale,
            scale, capture: false, default);
        draw.PopClipRect();
        ImGui.End();

        if (yes)
            ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Click(
                _staticPopupSlots, visible.Slot, buttonIndex: 1));
        else if (no)
            ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Click(
                _staticPopupSlots, visible.Slot, buttonIndex: 2));
    }
}
