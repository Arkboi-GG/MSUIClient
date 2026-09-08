namespace MSUIClient.Formats;

/// <summary>Mounted stable-slot prices, indexed by the next purchased slot.</summary>
public sealed class StableSlotPriceTable
{
    public const string MpqPath = @"DBFilesClient\StableSlotPrices.dbc";
    private readonly Dictionary<int, uint> _prices = [];
    public uint? NextPrice(int purchased) =>
        _prices.TryGetValue(purchased + 1, out uint price) ? price : null;

    public static StableSlotPriceTable? Parse(byte[] bytes)
    {
        DbcFile? dbc = DbcFile.Parse(bytes);
        if (dbc is null || dbc.FieldCount < 2) return null;
        var table = new StableSlotPriceTable();
        for (int row = 0; row < dbc.RecordCount; row++)
            table._prices[dbc.GetInt(row, 0)] = dbc.GetUInt(row, 1);
        return table;
    }
}
