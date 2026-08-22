namespace MSUIClient.Formats;

/// <summary>
/// Build-5875 EmotesTextSound.dbc: (EmotesText id, race, sex) to the
/// performer's SoundEntries voice kit. Sex is 0 male / 1 female.
/// </summary>
public sealed class EmoteTextSoundCatalog
{
    public const string MpqPath = @"DBFilesClient\EmotesTextSound.dbc";
    private readonly Dictionary<(uint TextEmote, uint Race, uint Sex), uint> _voices = [];

    public int Count => _voices.Count;
    public bool TryGet(uint textEmote, uint race, uint sex, out uint soundId)
        => _voices.TryGetValue((textEmote, race, sex), out soundId) && soundId != 0;

    public static EmoteTextSoundCatalog? Load(MpqMount mpq)
    {
        DbcFile? dbc = mpq.ReadFile(MpqPath) is { } bytes ? DbcFile.Parse(bytes) : null;
        if (dbc is null || dbc.FieldCount != 5 || dbc.RecordSize != 20) return null;
        var result = new EmoteTextSoundCatalog();
        for (int row = 0; row < dbc.RecordCount; row++)
        {
            uint text = dbc.GetUInt(row, 1);
            uint race = dbc.GetUInt(row, 2);
            uint sex = dbc.GetUInt(row, 3);
            uint kit = dbc.GetUInt(row, 4);
            if (text != 0 && race != 0 && kit != 0) result._voices[(text, race, sex)] = kit;
        }
        return result;
    }

    public static EmoteTextSoundCatalog FromRows(params
        (uint TextEmote, uint Race, uint Sex, uint SoundId)[] rows)
    {
        var result = new EmoteTextSoundCatalog();
        foreach (var row in rows)
            result._voices[(row.TextEmote, row.Race, row.Sex)] = row.SoundId;
        return result;
    }
}
