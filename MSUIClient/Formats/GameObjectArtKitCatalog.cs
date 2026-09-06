namespace MSUIClient.Formats;

public sealed record GameObjectArtKit(uint Id, IReadOnlyList<string> Textures, IReadOnlyList<string> Attachments);

/// <summary>Build-5875 layout: ID, three texture variations and four attachment models.</summary>
public sealed class GameObjectArtKitCatalog
{
    public const string MpqPath = @"DBFilesClient\GameObjectArtKit.dbc";
    private readonly Dictionary<uint, GameObjectArtKit> _rows = [];
    public GameObjectArtKit? Find(uint id) => _rows.GetValueOrDefault(id);
    public static GameObjectArtKitCatalog? Load(MpqMount mpq) =>
        mpq.ReadFile(MpqPath) is { } bytes ? Parse(bytes) : null;
    public static GameObjectArtKitCatalog? Parse(byte[] bytes)
    {
        var dbc = DbcFile.Parse(bytes);
        if (dbc is null || dbc.FieldCount != 8 || dbc.RecordSize != 32) return null;
        var result = new GameObjectArtKitCatalog();
        for (int r = 0; r < dbc.RecordCount; r++)
        {
            uint id = dbc.GetUInt(r, 0);
            if (id == 0) continue;
            result._rows[id] = new(id,
                Array.AsReadOnly(Enumerable.Range(1, 3).Select(c => dbc.GetString(r, c)).ToArray()),
                Array.AsReadOnly(Enumerable.Range(4, 4).Select(c => dbc.GetString(r, c)).ToArray()));
        }
        return result;
    }
}
