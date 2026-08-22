namespace MSUIClient.Formats;

/// <summary>Build-5875 PageTextMaterial.dbc id-to-art-basename map.</summary>
public sealed class PageTextMaterialCatalog
{
    public const string MpqPath = @"DBFilesClient\PageTextMaterial.dbc";
    private readonly Dictionary<uint, string> _names = [];

    public string Name(uint id) => _names.GetValueOrDefault(id, "Parchment");

    public static PageTextMaterialCatalog? Load(MpqMount mpq)
    {
        byte[]? bytes = mpq.ReadFile(MpqPath);
        DbcFile? dbc = bytes is null ? null : DbcFile.Parse(bytes);
        if (dbc is null || dbc.FieldCount < 2) return null;
        var result = new PageTextMaterialCatalog();
        for (int row = 0; row < dbc.RecordCount; row++)
        {
            uint id = dbc.GetUInt(row, 0);
            string name = dbc.GetString(row, 1);
            if (id != 0 && !string.IsNullOrWhiteSpace(name)) result._names[id] = name;
        }
        return result;
    }
}
