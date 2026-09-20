using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private sealed record PendingEquipBinding(ulong Actor, bool AutoEquip,
        int SourceContainer, int SourceSlot, ulong SourceGuid,
        int DestinationContainer = 0, int DestinationSlot = 0, ulong DestinationGuid = 0);
    private PendingEquipBinding? _pendingEquipBinding;

    private bool EquipWouldBind(WorldEntity? instance)
    {
        if (instance is null || _items?.TryGet(instance.Entry, out ItemTemplate? item) != true ||
            item is null || !_entities.TryGet(ControlledGuid, out WorldEntity actor)) return false;
        bool bound = (instance.Fields.ItemFlags & 1) != 0;
        for (int i = 0; !bound && i < 7; i++)
            bound = _enchantCatalog?.BindsItem(instance.Fields.ItemEnchantmentId(i)) == true;
        (byte race, byte cls, _, _) = actor.Fields.Bytes0;
        bool usable = item.RequiredLevel <= actor.Level &&
            (item.AllowableClass == -1 || cls is > 0 and <= 32 &&
                (unchecked((uint)item.AllowableClass) & (1u << (cls - 1))) != 0) &&
            (item.AllowableRace == -1 || race is > 0 and <= 32 &&
                (unchecked((uint)item.AllowableRace) & (1u << (race - 1))) != 0) &&
            (item.RequiredSpell == 0 || ActionsFor(ControlledGuid).KnownSpells.Contains(item.RequiredSpell)) &&
            (item.RequiredSkill == 0 || GetSkillValue(item.RequiredSkill, out ushort skill, out _) &&
                skill >= item.RequiredSkillRank) &&
            InventoryUiLaw.IsItemProficient(item.Class, item.Subclass, ItemProficienciesFor(ControlledGuid));
        return EquipBindingUiLaw.NeedsConfirmation(item.Bonding, bound, usable);
    }

    private void ShowEquipBinding(PendingEquipBinding pending)
    {
        ClearEquipBinding();
        bool dead = _entities.TryGet(ControlledGuid, out WorldEntity actor) && actor.IsDead;
        var plan = StaticPopupCoordinatorLaw.Show(_staticPopupSlots,
            EquipBindingUiLaw.Definition(pending.AutoEquip), dead);
        ExecuteStaticPopupPlan(plan);
        if (plan.Outcome == StaticPopupCoordinatorLaw.Outcome.Shown)
        {
            _pendingEquipBinding = pending;
            ClearCarriedItem();
            EmitInterface("inventory", "equip-bind", "CONFIRM", pending.SourceGuid,
                $"actor=0x{pending.Actor:X};auto={pending.AutoEquip}");
        }
    }

    private void ClearEquipBinding()
    {
        _pendingEquipBinding = null;
        foreach (string type in new[] { EquipBindingUiLaw.EquipType, EquipBindingUiLaw.AutoEquipType })
            ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.HideByType(_staticPopupSlots, type));
    }

    private bool EquipBindingStillValid(PendingEquipBinding pending) =>
        _net is not null && CanAuthorControlledOrSelf && ControlledGuid == pending.Actor &&
        ResolveInventoryItem(pending.SourceContainer, pending.SourceSlot)?.Guid == pending.SourceGuid &&
        (pending.AutoEquip || (ResolveInventoryItem(pending.DestinationContainer,
            pending.DestinationSlot)?.Guid ?? 0) == pending.DestinationGuid);

    private void AcceptEquipBinding()
    {
        var pending = _pendingEquipBinding;
        _pendingEquipBinding = null;
        if (pending is null || !EquipBindingStillValid(pending)) return;
        if (pending.AutoEquip)
        {
            if (InventoryUiLaw.ToWire(pending.SourceContainer, pending.SourceSlot) is { } wire)
                TryAutoEquipItem(wire.Bag, wire.Slot, bindConfirmed: true);
        }
        else
        {
            _carriedContainer = pending.SourceContainer;
            _carriedSlot = pending.SourceSlot;
            _carriedCount = null;
            PickupOrPlaceItem(pending.DestinationContainer, pending.DestinationSlot,
                pending.DestinationGuid, ignoreModifiers: true, bindConfirmed: true);
        }
    }

    private bool TryAutoEquipItem(byte bag, byte slot, bool bindConfirmed = false)
    {
        if (!CanAuthorControlledOrSelf || _net is null ||
            EquipBindingUiLaw.FromWire(bag, slot) is not { } position ||
            ResolveInventoryItem(position.Container, position.Slot) is not { } item) return false;
        if (!bindConfirmed && EquipWouldBind(item))
        {
            ShowEquipBinding(new(ControlledGuid, true, position.Container, position.Slot, item.Guid));
            return false;
        }
        bool sent = _net.AutoEquipItem(bag, slot);
        if (sent)
        {
            AddPendingBagLock(position.Container, position.Slot, ++_pendingBagOperation);
            EmitInterface("inventory", "auto-equip", "SENT", item.Guid,
                $"bag={bag};slot={slot};confirmed={bindConfirmed}");
        }
        return sent;
    }

    private void DrawEquipBinding()
    {
        if (_pendingEquipBinding is not { } pending || _skin is null) return;
        if (!EquipBindingStillValid(pending)) { ClearEquipBinding(); return; }
        if (EquipBindingUiLaw.Visible(_staticPopupSlots) is not { } visible) return;
        float scale = GameplayUiScale();
        string[] lines = WrapTooltipText(EquipBindingUiLaw.Text, "GameFontHighlight", scale,
            EquipBindingUiLaw.TextWidth * scale).ToArray();
        float pitch = GameText.LinePitch("GameFontHighlight", 1);
        var layout = EquipBindingUiLaw.Layout(lines.Length * pitch);
        Vector2 origin = StaticPopupOrigin(visible.Slot, EquipBindingUiLaw.Width, scale);
        ImGui.SetNextWindowPos(origin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(layout.Size * scale, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        bool begun = ImGui.Begin("##equip-binding", ImGuiWindowFlags.NoDecoration |
            ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav);
        ImGui.PopStyleVar(2);
        if (!begun) { ImGui.End(); return; }
        var draw = ImGui.GetWindowDrawList();
        draw.PushClipRectFullScreen();
        _skin.DrawBackdrop(draw, origin, origin + layout.Size * scale, WowSkin.Dialog);
        for (int i = 0; i < lines.Length; i++)
            GameText.DrawCentered(draw, "GameFontHighlight", lines[i], origin +
                (layout.TextCenter + new Vector2(0, (i + .5f) * pitch)) * scale, scale);
        bool accept = DrawPartyInviteButton(draw, "StaticPopup1Button1", "Okay",
            origin + layout.Accept.Min * scale, scale, capture: false, default);
        bool cancel = DrawPartyInviteButton(draw, "StaticPopup1Button2", "Cancel",
            origin + layout.Cancel.Min * scale, scale, capture: false, default);
        draw.PopClipRect();
        ImGui.End();
        if (accept || cancel)
            ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Click(_staticPopupSlots,
                visible.Slot, accept ? 1 : 2));
    }
}
