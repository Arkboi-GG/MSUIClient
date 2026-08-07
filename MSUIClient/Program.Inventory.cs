using System.Numerics;
using ImGuiNET;
using Silk.NET.Input;
using Silk.NET.OpenGL;
using MSUIClient.Formats;
using MSUIClient.Engine.UI;
using MSUIClient.Net;
using MSUIClient.World.Units;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private ItemTemplateCache? _items;
    private bool _backpackOpen;
    private bool _backpackKeyWasDown;
    private int _carriedContainer = InventoryUiLaw.EmptyContainer;
    private int _carriedSlot = -1;
    private int? _carriedCount;
    private readonly bool[] _equippedBagOpen = new bool[4];
    private bool _keyringOpen;
    private readonly List<int> _bagWindowOrder = [];
    private readonly Dictionary<int, Vector2> _bagWindowPositions = [];
    private readonly Dictionary<(int Container, int Slot), PendingBagLock> _pendingBagLocks = [];
    private int _splitContainer = InventoryUiLaw.EmptyContainer;
    private int _splitSlot = -1;
    private int _splitMaximum;
    private int _splitCount = 1;
    private int _itemPushContainer = InventoryUiLaw.EmptyContainer;
    private uint _itemPushEntry;
    private double _itemPushStartedAt = double.NegativeInfinity;
    private readonly Dictionary<int, Vector2> _bagButtonPositions = [];
    private int _liveEquipmentSignature;
    private InventoryTransition? _pendingInventoryTransition;

    private sealed record InventoryTransition(string Kind, ulong ItemGuid, uint Entry, int SourceSlot,
        int DestinationSlot, double SentAt);
    private sealed record PendingBagLock(ulong Guid, uint Count, double SentAt);

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
        if (_creatures is not null)
            _creatures.PlayerItemResolver = entry =>
                (_items.TryGet(entry, out ItemTemplate? item), item);
        InitBank();
        InitMail();
        InitAuction();
        InitProfessions();
        InitGuild();
        InitTabard();
        InitTalents();
    }

    private void UpdateInventoryInput(bool typing)
    {
        bool down = BindingDown(GameBinding.OpenBackpack);
        if (down && !_backpackKeyWasDown && !typing && _net is { IsInWorld: true })
        {
            bool shift = _window.IsDown(Key.ShiftLeft) || _window.IsDown(Key.ShiftRight);
            if (InventoryUiLaw.BindingAction(shift) == InventoryUiLaw.BagBindingAction.ToggleAllBags)
                ToggleAllBags();
            else
                ToggleBackpack();
        }
        _backpackKeyWasDown = down;
    }

    private void DiscoverItemTemplates()
    {
        if (_net is null || _items is null) return;
        foreach (WorldEntity entity in _entities.Entities.Values)
        {
            if (entity.Type is ObjectTypeId.Item or ObjectTypeId.Container)
                _items.Require(entity.Entry, entity.Guid, _net);
            if (entity.IsPlayer)
                for (int slot = 0; slot < 19; slot++)
                    _items.Require(entity.Fields.PlayerVisibleItemEntry(slot), 0, _net);
        }
        SyncLiveEquipmentModel();
        ObserveInventoryTransition();
        ObserveBagLocks();
        ObserveBankTransition();
        ObserveProfessionSkillTransition();
        ObserveProfessionProductTransition();
        ObserveTalentTransition();
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
                $"slot={slot};entry={item.Entry};count={Math.Max(1, instance.Fields.ItemStackCount)};name={SanitizeEvidence(item.Name)};" +
                $"quality={item.Quality};inventoryType={item.InventoryType};itemLevel={item.ItemLevel};requiredLevel={item.RequiredLevel};" +
                $"armor={item.Armor};stats={string.Join(',', item.Stats.Select(x => $"{x.Type}:{x.Value}"))};" +
                $"durability={instance.Fields.ItemDurability}/{instance.Fields.ItemMaxDurability}");
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
        var equipment = new CharacterEquipment { GuildEmblem = _tabardDesign };
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
        if (_net is null || !_entities.TryGet(_net.PlayerGuid, out WorldEntity player)) return;
        LayoutBagWindows(player);
        DrawBagBar();
        DrawBackpack();
        DrawEquippedBagWindows();
        DrawKeyringWindow(player);
        DrawStackSplit();
        DrawItemPushAnimation();
        DrawCarriedItem(player, GameplayUiScale());
    }

    private void DrawBackpack()
    {
        if (!_backpackOpen || _net is null || _items is null || _gameplayArt is null ||
            !_entities.TryGet(_net.PlayerGuid, out WorldEntity player)) return;

        float scale = GameplayUiScale();
        Vector2 frameSize = new(192f, 240f);
        if (!_bagWindowPositions.TryGetValue(0, out Vector2 frameMin)) return;
        Vector2 windowMin = frameMin - new Vector2(64f, 0) * scale;

        if (_uiParityArmed && _uiParityPanel == "backpack")
        {
            BeginUiParityFrame(frameMin, scale);
            CollectUiParity("ContainerFrame1", "Frame", frameMin, frameSize * scale, parent: "",
                point: "BOTTOMRIGHT", relativeTo: "UIParent", relativePoint: "BOTTOMRIGHT", offsetX: "0", offsetY: "70", strata: "MEDIUM");
            CollectUiParity("ContainerFrame1BackgroundTop", "Texture", windowMin, new Vector2(256) * scale,
                parent: "ContainerFrame1", point: "TOPRIGHT", relativeTo: "ContainerFrame1", relativePoint: "TOPRIGHT",
                offsetX: "0", offsetY: "0", texture: @"Interface\ContainerFrame\UI-BackpackBackground", layer: "ARTWORK", strata: "MEDIUM", texCoords: "0,1,0,1");
            CollectUiParity("ContainerFrame1Portrait", "Texture", frameMin + new Vector2(7, 5) * scale,
                new Vector2(40) * scale, parent: "ContainerFrame1", point: "TOPLEFT", relativeTo: "ContainerFrame1",
                relativePoint: "TOPLEFT", offsetX: "7", offsetY: "-5", texture: @"Interface\Buttons\Button-Backpack-Up", layer: "BACKGROUND", strata: "MEDIUM");
            CollectUiParity("ContainerFrame1Name", "FontString", frameMin + new Vector2(47, 10) * scale,
                new Vector2(112, 12) * scale, parent: "ContainerFrame1", point: "TOPLEFT", relativeTo: "ContainerFrame1",
                relativePoint: "TOPLEFT", offsetX: "47", offsetY: "-10", font: "GameFontHighlight", fontPath: @"Fonts\FRIZQT__.TTF",
                fontSize: "12", color: "1,1,1,1", layer: "ARTWORK", strata: "MEDIUM");
            CollectUiParity("ContainerFrame1CloseButton", "Button", frameMin + new Vector2(160, 1) * scale,
                new Vector2(32) * scale, parent: "ContainerFrame1", point: "TOPRIGHT", relativeTo: "ContainerFrame1",
                relativePoint: "TOPRIGHT", offsetX: "0", offsetY: "-1", strata: "MEDIUM");
            CollectUiParity("ContainerFrame1CloseButton/NormalTexture", "NormalTexture", frameMin + new Vector2(160, 1) * scale,
                new Vector2(32) * scale, parent: "ContainerFrame1CloseButton", texture: @"Interface\Buttons\UI-Panel-MinimizeButton-Up", strata: "MEDIUM");
        }

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

        GameText.Draw(dl, "GameFontNormal", "Backpack",
            frameMin + new Vector2(47f, 10f) * scale, scale);

        Vector2 closeMin = frameMin + new Vector2(160, 1) * scale;
        uint close = _gameplayArt.Handle(@"Interface\Buttons\UI-Panel-MinimizeButton-Up");
        if (close != 0) dl.AddImage((nint)close, closeMin, closeMin + new Vector2(32) * scale);
        ImGui.SetCursorScreenPos(closeMin);
        ImGui.InvisibleButton("##backpack-close", new Vector2(32) * scale);
        if (ImGui.IsItemClicked()) SetBagWindowOpen(0, false);

        for (int gameSlot = 0; gameSlot < 16; gameSlot++)
        {
            InventoryUiLaw.SlotGeometry cell = InventoryUiLaw.Slot(16, gameSlot, 240f, backpack: true);
            Vector2 slotMin = frameMin + new Vector2(cell.X, cell.Y) * scale;
            if (_uiParityArmed && _uiParityPanel == "backpack" && gameSlot == 0)
            {
                CollectUiParity("ContainerFrame1Item1", "Button", slotMin, new Vector2(37) * scale,
                    parent: "ContainerFrame1", point: "BOTTOMRIGHT", relativeTo: "ContainerFrame1", relativePoint: "BOTTOMRIGHT",
                    offsetX: "-12", offsetY: "30", strata: "MEDIUM");
                CollectUiParity("ContainerFrame1Item1NormalTexture", "NormalTexture", slotMin - new Vector2(13, 14) * scale,
                    new Vector2(64) * scale, parent: "ContainerFrame1Item1", point: "CENTER", relativeTo: "ContainerFrame1Item1",
                    relativePoint: "CENTER", offsetX: "0", offsetY: "-1", texture: @"Interface\Buttons\UI-Quickslot2", strata: "MEDIUM");
            }
            DrawInventorySlot(dl, player, 0, gameSlot, slotMin, scale, $"pack-{gameSlot}");
        }

        DrawMoney(dl, frameMin, player.Fields.Coinage, scale);
        if (_uiParityArmed && _uiParityPanel == "backpack") MarkUiParityFrameComplete();
        ImGui.End();

    }

    private void DrawBagBar()
    {
        if (_net is not { IsInWorld: true } || _gameplayArt is null || _items is null ||
            !_entities.TryGet(_net.PlayerGuid, out WorldEntity player)) return;
        float s = GameplayUiScale();
        Vector2 display = ImGui.GetIO().DisplaySize;
        Vector2 barMin = GameplayBarMin(display, s);
        Vector2 backpackMin = barMin + new Vector2(981f, 14f) * s;
        Vector2 firstBagMin = backpackMin - new Vector2(168f, 0f) * s;
        bool hasKey = HasKey(player);
        Vector2 windowMin = barMin + new Vector2(hasKey ? 774f : 798f, 0f) * s;
        float windowWidth = hasKey ? 250f : 226f;
        CollectGameplayLayout("bag-cluster", hasKey ? 774f : 798f, 715f, windowWidth, 53f,
            windowMin, new Vector2(windowWidth, 53f) * s);
        ImGui.SetNextWindowPos(windowMin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(windowWidth, 53) * s, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        if (!ImGui.Begin("##bag-bar", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav))
        { ImGui.End(); return; }
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        _bagButtonPositions.Clear();
        int[] containers = [4, 3, 2, 1, 0];
        for (int i = 0; i < containers.Length; i++)
        {
            int container = containers[i];
            Vector2 min = container == 0 ? backpackMin : firstBagMin + new Vector2(i * 42f, 0f) * s;
            _bagButtonPositions[container] = min;
            float buttonSize = container == 0 ? 37f : 36f;
            float authoredX = container == 0 ? 981f : 813f + i * 42f;
            string layoutId = container == 0 ? "backpack" : $"bag-slot-{container}";
            CollectGameplayLayout(layoutId, authoredX, 729f, buttonSize, buttonSize,
                min, new Vector2(buttonSize) * s);
            string art = container == 0 ? @"Interface\Buttons\Button-Backpack-Up" : @"Interface\Paperdoll\UI-PaperDoll-Slot-Bag";
            ItemTemplate? bagTemplate = null;
            ulong bagGuid = 0;
            int equipmentSlot = -1;
            if (container != 0)
            {
                int bagIndex = container - 1;
                equipmentSlot = 19 + bagIndex;
                bagGuid = player.Fields.PlayerInventorySlot(equipmentSlot);
                if (bagGuid != 0 && _entities.TryGet(bagGuid, out WorldEntity bag))
                {
                    _items.Require(bag.Entry, bag.Guid, _net);
                    if (_items.TryGet(bag.Entry, out ItemTemplate? template) && template is not null)
                    { bagTemplate = template; art = template.IconPath; }
                }
            }
            uint icon = _gameplayArt.Handle(art);
            bool locked = container != 0 && IsInventorySlotLocked(InventoryUiLaw.EquipmentContainer, equipmentSlot);
            bool menuDisabled = _settingsOpen && InventoryUiLaw.DisableWithGameMenu(container);
            uint tint = menuDisabled || locked ? 0xff666666 : 0xffffffff;
            if (icon != 0) dl.AddImage((nint)icon, min, min + new Vector2(buttonSize) * s,
                Vector2.Zero, Vector2.One, tint);
            ImGui.SetCursorScreenPos(min);
            ImGui.InvisibleButton($"##bag-button-{container}", new Vector2(buttonSize) * s);
            bool hovered = ImGui.IsItemHovered();
            if (!_settingsOpen && ImGui.IsItemClicked())
            {
                switch (InventoryUiLaw.BagBarAction(container, HasCarriedItem, bagGuid != 0))
                {
                    case InventoryUiLaw.BagBarClickAction.ToggleBackpack: ToggleBackpack(); break;
                    case InventoryUiLaw.BagBarClickAction.ToggleBag:
                        SetBagWindowOpen(container, !_equippedBagOpen[container - 1]); break;
                    case InventoryUiLaw.BagBarClickAction.PickupOrPlace:
                        PickupOrPlaceItem(InventoryUiLaw.EquipmentContainer, equipmentSlot, bagGuid); break;
                }
            }
            if (container != 0 && !_settingsOpen)
                HandleBagBarDrag(container, equipmentSlot, bagGuid, bagTemplate, min, buttonSize, s);
            uint ring = container != 0 ? _gameplayArt.Handle(@"Interface\Buttons\UI-Quickslot2") : 0;
            if (ring != 0)
            {
                Vector2 center = min + new Vector2(18f) * s, half = new(33f * s);
                dl.AddImage((nint)ring, center - half, center + half);
            }
            bool checkedState = container == 0 ? _backpackOpen : _equippedBagOpen[container - 1];
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
                ImGui.TextUnformatted(container == 0
                    ? $"Backpack ({BindingText(GameBinding.OpenBackpack)})"
                    : bagTemplate?.Name ?? "Equip Container");
                ImGui.EndTooltip();
            }
        }
        if (hasKey)
        {
            Vector2 min = firstBagMin - new Vector2(24f, 1.5f) * s;
            _bagButtonPositions[InventoryUiLaw.KeyringContainer] = min;
            uint icon = _gameplayArt.Handle(@"Interface\Buttons\UI-Button-KeyRing");
            if (icon != 0) dl.AddImage((nint)icon, min, min + new Vector2(18f, 39f) * s,
                Vector2.Zero, new Vector2(.5625f, .609375f), 0xffffffff);
            ImGui.SetCursorScreenPos(min);
            ImGui.InvisibleButton("##keyring-button", new Vector2(18f, 39f) * s);
            if (!_settingsOpen && ImGui.IsItemClicked())
            {
                if (HasCarriedItem) PutCarriedItemInKeyring(player);
                else SetBagWindowOpen(InventoryUiLaw.KeyringContainer, !_keyringOpen);
            }
            if (_keyringOpen)
            {
                uint check = _gameplayArt.AdditiveHandle(@"Interface\Buttons\CheckButtonHilight");
                if (check != 0) dl.AddImage((nint)check, min, min + new Vector2(18f, 39f) * s);
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip(); ImGui.TextUnformatted("Keyring"); ImGui.EndTooltip();
            }
            if (!_settingsOpen) HandleKeyringDropTarget(player);
        }
        ImGui.End();
    }

    private void DrawEquippedBagWindows()
    {
        if (_net is null || _gameplayArt is null || _items is null ||
            !_entities.TryGet(_net.PlayerGuid, out WorldEntity player)) return;
        float s = GameplayUiScale();
        for (int bagIndex = 0; bagIndex < 4; bagIndex++)
        {
            if (!_equippedBagOpen[bagIndex]) continue;
            ulong guid = player.Fields.PlayerInventorySlot(19 + bagIndex);
            if (guid == 0 || !_entities.TryGet(guid, out WorldEntity bag) ||
                bag.Type is not ObjectTypeId.Container) { SetBagWindowOpen(bagIndex + 1, false); continue; }
            _items.Require(bag.Entry, bag.Guid, _net);
            _items.TryGet(bag.Entry, out ItemTemplate? template);
            int slots = (int)Math.Clamp(bag.Fields.ContainerNumSlots, 1, InventoryUiLaw.MaxContainerSlots);
            if (!_bagWindowPositions.TryGetValue(bagIndex + 1, out Vector2 frameMin)) continue;
            DrawEquippedBagWindow(frameMin, s, bagIndex, bag, template, slots);
        }
    }

    private void DrawEquippedBagWindow(Vector2 p, float s, int bagIndex, WorldEntity bag,
        ItemTemplate? bagTemplate, int slots)
    {
        InventoryUiLaw.BackgroundGeometry geometry = InventoryUiLaw.Background(slots);
        float height = geometry.Height;
        ImGui.SetNextWindowPos(p - new Vector2(64, 0) * s, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(256, height) * s, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        if (!ImGui.Begin($"##bag-window-{bagIndex}", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav))
        { ImGui.End(); return; }
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        Vector2 artMin = p - new Vector2(64, 0) * s;
        // The portrait is a BACKGROUND layer in ContainerFrame.xml. Draw it first so the
        // UI-Bag-Components ARTWORK layer masks the square icon to its circular recess.
        uint portrait = _gameplayArt!.Handle(bagTemplate?.IconPath ?? @"Interface\Buttons\Button-Backpack-Up");
        if (portrait != 0) dl.AddImage((nint)portrait, p + new Vector2(7, 5) * s, p + new Vector2(47, 45) * s);
        uint bg = _gameplayArt.Handle(@"Interface\ContainerFrame\UI-Bag-Components");
        if (bg != 0)
        {
            float topHeight = geometry.TopHeight, middleHeight = geometry.MiddleHeight;
            dl.AddImage((nint)bg, artMin, artMin + new Vector2(256, topHeight) * s,
                new Vector2(0, geometry.TopUvY.X), new Vector2(1, geometry.TopUvY.Y));
            if (middleHeight > 0) dl.AddImage((nint)bg, artMin + new Vector2(0, topHeight) * s,
                artMin + new Vector2(256, topHeight + middleHeight) * s,
                new Vector2(0, geometry.MiddleUvY.X), new Vector2(1, geometry.MiddleUvY.Y));
            dl.AddImage((nint)bg, artMin + new Vector2(0, height - 10) * s, artMin + new Vector2(256, height) * s,
                new Vector2(0, geometry.BottomUvY.X), new Vector2(1, geometry.BottomUvY.Y));
        }
        GameText.Draw(dl, "GameFontNormal", bagTemplate?.Name ?? "Bag",
            p + new Vector2(47, 10) * s, s);

        for (int slot = 0; slot < slots; slot++)
        {
            InventoryUiLaw.SlotGeometry cell = InventoryUiLaw.Slot(slots, slot, height, backpack: false);
            Vector2 min = p + new Vector2(cell.X, cell.Y) * s;
            DrawInventorySlot(dl, bag, bagIndex + 1, slot, min, s, $"bag-{bagIndex}-{slot}");
        }
        Vector2 closeMin = p + new Vector2(160, 1) * s;
        uint close = _gameplayArt.Handle(@"Interface\Buttons\UI-Panel-MinimizeButton-Up");
        if (close != 0) dl.AddImage((nint)close, closeMin, closeMin + new Vector2(32) * s);
        ImGui.SetCursorScreenPos(closeMin);
        ImGui.InvisibleButton($"##bag-close-{bagIndex}", new Vector2(32) * s);
        if (ImGui.IsItemClicked()) SetBagWindowOpen(bagIndex + 1, false);
        ImGui.End();
    }

    private bool HasCarriedItem => _carriedContainer != InventoryUiLaw.EmptyContainer;

    private void PickupOrPlaceItem(int container, int slot, ulong guid, bool ignoreModifiers = false)
    {
        if (_net is null) return;
        if (!HasCarriedItem)
        {
            if (guid != 0 && !IsInventorySlotLocked(container, slot))
            {
                _carriedContainer = container;
                _carriedSlot = slot;
                _carriedCount = null;
            }
            return;
        }
        WorldEntity? carried = ResolveCarriedItem();
        WorldEntity? target = ResolveInventoryItem(container, slot);
        InventoryUiLaw.MovePlan plan = InventoryUiLaw.PlanMove(_carriedContainer, _carriedSlot,
            container, slot, _carriedCount, carried?.Entry ?? 0, target?.Entry ?? 0);
        if (plan.Kind == InventoryUiLaw.MoveKind.Cancel) { ClearCarriedItem(); return; }
        if (plan.Kind == InventoryUiLaw.MoveKind.Refuse) return;
        bool sent;
        if (plan.Kind == InventoryUiLaw.MoveKind.Split)
            sent = _net.SplitItem(plan.Source.Bag, plan.Source.Slot, plan.Destination.Bag,
                plan.Destination.Slot, plan.Count);
        else if (plan.Kind == InventoryUiLaw.MoveKind.SwapInventory)
            sent = _net.SwapInventoryItems(plan.Source.Slot, plan.Destination.Slot);
        else
            sent = _net.SwapItems(plan.Destination.Bag, plan.Destination.Slot,
                plan.Source.Bag, plan.Source.Slot);
        if (!sent) return;
        AddPendingBagLock(_carriedContainer, _carriedSlot);
        AddPendingBagLock(container, slot);
        ClearCarriedItem();
    }

    private void ClearCarriedItem()
    {
        _carriedContainer = InventoryUiLaw.EmptyContainer;
        _carriedSlot = -1;
        _carriedCount = null;
    }

    private WorldEntity? ResolveCarriedItem()
    {
        return HasCarriedItem ? ResolveInventoryItem(_carriedContainer, _carriedSlot) : null;
    }

    private bool PlaceCarriedItemOnAction(int actionSlot)
    {
        if (!HasCarriedItem || _net is null || ResolveCarriedItem() is not { } item) return false;
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
        if (!HasCarriedItem || _items is null || _gameplayArt is null) return;
        if (ResolveCarriedItem() is not { } item ||
            !_items.TryGet(item.Entry, out ItemTemplate? template) || template is null) return;
        uint icon = _gameplayArt.Handle(template.IconPath);
        if (icon == 0) return;
        Vector2 min = ImGui.GetIO().MousePos + new Vector2(12f) * scale;
        ImGui.GetForegroundDrawList().AddImage((nint)icon, min, min + new Vector2(32f) * scale,
            Vector2.Zero, Vector2.One, 0xccffffff);
        if (_carriedCount is int count)
            GameText.DrawRightAligned(ImGui.GetForegroundDrawList(), "NumberFontNormal",
                count.ToString(), min + new Vector2(32f, 18f) * scale, scale);
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

    private void DrawInventorySlot(ImDrawListPtr dl, WorldEntity owner, int container, int slot,
        Vector2 min, float scale, string id)
    {
        if (_items is null || _gameplayArt is null || _net is null) return;
        Vector2 max = min + new Vector2(37f) * scale;
        ulong guid = ResolveSlotGuid(owner, container, slot);
        WorldEntity? instance = guid != 0 && _entities.TryGet(guid, out WorldEntity found) ? found : null;
        ItemTemplate? item = null;
        if (instance is not null)
        {
            _items.Require(instance.Entry, instance.Guid, _net);
            _items.TryGet(instance.Entry, out item);
        }
        bool locked = IsInventorySlotLocked(container, slot);
        if (item is not null)
        {
            uint icon = _gameplayArt.Handle(item.IconPath);
            if (icon != 0) dl.AddImage((nint)icon, min, max, Vector2.Zero, Vector2.One,
                locked ? 0xff666666 : 0xffffffff);
        }

        ImGui.SetCursorScreenPos(min);
        ImGui.InvisibleButton($"##{id}", max - min,
            ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
        bool hovered = ImGui.IsItemHovered();
        bool leftClicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);
        bool rightClicked = ImGui.IsItemClicked(ImGuiMouseButton.Right);
        if (_itemCastSpell != 0)
        {
            if (rightClicked)
            {
                CancelItemTargeting();
                leftClicked = rightClicked = false;
            }
            else if (leftClicked)
            {
                if (instance is not null) TryBindItemCast(instance, item, bindConfirmed: false);
                // Empty slots keep the cursor armed, and occupied slots are consumed by the
                // item-target binder even when its local gate refuses or opens a confirmation.
                leftClicked = rightClicked = false;
            }
        }
        if (_enchantConfirmation is not null) leftClicked = rightClicked = false;
        bool tradePlacement = _tradeOpen && _tradePlaceSlot >= 0 &&
            InventoryUiLaw.ToWire(container, slot) is not null;
        InventoryUiLaw.SlotClickAction click = InventoryUiLaw.ClickAction(leftClicked, rightClicked,
            ImGui.GetIO().KeyShift, HasCarriedItem, instance is not null,
            instance?.Fields.ItemStackCount ?? 0, locked, tradePlacement);
        if (click == InventoryUiLaw.SlotClickAction.TradePlace &&
            InventoryUiLaw.ToWire(container, slot) is { } trade)
            PlaceTradeItem(trade.Bag, trade.Slot);
        else if (click == InventoryUiLaw.SlotClickAction.Split)
            OpenStackSplit(container, slot, (int)(instance?.Fields.ItemStackCount ?? 0));
        else if (click == InventoryUiLaw.SlotClickAction.PickupOrPlace)
        {
            CancelStackSplit();
            PickupOrPlaceItem(container, slot, guid);
        }
        else if (click == InventoryUiLaw.SlotClickAction.ClearCarried)
        {
            CancelStackSplit();
            ClearCarriedItem();
        }
        else if (click == InventoryUiLaw.SlotClickAction.ContextAction)
        {
            CancelStackSplit();
            if (instance is not null && item is not null && InventoryUiLaw.ToWire(container, slot) is { } wire)
            {
                if (_vendor is not null) SellToOpenVendor(instance.Guid);
                else if (_bankOpen) DepositBankEntry(instance.Entry);
                else if (_mailOpen && _mailTab == 1) AttachMailItem(instance.Guid, instance.Entry);
                else if (_auctionOpen && _auctionTab == 2) _auctionSellEntry = instance.Entry;
                else if (item.InventoryType != 0) _net.AutoEquipItem(wire.Bag, wire.Slot);
                else _net.UseItem(wire.Bag, wire.Slot, item.UseSpellIndex);
            }
        }
        if (_itemCastSpell == 0 && _enchantConfirmation is null)
            HandleInventoryDrag(container, slot, guid, item);

        uint ring = _gameplayArt.Handle(@"Interface\Buttons\UI-Quickslot2");
        if (ring != 0)
        {
            Vector2 center = (min + max) * .5f + new Vector2(0, -scale), half = new(32f * scale);
            dl.AddImage((nint)ring, center - half, center + half);
        }
        if (ImGui.IsItemActive())
        {
            uint depress = _gameplayArt.Handle(@"Interface\Buttons\UI-Quickslot-Depress");
            if (depress != 0) dl.AddImage((nint)depress, min, max);
        }
        if (hovered)
        {
            uint highlight = _gameplayArt.AdditiveHandle(@"Interface\Buttons\ButtonHilight-Square");
            if (highlight != 0) dl.AddImage((nint)highlight, min, max);
        }
        if (item?.UseSpellId > 0 &&
            _actions.TryCooldownDisplay(item.UseSpellId, NowSeconds(), item.UseSpellCategory,
                out CooldownDisplay cooldown))
        {
            Vector2 cdMin = min + new Vector2(.5f, .5f) * scale;
            Vector2 cdMax = cdMin + new Vector2(36f) * scale;
            if (cooldown.SweepFraction is float sweep) DrawCooldownSwipe(dl, cdMin, cdMax, sweep);
            if (cooldown.FlashProgress is float flash) DrawCooldownFlash(dl, cdMin, cdMax, flash);
        }
        uint count = instance?.Fields.ItemStackCount ?? 0;
        if (count > 1)
            GameText.DrawRightAligned(dl, "NumberFontNormal", count.ToString(),
                new Vector2(max.X - 4f * scale,
                    max.Y - GameText.EmPixels("NumberFontNormal", scale) - 2f * scale), scale);
        if (hovered && item is not null)
        {
            Vector2 display = ImGui.GetIO().DisplaySize;
            Vector2 mouse = ImGui.GetIO().MousePos;
            ImGui.SetNextWindowPos(mouse + new Vector2(mouse.X < display.X * .5f ? 24f : -300f, 18f),
                ImGuiCond.Always);
            DrawItemTooltip(item, count, instance?.Fields.ItemDurability ?? 0,
                instance?.Fields.ItemMaxDurability ?? 0);
            string? cursor = InventoryUiLaw.HoverCursor(_vendor is not null,
                instance?.Fields.ItemTextId != 0);
            if (cursor is not null) DrawBagHoverCursor(cursor);
        }
    }

    private void HandleInventoryDrag(int container, int slot, ulong guid, ItemTemplate? item)
    {
        if (ImGui.BeginDragDropSource(ImGuiDragDropFlags.SourceNoDisableHover))
        {
            CancelStackSplit();
            if (!HasCarriedItem) PickupOrPlaceItem(container, slot, guid, ignoreModifiers: true);
            ImGui.SetDragDropPayload("MSUI_INVENTORY_ITEM", IntPtr.Zero, 0);
            ImGui.TextUnformatted(item?.Name ?? "Item");
            ImGui.EndDragDropSource();
        }
        if (ImGui.BeginDragDropTarget())
        {
            ImGui.AcceptDragDropPayload("MSUI_INVENTORY_ITEM");
            if (ImGui.IsMouseReleased(ImGuiMouseButton.Left) && HasCarriedItem)
                PickupOrPlaceItem(container, slot, guid, ignoreModifiers: true);
            ImGui.EndDragDropTarget();
        }
    }

    private void HandleBagBarDrag(int container, int equipmentSlot, ulong guid,
        ItemTemplate? item, Vector2 min, float size, float scale)
    {
        if (ImGui.BeginDragDropSource(ImGuiDragDropFlags.SourceNoDisableHover))
        {
            if (!HasCarriedItem)
                PickupOrPlaceItem(InventoryUiLaw.EquipmentContainer, equipmentSlot, guid, true);
            ImGui.SetDragDropPayload("MSUI_INVENTORY_ITEM", IntPtr.Zero, 0);
            ImGui.TextUnformatted(item?.Name ?? "Equip Container");
            ImGui.EndDragDropSource();
        }
        if (ImGui.BeginDragDropTarget())
        {
            ImGui.AcceptDragDropPayload("MSUI_INVENTORY_ITEM");
            if (ImGui.IsMouseReleased(ImGuiMouseButton.Left) && HasCarriedItem)
                PickupOrPlaceItem(InventoryUiLaw.EquipmentContainer, equipmentSlot, guid, true);
            ImGui.EndDragDropTarget();
        }
    }

    private void HandleKeyringDropTarget(WorldEntity player)
    {
        if (!ImGui.BeginDragDropTarget()) return;
        ImGui.AcceptDragDropPayload("MSUI_INVENTORY_ITEM");
        if (ImGui.IsMouseReleased(ImGuiMouseButton.Left) && HasCarriedItem)
            PutCarriedItemInKeyring(player);
        ImGui.EndDragDropTarget();
    }

    private void DrawBagHoverCursor(string stem)
    {
        if (_gameplayArt is null) return;
        uint cursor = _gameplayArt.Handle($@"Interface\Cursor\{stem}");
        if (cursor == 0) return;
        ImGui.SetMouseCursor(ImGuiMouseCursor.None);
        Vector2 min = ImGui.GetIO().MousePos;
        ImGui.GetForegroundDrawList().AddImage((nint)cursor, min, min + new Vector2(32f));
    }

    private ulong ResolveSlotGuid(WorldEntity owner, int container, int slot) => container switch
    {
        0 => owner.Fields.PlayerBackpackSlot(slot),
        InventoryUiLaw.KeyringContainer => owner.Fields.PlayerKeyringSlot(slot),
        >= 1 and <= 4 => owner.Fields.ContainerSlot(slot),
        InventoryUiLaw.EquipmentContainer => owner.Fields.PlayerInventorySlot(slot),
        _ => 0,
    };

    private WorldEntity? ResolveInventoryItem(int container, int slot)
    {
        if (_net is null || !_entities.TryGet(_net.PlayerGuid, out WorldEntity player)) return null;
        ulong guid;
        if (container == 0) guid = player.Fields.PlayerBackpackSlot(slot);
        else if (container == InventoryUiLaw.KeyringContainer) guid = player.Fields.PlayerKeyringSlot(slot);
        else if (container == InventoryUiLaw.EquipmentContainer) guid = player.Fields.PlayerInventorySlot(slot);
        else if (container is >= 1 and <= 4)
        {
            ulong bagGuid = player.Fields.PlayerInventorySlot(18 + container);
            guid = bagGuid != 0 && _entities.TryGet(bagGuid, out WorldEntity bag)
                ? bag.Fields.ContainerSlot(slot) : 0;
        }
        else guid = 0;
        return guid != 0 && _entities.TryGet(guid, out WorldEntity item) ? item : null;
    }

    private bool IsInventorySlotLocked(int container, int slot) =>
        HasCarriedItem && _carriedContainer == container && _carriedSlot == slot ||
        _pendingBagLocks.ContainsKey((container, slot));

    private void AddPendingBagLock(int container, int slot)
    {
        WorldEntity? item = ResolveInventoryItem(container, slot);
        _pendingBagLocks[(container, slot)] = new(item?.Guid ?? 0,
            item?.Fields.ItemStackCount ?? 0, NowSeconds());
    }

    private void ObserveBagLocks()
    {
        double now = NowSeconds();
        foreach (((int container, int slot) key, PendingBagLock pending) in _pendingBagLocks.ToArray())
        {
            WorldEntity? item = ResolveInventoryItem(key.container, key.slot);
            if (now - pending.SentAt > 5 || item?.Guid != pending.Guid ||
                (item?.Fields.ItemStackCount ?? 0) != pending.Count)
                _pendingBagLocks.Remove(key);
        }
    }

    private void OpenStackSplit(int container, int slot, int stackCount)
    {
        _splitContainer = container;
        _splitSlot = slot;
        _splitMaximum = Math.Max(1, stackCount - 1);
        _splitCount = Math.Clamp(stackCount / 2, 1, _splitMaximum);
    }

    private void CancelStackSplit()
    {
        _splitContainer = InventoryUiLaw.EmptyContainer;
        _splitSlot = -1;
        _splitMaximum = 0;
    }

    private void DrawStackSplit()
    {
        if (_splitContainer == InventoryUiLaw.EmptyContainer) return;
        Vector2 mouse = ImGui.GetIO().MousePos;
        ImGui.SetNextWindowPos(mouse + new Vector2(16f), ImGuiCond.Appearing);
        ImGui.SetNextWindowSize(new Vector2(190f, 92f) * GameplayUiScale(), ImGuiCond.Always);
        bool open = true;
        if (ImGui.Begin("Split Stack", ref open, ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoSavedSettings))
        {
            ImGui.SliderInt("##split-count", ref _splitCount, 1, _splitMaximum);
            if (ImGui.Button("Split"))
            {
                _carriedContainer = _splitContainer;
                _carriedSlot = _splitSlot;
                _carriedCount = _splitCount;
                CancelStackSplit();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel")) CancelStackSplit();
        }
        ImGui.End();
        if (!open) CancelStackSplit();
    }

    private string BindingText(GameBinding binding)
    {
        BindingPair keys = BoundKeys(binding);
        string[] names = new[] { keys.Primary, keys.Secondary }
            .Where(key => key != Key.Unknown).Select(key => key.ToString()).ToArray();
        return names.Length == 0 ? "Unbound" : string.Join(" / ", names);
    }

    private bool HasKey(WorldEntity player)
    {
        for (int i = 0; i < 32; i++) if (player.Fields.PlayerKeyringSlot(i) != 0) return true;
        foreach (ulong guid in EnumeratePlayerInventoryGuids(player))
        {
            if (guid == 0 || !_entities.TryGet(guid, out WorldEntity item) || _items is null) continue;
            _items.TryGet(item.Entry, out ItemTemplate? template);
            if (template?.BagFamily == 9) return true;
        }
        return false;
    }

    private IEnumerable<ulong> EnumeratePlayerInventoryGuids(WorldEntity player)
    {
        for (int i = 0; i < 23; i++) yield return player.Fields.PlayerInventorySlot(i);
        for (int i = 0; i < 16; i++) yield return player.Fields.PlayerBackpackSlot(i);
        for (int bagIndex = 0; bagIndex < 4; bagIndex++)
        {
            ulong bagGuid = player.Fields.PlayerInventorySlot(19 + bagIndex);
            if (bagGuid == 0 || !_entities.TryGet(bagGuid, out WorldEntity bag)) continue;
            for (int slot = 0; slot < bag.Fields.ContainerNumSlots; slot++) yield return bag.Fields.ContainerSlot(slot);
        }
    }

    private void PutCarriedItemInKeyring(WorldEntity player)
    {
        bool[] occupied = Enumerable.Range(0, InventoryUiLaw.KeyringAddressableSlots)
            .Select(i => player.Fields.PlayerKeyringSlot(i) != 0).ToArray();
        int slot = InventoryUiLaw.FirstEmptyKeyringSlot(player.Level, occupied);
        if (slot >= 0) { PickupOrPlaceItem(InventoryUiLaw.KeyringContainer, slot, 0, true); return; }
        ShowUiError("Your keyring is full.");
    }

    private void LayoutBagWindows(WorldEntity player)
    {
        int[] visible = [.. _bagWindowOrder.Where(IsBagWindowOpen)];
        foreach (int container in new[] { 0, 1, 2, 3, 4, InventoryUiLaw.KeyringContainer })
            if (IsBagWindowOpen(container) && !visible.Contains(container)) visible = [.. visible, container];
        _bagWindowOrder.Clear(); _bagWindowOrder.AddRange(visible);
        var windows = new List<InventoryUiLaw.StackWindow>();
        foreach (int container in visible)
        {
            float height = container switch
            {
                0 => 240f,
                InventoryUiLaw.KeyringContainer => InventoryUiLaw.Background(
                    InventoryUiLaw.KeyringSize(player.Level)).Height,
                _ => EquippedBagHeight(player, container),
            };
            if (height > 0) windows.Add(new(container, height));
        }
        float scale = GameplayUiScale();
        Vector2 display = ImGui.GetIO().DisplaySize;
        _bagWindowPositions.Clear();
        foreach (InventoryUiLaw.StackPlacement placement in
                 InventoryUiLaw.LayoutStack(display.Y / scale, windows))
            _bagWindowPositions[placement.Container] = new(
                display.X - (placement.RightOffset + InventoryUiLaw.ContainerWidth) * scale,
                display.Y - (placement.BottomOffset + placement.Height) * scale);
    }

    private float EquippedBagHeight(WorldEntity player, int container)
    {
        ulong guid = container is >= 1 and <= 4 ? player.Fields.PlayerInventorySlot(18 + container) : 0;
        if (guid == 0 || !_entities.TryGet(guid, out WorldEntity bag)) return 0;
        return InventoryUiLaw.Background((int)Math.Clamp(bag.Fields.ContainerNumSlots, 1,
            InventoryUiLaw.MaxContainerSlots)).Height;
    }

    private bool IsBagWindowOpen(int container) => container switch
    {
        0 => _backpackOpen,
        InventoryUiLaw.KeyringContainer => _keyringOpen,
        >= 1 and <= 4 => _equippedBagOpen[container - 1],
        _ => false,
    };

    private bool SetBagWindowOpen(int container, bool open, bool playSound = true)
    {
        bool was = IsBagWindowOpen(container);
        if (was == open) return false;
        switch (container)
        {
            case 0: _backpackOpen = open; break;
            case InventoryUiLaw.KeyringContainer: _keyringOpen = open; break;
            case >= 1 and <= 4: _equippedBagOpen[container - 1] = open; break;
            default: return false;
        }
        _bagWindowOrder.Remove(container);
        if (open) _bagWindowOrder.Add(container);
        if (playSound)
            PlayBagSound(container == InventoryUiLaw.KeyringContainer
                ? open ? "KeyRingOpen" : "KeyRingClose"
                : open ? "igBackPackOpen" : "igBackPackClose");
        return true;
    }

    private void PlayBagSound(string name)
        => PlayUiSound(name);

    private void PlayUiSound(string name)
    {
        Vector3 listener = _controller?.Position ?? Vector3.Zero;
        _spellSounds?.Play(name, _net?.PlayerGuid ?? 0, listener, listener);
    }

    private void ToggleBackpack() => SetBagWindowOpen(0, !_backpackOpen);

    private bool SetAllNormalBagWindows(WorldEntity player, bool open)
    {
        bool changed = SetBagWindowOpen(0, open, playSound: false);
        for (int container = 1; container <= 4; container++)
        {
            bool exists = player.Fields.PlayerInventorySlot(18 + container) != 0;
            if (open && !exists) continue;
            changed |= SetBagWindowOpen(container, open, playSound: false);
        }
        if (changed) PlayBagSound(open ? "igBackPackOpen" : "igBackPackClose");
        return changed;
    }

    private bool ToggleAllBags()
    {
        if (_net is null || !_entities.TryGet(_net.PlayerGuid, out WorldEntity player)) return false;
        bool open = InventoryUiLaw.ShouldOpenAllBags(_backpackOpen, _equippedBagOpen);
        return SetAllNormalBagWindows(player, open);
    }

    private bool CloseAllBagWindows()
    {
        if (!_backpackOpen && !_keyringOpen && !_equippedBagOpen.Any(x => x)) return false;
        if (_net is not null && _entities.TryGet(_net.PlayerGuid, out WorldEntity player))
            SetAllNormalBagWindows(player, false);
        else
        {
            SetBagWindowOpen(0, false, playSound: false);
            for (int container = 1; container <= 4; container++)
                SetBagWindowOpen(container, false, playSound: false);
        }
        SetBagWindowOpen(InventoryUiLaw.KeyringContainer, false);
        return true;
    }

    private void TriggerItemPushAnimation(byte wireBag, uint wireSlot, uint entry)
    {
        int container = InventoryUiLaw.PushContainer(wireBag, wireSlot);
        if (container is not (0 or 1 or 2 or 3 or 4 or InventoryUiLaw.KeyringContainer)) return;
        _itemPushContainer = container;
        _itemPushEntry = entry;
        _itemPushStartedAt = NowSeconds();
    }

    private void DrawItemPushAnimation()
    {
        if (_itemPushContainer == InventoryUiLaw.EmptyContainer || _gameplayArt is null || _items is null ||
            !_bagButtonPositions.TryGetValue(_itemPushContainer, out Vector2 buttonMin)) return;
        float elapsed = (float)(NowSeconds() - _itemPushStartedAt);
        InventoryUiLaw.ItemPushSample sample = InventoryUiLaw.SampleItemPush(elapsed);
        if (!sample.Visible) { _itemPushContainer = InventoryUiLaw.EmptyContainer; return; }
        if (!_items.TryGet(_itemPushEntry, out ItemTemplate? item) || item is null) return;
        uint icon = _gameplayArt.Handle(item.IconPath);
        if (icon == 0) return;
        float scale = GameplayUiScale();
        Vector2 center = buttonMin + new Vector2(18f) * scale + sample.Offset * scale;
        Vector2 half = new(sample.Size * .5f * scale);
        uint alpha = (uint)Math.Clamp((int)(sample.Alpha * 255f), 0, 255);
        uint tint = (alpha << 24) | 0x00ffffff;
        ImGui.GetForegroundDrawList().AddImage((nint)icon, center - half, center + half,
            Vector2.Zero, Vector2.One, tint);
    }

    private void DrawKeyringWindow(WorldEntity player)
    {
        if (!_keyringOpen || _gameplayArt is null || _items is null || _net is null ||
            !_bagWindowPositions.TryGetValue(InventoryUiLaw.KeyringContainer, out Vector2 p)) return;
        float s = GameplayUiScale();
        int slots = InventoryUiLaw.KeyringSize(player.Level);
        InventoryUiLaw.BackgroundGeometry geometry = InventoryUiLaw.Background(slots);
        float height = geometry.Height;
        Vector2 artMin = p - new Vector2(64f, 0) * s;
        ImGui.SetNextWindowPos(artMin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(256f, height) * s, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        if (!ImGui.Begin("##keyring-window", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav))
        { ImGui.End(); return; }
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        // As with equipped bags, the keyring portrait sits below the frame artwork so its
        // square source texture is clipped by the authored circular opening.
        uint portrait = _gameplayArt.Handle(@"Interface\ContainerFrame\KeyRing-Bag-Icon");
        if (portrait != 0) dl.AddImage((nint)portrait, p + new Vector2(7, 5) * s,
            p + new Vector2(47, 45) * s);
        uint bg = _gameplayArt.Handle(@"Interface\ContainerFrame\UI-Bag-Components-Keyring");
        if (bg != 0)
        {
            dl.AddImage((nint)bg, artMin, artMin + new Vector2(256f, geometry.TopHeight) * s,
                new Vector2(0, geometry.TopUvY.X), new Vector2(1, geometry.TopUvY.Y));
            if (geometry.MiddleHeight > 0)
                dl.AddImage((nint)bg, artMin + new Vector2(0, geometry.TopHeight) * s,
                    artMin + new Vector2(256f, geometry.TopHeight + geometry.MiddleHeight) * s,
                    new Vector2(0, geometry.MiddleUvY.X), new Vector2(1, geometry.MiddleUvY.Y));
            dl.AddImage((nint)bg, artMin + new Vector2(0, height - 10f) * s,
                artMin + new Vector2(256f, height) * s,
                new Vector2(0, geometry.BottomUvY.X), new Vector2(1, geometry.BottomUvY.Y));
        }
        GameText.Draw(dl, "GameFontNormal", "Keyring", p + new Vector2(47, 10) * s, s);
        for (int slot = 0; slot < slots; slot++)
        {
            InventoryUiLaw.SlotGeometry cell = InventoryUiLaw.Slot(slots, slot, height, false);
            DrawInventorySlot(dl, player, InventoryUiLaw.KeyringContainer, slot,
                p + new Vector2(cell.X, cell.Y) * s, s, $"keyring-{slot}");
        }
        Vector2 closeMin = p + new Vector2(160, 1) * s;
        uint close = _gameplayArt.Handle(@"Interface\Buttons\UI-Panel-MinimizeButton-Up");
        if (close != 0) dl.AddImage((nint)close, closeMin, closeMin + new Vector2(32) * s);
        ImGui.SetCursorScreenPos(closeMin); ImGui.InvisibleButton("##keyring-close", new Vector2(32) * s);
        if (ImGui.IsItemClicked()) SetBagWindowOpen(InventoryUiLaw.KeyringContainer, false);
        ImGui.End();
    }

    private void DrawMoney(ImDrawListPtr dl, Vector2 frameMin, uint copper, float scale)
    {
        uint icons = _gameplayArt?.Handle(@"Interface\MoneyFrame\UI-MoneyIcons.blp") ?? 0;
        float right = frameMin.X + 177f * scale;
        float top = frameMin.Y + 216f * scale;
        foreach (InventoryUiLaw.MoneyDenomination denomination in InventoryUiLaw.Money(copper))
        {
            string text = denomination.Value.ToString();
            float numberWidth = GameText.MeasureWidth("NumberFontNormal", text, scale);
            Vector2 iconMin = new(right - 13f * scale, top);
            if (icons != 0)
            {
                float uvLeft = denomination.Index * .25f;
                dl.AddImage((nint)icons, iconMin, iconMin + new Vector2(13f) * scale,
                    new Vector2(uvLeft, 0), new Vector2(uvLeft + .25f, 1));
            }
            float textTop = GameText.BoxCenteredTop("NumberFontNormal", top, 13f, scale);
            GameText.DrawRightAligned(dl, "NumberFontNormal", text,
                new Vector2(iconMin.X, textTop), scale);
            right = iconMin.X - numberWidth - 4f * scale;
        }
    }
}
