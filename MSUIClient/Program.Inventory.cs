using System.Numerics;
using ImGuiNET;
using Silk.NET.Input;
using Silk.NET.OpenGL;
using MSUIClient.Formats;
using MSUIClient.Net;
using MSUIClient.World.Units;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private ItemTemplateCache? _items;
    private bool _backpackOpen;
    private bool _backpackKeyWasDown;
    private int _carriedBag = -1;
    private int _carriedSlot = -1;
    private readonly bool[] _equippedBagOpen = new bool[4];
    private int _liveEquipmentSignature;
    private InventoryTransition? _pendingInventoryTransition;

    private sealed record InventoryTransition(string Kind, ulong ItemGuid, uint Entry, int SourceSlot,
        int DestinationSlot, double SentAt);

    private void InitInventory()
    {
        if (_mpq is null) return;
        ItemDisplayTable? displays = null;
        try
        {
            byte[]? bytes = _mpq.ReadFile(ItemDisplayTable.MpqPath);
            displays = bytes is null ? null : ItemDisplayTable.Parse(bytes);
        }
        catch (Exception ex) { Console.WriteLine($"[items] display catalog failed: {ex.Message}"); }
        _items = new ItemTemplateCache(displays);
        InitBank();
        InitMail();
        InitAuction();
        InitProfessions();
        InitGuild();
    }

    private void UpdateInventoryInput(bool typing)
    {
        bool down = _window.IsDown(Key.B);
        if (down && !_backpackKeyWasDown && !typing && _net is { IsInWorld: true })
            _backpackOpen = !_backpackOpen;
        _backpackKeyWasDown = down;
    }

    private void DiscoverItemTemplates()
    {
        if (_net is null || _items is null) return;
        foreach (WorldEntity entity in _entities.Entities.Values)
            if (entity.Type is ObjectTypeId.Item or ObjectTypeId.Container)
                _items.Require(entity.Entry, entity.Guid, _net);
        SyncLiveEquipmentModel();
        ObserveInventoryTransition();
        ObserveBankTransition();
        ObserveProfessionSkillTransition();
    }

    private void ObserveInventoryTransition()
    {
        if (_pendingInventoryTransition is not { } pending || _net is null ||
            !_entities.TryGet(_net.PlayerGuid, out WorldEntity player)) return;
        int equipped = Enumerable.Range(0, 19).FirstOrDefault(i => player.Fields.PlayerInventorySlot(i) == pending.ItemGuid, -1);
        int backpack = Enumerable.Range(0, 16).FirstOrDefault(i => player.Fields.PlayerBackpackSlot(i) == pending.ItemGuid, -1);
        bool complete = pending.Kind == "equip" ? equipped >= 0 : backpack >= 0;
        if (complete)
        {
            EmitInterface("inventory", "equipment", pending.Kind == "equip" ? "EQUIPPED" : "UNEQUIPPED",
                pending.ItemGuid, $"item={pending.Entry};from={pending.SourceSlot};equipped={equipped};backpack={backpack}");
            _pendingInventoryTransition = null;
        }
        else if (NowSeconds() - pending.SentAt > 5)
        {
            EmitInterface("inventory", "equipment", "TIMEOUT", pending.ItemGuid,
                $"kind={pending.Kind};item={pending.Entry};from={pending.SourceSlot};to={pending.DestinationSlot}");
            _pendingInventoryTransition = null;
        }
    }

    private bool EquipBackpackEntry(uint entry)
    {
        if (_net is null || !_entities.TryGet(_net.PlayerGuid, out WorldEntity player)) return false;
        for (int slot = 0; slot < 16; slot++)
        {
            ulong guid = player.Fields.PlayerBackpackSlot(slot);
            if (guid == 0 || !_entities.TryGet(guid, out WorldEntity item) || item.Entry != entry) continue;
            bool sent = _net.AutoEquipItem(255, (byte)(23 + slot));
            EmitInterface("inventory", "equip-send", sent ? "SENT" : "SEND_FAILED", guid,
                $"item={entry};bag=255;slot={23 + slot};body={Convert.ToHexString(WorldSession.BuildAutoEquipBody(255, (byte)(23 + slot)))}");
            if (sent) _pendingInventoryTransition = new("equip", guid, entry, 23 + slot, -1, NowSeconds());
            return sent;
        }
        EmitInterface("inventory", "equip-send", "REFUSED", 0, $"item={entry};reason=not-in-backpack");
        return false;
    }

    private bool UnequipSlot(int slot)
    {
        if (_net is null || slot is < 0 or >= 19 || !_entities.TryGet(_net.PlayerGuid, out WorldEntity player)) return false;
        ulong guid = player.Fields.PlayerInventorySlot(slot);
        if (guid == 0 || !_entities.TryGet(guid, out WorldEntity item)) return false;
        int empty = Enumerable.Range(0, 16).FirstOrDefault(i => player.Fields.PlayerBackpackSlot(i) == 0, -1);
        if (empty < 0) { EmitInterface("inventory", "unequip-send", "REFUSED", guid, "reason=backpack-full"); return false; }
        byte destination = (byte)(23 + empty);
        bool sent = _net.SwapInventoryItems((byte)slot, destination);
        EmitInterface("inventory", "unequip-send", sent ? "SENT" : "SEND_FAILED", guid,
            $"item={item.Entry};from={slot};to={destination};body={Convert.ToHexString(WorldSession.BuildSwapInventoryBody((byte)slot, destination))}");
        if (sent) _pendingInventoryTransition = new("unequip", guid, item.Entry, slot, destination, NowSeconds());
        return sent;
    }

    private bool InspectCharacterInventory()
    {
        if (_net is null || _items is null || !_entities.TryGet(_net.PlayerGuid, out WorldEntity player)) return false;
        ObjectFields f = player.Fields;
        EmitInterface("character", "stats", "WIRE-SNAPSHOT", _net.PlayerGuid,
            $"level={player.Level};health={f.Health}/{f.MaxHealth};stats={string.Join(',', Enumerable.Range(0, 5).Select(f.Stat))};armor={f.Resistance(0)};attack={f.AttackPower};damage={f.MinDamage:R}-{f.MaxDamage:R};coin={f.Coinage}");
        int equipped = 0, backpack = 0, resolved = 0;
        foreach (int slot in Enumerable.Range(0, 19).Concat(Enumerable.Range(23, 16)))
        {
            ulong guid = slot < 19 ? f.PlayerInventorySlot(slot) : f.PlayerBackpackSlot(slot - 23);
            if (guid == 0 || !_entities.TryGet(guid, out WorldEntity instance)) continue;
            if (slot < 19) equipped++; else backpack++;
            _items.Require(instance.Entry, guid, _net);
            if (!_items.TryGet(instance.Entry, out ItemTemplate? item) || item is null) continue;
            resolved++;
            EmitInterface("inventory", "item-string", "VERIFIED", guid,
                $"slot={slot};entry={item.Entry};name={SanitizeEvidence(item.Name)};quality={item.Quality};inventoryType={item.InventoryType};itemLevel={item.ItemLevel};requiredLevel={item.RequiredLevel};armor={item.Armor};stats={string.Join(',', item.Stats.Select(x => $"{x.Type}:{x.Value}"))};durability={instance.Fields.ItemDurability}/{instance.Fields.ItemMaxDurability}");
        }
        EmitInterface("inventory", "snapshot", "COMPLETE", _net.PlayerGuid,
            $"equipped={equipped};backpack={backpack};resolved={resolved}");
        return true;
    }

    private void SyncLiveEquipmentModel()
    {
        if (_net is null || _items is null || _character is not { Loaded: true } ||
            !_entities.TryGet(_net.PlayerGuid, out WorldEntity player)) return;
        var resolved = new List<(int Slot, ItemTemplate Item)>();
        var hash = new HashCode();
        for (int slot = 0; slot < 19; slot++)
        {
            ulong guid = player.Fields.PlayerInventorySlot(slot);
            hash.Add(guid);
            if (guid == 0) continue;
            if (!_entities.TryGet(guid, out WorldEntity instance)) return;
            _items.Require(instance.Entry, instance.Guid, _net);
            if (!_items.TryGet(instance.Entry, out ItemTemplate? item) || item is null) return;
            resolved.Add((slot, item));
        }
        int signature = hash.ToHashCode();
        if (signature == _liveEquipmentSignature) return;
        var equipment = new CharacterEquipment();
        foreach (var resolvedItem in resolved)
        {
            ItemTemplate item = resolvedItem.Item;
            equipment.Add(item.Name, item.DisplayInfoId, (int)item.InventoryType, resolvedItem.Slot,
                (byte)item.Class, (byte)item.Subclass, (byte)item.Material, (byte)item.Sheath);
        }
        if (EquipmentVisuallyMatches(_character.Equipment, equipment))
        {
            // The character-select renderer already composited this exact
            // outfit. Live item GUIDs arrive later and change the transport
            // signature, not the visible model; rebuilding here allocated
            // 120-145 MB and forced gen2 during Terrain.
            _liveEquipmentSignature = signature;
            return;
        }
        _character.Equipment = equipment;
        _character.ApplyEquipment();
        _liveEquipmentSignature = signature;
        _playerPortraitDirty = true;
        _paperDollDirty = true;
    }

    private static bool EquipmentVisuallyMatches(CharacterEquipment current,
        CharacterEquipment incoming)
    {
        if (current.Pieces.Count != incoming.Pieces.Count) return false;
        foreach (CharacterEquipment.Piece piece in incoming.Pieces)
        {
            bool found = current.Pieces.Any(existing =>
                existing.DisplayId == piece.DisplayId &&
                existing.InventoryType == piece.InventoryType);
            if (!found) return false;
        }
        return true;
    }

    private void DrawInventory()
    {
        DrawBagBar();
        DrawBackpack();
        DrawEquippedBagWindows();
        if (_net is not null && _entities.TryGet(_net.PlayerGuid, out WorldEntity player))
            DrawCarriedItem(player, GameplayUiScale());
    }

    private void DrawBackpack()
    {
        if (!_backpackOpen || _net is null || _items is null || _gameplayArt is null ||
            !_entities.TryGet(_net.PlayerGuid, out WorldEntity player)) return;

        Vector2 display = ImGui.GetIO().DisplaySize;
        float scale = GameplayUiScale();
        Vector2 frameSize = new(192f, 240f);
        Vector2 frameMin = new(display.X - frameSize.X * scale, display.Y - (70f + frameSize.Y) * scale);
        Vector2 windowMin = frameMin - new Vector2(64f, 0) * scale;

        ImGui.SetNextWindowPos(windowMin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(256f, 256f) * scale, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
                                 ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground |
                                 ImGuiWindowFlags.NoNav;
        if (!ImGui.Begin("##backpack", flags)) { ImGui.End(); return; }
        ImDrawListPtr dl = ImGui.GetWindowDrawList();

        uint portrait = _gameplayArt.Handle(@"Interface\Buttons\Button-Backpack-Up.blp");
        if (portrait != 0)
        {
            Vector2 min = frameMin + new Vector2(7f, 5f) * scale;
            dl.AddImage((nint)portrait, min, min + new Vector2(40f) * scale);
        }
        uint background = _gameplayArt.Handle(@"Interface\ContainerFrame\UI-BackpackBackground.blp");
        if (background != 0)
            dl.AddImage((nint)background, windowMin, windowMin + new Vector2(256f) * scale);

        dl.AddText(ImGui.GetFont(), 13f * scale, frameMin + new Vector2(47f, 10f) * scale,
            0xffffffff, "Backpack");

        for (int gameSlot = 0; gameSlot < 16; gameSlot++)
        {
            // Slot 1 appears top-left; the authored anchor chain itself grows bottom-right upward.
            int row = gameSlot / 4;
            int col = gameSlot % 4;
            Vector2 slotMin = frameMin + new Vector2(14f + col * 42f, 45f + row * 41f) * scale;
            Vector2 slotMax = slotMin + new Vector2(37f) * scale;
            ulong guid = player.Fields.PlayerBackpackSlot(gameSlot);
            WorldEntity? instance = guid != 0 && _entities.TryGet(guid, out WorldEntity found) ? found : null;
            ItemTemplate? template = null;
            if (instance is not null)
            {
                _items.Require(instance.Entry, instance.Guid, _net);
                _items.TryGet(instance.Entry, out template);
            }

            if (template is not null)
            {
                uint icon = _gameplayArt.Handle(template.IconPath);
                if (icon != 0) dl.AddImage((nint)icon, slotMin, slotMax);
            }

            ImGui.SetCursorScreenPos(slotMin);
            ImGui.InvisibleButton($"##pack-{gameSlot}", slotMax - slotMin,
                ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
            if (ImGui.IsItemClicked(ImGuiMouseButton.Left)) PickupOrPlaceItem(255, 23 + gameSlot, guid);
            if (ImGui.IsItemClicked(ImGuiMouseButton.Right) && instance is not null && template is not null)
                UseBackpackItem(gameSlot, template);
            if (ImGui.IsItemHovered() && template is not null)
                DrawItemTooltip(template, instance?.Fields.ItemStackCount ?? 1);

            uint ring = _gameplayArt.Handle(@"Interface\Buttons\UI-Quickslot2.blp");
            if (ring != 0)
            {
                Vector2 center = (slotMin + slotMax) * 0.5f + new Vector2(0, -scale);
                Vector2 half = new(32f * scale);
                dl.AddImage((nint)ring, center - half, center + half);
            }
            uint count = instance?.Fields.ItemStackCount ?? 0;
            if (count > 1)
            {
                string label = count.ToString();
                Vector2 textSize = ImGui.CalcTextSize(label);
                dl.AddText(slotMax - textSize - new Vector2(4f, 2f) * scale, 0xffffffff, label);
            }
        }

        DrawMoney(dl, frameMin, player.Fields.Coinage, scale);
        ImGui.End();

    }

    private void DrawBagBar()
    {
        if (_net is not { IsInWorld: true } || _gameplayArt is null || _items is null ||
            !_entities.TryGet(_net.PlayerGuid, out WorldEntity player)) return;
        float s = GameplayUiScale();
        Vector2 display = ImGui.GetIO().DisplaySize;
        Vector2 barMin = GameplayBarMin(display, s);
        // BagFrame.xml:1233-1297: anchor from the bar's RIGHT edge so every UI scale keeps
        // the backpack and four 42px-chain bag wells seated in their authored recesses.
        Vector2 backpackMin = barMin + new Vector2(981f, 14f) * s;
        Vector2 firstBagMin = backpackMin - new Vector2(168f, 0f) * s;
        Vector2 windowMin = barMin + new Vector2(798f, 0f) * s;
        CollectGameplayLayout("bag-cluster", 798f, 715f, 226f, 53f,
            windowMin, new Vector2(226f, 53f) * s);
        ImGui.SetNextWindowPos(windowMin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(226, 53) * s, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        if (!ImGui.Begin("##bag-bar", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav))
        { ImGui.End(); return; }
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        for (int i = 0; i < 5; i++)
        {
            Vector2 min = i == 4 ? backpackMin : firstBagMin + new Vector2(i * 42f, 0f) * s;
            float buttonSize = i == 4 ? 37f : 36f;
            float authoredX = i == 4 ? 981f : 813f + i * 42f;
            string layoutId = i == 4 ? "backpack" : $"bag-slot-{4 - i}";
            CollectGameplayLayout(layoutId, authoredX, 729f, buttonSize, buttonSize,
                min, new Vector2(buttonSize) * s);
            string art = i == 4 ? @"Interface\Buttons\Button-Backpack-Up" : @"Interface\Paperdoll\UI-PaperDoll-Slot-Bag";
            ItemTemplate? bagTemplate = null;
            if (i < 4)
            {
                // Bag 0 is closest to the backpack in the original right-to-left anchor chain.
                int bagIndex = 3 - i;
                ulong bagGuid = player.Fields.PlayerInventorySlot(19 + bagIndex);
                if (bagGuid != 0 && _entities.TryGet(bagGuid, out WorldEntity bag))
                {
                    _items.Require(bag.Entry, bag.Guid, _net);
                    if (_items.TryGet(bag.Entry, out ItemTemplate? template) && template is not null)
                    { bagTemplate = template; art = template.IconPath; }
                }
            }
            uint icon = _gameplayArt.Handle(art);
            if (icon != 0) dl.AddImage((nint)icon, min, min + new Vector2(buttonSize) * s);
            ImGui.SetCursorScreenPos(min);
            ImGui.InvisibleButton($"##bag-button-{i}", new Vector2(buttonSize) * s);
            bool hovered = ImGui.IsItemHovered();
            if (ImGui.IsItemClicked())
            {
                if (i == 4) _backpackOpen = !_backpackOpen;
                else
                {
                    int bagIndex = 3 - i;
                    if (player.Fields.PlayerInventorySlot(19 + bagIndex) != 0)
                        _equippedBagOpen[bagIndex] = !_equippedBagOpen[bagIndex];
                }
            }
            uint ring = i < 4 ? _gameplayArt.Handle(@"Interface\Buttons\UI-Quickslot2") : 0;
            if (ring != 0)
            {
                Vector2 center = min + new Vector2(18f) * s, half = new(33f * s);
                dl.AddImage((nint)ring, center - half, center + half);
            }
            bool checkedState = i == 4 ? _backpackOpen : _equippedBagOpen[3 - i];
            if (checkedState)
            {
                uint check = _gameplayArt.AdditiveHandle(@"Interface\Buttons\CheckButtonHilight");
                if (check != 0) dl.AddImage((nint)check, min, min + new Vector2(buttonSize) * s);
            }
            if (hovered)
            {
                uint highlight = _gameplayArt.AdditiveHandle(@"Interface\Buttons\ButtonHilight-Square");
                if (highlight != 0) dl.AddImage((nint)highlight, min, min + new Vector2(buttonSize) * s);
                ImGui.BeginTooltip();
                ImGui.TextUnformatted(i == 4 ? "Backpack (B)" : bagTemplate?.Name ?? "Equip Container");
                ImGui.EndTooltip();
            }
        }
        ImGui.End();
    }

    private void DrawEquippedBagWindows()
    {
        if (_net is null || _gameplayArt is null || _items is null ||
            !_entities.TryGet(_net.PlayerGuid, out WorldEntity player)) return;
        float s = GameplayUiScale();
        Vector2 display = ImGui.GetIO().DisplaySize;
        float stackX = display.X - 192f * s;
        float stackBottom = display.Y - 70f * s;
        for (int bagIndex = 0; bagIndex < 4; bagIndex++)
        {
            if (!_equippedBagOpen[bagIndex]) continue;
            ulong guid = player.Fields.PlayerInventorySlot(19 + bagIndex);
            if (guid == 0 || !_entities.TryGet(guid, out WorldEntity bag) ||
                bag.Type is not ObjectTypeId.Container) { _equippedBagOpen[bagIndex] = false; continue; }
            _items.Require(bag.Entry, bag.Guid, _net);
            _items.TryGet(bag.Entry, out ItemTemplate? template);
            int slots = (int)Math.Clamp(bag.Fields.ContainerNumSlots, 1, 20);
            int rows = (slots + 3) / 4;
            float height = 104f + (rows - 1) * 41f;
            Vector2 frameMin = new(stackX, stackBottom - height * s);
            DrawEquippedBagWindow(frameMin, s, bagIndex, bag, template, slots, rows);
            stackBottom = frameMin.Y - 4f * s;
        }
    }

    private void DrawEquippedBagWindow(Vector2 p, float s, int bagIndex, WorldEntity bag,
        ItemTemplate? bagTemplate, int slots, int rows)
    {
        float height = 104f + (rows - 1) * 41f;
        ImGui.SetNextWindowPos(p - new Vector2(64, 0) * s, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(256, height) * s, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        if (!ImGui.Begin($"##bag-window-{bagIndex}", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav))
        { ImGui.End(); return; }
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        Vector2 artMin = p - new Vector2(64, 0) * s;
        uint bg = _gameplayArt!.Handle(@"Interface\ContainerFrame\UI-Bag-Components");
        if (bg != 0)
        {
            float topHeight = 94f, middleHeight = Math.Max(0, (rows - 1) * 41f);
            dl.AddImage((nint)bg, artMin, artMin + new Vector2(256, topHeight) * s,
                new Vector2(0, 0.00390625f), new Vector2(1, 0.18359375f));
            if (middleHeight > 0) dl.AddImage((nint)bg, artMin + new Vector2(0, topHeight) * s,
                artMin + new Vector2(256, topHeight + middleHeight) * s,
                new Vector2(0, 0.353515625f), new Vector2(1, 0.416015625f));
            dl.AddImage((nint)bg, artMin + new Vector2(0, height - 10) * s, artMin + new Vector2(256, height) * s,
                new Vector2(0, 0.330078125f), new Vector2(1, 0.349609375f));
        }
        uint portrait = _gameplayArt.Handle(bagTemplate?.IconPath ?? @"Interface\Buttons\Button-Backpack-Up");
        if (portrait != 0) dl.AddImage((nint)portrait, p + new Vector2(7, 5) * s, p + new Vector2(47, 45) * s);
        dl.AddText(ImGui.GetFont(), 13f * s, p + new Vector2(47, 10) * s, 0xffffffff,
            bagTemplate?.Name ?? "Bag");

        for (int slot = 0; slot < slots; slot++)
        {
            int row = slot / 4, col = slot % 4;
            Vector2 min = p + new Vector2(14 + col * 42, 45 + row * 41) * s;
            Vector2 max = min + new Vector2(37) * s;
            ulong itemGuid = bag.Fields.ContainerSlot(slot);
            WorldEntity? instance = itemGuid != 0 && _entities.TryGet(itemGuid, out WorldEntity found) ? found : null;
            ItemTemplate? item = null;
            if (instance is not null)
            {
                _items!.Require(instance.Entry, instance.Guid, _net!);
                _items.TryGet(instance.Entry, out item);
            }
            uint icon = item is null ? 0 : _gameplayArt.Handle(item.IconPath);
            if (icon != 0) dl.AddImage((nint)icon, min, max);
            ImGui.SetCursorScreenPos(min);
            ImGui.InvisibleButton($"##bag-{bagIndex}-{slot}", max - min,
                ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
            if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
                PickupOrPlaceItem(19 + bagIndex, slot, itemGuid);
            if (ImGui.IsItemClicked(ImGuiMouseButton.Right) && item is not null)
            {
                if (item.InventoryType != 0) _net!.AutoEquipItem((byte)(19 + bagIndex), (byte)slot);
                else _net!.UseItem((byte)(19 + bagIndex), (byte)slot, item.UseSpellIndex);
            }
            if (ImGui.IsItemHovered() && item is not null)
                DrawItemTooltip(item, instance?.Fields.ItemStackCount ?? 1,
                    instance?.Fields.ItemDurability ?? 0, instance?.Fields.ItemMaxDurability ?? 0);
            uint ring = _gameplayArt.Handle(@"Interface\Buttons\UI-Quickslot2");
            if (ring != 0)
            {
                Vector2 center = (min + max) * .5f + new Vector2(0, -s), half = new(32f * s);
                dl.AddImage((nint)ring, center - half, center + half);
            }
            uint count = instance?.Fields.ItemStackCount ?? 0;
            if (count > 1) dl.AddText(max - ImGui.CalcTextSize(count.ToString()) - new Vector2(4, 2) * s,
                0xffffffff, count.ToString());
        }
        ImGui.End();
    }

    private void PickupOrPlaceItem(int bag, int slot, ulong guid)
    {
        if (_net is null) return;
        if (_carriedBag < 0)
        {
            if (guid != 0) { _carriedBag = bag; _carriedSlot = slot; }
            return;
        }
        if (_carriedBag == bag && _carriedSlot == slot) { ClearCarriedItem(); return; }
        if (_carriedBag == 255 && bag == 255)
            _net.SwapInventoryItems((byte)_carriedSlot, (byte)slot);
        else
            _net.SwapItems((byte)bag, (byte)slot, (byte)_carriedBag, (byte)_carriedSlot);
        ClearCarriedItem();
    }

    private void ClearCarriedItem() { _carriedBag = -1; _carriedSlot = -1; }

    private WorldEntity? ResolveCarriedItem()
    {
        if (_carriedBag < 0 || _net is null || !_entities.TryGet(_net.PlayerGuid, out WorldEntity player)) return null;
        ulong guid;
        if (_carriedBag == 255)
            guid = _carriedSlot < 23 ? player.Fields.PlayerInventorySlot(_carriedSlot)
                : player.Fields.PlayerBackpackSlot(_carriedSlot - 23);
        else
        {
            int index = _carriedBag - 19;
            ulong bagGuid = index is >= 0 and < 4 ? player.Fields.PlayerInventorySlot(19 + index) : 0;
            guid = bagGuid != 0 && _entities.TryGet(bagGuid, out WorldEntity bag)
                ? bag.Fields.ContainerSlot(_carriedSlot) : 0;
        }
        return guid != 0 && _entities.TryGet(guid, out WorldEntity item) ? item : null;
    }

    private bool PlaceCarriedItemOnAction(int actionSlot)
    {
        if (_carriedBag < 0 || _net is null || ResolveCarriedItem() is not { } item) return false;
        var action = new ActionSlot(ActionSlot.Item, item.Entry);
        _actions.Set(actionSlot, action);
        _net.SetActionButton((byte)actionSlot, action.Packed);
        ClearCarriedItem();
        return true;
    }

    private void UseItemAction(uint entry)
    {
        if (_net is null || !_entities.TryGet(_net.PlayerGuid, out WorldEntity player)) return;
        for (int i = 0; i < 16; i++)
        {
            ulong guid = player.Fields.PlayerBackpackSlot(i);
            if (guid != 0 && _entities.TryGet(guid, out WorldEntity item) && item.Entry == entry)
            { _net.UseItem(255, (byte)(23 + i), 0); return; }
        }
        for (int bagIndex = 0; bagIndex < 4; bagIndex++)
        {
            ulong bagGuid = player.Fields.PlayerInventorySlot(19 + bagIndex);
            if (bagGuid == 0 || !_entities.TryGet(bagGuid, out WorldEntity bag)) continue;
            for (int slot = 0; slot < bag.Fields.ContainerNumSlots; slot++)
            {
                ulong guid = bag.Fields.ContainerSlot(slot);
                if (guid != 0 && _entities.TryGet(guid, out WorldEntity item) && item.Entry == entry)
                { _net.UseItem((byte)(19 + bagIndex), (byte)slot, 0); return; }
            }
        }
    }

    private void UseBackpackItem(int slot, ItemTemplate item)
    {
        if (_net is null) return;
        if (item.InventoryType != 0) _net.AutoEquipItem(255, (byte)(23 + slot));
        else _net.UseItem(255, (byte)(23 + slot), item.UseSpellIndex);
    }

    private void DrawCarriedItem(WorldEntity player, float scale)
    {
        if (_carriedBag < 0 || _items is null || _gameplayArt is null) return;
        if (ResolveCarriedItem() is not { } item ||
            !_items.TryGet(item.Entry, out ItemTemplate? template) || template is null) return;
        uint icon = _gameplayArt.Handle(template.IconPath);
        if (icon == 0) return;
        Vector2 min = ImGui.GetIO().MousePos + new Vector2(12f) * scale;
        ImGui.GetForegroundDrawList().AddImage((nint)icon, min, min + new Vector2(32f) * scale,
            Vector2.Zero, Vector2.One, 0xccffffff);
    }

    private static void DrawItemTooltip(ItemTemplate item, uint count, uint durability = 0, uint maxDurability = 0)
    {
        Vector4 quality = item.Quality switch
        {
            0 => new Vector4(0.62f, 0.62f, 0.62f, 1), 2 => new Vector4(0.12f, 1f, 0, 1),
            3 => new Vector4(0, 0.44f, 0.87f, 1), 4 => new Vector4(0.64f, 0.21f, 0.93f, 1),
            5 => new Vector4(1f, 0.50f, 0, 1), 6 => new Vector4(0.90f, 0.80f, 0.50f, 1),
            _ => Vector4.One,
        };
        ImGui.BeginTooltip();
        ImGui.TextColored(quality, item.Name);
        if (item.Bonding == 1) ImGui.TextUnformatted("Binds when picked up");
        else if (item.Bonding == 2) ImGui.TextUnformatted("Binds when equipped");
        foreach (ItemDamage damage in item.Damages)
            ImGui.TextUnformatted($"{damage.Min:0.#} - {damage.Max:0.#} Damage");
        if (item.Armor > 0) ImGui.TextUnformatted($"{item.Armor} Armor");
        string[] statNames = ["Mana", "Health", "Agility", "Strength", "Intellect", "Spirit", "Stamina"];
        foreach (ItemStat stat in item.Stats)
            ImGui.TextUnformatted($"{(stat.Value >= 0 ? "+" : "")}{stat.Value} {(stat.Type < statNames.Length ? statNames[stat.Type] : $"Stat {stat.Type}")}");
        if (item.RequiredLevel > 0) ImGui.TextUnformatted($"Requires Level {item.RequiredLevel}");
        if (item.ItemLevel > 0) ImGui.TextDisabled($"Item Level {item.ItemLevel}");
        if (maxDurability > 0) ImGui.TextUnformatted($"Durability {durability} / {maxDurability}");
        if (count > 1) ImGui.TextDisabled($"Stack: {count} / {Math.Max(1, item.Stackable)}");
        if (!string.IsNullOrWhiteSpace(item.Description)) ImGui.TextColored(new Vector4(1f, .82f, 0f, 1f), item.Description);
        ImGui.EndTooltip();
    }

    private void DrawMoney(ImDrawListPtr dl, Vector2 frameMin, uint copper, float scale)
    {
        uint icons = _gameplayArt?.Handle(@"Interface\MoneyFrame\UI-MoneyIcons.blp") ?? 0;
        uint gold = copper / 10000, silver = copper / 100 % 100, coin = copper % 100;
        string text = $"{gold}  {silver}  {coin}";
        Vector2 pos = frameMin + new Vector2(62f, 216f) * scale;
        dl.AddText(ImGui.GetFont(), 12f * scale, pos, 0xffffffff, text);
        if (icons != 0)
            dl.AddImage((nint)icons, frameMin + new Vector2(164f, 215f) * scale,
                frameMin + new Vector2(177f, 228f) * scale, new Vector2(0.5f, 0), new Vector2(0.75f, 1));
    }
}
