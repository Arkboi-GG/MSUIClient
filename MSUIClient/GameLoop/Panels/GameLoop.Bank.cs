using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private bool _bankOpen;
    private ulong _bankSource;
    private bool _bankPurchaseConfirmOpen;
    private BankBagSlotPriceTable? _bankPrices;
    private BankTransition? _pendingBankTransition;
    private byte _bankSlotCountBefore;

    private sealed record BankTransition(string Kind, ulong ItemGuid, uint Entry, int From, int To, double SentAt);

    private void InitBank()
    {
        if (_mpq?.ReadFile(BankBagSlotPriceTable.MpqPath) is { } bytes)
            _bankPrices = BankBagSlotPriceTable.Parse(bytes);
    }

    private bool RequestBank(ulong guid)
    {
        WorldEntity? banker = null;
        bool eligible = _controller is not null &&
                        _entities.TryGet(guid, out banker) && banker.IsCreature &&
                        !banker.IsDead && (banker.NpcFlags & NpcBanker) != 0 &&
                        NpcSessionUiLaw.InRange(
                            Vector3.DistanceSquared(_controller.Position, banker.Position));
        bool sent = eligible && _net?.BankerActivate(guid) == true;
        EmitInterface("bank", "open", sent ? "SENT" : "REFUSED", guid,
            $"eligible={eligible};npcFlags=0x{banker?.NpcFlags ?? 0:X8};body={Convert.ToHexString(WorldSession.BuildBankGuidBody(guid))}");
        return sent;
    }

    private bool UpdateBankLifecycle()
    {
        if (!_bankOpen || _controller is null) return false;
        ulong sourceGuid = _bankSource;
        bool sourceAvailable = _entities.TryGet(sourceGuid, out WorldEntity banker);
        float distanceSquared = sourceAvailable
            ? Vector3.DistanceSquared(_controller.Position, banker.Position)
            : float.PositiveInfinity;
        if (!NpcSessionUiLaw.ShouldClose(true, true, sourceAvailable, distanceSquared))
            return false;
        CloseBankSession(playSound: true);
        EmitInterface("bank", "lifecycle-close", "CLOSED", sourceGuid,
            sourceAvailable
                ? $"distanceSquared={distanceSquared:R};limitSquared={NpcSessionUiLaw.ServiceRangeSquared:R}"
                : "source-despawned");
        return true;
    }

    private void ApplyShowBank(byte[] body)
    {
        if (body.Length < 8) return;
        ulong source = BitConverter.ToUInt64(body, 0);
        bool freshSession = !_bankOpen || _bankSource != source;
        if (_bankOpen && _bankSource != source) CloseBankSession(playSound: true);
        _bankSource = source;
        _bankOpen = true;
        if (freshSession)
            PlayUiSound(BankFrameUiLaw.OpenSound, BankFrameUiLaw.SoundCategory);
        int occupied = 0;
        if (_net is not null && _entities.TryGet(_net.PlayerGuid, out WorldEntity player))
            occupied = Enumerable.Range(0, 24).Count(i => player.Fields.PlayerBankSlot(i) != 0);
        EmitInterface("bank", "open", "OPEN", _bankSource, $"occupied={occupied};body={Convert.ToHexString(body.AsSpan(0, 8))}");
    }

    private bool CloseBankSession(bool playSound = true)
    {
        if (!_bankOpen) return false;
        ulong source = _bankSource;
        _bankOpen = false;
        _bankSource = 0;
        _bankPurchaseConfirmOpen = false;
        _pendingBankTransition = null;
        for (int container = InventoryUiLaw.BankBagContainerFirst;
             container <= InventoryUiLaw.BankBagContainerLast; container++)
            SetBagWindowOpen(container, false, playSound: false);
        if (_carriedContainer == InventoryUiLaw.BankContainer ||
            _carriedContainer == InventoryUiLaw.BankBagEquipmentContainer ||
            _carriedContainer is >= InventoryUiLaw.BankBagContainerFirst and
                <= InventoryUiLaw.BankBagContainerLast)
            ClearCarriedItem();
        if (playSound)
            PlayUiSound(BankFrameUiLaw.CloseSound, BankFrameUiLaw.SoundCategory);
        EmitInterface("bank", "close", "CLOSED", source, $"sound={playSound}");
        return true;
    }

    private bool TryDismissBankPurchaseConfirmationOnEscape()
    {
        if (!_bankPurchaseConfirmOpen) return false;
        _bankPurchaseConfirmOpen = false;
        EmitInterface("bank", "buy-confirm", "CANCELLED_ESCAPE", _bankSource, "wire=none");
        return true;
    }

    private void ApplyBuyBankSlotResult(byte[] body)
    {
        if (body.Length < 4) return;
        uint result = BitConverter.ToUInt32(body, 0);
        EmitInterface("bank", "buy-result", result == 0 ? "SUCCESS" : $"FAILED-{result}", _bankSource,
            $"result={result};slotBefore={_bankSlotCountBefore}");
    }

    private bool DepositBankItem(byte sourceBag, byte sourceSlot, WorldEntity item)
    {
        if (!_bankOpen || _net is null) return false;
        bool sent = _net.AutoBankItem(sourceBag, sourceSlot);
        EmitInterface("bank", "deposit-send", sent ? "SENT" : "SEND_FAILED", item.Guid,
            $"item={item.Entry};sourceBag={sourceBag};sourceSlot={sourceSlot};destination=server-selected;body={Convert.ToHexString(WorldSession.BuildAutoBankItemBody(sourceBag, sourceSlot))}");
        if (sent)
            _pendingBankTransition = new("deposit", item.Guid, item.Entry, sourceSlot, -1, NowSeconds());
        return sent;
    }

    // Clinical/live helper: resolve an entry to a source position, then use the same
    // source-only auto-bank wire as the production inventory context action.
    private bool DepositBankEntry(uint entry)
    {
        if (!_bankOpen || _net is null || !_entities.TryGet(_net.PlayerGuid, out WorldEntity player)) return false;
        int from = Enumerable.Range(0, 16).FirstOrDefault(i =>
        {
            ulong guid = player.Fields.PlayerBackpackSlot(i);
            return guid != 0 && _entities.TryGet(guid, out WorldEntity item) && item.Entry == entry;
        }, -1);
        if (from < 0) return false;
        ulong itemGuid = player.Fields.PlayerBackpackSlot(from);
        return _entities.TryGet(itemGuid, out WorldEntity item) &&
            DepositBankItem(255, (byte)(23 + from), item);
    }

    private bool WithdrawBankSlot(int bankIndex, WorldEntity item)
    {
        if (!_bankOpen || _net is null || bankIndex is < 0 or >= 24) return false;
        byte sourceSlot = (byte)(39 + bankIndex);
        bool sent = _net.AutostoreBankItem(255, sourceSlot);
        EmitInterface("bank", "withdraw-send", sent ? "SENT" : "SEND_FAILED", item.Guid,
            $"item={item.Entry};sourceBag=255;sourceSlot={sourceSlot};destination=server-selected;body={Convert.ToHexString(WorldSession.BuildAutostoreBankItemBody(255, sourceSlot))}");
        if (sent)
            _pendingBankTransition = new("withdraw", item.Guid, item.Entry, sourceSlot, -1, NowSeconds());
        return sent;
    }

    // Clinical/live helper: resolve an entry to its source bank slot. Production
    // bank right-clicks already know the precise index and bypass this lookup.
    private bool WithdrawBankEntry(uint entry)
    {
        if (!_bankOpen || _net is null || !_entities.TryGet(_net.PlayerGuid, out WorldEntity player)) return false;
        int from = Enumerable.Range(0, 24).FirstOrDefault(i =>
        {
            ulong guid = player.Fields.PlayerBankSlot(i);
            return guid != 0 && _entities.TryGet(guid, out WorldEntity item) && item.Entry == entry;
        }, -1);
        if (from < 0) return false;
        ulong itemGuid = player.Fields.PlayerBankSlot(from);
        return _entities.TryGet(itemGuid, out WorldEntity item) && WithdrawBankSlot(from, item);
    }

    private bool BuyNextBankSlot()
    {
        if (!_bankOpen || _net is null || !_entities.TryGet(_net.PlayerGuid, out WorldEntity player)) return false;
        _bankSlotCountBefore = player.Fields.BankBagSlotCount;
        bool sent = _net.BuyBankSlot(_bankSource);
        uint price = _bankPrices?.Price(_bankSlotCountBefore + 1) ?? 0;
        EmitInterface("bank", "buy-send", sent ? "SENT" : "SEND_FAILED", _bankSource,
            $"slot={_bankSlotCountBefore + 1};price={price};money={player.Fields.Coinage};body={Convert.ToHexString(WorldSession.BuildBankGuidBody(_bankSource))}");
        if (sent) _pendingBankTransition = new("buy", 0, 0, _bankSlotCountBefore, _bankSlotCountBefore + 1, NowSeconds());
        return sent;
    }

    private void ObserveBankTransition()
    {
        if (_pendingBankTransition is not { } pending || _net is null ||
            !_entities.TryGet(_net.PlayerGuid, out WorldEntity player)) return;
        bool complete = pending.Kind switch
        {
            "deposit" => Enumerable.Range(0, 24).Any(i => player.Fields.PlayerBankSlot(i) == pending.ItemGuid),
            "withdraw" => Enumerable.Range(0, 16).Any(i => player.Fields.PlayerBackpackSlot(i) == pending.ItemGuid),
            "buy" => player.Fields.BankBagSlotCount > pending.From,
            _ => false,
        };
        if (complete)
        {
            if (pending.Kind == "buy")
                EmitInterface("bank", "buy-result", "SUCCESS-BY-DELTA", _bankSource,
                    $"slotBefore={pending.From};slotAfter={player.Fields.BankBagSlotCount};serverSuccessHasNoResultPacket=true");
            EmitInterface("bank", pending.Kind == "buy" ? "bag-slot" : "transfer",
                pending.Kind == "deposit" ? "DEPOSITED" : pending.Kind == "withdraw" ? "WITHDRAWN" : "PURCHASED",
                pending.ItemGuid, $"item={pending.Entry};from={pending.From};to={pending.To};slotCount={player.Fields.BankBagSlotCount}");
            _pendingBankTransition = null;
        }
        else if (NowSeconds() - pending.SentAt > 5)
        {
            EmitInterface("bank", pending.Kind == "buy" ? "bag-slot" : "transfer", "TIMEOUT", pending.ItemGuid,
                $"kind={pending.Kind};item={pending.Entry};from={pending.From};to={pending.To}");
            _pendingBankTransition = null;
        }
    }

    private void DrawBankFrame()
    {
        if (!_bankOpen || _net is null || _gameplayArt is null || !_entities.TryGet(_net.PlayerGuid, out WorldEntity player)) return;
        float s=GameplayUiScale(); Vector2 origin=UiPanelFrameOrigin(UiPanelOwnershipRegistry[5], s), logicalSize=new(BankFrameUiLaw.Width,BankFrameUiLaw.Height);
        ImGui.SetNextWindowPos(origin,ImGuiCond.Always); ImGui.SetNextWindowSize(BankFrameUiLaw.FrameSize(s),ImGuiCond.Always); ImGui.SetNextWindowBgAlpha(0);
        if (!ImGui.Begin("##bank", ImGuiWindowFlags.NoDecoration|ImGuiWindowFlags.NoMove|ImGuiWindowFlags.NoSavedSettings|ImGuiWindowFlags.NoBackground|ImGuiWindowFlags.NoNav)) { ImGui.End(); return; }
        ImDrawListPtr dl=ImGui.GetWindowDrawList();
        if(_uiParityArmed&&_uiParityPanel=="bank"){BeginUiParityFrame(origin,s);CollectUiParityDraw("BankFrame","Frame",origin,logicalSize*s,"",new("",0,"IMGUI_HOST","ANCHOR:ABSOLUTE","","",0,8));}
        WorldEntity? banker = _entities.TryGet(_bankSource, out WorldEntity foundBanker)
            ? foundBanker : null;
        if (banker is not null)
            DrawUnitPortraitImage(dl, banker,
                origin + BankFrameUiLaw.PortraitOffset * s,
                BankFrameUiLaw.PortraitSize * s, 0, false);
        DrawArt(dl, BankFrameUiLaw.Art, origin,
            new Vector2(BankFrameUiLaw.ArtSize), s);
        if(_uiParityArmed&&_uiParityPanel=="bank")
            CollectUiParityDraw("BankFrame/Texture","Texture",origin,
                new Vector2(BankFrameUiLaw.ArtSize)*s,"BankFrame",
                new(BankFrameUiLaw.Art,0xffffffff,"IMGUI_IMAGE","TOPLEFT","BankFrame","TOPLEFT",0,0));
        string bankerName = BankFrameUiLaw.FallbackTitle;
        if (banker is not null)
        {
            if (banker.Entry != 0 && TryBeginCreatureQuery(banker.Entry))
                _net?.CreatureQuery(banker.Entry, banker.Guid);
            bankerName = BankFrameUiLaw.Title(
                _creatureNames.GetValueOrDefault(banker.Entry, ""));
        }
        DrawNpcModalTitle(dl, bankerName, origin + BankFrameUiLaw.TitleCenter * s, s);
        DrawTrainerMoney(dl, player.Fields.Coinage,
            origin + BankFrameUiLaw.PurseRightTop * s, s, 0xffffffff, rightAligned: true);
        if (_gameplayArt is not null)
        {
            DrawVanillaBankSlots(dl, origin, s, player);
            Vector2 bankClose=origin+BankFrameUiLaw.CloseButton*s;
            DrawImageButton(dl,"##bank-close",bankClose,new Vector2(32)*s,@"Interface\Buttons\UI-Panel-MinimizeButton-Up",@"Interface\Buttons\UI-Panel-MinimizeButton-Down",@"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");
            if(ImGui.IsItemClicked())CloseBankSession();
            if(_uiParityArmed&&_uiParityPanel=="bank")MarkUiParityFrameComplete();
            ImGui.End(); return;
        }
        ImGui.SetCursorScreenPos(origin+new Vector2(35,75)*s); ImGui.BeginChild("##bank-slots",new Vector2(295,245)*s,false);
        for (int i = 0; i < 24; i++)
        {
            ulong guid = player.Fields.PlayerBankSlot(i);
            string label = $"{39 + i}: Empty";
            if (guid != 0 && _entities.TryGet(guid, out WorldEntity item))
            {
                _items?.Require(item.Entry, guid, _net!);
                label = _items?.TryGet(item.Entry, out ItemTemplate? t) == true && t is not null
                    ? $"{39 + i}: {t.Name} x{item.Fields.ItemStackCount}" : $"{39 + i}: Item {item.Entry}";
            }
            ImGui.Selectable(label + $"##bank-{i}");
        }
        ImGui.EndChild(); ImGui.SetCursorScreenPos(origin+new Vector2(36,333)*s);
        byte count = player.Fields.BankBagSlotCount;
        uint price = _bankPrices?.Price(count + 1) ?? 0;
        ImGui.TextUnformatted($"Bag slots purchased: {count}/6 — next: {FormatMoney(price)}");
        for (int i = 0; i < 6; i++) { if (i > 0) ImGui.SameLine(); ImGui.TextUnformatted(i < count ? "[Open]" : "[Locked]"); }
        if (count < 6 && ImGui.Button("Purchase next bag slot")) BuyNextBankSlot();
        if (_config.DevTools && ImGui.Button("Copy bank evidence"))
            CopyVerdictText(string.Join(Environment.NewLine, _verdicts.Snapshot("interface").OfType<InterfaceVerdict>()
                .Where(v => v.Family == "bank").Select(v => $"[verdict:interface] {v.ToLine()}")));
        Vector2 close=origin+BankFrameUiLaw.CloseButton*s;DrawImageButton(dl,"##bank-close",close,new Vector2(32)*s,@"Interface\Buttons\UI-Panel-MinimizeButton-Up",@"Interface\Buttons\UI-Panel-MinimizeButton-Down",@"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");if(ImGui.IsItemClicked())CloseBankSession();
        if(_uiParityArmed&&_uiParityPanel=="bank")MarkUiParityFrameComplete();
        ImGui.End();
    }

    private void DrawVanillaBankSlots(ImDrawListPtr dl, Vector2 origin, float s, WorldEntity player)
    {
        for (int i = 0; i < 24; i++)
        {
            ulong guid = player.Fields.PlayerBankSlot(i);
            WorldEntity? instance = guid != 0 && _entities.TryGet(guid, out WorldEntity found) ? found : null;
            ItemTemplate? item = null;
            if (instance is not null) { _items?.Require(instance.Entry, guid, _net!); _items?.TryGet(instance.Entry, out item); }
            int row = i / 6, col = i % 6;
            Vector2 min = origin + new Vector2(35 + col * 43, 78 + row * 43) * s;
            uint icon = item is null ? 0 : _gameplayArt?.Handle(item.IconPath) ?? 0;
            if (icon != 0) dl.AddImage((nint)icon, min, min + new Vector2(37) * s);
            uint ring = _gameplayArt?.Handle(@"Interface\Buttons\UI-Quickslot2") ?? 0;
            if (ring != 0) dl.AddImage((nint)ring, min - new Vector2(14) * s, min + new Vector2(50) * s);
            ImGui.SetCursorScreenPos(min); ImGui.InvisibleButton($"##bank-item-{i}", new Vector2(37) * s);
            bool leftClicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);
            bool rightClicked = ImGui.IsItemClicked(ImGuiMouseButton.Right);
            bool locked = IsInventorySlotLocked(InventoryUiLaw.BankContainer, i);
            InventoryUiLaw.SlotClickAction action = InventoryUiLaw.ClickAction(
                leftClicked, rightClicked, ImGui.GetIO().KeyShift, HasCarriedItem,
                instance is not null, instance?.Fields.ItemStackCount ?? 0, locked,
                tradePlacement: false);
            switch (action)
            {
                case InventoryUiLaw.SlotClickAction.Split:
                    OpenStackSplit(InventoryUiLaw.BankContainer, i,
                        (int)(instance?.Fields.ItemStackCount ?? 0), min + new Vector2(37) * s);
                    break;
                case InventoryUiLaw.SlotClickAction.PickupOrPlace:
                    CancelStackSplit();
                    PickupOrPlaceItem(InventoryUiLaw.BankContainer, i, guid);
                    break;
                case InventoryUiLaw.SlotClickAction.ClearCarried:
                    CancelStackSplit();
                    ClearCarriedItem();
                    break;
                case InventoryUiLaw.SlotClickAction.ContextAction when instance is not null:
                    CancelStackSplit();
                    WithdrawBankSlot(i, instance);
                    break;
            }
            HandleInventoryDrag(InventoryUiLaw.BankContainer, i, guid, item);
            if (ImGui.IsItemHovered() && item is not null)
            {
                uint itemCount = instance?.Fields.ItemStackCount ?? 1;
                ItemTooltipBodySnapshot tooltipBody =
                    PrepareItemTooltipBodySnapshot(item, itemCount);
                OfferPreparedItemTooltip(new("item:bank-item", (ulong)i), tooltipBody);
            }
        }
        byte count = player.Fields.BankBagSlotCount; uint price = _bankPrices?.Price(count + 1) ?? 0;
        for (int i = 0; i < 6; i++)
            DrawBankBagButton(dl, origin, s, player, i, count);
        if (count < 6)
        {
            DrawWrappedText(dl, BankFrameUiLaw.PurchaseMessageText,
                origin + BankFrameUiLaw.PurchaseMessage * s,
                BankFrameUiLaw.PurchaseMessageWidth, 10 * s, s, 0xffffffff, 2);
            GameText.Draw(dl, "GameFontNormal", BankFrameUiLaw.CostText,
                origin + BankFrameUiLaw.CostLabel * s, s);
            float costLabelWidth = GameText.MeasureWidth(
                "GameFontNormal", BankFrameUiLaw.CostText, s);
            DrawTrainerMoney(dl, price,
                origin + BankFrameUiLaw.CostLabel * s + new Vector2(costLabelWidth + 4 * s, 0),
                s, player.Fields.Coinage >= price ? 0xffffffff : 0xff1a1aff,
                rightAligned: false);
            if (VanillaButton(dl,"##bank-buy-slot",BankFrameUiLaw.PurchaseText,
                    origin+BankFrameUiLaw.PurchaseButton*s,
                    BankFrameUiLaw.PurchaseButtonSize,s))
            {
                PlayUiSound(BankFrameUiLaw.PurchaseSound, BankFrameUiLaw.SoundCategory);
                _bankPurchaseConfirmOpen = true;
                EmitInterface("bank", "buy-confirm", "OPEN", _bankSource,
                    $"slot={count + 1};price={price}");
            }
        }
    }

    private void DrawBankBagButton(ImDrawListPtr draw, Vector2 origin, float scale,
        WorldEntity player, int index, byte purchasedCount)
    {
        int container = InventoryUiLaw.BankBagContainerFirst + index;
        ulong guid = player.Fields.PlayerBankBagSlot(index);
        WorldEntity? bag = guid != 0 && _entities.TryGet(guid, out WorldEntity found)
            ? found : null;
        ItemTemplate? template = null;
        if (bag is not null)
        {
            _items?.Require(bag.Entry, bag.Guid, _net!);
            _items?.TryGet(bag.Entry, out template);
        }

        Vector2 min = origin + BankFrameUiLaw.BankBagSlotMin(index) * scale;
        bool purchased = index < purchasedCount;
        string iconPath = template?.IconPath ?? @"Interface\PaperDoll\UI-PaperDoll-Slot-Bag";
        uint icon = _gameplayArt?.Handle(iconPath) ?? 0;
        uint tint = purchased ? 0xffffffff : 0xff1a1aff;
        if (icon != 0)
            draw.AddImage((nint)icon, min, min + new Vector2(37) * scale,
                Vector2.Zero, Vector2.One, tint);
        uint ring = _gameplayArt?.Handle(purchased
            ? @"Interface\Buttons\UI-Quickslot2"
            : @"Interface\BankFrame\UI-Bank-Slot-Locked") ?? 0;
        if (ring != 0)
            draw.AddImage((nint)ring, min - new Vector2(14) * scale,
                min + new Vector2(50) * scale);

        ImGui.SetCursorScreenPos(min);
        ImGui.InvisibleButton($"##bank-bag-{index}", new Vector2(37) * scale);
        bool clicked = ImGui.IsItemClicked(ImGuiMouseButton.Left) ||
            ImGui.IsItemClicked(ImGuiMouseButton.Right);
        if (clicked)
        {
            if (HasCarriedItem)
                PickupOrPlaceItem(InventoryUiLaw.BankBagEquipmentContainer, index, guid,
                    ignoreModifiers: true);
            else if (bag is not null)
                SetBagWindowOpen(container, !IsBagWindowOpen(container));
        }
        HandleInventoryDrag(InventoryUiLaw.BankBagEquipmentContainer, index, guid, template);

        if (IsBagWindowOpen(container))
        {
            uint checkedArt = _gameplayArt?.AdditiveHandle(
                @"Interface\Buttons\CheckButtonHilight") ?? 0;
            if (checkedArt != 0)
                draw.AddImage((nint)checkedArt, min, min + new Vector2(37) * scale);
        }
        if (ImGui.IsItemHovered())
        {
            string text = template?.Name ?? (purchased ? "Bag Slot" : "Purchasable Bag Slot");
            OfferPreservedSharedGameTooltipRenderer(new("bank-bag-button", (ulong)index), () =>
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted(text);
                ImGui.EndTooltip();
            });
        }
    }

    private void DrawBankPurchaseConfirmation()
    {
        if (!_bankPurchaseConfirmOpen || !_bankOpen || _skin is null) return;
        float scale = GameplayUiScale();
        string[] lines = WrapTooltipText(BankPurchaseConfirmUiLaw.Prompt,
            "GameFontHighlight", scale,
            BankPurchaseConfirmUiLaw.TextWidth * scale).ToArray();
        float linePitch = GameText.LinePitch("GameFontHighlight", 1f);
        float textHeight = lines.Length * linePitch;
        BankPurchaseConfirmUiLaw.ScreenRect frame = BankPurchaseConfirmUiLaw.PopupRect(
            ImGui.GetIO().DisplaySize, scale, textHeight);

        ImGui.SetNextWindowPos(frame.Min, ImGuiCond.Always);
        ImGui.SetNextWindowSize(frame.Size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav;
        bool begun = ImGui.Begin("##bank-purchase-confirm", flags);
        ImGui.PopStyleVar(2);
        if (!begun) { ImGui.End(); return; }

        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        _skin.DrawBackdrop(draw, frame.Min, frame.Min + frame.Size, WowSkin.Dialog);
        for (int i = 0; i < lines.Length; i++)
            GameText.DrawCentered(draw, "GameFontHighlight", lines[i],
                frame.Min + BankPurchaseConfirmUiLaw.TextCenter(
                    (i + .5f) * linePitch) * scale, scale);

        bool accept = DrawBankPurchaseConfirmationButton(draw, 1,
            BankPurchaseConfirmUiLaw.AcceptText,
            frame.Min + BankPurchaseConfirmUiLaw.ButtonMin(1, textHeight) * scale, scale);
        bool cancel = DrawBankPurchaseConfirmationButton(draw, 2,
            BankPurchaseConfirmUiLaw.CancelText,
            frame.Min + BankPurchaseConfirmUiLaw.ButtonMin(2, textHeight) * scale, scale);
        ImGui.End();

        if (accept)
        {
            _bankPurchaseConfirmOpen = false;
            bool sent = BuyNextBankSlot();
            EmitInterface("bank", "buy-confirm", sent ? "ACCEPTED" : "SEND_FAILED",
                _bankSource, "wire=CMSG_BUY_BANK_SLOT");
        }
        else if (cancel)
        {
            _bankPurchaseConfirmOpen = false;
            EmitInterface("bank", "buy-confirm", "CANCELLED", _bankSource, "wire=none");
        }
    }

    private bool DrawBankPurchaseConfirmationButton(
        ImDrawListPtr draw, int buttonIndex, string caption, Vector2 min, float scale)
    {
        Vector2 size = new(BankPurchaseConfirmUiLaw.ButtonWidth * scale,
            BankPurchaseConfirmUiLaw.ButtonHeight * scale);
        ImGui.SetCursorScreenPos(min);
        bool clicked = ImGui.InvisibleButton($"##bank-purchase-confirm-{buttonIndex}", size);
        bool pressed = ImGui.IsItemActive();
        bool hovered = ImGui.IsItemHovered();
        uint art = _skin!.TextureHandle(pressed ? "dialog.button.down" : "dialog.button.up");
        if (art != 0)
            draw.AddImage((nint)art, min, min + size, Vector2.Zero, new Vector2(1f, .625f));
        if (hovered)
        {
            uint highlight = _gameplayArt?.BrightHighlightHandle(
                @"Interface\Buttons\UI-DialogBox-Button-Highlight") ?? 0;
            if (highlight != 0)
                draw.AddImage((nint)highlight, min, min + size,
                    Vector2.Zero, new Vector2(1f, .625f));
        }
        GameText.DrawCentered(draw, hovered ? "GameFontHighlight" : "GameFontNormal",
            caption, min + size * .5f, scale);
        return clicked;
    }
}
