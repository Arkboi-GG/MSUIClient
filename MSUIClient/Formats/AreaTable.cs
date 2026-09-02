namespace MSUIClient.Formats;

// AreaTable.dbc - the zone/area catalog. id -> (parent zone, display name). Used by the
// character-select roster to label each character's row with its zone ("Stormwind City").
//
// Layout VERIFIED against 1.12.1 build 5875 (benilla-formats/src/area_table.rs): 25 u32-wide
// columns; ID = col 0, parent ZoneID = col 2 (0 = this row IS a top-level zone), exploration
// flag = col 3, and the localized AreaName (enUS) = col 11.
public sealed class AreaTableCatalog
{
    public const string MpqPath = @"DBFilesClient\AreaTable.dbc";

    // id -> (parent area id, own display name, PLAYER_EXPLORED_ZONES bit index,
    // and the three audio FKs: SoundAmbience row, ZoneMusic row, ZoneIntroMusicTable row).
    // Parent 0 = top-level zone. Audio columns 7/8/9 verified against
    // benilla-formats/src/area_sound.rs (1081 x 25 x 100 B).
    private readonly Dictionary<uint, (uint Parent, string Name, uint ExploreFlag,
        uint Flags, uint FactionGroupMask, uint AmbienceId, uint ZoneMusicId,
        uint IntroSoundId)> _rows = new();
    public int Count => _rows.Count;

    public static AreaTableCatalog? Parse(byte[] data)
    {
        var dbc = DbcFile.Parse(data);
        if (dbc is null || dbc.FieldCount < 12) return null;   // need through col 11 (AreaName)
        var t = new AreaTableCatalog();
        for (int r = 0; r < dbc.RecordCount; r++)
        {
            uint id = dbc.GetUInt(r, 0);
            uint parent = dbc.GetUInt(r, 2);
            uint exploreFlag = dbc.GetUInt(r, 3);
            uint flags = dbc.GetUInt(r, 4);
            uint ambience = dbc.GetUInt(r, 7);
            uint zoneMusic = dbc.GetUInt(r, 8);
            uint introSound = dbc.GetUInt(r, 9);
            string name = dbc.GetString(r, 11);
            uint factionGroupMask = dbc.FieldCount > 20 ? dbc.GetUInt(r, 20) : 0;
            t._rows[id] = (parent, name, exploreFlag, flags, factionGroupMask,
                ambience, zoneMusic, introSound);
        }
        Console.WriteLine($"[dbc] AreaTable: {t.Count} area(s)");
        return t;
    }

    /// <summary>
    /// The audio FKs that apply at an area, walking ParentAreaID up to 8 levels
    /// and taking the first nonzero value PER FIELD independently - so a subzone
    /// with no music of its own inherits the zone's while keeping its own
    /// ambience. Mirrors benilla's AreaSoundCatalog::resolve.
    /// </summary>
    public (uint AmbienceId, uint ZoneMusicId, uint IntroSoundId) ResolveAudio(uint areaId)
    {
        uint ambience = 0, music = 0, intro = 0;
        uint id = areaId;
        for (int i = 0; i < 8 && id != 0; i++)
        {
            if (!_rows.TryGetValue(id, out var row)) break;
            if (ambience == 0) ambience = row.AmbienceId;
            if (music == 0) music = row.ZoneMusicId;
            if (intro == 0) intro = row.IntroSoundId;
            if (ambience != 0 && music != 0 && intro != 0) break;
            id = row.Parent;
        }
        return (ambience, music, intro);
    }

    /// <summary>
    /// The label to show for an area id: the area's own name, or - if this row is an unnamed subzone -
    /// the nearest named parent zone. Empty when the id is unknown, 0, or unnamed all the way up (the
    /// roster then draws no location line, as 1.12 does for a 0/unknown zone).
    /// </summary>
    public string ZoneName(uint areaId)
    {
        uint id = areaId;
        for (int i = 0; i < 8 && id != 0; i++)   // depth-guarded; 1.12 parent chains are 1-2 deep
        {
            if (!_rows.TryGetValue(id, out var row)) break;
            if (!string.IsNullOrEmpty(row.Name)) return row.Name;
            id = row.Parent;
        }
        return "";
    }

    /// <summary>The authored sub-zone name without collapsing it to its parent.</summary>
    public string AreaName(uint areaId) =>
        _rows.TryGetValue(areaId, out var row) ? row.Name : "";

    /// <summary>Resolve an exact area name, preferring a top-level zone over a subzone twin.</summary>
    public uint? IdForName(string name) => _rows
        .Where(pair => pair.Value.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        .OrderBy(pair => pair.Value.Parent == 0 ? 0 : 1)
        .ThenBy(pair => pair.Key)
        .Select(pair => (uint?)pair.Key)
        .FirstOrDefault();

    /// <summary>
    /// Bit index in the owning player's 64-word PLAYER_EXPLORED_ZONES array.
    /// Returns null for an unknown row; zero is a valid authored flag.
    /// </summary>
    public uint? ExplorationFlag(uint areaId) =>
        _rows.TryGetValue(areaId, out var row) ? row.ExploreFlag : null;

    /// <summary>The first row (file order) carrying every bit of <paramref name="flagBits"/> — e.g. the 0x200 city-word sentinel.</summary>
    public uint? FirstIdWithFlag(uint flagBits)
    {
        uint? best = null;
        foreach ((uint id, var row) in _rows)
            if ((row.Flags & flagBits) == flagBits && (best is null || id < best)) best = id;
        return best;
    }

    public uint? Flags(uint areaId) =>
        _rows.TryGetValue(areaId, out var row) ? row.Flags : null;

    public uint? FactionGroupMask(uint areaId) =>
        _rows.TryGetValue(areaId, out var row) ? row.FactionGroupMask : null;

    /// <summary>
    /// Top-level zone containing an area. CMSG_ZONEUPDATE carries this value;
    /// the visible minimap text still uses <see cref="AreaName"/>.
    /// </summary>
    public uint ParentZoneId(uint areaId)
    {
        uint id = areaId;
        for (int i = 0; i < 8 && id != 0; i++)
        {
            if (!_rows.TryGetValue(id, out var row)) return 0;
            if (row.Parent == 0) return id;
            id = row.Parent;
        }
        return 0;
    }
}
