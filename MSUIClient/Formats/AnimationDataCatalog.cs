namespace MSUIClient.Formats;

/// <summary>One AnimationData.dbc row: the id a SpellVisualKit (field 2), an emote or a
/// state names, and the name Blizzard gave it (Stand, Attack1H, SpellCastDirected ...).</summary>
public readonly record struct AnimationDataRow(ushort Id, string Name);

/// <summary>AnimationData.dbc id -> name (208 rows in 1.12: id, name, weapon flags, body
/// flags, flags, fallback, behaviour id, behaviour tier). The workshop's animation picker
/// lists these BY NAME so a caster animation is chosen the way an author thinks about it -
/// "a two-handed swing", not "18" - and log lines can say what an id was.</summary>
public sealed class AnimationDataCatalog
{
    public const string MpqPath = @"DBFilesClient\AnimationData.dbc";
    private readonly Dictionary<ushort, string> _names = [];
    private readonly List<AnimationDataRow> _rows = [];

    /// <summary>Every row, in id order.</summary>
    public IReadOnlyList<AnimationDataRow> Rows => _rows;

    public int Count => _rows.Count;

    public bool TryGetName(int id, out string name)
    {
        if (id >= 0 && id <= ushort.MaxValue && _names.TryGetValue((ushort)id, out string? found))
        {
            name = found;
            return true;
        }
        name = "";
        return false;
    }

    /// <summary>The name, or "Anim&lt;id&gt;" for an id the table does not carry.</summary>
    public string Name(int id) => TryGetName(id, out string name) ? name : $"Anim{id}";

    public static AnimationDataCatalog? Load(MpqMount mpq)
    {
        byte[]? bytes = mpq.ReadFile(MpqPath);
        DbcFile? dbc = bytes is null ? null : DbcFile.Parse(bytes);
        if (dbc is null || dbc.FieldCount < 2) return null;
        var result = new AnimationDataCatalog();
        for (int row = 0; row < dbc.RecordCount; row++)
        {
            uint id = dbc.GetUInt(row, 0);
            if (id > ushort.MaxValue) continue;
            string name = dbc.GetString(row, 1);
            if (name.Length == 0) continue;
            result._names[(ushort)id] = name;
            result._rows.Add(new AnimationDataRow((ushort)id, name));
        }
        result._rows.Sort((a, b) => a.Id.CompareTo(b.Id));
        Console.WriteLine($"[dbc] AnimationData: {result._rows.Count} row(s)");
        return result;
    }
}
