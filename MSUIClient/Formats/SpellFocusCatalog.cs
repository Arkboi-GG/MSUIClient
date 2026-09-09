namespace MSUIClient.Formats;

/// <summary>Build-5875 names for the client-facing spell-focus requirements.</summary>
public sealed class SpellFocusCatalog
{
    public const string MpqPath = @"DBFilesClient\SpellFocusObject.dbc";
    private readonly Dictionary<uint, string> _names = [];

    public string? KnownName(uint id) => _names.GetValueOrDefault(id);
    public int Count => _names.Count;
    public string Name(uint id) => _names.TryGetValue(id, out string? name) && name.Length > 0
        ? name : $"Spell Focus {id}";

    public static SpellFocusCatalog? Load(MpqMount mpq)
    {
        byte[]? bytes = mpq.ReadFile(MpqPath);
        DbcFile? dbc = bytes is null ? null : DbcFile.Parse(bytes);
        if (dbc is null || dbc.FieldCount < 2) return null;
        var result = new SpellFocusCatalog();
        for (int row = 0; row < dbc.RecordCount; row++)
        {
            uint id = dbc.GetUInt(row, 0);
            string name = "";
            for (int field = 1; field < dbc.FieldCount; field++)
            {
                string? candidate = dbc.GetStringIfStart(row, field);
                if (!string.IsNullOrWhiteSpace(candidate)) { name = candidate; break; }
            }
            result._names[id] = name;
        }
        return result;
    }
}
