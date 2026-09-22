using System.Numerics;
using System.Text;

namespace MSUIClient.Creator;

// ═══════════════════════════════════════════════════════════════════════════
// M2 MESH LAYER reader/patcher for vanilla v256 M2s (shared_docs/SPELL_CREATOR_IDE.md §2.5).
//
// An effect M2 draws three kinds of thing: particle emitters (M2EmitterParser), ribbon
// emitters (M2RibbonParser) and MESH geometry - the textured planes a Cleave crescent or
// a glow sheet is made of. The workshop's unit of mesh editing is the BATCH ("layer"): one
// submesh drawn with one texture through one material (render flags), optionally tinted by
// a colour record and faded by a transparency track. Verified v256 layout (M2Reader):
//
//   header 0x4C views (44 B each; vanilla inlines them): +24 nSubmeshes/+28 ofs, +32 nBatches/+36 ofs
//   submesh 32 B: +0 id u16, +4 vertexStart, +6 vertexCount, +8 indexStart, +10 indexCount
//   batch   24 B: +4 submeshIndex, +8 colorIndex s16, +10 materialIndex, +16 textureIndex
//                 (into the texture LOOKUP at 0x94), +20 textureWeightIndex (into the
//                 transparency LOOKUP at 0xA4)
//   render flags 0x84, 4 B: flags u16 (0x01 unlit, 0x02 unfogged, 0x04 two-sided,
//                 0x08 no depth test, 0x10 no depth write), blend u16
//   colours 0x54, 56 B: colour track (28 B, vec3 keys) + alpha track (28 B, fixed16 keys)
//   transparency 0x64, 28 B tracks (fixed16 keys)
//
// PATCH LAW: a hidden submesh has its triangle count zeroed in EVERY view (the 1.12 client
// and MSUI both skip it). Material and texture-lookup changes never write a shared record:
// the array is relocated to EOF with a private entry appended and the batch repointed, the
// same resize-at-EOF strategy CloneEmitter and PatchTextureFilenamesResize use, so nothing
// at a fixed offset ever moves. Colour / alpha / transparency edits write the FIRST key.
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>One mesh layer (batch) as read from the bytes.</summary>
public sealed class MeshSnapshot
{
    public int BatchIndex { get; set; }
    public int SubmeshIndex { get; set; }
    public ushort SubmeshId { get; set; }
    public ushort TriangleIndexCount { get; set; }
    /// <summary>How many batches draw on the same submesh (layers stack on one geometry).</summary>
    public int LayersOnSubmesh { get; set; } = 1;
    /// <summary>Texture table slot (through the lookup), or -1.</summary>
    public int TextureSlot { get; set; } = -1;
    public int MaterialIndex { get; set; } = -1;
    public ushort Blend { get; set; }
    public ushort MaterialFlags { get; set; }
    public int ColorIndex { get; set; } = -1;
    public Vector3? ColorFirst { get; set; }
    public int ColorKeys { get; set; }
    public float? AlphaFirst { get; set; }
    public int AlphaKeys { get; set; }
    public int TransparencyIndex { get; set; } = -1;
    public float? TransparencyFirst { get; set; }
    public int TransparencyKeys { get; set; }
    /// <summary>The submesh has no triangles in these bytes (authored empty, or hidden by us).</summary>
    public bool Hidden => TriangleIndexCount == 0;
}

/// <summary>Absolute edits for one mesh layer; null = leave authored.</summary>
public sealed class MeshPatch
{
    public int BatchIndex { get; set; }
    public ushort? Blend { get; set; }
    public ushort? MaterialFlags { get; set; }
    public int? TextureSlot { get; set; }
    public Vector3? Color { get; set; }
    public float? Alpha { get; set; }
    public float? Transparency { get; set; }

    public bool IsEmpty => Blend is null && MaterialFlags is null && TextureSlot is null &&
                           Color is null && Alpha is null && Transparency is null;
}

public static class M2MeshParser
{
    public const int HEADER_VIEWS = 0x4C;
    public const int HEADER_COLORS = 0x54;
    public const int HEADER_TRANSPARENCY = 0x64;
    public const int HEADER_RENDER_FLAGS = 0x84;
    public const int HEADER_TEXTURE_LOOKUP = 0x94;
    public const int HEADER_TRANSPARENCY_LOOKUP = 0xA4;
    public const int VIEW_SIZE = 44;
    public const int SUBMESH_SIZE = 32;
    public const int BATCH_SIZE = 24;
    public const int RENDER_FLAG_SIZE = 4;
    public const int COLOR_SIZE = 56;
    public const int TRACK_SIZE = 28;
    private const int MIN_HEADER = 0x144;

    // ── flags ──
    public const ushort FlagUnlit = 0x01;
    public const ushort FlagUnfogged = 0x02;
    public const ushort FlagTwoSided = 0x04;
    public const ushort FlagNoDepthTest = 0x08;
    public const ushort FlagNoDepthWrite = 0x10;

    // ── byte helpers (shared with the ribbon parser) ─────────────────────────

    internal static bool IsVanilla(byte[]? d) =>
        d is not null && d.Length >= MIN_HEADER &&
        Encoding.ASCII.GetString(d, 0, 4) == "MD20" && BitConverter.ToUInt32(d, 4) is >= 256 and < 264;   // every vanilla revision, like M2Reader

    internal static uint U32(byte[] d, long o) => BitConverter.ToUInt32(d, (int)o);
    internal static ushort U16(byte[] d, long o) => BitConverter.ToUInt16(d, (int)o);
    internal static float F32(byte[] d, long o) => BitConverter.ToSingle(d, (int)o);
    internal static void W32(byte[] d, long o, uint v) => BitConverter.GetBytes(v).CopyTo(d, (int)o);
    internal static void W16(byte[] d, long o, ushort v) => BitConverter.GetBytes(v).CopyTo(d, (int)o);
    internal static void WF(byte[] d, long o, float v) => BitConverter.GetBytes(v).CopyTo(d, (int)o);

    internal static (uint Count, uint Offset) Array(byte[] d, int header) =>
        (U32(d, header), U32(d, header + 4));

    internal static bool Fits(byte[] d, uint offset, uint count, int size) =>
        count == 0 || (offset > 0 && offset + (long)count * size <= d.Length);

    // ── M2Track first-key access (vanilla 28-byte AnimationBlock: keys at +20/+24) ──

    internal static (uint Count, uint Offset) TrackKeys(byte[] d, long trackOfs) =>
        (U32(d, trackOfs + 20), U32(d, trackOfs + 24));

    internal static int TrackKeyCount(byte[] d, long trackOfs) => (int)TrackKeys(d, trackOfs).Count;

    internal static float? TrackFirstFloat(byte[] d, long trackOfs)
    {
        var (n, ofs) = TrackKeys(d, trackOfs);
        return n > 0 && ofs > 0 && ofs + 4 <= d.Length ? F32(d, ofs) : null;
    }

    internal static Vector3? TrackFirstVec3(byte[] d, long trackOfs)
    {
        var (n, ofs) = TrackKeys(d, trackOfs);
        return n > 0 && ofs > 0 && ofs + 12 <= d.Length
            ? new Vector3(F32(d, ofs), F32(d, ofs + 4), F32(d, ofs + 8)) : null;
    }

    internal static float? TrackFirstFixed16(byte[] d, long trackOfs)
    {
        var (n, ofs) = TrackKeys(d, trackOfs);
        if (n == 0 || ofs == 0 || ofs + 2 > d.Length) return null;
        return Math.Clamp(unchecked((short)U16(d, ofs)) / 32767f, 0f, 1f);
    }

    internal static bool WriteTrackFirstFloat(byte[] d, long trackOfs, float v)
    {
        var (n, ofs) = TrackKeys(d, trackOfs);
        if (n == 0 || ofs == 0 || ofs + 4 > d.Length) return false;
        WF(d, ofs, v);
        return true;
    }

    internal static bool WriteTrackFirstVec3(byte[] d, long trackOfs, Vector3 v)
    {
        var (n, ofs) = TrackKeys(d, trackOfs);
        if (n == 0 || ofs == 0 || ofs + 12 > d.Length) return false;
        WF(d, ofs, v.X); WF(d, ofs + 4, v.Y); WF(d, ofs + 8, v.Z);
        return true;
    }

    internal static bool WriteTrackFirstFixed16(byte[] d, long trackOfs, float unit)
    {
        var (n, ofs) = TrackKeys(d, trackOfs);
        if (n == 0 || ofs == 0 || ofs + 2 > d.Length) return false;
        W16(d, ofs, (ushort)(short)MathF.Round(Math.Clamp(unit, 0f, 1f) * 32767f));
        return true;
    }

    /// <summary>Every key of a fixed16 track to one value (the wholesale "off" for alpha).</summary>
    internal static bool WriteTrackAllFixed16(byte[] d, long trackOfs, float unit)
    {
        var (n, ofs) = TrackKeys(d, trackOfs);
        if (n == 0 || ofs == 0 || ofs + (long)n * 2 > d.Length) return false;
        ushort v = (ushort)(short)MathF.Round(Math.Clamp(unit, 0f, 1f) * 32767f);
        for (uint i = 0; i < n; i++) W16(d, ofs + i * 2, v);
        return true;
    }

    internal static bool WriteTrackAllBytes(byte[] d, long trackOfs, byte value)
    {
        var (n, ofs) = TrackKeys(d, trackOfs);
        if (n == 0 || ofs == 0 || ofs + (long)n > d.Length) return false;
        for (uint i = 0; i < n; i++) d[ofs + i] = value;
        return true;
    }

    // ── relocate-and-append ──────────────────────────────────────────────────

    /// <summary>Relocate a header-addressed array to EOF (16-aligned) with one element appended.
    /// Returns the grown file and the new element's index, or null when the header is unusable.
    /// Fixed-offset structures never move; only the header count/offset are rewritten.</summary>
    internal static (byte[] Data, int NewIndex)? AppendToArray(byte[] d, int header, int elementSize,
        byte[] element)
    {
        if (element.Length != elementSize) return null;
        var (n, ofs) = Array(d, header);
        if (n > 65535 || !Fits(d, ofs, n, elementSize)) return null;
        int padded = (d.Length + 15) & ~15;
        var result = new byte[padded + (int)(n + 1) * elementSize];
        System.Array.Copy(d, result, d.Length);
        if (n > 0) System.Array.Copy(d, ofs, result, padded, n * elementSize);
        System.Array.Copy(element, 0, result, padded + (int)n * elementSize, elementSize);
        W32(result, header, n + 1);
        W32(result, header + 4, (uint)padded);
        return (result, (int)n);
    }

    /// <summary>Append a private material (render flags) record. Shared by the ribbon patcher.</summary>
    internal static (byte[] Data, int MaterialIndex)? AppendMaterial(byte[] d, ushort flags, ushort blend)
    {
        var element = new byte[RENDER_FLAG_SIZE];
        W16(element, 0, flags);
        W16(element, 2, blend);
        return AppendToArray(d, HEADER_RENDER_FLAGS, RENDER_FLAG_SIZE, element);
    }

    // ── read ─────────────────────────────────────────────────────────────────

    /// <summary>View 0's batches as mesh layers, with their material, texture slot, colour and
    /// transparency first keys. Empty for particle-only models.</summary>
    public static List<MeshSnapshot> ReadMeshes(byte[] d)
    {
        var result = new List<MeshSnapshot>();
        if (!IsVanilla(d)) return result;
        // Mirror M2Reader exactly: a model with no vertices is an emitter carrier and its view
        // is never read (its batches, if any, draw nothing); a view with no local vertices or
        // no triangle indices is rejected wholesale.
        if (U32(d, 0x44) == 0) return result;
        var (nViews, ofsViews) = Array(d, HEADER_VIEWS);
        if (nViews == 0 || ofsViews == 0 || ofsViews + VIEW_SIZE > d.Length) return result;
        if (U32(d, ofsViews) == 0 || U32(d, ofsViews + 8) == 0) return result;

        uint nSub = U32(d, ofsViews + 24), ofsSub = U32(d, ofsViews + 28);
        uint nBatch = U32(d, ofsViews + 32), ofsBatch = U32(d, ofsViews + 36);
        // The runtime keeps batches even when the submesh table is unusable (they then draw
        // nothing); keep the same count so the two readers always agree.
        bool subOk = nSub > 0 && ofsSub > 0 && ofsSub + (long)nSub * SUBMESH_SIZE <= d.Length;
        if (!(nBatch > 0 && ofsBatch > 0 && ofsBatch + (long)nBatch * BATCH_SIZE <= d.Length)) return result;
        if (!subOk) nSub = 0;

        var (nLookup, ofsLookup) = Array(d, HEADER_TEXTURE_LOOKUP);
        var (nTransLookup, ofsTransLookup) = Array(d, HEADER_TRANSPARENCY_LOOKUP);
        var (nRf, ofsRf) = Array(d, HEADER_RENDER_FLAGS);
        var (nCol, ofsCol) = Array(d, HEADER_COLORS);
        var (nTr, ofsTr) = Array(d, HEADER_TRANSPARENCY);
        bool lookupOk = Fits(d, ofsLookup, nLookup, 2);
        bool transLookupOk = Fits(d, ofsTransLookup, nTransLookup, 2);
        bool rfOk = Fits(d, ofsRf, nRf, RENDER_FLAG_SIZE);
        bool colOk = Fits(d, ofsCol, nCol, COLOR_SIZE);
        bool trOk = Fits(d, ofsTr, nTr, TRACK_SIZE);

        var layersPerSubmesh = new Dictionary<int, int>();
        for (uint b = 0; b < nBatch; b++)
        {
            int sub = U16(d, ofsBatch + b * BATCH_SIZE + 4);
            layersPerSubmesh[sub] = layersPerSubmesh.GetValueOrDefault(sub) + 1;
        }

        for (uint b = 0; b < nBatch; b++)
        {
            long bo = ofsBatch + b * BATCH_SIZE;
            var mesh = new MeshSnapshot
            {
                BatchIndex = (int)b,
                SubmeshIndex = U16(d, bo + 4),
                ColorIndex = unchecked((short)U16(d, bo + 8)),
                MaterialIndex = U16(d, bo + 10),
            };
            mesh.LayersOnSubmesh = layersPerSubmesh.GetValueOrDefault(mesh.SubmeshIndex, 1);
            if (mesh.SubmeshIndex < nSub)
            {
                long so = ofsSub + (long)mesh.SubmeshIndex * SUBMESH_SIZE;
                mesh.SubmeshId = U16(d, so);
                mesh.TriangleIndexCount = U16(d, so + 10);
            }
            int textureIndex = U16(d, bo + 16);
            if (lookupOk && textureIndex < nLookup) mesh.TextureSlot = U16(d, ofsLookup + textureIndex * 2);
            if (rfOk && mesh.MaterialIndex < nRf)
            {
                long ro = ofsRf + (long)mesh.MaterialIndex * RENDER_FLAG_SIZE;
                mesh.MaterialFlags = U16(d, ro);
                mesh.Blend = U16(d, ro + 2);
            }
            if (colOk && mesh.ColorIndex >= 0 && mesh.ColorIndex < nCol)
            {
                long co = ofsCol + (long)mesh.ColorIndex * COLOR_SIZE;
                mesh.ColorFirst = TrackFirstVec3(d, co);
                mesh.ColorKeys = TrackKeyCount(d, co);
                mesh.AlphaFirst = TrackFirstFixed16(d, co + TRACK_SIZE);
                mesh.AlphaKeys = TrackKeyCount(d, co + TRACK_SIZE);
            }
            int weightIndex = U16(d, bo + 20);
            if (transLookupOk && weightIndex < nTransLookup)
            {
                int transparency = U16(d, ofsTransLookup + weightIndex * 2);
                if (trOk && transparency < nTr)
                {
                    long to = ofsTr + (long)transparency * TRACK_SIZE;
                    mesh.TransparencyIndex = transparency;
                    mesh.TransparencyFirst = TrackFirstFixed16(d, to);
                    mesh.TransparencyKeys = TrackKeyCount(d, to);
                }
            }
            result.Add(mesh);
        }
        return result;
    }

    // ── patch ────────────────────────────────────────────────────────────────

    /// <summary>Zero the submesh's triangle count in every view. In-place; false when the
    /// header or the submesh index is unusable.</summary>
    public static bool HideSubmesh(byte[] d, int submeshIndex)
    {
        if (!IsVanilla(d) || submeshIndex < 0) return false;
        var (nViews, ofsViews) = Array(d, HEADER_VIEWS);
        if (!Fits(d, ofsViews, nViews, VIEW_SIZE)) return false;
        bool any = false;
        for (uint v = 0; v < nViews; v++)
        {
            long vo = ofsViews + v * VIEW_SIZE;
            uint nSub = U32(d, vo + 24), ofsSub = U32(d, vo + 28);
            if (!Fits(d, ofsSub, nSub, SUBMESH_SIZE) || submeshIndex >= nSub) continue;
            W16(d, ofsSub + (long)submeshIndex * SUBMESH_SIZE + 10, 0);
            any = true;
        }
        return any;
    }

    /// <summary>Apply one layer's edits. Material and texture-lookup changes append PRIVATE
    /// entries and repoint the batch (in every view that carries a batch at this index on the
    /// same submesh); colour, alpha and transparency write first keys in place. Returns the
    /// possibly-grown file; the input is returned untouched on a header failure.</summary>
    public static byte[] ApplyMeshPatch(byte[] d, MeshPatch p)
    {
        if (!IsVanilla(d) || p.IsEmpty) return d;
        var snapshot = ReadMeshes(d).FirstOrDefault(m => m.BatchIndex == p.BatchIndex);
        if (snapshot is null) return d;

        if (p.Blend is not null || p.MaterialFlags is not null)
        {
            ushort flags = p.MaterialFlags ?? snapshot.MaterialFlags;
            ushort blend = p.Blend ?? snapshot.Blend;
            if (AppendMaterial(d, flags, blend) is { } grown)
            {
                d = grown.Data;
                RepointBatches(d, p.BatchIndex, snapshot.SubmeshIndex, 10, (ushort)grown.MaterialIndex);
            }
        }
        if (p.TextureSlot is { } slot && slot >= 0)
        {
            var element = new byte[2];
            W16(element, 0, (ushort)slot);
            if (AppendToArray(d, HEADER_TEXTURE_LOOKUP, 2, element) is { } grown)
            {
                d = grown.Data;
                RepointBatches(d, p.BatchIndex, snapshot.SubmeshIndex, 16, (ushort)grown.NewIndex);
            }
        }
        if ((p.Color is not null || p.Alpha is not null) && snapshot.ColorIndex >= 0)
        {
            var (nCol, ofsCol) = Array(d, HEADER_COLORS);
            if (Fits(d, ofsCol, nCol, COLOR_SIZE) && snapshot.ColorIndex < nCol)
            {
                long co = ofsCol + (long)snapshot.ColorIndex * COLOR_SIZE;
                if (p.Color is { } color) WriteTrackFirstVec3(d, co, color);
                if (p.Alpha is { } alpha) WriteTrackFirstFixed16(d, co + TRACK_SIZE, alpha);
            }
        }
        if (p.Transparency is { } transparency && snapshot.TransparencyIndex >= 0)
        {
            var (nTr, ofsTr) = Array(d, HEADER_TRANSPARENCY);
            if (Fits(d, ofsTr, nTr, TRACK_SIZE) && snapshot.TransparencyIndex < nTr)
                WriteTrackFirstFixed16(d, ofsTr + (long)snapshot.TransparencyIndex * TRACK_SIZE, transparency);
        }
        return d;
    }

    /// <summary>Append a copy of one mesh layer to view 0: the view's batch array is relocated
    /// to EOF with the clone appended (the view record's count/offset repointed) and the clone
    /// gets a PRIVATE material. It draws the same submesh with its own image/blend/flags.
    /// LOD views (1+) are left as authored. Returns the grown file and the clone's index.</summary>
    public static (byte[] Data, int NewIndex)? CloneLayer(byte[] d, int batchIndex)
    {
        if (!IsVanilla(d)) return null;
        var (nViews, ofsViews) = Array(d, HEADER_VIEWS);
        if (nViews == 0 || ofsViews == 0 || ofsViews + VIEW_SIZE > d.Length) return null;
        uint nBatch = U32(d, ofsViews + 32), ofsBatch = U32(d, ofsViews + 36);
        if (batchIndex < 0 || batchIndex >= nBatch || nBatch >= 4095 || !Fits(d, ofsBatch, nBatch, BATCH_SIZE)) return null;
        var clone = new byte[BATCH_SIZE];
        System.Array.Copy(d, ofsBatch + (long)batchIndex * BATCH_SIZE, clone, 0, BATCH_SIZE);
        MeshSnapshot? source = ReadMeshes(d).FirstOrDefault(m => m.BatchIndex == batchIndex);
        if (source is not null && AppendMaterial(d, source.MaterialFlags, source.Blend) is { } material)
        {
            d = material.Data;
            W16(clone, 10, (ushort)material.MaterialIndex);
        }
        int arrayOfs = (d.Length + 15) & ~15;
        var result = new byte[arrayOfs + (int)(nBatch + 1) * BATCH_SIZE];
        System.Array.Copy(d, result, d.Length);
        System.Array.Copy(d, ofsBatch, result, arrayOfs, (long)nBatch * BATCH_SIZE);
        System.Array.Copy(clone, 0, result, arrayOfs + (long)nBatch * BATCH_SIZE, BATCH_SIZE);
        W32(result, ofsViews + 32, nBatch + 1);
        W32(result, ofsViews + 36, (uint)arrayOfs);
        return (result, (int)nBatch);
    }

    /// <summary>Write a u16 batch field (at <paramref name="fieldOffset"/> inside the 24-byte
    /// record) for the batch at <paramref name="batchIndex"/> in every view whose batch there
    /// sits on the same submesh - LOD views mirror view 0's layer order when they carry it.</summary>
    private static void RepointBatches(byte[] d, int batchIndex, int submeshIndex, int fieldOffset, ushort value)
    {
        var (nViews, ofsViews) = Array(d, HEADER_VIEWS);
        if (!Fits(d, ofsViews, nViews, VIEW_SIZE)) return;
        for (uint v = 0; v < nViews; v++)
        {
            long vo = ofsViews + v * VIEW_SIZE;
            uint nBatch = U32(d, vo + 32), ofsBatch = U32(d, vo + 36);
            if (!Fits(d, ofsBatch, nBatch, BATCH_SIZE) || batchIndex >= nBatch) continue;
            long bo = ofsBatch + (long)batchIndex * BATCH_SIZE;
            if (v > 0 && U16(d, bo + 4) != submeshIndex) continue;
            W16(d, bo + fieldOffset, value);
        }
    }
}
