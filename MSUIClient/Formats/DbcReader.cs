using System.Text;

namespace MSUIClient.Formats;

/// <summary>
/// Reader for Blizzard's WDBC tables, and the typed rows this client needs
/// from them.
///
/// WHY THE CLIENT NEEDS THIS AT ALL
///   Everything about equipped gear lives in ItemDisplayInfo.dbc: which body
///   textures paint into the skin atlas, which geoset variants switch on, and
///   which separate M2 models hang off attachment points. A display ID is a row
///   number in that table and nothing else - without it, "equip the Breastplate
///   of Might" has no meaning the client can act on.
///
/// FORMAT
///   "WDBC", then four uint32: record count, field count, record size, string
///   block size. Records follow, then one contiguous string block. Every string
///   in a record is a BYTE OFFSET into that block, not a length-prefixed value.
///   Offset 0 is the empty string by convention.
///
/// NO GL, NO GAME LOGIC - this is Formats/, same rule as every other reader.
/// </summary>
public sealed class DbcFile
{
    public int RecordCount { get; private set; }
    public int FieldCount { get; private set; }
    public int RecordSize { get; private set; }

    private byte[] _records = [];
    private byte[] _strings = [];

    public static DbcFile? Parse(byte[] data)
    {
        if (data.Length < 20) return null;
        if (data[0] != 'W' || data[1] != 'D' || data[2] != 'B' || data[3] != 'C') return null;

        var dbc = new DbcFile
        {
            RecordCount = BitConverter.ToInt32(data, 4),
            FieldCount = BitConverter.ToInt32(data, 8),
            RecordSize = BitConverter.ToInt32(data, 12),
        };

        int stringSize = BitConverter.ToInt32(data, 16);

        if (dbc.RecordCount < 0 || dbc.RecordSize <= 0 || stringSize < 0) return null;

        long recordBytes = (long)dbc.RecordCount * dbc.RecordSize;
        if (20 + recordBytes + stringSize > data.Length) return null;

        dbc._records = new byte[recordBytes];
        Array.Copy(data, 20, dbc._records, 0, recordBytes);

        dbc._strings = new byte[stringSize];
        Array.Copy(data, 20 + recordBytes, dbc._strings, 0, stringSize);

        return dbc;
    }

    public uint GetUInt(int row, int field)
    {
        int offset = row * RecordSize + field * 4;
        if (row < 0 || row >= RecordCount || field < 0 || offset + 4 > _records.Length) return 0;
        return BitConverter.ToUInt32(_records, offset);
    }

    public int GetInt(int row, int field) => unchecked((int)GetUInt(row, field));

    /// <summary>
    /// Read a stringref field. The value stored is an offset into the string
    /// block; the string runs to the next null byte.
    /// </summary>
    public string GetString(int row, int field)
    {
        uint offset = GetUInt(row, field);
        if (offset == 0 || offset >= _strings.Length) return "";

        int end = (int)offset;
        while (end < _strings.Length && _strings[end] != 0) end++;

        return Encoding.UTF8.GetString(_strings, (int)offset, end - (int)offset);
    }
}

/// <summary>
/// One ItemDisplayInfo.dbc row, vanilla 1.12.1.
///
/// THE FIELD LAYOUT IS NOT GUESSED. It comes from SuperUI's DbcService, where
/// it was established by dumping all 23 fields across robes, plate chests,
/// cloth, boots, gloves and a trade good, plus a histogram of small integer
/// values at every field index across all 29,604 records. 23 fields, 92 bytes
/// per record:
///
///   [0]      m_ID
///   [1-2]    m_modelName[0..1]        stringref  - the separate M2 for helms,
///                                                  shoulders, weapons, shields
///   [3-4]    m_modelTexture[0..1]     stringref  - textures for those models
///   [5]      m_inventoryIcon          stringref  (single in vanilla)
///   [6-8]    m_geosetGroup[0..2]      uint32     - which geoset variants switch on
///   [9]      m_spellVisualID
///   [10]     m_groundModel
///   [11]     m_groupSoundIndex
///   [12-13]  m_helmetGeosetVis[0..1]  - drives hair hiding under a helm
///   [14-21]  m_texture[0..7]          stringref  - the body-atlas paints
///   [22]     m_itemVisual             - ItemVisuals.dbc, glow and lightning
///
/// A note from that same source, worth keeping: an earlier parser used a -2
/// shift for the texture base and looked correct on chests, because the
/// compositor's slot mapping started at 2 and the two errors cancelled. It was
/// wrong for the right reason, and it hid LegLower and Foot entirely.
/// </summary>
public sealed class ItemDisplayRow
{
    public uint Id;
    public string ModelName1 = "";
    public string ModelName2 = "";
    public string ModelTexture1 = "";
    public string ModelTexture2 = "";
    public int[] GeosetGroup = new int[3];
    public uint HelmetGeosetVis1;
    public uint HelmetGeosetVis2;

    /// <summary>
    /// m_texture[0..7], in slot order:
    /// 0 ArmUpper, 1 ArmLower, 2 Hand, 3 TorsoUpper, 4 TorsoLower,
    /// 5 LegUpper, 6 LegLower, 7 Foot.
    /// </summary>
    public string[] BodyTextures = new string[8];

    public uint ItemVisualId;

    public bool HasModel => ModelName1.Length > 0 || ModelName2.Length > 0;
    public bool HasBodyTexture => BodyTextures.Any(t => t.Length > 0);

    public override string ToString()
    {
        var parts = new List<string>();
        if (ModelName1.Length > 0) parts.Add($"model '{ModelName1}'");
        if (ModelTexture1.Length > 0) parts.Add($"modelTex '{ModelTexture1}'");

        var textures = BodyTextures
            .Select((t, i) => (t, i))
            .Where(x => x.t.Length > 0)
            .Select(x => $"{x.i}:'{x.t}'");
        if (textures.Any()) parts.Add("tex " + string.Join(" ", textures));

        if (GeosetGroup.Any(g => g != 0)) parts.Add($"geoset [{string.Join(",", GeosetGroup)}]");
        if (HelmetGeosetVis1 != 0 || HelmetGeosetVis2 != 0)
            parts.Add($"helmVis {HelmetGeosetVis1}/{HelmetGeosetVis2}");

        return parts.Count > 0 ? string.Join("  ", parts) : "(empty row)";
    }
}

/// <summary>
/// One CharSections.dbc row. Vanilla 1.12: 10 fields, 40 bytes.
///
/// Layout verified in SuperUI's DbcService against the real file (3,671
/// records, a 198,838-byte string block):
///
///   [0] ID
///   [1] Race          CharRaces id - 1 Human, 2 Orc, 3 Dwarf, 4 NightElf,
///                     5 Scourge, 6 Tauren, 7 Gnome, 8 Troll
///   [2] Sex           0 male, 1 female
///   [3] BaseSection   0 Skin, 1 Face, 2 FacialHair, 3 Hair, 4 Underwear
///   [4] VariationIndex  face shape or hair style
///   [5] ColorIndex      skin tone or hair colour
///   [6-8] TextureName[0..2]   for Face: [0] lower, [1] upper, [2] empty
///   [9] Flags
///
/// THIS TABLE IS WHERE A CHARACTER'S FACE AND HAIR ACTUALLY COME FROM. A body
/// skin BLP for most races has no eye detail at all - the eyes are a Face row
/// composited onto the atlas. Skip that step and the character renders
/// blank-faced, which reads as "eyes closed" and sends you looking at geosets.
/// </summary>
public sealed class CharSectionRow
{
    public uint Race;
    public uint Sex;
    public uint BaseSection;
    public uint VariationIndex;
    public uint ColorIndex;
    public string Texture1 = "";
    public string Texture2 = "";
    public string Texture3 = "";
}

public sealed class CharSectionsTable
{
    public const string MpqPath = @"DBFilesClient\CharSections.dbc";

    public const uint SectionSkin = 0;
    public const uint SectionFace = 1;
    public const uint SectionFacialHair = 2;
    public const uint SectionHair = 3;
    public const uint SectionUnderwear = 4;

    private readonly List<CharSectionRow> _rows = [];
    public int Count => _rows.Count;

    public static CharSectionsTable? Parse(byte[] data)
    {
        var dbc = DbcFile.Parse(data);
        if (dbc is null) return null;

        if (dbc.RecordSize != 40)
            Console.WriteLine($"[dbc] CharSections record size is {dbc.RecordSize}, expected 40 - " +
                              "field offsets are probably wrong");

        var table = new CharSectionsTable();

        for (int r = 0; r < dbc.RecordCount; r++)
        {
            table._rows.Add(new CharSectionRow
            {
                Race = dbc.GetUInt(r, 1),
                Sex = dbc.GetUInt(r, 2),
                BaseSection = dbc.GetUInt(r, 3),
                VariationIndex = dbc.GetUInt(r, 4),
                ColorIndex = dbc.GetUInt(r, 5),
                Texture1 = dbc.GetString(r, 6),
                Texture2 = dbc.GetString(r, 7),
                Texture3 = dbc.GetString(r, 8),
            });
        }

        Console.WriteLine($"[dbc] CharSections: {table.Count} row(s)");
        return table;
    }

    /// <summary>
    /// Find a section row. The MATCH KEYS DIFFER PER SECTION and getting them
    /// wrong returns a plausible row for the wrong character:
    ///
    ///   Skin       colour only        (the skin tone)
    ///   Face       variation AND colour  (face shape, then skin tone)
    ///   Hair       variation AND colour  (hair style, then hair colour)
    ///   Underwear  colour only        (follows the skin tone)
    ///
    /// Pass -1 for a key that should not be matched on.
    /// </summary>
    public CharSectionRow? Find(uint race, uint sex, uint section, int variation, int colour)
    {
        foreach (var row in _rows)
        {
            if (row.Race != race || row.Sex != sex || row.BaseSection != section) continue;
            if (variation >= 0 && row.VariationIndex != (uint)variation) continue;
            if (colour >= 0 && row.ColorIndex != (uint)colour) continue;
            return row;
        }
        return null;
    }

    /// <summary>Vanilla CharRaces ids, matching the folder names used for MPQ paths.</summary>
    public static uint RaceId(string race) => race.ToLowerInvariant() switch
    {
        "human" => 1,
        "orc" => 2,
        "dwarf" => 3,
        "nightelf" => 4,
        "scourge" or "undead" => 5,
        "tauren" => 6,
        "gnome" => 7,
        "troll" => 8,
        _ => 1,
    };
}

/// <summary>
/// CharHairGeosets.dbc maps a character-creation hairstyle number to the
/// actual group-0 submesh in that race/gender M2. The hairstyle number is not
/// itself a geoset number.
///
/// Vanilla columns:
///   [0] ID, [1] RaceID, [2] SexID, [3] Variation, [4] GeosetID,
///   [5] ShowsScalp (when present; use the default scalp geoset instead).
/// </summary>
public sealed class CharHairGeosetsTable
{
    private readonly Dictionary<(uint Race, uint Sex, uint Style), int> _geosets = [];

    public const string MpqPath = @"DBFilesClient\CharHairGeosets.dbc";
    public int Count => _geosets.Count;

    public static CharHairGeosetsTable? Parse(byte[] data)
    {
        var dbc = DbcFile.Parse(data);
        if (dbc is null || dbc.FieldCount < 5) return null;

        var table = new CharHairGeosetsTable();
        for (int row = 0; row < dbc.RecordCount; row++)
        {
            uint race = dbc.GetUInt(row, 1);
            uint sex = dbc.GetUInt(row, 2);
            uint style = dbc.GetUInt(row, 3);
            uint geoset = dbc.GetUInt(row, 4);
            bool showsScalp = dbc.FieldCount > 5 && dbc.GetUInt(row, 5) != 0;

            table._geosets[(race, sex, style)] = showsScalp ? 1 : (int)geoset;
        }

        Console.WriteLine($"[dbc] CharHairGeosets: {table.Count} mapping(s)");
        return table;
    }

    public int Find(uint race, uint sex, int style)
        => style >= 0 && _geosets.TryGetValue((race, sex, (uint)style), out int geoset)
            ? geoset
            : -1;
}

/// <summary>ItemDisplayInfo.dbc, indexed by display ID.</summary>
public sealed class ItemDisplayTable
{
    private readonly Dictionary<uint, ItemDisplayRow> _rows = [];

    public int Count => _rows.Count;

    public ItemDisplayRow? Find(uint displayId)
        => _rows.TryGetValue(displayId, out var row) ? row : null;

    /// <summary>Vanilla path. Patched tables override it through the normal MPQ load order.</summary>
    public const string MpqPath = @"DBFilesClient\ItemDisplayInfo.dbc";

    private const int ExpectedRecordSize = 92;

    public static ItemDisplayTable? Parse(byte[] data)
    {
        var dbc = DbcFile.Parse(data);
        if (dbc is null) return null;

        // Loud rather than silent: a different record size means a different
        // schema, and every field offset below would be reading the wrong
        // bytes while still producing plausible-looking numbers.
        if (dbc.RecordSize != ExpectedRecordSize)
        {
            Console.WriteLine(
                $"[dbc] ItemDisplayInfo record size is {dbc.RecordSize}, expected {ExpectedRecordSize} " +
                "for vanilla 1.12 - field offsets are probably wrong");
        }

        var table = new ItemDisplayTable();

        for (int r = 0; r < dbc.RecordCount; r++)
        {
            var row = new ItemDisplayRow
            {
                Id = dbc.GetUInt(r, 0),
                ModelName1 = dbc.GetString(r, 1),
                ModelName2 = dbc.GetString(r, 2),
                ModelTexture1 = dbc.GetString(r, 3),
                ModelTexture2 = dbc.GetString(r, 4),
                GeosetGroup = [dbc.GetInt(r, 6), dbc.GetInt(r, 7), dbc.GetInt(r, 8)],
                HelmetGeosetVis1 = dbc.GetUInt(r, 12),
                HelmetGeosetVis2 = dbc.GetUInt(r, 13),
                ItemVisualId = dbc.GetUInt(r, 22),
            };

            for (int t = 0; t < 8; t++)
                row.BodyTextures[t] = dbc.GetString(r, 14 + t);

            if (row.Id != 0) table._rows[row.Id] = row;
        }

        Console.WriteLine(
            $"[dbc] ItemDisplayInfo: {dbc.RecordCount} record(s), {dbc.FieldCount} field(s), " +
            $"{dbc.RecordSize} bytes each, {table.Count} indexed");

        return table;
    }
}

/// <summary>
/// GroundEffectDoodad.dbc - maps a ground-effect doodad ID to its grass/flower
/// M2 model path. Vanilla layout has an ID, an internal tag, the model filename
/// (stringref), flags and a couple of floats, and the exact field order shifted
/// across versions - so rather than hard-code an offset we SCAN each field for
/// the one stringref that resolves to a model path (.mdx/.m2/.mdl). Robust and
/// self-verifying; the record size is logged so a wrong parse is visible.
/// </summary>
public sealed class GroundEffectDoodadTable
{
    public const string MpqPath = @"DBFilesClient\GroundEffectDoodad.dbc";

    private readonly Dictionary<uint, string> _models = [];
    public int Count => _models.Count;
    public string? Model(uint id) => _models.TryGetValue(id, out var m) ? m : null;

    public static GroundEffectDoodadTable? Parse(byte[] data)
    {
        var dbc = DbcFile.Parse(data);
        if (dbc is null) return null;

        var table = new GroundEffectDoodadTable();
        for (int r = 0; r < dbc.RecordCount; r++)
        {
            uint id = dbc.GetUInt(r, 0);
            string model = "";
            for (int f = 1; f < dbc.FieldCount; f++)
            {
                string s = dbc.GetString(r, f);
                if (s.Length > 3 &&
                    (s.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase) ||
                     s.EndsWith(".m2", StringComparison.OrdinalIgnoreCase) ||
                     s.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)))
                { model = s; break; }
            }
            if (id != 0 && model.Length > 0) table._models[id] = model;
        }

        Console.WriteLine($"[dbc] GroundEffectDoodad: {dbc.RecordCount} record(s), {dbc.FieldCount} field(s), " +
            $"{dbc.RecordSize} bytes; {table._models.Count} with a model path");
        return table;
    }
}

/// <summary>One GroundEffectTexture row, resolved to model paths + weights.</summary>
public sealed class GroundEffectRecipe
{
    public (string Model, int Weight)[] Doodads = [];
    public int Density = 1;
}

/// <summary>
/// GroundEffectTexture.dbc - a ground-effect ID (from MCLY.EffectId) gives up to
/// four GroundEffectDoodad IDs with weights, plus a density. Vanilla layout is
/// ID, DoodadID[4], DoodadWeight[4], Density, Sound (11 fields / 44 bytes); the
/// record size is logged so a mismatch is obvious. Doodad IDs are resolved to
/// model paths through GroundEffectDoodad at parse time.
/// </summary>
public sealed class GroundEffectTextureTable
{
    public const string MpqPath = @"DBFilesClient\GroundEffectTexture.dbc";

    private readonly Dictionary<uint, GroundEffectRecipe> _byId = [];
    public int Count => _byId.Count;

    public GroundEffectRecipe? Get(int effectId)
        => effectId > 0 && _byId.TryGetValue((uint)effectId, out var r) ? r : null;

    public static GroundEffectTextureTable? Parse(byte[] data, GroundEffectDoodadTable doodads)
    {
        var dbc = DbcFile.Parse(data);
        if (dbc is null) return null;

        var table = new GroundEffectTextureTable();
        for (int r = 0; r < dbc.RecordCount; r++)
        {
            uint id = dbc.GetUInt(r, 0);
            if (id == 0) continue;

            var list = new List<(string, int)>(4);
            for (int i = 0; i < 4; i++)
            {
                uint doodadId = dbc.GetUInt(r, 1 + i);
                if (doodadId == 0) continue;
                string? model = doodads.Model(doodadId);
                if (model is null) continue;
                int weight = dbc.FieldCount > 5 + i ? dbc.GetInt(r, 5 + i) : 1;
                list.Add((model, Math.Max(weight, 1)));
            }
            if (list.Count == 0) continue;

            int density = dbc.FieldCount > 9 ? dbc.GetInt(r, 9) : 16;
            table._byId[id] = new GroundEffectRecipe
            {
                Doodads = list.ToArray(),
                Density = Math.Max(density, 1),
            };
        }

        Console.WriteLine($"[dbc] GroundEffectTexture: {dbc.RecordCount} record(s), {dbc.FieldCount} field(s), " +
            $"{dbc.RecordSize} bytes; {table._byId.Count} effect(s) with resolvable doodads");
        return table;
    }
}