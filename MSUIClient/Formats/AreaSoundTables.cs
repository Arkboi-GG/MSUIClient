namespace MSUIClient.Formats;

// ─────────────────────────────────────────────────────────────────────────────
// The three zone-audio DBCs (SYSTEM: world soundscape). Layouts verified
// against benilla-formats/src/area_sound.rs, which byte-verified them against
// the shipped 5875 files:
//
//   ZoneMusic.dbc            99 x 8 x 32 B    ID, SetName, SilenceMin[2],
//                                             SilenceMax[2], Sounds[2]
//   SoundAmbience.dbc        68 x 3 x 12 B    ID, AmbienceID[2]
//   ZoneIntroMusicTable.dbc  43 x 5 x 20 B    ID, Name, SoundID, Priority,
//                                             MinDelayMinutes
//
// Every [2] array is [day, night]; silence intervals are MILLISECONDS; every
// sound value is a SoundEntries.dbc kit id. AreaTable.dbc columns 7/8/9 are
// the FKs into these (AreaTableCatalog.ResolveAudio).
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>One zone-music set: which kit to play by day phase, and how long
/// to stay silent between tracks.</summary>
public readonly record struct ZoneMusicEntry(
    uint Id, string SetName,
    uint SilenceMinDayMs, uint SilenceMinNightMs,
    uint SilenceMaxDayMs, uint SilenceMaxNightMs,
    uint DaySound, uint NightSound)
{
    public uint Sound(bool day) => day ? DaySound : NightSound;
    public uint SilenceMinMs(bool day) => day ? SilenceMinDayMs : SilenceMinNightMs;
    public uint SilenceMaxMs(bool day) => day ? SilenceMaxDayMs : SilenceMaxNightMs;
}

public sealed class ZoneMusicTable
{
    public const string MpqPath = @"DBFilesClient\ZoneMusic.dbc";

    private readonly Dictionary<uint, ZoneMusicEntry> _rows = new();
    public int Count => _rows.Count;

    public bool TryGet(uint id, out ZoneMusicEntry entry) => _rows.TryGetValue(id, out entry);

    public static ZoneMusicTable? Parse(byte[] data)
    {
        var dbc = DbcFile.Parse(data);
        if (dbc is null || dbc.FieldCount < 8) return null;
        var t = new ZoneMusicTable();
        for (int r = 0; r < dbc.RecordCount; r++)
        {
            uint id = dbc.GetUInt(r, 0);
            t._rows[id] = new ZoneMusicEntry(
                id, dbc.GetString(r, 1),
                dbc.GetUInt(r, 2), dbc.GetUInt(r, 3),
                dbc.GetUInt(r, 4), dbc.GetUInt(r, 5),
                dbc.GetUInt(r, 6), dbc.GetUInt(r, 7));
        }
        Console.WriteLine($"[dbc] ZoneMusic: {t.Count} set(s)");
        return t;
    }
}

/// <summary>One ambience pair: the day and night loop kits for a zone.</summary>
public readonly record struct SoundAmbienceEntry(uint Id, uint DayKit, uint NightKit)
{
    public uint Kit(bool day) => day ? DayKit : NightKit;
}

public sealed class SoundAmbienceTable
{
    public const string MpqPath = @"DBFilesClient\SoundAmbience.dbc";

    private readonly Dictionary<uint, SoundAmbienceEntry> _rows = new();
    public int Count => _rows.Count;

    public bool TryGet(uint id, out SoundAmbienceEntry entry) => _rows.TryGetValue(id, out entry);

    public static SoundAmbienceTable? Parse(byte[] data)
    {
        var dbc = DbcFile.Parse(data);
        if (dbc is null || dbc.FieldCount < 3) return null;
        var t = new SoundAmbienceTable();
        for (int r = 0; r < dbc.RecordCount; r++)
        {
            uint id = dbc.GetUInt(r, 0);
            t._rows[id] = new SoundAmbienceEntry(id, dbc.GetUInt(r, 1), dbc.GetUInt(r, 2));
        }
        Console.WriteLine($"[dbc] SoundAmbience: {t.Count} pair(s)");
        return t;
    }
}

/// <summary>One intro-music row: the fanfare kit played on zone entry, and the
/// per-row minimum delay before it may play again.</summary>
public readonly record struct ZoneIntroMusicEntry(
    uint Id, string Name, uint SoundId, uint Priority, uint MinDelayMinutes);

public sealed class ZoneIntroMusicTable
{
    public const string MpqPath = @"DBFilesClient\ZoneIntroMusicTable.dbc";

    private readonly Dictionary<uint, ZoneIntroMusicEntry> _rows = new();
    public int Count => _rows.Count;

    public bool TryGet(uint id, out ZoneIntroMusicEntry entry) => _rows.TryGetValue(id, out entry);

    public static ZoneIntroMusicTable? Parse(byte[] data)
    {
        var dbc = DbcFile.Parse(data);
        if (dbc is null || dbc.FieldCount < 5) return null;
        var t = new ZoneIntroMusicTable();
        for (int r = 0; r < dbc.RecordCount; r++)
        {
            uint id = dbc.GetUInt(r, 0);
            t._rows[id] = new ZoneIntroMusicEntry(
                id, dbc.GetString(r, 1), dbc.GetUInt(r, 2),
                dbc.GetUInt(r, 3), dbc.GetUInt(r, 4));
        }
        Console.WriteLine($"[dbc] ZoneIntroMusicTable: {t.Count} intro(s)");
        return t;
    }
}
