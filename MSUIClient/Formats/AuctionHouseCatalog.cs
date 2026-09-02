namespace MSUIClient.Formats;

/// <summary>
/// Build-5875 AuctionHouse.dbc: ID, FactionID, DepositPercent, CutPercent, Name[8]+mask.
/// Seven rows: the six faction houses (deposit 5%) and Blackwater (id 7, deposit 25%). The
/// hello reply names the row the auctioneer serves, and the sell pane's deposit is that row's
/// rate — a window that assumes one rate shows the goblin 25% where the faction house asks 5%.
/// </summary>
public sealed class AuctionHouseCatalog
{
    public const string MpqPath = @"DBFilesClient\AuctionHouse.dbc";
    public readonly record struct House(uint Id, uint FactionId, uint DepositPercent,
        uint CutPercent, string Name);

    private readonly Dictionary<uint, House> _houses = [];

    public bool TryGet(uint houseId, out House house) => _houses.TryGetValue(houseId, out house);

    /// <summary>The deposit rate for a house id, or null for an id the table does not carry.</summary>
    public uint? DepositPercent(uint houseId) =>
        _houses.TryGetValue(houseId, out House house) ? house.DepositPercent : null;

    public static AuctionHouseCatalog? Load(MpqMount mpq)
    {
        byte[]? bytes = mpq.ReadFile(MpqPath);
        DbcFile? dbc = bytes is null ? null : DbcFile.Parse(bytes);
        if (dbc is null || dbc.FieldCount < 5) return null;
        var result = new AuctionHouseCatalog();
        for (int row = 0; row < dbc.RecordCount; row++)
        {
            uint id = dbc.GetUInt(row, 0);
            result._houses[id] = new House(id, dbc.GetUInt(row, 1), dbc.GetUInt(row, 2),
                dbc.GetUInt(row, 3), dbc.GetString(row, 4));
        }
        return result;
    }
}
