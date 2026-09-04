using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private readonly record struct VendorControlInput(
        bool Hovered, bool LeftReleased, bool RightReleased, bool Active);

    private readonly record struct PreparedVendorTooltipLine(
        string Left,
        Vector4 LeftColor,
        string? Right = null,
        Vector4 RightColor = default);

    private readonly record struct PreparedVendorTemplateTooltip(
        ImmutableArray<PreparedVendorTooltipLine> Lines);

    private void DrawVendorFrame()
    {
        if (_vendor is null || _gameplayArt is null) return;
        float scale = GameplayUiScale();
        Vector2 origin = new(0, 104f * scale);
        Vector2 logicalSize = new(384, 512);
        ImGui.SetNextWindowPos(origin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(logicalSize * scale, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
                                 ImGuiWindowFlags.NoSavedSettings |
                                 ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav;
        if (!ImGui.Begin("##vendor", flags))
        {
            ImGui.End();
            return;
        }

        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        if (_uiParityArmed && _uiParityPanel == "merchant")
        {
            BeginUiParityFrame(origin, scale);
            CollectUiParityDraw("MerchantFrame", "Frame", origin, logicalSize * scale, "",
                new("", 0, "IMGUI_HOST", "ANCHOR:ABSOLUTE", "", "", 0, 8));
        }

        // [SUI] P4b: follow possession exactly as the inventory panel does — while
        // driving a party bot, the merchant page, buyback and purse are the BOT's,
        // so what you see and sell matches the server-side vendor redirect. When not
        // possessing, ControlledGuid IS your own guid, so the main is unchanged.
        WorldEntity? player = _net is not null &&
                              _entities.TryGet(ControlledGuid, out WorldEntity foundPlayer)
            ? foundPlayer : null;
        DrawVendorPortrait(draw, origin, scale);
        DrawVendorShell(draw, origin, scale);
        if (_vendorTab == 1) DrawVendorBuybackOverlay(draw, origin, scale);
        else DrawVendorBottomBorder(draw, origin, scale);
        DrawVendorTitle(draw, origin, scale);

        _vendorHoveredRow = -1;
        if (player is not null)
        {
            if (_vendorTab == 0) DrawVendorMerchantPage(draw, origin, scale, player);
            else DrawVendorBuybackPage(draw, origin, scale, player);
            DrawVendorPurse(draw, origin, scale, player.Fields.Coinage);
        }

        DrawVendorTabsAndClose(draw, origin, scale);
        if (_vendorRepairMode) DrawBagHoverCursor("Repair");
        if (ImGui.IsMouseReleased(ImGuiMouseButton.Left)) _vendorLeftPressedRow = -1;
        if (ImGui.IsMouseReleased(ImGuiMouseButton.Right)) _vendorRightPressedRow = -1;
        if (_uiParityArmed && _uiParityPanel == "merchant") MarkUiParityFrameComplete();
        ImGui.End();
    }

    private void DrawVendorShell(ImDrawListPtr draw, Vector2 origin, float scale)
    {
        (string Element, string Path, Vector2 Offset, Vector2 Size)[] art =
        [
            ("MerchantFrame/Texture", @"Interface\MerchantFrame\UI-Merchant-TopLeft",
                Vector2.Zero, new(256, 256)),
            ("MerchantFrame/Texture#2", @"Interface\MerchantFrame\UI-Merchant-TopRight",
                new(256, 0), new(128, 256)),
            ("MerchantFrame/Texture#3", @"Interface\MerchantFrame\UI-Merchant-BotLeft",
                new(0, 256), new(256, 256)),
            ("MerchantFrame/Texture#4", @"Interface\MerchantFrame\UI-Merchant-BotRight",
                new(256, 256), new(128, 256)),
        ];
        foreach (var part in art)
        {
            Vector2 minimum = origin + part.Offset * scale;
            DrawArt(draw, part.Path, minimum, part.Size, scale);
            if (_uiParityArmed && _uiParityPanel == "merchant")
                CollectUiParityDraw(part.Element, "Texture", minimum, part.Size * scale,
                    "MerchantFrame", new(part.Path, 0xffffffff, "IMGUI_IMAGE", "TOPLEFT",
                        "MerchantFrame", "TOPLEFT", part.Offset.X, -part.Offset.Y));
        }
    }

    private void DrawVendorPortrait(ImDrawListPtr draw, Vector2 origin, float scale)
    {
        Vector2 minimum = origin + new Vector2(7, 6) * scale;
        if (_vendorTab == 1)
        {
            uint buyback = _gameplayArt?.Handle(
                @"Interface\MerchantFrame\UI-BuyBack-Icon") ?? 0;
            if (buyback != 0)
                draw.AddImage((nint)buyback, minimum, minimum + new Vector2(60) * scale);
            return;
        }
        if (_vendor is not null &&
            _entities.TryGet(_vendor.VendorGuid, out WorldEntity vendor))
            DrawUnitPortraitImage(draw, vendor, minimum, 60f * scale, 0, false);
    }

    private void DrawVendorTitle(ImDrawListPtr draw, Vector2 origin, float scale)
    {
        string title = "Merchant";
        if (_vendorTab == 1) title = "Merchant Buyback";
        else if (_vendor is not null && _entities.TryGet(_vendor.VendorGuid, out WorldEntity vendor))
        {
            if (vendor.Entry != 0 && TryBeginCreatureQuery(vendor.Entry))
                _net?.CreatureQuery(vendor.Entry, vendor.Guid);
            title = _creatureNames.GetValueOrDefault(vendor.Entry, "Merchant");
            if (string.IsNullOrWhiteSpace(title)) title = "Merchant";
        }
        float em = GameText.EmPixels("GameFontNormal", scale);
        DrawNpcModalTitle(draw, title,
            origin + new Vector2(192, 17 + em / (2f * scale)) * scale, scale);
    }

    private void DrawVendorBuybackOverlay(ImDrawListPtr draw, Vector2 origin, float scale)
    {
        (string Path, Vector2 Offset, Vector2 Size)[] pieces =
        [
            (@"Interface\MerchantFrame\UI-BuyBack-TopLeft", new(19, 71), new(256, 256)),
            (@"Interface\MerchantFrame\UI-BuyBack-TopRight", new(275, 71), new(64, 256)),
            (@"Interface\MerchantFrame\UI-BuyBack-BotLeft", new(19, 327), new(256, 128)),
            (@"Interface\MerchantFrame\UI-BuyBack-BotRight", new(275, 327), new(64, 128)),
        ];
        foreach (var piece in pieces)
            DrawArt(draw, piece.Path, origin + piece.Offset * scale, piece.Size, scale);
    }

    private void DrawVendorBottomBorder(ImDrawListPtr draw, Vector2 origin, float scale)
    {
        uint texture = _gameplayArt?.Handle(
            @"Interface\MerchantFrame\UI-Merchant-BottomBorder") ?? 0;
        if (texture == 0) return;
        Vector2 left = origin + new Vector2(14, 366) * scale;
        draw.AddImage((nint)texture, left, left + new Vector2(256, 61) * scale,
            Vector2.Zero, new Vector2(1, .4765625f));
        Vector2 right = left + new Vector2(256, 0) * scale;
        draw.AddImage((nint)texture, right, right + new Vector2(76, 61) * scale,
            new Vector2(0, .4765625f), new Vector2(.296875f, .953125f));
    }

    private void DrawVendorMerchantPage(ImDrawListPtr draw, Vector2 origin, float scale,
        WorldEntity player)
    {
        MerchantFrameUiLaw.MerchantPagination page =
            MerchantFrameUiLaw.MerchantPage(_vendor!.Items.Count, _vendorPage);
        _vendorPage = page.Page;
        for (int physical = 1; physical <= MerchantFrameUiLaw.MerchantItemsPerPage; physical++)
        {
            int index = page.FirstAbsoluteSlot - 1 + physical - 1;
            MerchantFrameUiLaw.ItemRowGeometry geometry =
                MerchantFrameUiLaw.MerchantItemRow(physical);
            Vector2 cell = origin + new Vector2(geometry.X, geometry.Y) * scale;
            if (index >= _vendor.Items.Count)
            {
                VendorControlInput emptyInput = VendorControl(
                    $"##vendor-empty-{physical}", physical - 1, cell,
                    new Vector2(geometry.Width, geometry.Height) * scale);
                DrawVendorRowChrome(draw, cell, scale, emptyInput.Hovered, null, null, 0,
                    0, soldOut: false, usable: true);
                continue;
            }

            VendorItem row = _vendor.Items[index];
            ItemTemplate? item = _items?.TryGet(row.ItemId, out ItemTemplate? resolved) == true
                ? resolved : null;
            string? wireIcon = _items?.IconForDisplay(row.DisplayId);
            bool itemEstablished = item is not null || wireIcon is not null;
            string? iconPath = wireIcon ?? (item is not null
                ? item.IconPath
                : null);
            if (itemEstablished && string.IsNullOrWhiteSpace(iconPath))
                iconPath = @"Interface\Icons\INV_Misc_QuestionMark.blp";
            bool soldOut = row.Available == 0;
            bool usable = VendorItemUsable(player, item);
            VendorControlInput input = VendorControl($"##vendor-{physical}", physical - 1,
                cell, new Vector2(geometry.Width, geometry.Height) * scale);
            DrawVendorRowChrome(draw, cell, scale, input.Hovered, iconPath,
                item?.Name ?? (itemEstablished ? "..." : null),
                itemEstablished ? row.BuyCount : 0,
                itemEstablished ? row.Price : 0, soldOut, usable);

            if (input.LeftReleased && ImGui.GetIO().KeyCtrl) TryOnDressUp(row.ItemId);
            if (input.RightReleased) BuyVendorEntry(row.ItemId, 1);
            if (!input.Hovered) continue;
            _vendorHoveredRow = index;
            if (item is not null)
            {
                PreparedVendorTemplateTooltip tooltip =
                    PrepareVendorTemplateTooltip(item, player);
                OfferVendorTemplateTooltip(
                    new("item:vendor-merchant-row", (ulong)(physical - 1)),
                    tooltip, cell + new Vector2(37, 0) * scale);
            }
            if (item is not null && !_vendorRepairMode)
                DrawBagHoverCursor(ImGui.GetIO().KeyCtrl ? "Inspect" :
                    player.Fields.Coinage >= row.Price ? "Buy" : "UnableBuy");
        }

        DrawVendorMerchantFooter(draw, origin, scale, player, page);
    }

    private void DrawVendorMerchantFooter(ImDrawListPtr draw, Vector2 origin, float scale,
        WorldEntity player, in MerchantFrameUiLaw.MerchantPagination page)
    {
        if (page.ControlsVisible)
        {
            if (VendorPageButton(draw, "##vendor-prev", 500,
                    origin + new Vector2(21, 340) * scale, next: false,
                    page.PreviousEnabled, scale))
            {
                _vendorPage--;
                PlayUiSound("igMainMenuOptionCheckBoxOn", "ui.vendor");
            }
            if (VendorPageButton(draw, "##vendor-next", 501,
                    origin + new Vector2(308, 340) * scale, next: true,
                    page.NextEnabled, scale))
            {
                _vendorPage++;
                PlayUiSound("igMainMenuOptionCheckBoxOn", "ui.vendor");
            }
            GameText.Draw(draw, "GameFontNormal", "Prev",
                origin + new Vector2(53, 348) * scale, scale);
            GameText.DrawRightAligned(draw, "GameFontNormal", "Next",
                origin + new Vector2(305, 348) * scale, scale);
            GameText.DrawCentered(draw, "GameFontNormal", page.PageLabel ?? "",
                origin + new Vector2(178, 362) * scale, scale);
        }

        bool canRepair = VendorCanRepair();
        if (canRepair)
        {
            uint repairCost = ComputeVendorRepairAllCost(player);
            GameText.Draw(draw, "GameFontHighlightSmall", "Repair Items",
                origin + new Vector2(26, 399) * scale, scale);
            Vector2 itemMin = origin + new Vector2(98, 385) * scale;
            Vector2 allMin = origin + new Vector2(136, 385) * scale;
            GameTooltipOwnerKey itemOwner = new("vendor-repair-item", 0);
            GameTooltipOwnerKey allOwner = new("vendor-repair-all", 0);
            if (DrawVendorRepairButton(draw, "##vendor-repair-item", 510, itemMin,
                    new Vector2(0, 0), enabled: true, scale))
                _vendorRepairMode = !_vendorRepairMode;
            if (DrawVendorRepairButton(draw, "##vendor-repair-all", 511, allMin,
                    new Vector2(.28125f, 0), repairCost > 0, scale))
            {
                RepairAllVendorItems(player);
                PlayUiSound("ITEM_REPAIR", "ui.vendor");
                GameTooltipOwnerToken current = CurrentSharedGameTooltipOwnerToken();
                if (current.IsValid && current.Owner == allOwner)
                    HideSharedGameTooltip(current);
                _vendorSuppressRepairAllTooltipUntilLeave = true;
            }
            bool itemHovered = ImGui.IsMouseHoveringRect(itemMin,
                itemMin + new Vector2(36) * scale);
            bool allHovered = ImGui.IsMouseHoveringRect(allMin,
                allMin + new Vector2(36) * scale);
            if (!allHovered) _vendorSuppressRepairAllTooltipUntilLeave = false;
            if (itemHovered)
                OfferVendorRepairTooltip(itemOwner, "Repair an Item", null,
                    itemMin + new Vector2(36, 0) * scale);
            if (allHovered && !_vendorSuppressRepairAllTooltipUntilLeave)
                OfferVendorRepairTooltip(allOwner, "Repair All Items",
                    repairCost > 0 ? repairCost : null,
                    allMin + new Vector2(36, 0) * scale);
        }
        DrawVendorRecentBuyback(draw, origin, scale, player);
    }

    private void DrawVendorBuybackPage(ImDrawListPtr draw, Vector2 origin, float scale,
        WorldEntity player)
    {
        MerchantFrameUiLaw.BuybackDescriptor[] rows = VendorBuybackRows(player);
        for (int ordinal = 1; ordinal <= MerchantFrameUiLaw.BuybackItemCount; ordinal++)
        {
            MerchantFrameUiLaw.ItemRowGeometry geometry =
                MerchantFrameUiLaw.BuybackItemRow(ordinal);
            Vector2 cell = origin + new Vector2(geometry.X, geometry.Y) * scale;
            int key = 100 + ordinal - 1;
            VendorControlInput input = VendorControl($"##buyback-{ordinal}", key, cell,
                new Vector2(geometry.Width, geometry.Height) * scale);
            if (ordinal > rows.Length)
            {
                DrawVendorRowChrome(draw, cell, scale, input.Hovered, null, null, 0, 0,
                    soldOut: false, usable: true);
                continue;
            }
            DrawVendorBuybackRow(draw, cell, scale, player, rows[ordinal - 1], ordinal - 1,
                input, recent: false);
        }
    }

    private void DrawVendorRecentBuyback(ImDrawListPtr draw, Vector2 origin, float scale,
        WorldEntity player)
    {
        MerchantFrameUiLaw.BuybackDescriptor[] rows = VendorBuybackRows(player);
        Vector2 cell = origin + new Vector2(189, 385) * scale;
        VendorControlInput input = VendorControl("##vendor-recent-buyback", 200, cell,
            new Vector2(153, 37) * scale, rightButton: false);
        if (rows.Length == 0)
        {
            DrawVendorRowChrome(draw, cell, scale, input.Hovered, null, null, 0, 0,
                soldOut: false, usable: true, rowHeight: 37f, compactPlate: true);
            return;
        }
        DrawVendorBuybackRow(draw, cell, scale, player, rows[^1], 0, input,
            recent: true);
    }

    private void DrawVendorBuybackRow(ImDrawListPtr draw, Vector2 cell, float scale,
        WorldEntity player, in MerchantFrameUiLaw.BuybackDescriptor descriptor,
        int visibleOrdinal, in VendorControlInput input, bool recent)
    {
        WorldEntity? instance = _entities.TryGet(descriptor.ItemGuid, out WorldEntity resolved)
            ? resolved : null;
        if (instance is not null) _items?.Require(instance.Entry, instance.Guid, _net!);
        ItemTemplate? item = instance is not null &&
                             _items?.TryGet(instance.Entry, out ItemTemplate? found) == true
            ? found : null;
        string? iconPath = item?.IconPath;
        DrawVendorRowChrome(draw, cell, scale, input.Hovered, iconPath,
            item?.Name,
            item is null ? 0 : instance?.Fields.ItemStackCount ?? 0,
            item is null ? 0 : descriptor.Price,
            soldOut: false, usable: recent || VendorItemUsable(player, item),
            recent ? 37f : 44f, recent);
        bool clicked = recent ? input.LeftReleased : input.LeftReleased || input.RightReleased;
        if (clicked && !RefuseTacticalFreezeLiveCommand("buying back an item") &&
            !RefuseTacticalFrozenActor(_vendor!.VendorGuid, "buy back an item from it"))
            _net?.BuybackItem(_vendor!.VendorGuid, descriptor.WireInventorySlot);
        if (!input.Hovered || item is null) return;

        PreparedVendorTemplateTooltip tooltip = PrepareVendorTemplateTooltip(item, player);
        string surface = recent ? "item:vendor-recent-buyback" : "item:vendor-buyback-row";
        OfferVendorTemplateTooltip(new(surface, (ulong)visibleOrdinal), tooltip,
            cell + new Vector2(37, 0) * scale);
        if (!_vendorRepairMode)
            DrawBagHoverCursor(player.Fields.Coinage >= descriptor.Price
                ? "Buy"
                : "UnableBuy");
    }

    private MerchantFrameUiLaw.BuybackDescriptor[] VendorBuybackRows(WorldEntity player)
    {
        MerchantFrameUiLaw.BuybackFieldValue[] fields = Enumerable.Range(0, 12)
            .Select(index => new MerchantFrameUiLaw.BuybackFieldValue(
                player.Fields.PlayerBuybackSlot(index),
                player.Fields.PlayerBuybackPrice(index),
                player.Fields.PlayerBuybackTimestamp(index)))
            .ToArray();
        return MerchantFrameUiLaw.OrderBuyback(fields);
    }

    private void DrawVendorRowChrome(ImDrawListPtr draw, Vector2 cell, float scale,
        bool hovered, string? iconPath, string? name, uint count, uint price,
        bool soldOut, bool usable, float rowHeight = 44f, bool compactPlate = false)
    {
        bool occupied = iconPath is not null || name is not null;
        Vector4 plateColor = occupied
            ? soldOut && !usable ? new(.5f, 0, 0, 1)
            : soldOut ? new(.5f, .5f, .5f, 1)
            : !usable ? new(1, 0, 0, 1)
            : new(.5f, .5f, .5f, 1)
            : new(.5f, .5f, .5f, 1);
        Vector4 socketColor = !occupied ? new(.4f, .4f, .4f, 1)
            : soldOut && !usable ? new(.5f, 0, 0, 1)
            : soldOut ? new(.5f, .5f, .5f, 1)
            : !usable ? new(1, 0, 0, 1) : Vector4.One;
        Vector4 iconColor = soldOut && !usable ? new(.5f, 0, 0, 1)
            : soldOut ? new(.5f, .5f, .5f, 1)
            : !usable ? new(.9f, 0, 0, 1) : Vector4.One;

        uint socket = _gameplayArt?.Handle(@"Interface\Buttons\UI-EmptySlot") ?? 0;
        if (socket != 0)
            draw.AddImage((nint)socket, cell - new Vector2(13) * scale,
                cell + new Vector2(51) * scale, Vector2.Zero, Vector2.One,
                ImGui.ColorConvertFloat4ToU32(socketColor));
        uint plate = _gameplayArt?.Handle(
            @"Interface\MerchantFrame\UI-Merchant-LabelSlots") ?? 0;
        Vector2 plateMin = cell + new Vector2(42, compactPlate ? -3 : -2) * scale;
        if (plate != 0)
            draw.AddImage((nint)plate, plateMin,
                plateMin + new Vector2(128, compactPlate ? 64 : 78) * scale,
                Vector2.Zero, Vector2.One, ImGui.ColorConvertFloat4ToU32(plateColor));
        if (iconPath is not null)
        {
            uint icon = _gameplayArt?.Handle(iconPath) ?? 0;
            if (icon != 0)
                draw.AddImage((nint)icon, cell, cell + new Vector2(37) * scale,
                    Vector2.Zero, Vector2.One, ImGui.ColorConvertFloat4ToU32(iconColor));
        }
        if (hovered)
        {
            uint highlight = _gameplayArt?.AdditiveHandle(
                @"Interface\Buttons\ButtonHilight-Square") ?? 0;
            if (highlight != 0)
                draw.AddImage((nint)highlight, cell, cell + new Vector2(37) * scale);
        }
        if (!occupied) return;

        int line = 0;
        foreach (string text in WrapTooltipText(name ?? "...", "GameFontNormalSmall",
                     scale, (compactPlate ? 90f : 100f) * scale).Take(2))
        {
            GameText.Draw(draw, "GameFontNormalSmall", text,
                cell + new Vector2(46, 2 + line * 10) * scale, scale);
            line++;
        }
        if (count > 1)
            GameText.DrawRightAligned(draw, "NumberFontNormal", count.ToString(),
                cell + new Vector2(32, 37f - 2 -
                    GameText.EmPixels("NumberFontNormal", scale) / scale) * scale, scale);
        DrawVendorMoney(draw, cell, scale, price, MerchantFrameUiLaw.MoneyPlacement.MerchantRow,
            rowHeight, compactPlate ? 8f : MerchantFrameUiLaw.RowMoneyBottom);
    }

    private void DrawVendorPurse(ImDrawListPtr draw, Vector2 origin, float scale, uint copper)
        => DrawVendorMoney(draw, origin, scale, copper,
            MerchantFrameUiLaw.MoneyPlacement.MerchantPurse, 512f,
            MerchantFrameUiLaw.PurseMoneyBottom);

    private void DrawVendorMoney(ImDrawListPtr draw, Vector2 origin, float scale, uint copper,
        MerchantFrameUiLaw.MoneyPlacement placement, float containingHeight,
        float bottomOffset)
    {
        MerchantFrameUiLaw.MoneyDigitAdvances advances = new(
            DigitAdvance(0), DigitAdvance(1), DigitAdvance(2), DigitAdvance(3),
            DigitAdvance(4), DigitAdvance(5), DigitAdvance(6), DigitAdvance(7),
            DigitAdvance(8), DigitAdvance(9));
        float DigitAdvance(int digit) => GameText.MeasureWidth(
            MerchantFrameUiLaw.MoneyFontObject, digit.ToString(), scale) / scale;
        MerchantFrameUiLaw.MoneyLayout layout = placement ==
            MerchantFrameUiLaw.MoneyPlacement.MerchantRow
            ? MerchantFrameUiLaw.MerchantRowMoney(copper, advances)
            : MerchantFrameUiLaw.MerchantPurseMoney(copper, advances);
        uint texture = _gameplayArt?.Handle(MerchantFrameUiLaw.MoneyTexturePath) ?? 0;
        float top = placement == MerchantFrameUiLaw.MoneyPlacement.MerchantRow
            ? containingHeight - bottomOffset - MerchantFrameUiLaw.MoneyIconSize * .5f
            : containingHeight - bottomOffset - MerchantFrameUiLaw.MoneyIconSize;
        foreach (MerchantFrameUiLaw.MoneyCellGeometry coin in layout.VisibleCells)
        {
            Vector2 iconMin = origin + new Vector2(coin.IconLeft, top) * scale;
            if (texture != 0)
                draw.AddImage((nint)texture, iconMin,
                    iconMin + new Vector2(MerchantFrameUiLaw.MoneyIconSize) * scale,
                    new Vector2(coin.TexCoords.Left, coin.TexCoords.Top),
                    new Vector2(coin.TexCoords.Right, coin.TexCoords.Bottom));
            string text = coin.Value.ToString();
            float numberTop = GameText.BoxCenteredTop(MerchantFrameUiLaw.MoneyFontObject,
                origin.Y + top * scale, MerchantFrameUiLaw.MoneyIconSize, scale);
            GameText.DrawRightAligned(draw, MerchantFrameUiLaw.MoneyFontObject, text,
                new Vector2(iconMin.X, numberTop), scale);
        }
    }

    private static PreparedVendorTemplateTooltip PrepareVendorTemplateTooltip(
        ItemTemplate item,
        WorldEntity player)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(player);
        var lines = ImmutableArray.CreateBuilder<PreparedVendorTooltipLine>();
        Vector4 white = Vector4.One;
        Vector4 red = new(1f, 32f / 255f, 32f / 255f, 1f);
        Vector4 gold = new(1f, 210f / 255f, 0f, 1f);
        void Add(string text, Vector4? color = null) =>
            lines.Add(new(text, color ?? white));
        void AddPair(string left, string right) =>
            lines.Add(new(left, white, right, white));

        Add(item.Name, ItemTooltipQualityColor(item.Quality));
        switch (item.Bonding)
        {
            case 1: Add("Binds when picked up"); break;
            case 2: Add("Binds when equipped"); break;
            case 3: Add("Binds when used"); break;
            case 4:
            case 5: Add("Quest Item"); break;
        }
        if (item.MaxCount == 1) Add("Unique");
        else if (item.MaxCount > 1) Add($"Unique ({item.MaxCount})");
        if (item.StartQuest != 0) Add("This Item Begins a Quest");

        if (item.ContainerSlots > 0)
        {
            Add($"{item.ContainerSlots} Slot Bag");
        }
        else
        {
            string? slot = VendorInventoryTypeName(item.InventoryType);
            string? type = item.InventoryType == 16
                ? null
                : VendorSubclassName(item.Class, item.Subclass);
            if (slot is not null && type is not null) AddPair(slot, type);
            else if (slot is not null) Add(slot);
            else if (type is not null) Add(type);
        }

        ItemDamage[] damages = item.Damages
            .Where(static damage => damage.Max > 0f)
            .ToArray();
        if (damages.Length > 0)
        {
            ItemDamage first = damages[0];
            string damage = VendorDamageText(first, extra: false);
            double speed = item.DelayMs / 1000d;
            if (speed > 0d) AddPair(damage, $"Speed {speed.ToString("0.00", CultureInfo.InvariantCulture)}");
            else Add(damage);
            foreach (ItemDamage extra in damages.Skip(1))
                Add(VendorDamageText(extra, extra: true));
            if (speed > 0d && item.Class == MerchantFrameUiLaw.WeaponItemClass)
            {
                double average = damages.Sum(static damage =>
                    ((double)damage.Min + damage.Max) * .5d);
                Add($"({(average / speed).ToString("0.0", CultureInfo.InvariantCulture)} damage per second)");
            }
        }
        if (item.Armor > 0) Add($"{item.Armor} Armor");
        if (item.Block > 0) Add($"{item.Block} Block");

        uint[] statOrder = [4, 3, 7, 5, 6, 1, 0];
        foreach (uint wanted in statOrder)
            foreach (ItemStat stat in item.Stats)
            {
                if (stat.Type != wanted || stat.Value == 0 ||
                    VendorStatName(stat.Type) is not string name)
                    continue;
                Add($"{(stat.Value > 0 ? "+" : "-")}{Math.Abs((long)stat.Value)} {name}");
            }

        uint firstResistance = item.Resistances.Length == 0 ? 0 : item.Resistances[0];
        if (firstResistance != 0 && item.Resistances.All(value => value == firstResistance))
        {
            Add($"+{firstResistance} to All Resistances");
        }
        else
        {
            string[] resistanceNames = ["Holy", "Fire", "Nature", "Frost", "Shadow", "Arcane"];
            for (int i = 1; i < Math.Min(item.Resistances.Length, resistanceNames.Length); i++)
                if (item.Resistances[i] != 0)
                    Add($"+{item.Resistances[i]} {resistanceNames[i]} Resistance");
        }

        (byte race, byte @class, _, _) = player.Fields.Bytes0;
        AddVendorMaskLine(lines, "Classes", item.AllowableClass,
            [(1, "Warrior"), (2, "Paladin"), (3, "Hunter"), (4, "Rogue"),
             (5, "Priest"), (7, "Shaman"), (8, "Mage"), (9, "Warlock"),
             (11, "Druid")], @class, red);
        AddVendorMaskLine(lines, "Races", item.AllowableRace,
            [(1, "Human"), (2, "Orc"), (3, "Dwarf"), (4, "Night Elf"),
             (5, "Undead"), (6, "Tauren"), (7, "Gnome"), (8, "Troll")],
            race, red);
        if (item.RequiredLevel > 1)
            Add($"Requires Level {item.RequiredLevel}",
                player.Level >= item.RequiredLevel ? white : red);
        if (!string.IsNullOrEmpty(item.Description))
            Add($"\"{item.Description}\"", gold);

        return new(lines.ToImmutable());
    }

    private static void AddVendorMaskLine(
        ImmutableArray<PreparedVendorTooltipLine>.Builder lines,
        string label,
        int mask,
        (int Id, string Name)[] values,
        int playerId,
        Vector4 red)
    {
        int fullMask = values.Aggregate(0, static (current, value) =>
            current | 1 << (value.Id - 1));
        if (mask <= 0 || mask == fullMask) return;
        string[] names = values
            .Where(value => (mask & 1 << (value.Id - 1)) != 0)
            .Select(static value => value.Name)
            .ToArray();
        if (names.Length == 0) return;
        bool allowed = playerId > 0 && (mask & 1 << (playerId - 1)) != 0;
        lines.Add(new($"{label}: {string.Join(", ", names)}",
            allowed ? Vector4.One : red));
    }

    private static string VendorDamageText(in ItemDamage damage, bool extra)
    {
        long minimum = (long)Math.Floor(damage.Min + .5f);
        long maximum = (long)Math.Floor(damage.Max + .5f);
        string school = VendorDamageSchoolName(damage.School) is string name
            ? $" {name}"
            : "";
        return $"{(extra ? "+ " : "")}{minimum} - {maximum}{school} Damage";
    }

    private static string? VendorInventoryTypeName(uint type) => type switch
    {
        1 => "Head", 2 => "Neck", 3 => "Shoulder", 4 => "Shirt",
        5 or 20 => "Chest", 6 => "Waist", 7 => "Legs", 8 => "Feet",
        9 => "Wrist", 10 => "Hands", 11 => "Finger", 12 => "Trinket",
        13 => "One-Hand", 14 or 22 => "Off Hand", 15 or 26 => "Ranged",
        16 => "Back", 17 => "Two-Hand", 19 => "Tabard", 21 => "Main Hand",
        23 => "Held In Off-hand", 24 => "Projectile", 25 => "Thrown",
        28 => "Relic", _ => null,
    };

    private static string? VendorSubclassName(uint itemClass, uint subclass) =>
        (itemClass, subclass) switch
        {
            (2, 0) or (2, 1) => "Axe", (2, 2) => "Bow", (2, 3) => "Gun",
            (2, 4) or (2, 5) => "Mace", (2, 6) => "Polearm",
            (2, 7) or (2, 8) => "Sword", (2, 10) => "Staff",
            (2, 13) => "Fist Weapon", (2, 15) => "Dagger", (2, 16) => "Thrown",
            (2, 17) => "Spear", (2, 18) => "Crossbow", (2, 19) => "Wand",
            (2, 20) => "Fishing Pole", (4, 1) => "Cloth", (4, 2) => "Leather",
            (4, 3) => "Mail", (4, 4) => "Plate", (4, 6) => "Shield",
            (6, 2) => "Arrow", (6, 3) => "Bullet", _ => null,
        };

    private static string? VendorDamageSchoolName(uint school) => school switch
    {
        1 => "Holy", 2 => "Fire", 3 => "Nature", 4 => "Frost",
        5 => "Shadow", 6 => "Arcane", _ => null,
    };

    private static string? VendorStatName(uint type) => type switch
    {
        0 => "Mana", 1 => "Health", 3 => "Agility", 4 => "Strength",
        5 => "Intellect", 6 => "Spirit", 7 => "Stamina", _ => null,
    };

    private bool OfferVendorTemplateTooltip(
        in GameTooltipOwnerKey owner,
        in PreparedVendorTemplateTooltip tooltip,
        Vector2 ownerTopRight)
    {
        if (tooltip.Lines.IsDefault)
            throw new ArgumentException("The prepared vendor tooltip is uninitialized.",
                nameof(tooltip));

        var operations = ImmutableArray.CreateBuilder<PreparedItemTooltipPaintOp>(
            tooltip.Lines.Length);
        foreach (PreparedVendorTooltipLine line in tooltip.Lines)
            operations.Add(new(
                line.Right is null
                    ? PreparedItemTooltipPaintKind.Colored
                    : PreparedItemTooltipPaintKind.Paired,
                line.Left, line.LeftColor, line.Right, line.RightColor));

        ItemTooltipBodySnapshot body = new(operations.ToImmutable());
        PreparedInventoryTooltipRenderer? renderer = PrepareInventoryItemTooltipRenderer(
            body, ownerTopRight, new Vector2(0, 1));
        if (renderer is null) return false;
        return OfferPreservedSharedGameTooltipRenderer(owner,
            () => DrawPreparedInventoryItemTooltip(renderer));
    }

    private bool VendorItemUsable(WorldEntity player, ItemTemplate? item)
    {
        if (item is null) return true;
        (byte race, byte @class, _, _) = player.Fields.Bytes0;
        if (item.RequiredLevel > player.Level) return false;
        if (item.AllowableClass != -1 && @class is > 0 and <= 32 &&
            (unchecked((uint)item.AllowableClass) & (1u << (@class - 1))) == 0) return false;
        if (item.AllowableRace != -1 && race is > 0 and <= 32 &&
            (unchecked((uint)item.AllowableRace) & (1u << (race - 1))) == 0) return false;
        if (item.RequiredSpell != 0 && !_actions.KnownSpells.Contains(item.RequiredSpell))
            return false;
        if (item.RequiredSkill != 0 &&
            (!GetSkillValue(item.RequiredSkill, out ushort value, out _) ||
             value < item.RequiredSkillRank)) return false;
        if (!InventoryUiLaw.IsItemProficient(item.Class, item.Subclass, _itemProficiencies))
            return false;
        return true;
    }

    private VendorControlInput VendorControl(string id, int key, Vector2 minimum, Vector2 size,
        bool enabled = true,
        bool rightButton = true)
    {
        ImGui.SetCursorScreenPos(minimum);
        ImGuiButtonFlags buttons = ImGuiButtonFlags.MouseButtonLeft |
            (rightButton ? ImGuiButtonFlags.MouseButtonRight : ImGuiButtonFlags.None);
        ImGui.InvisibleButton(id, size, buttons);
        bool hovered = enabled && ImGui.IsItemHovered();
        if (!enabled) return default;
        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            _vendorLeftPressedRow = key;
        if (rightButton && hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            _vendorRightPressedRow = key;
        return new(hovered,
            hovered && ImGui.IsMouseReleased(ImGuiMouseButton.Left) &&
            _vendorLeftPressedRow == key,
            rightButton && hovered && ImGui.IsMouseReleased(ImGuiMouseButton.Right) &&
            _vendorRightPressedRow == key,
            ImGui.IsItemActive());
    }

    private bool VendorPageButton(ImDrawListPtr draw, string id, int key, Vector2 minimum,
        bool next, bool enabled, float scale)
    {
        VendorControlInput input = VendorControl(id, key, minimum, new Vector2(32) * scale,
            enabled, rightButton: false);
        string stem = next ? "Next" : "Prev";
        string path = !enabled
            ? $@"Interface\Buttons\UI-SpellbookIcon-{stem}Page-Disabled"
            : input.Active
            ? $@"Interface\Buttons\UI-SpellbookIcon-{stem}Page-Down"
            : $@"Interface\Buttons\UI-SpellbookIcon-{stem}Page-Up";
        uint background = _gameplayArt?.Handle(
            @"Interface\Buttons\UI-PageButton-Background") ?? 0;
        if (background != 0)
        {
            Vector2 backgroundMinimum = minimum + new Vector2(0, -1) * scale;
            draw.AddImage((nint)background, backgroundMinimum,
                backgroundMinimum + new Vector2(32) * scale);
        }
        uint art = _gameplayArt?.Handle(path) ?? 0;
        if (art != 0) draw.AddImage((nint)art, minimum, minimum + new Vector2(32) * scale);
        if (input.Hovered)
        {
            uint hi = _gameplayArt?.AdditiveHandle(
                @"Interface\Buttons\UI-Common-MouseHilight") ?? 0;
            if (hi != 0) draw.AddImage((nint)hi, minimum, minimum + new Vector2(32) * scale);
        }
        return input.LeftReleased;
    }

    private bool DrawVendorRepairButton(ImDrawListPtr draw, string id, int key,
        Vector2 minimum, Vector2 uvMinimum, bool enabled, float scale)
    {
        VendorControlInput input = VendorControl(id, key, minimum, new Vector2(36) * scale,
            enabled, rightButton: false);
        uint art = _gameplayArt?.Handle(
            @"Interface\MerchantFrame\UI-Merchant-RepairIcons") ?? 0;
        Vector2 uvMaximum = uvMinimum + new Vector2(.28125f, .5625f);
        if (art != 0)
            draw.AddImage((nint)art, minimum, minimum + new Vector2(36) * scale,
                uvMinimum, uvMaximum, enabled ? 0xffffffff : 0xff808080);
        if (input.Active)
        {
            uint depress = _gameplayArt?.Handle(
                @"Interface\Buttons\UI-Quickslot-Depress") ?? 0;
            if (depress != 0)
                draw.AddImage((nint)depress, minimum, minimum + new Vector2(36) * scale);
        }
        if (input.Hovered)
        {
            uint hi = _gameplayArt?.AdditiveHandle(
                @"Interface\Buttons\ButtonHilight-Square") ?? 0;
            if (hi != 0)
                draw.AddImage((nint)hi, minimum, minimum + new Vector2(36) * scale);
        }
        return enabled && input.LeftReleased;
    }

    private void DrawVendorTabsAndClose(ImDrawListPtr draw, Vector2 origin, float scale)
    {
        float merchantWidth = VanillaCharacterTabWidth("Merchant", scale, 0);
        float buybackWidth = VanillaCharacterTabWidth("Buyback", scale, 0);
        float merchantX = 60 - merchantWidth * .5f;
        if (VanillaTab(draw, "##vendor-tab",
                origin + new Vector2(merchantX, 450) * scale,
                "Merchant", merchantWidth, scale, _vendorTab == 0))
            _vendorTab = 0;
        if (VanillaTab(draw, "##buyback-tab",
                origin + new Vector2(merchantX + merchantWidth - 16, 450) * scale,
                "Buyback", buybackWidth, scale, _vendorTab == 1))
            _vendorTab = 1;

        Vector2 close = origin + new Vector2(322, 8) * scale;
        VendorControlInput input = VendorControl("##vendor-close", 600, close,
            new Vector2(32) * scale, rightButton: false);
        string closeArt = input.Active
            ? @"Interface\Buttons\UI-Panel-MinimizeButton-Down"
            : @"Interface\Buttons\UI-Panel-MinimizeButton-Up";
        uint texture = _gameplayArt?.Handle(closeArt) ?? 0;
        if (texture != 0)
            draw.AddImage((nint)texture, close, close + new Vector2(32) * scale);
        if (input.Hovered)
        {
            uint hi = _gameplayArt?.AdditiveHandle(
                @"Interface\Buttons\UI-Panel-MinimizeButton-Highlight") ?? 0;
            if (hi != 0) draw.AddImage((nint)hi, close, close + new Vector2(32) * scale);
        }
        if (input.LeftReleased) CloseVendorSession();
    }

    private bool OfferVendorRepairTooltip(in GameTooltipOwnerKey owner, string text,
        uint? money, Vector2 ownerTopRight)
    {
        if (!_sharedTooltipFrameOpen || _sharedTooltipFrameResolved || _skin is null) return false;
        GameTooltipOwnerToken token = ClaimSharedGameTooltip(owner);
        if (!PublishSharedGameTooltip(token,
                new GameTooltipContent(GameTooltipAnchorKind.OwnerRight,
                    [new(text, GameTooltipTextTone.White)]))) return false;
        if (money is uint copper && !SetSharedGameTooltipMoney(token, copper)) return false;
        PreparedSharedGameTooltipRenderer? prepared =
            PrepareSharedGameTooltipRenderer(SharedGameTooltipSnapshot(), ownerTopRight);
        return prepared is not null && QueueSharedGameTooltipRenderer(token,
            SharedGameTooltipLeavePolicy.ImmediateHide,
            () => DrawPreparedSharedGameTooltip(prepared));
    }
}
