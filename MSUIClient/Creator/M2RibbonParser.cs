using System.Numerics;

namespace MSUIClient.Creator;

// ═══════════════════════════════════════════════════════════════════════════
// M2 RIBBON EMITTER reader/patcher for vanilla v256 M2s (shared_docs/SPELL_CREATOR_IDE.md §2.5).
//
// Ribbons are the weapon-trail / slash streaks: a strip of edges committed over time behind
// a bone, each edge a vertical segment from -heightBelow to +heightAbove through the bone's
// position. Verified v256 layout (M2Reader.ParseRibbonEmitters, 0xDC bytes per record):
//
//   +0x04 bone (u32, read as u16)   +0x08 position raw WoW x,y,z
//   +0x14 nTextures/+0x18 ofs (u16 entries: texture TABLE slots)
//   +0x1C nMaterials/+0x20 ofs (u16 entries: render-flag indices)
//   +0x24 colour track (vec3)   +0x40 alpha track (fixed16)
//   +0x5C height-above track    +0x78 height-below track (floats)
//   +0x94 edgesPerSecond  +0x98 edgeLifetime  +0x9C gravity   +0xA0 rows u16  +0xA2 cols u16
//   +0xA4 texture-slot track (u16)   +0xC0 visibility track (u8)
//
// PATCH LAW: plain fields write in place; tracks write their FIRST key; the material goes
// through a private appended render-flag record (M2MeshParser.AppendMaterial) so a blend
// change never leaks to a layer sharing the material. "Off" is wholesale: zero edges per
// second (nothing is ever committed), every visibility key zero and every alpha key zero.
// ═══════════════════════════════════════════════════════════════════════════

public sealed class RibbonSnapshot
{
    public int Index { get; set; }
    public ushort Bone { get; set; }
    public float PositionX { get; set; }
    public float PositionY { get; set; }
    public float PositionZ { get; set; }
    public int TextureSlot { get; set; } = -1;
    public int MaterialIndex { get; set; } = -1;
    public ushort Blend { get; set; }
    public ushort MaterialFlags { get; set; }
    public float EdgesPerSecond { get; set; }
    public float EdgeLifetime { get; set; }
    public float Gravity { get; set; }
    public ushort TextureRows { get; set; }
    public ushort TextureColumns { get; set; }
    public float? HeightAboveFirst { get; set; }
    public int HeightAboveKeys { get; set; }
    public float? HeightBelowFirst { get; set; }
    public int HeightBelowKeys { get; set; }
    public Vector3? ColorFirst { get; set; }
    public int ColorKeys { get; set; }
    public float? AlphaFirst { get; set; }
    public int AlphaKeys { get; set; }
    public int VisibilityKeys { get; set; }
    /// <summary>No edges are ever committed in these bytes (authored dead, or silenced by us).</summary>
    public bool Silenced => EdgesPerSecond <= 0f;
}

/// <summary>Absolute edits for one ribbon; null = leave authored.</summary>
public sealed class RibbonPatch
{
    public int RibbonIndex { get; set; }
    public ushort? Blend { get; set; }
    public ushort? MaterialFlags { get; set; }
    public int? TextureSlot { get; set; }
    public ushort? Bone { get; set; }
    public float? PositionX { get; set; }
    public float? PositionY { get; set; }
    public float? PositionZ { get; set; }
    public float? EdgesPerSecond { get; set; }
    public float? EdgeLifetime { get; set; }
    public float? Gravity { get; set; }
    public ushort? TextureRows { get; set; }
    public ushort? TextureColumns { get; set; }
    public float? HeightAbove { get; set; }
    public float? HeightBelow { get; set; }
    public Vector3? Color { get; set; }
    public float? Alpha { get; set; }

    public bool IsEmpty => Blend is null && MaterialFlags is null && TextureSlot is null && Bone is null &&
                           PositionX is null && PositionY is null && PositionZ is null &&
                           EdgesPerSecond is null && EdgeLifetime is null && Gravity is null &&
                           TextureRows is null && TextureColumns is null &&
                           HeightAbove is null && HeightBelow is null && Color is null && Alpha is null;
}

public static class M2RibbonParser
{
    public const int HEADER_RIBBONS = 0x134;
    public const int RIBBON_SIZE = 0xDC;
    private const int REL_BONE = 0x04, REL_POSITION = 0x08;
    private const int REL_N_TEXTURES = 0x14, REL_OFS_TEXTURES = 0x18;
    private const int REL_N_MATERIALS = 0x1C, REL_OFS_MATERIALS = 0x20;
    private const int REL_COLOR = 0x24, REL_ALPHA = 0x40, REL_HEIGHT_ABOVE = 0x5C, REL_HEIGHT_BELOW = 0x78;
    private const int REL_EDGES_PER_SECOND = 0x94, REL_EDGE_LIFETIME = 0x98, REL_GRAVITY = 0x9C;
    private const int REL_ROWS = 0xA0, REL_COLS = 0xA2, REL_VISIBILITY = 0xC0;

    private static bool TryBase(byte[] d, int index, out long o)
    {
        o = 0;
        if (!M2MeshParser.IsVanilla(d) || index < 0) return false;
        var (n, ofs) = M2MeshParser.Array(d, HEADER_RIBBONS);
        if (index >= n || !M2MeshParser.Fits(d, ofs, n, RIBBON_SIZE)) return false;
        o = ofs + (long)index * RIBBON_SIZE;
        return true;
    }

    public static List<RibbonSnapshot> ReadRibbons(byte[] d)
    {
        var result = new List<RibbonSnapshot>();
        if (!M2MeshParser.IsVanilla(d)) return result;
        var (n, ofs) = M2MeshParser.Array(d, HEADER_RIBBONS);
        if (n == 0 || !M2MeshParser.Fits(d, ofs, n, RIBBON_SIZE)) return result;
        var (nRf, ofsRf) = M2MeshParser.Array(d, M2MeshParser.HEADER_RENDER_FLAGS);
        bool rfOk = M2MeshParser.Fits(d, ofsRf, nRf, M2MeshParser.RENDER_FLAG_SIZE);

        for (int i = 0; i < n; i++)
        {
            long o = ofs + (long)i * RIBBON_SIZE;
            var r = new RibbonSnapshot
            {
                Index = i,
                Bone = M2MeshParser.U16(d, o + REL_BONE),
                PositionX = M2MeshParser.F32(d, o + REL_POSITION),
                PositionY = M2MeshParser.F32(d, o + REL_POSITION + 4),
                PositionZ = M2MeshParser.F32(d, o + REL_POSITION + 8),
                EdgesPerSecond = M2MeshParser.F32(d, o + REL_EDGES_PER_SECOND),
                EdgeLifetime = M2MeshParser.F32(d, o + REL_EDGE_LIFETIME),
                Gravity = M2MeshParser.F32(d, o + REL_GRAVITY),
                TextureRows = M2MeshParser.U16(d, o + REL_ROWS),
                TextureColumns = M2MeshParser.U16(d, o + REL_COLS),
                HeightAboveFirst = M2MeshParser.TrackFirstFloat(d, o + REL_HEIGHT_ABOVE),
                HeightAboveKeys = M2MeshParser.TrackKeyCount(d, o + REL_HEIGHT_ABOVE),
                HeightBelowFirst = M2MeshParser.TrackFirstFloat(d, o + REL_HEIGHT_BELOW),
                HeightBelowKeys = M2MeshParser.TrackKeyCount(d, o + REL_HEIGHT_BELOW),
                ColorFirst = M2MeshParser.TrackFirstVec3(d, o + REL_COLOR),
                ColorKeys = M2MeshParser.TrackKeyCount(d, o + REL_COLOR),
                AlphaFirst = M2MeshParser.TrackFirstFixed16(d, o + REL_ALPHA),
                AlphaKeys = M2MeshParser.TrackKeyCount(d, o + REL_ALPHA),
                VisibilityKeys = M2MeshParser.TrackKeyCount(d, o + REL_VISIBILITY),
            };
            uint nTex = M2MeshParser.U32(d, o + REL_N_TEXTURES), ofsTex = M2MeshParser.U32(d, o + REL_OFS_TEXTURES);
            if (nTex > 0 && ofsTex > 0 && ofsTex + 2 <= d.Length) r.TextureSlot = M2MeshParser.U16(d, ofsTex);
            uint nMat = M2MeshParser.U32(d, o + REL_N_MATERIALS), ofsMat = M2MeshParser.U32(d, o + REL_OFS_MATERIALS);
            if (nMat > 0 && ofsMat > 0 && ofsMat + 2 <= d.Length)
            {
                r.MaterialIndex = M2MeshParser.U16(d, ofsMat);
                if (rfOk && r.MaterialIndex < nRf)
                {
                    long ro = ofsRf + (long)r.MaterialIndex * M2MeshParser.RENDER_FLAG_SIZE;
                    r.MaterialFlags = M2MeshParser.U16(d, ro);
                    r.Blend = M2MeshParser.U16(d, ro + 2);
                }
            }
            result.Add(r);
        }
        return result;
    }

    /// <summary>Wholesale off: no edges ever commit, every visibility and alpha key zero.</summary>
    public static bool DisableRibbon(byte[] d, int index)
    {
        if (!TryBase(d, index, out long o)) return false;
        M2MeshParser.WF(d, o + REL_EDGES_PER_SECOND, 0f);
        M2MeshParser.WriteTrackAllBytes(d, o + REL_VISIBILITY, 0);
        M2MeshParser.WriteTrackAllFixed16(d, o + REL_ALPHA, 0f);
        return true;
    }

    /// <summary>Append a copy of one ribbon: the array is relocated to EOF with the clone
    /// appended (header 0x134 repointed) and the clone gets PRIVATE copies of its texture and
    /// material index arrays and of every track's timestamps and keys, so editing it never
    /// writes through to the source. Returns the grown file and the clone's index.</summary>
    public static (byte[] Data, int NewIndex)? CloneRibbon(byte[] d, int sourceIndex)
    {
        if (!TryBase(d, sourceIndex, out long src)) return null;
        var (n, ofs) = M2MeshParser.Array(d, HEADER_RIBBONS);
        if (n >= 255) return null;
        int arrayOfs = (d.Length + 15) & ~15;
        int privateOfs = arrayOfs + (int)(n + 1) * RIBBON_SIZE;
        var clone = new byte[RIBBON_SIZE];
        Array.Copy(d, src, clone, 0, RIBBON_SIZE);
        var extra = new List<byte>();
        void Privatize(int countRel, int ofsRel, int elementSize)
        {
            uint count = M2MeshParser.U32(clone, countRel), at = M2MeshParser.U32(clone, ofsRel);
            if (count == 0 || count > 65535 || at == 0 || at + (long)count * elementSize > d.Length) return;
            int dst = privateOfs + extra.Count;
            for (long b = 0; b < (long)count * elementSize; b++) extra.Add(d[at + b]);
            while (extra.Count % 4 != 0) extra.Add(0);
            M2MeshParser.W32(clone, ofsRel, (uint)dst);
        }
        Privatize(REL_N_TEXTURES, REL_OFS_TEXTURES, 2);
        Privatize(REL_N_MATERIALS, REL_OFS_MATERIALS, 2);
        foreach ((int rel, int keySize) in new[]
                 {
                     (REL_COLOR, 12), (REL_ALPHA, 2), (REL_HEIGHT_ABOVE, 4), (REL_HEIGHT_BELOW, 4),
                     (0xA4, 2), (REL_VISIBILITY, 1),
                 })
        {
            Privatize(rel + 12, rel + 16, 4);      // timestamps
            Privatize(rel + 20, rel + 24, keySize); // keys
        }
        var result = new byte[privateOfs + extra.Count];
        Array.Copy(d, result, d.Length);
        Array.Copy(d, ofs, result, arrayOfs, (long)n * RIBBON_SIZE);
        Array.Copy(clone, 0, result, arrayOfs + (long)n * RIBBON_SIZE, RIBBON_SIZE);
        extra.CopyTo(result, privateOfs);
        M2MeshParser.W32(result, HEADER_RIBBONS, n + 1);
        M2MeshParser.W32(result, HEADER_RIBBONS + 4, (uint)arrayOfs);
        return (result, (int)n);
    }

    /// <summary>Apply one ribbon's edits: fields in place, first keys, a private material.
    /// Returns the possibly-grown file.</summary>
    public static byte[] ApplyRibbonPatch(byte[] d, RibbonPatch p)
    {
        if (p.IsEmpty || !TryBase(d, p.RibbonIndex, out long o)) return d;
        var snapshot = ReadRibbons(d).FirstOrDefault(r => r.Index == p.RibbonIndex);
        if (snapshot is null) return d;

        if (p.Blend is not null || p.MaterialFlags is not null)
        {
            uint nMat = M2MeshParser.U32(d, o + REL_N_MATERIALS), ofsMat = M2MeshParser.U32(d, o + REL_OFS_MATERIALS);
            if (nMat > 0 && ofsMat > 0 && ofsMat + 2 <= d.Length &&
                M2MeshParser.AppendMaterial(d, p.MaterialFlags ?? snapshot.MaterialFlags,
                    p.Blend ?? snapshot.Blend) is { } grown)
            {
                d = grown.Data;
                M2MeshParser.W16(d, ofsMat, (ushort)grown.MaterialIndex);   // record offsets are unchanged
            }
        }
        if (p.TextureSlot is { } slot && slot >= 0)
        {
            uint nTex = M2MeshParser.U32(d, o + REL_N_TEXTURES), ofsTex = M2MeshParser.U32(d, o + REL_OFS_TEXTURES);
            if (nTex > 0 && ofsTex > 0 && ofsTex + 2 <= d.Length) M2MeshParser.W16(d, ofsTex, (ushort)slot);
        }
        if (p.Bone is { } bone) M2MeshParser.W32(d, o + REL_BONE, bone);
        if (p.PositionX is { } x) M2MeshParser.WF(d, o + REL_POSITION, x);
        if (p.PositionY is { } y) M2MeshParser.WF(d, o + REL_POSITION + 4, y);
        if (p.PositionZ is { } z) M2MeshParser.WF(d, o + REL_POSITION + 8, z);
        if (p.EdgesPerSecond is { } eps) M2MeshParser.WF(d, o + REL_EDGES_PER_SECOND, MathF.Max(0f, eps));
        if (p.EdgeLifetime is { } life) M2MeshParser.WF(d, o + REL_EDGE_LIFETIME, MathF.Max(0f, life));
        if (p.Gravity is { } gravity) M2MeshParser.WF(d, o + REL_GRAVITY, gravity);
        if (p.TextureRows is { } rows) M2MeshParser.W16(d, o + REL_ROWS, Math.Max((ushort)1, rows));
        if (p.TextureColumns is { } cols) M2MeshParser.W16(d, o + REL_COLS, Math.Max((ushort)1, cols));
        if (p.HeightAbove is { } above) M2MeshParser.WriteTrackFirstFloat(d, o + REL_HEIGHT_ABOVE, MathF.Max(0f, above));
        if (p.HeightBelow is { } below) M2MeshParser.WriteTrackFirstFloat(d, o + REL_HEIGHT_BELOW, MathF.Max(0f, below));
        if (p.Color is { } color) M2MeshParser.WriteTrackFirstVec3(d, o + REL_COLOR, color);
        if (p.Alpha is { } alpha) M2MeshParser.WriteTrackFirstFixed16(d, o + REL_ALPHA, alpha);
        return d;
    }
}
