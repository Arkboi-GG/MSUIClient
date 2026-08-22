namespace MSUIClient.Formats;

public readonly record struct EmoteInfo(uint AnimationId, uint EventSoundId);

/// <summary>Emotes.dbc id -> AnimationData one-shot and EventSoundID kit.</summary>
public sealed class EmoteCatalog
{
    public const string MpqPath = @"DBFilesClient\Emotes.dbc";
    private readonly Dictionary<uint, EmoteInfo> _rows = [];

    public bool TryGet(uint id, out EmoteInfo info) => _rows.TryGetValue(id, out info);

    public static EmoteCatalog? Load(MpqMount mpq)
    {
        byte[]? bytes = mpq.ReadFile(MpqPath);
        DbcFile? dbc = bytes is null ? null : DbcFile.Parse(bytes);
        if (dbc is null || dbc.FieldCount < 7) return null;
        var result = new EmoteCatalog();
        for (int row = 0; row < dbc.RecordCount; row++)
        {
            uint id = dbc.GetUInt(row, 0);
            if (id != 0)
                result._rows[id] = new(dbc.GetUInt(row, 2), dbc.GetUInt(row, 6));
        }
        Console.WriteLine($"[dbc] Emotes: {result._rows.Count} row(s)");
        return result;
    }
}
