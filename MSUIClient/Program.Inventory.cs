using System.Collections.Immutable;
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
    private bool _shoppingTooltipParityCompletionPending;
    private bool _shoppingTooltipParityRendererCollected;
    private ImmutableArray<ShoppingTooltipParityExpectation>
        _shoppingTooltipParityExpectations = [];
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
            {
                // Require-then-lookup: the renderer no longer depends on DiscoverItemTemplates
                // having walked this entity first (ask-once cache, so re-asking is free).
                if (_items is not null && _net is not null) _items.Require(entry, 0, _net);
                return (_items!.TryGet(entry, out ItemTemplate? item), item);
            };
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
            bool shift = InputKeyDown(Key.ShiftLeft) || InputKeyDown(Key.ShiftRight);
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
            !_entities.TryGet(ControlledGuid, out WorldEntity player)) return;
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
        if (_net is null || !_entities.TryGet(ControlledGuid, out WorldEntity player)) return false;
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
        if (_net is null || slot is < 0 or >= 19 || !_entities.TryGet(ControlledGuid, out WorldEntity player)) return false;
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
        if (_net is null || _items is null || !_entities.TryGet(ControlledGuid, out WorldEntity player)) return false;
        ObjectFields f = player.Fields;
        EmitInterface("character", "stats", "WIRE-SNAPSHOT", ControlledGuid,
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
        EmitInterface("inventory", "snapshot", "COMPLETE", ControlledGuid,
            $"equipped={equipped};backpack={backpack};resolved={resolved}");
        return true;
    }

    private void SyncLiveEquipmentModel()
    {
        if (_net is null || _items is null || _character is not { Loaded: true } ||
            !_entities.TryGet(ControlledGuid, out WorldEntity player)) return;
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
        if (_net is null || !_entities.TryGet(ControlledGuid, out WorldEntity player)) return;
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
            !_entities.TryGet(ControlledGuid, out WorldEntity player)) return;

        float scale = GameplayUiScale();
        Vector2 frameSize = new(192f, 240f);
        if (!_bagWindowPositions.TryGetValue(0, out Vector2 frameMin)) return;
        Vector2 windowMin = frameMin - new Vector2(64f, 0) * scale;

        if (_uiParityArmed && _uiParityPanel == "backpack")
        {
            BeginUiParityFrame(frameMin, scale);
            CollectUiParityDraw("ContainerFrame1", "Frame", frameMin, frameSize * scale, "",
                new("", 0, "IMGUI_HOST", "BOTTOMRIGHT", "UIParent", "BOTTOMRIGHT", 0, 70));
            CollectUiParityDraw("ContainerFrame1Portrait", "Texture", frameMin + new Vector2(7, 5) * scale,
                new Vector2(40) * scale, "ContainerFrame1",
                new(@"Interface\Buttons\Button-Backpack-Up", 0xffffffff, "BACKGROUND", "TOPLEFT",
                    "ContainerFrame1", "TOPLEFT", 7, -5));
            CollectUiParityDraw("ContainerFrame1BackgroundTop", "Texture", windowMin,
                new Vector2(256) * scale, "ContainerFrame1",
                new(@"Interface\ContainerFrame\UI-BackpackBackground", 0xffffffff, "ARTWORK", "TOPRIGHT",
                    "ContainerFrame1", "TOPRIGHT", 0, 0));
            CollectUiParityDraw("ContainerFrame1Name", "FontString", frameMin + new Vector2(47, 10) * scale,
                new Vector2(112, 12) * scale, "ContainerFrame1",
                new("", 0xffffffff, "ARTWORK", "TOPLEFT", "ContainerFrame1", "TOPLEFT", 47, -10,
                    @"Fonts\FRIZQT__.TTF", 12));
            CollectUiParityDraw("ContainerFrame1CloseButton", "Button", frameMin + new Vector2(160, 1) * scale,
                new Vector2(32) * scale, "ContainerFrame1",
                new("", 0, "IMGUI_HIT_TARGET", "TOPRIGHT", "ContainerFrame1", "TOPRIGHT", 0, -1,
                    Enabled: true, InteractionState: "normal",
                    HitMin: frameMin + new Vector2(160, 1) * scale,
                    HitMax: frameMin + new Vector2(192, 33) * scale));
            CollectUiParityDraw("ContainerFrame1CloseButton/NormalTexture", "NormalTexture",
                frameMin + new Vector2(160, 1) * scale, new Vector2(32) * scale, "ContainerFrame1CloseButton",
                new(@"Interface\Buttons\UI-Panel-MinimizeButton-Up", 0xffffffff, "ARTWORK", "CENTER",
                    "ContainerFrame1CloseButton", "CENTER", 0, 0));
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
            DrawInventorySlot(dl, player, 0, gameSlot, slotMin, scale, $"pack-{gameSlot}");
        }

        DrawMoney(dl, frameMin, player.Fields.Coinage, scale);
        if (_uiParityArmed && _uiParityPanel == "backpack") MarkUiParityFrameComplete();
        ImGui.End();

    }

    private void DrawBagBar()
    {
        if (_net is not { IsInWorld: true } || _gameplayArt is null || _items is null ||
            !_entities.TryGet(ControlledGuid, out WorldEntity player)) return;
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
        bool parityProof = _uiParityArmed && _uiParityPanel == "bag-bar";
        const string parityRoot = "MainMenuBarBagButtons";
        if (parityProof)
        {
            BeginUiParityFrame(windowMin, s);
            CollectUiParityDraw(parityRoot, "Frame", windowMin, new Vector2(windowWidth, 53) * s, "",
                new("", 0, "IMGUI_HOST", "BOTTOMRIGHT", "MainMenuBar", "BOTTOMRIGHT",
                    hasKey ? -250 : -226, 0));
        }
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
            bool locked = container != 0 && IsInventorySlotLocked(InventoryUiLaw.EquipmentContainer, equipmentSlot);
            bool menuDisabled = _settingsOpen && InventoryUiLaw.DisableWithGameMenu(container);
            uint tint = menuDisabled || locked ? 0xff666666 : 0xffffffff;
            uint icon = container == 0 ? _gameplayArt.Handle(art) : _gameplayArt.CircularHandle(art);
            string parityButton = container == 0 ? "MainMenuBarBackpackButton" : $"CharacterBag{container - 1}Slot";
            bool checkedState = container == 0 ? _backpackOpen : _equippedBagOpen[container - 1];
            if (parityProof)
            {
                CollectUiParityDraw(parityButton, "CheckButton", min, new Vector2(buttonSize) * s,
                    parityRoot, new("", 0, "IMGUI_HIT_TARGET", "ABSOLUTE", parityRoot, "TOPLEFT",
                        (min.X - windowMin.X) / s, -((min.Y - windowMin.Y) / s),
                        Enabled: !menuDisabled && !locked,
                        InteractionState: menuDisabled || locked ? "disabled" : checkedState ? "checked" : "normal",
                        HitMin: min, HitMax: min + new Vector2(buttonSize) * s));
                CollectUiParityDraw(parityButton + "IconTexture",
                    container == 0 ? "Texture" : "MaskedTexture", min, new Vector2(buttonSize) * s,
                    parityButton, new(art, tint, "BACKGROUND", "CENTER", parityButton, "CENTER", 0, 0,
                        TexCoords: "0|0|1|1",
                        ClipRect: new Vector4(min.X, min.Y, min.X + buttonSize * s, min.Y + buttonSize * s),
                        ClipMask: container == 0 ? "" : "ALPHA_CIRCLE_INSCRIBED",
                        BlendMode: "BLEND", Visible: icon != 0));
            }
            BagIconContainmentLaw.Geometry barProof = BagIconContainmentLaw.BagBar;
            bool drawDynamicIcon = container == 0 || BagContainmentDrawIcon(parityButton + "IconTexture",
                min, new Vector2(buttonSize) * s,
                min - new Vector2(barProof.ApertureOffset) * s,
                new Vector2(barProof.CaptureSize) * s);
            if (icon != 0 && drawDynamicIcon) dl.AddImage((nint)icon, min, min + new Vector2(buttonSize) * s,
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
                if (parityProof)
                    CollectUiParityDraw(parityButton + "NormalTexture", "NormalTexture",
                        center - half, half * 2, parityButton,
                        new(@"Interface\Buttons\UI-Quickslot2", 0xffffffff, "ARTWORK", "CENTER",
                            parityButton, "CENTER", 0, 0, TexCoords: "0|0|1|1",
                            BlendMode: "BLEND", Visible: ring != 0));
            }
            if (checkedState)
            {
                uint check = _gameplayArt.AdditiveHandle(@"Interface\Buttons\CheckButtonHilight");
                if (check != 0) dl.AddImage((nint)check, min, min + new Vector2(buttonSize) * s);
                if (parityProof)
                    CollectUiParityDraw(parityButton + "CheckedTexture", "CheckedTexture", min,
                        new Vector2(buttonSize) * s, parityButton,
                        new(@"Interface\Buttons\CheckButtonHilight", 0xffffffff, "OVERLAY", "CENTER",
                            parityButton, "CENTER", 0, 0));
            }
            if (hovered)
            {
                uint highlight = _gameplayArt.AdditiveHandle(@"Interface\Buttons\ButtonHilight-Square");
                if (highlight != 0) dl.AddImage((nint)highlight, min, min + new Vector2(buttonSize) * s);
                if (parityProof)
                    CollectUiParityDraw(parityButton + "HighlightTexture", "HighlightTexture", min,
                        new Vector2(buttonSize) * s, parityButton,
                        new(@"Interface\Buttons\ButtonHilight-Square", 0xffffffff, "OVERLAY", "CENTER",
                            parityButton, "CENTER", 0, 0));
                string tooltipText = container == 0
                    ? $"Backpack ({BindingText(GameBinding.OpenBackpack)})"
                    : bagTemplate?.Name ?? "Equip Container";
                GameTooltipOwnerKey tooltipOwner = container == 0
                    ? new("bag-button", 0)
                    : new("item:inventory-bag-bar", (ulong)container);
                OfferPreservedSharedGameTooltipRenderer(tooltipOwner, () =>
                {
                    ImGui.BeginTooltip();
                    ImGui.TextUnformatted(tooltipText);
                    ImGui.EndTooltip();
                });
            }
        }
        if (hasKey)
        {
            Vector2 min = firstBagMin - new Vector2(24f, 1.5f) * s;
            _bagButtonPositions[InventoryUiLaw.KeyringContainer] = min;
            if (parityProof)
            {
                CollectUiParityDraw("KeyRingButton", "CheckButton", min, new Vector2(18f, 39f) * s,
                    parityRoot, new("", 0, "IMGUI_HIT_TARGET", "RIGHT", "CharacterBag3Slot", "LEFT", -6, 0,
                        Enabled: !_settingsOpen,
                        InteractionState: _settingsOpen ? "disabled" : _keyringOpen ? "checked" : "normal",
                        HitMin: min, HitMax: min + new Vector2(18f, 39f) * s));
                CollectUiParityDraw("KeyRingButtonNormalTexture", "NormalTexture", min,
                    new Vector2(18f, 39f) * s, "KeyRingButton",
                    new(@"Interface\Buttons\UI-Button-KeyRing", 0xffffffff, "ARTWORK", "CENTER",
                        "KeyRingButton", "CENTER", 0, 0,
                        TexCoords: "0|0|0.5625|0.609375", BlendMode: "BLEND"));
            }
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
                OfferPreservedSharedGameTooltipRenderer(new("keyring-button", 0), () =>
                {
                    ImGui.BeginTooltip();
                    ImGui.TextUnformatted("Keyring");
                    ImGui.EndTooltip();
                });
            }
            if (!_settingsOpen) HandleKeyringDropTarget(player);
        }
        if (parityProof) MarkUiParityFrameComplete();
        ImGui.End();
    }

    private void DrawEquippedBagWindows()
    {
        if (_net is null || _gameplayArt is null || _items is null ||
            !_entities.TryGet(ControlledGuid, out WorldEntity player)) return;
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
        Vector2 portraitMin = p + new Vector2(7, 5) * s;
        bool parityProof = _uiParityArmed && _uiParityPanel == "equipped-bag" &&
            bagIndex + 1 == _uiParityEquippedBagContainer;
        string parityRoot = $"ContainerFrameBag{bagIndex + 1}";
        if (parityProof)
        {
            BeginUiParityFrame(p, s);
            CollectUiParityDraw(parityRoot, "Frame", p, new Vector2(192, height) * s, "",
                new("", 0, "IMGUI_HOST", "BOTTOMRIGHT", "UIParent", "BOTTOMRIGHT", 0, 70));
            CollectUiParityDraw(parityRoot + "Portrait", "MaskedTexture", portraitMin,
                new Vector2(40) * s, parityRoot,
                new(bagTemplate?.IconPath ?? @"Interface\Buttons\Button-Backpack-Up", 0xffffffff,
                    "BACKGROUND", "TOPLEFT", parityRoot, "TOPLEFT", 7, -5,
                    TexCoords: "0|0|1|1",
                    ClipRect: new Vector4(portraitMin.X, portraitMin.Y,
                        portraitMin.X + 40 * s, portraitMin.Y + 40 * s),
                    ClipMask: "ALPHA_CIRCLE_INSCRIBED", BlendMode: "BLEND"));
            CollectUiParityDraw(parityRoot + "BackgroundTop", "TextureUv", artMin,
                new Vector2(256, geometry.TopHeight) * s, parityRoot,
                new(@"Interface\ContainerFrame\UI-Bag-Components", 0xffffffff, "ARTWORK",
                    "TOPRIGHT", parityRoot, "TOPRIGHT", 0, 0,
                    TexCoords: $"0|{geometry.TopUvY.X:R}|1|{geometry.TopUvY.Y:R}", BlendMode: "BLEND"));
            if (geometry.MiddleHeight > 0)
                CollectUiParityDraw(parityRoot + "BackgroundMiddle", "TextureUv",
                    artMin + new Vector2(0, geometry.TopHeight) * s,
                    new Vector2(256, geometry.MiddleHeight) * s, parityRoot,
                    new(@"Interface\ContainerFrame\UI-Bag-Components", 0xffffffff, "ARTWORK",
                        "TOPRIGHT", parityRoot + "BackgroundTop", "BOTTOMRIGHT", 0, 0,
                        TexCoords: $"0|{geometry.MiddleUvY.X:R}|1|{geometry.MiddleUvY.Y:R}", BlendMode: "BLEND"));
            CollectUiParityDraw(parityRoot + "BackgroundBottom", "TextureUv",
                artMin + new Vector2(0, height - 10) * s, new Vector2(256, 10) * s, parityRoot,
                new(@"Interface\ContainerFrame\UI-Bag-Components", 0xffffffff, "ARTWORK",
                    "TOPRIGHT", geometry.MiddleHeight > 0 ? parityRoot + "BackgroundMiddle" : parityRoot + "BackgroundTop",
                    "BOTTOMRIGHT", 0, 0,
                    TexCoords: $"0|{geometry.BottomUvY.X:R}|1|{geometry.BottomUvY.Y:R}", BlendMode: "BLEND"));
            CollectUiParityDraw(parityRoot + "Name", "FontString", p + new Vector2(47, 10) * s,
                new Vector2(112, 12) * s, parityRoot,
                new("", 0xffffffff, "ARTWORK", "TOPLEFT", parityRoot, "TOPLEFT", 47, -10,
                    @"Fonts\FRIZQT__.TTF", 12));
        }
        // The portrait is a BACKGROUND layer in ContainerFrame.xml. Its derived handle is alpha
        // masked because the ring has transparent corners and cannot contain a square icon alone;
        // the UI-Bag-Components ARTWORK still draws over it in the authored layer order.
        uint portrait = _gameplayArt!.CircularHandle(bagTemplate?.IconPath ?? @"Interface\Buttons\Button-Backpack-Up");
        BagIconContainmentLaw.Geometry portraitProof = BagIconContainmentLaw.HeaderPortrait;
        bool drawPortrait = BagContainmentDrawIcon(parityRoot + "Portrait", portraitMin,
            new Vector2(portraitProof.ApertureSize) * s,
            portraitMin - new Vector2(portraitProof.ApertureOffset) * s,
            new Vector2(portraitProof.CaptureSize) * s);
        if (portrait != 0 && drawPortrait)
            dl.AddImage((nint)portrait, portraitMin, portraitMin + new Vector2(40) * s);
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
        if (parityProof)
        {
            CollectUiParityDraw(parityRoot + "CloseButton", "Button", closeMin, new Vector2(32) * s,
                parityRoot, new("", 0, "IMGUI_HIT_TARGET", "TOPRIGHT", parityRoot, "TOPRIGHT", 0, -1,
                    Enabled: true, InteractionState: "normal", HitMin: closeMin,
                    HitMax: closeMin + new Vector2(32) * s));
            CollectUiParityDraw(parityRoot + "CloseButton/NormalTexture", "NormalTexture", closeMin,
                new Vector2(32) * s, parityRoot + "CloseButton",
                new(@"Interface\Buttons\UI-Panel-MinimizeButton-Up", 0xffffffff, "ARTWORK", "CENTER",
                    parityRoot + "CloseButton", "CENTER", 0, 0));
        }
        uint close = _gameplayArt.Handle(@"Interface\Buttons\UI-Panel-MinimizeButton-Up");
        if (close != 0) dl.AddImage((nint)close, closeMin, closeMin + new Vector2(32) * s);
        ImGui.SetCursorScreenPos(closeMin);
        ImGui.InvisibleButton($"##bag-close-{bagIndex}", new Vector2(32) * s);
        if (ImGui.IsItemClicked()) SetBagWindowOpen(bagIndex + 1, false);
        if (parityProof) MarkUiParityFrameComplete();
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

    private void ClearCarriedItemOnEscape()
    {
        if (HasCarriedItem) ClearCarriedItem();
        ClearActionBarCursorOnEscape();
    }

    private WorldEntity? ResolveCarriedItem()
    {
        return HasCarriedItem ? ResolveInventoryItem(_carriedContainer, _carriedSlot) : null;
    }

    private bool PlaceCarriedItemOnAction(int actionSlot)
    {
        if (!HasCarriedItem) return false;
        // A held cursor payload owns the click even when the destination refuses it. Falling
        // through to UseAction would cast/use the action underneath a silently refused item.
        if (_net is null || ResolveCarriedItem() is not { } item) return true;
        _items?.Require(item.Entry, item.Guid, _net);
        if (_items?.TryGet(item.Entry, out ItemTemplate? template) != true || template is null)
            return true;
        if (!MultiActionBarUiLaw.ItemMayBePlaced(template.InventoryType, template.UseSpellId))
            return true;

        var action = new ActionSlot(ActionSlot.Item, item.Entry);
        PlaceActionPayload(actionSlot, action);
        ClearCarriedItem();
        return true;
    }

    private void UseItemAction(uint entry)
    {
        if (_net is null || _items is null ||
            !_entities.TryGet(ControlledGuid, out WorldEntity player)) return;

        List<(byte Bag, byte Slot, WorldEntity Item, bool Worn)> all =
            EnumerateActionItemCopies(player, entry).ToList();
        if (all.Count == 0) return;
        WorldEntity exemplar = all[0].Item;
        _items.Require(entry, exemplar.Guid, _net);
        if (!_items.TryGet(entry, out ItemTemplate? template) || template is null) return;

        bool chargeFilter = template.InventoryType == 0 &&
            MultiActionBarUiLaw.RequiresLiveCharges(template.SpellCharges0);
        (byte Bag, byte Slot, WorldEntity Item, bool Worn)? any = null;
        foreach (var candidate in all)
        {
            if (chargeFilter && !MultiActionBarUiLaw.LiveChargeCandidate(
                    candidate.Item.Fields.ContainerNumSlots > 0,
                    candidate.Item.Fields.ItemSpellCharges(0))) continue;
            any = candidate;
            break;
        }
        (byte Bag, byte Slot, WorldEntity Item, bool Worn)? equipped =
            all.FirstOrDefault(candidate => candidate.Worn) is { Worn: true } worn
                ? worn : null;
        MultiActionItemRoute route = MultiActionBarUiLaw.ItemUseRoute(template.InventoryType,
            equipped is not null, any is not null);
        if (route == MultiActionItemRoute.Use)
        {
            (byte Bag, byte Slot, WorldEntity Item, bool Worn) at = template.InventoryType != 0
                ? equipped!.Value : any!.Value;
            uint activeIconId = _spellCatalog?.TryGet(template.UseSpellId, out SpellInfo spell) == true
                ? spell.ActiveIconId : 0;
            bool matchingCancelableAura = player.Fields.Auras().Any(aura =>
                aura.SpellId == template.UseSpellId && (aura.Flags & 0x1) != 0);
            switch (MultiActionBarUiLaw.ItemUseDisposition(template.StartQuest,
                        template.UseSpellId, activeIconId, matchingCancelableAura))
            {
                case MultiActionItemUseDisposition.QuestOffer:
                    _net.QuestgiverQuery(at.Item.Guid, template.StartQuest);
                    break;
                case MultiActionItemUseDisposition.ToggleCancel:
                    _net.CancelAura(template.UseSpellId);
                    break;
                case MultiActionItemUseDisposition.Use:
                    _net.UseItem(at.Bag, at.Slot, template.UseSpellIndex);
                    break;
            }
        }
        else if (route == MultiActionItemRoute.Equip && any is { } at)
            _net.AutoEquipItem(at.Bag, at.Slot);
    }

    /// <summary>
    /// The reference's mode-0x47 inventory walk. Order is observable when duplicate copies have
    /// different remaining charges, and its wire bag bytes are not UI container ids.
    /// </summary>
    private IEnumerable<(byte Bag, byte Slot, WorldEntity Item, bool Worn)>
        EnumerateActionItemCopies(WorldEntity player, uint entry)
    {
        (WorldEntity Item, bool Hit) Resolve(ulong guid) =>
            guid != 0 && _entities.TryGet(guid, out WorldEntity item) && item.Entry == entry
                ? (item, true) : (null!, false);

        for (int slot = 0; slot < 19; slot++)
            if (Resolve(player.Fields.PlayerInventorySlot(slot)) is { Hit: true } worn)
                yield return (255, (byte)slot, worn.Item, true);
        for (int bagIndex = 0; bagIndex < 4; bagIndex++)
        {
            byte bagSlot = (byte)(19 + bagIndex);
            ulong bagGuid = player.Fields.PlayerInventorySlot(bagSlot);
            if (Resolve(bagGuid) is { Hit: true } bagObject)
                yield return (255, bagSlot, bagObject.Item, false);
            if (bagGuid == 0 || !_entities.TryGet(bagGuid, out WorldEntity bag)) continue;
            int slots = (int)Math.Min(bag.Fields.ContainerNumSlots, 36);
            for (int slot = 0; slot < slots; slot++)
                if (Resolve(bag.Fields.ContainerSlot(slot)) is { Hit: true } content)
                    yield return (bagSlot, (byte)slot, content.Item, false);
        }
        for (int i = 0; i < 16; i++)
            if (Resolve(player.Fields.PlayerBackpackSlot(i)) is { Hit: true } backpack)
                yield return (255, (byte)(23 + i), backpack.Item, false);
        for (int i = 0; i < 16; i++)
            if (Resolve(player.Fields.PlayerKeyringSlot(i)) is { Hit: true } key)
                yield return (255, (byte)(81 + i), key.Item, false);
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

    private static Vector4 ItemTooltipQualityColor(uint quality) => quality switch
    {
        0 => new Vector4(0.62f, 0.62f, 0.62f, 1), 2 => new Vector4(0.12f, 1f, 0, 1),
        3 => new Vector4(0, 0.44f, 0.87f, 1), 4 => new Vector4(0.64f, 0.21f, 0.93f, 1),
        5 => new Vector4(1f, 0.50f, 0, 1), 6 => new Vector4(0.90f, 0.80f, 0.50f, 1),
        _ => Vector4.One,
    };

    private enum PreparedItemTooltipPaintKind
    {
        Plain,
        Disabled,
        Colored,
        Separator,
    }

    private readonly record struct PreparedItemTooltipPaintOp(
        PreparedItemTooltipPaintKind Kind,
        string Text,
        Vector4 Color);

    private readonly record struct ItemTooltipBodySnapshot(
        ImmutableArray<PreparedItemTooltipPaintOp> Operations);

    private readonly record struct PreparedPaperDollComparisonTooltip(
        int TooltipNumber,
        int EquipmentSlot,
        ItemTooltipBodySnapshot Body,
        Vector2 WindowPosition,
        Vector2 WindowPivot,
        float Scale,
        string AnchorPoint,
        string RelativePoint,
        string ParentElement,
        bool CaptureParity);

    private readonly record struct ShoppingTooltipParityExpectation(
        int TooltipNumber,
        string ParentElement);

    private static GameTooltipOwnerKey InventoryItemGameTooltipOwner(
        int container,
        int physicalButton)
    {
        if (physicalButton <= 0)
            throw new ArgumentOutOfRangeException(nameof(physicalButton));
        return new($"item:inventory-container:{container}", (ulong)physicalButton);
    }

    private static int HighestLiveComparisonOrdinal(IEnumerable<int> tooltipNumbers)
        => tooltipNumbers.DefaultIfEmpty(0).Max();

    private static PreparedItemTooltipPaintOp PreparedItemTooltipPlain(string text)
        => new(PreparedItemTooltipPaintKind.Plain, text, default);

    private static PreparedItemTooltipPaintOp PreparedItemTooltipDisabled(string text)
        => new(PreparedItemTooltipPaintKind.Disabled, text, default);

    private static PreparedItemTooltipPaintOp PreparedItemTooltipColored(
        string text,
        Vector4 color)
        => new(PreparedItemTooltipPaintKind.Colored, text, color);

    private static PreparedItemTooltipPaintOp PreparedItemTooltipSeparator()
        => new(PreparedItemTooltipPaintKind.Separator, "", default);

    private static ItemTooltipBodySnapshot AppendPreparedItemTooltipBody(
        in ItemTooltipBodySnapshot body,
        params PreparedItemTooltipPaintOp[] tail)
    {
        if (body.Operations.IsDefault)
            throw new ArgumentException("The prepared item tooltip body is uninitialized.",
                nameof(body));
        ArgumentNullException.ThrowIfNull(tail);
        return new(body.Operations.AddRange(tail));
    }

    private static ItemTooltipBodySnapshot PrepareItemTooltipBodySnapshot(
        ItemTemplate item,
        uint count,
        uint durability = 0,
        uint maxDurability = 0,
        bool compact = false)
    {
        ArgumentNullException.ThrowIfNull(item);
        var operations = ImmutableArray.CreateBuilder<PreparedItemTooltipPaintOp>();

        // Resolve every mutable ItemTemplate field, Stats entry, and Damages entry into an
        // immutable paint operation before the terminal tooltip stratum can invoke a renderer.
        operations.Add(PreparedItemTooltipColored(item.Name,
            compact ? Vector4.One : ItemTooltipQualityColor(item.Quality)));
        if (item.Bonding == 1)
            operations.Add(PreparedItemTooltipPlain("Binds when picked up"));
        else if (item.Bonding == 2)
            operations.Add(PreparedItemTooltipPlain("Binds when equipped"));
        foreach (ItemDamage damage in item.Damages)
            operations.Add(PreparedItemTooltipPlain(
                $"{damage.Min:0.#} - {damage.Max:0.#} Damage"));
        if (item.Armor > 0)
            operations.Add(PreparedItemTooltipPlain($"{item.Armor} Armor"));
        string[] statNames =
            ["Mana", "Health", "Agility", "Strength", "Intellect", "Spirit", "Stamina"];
        foreach (ItemStat stat in item.Stats)
        {
            string statName = stat.Type < statNames.Length
                ? statNames[stat.Type]
                : $"Stat {stat.Type}";
            operations.Add(PreparedItemTooltipPlain(
                $"{(stat.Value >= 0 ? "+" : "")}{stat.Value} {statName}"));
        }
        if (item.RequiredLevel > 0)
            operations.Add(PreparedItemTooltipPlain($"Requires Level {item.RequiredLevel}"));
        if (item.ItemLevel > 0)
            operations.Add(PreparedItemTooltipDisabled($"Item Level {item.ItemLevel}"));
        if (maxDurability > 0)
            operations.Add(PreparedItemTooltipPlain(
                $"Durability {durability} / {maxDurability}"));
        if (count > 1)
            operations.Add(PreparedItemTooltipDisabled(
                $"Stack: {count} / {Math.Max(1, item.Stackable)}"));
        if (!compact && !string.IsNullOrWhiteSpace(item.Description))
            operations.Add(PreparedItemTooltipColored(item.Description,
                new Vector4(1f, .82f, 0f, 1f)));

        return new(operations.ToImmutable());
    }

    private static void DrawPreparedItemTooltipBody(in ItemTooltipBodySnapshot body)
    {
        if (body.Operations.IsDefault)
            throw new ArgumentException("The prepared item tooltip body is uninitialized.",
                nameof(body));
        foreach (PreparedItemTooltipPaintOp operation in body.Operations)
        {
            switch (operation.Kind)
            {
                case PreparedItemTooltipPaintKind.Plain:
                    ImGui.TextUnformatted(operation.Text);
                    break;
                case PreparedItemTooltipPaintKind.Disabled:
                    ImGui.TextDisabled(operation.Text);
                    break;
                case PreparedItemTooltipPaintKind.Colored:
                    ImGui.TextColored(operation.Color, operation.Text);
                    break;
                case PreparedItemTooltipPaintKind.Separator:
                    ImGui.Separator();
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unknown prepared item tooltip paint kind {operation.Kind}.");
            }
        }
    }

    private bool OfferPreparedItemTooltip(
        in GameTooltipOwnerKey owner,
        in ItemTooltipBodySnapshot body,
        Vector2? nextWindowPosition = null,
        int comparisonCount = 0,
        Action? preparedFollowingRenderer = null)
    {
        if (body.Operations.IsDefault)
            throw new ArgumentException("The prepared item tooltip body is uninitialized.",
                nameof(body));
        ItemTooltipBodySnapshot preparedBody = body;
        Vector2? preparedPosition = nextWindowPosition;
        Action? preparedFollowing = preparedFollowingRenderer;
        bool offered = OfferPreservedSharedGameTooltipRenderer(owner, () =>
        {
            if (preparedPosition is Vector2 at)
                ImGui.SetNextWindowPos(at, ImGuiCond.Always);
            ImGui.BeginTooltip();
            DrawPreparedItemTooltipBody(preparedBody);
            ImGui.EndTooltip();
            preparedFollowing?.Invoke();
        });
        if (!offered) return false;

        GameTooltipOwnerToken token = CurrentSharedGameTooltipOwnerToken();
        if (!SetSharedGameTooltipComparisonCount(token, comparisonCount))
            throw new InvalidOperationException(
                "A freshly offered item tooltip rejected its comparison ordinal.");
        return true;
    }

    private ImmutableArray<PreparedPaperDollComparisonTooltip>
        PreparePaperDollComparisonTooltips(ItemTemplate hoveredItem)
    {
        // Freeze the complete SHOW_COMPARE_TOOLTIP verdict at producer time. Equipped-item,
        // ammo, and inspect adapters never enter this method, preserving the self-compare rule.
        bool shift = ImGui.GetIO().KeyShift;
        bool show = PaperDollUiLaw.ShowBagItemComparison(_characterOpen, _characterTab, shift,
            sourceIsEquipped: false);
        if (!show || _net is null || _items is null ||
            !_entities.TryGet(ControlledGuid, out WorldEntity player))
            return [];

        float scale = GameplayUiScale();
        Vector2 frameOrigin = new(0, 104f * scale);
        bool captureParity = _uiParityArmed && _uiParityPanel == "character-frame";
        int candidateCount = PaperDollUiLaw.ComparisonSlotCount(hoveredItem.InventoryType);
        var prepared =
            ImmutableArray.CreateBuilder<PreparedPaperDollComparisonTooltip>(candidateCount);
        for (int ordinal = 0; ordinal < candidateCount; ordinal++)
        {
            int slot = PaperDollUiLaw.ComparisonSlot(hoveredItem.InventoryType, ordinal);
            // Preserve the authored arm before its live slot listener decides whether content
            // exists. Missing ordinal one cannot compact a surviving ShoppingTooltip2.
            int tooltipNumber = ordinal + 1;
            ulong equippedGuid = player.Fields.PlayerInventorySlot(slot);
            if (equippedGuid == 0 || !_entities.TryGet(equippedGuid, out WorldEntity equipped))
                continue;
            _items.Require(equipped.Entry, equipped.Guid, _net);
            if (!_items.TryGet(equipped.Entry, out ItemTemplate? equippedTemplate) ||
                equippedTemplate is null)
                continue;

            PaperDollUiLaw.LogicalRect logical = PaperDollUiLaw.EquipmentSlotRect(slot);
            Vector2 slotMin = frameOrigin + new Vector2(logical.X, logical.Y) * scale;
            PaperDollUiLaw.TooltipAnchor anchor = PaperDollUiLaw.ShoppingTooltipAnchor(ordinal);
            Vector2 windowAt = slotMin + new Vector2(logical.Width,
                ordinal == 0 ? 0f : logical.Height) * scale;
            prepared.Add(new(tooltipNumber, slot,
                PrepareItemTooltipBodySnapshot(equippedTemplate,
                    equipped.Fields.ItemStackCount, equipped.Fields.ItemDurability,
                    equipped.Fields.ItemMaxDurability, compact: true),
                windowAt, new Vector2(anchor.PivotX, anchor.PivotY), scale,
                anchor.Point, anchor.RelativePoint, PaperDollSlotElement(slot), captureParity));
        }
        return prepared.ToImmutable();
    }

    private void DrawPreparedPaperDollComparisonTooltips(
        ImmutableArray<PreparedPaperDollComparisonTooltip> comparisons)
    {
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration |
                                 ImGuiWindowFlags.AlwaysAutoResize |
                                 ImGuiWindowFlags.NoInputs |
                                 ImGuiWindowFlags.NoSavedSettings |
                                 ImGuiWindowFlags.NoFocusOnAppearing |
                                 ImGuiWindowFlags.NoNav;
        bool collected = false;
        foreach (PreparedPaperDollComparisonTooltip comparison in comparisons)
        {
            ImGui.SetNextWindowPos(comparison.WindowPosition, ImGuiCond.Always,
                comparison.WindowPivot);
            ImGui.Begin($"##paper-doll-comparison-{comparison.TooltipNumber}", flags);
            ImGui.SetWindowFontScale(
                Math.Max(.5f, 10f * comparison.Scale / ImGui.GetFontSize()));
            ImGui.TextDisabled("Currently Equipped");
            DrawPreparedItemTooltipBody(comparison.Body);
            if (comparison.CaptureParity)
            {
                Vector2 windowMin = ImGui.GetWindowPos();
                Vector2 windowSize = ImGui.GetWindowSize();
                Vector4 content = new(windowMin.X, windowMin.Y,
                    windowMin.X + windowSize.X, windowMin.Y + windowSize.Y);
                CollectUiParityDraw($"ShoppingTooltip{comparison.TooltipNumber}", "Frame",
                    windowMin, windowSize, comparison.ParentElement,
                    new("", 0, "TOOLTIP", comparison.AnchorPoint,
                        comparison.ParentElement, comparison.RelativePoint, 0, 0,
                        @"Fonts\FRIZQT__.TTF", 10, ContentRect: content, ClipRect: content,
                        ClipMask: "COMPACT_NO_DESCRIPTION", Visible: true, Enabled: false,
                        InteractionState:
                            $"shift-compare-live-slot:{comparison.EquipmentSlot}",
                        Strata: "TOOLTIP"));
                collected = true;
            }
            ImGui.End();
        }
        if (collected) _shoppingTooltipParityRendererCollected = true;
    }

    private void ArmDeferredShoppingTooltipParityCapture(
        ImmutableArray<PreparedPaperDollComparisonTooltip> comparisons)
    {
        ImmutableArray<ShoppingTooltipParityExpectation> expectations = comparisons
            .Where(comparison => comparison.CaptureParity)
            .Select(comparison => new ShoppingTooltipParityExpectation(
                comparison.TooltipNumber, comparison.ParentElement))
            .ToImmutableArray();
        if (expectations.IsEmpty) return;
        _shoppingTooltipParityCompletionPending = true;
        _shoppingTooltipParityRendererCollected = false;
        _shoppingTooltipParityExpectations = expectations;
    }

    private void CompleteDeferredShoppingTooltipParityCapture()
    {
        if (!_shoppingTooltipParityCompletionPending) return;
        bool collected = _shoppingTooltipParityRendererCollected;
        ImmutableArray<ShoppingTooltipParityExpectation> expectations =
            _shoppingTooltipParityExpectations;
        _shoppingTooltipParityCompletionPending = false;
        _shoppingTooltipParityRendererCollected = false;
        _shoppingTooltipParityExpectations = [];

        if (!collected)
            foreach (ShoppingTooltipParityExpectation expectation in expectations)
                ClassifyUiParity($"ShoppingTooltip{expectation.TooltipNumber}", "Frame",
                    expectation.ParentElement, "NOT-DRAWN",
                    "shared-tooltip-owner-replaced-before-tooltip-stratum");
        MarkUiParityFrameComplete();
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
        bool parityProof = _uiParityArmed &&
            (_uiParityPanel == "backpack" && container == 0 ||
             _uiParityPanel == "equipped-bag" && container == _uiParityEquippedBagContainer);
        string parityRoot = container == 0 ? "ContainerFrame1" : $"ContainerFrameBag{container}";
        int liveSize = container switch
        {
            0 => InventoryUiLaw.BackpackSlots,
            InventoryUiLaw.KeyringContainer => InventoryUiLaw.KeyringSize(owner.Level),
            _ => (int)Math.Clamp(owner.Fields.ContainerNumSlots, 1,
                InventoryUiLaw.MaxContainerSlots),
        };
        int physical = liveSize - slot;
        string parityButton = $"{parityRoot}Item{physical}";
        if (parityProof)
        {
            CollectUiParityDraw(parityButton, "Button", min, max - min, parityRoot,
                new("", 0, "IMGUI_HIT_TARGET", "ABSOLUTE", parityRoot, "TOPLEFT",
                    (min.X - _uiParityOrigin.X) / scale, -((min.Y - _uiParityOrigin.Y) / scale),
                    Enabled: !locked, InteractionState: locked ? "locked" : "normal",
                    HitMin: min, HitMax: max));
            if (item is not null)
                CollectUiParityDraw(parityButton + "Icon", "Texture", min, max - min, parityButton,
                    new(item.IconPath, locked ? 0xff666666 : 0xffffffff, "BACKGROUND", "CENTER",
                        parityButton, "CENTER", 0, 0));
            else
                ClassifyUiParity(parityButton + "Icon", "Texture", parityButton, "NOT-DRAWN",
                    "EMPTY_SLOT_NO_ITEM_TEXTURE");
            Vector2 ringCenter = (min + max) * .5f + new Vector2(0, -scale);
            CollectUiParityDraw(parityButton + "NormalTexture", "NormalTexture",
                ringCenter - new Vector2(32f * scale), new Vector2(64f * scale), parityButton,
                new(@"Interface\Buttons\UI-Quickslot2", 0xffffffff, "ARTWORK", "CENTER",
                    parityButton, "CENTER", 0, -1));
        }
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
        bool repairReleased = _vendorRepairMode && hovered &&
            ImGui.IsMouseReleased(ImGuiMouseButton.Left) && ImGui.IsItemDeactivated();
        // A possessed bot's bags are read-only in v1.0: every inventory mutation opcode is
        // guid-less and acts on the SESSION character server-side. Hover/tooltips stay live.
        bool interactive = ControlledGuid == LocalPlayerGuid;
        bool leftClicked = interactive && !_vendorRepairMode && ImGui.IsItemClicked(ImGuiMouseButton.Left);
        bool rightClicked = interactive && !_vendorRepairMode && ImGui.IsItemClicked(ImGuiMouseButton.Right);
        if (repairReleased) TryRepairMerchantItem(instance?.Guid ?? 0);
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
        if (!_vendorRepairMode && _itemCastSpell == 0 && _enchantConfirmation is null)
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
            if (parityProof)
                CollectUiParityDraw(parityButton + "PushedTexture", "PushedTexture", min, max - min,
                    parityButton, new(@"Interface\Buttons\UI-Quickslot-Depress", 0xffffffff,
                        "ARTWORK", "CENTER", parityButton, "CENTER", 0, 0));
        }
        if (hovered)
        {
            uint highlight = _gameplayArt.AdditiveHandle(@"Interface\Buttons\ButtonHilight-Square");
            if (highlight != 0) dl.AddImage((nint)highlight, min, max);
            if (parityProof)
                CollectUiParityDraw(parityButton + "HighlightTexture", "HighlightTexture", min, max - min,
                    parityButton, new(@"Interface\Buttons\ButtonHilight-Square", 0xffffffff,
                        "OVERLAY", "CENTER", parityButton, "CENTER", 0, 0));
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
        {
            GameText.DrawRightAligned(dl, "NumberFontNormal", count.ToString(),
                new Vector2(max.X - 4f * scale,
                    max.Y - GameText.EmPixels("NumberFontNormal", scale) - 2f * scale), scale);
            if (parityProof)
                CollectUiParityDraw(parityButton + "Count", "FontString", min, max - min, parityButton,
                    new("", 0xffffffff, "OVERLAY", "BOTTOMRIGHT", parityButton, "BOTTOMRIGHT", -5, 2,
                        @"Fonts\FRIZQT__.TTF", 12));
        }
        if (hovered && item is not null)
        {
            Vector2 display = ImGui.GetIO().DisplaySize;
            Vector2 mouse = ImGui.GetIO().MousePos;
            Vector2 tooltipPosition = mouse +
                new Vector2(mouse.X < display.X * .5f ? 24f : -300f, 18f);
            ItemTooltipBodySnapshot body = PrepareItemTooltipBodySnapshot(item, count,
                instance?.Fields.ItemDurability ?? 0,
                instance?.Fields.ItemMaxDurability ?? 0);
            ImmutableArray<PreparedPaperDollComparisonTooltip> comparisons =
                PreparePaperDollComparisonTooltips(item);
            Action? drawComparisons = comparisons.IsEmpty
                ? null
                : () => DrawPreparedPaperDollComparisonTooltips(comparisons);
            bool offered = OfferPreparedItemTooltip(
                InventoryItemGameTooltipOwner(container, physical), body, tooltipPosition,
                HighestLiveComparisonOrdinal(
                    comparisons.Select(comparison => comparison.TooltipNumber)),
                drawComparisons);
            if (offered) ArmDeferredShoppingTooltipParityCapture(comparisons);
            string? cursor = _vendorRepairMode ? "Repair" :
                InventoryUiLaw.HoverCursor(_vendor is not null,
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
        if (_net is null || !_entities.TryGet(ControlledGuid, out WorldEntity player)) return null;
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

    private bool TryCancelStackSplitOnEscape()
    {
        if (_splitContainer == InventoryUiLaw.EmptyContainer) return false;
        CancelStackSplit();
        return true;
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
        => PlayUiSound(name, "ui.inventory");

    private void PlayUiSound(string name, string category = "ui")
    {
        Vector3 listener = _controller?.Position ?? Vector3.Zero;
        _spellSounds?.Play(name, ControlledGuid, listener, listener, category);
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
        if (_net is null || !_entities.TryGet(ControlledGuid, out WorldEntity player)) return false;
        bool open = InventoryUiLaw.ShouldOpenAllBags(_backpackOpen, _equippedBagOpen);
        return SetAllNormalBagWindows(player, open);
    }

    private bool CloseAllBagWindows()
    {
        if (!_backpackOpen && !_keyringOpen && !_equippedBagOpen.Any(x => x)) return false;
        if (_net is not null && _entities.TryGet(ControlledGuid, out WorldEntity player))
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
