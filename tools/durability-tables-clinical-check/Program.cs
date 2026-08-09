using MSUIClient.Engine.UI;
using MSUIClient.Formats;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidDataException(message);
}

static byte[] BuildWdbc(uint[][] rows)
{
    int fields = rows.Length == 0 ? 0 : rows[0].Length;
    if (rows.Any(row => row.Length != fields))
        throw new ArgumentException("fixture rows must have equal widths", nameof(rows));

    using var stream = new MemoryStream();
    using var writer = new BinaryWriter(stream);
    writer.Write("WDBC"u8);
    writer.Write(rows.Length);
    writer.Write(fields);
    writer.Write(checked(fields * sizeof(uint)));
    writer.Write(0); // no strings
    foreach (uint[] row in rows)
    foreach (uint cell in row)
        writer.Write(cell);
    writer.Flush();
    return stream.ToArray();
}

Check(DurabilityTables.DurabilityCostsMpqPath ==
      @"DBFilesClient\DurabilityCosts.dbc" &&
      DurabilityTables.DurabilityQualityMpqPath ==
      @"DBFilesClient\DurabilityQuality.dbc",
    "build-5875 durability DBC path drifted");
Check(DurabilityTables.WeaponCostColumnCount == 21 &&
      DurabilityTables.ArmorCostColumnCount == 8 &&
      DurabilityTables.CostVectorLength == 29 &&
      DurabilityTables.DurabilityCostsFieldCount == 30 &&
      DurabilityTables.DurabilityQualityFieldCount == 2,
    "build-5875 durability schema width drifted");

uint[] first = new uint[29];
first[7] = 4;       // weapon subclass 7
first[21 + 3] = 2;  // armor subclass 3
uint[] replacement = new uint[29];
replacement[7] = 9;
replacement[21 + 3] = 2;
byte[] costs = BuildWdbc([
    [23u, .. first],
    [23u, .. replacement], // duplicate ids are last-row-wins
]);
byte[] qualities = BuildWdbc([
    [3u, BitConverter.SingleToUInt32Bits(.6f)],
]);

DurabilityTables tables = DurabilityTables.Parse(costs, qualities) ??
    throw new InvalidDataException("valid durability fixtures did not parse");
Check(tables.CostRowCount == 1 && tables.QualityRowCount == 1,
    "durability tables did not retain id-keyed last-row-wins storage");
Check(tables.Cost(23, 7) == 9 && tables.Cost(23, 24) == 2 &&
      tables.Cost(23, -1) is null && tables.Cost(23, 29) is null &&
      tables.Cost(99, 7) is null,
    "durability cost-vector lookup drifted");
Check(tables.QualityMultiplier(3) == .6f &&
      tables.QualityMultiplier(5) is null,
    "durability quality lookup drifted");

var sword = new MerchantFrameUiLaw.RepairItem(2, 7, 23, 1, 90, 100);
Check(tables.RepairCost(sword) == 54,
    "catalog did not feed the selected DBC cells into the MerchantFrame repair law");
var mail = new MerchantFrameUiLaw.RepairItem(4, 3, 23, 1, 13, 20);
Check(tables.RepairCost(mail) == 8,
    "armor subclass did not select cost-vector offset 21");
var missingLevel = sword with { ItemLevel = 99 };
var missingQuality = sword with { Quality = 2 };
var unrepairable = sword with { ItemClass = 15 };
Check(tables.RepairCost(missingLevel) == 0 &&
      tables.RepairCost(missingQuality) == 0 &&
      tables.RepairCost(unrepairable) == 0,
    "missing or unrepairable durability input invented a repair price");

Check(DurabilityTables.Parse(
        BuildWdbc([[1u, 2u]]), qualities) is null,
    "two-field DurabilityCosts fixture bypassed the exact 30-field schema");
Check(DurabilityTables.Parse(costs,
        BuildWdbc([[1u, 2u, 3u]])) is null,
    "three-field DurabilityQuality fixture bypassed the exact two-field schema");
Check(DurabilityTables.Parse([], qualities) is null &&
      DurabilityTables.Parse(costs, []) is null,
    "malformed durability input did not fail the paired resource closed");

Console.WriteLine("durability-tables-clinical-check: PASS");
