using MSUIClient;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

internal static class ProficiencyClinicalChecks
{
    public static void Run()
    {
        ProficiencyPacket warriorWeapons = ProficiencyPackets.Parse(
            Convert.FromHexString("028F400200"));
        Check(warriorWeapons == new ProficiencyPacket(2, 0x0002_408f) &&
              (ushort)Op.SMSG_SET_PROFICIENCY == 0x0127,
            "proficiency packet/opcode drift");
        CheckThrows(() => ProficiencyPackets.Parse(new byte[4]),
            "truncated proficiency packet accepted");
        CheckThrows(() => ProficiencyPackets.Parse(new byte[6]),
            "proficiency trailing byte accepted");

        var twoHandOnly = new Dictionary<uint, uint> { [2] = 1u << 1 };
        InventoryUiLaw.ProficiencyColors allowed = InventoryUiLaw.ItemProficiencyColors(
            2, 1, 17, twoHandOnly, 0, canDualWield: false);
        Check(!allowed.SlotRed && !allowed.TypeRed,
            "own-subclass proficiency should leave both cells white");

        var oneHandOnly = new Dictionary<uint, uint> { [2] = 1u << 0 };
        InventoryUiLaw.ProficiencyColors hardMiss = InventoryUiLaw.ItemProficiencyColors(
            2, 1, 17, oneHandOnly, null, canDualWield: false);
        InventoryUiLaw.ProficiencyColors alternate = InventoryUiLaw.ItemProficiencyColors(
            2, 1, 17, oneHandOnly, 0, canDualWield: false);
        Check(!hardMiss.SlotRed && hardMiss.TypeRed &&
              alternate.SlotRed && !alternate.TypeRed,
            "hard/alternate proficiency cell split drift");

        var daggers = new Dictionary<uint, uint> { [2] = 1u << 15 };
        InventoryUiLaw.ProficiencyColors noDual = InventoryUiLaw.ItemProficiencyColors(
            2, 15, 22, daggers, null, canDualWield: false);
        InventoryUiLaw.ProficiencyColors dual = InventoryUiLaw.ItemProficiencyColors(
            2, 15, 22, daggers, null, canDualWield: true);
        InventoryUiLaw.ProficiencyColors undeclared = InventoryUiLaw.ItemProficiencyColors(
            4, 4, 5, daggers, null, canDualWield: false);
        Check(noDual.SlotRed && !noDual.TypeRed &&
              !dual.SlotRed && !dual.TypeRed &&
              !undeclared.SlotRed && !undeclared.TypeRed,
            "dual-wield/undeclared proficiency coloring drift");
        Check(!InventoryUiLaw.IsItemProficient(2, 1, oneHandOnly) &&
              InventoryUiLaw.IsItemProficient(2, 0, oneHandOnly) &&
              InventoryUiLaw.IsItemProficient(4, 4, oneHandOnly),
            "basic item usability must check the own subclass only");
        Check(InventoryUiLaw.InventoryTypeName(17) == "Two-Hand" &&
              InventoryUiLaw.InventoryTypeName(22) == "Off Hand" &&
              InventoryUiLaw.InventoryTypeName(18) is null,
            "inventory-type tooltip vocabulary drift");

        string root = ClientConfig.FindRepoRoot();
        string dispatch = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Net.cs"));
        string runtime = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Inventory.cs"));
        string subclass = SourceText.Read(Path.Combine(root, "MSUIClient", "Formats",
            "ItemSubClassCatalog.cs"));
        string vendor = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Vendor.Render.cs"));
        string reset = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Combat",
            "GameLoop.Targeting.cs"));
        Check(dispatch.Contains("case Op.SMSG_SET_PROFICIENCY", StringComparison.Ordinal) &&
              dispatch.Contains("ProficiencyPackets.Parse(body)", StringComparison.Ordinal) &&
              runtime.Contains("PreparedItemTooltipPair(slot", StringComparison.Ordinal) &&
              runtime.Contains("spell.EffectIds[0] == 40", StringComparison.Ordinal) &&
              subclass.Contains("int prerequisite = dbc.GetInt(row, 2)", StringComparison.Ordinal) &&
              subclass.Contains("dbc.GetUInt(row, 5) & 1", StringComparison.Ordinal) &&
              vendor.Contains("InventoryUiLaw.IsItemProficient", StringComparison.Ordinal) &&
              reset.Contains("_itemProficiencies.Clear()", StringComparison.Ordinal),
            "proficiency tooltip/usability runtime wiring drift");
    }

    private static void CheckThrows(Action action, string message)
    {
        try { action(); }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException) { return; }
        throw new InvalidDataException(message);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
