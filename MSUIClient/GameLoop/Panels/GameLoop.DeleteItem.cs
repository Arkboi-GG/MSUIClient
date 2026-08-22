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
        if (_deleteItemConfirmation is not null || !HasCarriedItem ||
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
        if (_deleteItemConfirmation is not { } pending || _net is null ||
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
        Vector2 display = ImGui.GetIO().DisplaySize;
        float top = DeleteItemUiLaw.ScreenTop;
        if (slot == 2)
            top += StaticPopupFirstHeight(scale) + DeleteItemUiLaw.SlotGap;
        return new((display.X - logicalWidth * scale) * .5f, top * scale);
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
        float logicalHeight = DeleteItemUiLaw.Height(logicalTextHeight);
        Vector2 origin = StaticPopupOrigin(visible.Slot, DeleteItemUiLaw.Width, scale);
        Vector2 size = new(DeleteItemUiLaw.Width * scale, logicalHeight * scale);

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
        _skin.GlueImage(draw, "dialog.alert",
            origin + new Vector2(DeleteItemUiLaw.AlertLeft,
                (logicalHeight - DeleteItemUiLaw.AlertSize) * .5f) * scale,
            origin + new Vector2(DeleteItemUiLaw.AlertLeft + DeleteItemUiLaw.AlertSize,
                (logicalHeight + DeleteItemUiLaw.AlertSize) * .5f) * scale);
        for (int i = 0; i < lines.Length; i++)
            GameText.DrawCentered(draw, "GameFontHighlight", lines[i],
                origin + new Vector2(DeleteItemUiLaw.Width * .5f,
                    DeleteItemUiLaw.TextTop +
                    (i + .5f) * GameText.LinePitch("GameFontHighlight", 1)) * scale, scale);

        float buttonTop = DeleteItemUiLaw.ButtonTop(logicalTextHeight);
        bool yes = DrawPartyInviteButton(draw, "StaticPopup1Button1", "Yes",
            origin + new Vector2(DeleteItemUiLaw.ButtonOneX(DeleteItemUiLaw.Width), buttonTop) * scale,
            scale, capture: false, default);
        bool no = DrawPartyInviteButton(draw, "StaticPopup1Button2", "No",
            origin + new Vector2(DeleteItemUiLaw.ButtonTwoX(DeleteItemUiLaw.Width), buttonTop) * scale,
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
