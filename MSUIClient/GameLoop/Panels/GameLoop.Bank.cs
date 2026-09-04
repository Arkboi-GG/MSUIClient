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

    // [SUI] The bank acts as the DRIVEN body: the server's banker/bank-slot handlers run
    // as GetSuiActor(), the driven bot's bank slots ride its snapshot (v4 bank rows) and
    // every read below keys on ControlledGuid (== the session player unpossessed).
    private bool RequestBank(ulong guid)
    {
        if (RefuseTacticalFreezeLiveCommand("opening the bank")) return false;
        if (RefuseTacticalFrozenActor(guid, "open its bank service")) return false;
        WorldEntity? banker = null;
        bool eligible = TryGetInteractionBodyPose(out WorldBodyPose sessionBody) &&
                        _entities.TryGet(guid, out banker) && banker.IsCreature &&
                        !banker.IsDead && (banker.NpcFlags & NpcBanker) != 0 &&
                        NpcSessionUiLaw.InRange(
                            Vector3.DistanceSquared(sessionBody.Position, banker.Position));
        bool sent = eligible && _net?.BankerActivate(guid) == true;
        EmitInterface("bank", "open", sent ? "SENT" : "REFUSED", guid,
            $"eligible={eligible};npcFlags=0x{banker?.NpcFlags ?? 0:X8};body={Convert.ToHexString(WorldSession.BuildBankGuidBody(guid))}");
        return sent;
    }

    private bool UpdateBankLifecycle()
    {
        if (!_bankOpen ||
            !TryGetInteractionBodyPose(out WorldBodyPose sessionBody)) return false;
        ulong sourceGuid = _bankSource;
        bool sourceAvailable = _entities.TryGet(sourceGuid, out WorldEntity banker);
        float distanceSquared = sourceAvailable
            ? Vector3.DistanceSquared(sessionBody.Position, banker.Position)
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
        {
            SetBagWindowOpen(0, true);
            PlayUiSound(BankFrameUiLaw.OpenSound, BankFrameUiLaw.SoundCategory);
        }
        int occupied = 0;
        if (_net is not null && _entities.TryGet(ControlledGuid, out WorldEntity player))
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
        // vmangos BuyBankSlotResult: 0 too many, 1 insufficient funds, 2 not a banker, 3 OK.
        // Success is observed on the descriptor; the three refusals are told to the player
        // (they used to vanish into the dev log — the confirm closed and nothing happened).
        EmitInterface("bank", "buy-result", result == 3 ? "SUCCESS" : $"FAILED-{result}", _bankSource,
            $"result={result};slotBefore={_bankSlotCountBefore}");
        string? refusal = result switch
        {
            0 => InventoryGlobalString("ERR_BANKSLOT_FAILED_TOO_MANY", "You cannot buy any more bank slots."),
            1 => InventoryGlobalString("ERR_BANKSLOT_INSUFFICIENT_FUNDS", "You don't have enough money to buy that bank slot."),
            2 => InventoryGlobalString("ERR_BANKSLOT_NOTBANKER", "You must be at a banker to buy a bank slot."),
            _ => null,
        };
        if (refusal is not null) ShowUiError(refusal);
    }

    private bool DepositBankItem(byte sourceBag, byte sourceSlot, WorldEntity item)
    {
        if (!_bankOpen || _net is null) return false;
        if (RefuseTacticalFreezeLiveCommand("moving bank items")) return false;
        if (RefuseTacticalFrozenActor(_bankSource, "deposit through it")) return false;
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
        if (!_bankOpen || _net is null || !_entities.TryGet(ControlledGuid, out WorldEntity player)) return false;
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
        if (RefuseTacticalFreezeLiveCommand("moving bank items")) return false;
        if (RefuseTacticalFrozenActor(_bankSource, "withdraw through it")) return false;
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
        if (!_bankOpen || _net is null || !_entities.TryGet(ControlledGuid, out WorldEntity player)) return false;
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
        if (!_bankOpen || _net is null || !_entities.TryGet(ControlledGuid, out WorldEntity player)) return false;
        if (RefuseTacticalFreezeLiveCommand("buying a bank slot")) return false;
        if (RefuseTacticalFrozenActor(_bankSource, "buy a bank slot from it")) return false;
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
            !_entities.TryGet(ControlledGuid, out WorldEntity player)) return;
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
        if (!_bankOpen || _net is null || _gameplayArt is null ||
            !_entities.TryGet(ControlledGuid, out WorldEntity player)) return;
        float s = GameplayUiScale();
        Vector2 origin = UiPanelFrameOrigin(UiPanelOwnershipRegistry[5], s);
        ImGui.SetNextWindowPos(origin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(BankFrameUiLaw.FrameSize(s), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoNav;
        if (!ImGui.Begin("##bank", flags))
        {
            ImGui.End();
            return;
        }
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        if (_uiParityArmed && _uiParityPanel == "bank")
        {
            BeginUiParityFrame(origin, s);
            CollectUiParityDraw("BankFrame", "Frame", origin,
                BankFrameUiLaw.FrameLogicalSize * s, "",
                new("", 0, "IMGUI_HOST", "ANCHOR:ABSOLUTE", "", "", 0, 8));
        }
        WorldEntity? banker = _entities.TryGet(_bankSource, out WorldEntity foundBanker)
            ? foundBanker : null;
        if (banker is not null)
            DrawUnitPortraitImage(dl, banker,
                origin + BankFrameUiLaw.PortraitOffset * s,
                BankFrameUiLaw.PortraitSize * s, 0, false);
        DrawArt(dl, BankFrameUiLaw.Art, origin,
            BankFrameUiLaw.ArtLogicalSize, s);
        if (_uiParityArmed && _uiParityPanel == "bank")
            CollectUiParityDraw("BankFrame/Texture", "Texture", origin,
                BankFrameUiLaw.ArtLogicalSize * s, "BankFrame",
                new(BankFrameUiLaw.Art, 0xffffffff, "IMGUI_IMAGE", "TOPLEFT", "BankFrame",
                    "TOPLEFT", 0, 0));
        string bankerName = BankFrameUiLaw.FallbackTitle;
        if (banker is not null)
        {
            if (banker.Entry != 0 && TryBeginCreatureQuery(banker.Entry))
                _net?.CreatureQuery(banker.Entry, banker.Guid);
            bankerName = BankFrameUiLaw.Title(
                _creatureNames.GetValueOrDefault(banker.Entry, ""));
        }
        GameText.DrawCentered(dl, BankFrameUiLaw.TitleFont, bankerName,
            origin + BankFrameUiLaw.TitleCenter * s, s);
        GameText.DrawCentered(dl, "GameFontNormal", "Item Slots",
            origin + BankFrameUiLaw.ItemSlotsLabelCenter * s, s);
        GameText.DrawCentered(dl, "GameFontNormal", "Bag Slots",
            origin + BankFrameUiLaw.BagSlotsLabelCenter * s, s);
        DrawTrainerMoney(dl, player.Fields.Coinage,
            origin + BankFrameUiLaw.PurseRightTop * s, s, 0xffffffff, rightAligned: true);
        DrawVanillaBankSlots(dl, origin, s, player);
        Vector2 close = origin + BankFrameUiLaw.CloseButton * s;
        DrawImageButton(dl, "##bank-close", close, BankFrameUiLaw.CloseButtonSize * s,
            @"Interface\Buttons\UI-Panel-MinimizeButton-Up",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Down",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");
        if (ImGui.IsItemClicked()) CloseBankSession();
        if (_uiParityArmed && _uiParityPanel == "bank") MarkUiParityFrameComplete();
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
            Vector2 min = origin + BankFrameUiLaw.ItemSlotMin(i) * s;
            uint icon = item is null ? 0 : _gameplayArt?.Handle(item.IconPath) ?? 0;
            if (icon != 0)
                dl.AddImage((nint)icon, min, min + BankFrameUiLaw.ItemSlotSize * s);
            uint ring = _gameplayArt?.Handle(@"Interface\Buttons\UI-Quickslot2") ?? 0;
            if (ring != 0)
                dl.AddImage((nint)ring, min + BankFrameUiLaw.SlotRingMinOffset * s,
                    min + BankFrameUiLaw.SlotRingMaxOffset * s);
            ImGui.SetCursorScreenPos(min);
            ImGui.InvisibleButton($"##bank-item-{i}", BankFrameUiLaw.ItemSlotSize * s);
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
                        (int)(instance?.Fields.ItemStackCount ?? 0),
                        min + BankFrameUiLaw.ItemSlotSize * s);
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
            IReadOnlyList<string> messageLines = BankFrameUiLaw.WrapPurchaseMessage(
                BankFrameUiLaw.PurchaseMessageWidth * s,
                line => GameText.MeasureWidth("GameFontHighlight", line, s));
            float messagePitch = GameText.LinePitch("GameFontHighlight", s);
            Vector2 messageAt = origin + BankFrameUiLaw.PurchaseMessageMinimum(
                messageLines.Count, messagePitch / s) * s;
            for (int line = 0; line < messageLines.Count; line++)
                GameText.Draw(dl, "GameFontHighlight", messageLines[line],
                    messageAt + BankFrameUiLaw.PurchaseMessageLine(line,
                        messagePitch), s);
            GameText.Draw(dl, "GameFontNormal", BankFrameUiLaw.CostText,
                origin + BankFrameUiLaw.CostLabel * s, s);
            float costLabelWidth = GameText.MeasureWidth(
                "GameFontNormal", BankFrameUiLaw.CostText, s);
            DrawTrainerMoney(dl, price,
                origin + BankFrameUiLaw.CostLabel * s +
                    BankFrameUiLaw.CostMoneyOffset(costLabelWidth, s),
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
            draw.AddImage((nint)icon, min, min + BankFrameUiLaw.ItemSlotSize * scale,
                Vector2.Zero, Vector2.One, tint);
        uint ring = _gameplayArt?.Handle(purchased
            ? @"Interface\Buttons\UI-Quickslot2"
            : @"Interface\BankFrame\UI-Bank-Slot-Locked") ?? 0;
        if (ring != 0)
            draw.AddImage((nint)ring, min + BankFrameUiLaw.SlotRingMinOffset * scale,
                min + BankFrameUiLaw.SlotRingMaxOffset * scale);

        ImGui.SetCursorScreenPos(min);
        ImGui.InvisibleButton($"##bank-bag-{index}", BankFrameUiLaw.ItemSlotSize * scale);
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
                draw.AddImage((nint)checkedArt, min,
                    min + BankFrameUiLaw.ItemSlotSize * scale);
        }
        if (ImGui.IsItemHovered())
        {
            string text = template?.Name ?? (purchased ? "Bag Slot" : "Purchasable Bag Slot");
            BankFrameUiLaw.TooltipSeat tooltipSeat =
                BankFrameUiLaw.BankBagTooltipSeat(min, scale);
            OfferPreservedSharedGameTooltipRenderer(new("bank-bag-button", (ulong)index), () =>
            {
                ImGui.SetNextWindowPos(tooltipSeat.Anchor, ImGuiCond.Always,
                    tooltipSeat.Pivot);
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
        Vector2 size = BankPurchaseConfirmUiLaw.ButtonSize * scale;
        ImGui.SetCursorScreenPos(min);
        bool clicked = ImGui.InvisibleButton($"##bank-purchase-confirm-{buttonIndex}", size);
        bool pressed = ImGui.IsItemActive();
        bool hovered = ImGui.IsItemHovered();
        uint art = _skin!.TextureHandle(pressed ? "dialog.button.down" : "dialog.button.up");
        if (art != 0)
            draw.AddImage((nint)art, min, min + size, Vector2.Zero,
                BankPurchaseConfirmUiLaw.ButtonUvMax);
        if (hovered)
        {
            uint highlight = _gameplayArt?.BrightHighlightHandle(
                @"Interface\Buttons\UI-DialogBox-Button-Highlight") ?? 0;
            if (highlight != 0)
                draw.AddImage((nint)highlight, min, min + size,
                    Vector2.Zero, BankPurchaseConfirmUiLaw.ButtonUvMax);
        }
        GameText.DrawCentered(draw, hovered ? "GameFontHighlight" : "GameFontNormal",
            caption, min + size * .5f, scale);
        return clicked;
    }
}
