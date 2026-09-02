using System.Text;

namespace MSUIClient.Formats;

/// <summary>
/// Reads WoW 1.12.1 (vanilla) WMO (World Map Object) files.
///
/// WMO structure:
///   Root file (e.g. "building.wmo") — textures, materials, group count
///   Group files (e.g. "building_000.wmo", "building_001.wmo") — actual geometry
///
/// Root chunks we parse:
///   MOHD — header (64 bytes): nTextures, nGroups, etc.
///   MOTX — texture filename blob (BLP paths)
///   MOMT — materials (64 bytes each): texture offsets into MOTX, flags
///   MOGN/MOGI — group names and per-group metadata used for LOD classification
///   MODS — doodad sets (32 bytes each): set name + first instance + count
///   MODN — null-separated M2 filenames for embedded doodads
///   MODD — doodad placements (40 bytes each): name offset, flags, pos,
///          quaternion (XYZW), scale, BGRA tint
///
/// Group file chunks we parse:
///   MOGP — group header (68 bytes) then subchunks
///   MOPY — per-triangle material info (2 bytes: flags + materialID)
///   MOVI — triangle indices (uint16)
///   MOVT — vertices (float x3), stored RAW, exactly as they sit in the file
///   MONR — normals (float x3), likewise raw
///   MOTV — UV coords (float x2)
///   MOCV — baked per-vertex lighting (BGRA bytes), the whole basis of
///          interior lighting in 1.12: see FixVertexColors below
///   MOBA — render batches (24 bytes each)
///
/// NO COORDINATE CONVERSION HAPPENS HERE. Vertices and normals come out in WMO
/// local space untouched; whatever the consumer needs — Y-up for a three.js
/// viewer, WoW space for a native client — is its business. (An earlier version
/// of this comment claimed a Z-up to Y-up transform was applied at parse time.
/// It never was; see the MOVT branch. Believing it would put every building in
/// the wrong orientation.)
///
/// FourCC note: WoW stores chunk IDs reversed on disk (little-endian).
///   "MOHD" is stored as bytes D,H,O,M. We use the same ChunkId() reverse
///   helper as AdtTerrainReader to handle this correctly.
///
/// Reference: https://wowdev.wiki/WMO
/// </summary>
public class WmoReader
{
    // ── FourCC helpers — WoW stores chunk IDs reversed on disk (little-endian) ──
    // Same approach as AdtTerrainReader.ChunkId()
    private static uint ChunkId(string fourcc)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(fourcc);
        Array.Reverse(bytes);
        return BitConverter.ToUInt32(bytes, 0);
    }

    // Root file chunks
    private static readonly uint MAGIC_MVER = ChunkId("MVER");
    private static readonly uint MAGIC_MOHD = ChunkId("MOHD");
    private static readonly uint MAGIC_MOTX = ChunkId("MOTX");
    private static readonly uint MAGIC_MOMT = ChunkId("MOMT");
    private static readonly uint MAGIC_MOGN = ChunkId("MOGN");
    private static readonly uint MAGIC_MOGI = ChunkId("MOGI");

    // Root file chunks — doodads (M2 props embedded inside the WMO)
    // MODS = doodad sets (named groups, e.g. "Set_$DefaultGlobal" + variants)
    // MODN = null-separated M2 filename blob
    // MODD = doodad instances (40 bytes each: name offset + flags, pos, quaternion, scale, color)
    // Note: MODI (file data ID table) is a Legion+ chunk; vanilla 1.12.1 uses MODN byte offsets.
    private static readonly uint MAGIC_MODS = ChunkId("MODS");
    private static readonly uint MAGIC_MODN = ChunkId("MODN");
    private static readonly uint MAGIC_MODD = ChunkId("MODD");

    // Root file chunks — portals (the data real WMO visibility traversal needs).
    // MOPV = portal vertices (C3Vector each, quads referenced by MOPT ranges)
    // MOPT = portal descriptors (20 bytes: startVertex, count, C4Plane)
    // MOPR = portal-to-group references (8 bytes: portalIndex, groupIndex, side)
    // These are PARSED but not yet consumed: traversal is a separate step. Until
    // it exists the renderer keeps its existing per-group frustum/distance path,
    // so adding these cannot change what draws.
    private static readonly uint MAGIC_MOPV = ChunkId("MOPV");
    private static readonly uint MAGIC_MOPT = ChunkId("MOPT");
    private static readonly uint MAGIC_MOPR = ChunkId("MOPR");

    // Group file chunks
    private static readonly uint MAGIC_MOGP = ChunkId("MOGP");
    private static readonly uint MAGIC_MOPY = ChunkId("MOPY");
    private static readonly uint MAGIC_MOVI = ChunkId("MOVI");
    private static readonly uint MAGIC_MOVT = ChunkId("MOVT");
    private static readonly uint MAGIC_MONR = ChunkId("MONR");
    private static readonly uint MAGIC_MOTV = ChunkId("MOTV");
    private static readonly uint MAGIC_MOBA = ChunkId("MOBA");
    private static readonly uint MAGIC_MOCV = ChunkId("MOCV");
    // MODR — which root MODD doodads belong to THIS group. One uint16 per
    // doodad, indexing the root's flat MODD array. Present when MOGP flag
    // 0x800 (SMOGroup::HAS_DOODADS) is set.
    private static readonly uint MAGIC_MODR = ChunkId("MODR");
    // MLIQ = liquid inside the WMO group (Stormwind canals, Undercity slime, etc.).
    // Present only when MOGP.GroupFlags bit 0x1000 (SMOGroup::LIQUIDSURFACE) is set.
    private static readonly uint MAGIC_MLIQ = ChunkId("MLIQ");

    // MLIQ sub-structure sizes per wowdev WMO#MLIQ_chunk + Noggit wmo_liquid.cpp:
    //   WMOLiquidHeader = 30 bytes (0x1E):
    //     C2iVector liquidVerts  (uint32 x, y)      — xverts, yverts
    //     C2iVector liquidTiles  (uint32 x, y)      — xtiles, ytiles (=verts - 1)
    //     C3Vector  liquidCorner (float x, y, z)    — base coord in WMO local space
    //     uint16    liquidMtlId                     — material id (MOMT index)
    //   SMOLVert  = 8 bytes (union 4-byte meta + float height)   × xverts*yverts
    //   SMOLTile  = 1 byte  (bits: 0-5 legacy/type, bit 0x08 dont_render, etc.) × xtiles*ytiles
    private const int WMO_LIQUID_HEADER_SIZE = 30;
    private const int WMO_LIQUID_VERT_SIZE = 8;
    private const int WMO_LIQUID_TILE_SIZE = 1;
    // Liquid tile size in WMO local units. Per wowdev: same as ADT cell (1/8 of an MCNK chunk).
    // ADT CELL_SIZE = 33.3333 / 8 = 4.16667. WMO uses identical tile size for MLIQ.
    //
    // PROVEN 2026-07-26, do not tune (PLAN_15_WMO_LIQUID.md §4.2): all 470 corner
    // coordinates (CornerX and CornerY) of the 235 MLIQ groups in wmo.MPQ are exact
    // integer multiples of this value, to within 0.01 yards. 470 of 470. The nearest
    // competing candidate, 4.2, hits 1.1%. Blizzard authored MLIQ corners on this grid.
    private const float WMO_LIQUID_UNIT = 33.3333f / 8.0f;

    /// <summary>
    /// Parse a WMO root file from raw bytes.
    /// Returns header info + material/texture data needed to resolve group textures.
    /// </summary>
    public static WmoRootData? ParseRoot(byte[] data)
    {
        if (data == null || data.Length < 20) return null;

        var root = new WmoRootData();
        int pos = 0;

        while (pos + 8 <= data.Length)
        {
            uint chunkId = BitConverter.ToUInt32(data, pos);
            uint chunkSize = BitConverter.ToUInt32(data, pos + 4);
            int chunkData = pos + 8;
            int chunkEnd = (int)(chunkData + chunkSize);

            if (chunkEnd > data.Length) break;

            if (chunkId == MAGIC_MOHD)
            {
                if (chunkSize >= 64)
                {
                    root.NTextures = BitConverter.ToUInt32(data, chunkData + 0);
                    root.NGroups = BitConverter.ToUInt32(data, chunkData + 4);
                    root.NPortals = BitConverter.ToUInt32(data, chunkData + 8);
                    root.NLights = BitConverter.ToUInt32(data, chunkData + 12);
                    // +0x20: WMOAreaTable.WMOID. This is an authored identity,
                    // not the root file name; one model can have many placed
                    // name sets and every group carries its own third join key.
                    root.WmoId = BitConverter.ToUInt32(data, chunkData + 0x20);
                    // +0x1C ambColor, CArgb stored BGRA on disk. On vanilla's
                    // CLASSIC render path this value is already baked into the
                    // interior MOCV bytes, so the renderer must NOT add it back
                    // at runtime — it survives here only as the ambient fallback
                    // for doodads placed inside the WMO.
                    root.AmbientB = data[chunkData + 0x1C];
                    root.AmbientG = data[chunkData + 0x1D];
                    root.AmbientR = data[chunkData + 0x1E];
                    root.AmbientA = data[chunkData + 0x1F];
                    // +0x3C flags. A full uint32 in vanilla; Legion later split
                    // the top half off as numLod.
                    root.Flags = BitConverter.ToUInt32(data, chunkData + 0x3C);
                    // Bounding box at +0x24 (6 floats)
                    if (chunkSize >= 0x3C)
                    {
                        root.BbMinX = BitConverter.ToSingle(data, chunkData + 0x24);
                        root.BbMinY = BitConverter.ToSingle(data, chunkData + 0x28);
                        root.BbMinZ = BitConverter.ToSingle(data, chunkData + 0x2C);
                        root.BbMaxX = BitConverter.ToSingle(data, chunkData + 0x30);
                        root.BbMaxY = BitConverter.ToSingle(data, chunkData + 0x34);
                        root.BbMaxZ = BitConverter.ToSingle(data, chunkData + 0x38);
                    }
                }
            }
            else if (chunkId == MAGIC_MOTX)
            {
                // Blob of zero-terminated texture filenames
                root.TextureBlob = new byte[chunkSize];
                Array.Copy(data, chunkData, root.TextureBlob, 0, (int)chunkSize);
            }
            else if (chunkId == MAGIC_MOMT)
            {
                // Materials, 64 bytes each
                int matCount = (int)(chunkSize / 64);
                for (int i = 0; i < matCount; i++)
                {
                    int mOfs = chunkData + i * 64;
                    root.Materials.Add(new WmoMaterial
                    {
                        Flags = BitConverter.ToUInt32(data, mOfs + 0),
                        Shader = BitConverter.ToUInt32(data, mOfs + 4),
                        BlendMode = BitConverter.ToUInt32(data, mOfs + 8),
                        Texture0Offset = BitConverter.ToUInt32(data, mOfs + 0x0C),
                        Texture1Offset = BitConverter.ToUInt32(data, mOfs + 0x18),
                        GroundType = BitConverter.ToUInt32(data, mOfs + 0x20),
                        Texture2Offset = BitConverter.ToUInt32(data, mOfs + 0x24),
                        SidnColor = new System.Numerics.Vector3(
                            data[mOfs + 0x12] / 255f, data[mOfs + 0x11] / 255f, data[mOfs + 0x10] / 255f),
                    });
                }
            }
            else if (chunkId == MAGIC_MOGN)
            {
                root.GroupNameBlob = new byte[chunkSize];
                Array.Copy(data, chunkData, root.GroupNameBlob, 0, (int)chunkSize);
            }
            else if (chunkId == MAGIC_MOGI)
            {
                int groupCount = (int)(chunkSize / 32);
                for (int i = 0; i < groupCount; i++)
                {
                    int offset = chunkData + i * 32;
                    root.GroupInfos.Add(new WmoGroupInfo
                    {
                        Flags = BitConverter.ToUInt32(data, offset),
                        NameOffset = BitConverter.ToInt32(data, offset + 28),
                    });
                }
            }
            else if (chunkId == MAGIC_MODS)
            {
                // Doodad sets, 32 bytes each:
                //   char[20] name (null-padded; e.g. "Set_$DefaultGlobal", "Set_Stormwind_City")
                //   uint32   firstInstanceIndex (index into MODD array)
                //   uint32   doodadCount
                //   uint32   padding (unused)
                int setCount = (int)(chunkSize / 32);
                for (int i = 0; i < setCount; i++)
                {
                    int sOfs = chunkData + i * 32;
                    // Read name as null-terminated within the 20-byte slot
                    int nameLen = 0;
                    while (nameLen < 20 && data[sOfs + nameLen] != 0) nameLen++;
                    string setName = Encoding.ASCII.GetString(data, sOfs, nameLen);
                    root.DoodadSets.Add(new WmoDoodadSet
                    {
                        Name = setName,
                        FirstInstanceIndex = BitConverter.ToUInt32(data, sOfs + 20),
                        DoodadCount = BitConverter.ToUInt32(data, sOfs + 24)
                    });
                }
            }
            else if (chunkId == MAGIC_MODN)
            {
                // Blob of zero-terminated M2 filenames. MODD nameOffset values
                // are byte offsets into this blob.
                root.DoodadNameBlob = new byte[chunkSize];
                Array.Copy(data, chunkData, root.DoodadNameBlob, 0, (int)chunkSize);
            }
            else if (chunkId == MAGIC_MODD)
            {
                // Doodad instances, 40 bytes each. WoW vanilla layout:
                //   bytes 0..3   : packed = (nameOffset:24) | (flags:8)
                //   bytes 4..15  : position    (3 × float32, Z-up)
                //   bytes 16..31 : quaternion  (4 × float32, X,Y,Z,W in Z-up space)
                //   bytes 32..35 : scale       (float32)
                //   bytes 36..39 : color       (BGRA, 4 × uint8)
                //
                // We store raw values here. Z-up→Y-up conversion happens at
                // composition time (server-side), same as for ADT MDDF.
                int doodadCount = (int)(chunkSize / 40);
                for (int i = 0; i < doodadCount; i++)
                {
                    int dOfs = chunkData + i * 40;
                    uint packed = BitConverter.ToUInt32(data, dOfs + 0);
                    uint nameOffset = packed & 0x00FFFFFFu;          // low 24 bits
                    byte flags = (byte)((packed >> 24) & 0xFFu);     // high 8 bits

                    root.Doodads.Add(new WmoDoodadDef
                    {
                        NameOffset = nameOffset,
                        Flags = flags,
                        PosX = BitConverter.ToSingle(data, dOfs + 4),
                        PosY = BitConverter.ToSingle(data, dOfs + 8),
                        PosZ = BitConverter.ToSingle(data, dOfs + 12),
                        QuatX = BitConverter.ToSingle(data, dOfs + 16),
                        QuatY = BitConverter.ToSingle(data, dOfs + 20),
                        QuatZ = BitConverter.ToSingle(data, dOfs + 24),
                        QuatW = BitConverter.ToSingle(data, dOfs + 28),
                        Scale = BitConverter.ToSingle(data, dOfs + 32),
                        ColorB = data[dOfs + 36],
                        ColorG = data[dOfs + 37],
                        ColorR = data[dOfs + 38],
                        ColorA = data[dOfs + 39],
                    });
                }
            }
            else if (chunkId == MAGIC_MOPV)
            {
                // Portal vertices — C3Vector (3 float) each, WMO local Z-up,
                // stored raw exactly like MOVT. A portal is a convex polygon
                // (usually a quad) formed by VertexCount of these starting at
                // MOPT.StartVertex.
                int count = (int)(chunkSize / 12);
                for (int i = 0; i < count; i++)
                {
                    int o = chunkData + i * 12;
                    root.PortalVertices.Add((
                        BitConverter.ToSingle(data, o + 0),
                        BitConverter.ToSingle(data, o + 4),
                        BitConverter.ToSingle(data, o + 8)));
                }
            }
            else if (chunkId == MAGIC_MOPT)
            {
                // Portal descriptors, 20 bytes each:
                //   uint16  startVertex  (index into MOPV)
                //   uint16  count        (vertices in this portal polygon)
                //   C4Plane plane        (float normal[3] + float distance)
                int count = (int)(chunkSize / 20);
                for (int i = 0; i < count; i++)
                {
                    int o = chunkData + i * 20;
                    root.Portals.Add(new WmoPortal
                    {
                        StartVertex = BitConverter.ToUInt16(data, o + 0),
                        VertexCount = BitConverter.ToUInt16(data, o + 2),
                        NormalX = BitConverter.ToSingle(data, o + 4),
                        NormalY = BitConverter.ToSingle(data, o + 8),
                        NormalZ = BitConverter.ToSingle(data, o + 12),
                        PlaneDistance = BitConverter.ToSingle(data, o + 16),
                    });
                }
            }
            else if (chunkId == MAGIC_MOPR)
            {
                // Portal references, 8 bytes each:
                //   uint16 portalIndex (index into MOPT)
                //   uint16 groupIndex  (the group on the far side of the portal)
                //   int16  side        (which half-space the group sits in)
                //   uint16 filler
                // A group's MOGP header names a run of these via PortalStart/
                // PortalCount; each entry is one doorway out of that group.
                int count = (int)(chunkSize / 8);
                for (int i = 0; i < count; i++)
                {
                    int o = chunkData + i * 8;
                    root.PortalRefs.Add(new WmoPortalRef
                    {
                        PortalIndex = BitConverter.ToUInt16(data, o + 0),
                        GroupIndex = BitConverter.ToUInt16(data, o + 2),
                        Side = BitConverter.ToInt16(data, o + 4),
                    });
                }
            }

            pos = chunkEnd;
        }

        // Resolve texture filenames from blob
        if (root.TextureBlob != null)
        {
            foreach (var mat in root.Materials)
            {
                mat.Texture0Name = ReadStringFromBlob(root.TextureBlob, (int)mat.Texture0Offset);
                mat.Texture1Name = ReadStringFromBlob(root.TextureBlob, (int)mat.Texture1Offset);
                mat.Texture2Name = ReadStringFromBlob(root.TextureBlob, (int)mat.Texture2Offset);
            }
        }

        if (root.GroupNameBlob != null)
            foreach (var group in root.GroupInfos)
                group.Name = ReadStringFromBlob(root.GroupNameBlob, group.NameOffset);

        // Resolve doodad M2 filenames from MODN blob
        if (root.DoodadNameBlob != null)
        {
            foreach (var d in root.Doodads)
            {
                d.ModelPath = ReadStringFromBlob(root.DoodadNameBlob, (int)d.NameOffset);
            }
        }

        return root.NGroups > 0 ? root : null;
    }

    /// <summary>
    /// Parse a WMO group file from raw bytes.
    /// Returns geometry: vertices, indices, normals, UVs, per-triangle material IDs, batches.
    /// </summary>
    public static WmoGroupData? ParseGroup(byte[] data, uint mohdFlags = 0)
    {
        if (data == null || data.Length < 20) return null;

        var group = new WmoGroupData();
        int pos = 0;

        // Group file structure:
        //   MVER chunk (version)
        //   MOGP chunk (contains everything else as subchunks)
        // The MOGP chunk size covers the whole rest of the file.
        // MOGP has a 68-byte header, then subchunks start at offset 0x44 from MOGP data start.

        // Find MOGP
        while (pos + 8 <= data.Length)
        {
            uint magic = BitConverter.ToUInt32(data, pos);
            uint chunkSize = BitConverter.ToUInt32(data, pos + 4);
            int chunkData = pos + 8;

            if (magic == MAGIC_MOGP)
            {
                // MOGP header is 68 bytes
                if (chunkData + 68 > data.Length) return null;

                // MOGP begins with two MOTX-style name offsets. The flags and
                // bounds follow them; reading from +0 made interior/exterior
                // classification depend on a string offset, which is why
                // distant Stormwind interior cells rendered outdoors.
                group.GroupFlags = BitConverter.ToUInt32(data, chunkData + 0x08);
                group.BbMinX = BitConverter.ToSingle(data, chunkData + 0x0C);
                group.BbMinY = BitConverter.ToSingle(data, chunkData + 0x10);
                group.BbMinZ = BitConverter.ToSingle(data, chunkData + 0x14);
                group.BbMaxX = BitConverter.ToSingle(data, chunkData + 0x18);
                group.BbMaxY = BitConverter.ToSingle(data, chunkData + 0x1C);
                group.BbMaxZ = BitConverter.ToSingle(data, chunkData + 0x20);
                // MOGP +0x24/0x26 — the run of MOPR portal references that
                // belong to this group: PortalStart is the first index into the
                // root MOPR array, PortalCount how many. This is what a real
                // traversal walks to move from cell to cell. Parsed now, unused
                // until the traversal lands.
                group.PortalStart = BitConverter.ToUInt16(data, chunkData + 0x24);
                group.PortalCount = BitConverter.ToUInt16(data, chunkData + 0x26);
                // MOGP +0x34 = groupLiquid (uint32 — LiquidType DBC id, used by MLIQ).
                // Some vanilla WMOs leave this 0 even when MLIQ is present; the actual
                // per-tile liquid_type bits in SMOLTile take priority for rendering.
                group.GroupLiquid = BitConverter.ToUInt32(data, chunkData + 0x34);

                // MOGP +0x28/0x2A/0x2C — batch type counts. The MOBA batches
                // are ordered: [0..trans-1] transparent, [trans..trans+int-1]
                // interior, [trans+int..] exterior. For aerial/editor views,
                // skip the interior range to hide ceilings, indoor walls, etc.
                group.TransBatchCount = BitConverter.ToUInt16(data, chunkData + 0x28);
                group.IntBatchCount = BitConverter.ToUInt16(data, chunkData + 0x2A);
                group.ExtBatchCount = BitConverter.ToUInt16(data, chunkData + 0x2C);
                // MOGP +0x38 uniqueID: WMOAreaTable.WMOGroupID.
                group.GroupWmoId = BitConverter.ToUInt32(data, chunkData + 0x38);

                // Subchunks start at MOGP data + 68 (0x44)
                int subPos = chunkData + 68;
                int mogpEnd = (int)(chunkData + chunkSize);
                if (mogpEnd > data.Length) mogpEnd = data.Length;

                while (subPos + 8 <= mogpEnd)
                {
                    uint subMagic = BitConverter.ToUInt32(data, subPos);
                    uint subSize = BitConverter.ToUInt32(data, subPos + 4);
                    int subData = subPos + 8;
                    int subEnd = (int)(subData + subSize);
                    if (subEnd > mogpEnd) break;

                    if (subMagic == MAGIC_MOPY)
                    {
                        // Per-triangle material info, 2 bytes each (flags + materialID)
                        int triCount = (int)(subSize / 2);
                        for (int i = 0; i < triCount; i++)
                        {
                            byte flags = data[subData + i * 2];
                            byte matId = data[subData + i * 2 + 1];
                            group.TriMaterials.Add((flags, matId));
                        }
                    }
                    else if (subMagic == MAGIC_MOVI)
                    {
                        // Triangle indices, uint16
                        int idxCount = (int)(subSize / 2);
                        for (int i = 0; i < idxCount; i++)
                        {
                            group.Indices.Add(BitConverter.ToUInt16(data, subData + i * 2));
                        }
                    }
                    else if (subMagic == MAGIC_MOVT)
                    {
                        // Vertices, 3 floats each
                        // WMO coords stored as-is; Z-up → Y-up transform done in controller
                        int vertCount = (int)(subSize / 12);
                        for (int i = 0; i < vertCount; i++)
                        {
                            int vOfs = subData + i * 12;
                            float x = BitConverter.ToSingle(data, vOfs + 0);
                            float y = BitConverter.ToSingle(data, vOfs + 4);
                            float z = BitConverter.ToSingle(data, vOfs + 8);
                            group.Vertices.Add((x, y, z));
                        }
                    }
                    else if (subMagic == MAGIC_MONR)
                    {
                        // Normals, 3 floats each
                        int normCount = (int)(subSize / 12);
                        for (int i = 0; i < normCount; i++)
                        {
                            int nOfs = subData + i * 12;
                            float nx = BitConverter.ToSingle(data, nOfs + 0);
                            float ny = BitConverter.ToSingle(data, nOfs + 4);
                            float nz = BitConverter.ToSingle(data, nOfs + 8);
                            group.Normals.Add((nx, ny, nz));
                        }
                    }
                    else if (subMagic == MAGIC_MOTV)
                    {
                        // UV coords, 2 floats each (only first set)
                        if (group.UVs.Count == 0) // only take first MOTV
                        {
                            int uvCount = (int)(subSize / 8);
                            for (int i = 0; i < uvCount; i++)
                            {
                                int uOfs = subData + i * 8;
                                float u = BitConverter.ToSingle(data, uOfs + 0);
                                float v = BitConverter.ToSingle(data, uOfs + 4);
                                group.UVs.Add((u, v));
                            }
                        }
                    }
                    else if (subMagic == MAGIC_MOCV)
                    {
                        // MOCV — baked per-vertex lighting, 4 bytes per MOVT
                        // vertex, stored BGRA. Present when MOGP flag 0x4
                        // (has_vertex_color) is set; 0x2000 (INTERIOR) is a
                        // different question and does not gate this chunk.
                        // A second MOCV exists from Cataclysm on — vanilla
                        // ships exactly one, so later ones are ignored.
                        //
                        // Swizzled to RGBA on the way in so nothing downstream
                        // has to remember the on-disk order again.
                        if (group.VertexColors.Length == 0)
                        {
                            int colorCount = (int)(subSize / 4);
                            var rgba = new byte[colorCount * 4];
                            for (int i = 0; i < colorCount; i++)
                            {
                                int cOfs = subData + i * 4;
                                rgba[i * 4 + 0] = data[cOfs + 2]; // R
                                rgba[i * 4 + 1] = data[cOfs + 1]; // G
                                rgba[i * 4 + 2] = data[cOfs + 0]; // B
                                rgba[i * 4 + 3] = data[cOfs + 3]; // A
                            }
                            group.VertexColors = rgba;
                        }
                    }
                    else if (subMagic == MAGIC_MOBA)
                    {
                        // Render batches, 24 bytes each
                        // Layout: 6×uint16 bounding box (12 bytes) + uint32 startIndex + uint16 nIndices
                        //         + uint16 startVertex + uint16 endVertex + byte flags + byte materialId
                        int batchCount = (int)(subSize / 24);
                        for (int i = 0; i < batchCount; i++)
                        {
                            int bOfs = subData + i * 24;
                            group.Batches.Add(new WmoBatch
                            {
                                // Bytes 0-11: bounding box (6 × uint16 = 12 bytes)
                                IndexStart = BitConverter.ToUInt32(data, bOfs + 12),   // 0x0C
                                IndexCount = BitConverter.ToUInt16(data, bOfs + 16),   // 0x10
                                VertexStart = BitConverter.ToUInt16(data, bOfs + 18),  // 0x12
                                VertexEnd = BitConverter.ToUInt16(data, bOfs + 20),    // 0x14
                                // Byte 22 (0x16): flags
                                MaterialId = data[bOfs + 23],                          // 0x17
                            });
                        }
                    }
                    else if (subMagic == MAGIC_MODR)
                    {
                        // MODR — doodad references. The root's MODD array is
                        // flat and says nothing about which room a barrel is
                        // in; this is the only thing that does, and it is what
                        // decides whether a prop takes baked interior light or
                        // daylight. Measured across 191 vanilla WMOs: every one
                        // of 70,228 doodads is referenced by at least one group,
                        // so there is no orphan case to design around.
                        //
                        // The mapping is many-to-many. A doodad standing in a
                        // doorway is legitimately listed by both rooms.
                        int refCount = (int)(subSize / 2);
                        group.DoodadRefs.Capacity = refCount;
                        for (int i = 0; i < refCount; i++)
                            group.DoodadRefs.Add(BitConverter.ToUInt16(data, subData + i * 2));
                    }
                    else if (subMagic == MAGIC_MLIQ)
                    {
                        // MLIQ — water/lava surface inside the WMO group.
                        // Reference: wowdev WMO#MLIQ_chunk + Noggit wmo_liquid.cpp::initGeometry.
                        //
                        // Layout:
                        //   +0x00  WMOLiquidHeader  (30 bytes)
                        //   +0x1E  SMOLVert[xverts * yverts]  (8 bytes each)
                        //   +...   SMOLTile[xtiles * ytiles]  (1 byte each)
                        //
                        // Vertex grid is (xtiles+1) × (ytiles+1). Tile (i, j) uses vertices
                        // (i, j), (i+1, j), (i+1, j+1), (i, j+1).
                        //
                        // Tile byte: bit 0x08 = dont_render (skip this tile).
                        //            bits 0..5 are the liquid type / material code.
                        if (subSize >= WMO_LIQUID_HEADER_SIZE)
                        {
                            int xverts = (int)BitConverter.ToUInt32(data, subData + 0x00);
                            int yverts = (int)BitConverter.ToUInt32(data, subData + 0x04);
                            int xtiles = (int)BitConverter.ToUInt32(data, subData + 0x08);
                            int ytiles = (int)BitConverter.ToUInt32(data, subData + 0x0C);
                            float cornerX = BitConverter.ToSingle(data, subData + 0x10);
                            float cornerY = BitConverter.ToSingle(data, subData + 0x14);
                            float cornerZ = BitConverter.ToSingle(data, subData + 0x18);
                            ushort mtlId = BitConverter.ToUInt16(data, subData + 0x1C);

                            int vertCount = xverts * yverts;
                            int tileCount = xtiles * ytiles;
                            int needed = WMO_LIQUID_HEADER_SIZE
                                       + vertCount * WMO_LIQUID_VERT_SIZE
                                       + tileCount * WMO_LIQUID_TILE_SIZE;

                            if (xverts > 0 && yverts > 0 && xtiles > 0 && ytiles > 0
                                && (uint)needed <= subSize
                                && subData + needed <= mogpEnd)
                            {
                                var liq = new WmoLiquid
                                {
                                    XVerts = xverts,
                                    YVerts = yverts,
                                    XTiles = xtiles,
                                    YTiles = ytiles,
                                    CornerX = cornerX,
                                    CornerY = cornerY,
                                    CornerZ = cornerZ,
                                    MaterialId = mtlId,
                                    VertexHeights = new float[vertCount],
                                    VertexS = new short[vertCount],
                                    VertexT = new short[vertCount],
                                    TileFlags = new byte[tileCount],
                                };

                                // Vertices: 4 bytes union + float height. The union is
                                // per-substance (wowdev SMOWVert/SMOMVert):
                                //   water/ocean/slime: flow1, flow2, flow1Pct, filler (4 x uint8)
                                //   magma:             s, t (2 x int16) — authored texture coords
                                // The height float is at +4 in BOTH layouts. The union is
                                // stored raw as two int16s here; it is only MEANINGFUL for
                                // magma tiles (see WmoLiquid.VertexS). Verified against
                                // Blackrock groups 38/43 (2026-08-13): magma s/t are smooth
                                // int16 gradients spanning about -1618..1971 across the lake,
                                // while heights land exactly on the CornerZ..0 band.
                                int vBase = subData + WMO_LIQUID_HEADER_SIZE;
                                for (int i = 0; i < vertCount; i++)
                                {
                                    int vp = vBase + i * WMO_LIQUID_VERT_SIZE;
                                    liq.VertexS[i] = BitConverter.ToInt16(data, vp);
                                    liq.VertexT[i] = BitConverter.ToInt16(data, vp + 2);
                                    liq.VertexHeights[i] = BitConverter.ToSingle(data, vp + 4);
                                }

                                // Tiles: 1 byte each. Caller checks (b & 0x08) for dont_render.
                                int tBase = vBase + vertCount * WMO_LIQUID_VERT_SIZE;
                                Array.Copy(data, tBase, liq.TileFlags, 0, tileCount);

                                group.Liquid = liq;
                            }
                        }
                    }

                    subPos = subEnd;
                }

                // Needs MOBA and the MOGP batch counts, so it can only run once
                // the whole group has been read.
                FixVertexColors(group, mohdFlags);

                break; // Only one MOGP per file
            }

            pos = (int)(chunkData + chunkSize);
        }

        return (group.Vertices.Count > 0 && group.Indices.Count >= 3) || group.Liquid != null
            ? group
            : null;
    }

    /// <summary>
    /// CMapObjGroup::FixColorVertexAlpha — the load-time half of vanilla's
    /// baked interior lighting. Destructive and in place; guarded so it can
    /// never run twice on the same group (a second pass roughly quarters the
    /// brightness and destroys the transparent range's blend weights).
    ///
    /// WHAT THIS IS NOT: the widely-copied pseudocode on wowdev.wiki is
    /// decompiled from build 18179 (WoD) and belongs to the UNIFIED render
    /// path, where MOHD.ambColor is subtracted at load and re-added by the
    /// shader. 1.12 is the CLASSIC path — ambColor is already baked into the
    /// on-disk MOCV — so we neither subtract nor re-add it. Warcraft.NET's
    /// MOHDFlags documents the switch: "In 3.3.5a this flag switches between
    /// classic render path (MOHD color is baked into MCV values) and unified
    /// (MOHD color is added to lighting at runtime)". Following the wiki
    /// literally on vanilla data drags every interior toward black.
    ///
    /// Alpha means two different things either side of begin_second_fixup:
    ///   [0, intStart)  transparent batches — portal-proximity blend weight,
    ///                  genuinely 0..255 in the data, PRESERVED.
    ///   [intStart, n)  interior + exterior batches — a brightness boost with
    ///                  64 as unity (rgb *= 1 + a/64), consumed here and then
    ///                  overwritten with the shader's "mix in runtime light"
    ///                  switch. Vanilla data keeps it near 0, so this is close
    ///                  to a no-op in practice — which is itself the evidence
    ///                  that the values are already fully baked.
    ///
    /// It is never opacity. Feeding it to glBlendFunc turns walls into glass.
    ///
    /// The classic path also divides the transparent range's RGB by 2 while
    /// the shader multiplies every vertex colour by 2 — a matched pair. We
    /// fold it away here instead of round-tripping 8-bit values through a
    /// halving; noggit3 does the same, with the comment "I removed the
    /// color = color/2 because it's just multiplied by 2 in the shader
    /// afterward in blizzard's code."
    /// </summary>
    private static void FixVertexColors(WmoGroupData group, uint mohdFlags)
    {
        var mocv = group.VertexColors;
        if (mocv.Length == 0 || group.VertexColorsFixed) return;
        group.VertexColorsFixed = true;

        int n = mocv.Length / 4;
        // MOGP 0x8 = EXTERIOR. Exterior-lit vertices take the runtime sun on
        // top of their baked colour; interior ones are baked and nothing else.
        byte settledAlpha = (group.GroupFlags & 0x08) != 0 ? (byte)255 : (byte)0;

        // begin_second_fixup: the first vertex past the transparent batches.
        // MOBA.VertexEnd is INCLUSIVE, hence the +1.
        int intStart = 0;
        if (group.TransBatchCount > 0 && group.TransBatchCount <= group.Batches.Count)
            intStart = group.Batches[group.TransBatchCount - 1].VertexEnd + 1;
        if (intStart > n) intStart = n;
        group.InteriorVertexStart = intStart;

        if ((mohdFlags & 0x08) != 0)
        {
            // do_not_fix_vertex_color_alpha: RGB is final as authored.
            for (int i = intStart; i < n; i++) mocv[i * 4 + 3] = settledAlpha;
            return;
        }

        for (int i = intStart; i < n; i++)
        {
            int o = i * 4;
            int boost = mocv[o + 3];
            for (int c = 0; c < 3; c++)
            {
                // v * boost reaches 65025 — it must not be computed in a byte.
                int v = mocv[o + c];
                v += v * boost / 64;
                mocv[o + c] = v > 255 ? (byte)255 : (byte)v;
            }
            mocv[o + 3] = settledAlpha;
        }
    }

    private static string ReadStringFromBlob(byte[] blob, int offset)
    {
        if (offset < 0 || offset >= blob.Length) return "";
        int end = offset;
        while (end < blob.Length && blob[end] != 0) end++;
        if (end == offset) return "";
        return Encoding.ASCII.GetString(blob, offset, end - offset);
    }
}

// ── DTOs ──

public class WmoRootData
{
    public uint NTextures { get; set; }
    public uint NGroups { get; set; }
    public uint NPortals { get; set; }
    public uint NLights { get; set; }
    /// <summary>MOHD +0x20: WMOAreaTable.WMOID.</summary>
    public uint WmoId { get; set; }
    /// <summary>
    /// MOHD +0x3C. 0x1 = do_not_attenuate_vertices_based_on_distance_to_portal,
    /// 0x2 = use_unified_render_path, 0x4 = use_liquid_type_dbc_id,
    /// 0x8 = do_not_fix_vertex_color_alpha. Every vanilla WMO checked ships 0,
    /// i.e. the classic render path with ambColor pre-baked into MOCV.
    /// </summary>
    public uint Flags { get; set; }
    /// <summary>MOHD +0x1C ambColor, swizzled from the on-disk BGRA.
    /// Already baked into interior MOCV on the classic path — do not add it to
    /// lighting. Kept as the ambient fallback for doodads inside the WMO.</summary>
    public byte AmbientR { get; set; }
    public byte AmbientG { get; set; }
    public byte AmbientB { get; set; }
    public byte AmbientA { get; set; }
    public float BbMinX { get; set; }
    public float BbMinY { get; set; }
    public float BbMinZ { get; set; }
    public float BbMaxX { get; set; }
    public float BbMaxY { get; set; }
    public float BbMaxZ { get; set; }
    public byte[]? TextureBlob { get; set; }
    public List<WmoMaterial> Materials { get; set; } = new();
    public byte[]? GroupNameBlob { get; set; }
    public List<WmoGroupInfo> GroupInfos { get; set; } = new();

    // Embedded doodads (MODS + MODN + MODD)
    public List<WmoDoodadSet> DoodadSets { get; set; } = new();
    public byte[]? DoodadNameBlob { get; set; }
    public List<WmoDoodadDef> Doodads { get; set; } = new();

    // Portals (MOPV + MOPT + MOPR). Parsed for the future visibility traversal;
    // no renderer reads these yet. NPortals (from MOHD) should equal Portals.Count.
    public List<(float x, float y, float z)> PortalVertices { get; set; } = new();
    public List<WmoPortal> Portals { get; set; } = new();
    public List<WmoPortalRef> PortalRefs { get; set; } = new();
}

/// <summary>
/// One portal polygon from MOPT (20 bytes). The polygon is
/// <see cref="VertexCount"/> vertices of <see cref="WmoRootData.PortalVertices"/>
/// starting at <see cref="StartVertex"/>; the plane (normal + distance) is the
/// portal's supporting plane, in WMO local space. Consumed by portal traversal,
/// which does not exist yet.
/// </summary>
public class WmoPortal
{
    public ushort StartVertex { get; set; }
    public ushort VertexCount { get; set; }
    public float NormalX { get; set; }
    public float NormalY { get; set; }
    public float NormalZ { get; set; }
    public float PlaneDistance { get; set; }
}

/// <summary>
/// One portal-to-group reference from MOPR (8 bytes). A group's MOGP header
/// names a contiguous run of these (PortalStart/PortalCount); each says "portal
/// <see cref="PortalIndex"/> connects this group to group
/// <see cref="GroupIndex"/>", with <see cref="Side"/> telling the traversal
/// which half-space of the portal plane the target group is on.
/// </summary>
public class WmoPortalRef
{
    public ushort PortalIndex { get; set; }
    public ushort GroupIndex { get; set; }
    public short Side { get; set; }
}

public class WmoGroupInfo
{
    public uint Flags { get; set; }
    public int NameOffset { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>
/// Named doodad set from MODS (32 bytes). A WMO can ship multiple sets
/// (e.g. "Set_$DefaultGlobal" + "Set_Stormwind_Day" + destroyed variants).
/// Each MODF placement chooses one set via its doodadSet field.
/// FirstInstanceIndex/DoodadCount slice the MODD array for this set.
/// </summary>
public class WmoDoodadSet
{
    public string Name { get; set; } = "";
    public uint FirstInstanceIndex { get; set; }
    public uint DoodadCount { get; set; }
}

/// <summary>
/// Single doodad instance from MODD (40 bytes), in the WMO's local
/// coordinate system (Z-up). Position, rotation (quaternion XYZW), and
/// scale must be composed with the parent WMO's world transform at
/// placement time.
///
/// COLOR IS NOT A TINT, despite what the wiki calls it. It is the light the
/// artist baked into THIS PLACEMENT when the building was authored - the same
/// barrel model carries (60, 60, 60) in a dark corner and (114, 113, 110) two
/// rooms away. Measured against an independent MOCV sample taken at each
/// doodad's own position it correlates at 0.89 over 4,929 channels (0.97 on
/// Stormwind's Subway alone, 1,006 doodads), so it lives on the same scale as
/// raw MOCV and belongs behind the same overbright factor the walls use.
///
/// It is only meaningful for doodads owned by an INTERIOR group. Exterior-owned
/// doodads carry dark, mostly-ignored values (mean RGB 51/51/63, 13% pure
/// black) because the real client lights those by daylight and never reads the
/// field. Applying it ungated turns every lamp-post black.
///
/// Alpha is 255 in every vanilla WMO measured and carries no information.
/// </summary>
public class WmoDoodadDef
{
    public uint NameOffset { get; set; }   // byte offset into MODN
    public byte Flags { get; set; }        // high byte of the packed name+flags field
    public string ModelPath { get; set; } = ""; // resolved from MODN blob

    public float PosX { get; set; }
    public float PosY { get; set; }
    public float PosZ { get; set; }

    public float QuatX { get; set; }
    public float QuatY { get; set; }
    public float QuatZ { get; set; }
    public float QuatW { get; set; }

    public float Scale { get; set; } = 1.0f;

    public byte ColorB { get; set; } = 255;
    public byte ColorG { get; set; } = 255;
    public byte ColorR { get; set; } = 255;
    public byte ColorA { get; set; } = 255;
}

public class WmoMaterial
{
    public uint Flags { get; set; }
    public uint Shader { get; set; }
    public uint BlendMode { get; set; }
    public uint Texture0Offset { get; set; }
    public uint Texture1Offset { get; set; }
    /// <summary>MOMT +0x20: TerrainType.dbc id used by the WMO footstep surface ray.</summary>
    public uint GroundType { get; set; }
    public uint Texture2Offset { get; set; }
    public string Texture0Name { get; set; } = "";
    public string Texture1Name { get; set; } = "";
    public string Texture2Name { get; set; } = "";
    public bool IsNoCull => (Flags & 0x04) != 0; // two-sided
    public bool IsTransparent => BlendMode != 0;
    /// <summary>MOMT F_UNLIT (0x01): drawn at texture brightness, no scene light — lamp glass, glow panes.</summary>
    public bool IsUnlit => (Flags & 0x01) != 0;
    /// <summary>MOMT F_SIDN (0x10): self-illuminated day/night — the window-glows-at-night mechanism.</summary>
    public bool IsSidn => (Flags & 0x10) != 0;
    /// <summary>MOMT F_WINDOW (0x20).</summary>
    public bool IsWindow => (Flags & 0x20) != 0;
    /// <summary>
    /// MOMT+0x10 SIDN emissive colour (BGRA on disk, RGB here, 0..1). The reference's per-frame
    /// updater scales it by the night fraction and adds it as the material EMISSION on lit batches.
    /// Meaningless (usually zero) when <see cref="IsSidn"/> is clear.
    /// </summary>
    public System.Numerics.Vector3 SidnColor { get; set; }
}

public class WmoGroupData
{
    public string GroupName { get; set; } = "";
    public uint GroupFlags { get; set; }
    /// <summary>MOGP +0x38 uniqueID: WMOAreaTable.WMOGroupID.</summary>
    public uint GroupWmoId { get; set; }
    public float BbMinX { get; set; }
    public float BbMinY { get; set; }
    public float BbMinZ { get; set; }
    public float BbMaxX { get; set; }
    public float BbMaxY { get; set; }
    public float BbMaxZ { get; set; }
    /// <summary>
    /// MOGP +0x34 groupLiquid — whole-group liquid override. 0x0F means no
    /// override; an MLIQ grid's per-tile nibble remains a separate mechanism.
    /// </summary>
    public uint GroupLiquid { get; set; } = 0x0fu;
    /// <summary>MOGP +0x24: first index into the root MOPR portal-reference array for this group.</summary>
    public ushort PortalStart { get; set; }
    /// <summary>MOGP +0x26: number of MOPR portal references that belong to this group.</summary>
    public ushort PortalCount { get; set; }
    /// <summary>MOGP +0x28: transparent batch count (rendered first, alpha-blended).</summary>
    public ushort TransBatchCount { get; set; }
    /// <summary>MOGP +0x2A: interior batch count (skip for aerial/editor view).</summary>
    public ushort IntBatchCount { get; set; }
    /// <summary>MOGP +0x2C: exterior batch count (always visible from outside).</summary>
    public ushort ExtBatchCount { get; set; }
    public List<(byte flags, byte materialId)> TriMaterials { get; set; } = new();
    public List<ushort> Indices { get; set; } = new();
    public List<(float x, float y, float z)> Vertices { get; set; } = new();
    public List<(float x, float y, float z)> Normals { get; set; } = new();
    public List<(float u, float v)> UVs { get; set; } = new();
    public List<WmoBatch> Batches { get; set; } = new();
    /// <summary>
    /// MOCV baked lighting, 4 bytes per vertex, RGBA (the file stores BGRA).
    /// Empty when the group has no MOCV, which in vanilla means an exterior
    /// group lit by the sun instead. After FixVertexColors the alpha channel
    /// is a portal blend weight below InteriorVertexStart and a "mix in the
    /// runtime light" switch (255 yes, 0 baked-only) above it. Never opacity.
    /// </summary>
    public byte[] VertexColors { get; set; } = Array.Empty<byte>();
    /// <summary>
    /// MODR — indices into the ROOT's MODD array for the doodads that belong to
    /// this group. Empty when the group has no doodads (MOGP flag 0x800 clear).
    ///
    /// A doodad may appear in more than one group's list; that is not a data
    /// error, it is a prop sitting on a boundary.
    /// </summary>
    public List<ushort> DoodadRefs { get; set; } = new();

    /// <summary>begin_second_fixup: first vertex past the transparent batches.</summary>
    public int InteriorVertexStart { get; set; }
    /// <summary>Guard — FixColorVertexAlpha is destructive and in place.</summary>
    public bool VertexColorsFixed { get; set; }
    /// <summary>
    /// Water/lava surface inside this group (null = no MLIQ). Set when GroupFlags &amp; 0x1000
    /// (SMOGroup::LIQUIDSURFACE). Coordinates are in WMO local space — caller must
    /// transform by the WMO instance's MODF position+rotation.
    /// </summary>
    public WmoLiquid? Liquid { get; set; }
    public bool IsExterior => (GroupFlags & 0x08) != 0;
    public bool IsInterior => (GroupFlags & 0x2000) != 0;
    /// <summary>MOGP 0x4 (has_vertex_color) — the group ships a MOCV chunk.</summary>
    public bool HasVertexColors => (GroupFlags & 0x04) != 0;
    /// <summary>
    /// Occlusion-only polygon group. It participates in WMO visibility tests
    /// but is never visible geometry and must not be uploaded or drawn.
    /// </summary>
    public bool IsAntiportal => (GroupFlags & 0x04000000) != 0;
    /// <summary>True when MOGP.GroupFlags has SMOGroup::LIQUIDSURFACE (0x1000) set.</summary>
    public bool HasLiquid => (GroupFlags & 0x1000) != 0;
}

/// <summary>
/// One MLIQ liquid surface inside a WMO group. Layout matches wowdev WMO#MLIQ_chunk:
/// a (XVerts × YVerts) vertex grid of heights plus a (XTiles × YTiles) tile mask
/// (XTiles = XVerts - 1, etc.). Tile flag bit 0x08 means dont_render.
///
/// LOCAL SPACE CONVENTION — settled 2026-07-26 against 235 real MLIQ groups in
/// wmo.MPQ. See PLAN_15_WMO_LIQUID.md §4.1 for the scoring table.
///
///   vertex(i, j) = ( CornerX + i*UNIT,  CornerY + j*UNIT,  VertexHeights[j*XVerts + i] )
///
/// MLIQ is **Z-up, in the same local space as MOVT** (handbook §3.4). This text
/// previously claimed Noggit's Y-up layout — (CornerX + i*UNIT, height,
/// CornerY - j*UNIT) — which is Noggit's own RENDER space, not the file's, and
/// scored 18x worse than the Z-up reading when each candidate was tested for
/// how far it puts liquid outside its group's own authored MOGP bounding box.
///
/// This is the SECOND stale Noggit-derived comment found in this file; the
/// first claimed MOVT was converted to Y-up at parse, which it also is not.
/// Treat prose in this file as a lead, never as ground truth.
///
/// The caller composes this with the WMO instance's MODF position+rotation to get world coords.
/// </summary>
public class WmoLiquid
{
    public int XVerts { get; set; }
    public int YVerts { get; set; }
    public int XTiles { get; set; }
    public int YTiles { get; set; }
    public float CornerX { get; set; }
    public float CornerY { get; set; }
    public float CornerZ { get; set; }
    public ushort MaterialId { get; set; }
    /// <summary>Vertex heights, row-major over (yverts × xverts): index = j*xverts + i.</summary>
    public float[] VertexHeights { get; set; } = Array.Empty<float>();

    /// <summary>
    /// The 4-byte per-vertex union that precedes each height float, read as two
    /// int16s. ONLY MEANINGFUL FOR MAGMA: there it is Blizzard's hand-authored
    /// texture coordinate (SMOMVert s/t) — one texture repeat per 255 units —
    /// which is what paints the big swirls of lava dragged around Blackrock's
    /// central spire. For water/ocean/slime the same bytes are flow data
    /// (flow1, flow2, flow1Pct, filler as 4 x uint8) and this reinterpretation
    /// is garbage; callers must gate on the tile substance being magma.
    ///
    /// Measured on Blackrock (2026-08-13): with UV = value/255, group 38's lake
    /// authors one repeat per ~35-175 yd and group 43 per ~8-30 yd — a warped,
    /// anisotropic flow mapping, not a uniform scale.
    /// </summary>
    public short[] VertexS { get; set; } = Array.Empty<short>();

    /// <summary>See <see cref="VertexS"/>.</summary>
    public short[] VertexT { get; set; } = Array.Empty<short>();
    /// <summary>Tile flag bytes, row-major over (ytiles × xtiles): index = j*xtiles + i.
    ///
    /// MEASURED over all 235 MLIQ groups (PLAN_15 §4.3): the LOW NIBBLE takes only the
    /// values 0, 2, 3, 4, 6, 7 and 15 — never 8..14. So:
    ///
    ///   (b &amp; 0x0F) == 0x0F  ->  no liquid here, skip the tile
    ///   otherwise (b &amp; 0x03) ->  0 water, 1 ocean, 2 magma, 3 slime
    ///
    /// The older "bit 0x08 = dont_render" test gives the same answer on real data
    /// ONLY because nibbles 8..14 never occur. It is right by luck, not by
    /// construction, and it under-counts the type field by a bit. Prefer the nibble
    /// test. Use <see cref="IsHidden"/> / <see cref="BasicType"/> rather than either.
    ///
    /// Hidden tiles are the majority (46,455 of ~115,000): a WMO liquid grid is a
    /// bounding rectangle with the actual pool cut out of it, so skipping them is not
    /// an optimisation — it is the difference between a canal and a slab of water
    /// across the whole district.</summary>
    public byte[] TileFlags { get; set; } = Array.Empty<byte>();

    /// <summary>True when this tile carries no liquid and must not be drawn.
    /// PLAN_15 §4.3.</summary>
    public bool IsHidden(int i, int j)
    {
        int k = j * XTiles + i;
        return (uint)k >= (uint)TileFlags.Length || (TileFlags[k] & 0x0F) == 0x0F;
    }

    /// <summary>Substance of a tile: 0 water, 1 ocean, 2 magma, 3 slime.
    ///
    /// Derived from placement, not from LiquidType.dbc (which is in patch.MPQ, over
    /// the transfer cap). Every one of the 235 groups agrees: `&amp; 3 == 0` is
    /// Stormwind's canals / Maraudon / Blackfathom, `== 2` is Blackrock and
    /// Ironforge, `== 3` is Undercity and Stratholme. Zero counterexamples.
    ///
    /// **These are NOT the codes water.frag routes on** — see PLAN_15 §4.5 and
    /// translate before handing a type to the shader. Three of the six live codes
    /// happen to mean the same thing in both encodings, which is exactly why passing
    /// them through untranslated survives a test in Stormwind and ships broken in
    /// Ironforge.</summary>
    public int BasicType(int i, int j)
    {
        int k = j * XTiles + i;
        return (uint)k >= (uint)TileFlags.Length ? 0 : TileFlags[k] & 0x03;
    }

    /// <summary>Raw liquid/speed low nibble; 0x0f means no liquid.</summary>
    public byte TypeNibble(int i, int j)
    {
        int k = j * XTiles + i;
        return (uint)k >= (uint)TileFlags.Length ? (byte)0x0f :
            (byte)(TileFlags[k] & 0x0f);
    }

    /// <summary>Absolute height of grid vertex (i, j). Row-major over (yverts x xverts).</summary>
    public float HeightAt(int i, int j)
    {
        int k = j * XVerts + i;
        return (uint)k >= (uint)VertexHeights.Length ? 0f : VertexHeights[k];
    }

    /// <summary>
    /// Authored texture coordinate of grid vertex (i, j), in repeats (the raw
    /// int16 s/t divided by 255). ONLY meaningful when the surrounding tiles are
    /// magma — see <see cref="VertexS"/>.
    /// </summary>
    public (float U, float V) UvAt(int i, int j)
    {
        int k = j * XVerts + i;
        return (uint)k >= (uint)VertexS.Length
            ? (0f, 0f)
            : (VertexS[k] / 255f, VertexT[k] / 255f);
    }
}

public class WmoBatch
{
    public uint IndexStart { get; set; }
    public ushort IndexCount { get; set; }
    public ushort VertexStart { get; set; }
    public ushort VertexEnd { get; set; }
    public byte MaterialId { get; set; }
}
