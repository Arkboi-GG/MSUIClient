namespace MSUIClient.Engine.UI;

/// <summary>
/// Pure build-5875 MerchantFrame laws. This file describes frozen indexing, layout, buyback,
/// money, and repair arithmetic only; it does not wire a panel, cursor, packet, DBC loader, or
/// live merchant state.
/// </summary>
public static class MerchantFrameUiLaw
{
    public const int MerchantItemsPerPage = 10;
    public const int BuybackItemCount = 12;
    public const bool BuybackIsPaged = false;

    public const int PlayerFieldVendorBuybackSlot1 = 624;
    public const int PlayerFieldBuybackPrice1 = 1226;
    public const int PlayerFieldBuybackTimestamp1 = 1238;
    public const int BuybackInventorySlotFirst = 69;
    public const int BuybackGuidFieldStride = 2;

    public const float MerchantFrameWidth = 384f;
    public const float ItemFirstX = 24f;
    public const float ItemSecondX = 189f;
    public const float ItemFirstY = 80f;
    public const float MerchantRowStep = 52f;
    public const float BuybackRowStep = 59f;
    public const float ItemHitWidth = 153f;
    public const float ItemHitHeight = 44f;

    public const string MoneyFontObject = "NumberFontNormal";
    public const string MoneyTexturePath = @"Interface\MoneyFrame\UI-MoneyIcons";
    public const float MoneyIconSize = 13f;
    public const float MoneySlotGap = 4f;
    public const float RowMoneyFirstLeft = 46f;
    public const float RowMoneyBottom = 6f;
    public const float PurseMoneyFirstRightOffset = -53f;
    public const float PurseMoneyBottom = 67f;

    public const uint RepairVendorNpcFlag = 0x4000;
    public const uint WeaponItemClass = 2;
    public const uint ArmorItemClass = 4;
    public const int WeaponRepairSubclassCount = 21;
    public const int ArmorRepairSubclassCount = 8;
    public const int ArmorRepairCostVectorOffset = 21;

    public readonly record struct MerchantPagination(
        int Page,
        int PageCount,
        int FirstAbsoluteSlot,
        int VisibleItemCount,
        bool ControlsVisible,
        bool PreviousEnabled,
        bool NextEnabled,
        string? PageLabel);

    /// <summary>Logical coordinates are relative to MerchantFrame's top-left and grow downward.</summary>
    public readonly record struct ItemRowGeometry(float X, float Y, float Width, float Height);

    public readonly record struct BuybackFieldValue(
        ulong ItemGuid,
        uint Price,
        uint? Timestamp = null);

    public readonly record struct BuybackDescriptor(
        int PhysicalIndex,
        ulong ItemGuid,
        uint Price,
        uint Timestamp,
        uint WireInventorySlot);

    public enum MoneyDenomination
    {
        Gold,
        Silver,
        Copper,
    }

    public enum MoneyPlacement
    {
        MerchantRow,
        MerchantPurse,
    }

    public readonly record struct MoneyParts(uint Gold, uint Silver, uint Copper);

    public readonly record struct MoneyValue(MoneyDenomination Denomination, uint Value);

    public readonly record struct MoneyTexCoords(
        float Left,
        float Right,
        float Top,
        float Bottom);

    /// <summary>Logical NumberFontNormal advances for the ten decimal glyphs.</summary>
    public readonly record struct MoneyDigitAdvances(
        float Zero,
        float One,
        float Two,
        float Three,
        float Four,
        float Five,
        float Six,
        float Seven,
        float Eight,
        float Nine)
    {
        public float this[int digit] => digit switch
        {
            0 => Zero,
            1 => One,
            2 => Two,
            3 => Three,
            4 => Four,
            5 => Five,
            6 => Six,
            7 => Seven,
            8 => Eight,
            9 => Nine,
            _ => throw new ArgumentOutOfRangeException(nameof(digit)),
        };

        public static MoneyDigitAdvances Uniform(float advance) =>
            new(advance, advance, advance, advance, advance,
                advance, advance, advance, advance, advance);
    }

    public readonly record struct MoneyCellGeometry(
        int FrameSlot,
        MoneyDenomination Denomination,
        uint Value,
        float NumberWidth,
        float Left,
        float Right,
        float Bottom,
        MoneyTexCoords TexCoords)
    {
        public float Width => Right - Left;
        public float IconLeft => Right - MoneyIconSize;
    }

    public sealed record MoneyLayout(
        MoneyPlacement Placement,
        MoneyCellGeometry[] VisibleCells,
        int[] HiddenFrameSlots);

    public readonly record struct RepairItem(
        uint ItemClass,
        uint ItemSubclass,
        uint ItemLevel,
        uint Quality,
        uint CurrentDurability,
        uint MaximumDurability);

    /// <summary>
    /// DBC field indices are one-based after the row key in the frozen schema; CostVectorIndex is
    /// zero-based after the row key and is suitable for an already-decoded cost vector.
    /// </summary>
    public readonly record struct RepairTableLookup(
        uint DurabilityCostsRowKey,
        int DurabilityCostsFieldIndex,
        int CostVectorIndex,
        uint DurabilityQualityRowKey);

    public static int MerchantPageCount(int itemCount) =>
        Math.Max(1, (Math.Max(0, itemCount) + MerchantItemsPerPage - 1) /
                    MerchantItemsPerPage);

    /// <summary>
    /// Maps the one-based authored MerchantItem1..10 control on a one-based page to the one-based
    /// absolute merchant-list slot used by the frozen FrameXML.
    /// </summary>
    public static int MerchantAbsoluteSlot(int page, int physicalButton)
    {
        if (page < 1) throw new ArgumentOutOfRangeException(nameof(page));
        if (physicalButton is < 1 or > MerchantItemsPerPage)
            throw new ArgumentOutOfRangeException(nameof(physicalButton));
        return checked((page - 1) * MerchantItemsPerPage + physicalButton);
    }

    public static MerchantPagination MerchantPage(int itemCount, int requestedPage)
    {
        int count = Math.Max(0, itemCount);
        int pages = MerchantPageCount(count);
        int page = Math.Clamp(requestedPage, 1, pages);
        int first = MerchantAbsoluteSlot(page, 1);
        int visible = Math.Clamp(count - (first - 1), 0, MerchantItemsPerPage);
        bool controls = count > MerchantItemsPerPage;
        return new(page, pages, first, visible, controls,
            controls && page > 1,
            controls && page < pages,
            controls ? $"Page {page}" : null);
    }

    public static ItemRowGeometry MerchantItemRow(int physicalButton) =>
        ItemRow(physicalButton, MerchantItemsPerPage, MerchantRowStep);

    public static ItemRowGeometry BuybackItemRow(int displayOrdinal) =>
        ItemRow(displayOrdinal, BuybackItemCount, BuybackRowStep);

    private static ItemRowGeometry ItemRow(int physicalButton, int maximum, float rowStep)
    {
        if (physicalButton < 1 || physicalButton > maximum)
            throw new ArgumentOutOfRangeException(nameof(physicalButton));
        int zeroBased = physicalButton - 1;
        return new(zeroBased % 2 == 0 ? ItemFirstX : ItemSecondX,
            ItemFirstY + zeroBased / 2 * rowStep,
            ItemHitWidth, ItemHitHeight);
    }

    public static int BuybackGuidField(int physicalIndex) =>
        PlayerFieldVendorBuybackSlot1 + CheckedBuybackIndex(physicalIndex) *
        BuybackGuidFieldStride;

    public static int BuybackPriceField(int physicalIndex) =>
        PlayerFieldBuybackPrice1 + CheckedBuybackIndex(physicalIndex);

    public static int BuybackTimestampField(int physicalIndex) =>
        PlayerFieldBuybackTimestamp1 + CheckedBuybackIndex(physicalIndex);

    public static uint BuybackWireInventorySlot(int physicalIndex) =>
        (uint)(BuybackInventorySlotFirst + CheckedBuybackIndex(physicalIndex));

    private static int CheckedBuybackIndex(int physicalIndex)
    {
        if (physicalIndex is < 0 or >= BuybackItemCount)
            throw new ArgumentOutOfRangeException(nameof(physicalIndex));
        return physicalIndex;
    }

    /// <summary>
    /// Scans the twelve physical update-field descriptors, removes incomplete entries, and orders
    /// them oldest-first. A missing timestamp is frozen as zero; equal timestamps retain physical
    /// scan order. The physical index and its wire slot survive sorting.
    /// </summary>
    public static BuybackDescriptor[] OrderBuyback(
        IReadOnlyList<BuybackFieldValue> physicalFields)
    {
        ArgumentNullException.ThrowIfNull(physicalFields);
        var result = new List<BuybackDescriptor>(BuybackItemCount);
        int count = Math.Min(BuybackItemCount, physicalFields.Count);
        for (int physical = 0; physical < count; physical++)
        {
            BuybackFieldValue value = physicalFields[physical];
            if (value.ItemGuid == 0 || value.Price == 0) continue;
            result.Add(new(physical, value.ItemGuid, value.Price, value.Timestamp ?? 0,
                BuybackWireInventorySlot(physical)));
        }
        result.Sort(static (left, right) =>
        {
            int byTimestamp = left.Timestamp.CompareTo(right.Timestamp);
            return byTimestamp != 0
                ? byTimestamp
                : left.PhysicalIndex.CompareTo(right.PhysicalIndex);
        });
        return result.ToArray();
    }

    /// <summary>The merchant page's recent-buyback entry is the last oldest-first descriptor.</summary>
    public static BuybackDescriptor? RecentBuyback(
        IReadOnlyList<BuybackFieldValue> physicalFields)
    {
        BuybackDescriptor[] ordered = OrderBuyback(physicalFields);
        return ordered.Length == 0 ? null : ordered[^1];
    }

    public static MoneyParts SplitMoney(uint copper) =>
        new(copper / 10_000, copper / 100 % 100, copper % 100);

    /// <summary>
    /// Omits zero denominations except that a zero total displays one zero-copper cell. Merchant
    /// rows use G/S/C; the purse uses C/S/G.
    /// </summary>
    public static MoneyValue[] VisibleMoney(uint copper, bool highestFirst)
    {
        MoneyParts parts = SplitMoney(copper);
        MoneyValue[] ordered = highestFirst
            ?
            [
                new(MoneyDenomination.Gold, parts.Gold),
                new(MoneyDenomination.Silver, parts.Silver),
                new(MoneyDenomination.Copper, parts.Copper),
            ]
            :
            [
                new(MoneyDenomination.Copper, parts.Copper),
                new(MoneyDenomination.Silver, parts.Silver),
                new(MoneyDenomination.Gold, parts.Gold),
            ];
        return ordered.Where(value => value.Value != 0 ||
            copper == 0 && value.Denomination == MoneyDenomination.Copper).ToArray();
    }

    public static float NumberWidth(uint value, in MoneyDigitAdvances advances)
    {
        ValidateAdvances(advances);
        if (value == 0) return advances[0];
        float width = 0f;
        while (value != 0)
        {
            width += advances[(int)(value % 10)];
            value /= 10;
        }
        return width;
    }

    public static MoneyTexCoords MoneyIconTexCoords(MoneyDenomination denomination) =>
        denomination switch
        {
            MoneyDenomination.Gold => new(0f, .25f, 0f, 1f),
            MoneyDenomination.Silver => new(.25f, .5f, 0f, 1f),
            MoneyDenomination.Copper => new(.5f, .75f, 0f, 1f),
            _ => throw new ArgumentOutOfRangeException(nameof(denomination)),
        };

    public static MoneyLayout MerchantRowMoney(uint copper, in MoneyDigitAdvances advances) =>
        LayoutMoney(copper, highestFirst: true, MoneyPlacement.MerchantRow, advances);

    public static MoneyLayout MerchantPurseMoney(uint copper, in MoneyDigitAdvances advances) =>
        LayoutMoney(copper, highestFirst: false, MoneyPlacement.MerchantPurse, advances);

    private static MoneyLayout LayoutMoney(uint copper, bool highestFirst,
        MoneyPlacement placement, in MoneyDigitAdvances advances)
    {
        ValidateAdvances(advances);
        MoneyValue[] values = VisibleMoney(copper, highestFirst);
        var visible = new MoneyCellGeometry[values.Length];
        float edge = placement == MoneyPlacement.MerchantRow
            ? RowMoneyFirstLeft
            : MerchantFrameWidth + PurseMoneyFirstRightOffset;
        for (int i = 0; i < values.Length; i++)
        {
            float numberWidth = NumberWidth(values[i].Value, advances);
            float frameWidth = numberWidth + MoneyIconSize;
            float left;
            float right;
            if (placement == MoneyPlacement.MerchantRow)
            {
                left = edge;
                right = left + frameWidth;
                edge = right + MoneySlotGap;
            }
            else
            {
                right = edge;
                left = right - frameWidth;
                edge = left - MoneySlotGap;
            }
            visible[i] = new(i + 1, values[i].Denomination, values[i].Value,
                numberWidth, left, right,
                placement == MoneyPlacement.MerchantRow ? RowMoneyBottom : PurseMoneyBottom,
                MoneyIconTexCoords(values[i].Denomination));
        }
        int[] hidden = Enumerable.Range(values.Length + 1, 3 - values.Length).ToArray();
        return new(placement, visible, hidden);
    }

    private static void ValidateAdvances(in MoneyDigitAdvances advances)
    {
        for (int digit = 0; digit < 10; digit++)
        {
            float advance = advances[digit];
            if (!float.IsFinite(advance) || advance < 0f)
                throw new ArgumentOutOfRangeException(nameof(advances),
                    "NumberFontNormal digit advances must be finite and non-negative.");
        }
    }

    public static int? RepairCostVectorIndex(uint itemClass, uint itemSubclass) =>
        itemClass switch
        {
            WeaponItemClass when itemSubclass < WeaponRepairSubclassCount =>
                (int)itemSubclass,
            ArmorItemClass when itemSubclass < ArmorRepairSubclassCount =>
                ArmorRepairCostVectorOffset + (int)itemSubclass,
            _ => null,
        };

    public static uint DurabilityQualityRowKey(uint quality) =>
        checked(quality * 2 + 1);

    public static RepairTableLookup? RepairLookup(in RepairItem item)
    {
        int? vectorIndex = RepairCostVectorIndex(item.ItemClass, item.ItemSubclass);
        return vectorIndex is int index
            ? new(item.ItemLevel, index + 1, index, DurabilityQualityRowKey(item.Quality))
            : null;
    }

    /// <summary>
    /// Computes one item's repair cost from already-resolved DBC cells. Null represents a missing
    /// DurabilityCosts or DurabilityQuality row and returns zero; this law does not load or invent
    /// either table. The first rounding is positive half-away, and the discounted result uses
    /// nearest-even as in the frozen implementation.
    /// </summary>
    public static uint RepairCost(in RepairItem item, uint? durabilityCost,
        float? qualityMultiplier, float reputationDiscount = 0f)
    {
        if (!float.IsFinite(reputationDiscount))
            throw new ArgumentOutOfRangeException(nameof(reputationDiscount));
        if (RepairCostVectorIndex(item.ItemClass, item.ItemSubclass) is null ||
            item.MaximumDurability <= item.CurrentDurability ||
            durabilityCost is null || qualityMultiplier is null)
            return 0;
        if (!float.IsFinite(qualityMultiplier.Value) || qualityMultiplier.Value < 0f)
            throw new ArgumentOutOfRangeException(nameof(qualityMultiplier));

        uint pointsLost = item.MaximumDurability - item.CurrentDurability;
        double raw = (double)pointsLost * (double)qualityMultiplier.Value *
                     durabilityCost.Value;
        uint undiscounted;
        if (double.IsPositiveInfinity(raw) || raw >= uint.MaxValue)
        {
            undiscounted = uint.MaxValue;
        }
        else
        {
            double rounded = Math.Floor(raw + .5d);
            undiscounted = Math.Max(1u, (uint)Math.Max(0d, rounded));
        }

        // The factor subtraction is f32 in the frozen source; multiplication and rounding are f64.
        float discountFactor = 1f - reputationDiscount;
        double discounted = (double)undiscounted * discountFactor;
        if (double.IsPositiveInfinity(discounted) || discounted >= uint.MaxValue)
            return uint.MaxValue;
        if (!(discounted > 0d)) return 0;
        return (uint)Math.Round(discounted, MidpointRounding.ToEven);
    }
}
