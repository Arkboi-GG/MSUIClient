using ImGuiNET;
using System.Numerics;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

namespace MSUIClient;

/// <summary>
/// Party Inventory — the BG3-style multi-character view, v3 (owner 2026-08-25):
/// one column per member with a MINIFIED CRPG character sheet (paper-doll rails
/// around the member's baked portrait), then its bags in small icons under it.
/// Columns are CENTERED and share the window width equally — resizing the
/// panel resizes the columns, with equal gaps and margins on both sides; a
/// hard minimum keeps slots readable (the host scrolls horizontally below it).
/// Equipment is public wire data (always current); bags come from the
/// member-facts snapshot (server push) or the possession-retention fallback.
/// Phase C v1: DRAG an item onto another member's column — or right-click →
/// "Give to …" — to move it instantly through CMSG_SUI_MEMBER_ITEM_MOVE. The
/// server validates the party line and re-snapshots both ends; the columns
/// update from those pushes, never from client optimism.
/// </summary>
public sealed partial class GameLoop
{
    private bool _partyInventoryOpen;
    private ulong _partyInventoryGuid;
    // Give-menu state: the bag item the context menu was opened for. The open
    // request is latched because the click lands inside a column CHILD window
    // while the popup lives in the columns host — OpenPopup must run there.
    private ulong _partyGiveFrom;
    private byte _partyGiveBag;
    private byte _partyGiveSlot;
    private string _partyGiveName = "";
    private bool _partyGiveOpenRequested;
    // Drag state: stamped when an ImGui drag-drop source begins on a bag item.
    private ulong _partyDragFrom;
    private byte _partyDragBag;
    private byte _partyDragSlot;
    private string _partyDragName = "";

    private static readonly string[] EquipSlotNames =
    [
        "Head", "Neck", "Shoulders", "Shirt", "Chest", "Waist", "Legs", "Feet",
        "Wrists", "Hands", "Ring 1", "Ring 2", "Trinket 1", "Trinket 2", "Back",
        "Main hand", "Off hand", "Ranged", "Tabard",
    ];

    // Vanilla paper-doll arrangement, minified: left rail, right rail, weapons.
    private static readonly int[] PartyDollLeftRail = [0, 1, 2, 14, 4, 3, 18, 8];
    private static readonly int[] PartyDollRightRail = [9, 5, 6, 7, 10, 11, 12, 13];
    private static readonly int[] PartyDollWeapons = [15, 16, 17];

    private const float PartyColumnMinWidth = 150f;  // logical; below it the host scrolls
    private const float PartyColumnMaxWidth = 280f;  // don't balloon on huge windows
    private const float PartyCell = 20f;             // small icons (owner: manage more later)
    private const float PartyCellGap = 2f;

    private void OpenPartyInventory(ulong guid)
    {
        _partyInventoryOpen = true;
        _partyInventoryGuid = guid;
        // Member-facts server: refresh every column's bags on open (rate-limited).
        RequestPartyMemberFacts("party inventory opened");
    }

    private void DrawPartyInventoryPanel()
    {
        if (!_partyInventoryOpen || _net is null || _items is null || _gameplayArt is null)
            return;
        float scale = GameplayUiScale();

        List<(ulong Guid, string Name)> owners = [(LocalPlayerGuid, _net.PlayerName ?? "You")];
        foreach (PartyMember member in _partyMembers)
            owners.Add((member.Guid, member.Name));

        ImGui.SetNextWindowSize(
            new Vector2(Math.Min(4, owners.Count) * 190f + 40f, 480f) * scale,
            ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(
            new Vector2(PartyColumnMinWidth + 60f, 300f) * scale,
            new Vector2(float.MaxValue, float.MaxValue));
        ImGui.SetNextWindowBgAlpha(0f);
        if (!ImGui.Begin("##party-inventory", ref _partyInventoryOpen,
                ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoBackground |
                ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            return;
        }
        DrawVanillaPanelChrome("Party Inventory", scale, ref _partyInventoryOpen);
        ImGui.Dummy(new Vector2(1, 10 * scale));

        ImGui.BeginChild("##pinv-columns", new Vector2(0, 0), false,
            ImGuiWindowFlags.HorizontalScrollbar);
        // Centered adaptive layout: equal column widths, equal gaps, equal
        // margins on BOTH sides, all derived from the live window width.
        float gap = 8f * scale;
        float avail = ImGui.GetContentRegionAvail().X;
        float column = Math.Clamp((avail - gap * (owners.Count + 1)) / owners.Count,
            PartyColumnMinWidth * scale, PartyColumnMaxWidth * scale);
        float leftPad = MathF.Max(gap,
            (avail - column * owners.Count - gap * (owners.Count - 1)) * 0.5f);
        bool first = true;
        foreach ((ulong guid, string name) in owners)
        {
            if (first)
            {
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + leftPad);
                first = false;
            }
            else ImGui.SameLine(0f, gap);
            ImGui.BeginChild($"##pinv-col-{guid}", new Vector2(column, 0), true);
            DrawPartyInventoryColumn(guid, name, scale);
            ImGui.EndChild();
            PartyItemDropTarget(guid);   // the whole column catches drops too
        }
        DrawPartyGiveMenu(owners);
        ImGui.EndChild();
        ImGui.End();
    }

    /// <summary>The shared "Give to …" context menu — opened by any bag slot's
    /// right-click, listing every OTHER member as a destination.</summary>
    private void DrawPartyGiveMenu(List<(ulong Guid, string Name)> owners)
    {
        if (_partyGiveOpenRequested)
        {
            _partyGiveOpenRequested = false;
            ImGui.OpenPopup("##pinv-give");
        }
        if (!ImGui.BeginPopup("##pinv-give")) return;
        ImGui.TextDisabled(_partyGiveName.Length != 0 ? _partyGiveName : "Item");
        ImGui.Separator();
        if (!_partyItemMoveAvailable)
            ImGui.TextDisabled("Moving items needs the party-item-move server.");
        else
            foreach ((ulong guid, string name) in owners)
            {
                if (guid == _partyGiveFrom) continue;
                if (ImGui.MenuItem($"Give to {name}##give-{guid}"))
                    RequestMemberItemMove(_partyGiveFrom, guid, _partyGiveBag, _partyGiveSlot);
            }
        ImGui.EndPopup();
    }

    /// <summary>Accept a dragged party item on the LAST ImGui item. A cross-owner
    /// drop hands it to <paramref name="ownerGuid"/> (auto-stored). A same-owner
    /// drop onto a bag cell rearranges it into THAT exact slot — but only when the
    /// target carries a destination (<paramref name="hasDest"/>): equip cells and
    /// column-wide drops omit it, so a same-owner drop there is a no-op (equipment
    /// still needs an explicit dequip). Server-validated either way.</summary>
    private unsafe void PartyItemDropTarget(ulong ownerGuid, byte destBag = 255,
        byte destSlot = 255, bool hasDest = false)
    {
        if (_partyDragFrom == 0 || !ImGui.BeginDragDropTarget()) return;
        ImGuiPayloadPtr payload = ImGui.AcceptDragDropPayload("SUI_PARTY_ITEM");
        if (payload.NativePtr != null && _partyDragFrom != 0)
        {
            if (_partyDragFrom != ownerGuid)
                RequestMemberItemMove(_partyDragFrom, ownerGuid, _partyDragBag, _partyDragSlot);
            else if (hasDest)
                RequestMemberItemRearrange(ownerGuid, _partyDragBag, _partyDragSlot,
                    destBag, destSlot);
            _partyDragFrom = 0;
        }
        ImGui.EndDragDropTarget();
    }

    /// <summary>The member's baked portrait handle — the party bake, or the own
    /// character's portrait booth for the self column. 0 = no bake yet.</summary>
    private uint PartyColumnPortraitHandle(ulong guid)
    {
        uint baked = PartyPortraitHandle(guid);
        if (baked != 0) return baked;
        // [SUI] P4b: _playerPortrait bakes the DRIVEN unit, not the logged-in char —
        // so while possessing a bot it holds the BOT's face. Hand it back only for the
        // controlled unit's OWN column (matching ConsolePortraitHandle); the logged-in
        // character, now an abandoned-own-char party slot, takes its party bake. Keyed
        // on LocalPlayerGuid this painted the possessed bot's face onto your main in
        // every party column and the quest rail.
        if (guid == ControlledGuid && PlayerPortraitCurrent && _playerPortrait is not null)
            return _playerPortrait.CircularTextureHandle;
        return 0;
    }

    private void DrawPartyInventoryColumn(ulong guid, string name, float scale)
    {
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        GameText.Draw(dl, "GameFontNormal", name, ImGui.GetCursorScreenPos(), scale);
        ImGui.Dummy(new Vector2(1, 13 * scale));

        // Sync status line: the member-facts server pushes bags by itself;
        // possession-retention is the fallback wire.
        bool live = guid == LocalPlayerGuid || guid == ControlledGuid;
        double? age = SuiSnapshotAgeSeconds(guid);
        ImGui.TextDisabled(live ? "live"
            : age is double seconds
                ? $"synced {(seconds < 90 ? $"{(int)seconds}s" : $"{(int)(seconds / 60)}m")} ago"
                : "not synced");

        if (!_entities.TryGet(guid, out WorldEntity owner))
        {
            ImGui.TextWrapped("Not streamed in — nothing readable from here.");
            return;
        }

        // ── Minified character sheet: rails at the column edges, portrait ────
        // centered — every measure derives from the LIVE column width.
        Vector2 doll = ImGui.GetCursorScreenPos();
        float cell = PartyCell * scale, gapCell = PartyCellGap * scale;
        float railStep = cell + gapCell;
        float inner = ImGui.GetContentRegionAvail().X - 12f * scale;  // scrollbar reserve
        float rightRailX = inner - cell;

        // Portrait between the rails: the live 3-D bake (V is flipped — render
        // target), the class-colored initial otherwise.
        var portraitSize = new Vector2(44f) * scale;
        Vector2 portraitMin = doll + new Vector2((inner - portraitSize.X) * 0.5f, 0f);
        ImGui.SetCursorScreenPos(portraitMin);
        ImGui.InvisibleButton($"##pinv-face-{guid}", portraitSize);
        uint baked = PartyColumnPortraitHandle(guid);
        if (baked != 0)
            dl.AddImage((nint)baked, portraitMin, portraitMin + portraitSize,
                new Vector2(0, 1), new Vector2(1, 0));
        else
        {
            (_, byte classId, _, _) = owner.Fields.Bytes0;
            dl.AddRectFilled(portraitMin, portraitMin + portraitSize, ClassChipColor(classId));
            string initial = name.Length > 0 ? name[..1].ToUpperInvariant() : "?";
            Vector2 half = ImGui.CalcTextSize(initial) * 0.5f;
            dl.AddText(portraitMin + portraitSize * 0.5f - half, 0xe0101418, initial);
        }
        DrawClassPortraitBorderRect(dl, portraitMin, portraitMin + portraitSize, guid, scale, name);
        PartyItemDropTarget(guid);   // dropping "on the character" works

        for (int i = 0; i < PartyDollLeftRail.Length; i++)
            DrawPartyEquipCell(dl, owner, guid, PartyDollLeftRail[i],
                doll + new Vector2(0f, i * railStep), cell, scale);
        for (int i = 0; i < PartyDollRightRail.Length; i++)
            DrawPartyEquipCell(dl, owner, guid, PartyDollRightRail[i],
                doll + new Vector2(rightRailX, i * railStep), cell, scale);
        float weaponsY = PartyDollLeftRail.Length * railStep + 2f * scale;
        float weaponsX = (inner - (PartyDollWeapons.Length * railStep - gapCell)) * 0.5f;
        for (int i = 0; i < PartyDollWeapons.Length; i++)
            DrawPartyEquipCell(dl, owner, guid, PartyDollWeapons[i],
                doll + new Vector2(weaponsX + i * railStep, weaponsY), cell, scale);

        ImGui.SetCursorScreenPos(doll + new Vector2(0f, weaponsY + railStep + 4f * scale));

        // ── Bags: live for yourself/the driven body, snapshot for the rest ───
        if (!live && age is null)
        {
            // A member-facts server syncs party members by itself; the possession
            // gesture is only the fallback wire for servers without the push.
            ImGui.TextWrapped(_partyMemberFactsAvailable && IsPartyMemberFactsSubject(guid)
                ? "Bags not received yet — the server syncs party members automatically."
                : "Bags not synced. Possess this companion once (Alt+click " +
                  "in the free view) — the snapshot is kept after release.");
            return;
        }

        GameText.Draw(dl, "GameFontNormalSmall", "Bags", ImGui.GetCursorScreenPos(), scale);
        ImGui.Dummy(new Vector2(1, 12 * scale));
        Vector2 bagsOrigin = ImGui.GetCursorScreenPos();
        int perRow = Math.Max(4, (int)((inner + gapCell) / railStep));
        int drawn = 0;
        for (int gameSlot = 0; gameSlot < 16; gameSlot++)
            DrawPartyBagCell(dl, owner, guid, 0, gameSlot, bagsOrigin, ref drawn, perRow,
                cell, gapCell, scale);
        for (int bag = 0; bag < 4; bag++)
        {
            ulong bagGuid = owner.Fields.PlayerInventorySlot(19 + bag);
            if (bagGuid == 0 || !_entities.TryGet(bagGuid, out WorldEntity bagEntity))
                continue;
            int slots = (int)Math.Min(bagEntity.Fields.ContainerNumSlots, 36);
            for (int s = 0; s < slots; s++)
                DrawPartyBagCell(dl, bagEntity, guid, bag + 1, s, bagsOrigin, ref drawn, perRow,
                    cell, gapCell, scale);
        }
        int rows = (drawn + perRow - 1) / perRow;
        ImGui.SetCursorScreenPos(bagsOrigin + new Vector2(0f, rows * railStep + 4f * scale));
        uint coinage = owner.Fields.Coinage;
        ImGui.TextDisabled($"{coinage / 10000}g {coinage / 100 % 100}s {coinage % 100}c");
    }

    /// <summary>One minified equipment cell — public visible-item entry, always
    /// current for any streamed player. Display + tooltip only in v1 (moving
    /// equipment needs a server dequip, a later Phase C step); a dragged bag
    /// item dropped here still lands on this member (auto-stored).</summary>
    private void DrawPartyEquipCell(ImDrawListPtr dl, WorldEntity owner, ulong guid,
        int equipSlot, Vector2 min, float cell, float scale)
    {
        var size = new Vector2(cell, cell);
        Vector2 max = min + size;
        ImGui.SetCursorScreenPos(min);
        ImGui.InvisibleButton($"##pinv-eq-{guid}-{equipSlot}", size);
        uint entry = owner.Fields.PlayerVisibleItemEntry(equipSlot);
        if (entry != 0)
        {
            _items!.Require(entry, 0, _net!);
            if (_items.TryGet(entry, out ItemTemplate? item) && item is not null)
            {
                uint icon = _gameplayArt!.Handle(item.IconPath);
                if (icon != 0) dl.AddImage((nint)icon, min, max);
                if (ImGui.IsItemHovered())
                    OfferPreparedItemTooltip(
                        new GameTooltipOwnerKey($"item:party-inventory-{guid}", (ulong)equipSlot),
                        PrepareItemTooltipBodySnapshot(item, 1), max);
            }
            else if (ImGui.IsItemHovered())
                HoverTip($"{EquipSlotNames[equipSlot]}: item {entry} (loading…)");
        }
        else
        {
            dl.AddRectFilled(min, max, 0x55101418);
            if (ImGui.IsItemHovered()) HoverTip($"{EquipSlotNames[equipSlot]}: empty");
        }
        dl.AddRect(min, max, ImGui.IsItemHovered() ? 0xffd0b060 : 0xff2a343d,
            0, ImDrawFlags.None, MathF.Max(1f, scale));
        PartyItemDropTarget(guid);
    }

    /// <summary>One small bag cell: icon, stack count, tooltip, drag-drop
    /// source, and the right-click "Give to …" menu (container 0 = backpack,
    /// 1-4 = equipped bags). slotSource is the entity the slot guid is
    /// READ from: the player for the backpack, the equipped BAG entity for
    /// 1-4. CONTAINER_SLOT_* lives on the container -- off a player those
    /// indices land in UNIT_AURA, so every cell resolved to nothing and the
    /// bags drew empty at exactly the right slot count. ownerGuid stays the
    /// member either way: it addresses the wire, the tooltips and the drops.</summary>
    private void DrawPartyBagCell(ImDrawListPtr dl, WorldEntity slotSource, ulong ownerGuid,
        int container, int slot, Vector2 origin, ref int drawn, int perRow, float cell,
        float gap, float scale)
    {
        Vector2 min = origin + new Vector2(drawn % perRow * (cell + gap),
            drawn / perRow * (cell + gap));
        drawn++;
        var size = new Vector2(cell, cell);
        Vector2 max = min + size;
        ImGui.SetCursorScreenPos(min);
        ImGui.InvisibleButton($"##pinv-bag-{ownerGuid}-{container}-{slot}", size,
            ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
        bool hovered = ImGui.IsItemHovered();

        ulong itemGuid = ResolveSlotGuid(slotSource, container, slot);
        WorldEntity? instance = itemGuid != 0 && _entities.TryGet(itemGuid, out WorldEntity found)
            ? found : null;
        ItemTemplate? item = null;
        if (instance is not null)
        {
            _items!.Require(instance.Entry, instance.Guid, _net!);
            _items.TryGet(instance.Entry, out item);
        }

        // Wire terms mirror the snapshot: 255 = character-held backpack slots
        // 23-38; 19-22 = the equipped bag by inventory position.
        byte wireBag = container == 0 ? (byte)255 : (byte)(19 + container - 1);
        byte wireSlot = container == 0 ? (byte)(23 + slot) : (byte)slot;

        if (instance is not null)
        {
            uint icon = 0;
            if (item is not null)
            {
                icon = _gameplayArt!.Handle(item.IconPath);
                if (icon != 0) dl.AddImage((nint)icon, min, max);
            }
            uint stack = instance.Fields.ItemStackCount;
            if (stack > 1)
            {
                string label = stack.ToString();
                Vector2 half = ImGui.CalcTextSize(label);
                dl.AddText(max - half - new Vector2(1f, 0f) * scale, 0xffffffff, label);
            }
            if (hovered && item is not null)
                OfferPreparedItemTooltip(
                    new GameTooltipOwnerKey($"item:party-bags-{ownerGuid}",
                        (ulong)(container * 64 + slot + 1)),
                    PrepareItemTooltipBodySnapshot(item, Math.Max(1u, instance.Fields.ItemStackCount)),
                    max);
            if (_partyItemMoveAvailable && ImGui.BeginDragDropSource())
            {
                _partyDragFrom = ownerGuid;
                _partyDragBag = wireBag;
                _partyDragSlot = wireSlot;
                _partyDragName = item?.Name ?? "Item";
                ImGui.SetDragDropPayload("SUI_PARTY_ITEM", IntPtr.Zero, 0);
                if (icon != 0) ImGui.Image((nint)icon, new Vector2(24f, 24f) * scale);
                ImGui.SameLine();
                ImGui.TextUnformatted(_partyDragName);
                ImGui.EndDragDropSource();
            }
            if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            {
                _partyGiveFrom = ownerGuid;
                _partyGiveBag = wireBag;
                _partyGiveSlot = wireSlot;
                _partyGiveName = item?.Name ?? "Item";
                _partyGiveOpenRequested = true;
            }
        }
        else
            dl.AddRectFilled(min, max, 0x44101418);
        dl.AddRect(min, max, hovered ? 0xffd0b060 : 0xff2a343d,
            0, ImDrawFlags.None, MathF.Max(1f, scale));
        // This cell is a concrete destination slot, so a same-owner drop rearranges into it.
        PartyItemDropTarget(ownerGuid, wireBag, wireSlot, hasDest: true);
    }
}
