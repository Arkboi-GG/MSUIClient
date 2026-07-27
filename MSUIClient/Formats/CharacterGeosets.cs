namespace MSUIClient.Formats;

// Character geoset VISIBILITY — which of a character model's many geosets to draw for
// a given race/sex/hair/facial-hair + equipment. Ported byte-faithfully from benilla
// (benilla-formats/characters/geosets.rs visible_geosets). The SAME function drives
// the player and every humanoid NPC (Stormwind guards, townsfolk, ...). Without it a
// character model draws EVERY variant at once — all hairstyles, all beards, all
// sleeve/glove/boot variants stacked — which is the "messy head / clipping" symptom.
//
// A submesh's skinSectionId (M2Submesh.Id) = group*100 + variant. A submesh is drawn
// iff its id is in the returned set. Group 0 = hair/scalp, 1/2/3 = facial hair,
// 4 = gloves, 5 = boots, 7 = ears, 8 = sleeves, 9 = knees, 10 = doublet, 11 = legs,
// 12 = tabard, 13 = robe skirt, 15 = cape; 6 and 14 are always-on bases.

/// <summary>What a character is wearing, as the ItemDisplayInfo rows that carry geoset groups.</summary>
public sealed class EquipGeosets
{
    // Body slots in benilla order: [0 shirt, 1 chest, 2 belt, 3 pants, 4 boots, 5 wrist, 6 gloves, 7 tabard].
    public ItemDisplayRow?[] Bodyslots = new ItemDisplayRow?[8];
    public bool HasCloak;
    public int CloakGroup;                       // cloak item's GeosetGroup[0]
    public (uint Male, uint Female)? HelmVis;    // head item's HelmetGeosetVis row ids [male, female]
}

/// <summary>CharacterFacialHairStyles.dbc — facialHair -> three facial geoset groups. No ID column.</summary>
public sealed class CharacterFacialHairTable
{
    public const string MpqPath = @"DBFilesClient\CharacterFacialHairStyles.dbc";
    // key = (race<<16)|(sex<<8)|variation -> (a,b,c) from fields 6/7/8 (a->group1, b->group3, c->group2)
    private readonly Dictionary<int, (int A, int B, int C)> _rows = new();
    public int Count => _rows.Count;

    public static CharacterFacialHairTable? Parse(byte[] data)
    {
        var dbc = DbcFile.Parse(data);
        if (dbc is null || dbc.FieldCount < 9) return null;
        var t = new CharacterFacialHairTable();
        for (int r = 0; r < dbc.RecordCount; r++)
        {
            int key = Key(dbc.GetUInt(r, 0), dbc.GetUInt(r, 1), dbc.GetInt(r, 2));
            if (!t._rows.ContainsKey(key))   // first row wins (matches the client's linear scan)
                t._rows[key] = (dbc.GetInt(r, 6), dbc.GetInt(r, 7), dbc.GetInt(r, 8));
        }
        Console.WriteLine($"[dbc] CharacterFacialHairStyles: {t.Count} style(s)");
        return t;
    }

    public (int A, int B, int C)? Find(uint race, uint sex, int variation) =>
        _rows.TryGetValue(Key(race, sex, variation), out var v) ? v : null;

    private static int Key(uint race, uint sex, int variation) => ((int)race << 16) | ((int)sex << 8) | (variation & 0xFF);
}

/// <summary>HelmetGeosetVisData.dbc — row id -> 5 race-bitmask columns (hide hair/facial1/2/3/ears).</summary>
public sealed class HelmetGeosetVisTable
{
    public const string MpqPath = @"DBFilesClient\HelmetGeosetVisData.dbc";
    private readonly Dictionary<uint, uint[]> _rows = new();
    public int Count => _rows.Count;

    public static HelmetGeosetVisTable? Parse(byte[] data)
    {
        var dbc = DbcFile.Parse(data);
        if (dbc is null || dbc.FieldCount < 6) return null;
        var t = new HelmetGeosetVisTable();
        for (int r = 0; r < dbc.RecordCount; r++)
        {
            uint id = dbc.GetUInt(r, 0);
            if (id == 0) continue;
            t._rows[id] = new[] { dbc.GetUInt(r, 1), dbc.GetUInt(r, 2), dbc.GetUInt(r, 3), dbc.GetUInt(r, 4), dbc.GetUInt(r, 5) };
        }
        Console.WriteLine($"[dbc] HelmetGeosetVisData: {t.Count} row(s)");
        return t;
    }

    public uint[]? Find(uint rowId) => _rows.TryGetValue(rowId, out var m) ? m : null;
}

/// <summary>The visible-geoset engine. Load once; call Visible() per character/NPC.</summary>
public sealed class CharacterGeosets
{
    // group*100 + 1 for each group EXCEPT group 7 (ears) which defaults to variant 2 -> 702.
    private static readonly int[] RegionBases =
        { 1, 101, 201, 301, 401, 501, 601, 702, 801, 901, 1001, 1101, 1201, 1301, 1401, 1501 };

    private readonly CharHairGeosetsTable? _hair;
    private readonly CharacterFacialHairTable? _facial;
    private readonly HelmetGeosetVisTable? _helmetVis;

    public CharacterGeosets(CharHairGeosetsTable? hair, CharacterFacialHairTable? facial, HelmetGeosetVisTable? helmetVis)
    {
        _hair = hair;
        _facial = facial;
        _helmetVis = helmetVis;
    }

    public bool Ok => _hair is not null || _facial is not null;

    /// <summary>The full set of geoset ids (skinSectionId) to draw. equip may be null (naked).</summary>
    public HashSet<int> Visible(uint race, uint sex, int hairStyle, int facialHair, EquipGeosets? equip)
    {
        var set = new List<int>(RegionBases) { 0 };   // indices 0..15 = region slots; body (0) always on

        // Hair (group 0): CharHairGeosets geoset id, clamped >= 1 (a "bald" style is the bare scalp, not the body).
        int hairGeo = _hair?.Find(race, sex, hairStyle) ?? -1;
        if (hairGeo >= 0) set[0] = Math.Max(1, hairGeo);

        // Facial hair (groups 1/3/2 — note the DBC column order maps to [1,3,2]).
        if (_facial?.Find(race, sex, facialHair) is { } f)
        {
            set[1] = f.A + 100;
            set[3] = f.B + 300;
            set[2] = f.C + 200;
        }

        // Helm hides hair/facial/ears (before equipment). A set race-bit forces the slot back to its base.
        if (equip?.HelmVis is { } hv && _helmetVis is not null)
        {
            uint rowId = sex == 1 ? hv.Female : hv.Male;
            if (_helmetVis.Find(rowId) is { } masks)
            {
                (int Slot, int Val)[] forced = { (0, 1), (1, 101), (2, 201), (3, 301), (7, 701) };
                uint bit = 1u << (int)(race & 0x1f);
                for (int i = 0; i < forced.Length && i < masks.Length; i++)
                    if ((masks[i] & bit) != 0) set[forced[i].Slot] = forced[i].Val;
            }
        }

        // Equipment branches (benilla geosets.rs:102-148). g(slot,sub) = nonzero geoset group or null.
        if (equip is not null)
        {
            var bs = equip.Bodyslots;
            int? G(int slot, int sub)
            {
                if (slot < 0 || slot >= bs.Length || bs[slot] is null) return null;
                int v = bs[slot]!.GeosetGroup[sub];
                return v != 0 ? v : (int?)null;
            }

            int? robe = G(1, 2) ?? G(3, 2);   // chest gg[2], else pants gg[2]

            // B1: gloves (group 4) replace the naked gloves; else chest sleeves (group 8) add.
            if (G(6, 0) is { } gl) { Disable(set, 401, 499); set.Add(401 + gl); }
            else if (G(1, 0) is { } sl) set.Add(801 + sl);

            // B3: shirt sleeves only when no chest item.
            if (bs[1] is null && G(0, 0) is { } ssl) set.Add(801 + ssl);

            // B4: robe hides boots/knees/legs/trousers and shows its skirt; else boots; else kneepads.
            if (robe is { } rb)
            {
                Disable(set, 501, 599); Disable(set, 902, 999); Disable(set, 1100, 1199); Disable(set, 1300, 1399);
                set.Add(1301 + rb);
            }
            else if (G(4, 0) is { } bt) set.Add(501 + bt);   // boots; naked 501 deliberately stays
            else if (G(3, 1) is { } kp) set.Add(901 + kp);

            // B5: tabard (group 12), hidden by a robe.
            if (robe is null && G(7, 0) is { } tb) set.Add(1201 + tb);

            // B7: doublet (group 10) + pant legs (group 11, base 1102 — bare-legs 1101 stays).
            if (G(0, 1) is { } db) set.Add(1001 + db);
            if (robe is null && G(3, 0) is { } pl) set.Add(1102 + pl);

            // B8: cloak (group 15) replaces the naked cape base.
            if (equip.HasCloak) { Disable(set, 1500, 1599); set.Add(1501 + equip.CloakGroup); }
        }

        return new HashSet<int>(set);
    }

    private static void Disable(List<int> set, int lo, int hi) => set.RemoveAll(x => x >= lo && x <= hi);
}
