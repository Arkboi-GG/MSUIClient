using MSUIClient.Engine.UI;

namespace MSUIClient.Formats;

/// <summary>
/// Build-5875 client-side repair tables. DurabilityCosts is keyed by item level and stores the
/// twenty-one weapon columns followed by the eight armor columns; DurabilityQuality is keyed by
/// the odd row id selected by <see cref="MerchantFrameUiLaw.DurabilityQualityRowKey"/>.
///
/// Both files are one optional resource: if either file is absent, malformed, or has a different
/// schema, <see cref="Load"/> returns <see langword="null"/>. Merchant callers therefore display
/// zero repair cost and leave repair-all disabled instead of inventing fallback prices.
/// </summary>
public sealed class DurabilityTables
{
    public const string DurabilityCostsMpqPath = @"DBFilesClient\DurabilityCosts.dbc";
    public const string DurabilityQualityMpqPath = @"DBFilesClient\DurabilityQuality.dbc";

    public const int WeaponCostColumnCount = 21;
    public const int ArmorCostColumnCount = 8;
    public const int CostVectorLength = WeaponCostColumnCount + ArmorCostColumnCount;
    public const int DurabilityCostsFieldCount = 1 + CostVectorLength;
    public const int DurabilityQualityFieldCount = 2;

    private readonly Dictionary<uint, uint[]> _costs;
    private readonly Dictionary<uint, float> _qualities;

    private DurabilityTables(Dictionary<uint, uint[]> costs,
        Dictionary<uint, float> qualities)
    {
        _costs = costs;
        _qualities = qualities;
    }

    public int CostRowCount => _costs.Count;
    public int QualityRowCount => _qualities.Count;

    /// <summary>
    /// Load the two tables from the mounted patch chain. Patch precedence remains entirely owned
    /// by <see cref="MpqMount"/>; no loose-file or generated-data fallback is used.
    /// </summary>
    public static DurabilityTables? Load(MpqMount mpq)
    {
        ArgumentNullException.ThrowIfNull(mpq);

        byte[]? costBytes = mpq.ReadFile(DurabilityCostsMpqPath);
        if (costBytes is null || ParseCosts(costBytes) is not { } costs) return null;

        byte[]? qualityBytes = mpq.ReadFile(DurabilityQualityMpqPath);
        if (qualityBytes is null || ParseQualities(qualityBytes) is not { } qualities) return null;

        return new DurabilityTables(costs, qualities);
    }

    /// <summary>
    /// Parse an already-resolved pair of DBC files. This is the deterministic seam used by the
    /// clinical fixture; production should normally call <see cref="Load"/>.
    /// </summary>
    public static DurabilityTables? Parse(byte[] durabilityCosts, byte[] durabilityQuality)
    {
        ArgumentNullException.ThrowIfNull(durabilityCosts);
        ArgumentNullException.ThrowIfNull(durabilityQuality);

        if (ParseCosts(durabilityCosts) is not { } costs) return null;
        if (ParseQualities(durabilityQuality) is not { } qualities) return null;
        return new DurabilityTables(costs, qualities);
    }

    /// <summary>
    /// Resolve a zero-based cost-vector cell. Indices 0..20 are weapon subclasses and 21..28 are
    /// armor subclasses. A missing level row or out-of-range column returns <see langword="null"/>.
    /// </summary>
    public uint? Cost(uint itemLevel, int costVectorIndex)
    {
        if (costVectorIndex is < 0 or >= CostVectorLength ||
            !_costs.TryGetValue(itemLevel, out uint[]? row))
            return null;
        return row[costVectorIndex];
    }

    /// <summary>Resolve the raw float multiplier by its DBC row key.</summary>
    public float? QualityMultiplier(uint rowKey) =>
        _qualities.TryGetValue(rowKey, out float multiplier) ? multiplier : null;

    /// <summary>
    /// Resolve both DBC cells selected by the frozen MerchantFrame law and apply that law's exact
    /// positive-half-away then discounted-nearest-even arithmetic.
    /// </summary>
    public uint RepairCost(in MerchantFrameUiLaw.RepairItem item,
        float reputationDiscount = 0f)
    {
        MerchantFrameUiLaw.RepairTableLookup? lookup = MerchantFrameUiLaw.RepairLookup(item);
        uint? cost = lookup is { } found
            ? Cost(found.DurabilityCostsRowKey, found.CostVectorIndex)
            : null;
        float? quality = lookup is { } resolved
            ? QualityMultiplier(resolved.DurabilityQualityRowKey)
            : null;
        return MerchantFrameUiLaw.RepairCost(item, cost, quality, reputationDiscount);
    }

    private static Dictionary<uint, uint[]>? ParseCosts(byte[] data)
    {
        DbcFile? dbc = DbcFile.Parse(data);
        if (dbc is null || dbc.FieldCount != DurabilityCostsFieldCount ||
            dbc.RecordSize != DurabilityCostsFieldCount * sizeof(uint))
            return null;

        var result = new Dictionary<uint, uint[]>();
        for (int row = 0; row < dbc.RecordCount; row++)
        {
            uint id = dbc.GetUInt(row, 0);
            var costs = new uint[CostVectorLength];
            for (int column = 0; column < costs.Length; column++)
                costs[column] = dbc.GetUInt(row, column + 1);
            // The frozen loader is id-keyed and the last duplicate row wins.
            result[id] = costs;
        }
        return result;
    }

    private static Dictionary<uint, float>? ParseQualities(byte[] data)
    {
        DbcFile? dbc = DbcFile.Parse(data);
        if (dbc is null || dbc.FieldCount != DurabilityQualityFieldCount ||
            dbc.RecordSize != DurabilityQualityFieldCount * sizeof(uint))
            return null;

        var result = new Dictionary<uint, float>();
        for (int row = 0; row < dbc.RecordCount; row++)
            // Preserve the raw float, including non-finite values. The arithmetic law owns
            // validation when (and only when) a selected row is actually used.
            result[dbc.GetUInt(row, 0)] = dbc.GetFloat(row, 1);
        return result;
    }
}
