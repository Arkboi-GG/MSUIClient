namespace MSUIClient.Formats;

/// <summary>QuestSort.dbc names used by negative QuestTemplate.ZoneOrSort log headers.</summary>
public sealed class QuestSortCatalog
{
    public const string MpqPath = @"DBFilesClient\QuestSort.dbc";
    private readonly Dictionary<uint, string> _names = [];

    public static QuestSortCatalog? Parse(byte[] data)
    {
        DbcFile? dbc = DbcFile.Parse(data);
        if (dbc is null || dbc.FieldCount < 2) return null;
        var result = new QuestSortCatalog();
        for (int row = 0; row < dbc.RecordCount; row++)
        {
            uint id = dbc.GetUInt(row, 0);
            string name = dbc.GetString(row, 1);
            if (id != 0 && name.Length != 0) result._names[id] = name;
        }
        Console.WriteLine($"[dbc] QuestSort: {result._names.Count} sort(s)");
        return result;
    }

    public string Name(uint id) => _names.GetValueOrDefault(id, "");
}
