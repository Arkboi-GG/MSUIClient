using System.Collections;
using System.Reflection;
using MSUIClient;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

internal static class MerchantFrameClinicalChecks
{
    public static void RunTooltipOnly()
    {
        CheckVendorTemplateTooltipProjection();
        CheckVendorTooltipRendererSourceFence();
    }

    public static void Run()
    {
        CheckMerchantPaginationAndRows();
        CheckBuybackFieldsAndOrdering();
        CheckMoneyDenominationsAndGeometry();
        CheckRepairLookupAndArithmetic();
        CheckVendorTemplateTooltipProjection();
        CheckRuntimeIntegrationSourceFence();
    }

    private static void CheckMerchantPaginationAndRows()
    {
        Check(MerchantFrameUiLaw.MerchantItemsPerPage == 10 &&
              MerchantFrameUiLaw.BuybackItemCount == 12 &&
              !MerchantFrameUiLaw.BuybackIsPaged,
            "MerchantFrame page-size constants drifted");

        for (int page = 1; page <= 20; page++)
        for (int button = 1; button <= MerchantFrameUiLaw.MerchantItemsPerPage; button++)
            Check(MerchantFrameUiLaw.MerchantAbsoluteSlot(page, button) ==
                  (page - 1) * 10 + button,
                $"MerchantItem{button} did not map to page {page}'s absolute slot");
        Throws<ArgumentOutOfRangeException>(() =>
            MerchantFrameUiLaw.MerchantAbsoluteSlot(0, 1));
        Throws<ArgumentOutOfRangeException>(() =>
            MerchantFrameUiLaw.MerchantAbsoluteSlot(1, 0));
        Throws<ArgumentOutOfRangeException>(() =>
            MerchantFrameUiLaw.MerchantAbsoluteSlot(1, 11));

        for (int itemCount = -2; itemCount <= 107; itemCount++)
        {
            int nonnegative = Math.Max(0, itemCount);
            int expectedPages = Math.Max(1, (nonnegative + 9) / 10);
            Check(MerchantFrameUiLaw.MerchantPageCount(itemCount) == expectedPages,
                $"merchant page count drifted for {itemCount}");
            for (int requested = -2; requested <= expectedPages + 2; requested++)
            {
                MerchantFrameUiLaw.MerchantPagination state =
                    MerchantFrameUiLaw.MerchantPage(itemCount, requested);
                int page = Math.Clamp(requested, 1, expectedPages);
                int first = (page - 1) * 10 + 1;
                int visible = Math.Clamp(nonnegative - (first - 1), 0, 10);
                bool controls = nonnegative > 10;
                Check(state.Page == page && state.PageCount == expectedPages &&
                      state.FirstAbsoluteSlot == first && state.VisibleItemCount == visible &&
                      state.ControlsVisible == controls &&
                      state.PreviousEnabled == (controls && page > 1) &&
                      state.NextEnabled == (controls && page < expectedPages) &&
                      state.PageLabel == (controls ? $"Page {page}" : null),
                    $"merchant pagination state drifted for count={itemCount}, page={requested}");
                Check(state.PageLabel is null || !state.PageLabel.Contains(" of ",
                        StringComparison.Ordinal),
                    "merchant page label invented an of-M suffix");
            }
        }

        for (int button = 1; button <= 10; button++)
        {
            MerchantFrameUiLaw.ItemRowGeometry row = MerchantFrameUiLaw.MerchantItemRow(button);
            int zeroBased = button - 1;
            Check(row.X == (zeroBased % 2 == 0 ? 24f : 189f) &&
                  row.Y == 80f + zeroBased / 2 * 52f &&
                  row.Width == 153f && row.Height == 44f,
                $"merchant physical row {button} geometry drifted");
        }
        for (int ordinal = 1; ordinal <= 12; ordinal++)
        {
            MerchantFrameUiLaw.ItemRowGeometry row = MerchantFrameUiLaw.BuybackItemRow(ordinal);
            int zeroBased = ordinal - 1;
            Check(row.X == (zeroBased % 2 == 0 ? 24f : 189f) &&
                  row.Y == 80f + zeroBased / 2 * 59f &&
                  row.Width == 153f && row.Height == 44f,
                $"buyback display row {ordinal} geometry drifted");
        }
        Check(MerchantFrameUiLaw.BuybackItemRow(11).Y == 375f &&
              MerchantFrameUiLaw.BuybackItemRow(12).Y == 375f,
            "buyback-only rows 11/12 did not retain the frozen sixth-row origin");
        Throws<ArgumentOutOfRangeException>(() => MerchantFrameUiLaw.MerchantItemRow(0));
        Throws<ArgumentOutOfRangeException>(() => MerchantFrameUiLaw.MerchantItemRow(11));
        Throws<ArgumentOutOfRangeException>(() => MerchantFrameUiLaw.BuybackItemRow(0));
        Throws<ArgumentOutOfRangeException>(() => MerchantFrameUiLaw.BuybackItemRow(13));
    }

    private static void CheckBuybackFieldsAndOrdering()
    {
        Check(MerchantFrameUiLaw.PlayerFieldVendorBuybackSlot1 == 624 &&
              MerchantFrameUiLaw.PlayerFieldBuybackPrice1 == 1226 &&
              MerchantFrameUiLaw.PlayerFieldBuybackTimestamp1 == 1238 &&
              MerchantFrameUiLaw.BuybackInventorySlotFirst == 69 &&
              MerchantFrameUiLaw.BuybackGuidFieldStride == 2,
            "build-5875 buyback descriptor bases drifted");
        for (int physical = 0; physical < 12; physical++)
        {
            Check(MerchantFrameUiLaw.BuybackGuidField(physical) == 624 + physical * 2 &&
                  MerchantFrameUiLaw.BuybackPriceField(physical) == 1226 + physical &&
                  MerchantFrameUiLaw.BuybackTimestampField(physical) == 1238 + physical &&
                  MerchantFrameUiLaw.BuybackWireInventorySlot(physical) == 69u + (uint)physical,
                $"buyback descriptor {physical} field/wire mapping drifted");
        }
        foreach (int invalid in new[] { -1, 12 })
        {
            Throws<ArgumentOutOfRangeException>(() => MerchantFrameUiLaw.BuybackGuidField(invalid));
            Throws<ArgumentOutOfRangeException>(() => MerchantFrameUiLaw.BuybackPriceField(invalid));
            Throws<ArgumentOutOfRangeException>(() => MerchantFrameUiLaw.BuybackTimestampField(invalid));
            Throws<ArgumentOutOfRangeException>(() =>
                MerchantFrameUiLaw.BuybackWireInventorySlot(invalid));
        }

        var fields = new MerchantFrameUiLaw.BuybackFieldValue[14];
        fields[0] = new(0x10, 100, null);
        fields[1] = new(0x11, 0, 1);
        fields[2] = new(0, 200, 2);
        fields[3] = new(0x13, 300, 7);
        fields[4] = new(0x14, 400, 3);
        fields[5] = new(0x15, 500, 3);
        fields[11] = new(0x1b, 1_100, 20);
        fields[12] = new(0x1c, 1_200, 1); // beyond the twelve physical descriptors

        MerchantFrameUiLaw.BuybackDescriptor[] ordered =
            MerchantFrameUiLaw.OrderBuyback(fields);
        int[] expectedPhysical = [0, 4, 5, 3, 11];
        Check(ordered.Select(value => value.PhysicalIndex).SequenceEqual(expectedPhysical) &&
              ordered.Select(value => value.Timestamp).SequenceEqual(
                  new uint[] { 0, 3, 3, 7, 20 }) &&
              ordered.Select(value => value.WireInventorySlot).SequenceEqual(
                  new uint[] { 69, 73, 74, 72, 80 }),
            "buyback filtering, missing-timestamp base, stable oldest-first order, or wire slot drifted");
        Check(ordered[1].ItemGuid == 0x14 && ordered[1].Price == 400 &&
              ordered[2].ItemGuid == 0x15 && ordered[2].Price == 500,
            "equal-timestamp buybacks did not retain physical scan order");

        MerchantFrameUiLaw.BuybackDescriptor? recent =
            MerchantFrameUiLaw.RecentBuyback(fields);
        Check(recent is { PhysicalIndex: 11, WireInventorySlot: 80 },
            "merchant recent-buyback entry was not the last oldest-first descriptor");
        Check(MerchantFrameUiLaw.RecentBuyback([]) is null,
            "empty buyback state manufactured a recent entry");
        Throws<ArgumentNullException>(() => MerchantFrameUiLaw.OrderBuyback(null!));
    }

    private static void CheckMoneyDenominationsAndGeometry()
    {
        Check(MerchantFrameUiLaw.MoneyFontObject == "NumberFontNormal" &&
              MerchantFrameUiLaw.MoneyTexturePath == @"Interface\MoneyFrame\UI-MoneyIcons" &&
              MerchantFrameUiLaw.MoneyIconSize == 13f &&
              MerchantFrameUiLaw.MoneySlotGap == 4f,
            "merchant money font/texture/slot constants drifted");
        Check(MerchantFrameUiLaw.SplitMoney(123_456) == new MerchantFrameUiLaw.MoneyParts(12, 34, 56) &&
              MerchantFrameUiLaw.SplitMoney(0) == new MerchantFrameUiLaw.MoneyParts(0, 0, 0) &&
              MerchantFrameUiLaw.SplitMoney(uint.MaxValue) ==
                  new MerchantFrameUiLaw.MoneyParts(429_496, 72, 95),
            "copper denomination split drifted");

        CheckMoneyValues(0, true,
            [(MerchantFrameUiLaw.MoneyDenomination.Copper, 0u)]);
        CheckMoneyValues(0, false,
            [(MerchantFrameUiLaw.MoneyDenomination.Copper, 0u)]);
        CheckMoneyValues(10_000, true,
            [(MerchantFrameUiLaw.MoneyDenomination.Gold, 1u)]);
        CheckMoneyValues(100, true,
            [(MerchantFrameUiLaw.MoneyDenomination.Silver, 1u)]);
        CheckMoneyValues(1, true,
            [(MerchantFrameUiLaw.MoneyDenomination.Copper, 1u)]);
        CheckMoneyValues(10_001, true,
            [
                (MerchantFrameUiLaw.MoneyDenomination.Gold, 1u),
                (MerchantFrameUiLaw.MoneyDenomination.Copper, 1u),
            ]);
        CheckMoneyValues(10_001, false,
            [
                (MerchantFrameUiLaw.MoneyDenomination.Copper, 1u),
                (MerchantFrameUiLaw.MoneyDenomination.Gold, 1u),
            ]);
        CheckMoneyValues(10_101, true,
            [
                (MerchantFrameUiLaw.MoneyDenomination.Gold, 1u),
                (MerchantFrameUiLaw.MoneyDenomination.Silver, 1u),
                (MerchantFrameUiLaw.MoneyDenomination.Copper, 1u),
            ]);

        var advances = new MerchantFrameUiLaw.MoneyDigitAdvances(
            1, 2, 3, 4, 5, 6, 7, 8, 9, 10);
        Check(MerchantFrameUiLaw.NumberWidth(0, advances) == 1f &&
              MerchantFrameUiLaw.NumberWidth(12, advances) == 5f &&
              MerchantFrameUiLaw.NumberWidth(909, advances) == 21f,
            "NumberFontNormal decimal advance sum drifted");

        MerchantFrameUiLaw.MoneyLayout row =
            MerchantFrameUiLaw.MerchantRowMoney(123_456, advances);
        Check(row.Placement == MerchantFrameUiLaw.MoneyPlacement.MerchantRow &&
              row.HiddenFrameSlots.Length == 0 && row.VisibleCells.Length == 3,
            "merchant row money visibility drifted");
        CheckMoneyCell(row.VisibleCells[0], 1, MerchantFrameUiLaw.MoneyDenomination.Gold,
            12, 5, 46, 64, 6);
        CheckMoneyCell(row.VisibleCells[1], 2, MerchantFrameUiLaw.MoneyDenomination.Silver,
            34, 9, 68, 90, 6);
        CheckMoneyCell(row.VisibleCells[2], 3, MerchantFrameUiLaw.MoneyDenomination.Copper,
            56, 13, 94, 120, 6);

        MerchantFrameUiLaw.MoneyLayout purse =
            MerchantFrameUiLaw.MerchantPurseMoney(123_456, advances);
        Check(purse.Placement == MerchantFrameUiLaw.MoneyPlacement.MerchantPurse &&
              purse.HiddenFrameSlots.Length == 0 && purse.VisibleCells.Length == 3,
            "merchant purse money visibility drifted");
        CheckMoneyCell(purse.VisibleCells[0], 1, MerchantFrameUiLaw.MoneyDenomination.Copper,
            56, 13, 305, 331, 67);
        CheckMoneyCell(purse.VisibleCells[1], 2, MerchantFrameUiLaw.MoneyDenomination.Silver,
            34, 9, 279, 301, 67);
        CheckMoneyCell(purse.VisibleCells[2], 3, MerchantFrameUiLaw.MoneyDenomination.Gold,
            12, 5, 257, 275, 67);

        MerchantFrameUiLaw.MoneyLayout zero =
            MerchantFrameUiLaw.MerchantRowMoney(0, advances);
        Check(zero.VisibleCells.Length == 1 &&
              zero.VisibleCells[0].Denomination == MerchantFrameUiLaw.MoneyDenomination.Copper &&
              zero.VisibleCells[0].Value == 0 && zero.VisibleCells[0].Left == 46f &&
              zero.VisibleCells[0].Right == 60f &&
              zero.HiddenFrameSlots.SequenceEqual(new[] { 2, 3 }),
            "zero money did not retain one copper cell while hiding frames 2/3");

        Check(MerchantFrameUiLaw.MoneyIconTexCoords(
                  MerchantFrameUiLaw.MoneyDenomination.Gold) ==
              new MerchantFrameUiLaw.MoneyTexCoords(0, .25f, 0, 1) &&
              MerchantFrameUiLaw.MoneyIconTexCoords(
                  MerchantFrameUiLaw.MoneyDenomination.Silver) ==
              new MerchantFrameUiLaw.MoneyTexCoords(.25f, .5f, 0, 1) &&
              MerchantFrameUiLaw.MoneyIconTexCoords(
                  MerchantFrameUiLaw.MoneyDenomination.Copper) ==
              new MerchantFrameUiLaw.MoneyTexCoords(.5f, .75f, 0, 1),
            "merchant money denomination UVs drifted");

        Throws<ArgumentOutOfRangeException>(() => MerchantFrameUiLaw.NumberWidth(1,
            MerchantFrameUiLaw.MoneyDigitAdvances.Uniform(-1)));
        Throws<ArgumentOutOfRangeException>(() => MerchantFrameUiLaw.MerchantRowMoney(1,
            MerchantFrameUiLaw.MoneyDigitAdvances.Uniform(float.NaN)));
    }

    private static void CheckRepairLookupAndArithmetic()
    {
        Check(MerchantFrameUiLaw.RepairVendorNpcFlag == 0x4000 &&
              MerchantFrameUiLaw.WeaponItemClass == 2 &&
              MerchantFrameUiLaw.ArmorItemClass == 4,
            "merchant repair constants drifted");

        for (uint subclass = 0; subclass < 21; subclass++)
        {
            var item = new MerchantFrameUiLaw.RepairItem(2, subclass, 57, 3, 1, 2);
            MerchantFrameUiLaw.RepairTableLookup? lookup = MerchantFrameUiLaw.RepairLookup(item);
            Check(MerchantFrameUiLaw.RepairCostVectorIndex(2, subclass) == (int)subclass &&
                  lookup == new MerchantFrameUiLaw.RepairTableLookup(
                      57, (int)subclass + 1, (int)subclass, 7),
                $"weapon repair column {subclass} drifted");
        }
        for (uint subclass = 0; subclass < 8; subclass++)
        {
            int vector = 21 + (int)subclass;
            var item = new MerchantFrameUiLaw.RepairItem(4, subclass, 63, 4, 1, 2);
            MerchantFrameUiLaw.RepairTableLookup? lookup = MerchantFrameUiLaw.RepairLookup(item);
            Check(MerchantFrameUiLaw.RepairCostVectorIndex(4, subclass) == vector &&
                  lookup == new MerchantFrameUiLaw.RepairTableLookup(
                      63, vector + 1, vector, 9),
                $"armor repair column {subclass} drifted");
        }
        Check(MerchantFrameUiLaw.RepairCostVectorIndex(2, 21) is null &&
              MerchantFrameUiLaw.RepairCostVectorIndex(4, 8) is null &&
              MerchantFrameUiLaw.RepairCostVectorIndex(1, 0) is null &&
              MerchantFrameUiLaw.RepairLookup(
                  new MerchantFrameUiLaw.RepairItem(1, 0, 1, 0, 0, 1)) is null,
            "unsupported repair class/subclass manufactured a DBC lookup");
        Check(MerchantFrameUiLaw.DurabilityQualityRowKey(0) == 1 &&
              MerchantFrameUiLaw.DurabilityQualityRowKey(1) == 3 &&
              MerchantFrameUiLaw.DurabilityQualityRowKey(7) == 15,
            "client DurabilityQuality key was not 2*quality+1");
        Throws<OverflowException>(() =>
            MerchantFrameUiLaw.DurabilityQualityRowKey(uint.MaxValue));

        var onePoint = new MerchantFrameUiLaw.RepairItem(2, 0, 10, 1, 9, 10);
        Check(MerchantFrameUiLaw.RepairCost(onePoint, 3, .5f) == 2,
            "repair base did not round positive 1.5 half-away");
        Check(MerchantFrameUiLaw.RepairCost(onePoint, 1, .49f) == 1 &&
              MerchantFrameUiLaw.RepairCost(onePoint, 0, 0f) == 1,
            "repair base did not retain the frozen minimum-one cost");
        Check(MerchantFrameUiLaw.RepairCost(onePoint, null, 1f) == 0 &&
              MerchantFrameUiLaw.RepairCost(onePoint, 1, null) == 0,
            "missing durability table cells did not produce zero");
        Check(MerchantFrameUiLaw.RepairCost(onePoint with { CurrentDurability = 10 }, 5, 1f) == 0 &&
              MerchantFrameUiLaw.RepairCost(onePoint with { CurrentDurability = 11 }, 5, 1f) == 0 &&
              MerchantFrameUiLaw.RepairCost(onePoint with { ItemClass = 1 }, 5, 1f) == 0,
            "intact/overfull/unsupported repair inputs produced a cost");

        Check(MerchantFrameUiLaw.RepairCost(onePoint, 5, 1f, .5f) == 2,
            "discounted repair 2.5 did not round nearest-even to 2");
        Check(MerchantFrameUiLaw.RepairCost(onePoint, 3, 1f, .5f) == 2,
            "discounted repair 1.5 did not round nearest-even to 2");
        Check(MerchantFrameUiLaw.RepairCost(onePoint, 1, 1f, .5f) == 0 &&
              MerchantFrameUiLaw.RepairCost(onePoint, 5, 1f, 1f) == 0,
            "discounted repair did not permit an exact nearest-even/clamped zero");
        Check(MerchantFrameUiLaw.RepairCost(onePoint, 3, 1f, -1f) == 6,
            "repair discount multiplier was not applied before final rounding");

        var precisionEdge = onePoint with
        {
            CurrentDurability = 0,
            MaximumDurability = 16_777_217,
        };
        Check(MerchantFrameUiLaw.RepairCost(precisionEdge, 1, 1f) == 16_777_217 &&
              MerchantFrameUiLaw.RepairCost(precisionEdge, 1, 1f, .25f) == 12_582_913,
            "repair points/product lost the frozen f64 precision after f32 input conversion");

        var saturated = onePoint with { CurrentDurability = 0, MaximumDurability = uint.MaxValue };
        Check(MerchantFrameUiLaw.RepairCost(saturated, uint.MaxValue, float.MaxValue) ==
              uint.MaxValue,
            "repair arithmetic did not saturate an overflowing positive cost");
        Throws<ArgumentOutOfRangeException>(() =>
            MerchantFrameUiLaw.RepairCost(onePoint, 1, float.NaN));
        Throws<ArgumentOutOfRangeException>(() =>
            MerchantFrameUiLaw.RepairCost(onePoint, 1, 1f, float.PositiveInfinity));
    }

    private static void CheckVendorTemplateTooltipProjection()
    {
        var player = new WorldEntity
        {
            Fields = CreateObjectFields(
            [
                (ObjectFields.UNIT_LEVEL, 60u),
                (ObjectFields.UNIT_BYTES_0, 1u | 1u << 8),
            ]),
        };
        var sword = new ItemTemplate
        {
            Name = "Clinical Sword",
            Quality = 3,
            Class = 2,
            Subclass = 7,
            InventoryType = 21,
            Bonding = 2,
            DelayMs = 2_500,
            RequiredLevel = 37,
            Description = "Frozen flavor.",
            Block = 1,
        };
        sword.Damages.Add(new ItemDamage(10.2f, 20.5f, 0));
        sword.Stats.Add(new ItemStat(4, 9));

        (string Left, string? Right)[] lines = VendorTooltipLines(sword, player);
        Check(lines.SequenceEqual(new (string, string?)[]
            {
                ("Clinical Sword", null),
                ("Binds when equipped", null),
                ("Main Hand", "Sword"),
                ("10 - 21 Damage", "Speed 2.50"),
                ("(6.1 damage per second)", null),
                ("1 Block", null),
                ("+9 Strength", null),
                ("Requires Level 37", null),
                ("\"Frozen flavor.\"", null),
            }),
            "Merchant template tooltip lost slot/type, speed, DPS, block, order, or quoting");
        Check(lines.All(line => !line.Left.StartsWith("Stack:", StringComparison.Ordinal) &&
                                !line.Left.StartsWith("Durability ", StringComparison.Ordinal) &&
                                !line.Left.StartsWith("Item Level ", StringComparison.Ordinal)),
            "Merchant template tooltip invented instance-only stack/durability/item-level lines");

        sword.Name = "Mutated";
        sword.Damages.Clear();
        sword.Stats.Clear();
        Check(lines[0].Left == "Clinical Sword" &&
              lines.Any(line => line.Left == "10 - 21 Damage") &&
              lines.Any(line => line.Left == "+9 Strength"),
            "Merchant tooltip receipt retained mutable ItemTemplate/list state");

        var shield = new ItemTemplate
        {
            Name = "Clinical Shield",
            Class = 4,
            Subclass = 6,
            InventoryType = 14,
            Armor = 85,
            Block = 1,
        };
        (string Left, string? Right)[] shieldLines = VendorTooltipLines(shield, player);
        Check(shieldLines.Contains(("Off Hand", "Shield")) &&
              shieldLines.Contains(("85 Armor", null)) &&
              shieldLines.Contains(("1 Block", null)),
            "Merchant shield tooltip lost Off Hand/Shield/Armor/Block projection");
    }

    private static (string Left, string? Right)[] VendorTooltipLines(
        ItemTemplate item,
        WorldEntity player)
    {
        MethodInfo? prepare = typeof(GameLoop).GetMethod("PrepareVendorTemplateTooltip",
            BindingFlags.Static | BindingFlags.NonPublic);
        object snapshot = prepare?.Invoke(null, [item, player]) ??
            throw new InvalidDataException("Merchant template tooltip preparation seam missing");
        object enumerable = snapshot.GetType().GetProperty("Lines")?.GetValue(snapshot) ??
            throw new InvalidDataException("Merchant template tooltip line receipt missing");
        var result = new List<(string Left, string? Right)>();
        foreach (object line in (IEnumerable)enumerable)
        {
            Type type = line.GetType();
            string left = type.GetProperty("Left")?.GetValue(line) as string ?? "";
            string? right = type.GetProperty("Right")?.GetValue(line) as string;
            result.Add((left, right));
        }
        return result.ToArray();
    }

    private static ObjectFields CreateObjectFields(
        IReadOnlyList<(ushort Field, uint Value)> fields)
    {
        int blocks = fields.Count == 0 ? 1 : fields.Max(pair => pair.Field) / 32 + 1;
        var writer = new PacketWriter();
        writer.WriteU8((byte)blocks);
        for (int block = 0; block < blocks; block++)
        {
            uint mask = 0;
            foreach ((ushort field, _) in fields)
                if (field / 32 == block) mask |= 1u << (field & 31);
            writer.WriteU32(mask);
        }
        foreach ((_, uint value) in fields.OrderBy(pair => pair.Field))
            writer.WriteU32(value);
        return ObjectFields.Read(new PacketReader(writer.ToArray())).AsCreated();
    }

    private static void CheckRuntimeIntegrationSourceFence()
    {
        string client = Path.Combine(ClientConfig.FindRepoRoot(), "MSUIClient");
        string renderer = SourceText.Read(Path.Combine(client, "Program.Vendor.Render.cs"));
        string session = SourceText.Read(Path.Combine(client, "Program.Vendor.Session.cs"));
        string repair = SourceText.Read(Path.Combine(client, "Program.Vendor.Repair.cs"));
        string vendor = SourceText.Read(Path.Combine(client, "Program.Vendor.cs"));
        string inventory = SourceText.Read(Path.Combine(client, "Program.Inventory.cs"));
        string character = SourceText.Read(Path.Combine(client, "Program.CharacterPage.cs"));
        string settings = SourceText.Read(Path.Combine(client, "Program.Settings.cs"));
        string quest = SourceText.Read(Path.Combine(client, "Program.Quest.cs"));
        string net = SourceText.Read(Path.Combine(client, "Program.Net.cs"));
        string program = SourceText.Read(Path.Combine(client, "Program.cs"));
        string items = SourceText.Read(Path.Combine(client, "Net", "Items.cs"));

        Check(renderer.Contains(
                  "physical <= MerchantFrameUiLaw.MerchantItemsPerPage", StringComparison.Ordinal) &&
              renderer.Contains(
                  "ordinal <= MerchantFrameUiLaw.BuybackItemCount", StringComparison.Ordinal) &&
              renderer.Contains("if (input.RightReleased) BuyVendorEntry(row.ItemId, 1);",
                  StringComparison.Ordinal) &&
              renderer.Contains(
                  "recent ? input.LeftReleased : input.LeftReleased || input.RightReleased",
                  StringComparison.Ordinal) &&
              renderer.Contains("rows[^1]", StringComparison.Ordinal) &&
              renderer.Contains("descriptor.WireInventorySlot", StringComparison.Ordinal) &&
              renderer.Contains("player.Fields.PlayerBuybackTimestamp(index)",
                  StringComparison.Ordinal) &&
              renderer.Contains(
                  "new(\"item:vendor-merchant-row\", (ulong)(physical - 1))",
                  StringComparison.Ordinal) &&
              renderer.Contains(
                  "new(surface, (ulong)visibleOrdinal)", StringComparison.Ordinal),
            "Merchant renderer lost fixed row ownership, click verbs, or physical buyback ordering");

        int pressLeft = renderer.IndexOf(
            "if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))",
            StringComparison.Ordinal);
        int releaseLeft = renderer.IndexOf(
            "hovered && ImGui.IsMouseReleased(ImGuiMouseButton.Left)",
            StringComparison.Ordinal);
        int ownerLeft = renderer.IndexOf("_vendorLeftPressedRow == key", releaseLeft,
            StringComparison.Ordinal);
        int pressRight = renderer.IndexOf(
            "if (rightButton && hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right))",
            StringComparison.Ordinal);
        int releaseRight = renderer.IndexOf(
            "rightButton && hovered && ImGui.IsMouseReleased(ImGuiMouseButton.Right)",
            StringComparison.Ordinal);
        int ownerRight = renderer.IndexOf("_vendorRightPressedRow == key", releaseRight,
            StringComparison.Ordinal);
        Check(pressLeft >= 0 && pressLeft < releaseLeft && releaseLeft < ownerLeft &&
              pressRight >= 0 && pressRight < releaseRight && releaseRight < ownerRight &&
              renderer.Contains("(rightButton ? ImGuiButtonFlags.MouseButtonRight : " +
                  "ImGuiButtonFlags.None)", StringComparison.Ordinal) &&
              Count(renderer, "rightButton: false") == 4 &&
              !renderer.Contains("IsItemClicked", StringComparison.Ordinal),
            "Merchant controls no longer require same-control press/release ownership");

        Check(session.Contains("private readonly bool[] _vendorOpenedBags = new bool[5];",
                  StringComparison.Ordinal) &&
              session.Contains("container, true, playSound: true)",
                  StringComparison.Ordinal) &&
              session.Contains("SetBagWindowOpen(container, false, playSound: true);",
                  StringComparison.Ordinal) &&
              !session.Contains("if (openedBag) PlayBagSound", StringComparison.Ordinal) &&
              !session.Contains("if (closedBag) PlayBagSound", StringComparison.Ordinal) &&
              Count(session, "PlayUiSound(\"igCharacterInfoOpen\"") == 1 &&
              Count(session, "PlayUiSound(\"igCharacterInfoClose\"") == 1 &&
              session.Contains("if (_vendorOpenedBags[container] && IsBagWindowOpen(container))",
                  StringComparison.Ordinal) &&
              session.Contains("TryGetInteractionBodyPose(out WorldBodyPose sessionBody)",
                  StringComparison.Ordinal) &&
              session.Contains("return NpcSessionUiLaw.InRange(delta.LengthSquared());",
                  StringComparison.Ordinal) &&
              session.Contains("Vector3 delta = sessionBody.Position - candidate.Position",
                  StringComparison.Ordinal) &&
              session.Contains("Vector3.DistanceSquared(sessionBody.Position, vendor.Position)",
                  StringComparison.Ordinal) &&
              session.Contains(
                  "NpcSessionUiLaw.ShouldClose(true, true, sourceAvailable, distanceSquared)",
                  StringComparison.Ordinal) &&
              Count(session, "IsVendorServiceAvailable(") == 1,
            "Merchant session lost per-bag sound, owned close, or exact range lifecycle behavior");

        Check(repair.Contains("var seen = new HashSet<ulong>();", StringComparison.Ordinal) &&
              repair.Contains("for (int slot = 0; slot < 19; slot++)",
                  StringComparison.Ordinal) &&
              repair.Contains("for (int slot = 0; slot < 16; slot++)",
                  StringComparison.Ordinal) &&
              repair.Contains("for (int bagSlot = 19; bagSlot < 23; bagSlot++)",
                  StringComparison.Ordinal) &&
              repair.Contains("Math.Min(36u, bag.Fields.ContainerNumSlots)",
                  StringComparison.Ordinal) &&
              repair.Contains("MerchantFrameUiLaw.RepairVendorNpcFlag",
                  StringComparison.Ordinal) &&
              repair.Contains("_net?.RepairItem(_vendor.VendorGuid, itemGuid);",
                  StringComparison.Ordinal) &&
              repair.Contains("_net.RepairItem(_vendor.VendorGuid, 0)",
                  StringComparison.Ordinal) &&
              repair.Contains("_items.Require(item.Entry, item.Guid, _net)",
                  StringComparison.Ordinal) &&
              !repair.Contains("PlayUiSound(\"ITEM_REPAIR\"", StringComparison.Ordinal) &&
              renderer.Contains("PlayUiSound(\"ITEM_REPAIR\", \"ui.vendor\")",
                  StringComparison.Ordinal) &&
              renderer.Contains("HideSharedGameTooltip(current);", StringComparison.Ordinal),
            "Merchant repair scan, vendor gate, item/all send, or repair cue drifted");

        Check(vendor.Contains("OpenVendorSession(inventory);", StringComparison.Ordinal) &&
              inventory.Contains("TryRepairMerchantItem(instance?.Guid ?? 0)",
                  StringComparison.Ordinal) &&
              character.Contains("TryRepairMerchantItem(instance?.Guid ?? 0)",
                  StringComparison.Ordinal) &&
              Count(inventory + character,
                  "bool repairReleased = _vendorRepairMode && hovered &&") == 2 &&
              Count(inventory + character,
                  "ImGui.IsMouseReleased(ImGuiMouseButton.Left) && ImGui.IsItemDeactivated();") == 2 &&
              // Bag clicks carry the possession read-only gate (interactive = controlling
              // your own character); the paperdoll's stay session-scoped.
              inventory.Contains("bool leftClicked = interactive && !_vendorRepairMode &&",
                  StringComparison.Ordinal) &&
              inventory.Contains("bool rightClicked = interactive && !_vendorRepairMode &&",
                  StringComparison.Ordinal) &&
              character.Contains("bool leftClicked = !_vendorRepairMode &&",
                  StringComparison.Ordinal) &&
              character.Contains("bool rightClicked = !_vendorRepairMode &&",
                  StringComparison.Ordinal) &&
              Count(inventory + character,
                  "if (!giftWrapClick && _giftWrap is null && !dressUpClick && " +
                  "!_vendorRepairMode && _itemCastSpell == 0 && _enchantConfirmation is null)") == 2 &&
              settings.Contains("CloseVendorSession();", StringComparison.Ordinal) &&
              quest.Contains("CloseVendorSession();", StringComparison.Ordinal) &&
              Count(net, "ResetVendor();") == 2 &&
              program.Contains("UpdateVendorLifecycle();", StringComparison.Ordinal) &&
              !settings.Contains("_vendor = null", StringComparison.Ordinal) &&
              !quest.Contains("_vendor = null", StringComparison.Ordinal),
            "Merchant host integration bypassed central open/close/reset/repair lifecycle");

        int wireIcon = renderer.IndexOf(
            "string? wireIcon = _items?.IconForDisplay(row.DisplayId);",
            StringComparison.Ordinal);
        int templateIcon = renderer.IndexOf(
            "wireIcon ?? (item is not null", wireIcon, StringComparison.Ordinal);
        Check(wireIcon >= 0 && templateIcon > wireIcon &&
              renderer.Contains("item?.Name ?? (itemEstablished ? \"...\" : null)",
                  StringComparison.Ordinal) &&
              renderer.Contains("if (item is not null && !_vendorRepairMode)",
                  StringComparison.Ordinal) &&
              renderer.Contains("if (rows.Length == 0)", StringComparison.Ordinal) &&
              renderer.Contains("item is null ? 0 : descriptor.Price",
                  StringComparison.Ordinal) &&
              renderer.Contains("usable: recent || VendorItemUsable(player, item)",
                  StringComparison.Ordinal) &&
              renderer.Contains("if (hovered)", StringComparison.Ordinal) &&
              renderer.Contains("compactPlate ? -3 : -2", StringComparison.Ordinal) &&
              renderer.Contains("new Vector2(32, 37f - 2 -", StringComparison.Ordinal) &&
              renderer.Contains("MoneyIconSize * .5f", StringComparison.Ordinal),
            "Merchant unresolved/empty rows, recent tint, highlight, count, or coin geometry drifted");

        Check(renderer.Contains("PrepareVendorTemplateTooltip(item, player)",
                  StringComparison.Ordinal) &&
              Count(renderer, "PrepareItemTooltipBodySnapshot(item") == 0 &&
              Count(renderer, "OfferPreparedItemTooltip(") == 0 &&
              renderer.Contains("VendorInventoryTypeName", StringComparison.Ordinal) &&
              renderer.Contains("VendorSubclassName", StringComparison.Ordinal) &&
              renderer.Contains("VendorDamageText", StringComparison.Ordinal) &&
              renderer.Contains("item.Block > 0", StringComparison.Ordinal) &&
              items.Contains("public uint Block;", StringComparison.Ordinal) &&
              items.Contains("item.Block = r.ReadU32();", StringComparison.Ordinal) &&
              renderer.Contains("Speed {speed.ToString(\"0.00\"",
                  StringComparison.Ordinal) &&
              renderer.Contains("damage per second)", StringComparison.Ordinal) &&
              renderer.Contains("PrepareInventoryItemTooltipRenderer(",
                  StringComparison.Ordinal) &&
              renderer.Contains("body, ownerTopRight, new Vector2(0, 1)",
                  StringComparison.Ordinal) &&
              renderer.Contains("DrawPreparedInventoryItemTooltip(renderer)",
                  StringComparison.Ordinal) &&
              !renderer.Contains("##vendor-template-tooltip-columns",
                  StringComparison.Ordinal) &&
              !renderer.Contains("ImGui.BeginTooltip()", StringComparison.Ordinal) &&
              !renderer.Contains("ImGui.BeginTable", StringComparison.Ordinal) &&
              !renderer.Contains("ImGui.TextColored", StringComparison.Ordinal),
            "Merchant template-source tooltip projection escaped the authored GameText/WowSkin renderer or BOTTOMLEFT-on-TOPRIGHT seat");

        Check(renderer.Contains("Page-Disabled", StringComparison.Ordinal) &&
              renderer.Contains("UI-PageButton-Background", StringComparison.Ordinal) &&
              renderer.Contains("Vector2 backgroundMinimum = minimum + " +
                  "new Vector2(0, -1) * scale;", StringComparison.Ordinal) &&
              renderer.Contains("bool enabled = true", StringComparison.Ordinal) &&
              renderer.Contains("if (!enabled) return default;", StringComparison.Ordinal) &&
              renderer.Contains("GameTooltipAnchorKind.OwnerRight", StringComparison.Ordinal) &&
              renderer.Contains("bool allHovered = ImGui.IsMouseHoveringRect(allMin,",
                  StringComparison.Ordinal) &&
              renderer.Contains("repairCost > 0 ? repairCost : null",
                  StringComparison.Ordinal) &&
              renderer.Contains("_vendorSuppressRepairAllTooltipUntilLeave = true;",
                  StringComparison.Ordinal) &&
              renderer.Contains("PrepareSharedGameTooltipRenderer(" +
                  "SharedGameTooltipSnapshot(), ownerTopRight)", StringComparison.Ordinal),
            "Merchant disabled controls or repair tooltip owner-right lifecycle drifted");

        Check(renderer.Contains("MerchantFrameUiLaw.MerchantRowMoney(copper, advances)",
                  StringComparison.Ordinal) &&
              renderer.Contains("MerchantFrameUiLaw.MerchantPurseMoney(copper, advances)",
                  StringComparison.Ordinal) &&
              renderer.Contains("GameText.DrawRightAligned(draw, " +
                  "MerchantFrameUiLaw.MoneyFontObject", StringComparison.Ordinal) &&
              renderer.Contains("draw.AddImage((nint)texture, iconMin,",
                  StringComparison.Ordinal),
            "Merchant row/purse money stopped consuming the measured source-pinned law");
    }

    private static void CheckVendorTooltipRendererSourceFence()
    {
        string client = Path.Combine(ClientConfig.FindRepoRoot(), "MSUIClient");
        string renderer = SourceText.Read(Path.Combine(client, "Program.Vendor.Render.cs"));
        string inventory = SourceText.Read(Path.Combine(client, "Program.Inventory.cs"));

        Check(renderer.Contains("PrepareVendorTemplateTooltip(item, player)",
                  StringComparison.Ordinal) &&
              renderer.Contains("PrepareInventoryItemTooltipRenderer(",
                  StringComparison.Ordinal) &&
              renderer.Contains("body, ownerTopRight, new Vector2(0, 1)",
                  StringComparison.Ordinal) &&
              renderer.Contains("DrawPreparedInventoryItemTooltip(renderer)",
                  StringComparison.Ordinal) &&
              inventory.Contains("prepared.Skin.DrawBackdrop", StringComparison.Ordinal) &&
              inventory.Contains("WowSkin.Tooltip", StringComparison.Ordinal) &&
              inventory.Contains("GameText.Draw(draw, line.FontObject", StringComparison.Ordinal) &&
              !renderer.Contains("##vendor-template-tooltip-columns",
                  StringComparison.Ordinal) &&
              !renderer.Contains("ImGui.BeginTooltip()", StringComparison.Ordinal) &&
              !renderer.Contains("ImGui.BeginTable", StringComparison.Ordinal) &&
              !renderer.Contains("ImGui.TextColored", StringComparison.Ordinal),
            "Merchant item hover escaped the prepared GameText/WowSkin tooltip renderer");
    }

    private static int Count(string text, string needle)
    {
        int count = 0;
        for (int index = 0;;)
        {
            index = text.IndexOf(needle, index, StringComparison.Ordinal);
            if (index < 0) return count;
            count++;
            index += needle.Length;
        }
    }

    private static void CheckMoneyValues(uint copper, bool highestFirst,
        (MerchantFrameUiLaw.MoneyDenomination Denomination, uint Value)[] expected)
    {
        MerchantFrameUiLaw.MoneyValue[] actual =
            MerchantFrameUiLaw.VisibleMoney(copper, highestFirst);
        Check(actual.Length == expected.Length && actual.Select(value =>
                (value.Denomination, value.Value)).SequenceEqual(expected),
            $"merchant money visibility/order drifted for copper={copper}, highestFirst={highestFirst}");
    }

    private static void CheckMoneyCell(MerchantFrameUiLaw.MoneyCellGeometry cell,
        int frameSlot, MerchantFrameUiLaw.MoneyDenomination denomination, uint value,
        float numberWidth, float left, float right, float bottom)
    {
        Check(cell.FrameSlot == frameSlot && cell.Denomination == denomination &&
              cell.Value == value && cell.NumberWidth == numberWidth &&
              cell.Left == left && cell.Right == right && cell.Bottom == bottom &&
              cell.Width == numberWidth + 13f && cell.IconLeft == right - 13f,
            $"merchant money frame slot {frameSlot} geometry drifted");
    }

    private static void Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException(
            $"Expected {typeof(TException).Name} was not thrown.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
