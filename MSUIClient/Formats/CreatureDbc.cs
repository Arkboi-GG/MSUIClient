namespace MSUIClient.Formats;

// CreatureDisplayInfo.dbc + CreatureModelData.dbc (+ CreatureDisplayInfoExtra.dbc) —
// the displayId -> model-path + texture + geoset chain that turns a unit's
// UNIT_FIELD_DISPLAYID into a renderable, TEXTURED, correctly-GEOSETED M2. Standalone
// tables over the existing DbcFile primitive, field indices verified against
// benilla-formats/creatures.rs.
//
// Two kinds of creature texture/geoset DIFFERENTLY:
//   * BEASTS (ExtendedDisplayId == 0): monster-skin slots (texture type 11/12/13);
//     every submesh drawn (no character geosets).
//   * HUMANOID NPCs (ExtendedDisplayId != 0): a CHARACTER .m2 — body slot type 1 filled
//     from CreatureDisplayInfoExtra, and its geosets FILTERED like a player's from the
//     extra row's hair/facial/equipment (else all hairstyles/beards stack).

/// <summary>A CreatureDisplayInfo row.</summary>
public sealed class CreatureDisplayRow
{
    public uint Id;
    public uint ModelId;           // field 1 -> CreatureModelData.Id
    public uint ExtendedDisplayId; // field 3 -> CreatureDisplayInfoExtra.Id (0 = plain beast)
    public float Scale = 1f;       // field 4
    public string[] Textures = new string[3];  // fields 6/7/8 (bare stems)
}

/// <summary>CreatureDisplayInfo.dbc, indexed by display id (UNIT_FIELD_DISPLAYID).</summary>
public sealed class CreatureDisplayInfoTable
{
    public const string MpqPath = @"DBFilesClient\CreatureDisplayInfo.dbc";

    private readonly Dictionary<uint, CreatureDisplayRow> _rows = new();
    public int Count => _rows.Count;
    public IReadOnlyCollection<CreatureDisplayRow> All => _rows.Values;
    public CreatureDisplayRow? Find(uint displayId) => _rows.TryGetValue(displayId, out var r) ? r : null;

    public static CreatureDisplayInfoTable? Parse(byte[] data)
    {
        var dbc = DbcFile.Parse(data);
        if (dbc is null) return null;

        var table = new CreatureDisplayInfoTable();
        for (int r = 0; r < dbc.RecordCount; r++)
        {
            uint id = dbc.GetUInt(r, 0);
            if (id == 0) continue;
            var row = new CreatureDisplayRow
            {
                Id = id,
                ModelId = dbc.GetUInt(r, 1),
                ExtendedDisplayId = dbc.FieldCount > 3 ? dbc.GetUInt(r, 3) : 0,
                Scale = SaneScale(dbc.GetFloat(r, 4)),
            };
            for (int t = 0; t < 3; t++) row.Textures[t] = dbc.GetString(r, 6 + t);
            table._rows[id] = row;
        }
        Console.WriteLine($"[dbc] CreatureDisplayInfo: {dbc.RecordCount} record(s), {dbc.RecordSize}B each, {table.Count} indexed");
        return table;
    }

    private static float SaneScale(float s) => s > 0 && !float.IsNaN(s) ? s : 1f;
}

/// <summary>A CreatureModelData row: the model path (.m2) + a model-level scale.</summary>
public sealed class CreatureModelRow
{
    public uint Id;
    public string ModelPath = "";
    public float Scale = 1f;
}

/// <summary>CreatureModelData.dbc, indexed by model id.</summary>
public sealed class CreatureModelDataTable
{
    public const string MpqPath = @"DBFilesClient\CreatureModelData.dbc";

    private readonly Dictionary<uint, CreatureModelRow> _rows = new();
    public int Count => _rows.Count;
    public CreatureModelRow? Find(uint modelId) => _rows.TryGetValue(modelId, out var r) ? r : null;

    public static CreatureModelDataTable? Parse(byte[] data)
    {
        var dbc = DbcFile.Parse(data);
        if (dbc is null) return null;

        var table = new CreatureModelDataTable();
        for (int r = 0; r < dbc.RecordCount; r++)
        {
            uint id = dbc.GetUInt(r, 0);
            if (id == 0) continue;
            string path = dbc.GetString(r, 2);
            if (!LooksLikeModel(path)) path = ScanForModel(dbc, r);
            table._rows[id] = new CreatureModelRow
            {
                Id = id,
                ModelPath = path,
                Scale = dbc.GetFloat(r, 4) is var s && s > 0 && !float.IsNaN(s) ? s : 1f,
            };
        }
        Console.WriteLine($"[dbc] CreatureModelData: {dbc.RecordCount} record(s), {dbc.RecordSize}B each, {table.Count} indexed");
        return table;
    }

    private static bool LooksLikeModel(string s) =>
        !string.IsNullOrEmpty(s) &&
        (s.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase) ||
         s.EndsWith(".m2", StringComparison.OrdinalIgnoreCase) ||
         s.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase));

    private static string ScanForModel(DbcFile dbc, int row)
    {
        for (int f = 0; f < dbc.FieldCount; f++)
        {
            string s = dbc.GetString(row, f);
            if (LooksLikeModel(s)) return s;
        }
        return "";
    }
}

/// <summary>
/// A CreatureDisplayInfoExtra row: the humanoid NPC's race/gender/skin + hair + facial-hair
/// + 10 equipment display ids (head, shoulder, shirt, chest, belt, pants, boots, wrist, gloves,
/// tabard) + the baked atlas name if any. Fields verified vs benilla creatures.rs:245-264.
/// </summary>
public sealed class CreatureDisplayExtraRow
{
    public uint Id;
    public byte Race;       // field 1
    public byte Sex;        // field 2
    public uint Skin;       // field 3
    public uint Face;       // field 4
    public byte HairStyle;  // field 5
    public uint HairColor;  // field 6
    public byte FacialHair; // field 7
    public uint[] Equipment = new uint[10];   // fields 8..17 (head..tabard)
    public string BakeName = "";
}

/// <summary>CreatureDisplayInfoExtra.dbc — the character-model NPC descriptor (guards, townsfolk, ...).</summary>
public sealed class CreatureDisplayExtraTable
{
    public const string MpqPath = @"DBFilesClient\CreatureDisplayInfoExtra.dbc";

    private readonly Dictionary<uint, CreatureDisplayExtraRow> _rows = new();
    public int Count => _rows.Count;
    public IReadOnlyCollection<CreatureDisplayExtraRow> All => _rows.Values;
    public CreatureDisplayExtraRow? Find(uint id) => id != 0 && _rows.TryGetValue(id, out var r) ? r : null;

    public static CreatureDisplayExtraTable? Parse(byte[] data)
    {
        var dbc = DbcFile.Parse(data);
        if (dbc is null) return null;

        int fc = dbc.FieldCount;
        var table = new CreatureDisplayExtraTable();
        for (int r = 0; r < dbc.RecordCount; r++)
        {
            uint id = dbc.GetUInt(r, 0);
            if (id == 0) continue;
            var row = new CreatureDisplayExtraRow
            {
                Id = id,
                Race = fc > 1 ? (byte)dbc.GetUInt(r, 1) : (byte)0,
                Sex = fc > 2 ? (byte)dbc.GetUInt(r, 2) : (byte)0,
                Skin = fc > 3 ? dbc.GetUInt(r, 3) : 0,
                Face = fc > 4 ? dbc.GetUInt(r, 4) : 0,
                HairStyle = fc > 5 ? (byte)dbc.GetUInt(r, 5) : (byte)0,
                HairColor = fc > 6 ? dbc.GetUInt(r, 6) : 0,
                FacialHair = fc > 7 ? (byte)dbc.GetUInt(r, 7) : (byte)0,
                BakeName = ScanForBake(dbc, r),
            };
            for (int i = 0; i < 10 && 8 + i < fc; i++) row.Equipment[i] = dbc.GetUInt(r, 8 + i);
            table._rows[id] = row;
        }
        Console.WriteLine($"[dbc] CreatureDisplayInfoExtra: {dbc.RecordCount} record(s), {dbc.RecordSize}B each, {table.Count} indexed");
        return table;
    }

    private static string ScanForBake(DbcFile dbc, int row)
    {
        for (int f = dbc.FieldCount - 1; f >= 8; f--)   // bake name is a string past the item columns
        {
            string s = dbc.GetString(row, f);
            if (!string.IsNullOrEmpty(s) && s.EndsWith(".blp", StringComparison.OrdinalIgnoreCase)) return s;
        }
        return "";
    }
}

/// <summary>Everything the creature renderer needs to draw + texture + geoset one display id.</summary>
public readonly record struct CreatureModelInfo(
    string ModelPath,
    float Scale,
    string[] Textures,       // beast monster-skin variations
    bool HasExtended,        // true = character-model NPC
    uint ExtId,
    byte ExtRace,
    byte ExtSex,
    uint ExtSkin,
    uint ExtFace,
    byte ExtHairStyle,
    uint ExtHairColor,
    byte ExtFacialHair,
    uint[] ExtEquipment,     // 10 display ids: head, shoulder, shirt, chest, belt, pants, boots, wrist, gloves, tabard
    string BakeName);

/// <summary>Resolves a UNIT_FIELD_DISPLAYID to a model path + scale + texture + geoset sources.</summary>
public sealed class CreatureModelResolver
{
    private readonly CreatureDisplayInfoTable _display;
    private readonly CreatureModelDataTable _model;
    private readonly CreatureDisplayExtraTable? _extra;

    public CreatureModelResolver(CreatureDisplayInfoTable display, CreatureModelDataTable model,
                                 CreatureDisplayExtraTable? extra = null)
    {
        _display = display;
        _model = model;
        _extra = extra;
    }

    public bool TryResolve(int displayId, out CreatureModelInfo info)
    {
        info = default;
        if (displayId <= 0) return false;

        var d = _display.Find((uint)displayId);
        if (d is null) return false;
        var m = _model.Find(d.ModelId);
        if (m is null || string.IsNullOrEmpty(m.ModelPath)) return false;

        var ex = d.ExtendedDisplayId != 0 ? _extra?.Find(d.ExtendedDisplayId) : null;

        info = new CreatureModelInfo(
            ModelPath: NormalizeModelPath(m.ModelPath),
            Scale: d.Scale * m.Scale,
            Textures: d.Textures,
            HasExtended: ex is not null,
            ExtId: ex?.Id ?? 0,
            ExtRace: ex?.Race ?? 0,
            ExtSex: ex?.Sex ?? 0,
            ExtSkin: ex?.Skin ?? 0,
            ExtFace: ex?.Face ?? 0,
            ExtHairStyle: ex?.HairStyle ?? 0,
            ExtHairColor: ex?.HairColor ?? 0,
            ExtFacialHair: ex?.FacialHair ?? 0,
            ExtEquipment: ex?.Equipment ?? System.Array.Empty<uint>(),
            BakeName: ex?.BakeName ?? "");
        return true;
    }

    /// <summary>DBC stores .mdx/.mdl; the MPQ files are .m2.</summary>
    public static string NormalizeModelPath(string p)
    {
        if (p.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase)) return string.Concat(p.AsSpan(0, p.Length - 4), ".m2");
        if (p.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)) return string.Concat(p.AsSpan(0, p.Length - 4), ".m2");
        return p;
    }
}
