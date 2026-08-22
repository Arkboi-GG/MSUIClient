namespace MSUIClient.Formats;

public readonly record struct NpcGreeting(uint Hello, uint Goodbye, uint Pissed);

/// <summary>
/// Build-5875 NPC interaction voices. CreatureDisplayInfo field 11 names an
/// NPCSounds row; its hello/goodbye/pissed fields are SoundEntries kit ids.
/// This is deliberately separate from CreatureSoundData combat/body vocals.
/// </summary>
public sealed class NpcGreetingCatalog
{
    public const string SoundPath = @"DBFilesClient\NPCSounds.dbc";
    private readonly Dictionary<uint, uint> _displayToSound = [];
    private readonly Dictionary<uint, NpcGreeting> _rows = [];

    public int Count => _rows.Count;

    public bool TryGet(uint displayId, out NpcGreeting greeting)
    {
        greeting = default;
        return _displayToSound.TryGetValue(displayId, out uint soundId) &&
               _rows.TryGetValue(soundId, out greeting);
    }

    public static NpcGreetingCatalog? Load(MpqMount mpq)
    {
        DbcFile? sounds = mpq.ReadFile(SoundPath) is { } soundBytes
            ? DbcFile.Parse(soundBytes) : null;
        DbcFile? displays = mpq.ReadFile(CreatureDisplayInfoTable.MpqPath) is { } displayBytes
            ? DbcFile.Parse(displayBytes) : null;
        if (sounds is null || displays is null ||
            sounds.FieldCount != 5 || sounds.RecordSize != 20 ||
            displays.FieldCount != 12 || displays.RecordSize != 48)
            return null;

        var result = new NpcGreetingCatalog();
        for (int row = 0; row < sounds.RecordCount; row++)
        {
            uint id = sounds.GetUInt(row, 0);
            if (id != 0)
                result._rows[id] = new(sounds.GetUInt(row, 1), sounds.GetUInt(row, 2),
                    sounds.GetUInt(row, 3));
        }
        for (int row = 0; row < displays.RecordCount; row++)
        {
            uint displayId = displays.GetUInt(row, 0);
            uint soundId = displays.GetUInt(row, 11);
            if (displayId != 0 && soundId != 0)
                result._displayToSound[displayId] = soundId;
        }
        Console.WriteLine($"[dbc] NPC greetings: {result._rows.Count} sound row(s), " +
                          $"{result._displayToSound.Count} display mapping(s)");
        return result;
    }

    public static NpcGreetingCatalog FromRows(params
        (uint DisplayId, uint SoundId, uint Hello, uint Goodbye, uint Pissed)[] rows)
    {
        var result = new NpcGreetingCatalog();
        foreach (var row in rows)
        {
            result._displayToSound[row.DisplayId] = row.SoundId;
            result._rows[row.SoundId] = new(row.Hello, row.Goodbye, row.Pissed);
        }
        return result;
    }
}
