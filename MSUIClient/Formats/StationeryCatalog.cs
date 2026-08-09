namespace MSUIClient.Formats;

/// <summary>Build-5875 Stationery.dbc id-to-texture-stem lookup used by mailed letters.</summary>
public sealed class StationeryCatalog
{
    public const string MpqPath = @"DBFilesClient\Stationery.dbc";
    public const string DefaultTexture = "STATIONERYTEST";
    private readonly Dictionary<uint, string> _textures = [];

    public int Count => _textures.Count;

    public string Texture(uint id) =>
        _textures.TryGetValue(id, out string? texture) && texture.Length > 0
            ? texture : DefaultTexture;

    public static StationeryCatalog? Load(MpqMount mpq)
    {
        byte[]? bytes = mpq.ReadFile(MpqPath);
        DbcFile? dbc = bytes is null ? null : DbcFile.Parse(bytes);
        if (dbc is null || dbc.FieldCount < 4) return null;
        var result = new StationeryCatalog();
        for (int row = 0; row < dbc.RecordCount; row++)
        {
            uint id = dbc.GetUInt(row, 0);
            string? texture = dbc.GetStringIfStart(row, 2);
            if (!string.IsNullOrWhiteSpace(texture)) result._textures[id] = texture;
        }
        return result;
    }
}
