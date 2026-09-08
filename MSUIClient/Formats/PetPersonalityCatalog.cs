namespace MSUIClient.Formats;

public readonly record struct PetHappinessInfo(int Bucket, float DamagePercent, float LoyaltyRate);

/// <summary>Mounted PetPersonality.dbc thresholds, damage and loyalty-rate triples.</summary>
public sealed class PetPersonalityCatalog
{
    public const string MpqPath = @"DBFilesClient\PetPersonality.dbc";
    private sealed record Row(uint[] Thresholds, float[] Damage, float[] Loyalty);
    private readonly Dictionary<uint, Row> _rows = [];

    public PetHappinessInfo? Happiness(uint raw, uint personality = 0)
    {
        // The template personality selector is not exposed by this protocol. Use the
        // original client's missing-selector fallback row 1, rather than hardcoded rates.
        if (!_rows.TryGetValue(personality, out Row? row) && !_rows.TryGetValue(1, out row))
            return null;
        int bucket = row.Thresholds.TakeWhile(value => raw >= value).Count();
        return bucket == 0 ? new(0, 100, 0) :
            new(bucket, row.Damage[bucket - 1] * 100f, row.Loyalty[bucket - 1]);
    }

    public static PetPersonalityCatalog? Parse(byte[] bytes)
    {
        DbcFile? dbc = DbcFile.Parse(bytes);
        if (dbc is null || dbc.FieldCount < 19) return null;
        var result = new PetPersonalityCatalog();
        for (int row = 0; row < dbc.RecordCount; row++)
            result._rows[dbc.GetUInt(row, 0)] = new(
                Enumerable.Range(10, 3).Select(col => dbc.GetUInt(row, col)).ToArray(),
                Enumerable.Range(13, 3).Select(col => dbc.GetFloat(row, col)).ToArray(),
                Enumerable.Range(16, 3).Select(col => dbc.GetFloat(row, col)).ToArray());
        return result;
    }
}
