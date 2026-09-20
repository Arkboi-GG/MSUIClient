using System.Numerics;
using System.Text;

namespace MSUIClient.Creator.Sketch;

// ═══════════════════════════════════════════════════════════════════════════
// SKETCH → M2 — shared_docs/SPELL_SKETCH.md §4.2 / §4.3 / §4.4 / §4.6.
//
// Writes a real vanilla M2 (MD20, version 256) from a SketchDoc. This is the
// only genuinely new engineering in the Sketch feature: a WRITER for a format
// the repo already reads, validates and patches in two places.
//
// The contract is the READERS, not a wiki page. Every stride and rule below was
// taken out of MSUIClient/Formats/M2Reader.cs and the four Creator/M2*Parser
// files, and the emitter lab re-checks them on the bytes this produces:
//
//   * M2Array is {u32 count; u32 offset}, offset ABSOLUTE from byte 0. An absent
//     array is count=0 AND offset=0 — count>0 with offset 0 is read as absent.
//   * Header runs to 0x144; the reader's first guard needs >= 0x110 bytes, and
//     an out-of-bounds read returns 0 rather than throwing, so a short header
//     silently loses its tail arrays. All 324 bytes are always written.
//   * Vanilla animation blocks are FLAT (28 bytes): {u16 interp, i16 globalSeq,
//     M2Array ranges, M2Array timestamps, M2Array keys}. ONE timestamp list and
//     ONE key list for the whole model, with a ranges entry per sequence giving
//     [first, last] — the nested array-of-arrays shape is WotLK+ and misparses.
//   * AnimationRange.End is INCLUSIVE, timestamps are ABSOLUTE ms on the model
//     timeline and must land inside the owning sequence's window, and
//     nTimestamps MUST equal nKeys or the reader silently wipes the track.
//   * globalSequence must be -1 (0xFFFF); a 0 there means "global sequence 0"
//     and the bone reads as static.
//   * Rotation keys are FOUR RAW FLOATS (16 B/key). Packed quaternions are TBC+.
//   * Strides: vertex 48, bone 108, sequence 68, colour 56, texture 16,
//     transparency 28, render flag 4, view header 44, submesh 32, batch 24.
//   * A texture's filename count is strlen + 1 (the count must be > 1).
//   * View triangles index the view's LOCAL vertex lookup, not the global vertex
//     array; an out-of-range entry is silently rewritten to vertex 0. We write an
//     identity lookup, which is the safe shape.
//
// RAW COORDINATES. Everything here is the FILE's frame — x forward, y left,
// z up. M2Reader swaps to (x, z, -y) on the way in; SketchWriter must never
// write what a reader handed back. The lab's swap checks are the tripwire.
//
// LAW (CODE_STRUCTURE_LAW §1): this namespace never references GameLoop.
// ═══════════════════════════════════════════════════════════════════════════
public static class SketchWriter
{
    // ── Strides, straight out of the readers ────────────────────────────────
    private const int VertexStride = 48;
    private const int BoneStride = 108;
    private const int SequenceStride = 68;
    private const int TrackStride = 28;
    private const int ColourStride = 56;
    private const int TextureStride = 16;
    private const int TransparencyStride = 28;
    private const int RenderFlagStride = 4;
    private const int ViewHeaderStride = 44;
    private const int SubmeshStride = 32;
    private const int BatchStride = 24;

    /// <summary>The header runs to 0x144; the payload starts on the next 16-byte line.</summary>
    private const int HeaderBytes = 0x144;
    private const int PayloadStart = 0x150;

    // Header field offsets (M2Reader.cs:1546-1578 and the eight past its "...").
    private const int HdrName = 0x008;
    private const int HdrGlobalFlags = 0x010;
    private const int HdrGlobalSequences = 0x014;
    private const int HdrSequences = 0x01C;
    private const int HdrBones = 0x034;
    private const int HdrKeyBoneLookup = 0x03C;
    private const int HdrVertices = 0x044;
    private const int HdrNViews = 0x04C;
    private const int HdrOfsViews = 0x050;
    private const int HdrColours = 0x054;
    private const int HdrTextures = 0x05C;
    private const int HdrTransparency = 0x064;
    private const int HdrRenderFlags = 0x084;
    private const int HdrBoneLookup = 0x08C;
    private const int HdrTextureLookup = 0x094;
    private const int HdrTextureUnits = 0x09C;
    private const int HdrTransparencyLookup = 0x0A4;
    private const int HdrUvAnimationLookup = 0x0AC;
    private const int HdrBoundingBox = 0x0B4;
    private const int HdrBoundingRadius = 0x0CC;
    private const int HdrCollisionBox = 0x0D0;
    private const int HdrCollisionRadius = 0x0E8;

    /// <summary>fixed16 alpha: 32767 is fully opaque, NOT 65535 (M2Reader ConstantFixed16).</summary>
    private const short AlphaOpaque = 32767;

    /// <summary>Linear interpolation. Anything else makes every sampler hold the previous key.</summary>
    private const ushort InterpLinear = 1;

    /// <summary>Spherical billboard (M2 bone flag), inside the 0x78 billboard mask.</summary>
    public const uint BoneFlagSphericalBillboard = 0x08;

    /// <summary>Rotation keys get one key per 45 degrees of spin, per §4.3 ("8 per turn").</summary>
    private const float SpinDegreesPerKey = 45f;

    /// <summary>How many pieces one Sketch may hold.
    ///
    /// Two independent ceilings, and this sits under the lower of them: an M2 vertex stores
    /// its bone indices as BYTES, so bone 256 would silently wrap to bone 0 and the piece
    /// would ride the root instead of its own bone; and the skinning palette
    /// (M2Animator.MaxBones) is 160. A Sketch is a handful of painted planes, so the real
    /// limit is far below either - but "far below" is not a guarantee, and a silent wrap is
    /// the worst way to find that out. The window gates its shape shelf on this.</summary>
    public const int MaxPieces = 128;

    // ── Result ──────────────────────────────────────────────────────────────

    public sealed record SketchTextureFile(string Path, byte[] Blp);

    public sealed record SketchBuild(
        byte[] Model,
        string ModelPath,
        IReadOnlyList<SketchTextureFile> Textures,
        int PieceCount,
        int BoneCount,
        int VertexCount,
        int IndexCount,
        int BatchCount,
        float LengthSeconds);

    // ── Entry point ─────────────────────────────────────────────────────────

    /// <summary>Compile a Sketch to M2 bytes plus one BLP per piece. Returns null for a
    /// doc with no pieces: an M2 with no geometry and no emitters is rejected by
    /// M2Reader.HasRenderableContent, and handing those bytes on would be a lie.</summary>
    /// <param name="loopForEditing">Emit a LOOPING sequence instead of a one-shot.
    ///
    /// A spell effect plays once and stops, which is right in the game and wrong in an editor:
    /// the piece fades to nothing after half a second and sits there invisible, so there is
    /// nothing on screen to grab a handle on. Keeping the instance alive is not enough - the
    /// FADE is what hides it, not the lifetime. A looping sequence replays instead, so the
    /// piece is there whenever you reach for it. Never used for the bytes that get exported.</param>
    public static SketchBuild? Build(SketchDoc doc, int spellId, string spellName,
        BlpWriterService blp, SketchTextureCache? cache = null, bool loopForEditing = false)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(blp);
        // Both refusals return null rather than throwing: Build sits on the live-edit path,
        // where a card change must never be able to take the workshop down.
        if (doc.Pieces.Count == 0) return null;
        if (doc.Pieces.Count > MaxPieces) return null;

        var textures = new List<SketchTextureFile>(doc.Pieces.Count);
        foreach (SketchPiece piece in doc.Pieces)
        {
            byte[]? bytes = cache is null
                ? SketchTextures.BuildBlp(piece, blp)
                : cache.Get(piece, blp);
            if (bytes == null) return null;      // a piece with no texture is not a piece
            textures.Add(new SketchTextureFile(
                SketchDoc.TexturePath(spellId, spellName, doc.Stage, piece.Name), bytes));
        }

        byte[] model = WriteModel(doc, textures, loopForEditing, out Stats stats);
        return new SketchBuild(
            model,
            SketchDoc.ModelPath(spellId, spellName, doc.Stage),
            textures,
            doc.Pieces.Count,
            stats.Bones,
            stats.Vertices,
            stats.Indices,
            stats.Batches,
            doc.Length);
    }

    private record struct Stats(int Bones, int Vertices, int Indices, int Batches);

    /// <summary>Reuse a piece's rasterized BLP across recompiles while its SHAPE is unchanged.
    ///
    /// §4.7 wants a recompile on every card change, under about 5 ms. Rasterizing a signed
    /// distance field, blurring it for the glow and palettizing the result is most of that
    /// cost, and almost every card change - colour, alpha, blend, placement, travel, spin,
    /// grow, fade - cannot alter a single pixel. So the IDE holds one of these for the
    /// session and drags a motion dial for free; the raster only reruns when the shape or
    /// the glow weight actually moves.
    ///
    /// Not thread-safe: it belongs to whoever is editing, and there is one of those.</summary>
    public sealed class SketchTextureCache
    {
        private readonly Dictionary<string, byte[]> _byShape = new(StringComparer.Ordinal);

        public int Hits { get; private set; }
        public int Misses { get; private set; }

        public byte[]? Get(SketchPiece piece, BlpWriterService blp)
        {
            string key = SketchTextures.CacheKey(piece);
            if (_byShape.TryGetValue(key, out byte[]? cached)) { Hits++; return cached; }

            Misses++;
            byte[]? bytes = SketchTextures.BuildBlp(piece, blp);
            if (bytes != null) _byShape[key] = bytes;
            return bytes;
        }

        /// <summary>Drop everything - a new spell, or a deliberate "re-render it all".</summary>
        public void Clear()
        {
            _byShape.Clear();
            Hits = 0;
            Misses = 0;
        }
    }

    // ── The file ────────────────────────────────────────────────────────────

    private static byte[] WriteModel(SketchDoc doc, List<SketchTextureFile> textures,
        bool loopForEditing, out Stats stats)
    {
        float length = doc.Length;
        uint lengthMs = (uint)MathF.Max(MathF.Round(length * 1000f), 2f);

        // A "quad" is one drawn plane: every piece has one, and a piece with a glow
        // has a second, larger one on the SAME bone (§4.2 — the glow must travel with
        // the piece, and sharing the bone is what guarantees it).
        var quads = new List<Quad>();
        for (int i = 0; i < doc.Pieces.Count; i++)
        {
            SketchPiece piece = doc.Pieces[i];
            int bone = i + 1;                                  // bone 0 is the static root
            quads.Add(new Quad(piece, bone, TextureIndex: i, Glow: false));
            if (piece.Extras.Glow.Radius > 0.001f)
                quads.Add(new Quad(piece, bone, TextureIndex: i, Glow: true));
        }

        var b = new Blob();
        b.Zero(PayloadStart);                                  // header + its 16-byte pad
        ExpectStride(b, 0, 1, PayloadStart, "header");

        // ── name ────────────────────────────────────────────────────────────
        byte[] name = Encoding.ASCII.GetBytes($"sketch_{SketchDoc.Sanitize(doc.Stage)}\0");
        int nameOffset = b.Append(name);
        b.PatchArray(HdrName, name.Length, nameOffset);

        // ── sequences: exactly one, id 0, not looping ───────────────────────
        // Flags bit 0 SET means "play once and clamp" (M2Sequence.IsLooping is
        // (Flags & 1) == 0). SpellAttachment.SelfTerminatingSpan reads End - Start
        // off THIS record, so the Sketch's Length is also the effect's life.
        (Vector3 min, Vector3 max) bounds = ComputeBounds(doc);
        int sequenceOffset = b.Mark();
        b.U16(0); b.U16(0);                                    // id, variationId
        b.U32(0); b.U32(lengthMs);                             // start, end (absolute ms)
        b.F32(0f);                                             // movespeed
        b.U32(loopForEditing ? 0u : 1u);                       // bit 0 SET = play once and stop
        b.U16(0); b.U16(0);                                    // frequency, padding
        b.U32(0); b.U32(0);                                    // min/max repetitions
        b.U32(0);                                              // blendTime
        b.Vec3(bounds.min); b.Vec3(bounds.max);
        b.F32(BoundsRadius(bounds));
        b.I16(-1); b.U16(0);                                   // next variation, alias
        ExpectStride(b, sequenceOffset, 1, SequenceStride, "sequence");
        b.PatchArray(HdrSequences, 1, sequenceOffset);

        // ── bones ───────────────────────────────────────────────────────────
        // Every bone's three tracks are written FIRST so the 108-byte records can
        // embed real offsets; the records then go out contiguously.
        int boneCount = doc.Pieces.Count + 1;
        var boneRecords = new BoneRecord[boneCount];

        boneRecords[0] = new BoneRecord(
            Flags: 0, Parent: -1, Pivot: Vector3.Zero,
            Translation: TrackRef.Absent, Rotation: TrackRef.Absent, Scale: TrackRef.Absent);

        for (int i = 0; i < doc.Pieces.Count; i++)
        {
            SketchPiece piece = doc.Pieces[i];
            boneRecords[i + 1] = new BoneRecord(
                Flags: piece.Place.Facing == SketchFacing.FaceCamera ? BoneFlagSphericalBillboard : 0u,
                Parent: 0,
                Pivot: piece.Place.Origin,
                Translation: WriteTranslationTrack(b, piece, lengthMs),
                Rotation: WriteRotationTrack(b, piece, lengthMs),
                Scale: WriteScaleTrack(b, piece, lengthMs));
        }

        int boneArrayOffset = b.Mark();
        foreach (BoneRecord bone in boneRecords)
        {
            b.I32(-1);                                         // keyBoneId: none
            b.U32(bone.Flags);
            b.I16(bone.Parent);
            b.U16(0);                                          // submeshId
            b.Track(bone.Translation);
            b.Track(bone.Rotation);
            b.Track(bone.Scale);
            b.Vec3(bone.Pivot);                                // raw frame, Z up
        }
        ExpectStride(b, boneArrayOffset, boneCount, BoneStride, "bone");
        b.PatchArray(HdrBones, boneCount, boneArrayOffset);
        b.PatchArray(HdrKeyBoneLookup, 0, 0);

        // ── vertices: four per quad ─────────────────────────────────────────
        int vertexOffset = b.Mark();
        int vertexCount = 0;
        foreach (Quad quad in quads)
        {
            foreach (Vertex v in QuadVertices(quad))
            {
                b.Vec3(v.Position);
                b.U8(255); b.U8(0); b.U8(0); b.U8(0);          // weights: fully on its own bone
                b.U8((byte)quad.Bone); b.U8(0); b.U8(0); b.U8(0);
                b.Vec3(v.Normal);
                b.F32(v.U); b.F32(v.V);
                b.F32(0f); b.F32(0f);                          // uv2, unused
                vertexCount++;
            }
        }
        ExpectStride(b, vertexOffset, vertexCount, VertexStride, "vertex");
        b.PatchArray(HdrVertices, vertexCount, vertexOffset);

        // ── view 0 ──────────────────────────────────────────────────────────
        // Identity local lookup: triangle entries index THIS array, and anything out
        // of range would be silently rewritten to vertex 0.
        int lookupOffset = b.Mark();
        for (int i = 0; i < vertexCount; i++) b.U16((ushort)i);

        int triangleOffset = b.Mark();
        int indexCount = 0;
        for (int q = 0; q < quads.Count; q++)
        {
            int v0 = q * 4;
            // Corner order from QuadVertices: 0 = top-left, 1 = top-right,
            // 2 = bottom-left, 3 = bottom-right. Two-sided material, so winding
            // never decides visibility.
            foreach (int idx in new[] { v0 + 0, v0 + 1, v0 + 2, v0 + 2, v0 + 1, v0 + 3 })
            {
                b.U16((ushort)idx);
                indexCount++;
            }
        }

        int submeshOffset = b.Mark();
        for (int q = 0; q < quads.Count; q++)
        {
            Quad quad = quads[q];
            b.U16((ushort)q);                                  // id
            b.U16(0);                                          // level
            b.U16((ushort)(q * 4)); b.U16(4);                  // vertexStart, vertexCount
            b.U16((ushort)(q * 6)); b.U16(6);                  // indexStart, indexCount
            b.U16(1);                                          // boneCount
            b.U16((ushort)quad.Bone);                          // boneComboIndex into boneLookup
            b.U16(1);                                          // boneInfluences
            b.U16((ushort)quad.Bone);                          // centerBoneIndex
            b.Vec3(quad.Piece.Place.Origin);                   // centerPosition
        }

        ExpectStride(b, submeshOffset, quads.Count, SubmeshStride, "submesh");

        int batchOffset = b.Mark();
        for (int q = 0; q < quads.Count; q++)
        {
            b.U8(0);                                           // flags
            b.I8(0);                                           // priorityPlane
            b.U16(0);                                          // shaderId
            b.U16((ushort)q);                                  // submeshIndex
            b.U16((ushort)q);                                  // geosetIndex
            b.I16((short)q);                                   // colorIndex -> colors[q]
            b.U16((ushort)q);                                  // materialIndex -> renderFlags[q]
            b.U16(0);                                          // materialLayer
            b.U16(1);                                          // textureCount
            b.U16((ushort)q);                                  // textureIndex -> textureLookup[q]
            b.U16((ushort)q);                                  // texCoordCombo -> textureUnits[q]
            b.U16(0);                                          // weightCombo -> transparencyLookup[0]
            b.U16(0);                                          // transformCombo -> uvAnimationLookup[0] = -1
        }

        ExpectStride(b, batchOffset, quads.Count, BatchStride, "batch");

        int viewOffset = b.Mark();
        b.U32((uint)vertexCount); b.U32((uint)lookupOffset);
        b.U32((uint)indexCount); b.U32((uint)triangleOffset);
        b.U32(0); b.U32(0);                                    // vertex properties: none
        b.U32((uint)quads.Count); b.U32((uint)submeshOffset);
        b.U32((uint)quads.Count); b.U32((uint)batchOffset);
        b.U32(1);                                              // bones per vertex / lod
        ExpectStride(b, viewOffset, 1, ViewHeaderStride, "view header");
        b.PatchU32(HdrNViews, 1);
        b.PatchU32(HdrOfsViews, (uint)viewOffset);             // NOT an M2Array: view[0] directly

        // ── colours: one record per batch (colour key + the fade alpha track) ──
        var colourRefs = new (TrackRef Colour, TrackRef Alpha)[quads.Count];
        for (int q = 0; q < quads.Count; q++)
        {
            Quad quad = quads[q];
            Vector3 tint = quad.Glow
                ? quad.Piece.Extras.Glow.Colour ?? quad.Piece.Look.Colour
                : quad.Piece.Look.Colour;
            colourRefs[q] = (WriteColourTrack(b, tint), WriteAlphaTrack(b, quad.Piece, lengthMs));
        }
        int colourOffset = b.Mark();
        foreach ((TrackRef colour, TrackRef alpha) in colourRefs)
        {
            b.Track(colour);
            b.Track(alpha);
        }
        ExpectStride(b, colourOffset, quads.Count, ColourStride, "colour");
        b.PatchArray(HdrColours, quads.Count, colourOffset);

        // ── textures: one per PIECE; a glow quad reuses its piece's image ────
        var filenameOffsets = new (int Offset, int Count)[textures.Count];
        for (int i = 0; i < textures.Count; i++)
        {
            byte[] path = Encoding.ASCII.GetBytes(textures[i].Path + "\0");
            filenameOffsets[i] = (b.Append(path), path.Length);   // count includes the NUL, and must be > 1
        }
        int textureOffset = b.Mark();
        foreach ((int offset, int count) in filenameOffsets)
        {
            b.U32(0);                                          // type 0 = hardcoded filename
            b.U32(0);                                          // flags: no wrap
            b.U32((uint)count); b.U32((uint)offset);
        }
        ExpectStride(b, textureOffset, textures.Count, TextureStride, "texture");
        b.PatchArray(HdrTextures, textures.Count, textureOffset);

        // ── transparency: one record, weight 1, shared by every batch ────────
        TrackRef weight = WriteFixed16Track(b, new uint[] { 0 }, new[] { AlphaOpaque });
        int transparencyOffset = b.Mark();
        b.Track(weight);
        ExpectStride(b, transparencyOffset, 1, TransparencyStride, "transparency");
        b.PatchArray(HdrTransparency, 1, transparencyOffset);

        // ── render flags ────────────────────────────────────────────────────
        int renderFlagOffset = b.Mark();
        foreach (Quad quad in quads)
        {
            ushort flags = 0x04;                               // two-sided: a plane is never backfaced away
            if (quad.Piece.Look.Unlit) flags |= 0x01;
            ushort blend = quad.Glow ? (ushort)4 : BlendMode(quad.Piece.Look.Blend);
            if (blend != 0) flags |= 0x10;                     // no Z-write for anything blended
            b.U16(flags);
            b.U16(blend);
        }
        ExpectStride(b, renderFlagOffset, quads.Count, RenderFlagStride, "render flag");
        b.PatchArray(HdrRenderFlags, quads.Count, renderFlagOffset);

        // ── lookups ─────────────────────────────────────────────────────────
        // boneLookup is not read by M2Reader but the real 1.12 client skins through it.
        int boneLookupOffset = b.Mark();
        for (int i = 0; i < boneCount; i++) b.U16((ushort)i);
        b.PatchArray(HdrBoneLookup, boneCount, boneLookupOffset);

        int textureLookupOffset = b.Mark();
        foreach (Quad quad in quads) b.U16((ushort)quad.TextureIndex);
        b.PatchArray(HdrTextureLookup, quads.Count, textureLookupOffset);

        int textureUnitOffset = b.Mark();
        for (int q = 0; q < quads.Count; q++) b.I16(0);        // UV set 0; > 2 would mean env-mapped
        b.PatchArray(HdrTextureUnits, quads.Count, textureUnitOffset);

        int transparencyLookupOffset = b.Mark();
        b.U16(0);
        b.PatchArray(HdrTransparencyLookup, 1, transparencyLookupOffset);

        int uvAnimationLookupOffset = b.Mark();
        b.I16(-1);                                             // -1 = this batch does not animate its UVs
        b.PatchArray(HdrUvAnimationLookup, 1, uvAnimationLookupOffset);

        // ── header scalars ──────────────────────────────────────────────────
        b.PatchAscii(0, "MD20");
        b.PatchU32(0x004, 256);
        b.PatchU32(HdrGlobalFlags, 0);
        b.PatchArray(HdrGlobalSequences, 0, 0);
        b.PatchBounds(HdrBoundingBox, bounds.min, bounds.max);
        b.PatchF32(HdrBoundingRadius, BoundsRadius(bounds));
        b.PatchBounds(HdrCollisionBox, bounds.min, bounds.max);
        b.PatchF32(HdrCollisionRadius, BoundsRadius(bounds));

        stats = new Stats(boneCount, vertexCount, indexCount, quads.Count);
        return b.ToArray();
    }

    // ── Geometry ────────────────────────────────────────────────────────────

    private readonly record struct Quad(SketchPiece Piece, int Bone, int TextureIndex, bool Glow);

    private readonly record struct Vertex(Vector3 Position, Vector3 Normal, float U, float V);

    /// <summary>The four corners of one quad, in the piece's LOCAL plane.
    ///
    /// §4.2: "A vertical slash facing forward is a quad in the raw XZ plane." So for every
    /// ORDINARY facing the local plane is spanned by raw +X (width, texture u) and raw +Z
    /// (height, texture v upward), with the normal along raw +Y, and the bone's rest
    /// rotation turns that plane wherever the facing asks. Place and Motion never touch
    /// these numbers — only the bone moves.
    ///
    /// A BILLBOARDED piece is the exception, and it has to be, because the billboard
    /// REPLACES the bone's basis instead of rotating it. SpellMeshSkinningLaw's spherical
    /// case builds `bx = -forward; by = right; bz = up` into a matrix whose rows are the
    /// images of the axes — and it is applied to the palette that skins MODEL-space
    /// vertices, which M2Reader already swapped. Translating back through
    /// (model x, y, z) = (raw x, raw z, -raw y), a camera-facing bone maps:
    ///
    ///     raw +X -> -forward   (straight at the camera: the DEPTH axis)
    ///     raw +Y -> right      (screen RIGHT)
    ///     raw +Z -> up         (screen UP)
    ///
    /// A quad in the raw XZ plane therefore has its whole width pointing at the camera and
    /// renders as a vertical line — invisible, with nothing in either reader to complain
    /// about. A camera-facing piece must be authored in the raw YZ plane: width along raw
    /// +Y (screen right, so u grows with +Y) and height along raw +Z (screen up).
    ///
    /// Found by review 2026-09-10. The offline preview could not catch it because rasterizing
    /// through M2Animator alone never applies the billboard — and the first fix put the quad
    /// in the right PLANE with the two axes swapped, which is visible but silently rotated a
    /// quarter turn. That is why the lab now measures the projected WIDTH and HEIGHT of a
    /// deliberately oblong piece, not just that its area is non-zero.
    ///
    /// Vertices are LOCAL to the bone: the bone's pivot carries the piece's Origin, and
    /// vanilla's bone convention is T(pivot) . T(translation) . R . S . T(-pivot), so a
    /// quad centred on its own pivot is what makes rotation and scale act about the
    /// piece's own centre rather than about the caster's feet.</summary>
    private static Vertex[] QuadVertices(Quad quad)
    {
        SketchShape shape = quad.Piece.Shape;
        float halfWidth = MathF.Abs(shape.Width) * 0.5f;
        float halfHeight = MathF.Abs(shape.Height) * 0.5f;
        if (quad.Glow)
        {
            float radius = MathF.Max(quad.Piece.Extras.Glow.Radius, 0f);
            halfWidth += radius;
            halfHeight += radius;
        }

        // The texture's drawn shape occupies only the middle of the image so a soft edge
        // and a glow have somewhere to fall off; grow the quad by the same factor so the
        // SHAPE still measures the width and height the owner typed.
        halfWidth *= SketchTextures.QuadOversize;
        halfHeight *= SketchTextures.QuadOversize;

        // The pivot is the point the piece TURNS ABOUT; the drawing sits Offset away from it.
        // With a zero offset the two coincide and the piece turns in place; with an offset it
        // sweeps, which is how Cleave's crescents are built.
        Vector3 pivot = quad.Piece.Place.Origin + quad.Piece.Place.Offset;

        // Mirrors are UV swaps, never a negative scale: a negative scale key flips triangle
        // winding and has to survive Matrix4x4.Decompose in the billboard path, and neither is
        // worth risking when swapping two floats does the same job exactly.
        float u0 = quad.Piece.Place.MirrorX ? 1f : 0f, u1 = 1f - u0;
        float v0 = quad.Piece.Place.MirrorY ? 1f : 0f, v1 = 1f - v0;

        if (quad.Piece.Place.Facing == SketchFacing.FaceCamera)
        {
            var billboardNormal = new Vector3(1f, 0f, 0f);      // raw +X points at the camera
            return new[]
            {
                new Vertex(pivot + new Vector3(0f, -halfWidth, +halfHeight), billboardNormal, u0, v0),
                new Vertex(pivot + new Vector3(0f, +halfWidth, +halfHeight), billboardNormal, u1, v0),
                new Vertex(pivot + new Vector3(0f, -halfWidth, -halfHeight), billboardNormal, u0, v1),
                new Vertex(pivot + new Vector3(0f, +halfWidth, -halfHeight), billboardNormal, u1, v1),
            };
        }

        var normal = new Vector3(0f, 1f, 0f);
        return new[]
        {
            new Vertex(pivot + new Vector3(-halfWidth, 0f, +halfHeight), normal, u0, v0),  // top-left
            new Vertex(pivot + new Vector3(+halfWidth, 0f, +halfHeight), normal, u1, v0),  // top-right
            new Vertex(pivot + new Vector3(-halfWidth, 0f, -halfHeight), normal, u0, v1),  // bottom-left
            new Vertex(pivot + new Vector3(+halfWidth, 0f, -halfHeight), normal, u1, v1),  // bottom-right
        };
    }

    /// <summary>Where a piece ends up, so culling never clips a flying slash (§4.6).</summary>
    private static (Vector3 Min, Vector3 Max) ComputeBounds(SketchDoc doc)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        foreach (SketchPiece piece in doc.Pieces)
        {
            float reach = (MathF.Max(MathF.Abs(piece.Shape.Width), MathF.Abs(piece.Shape.Height)) * 0.5f
                              * SketchTextures.QuadOversize + piece.Place.Offset.Length())
                          * MathF.Max(piece.Place.Scale, 0.01f)
                          * MathF.Max(piece.Motion.GrowFrom, piece.Motion.GrowTo)
                          + MathF.Max(piece.Extras.Glow.Radius, 0f);

            foreach (Vector3 point in MotionExtremes(piece))
            {
                min = Vector3.Min(min, point - new Vector3(reach));
                max = Vector3.Max(max, point + new Vector3(reach));
            }
        }

        if (min.X > max.X) return (Vector3.Zero, Vector3.Zero);
        return (min, max);
    }

    /// <summary>Rest, and the end of travel or every path point.</summary>
    private static IEnumerable<Vector3> MotionExtremes(SketchPiece piece)
    {
        yield return piece.Place.Origin;
        if (piece.Motion.Path.Count > 1)
        {
            foreach (Vector3 point in piece.Motion.Path) yield return point;
            yield break;
        }
        if (piece.Motion.Mode == SketchTravelMode.FixedDistance && piece.Motion.Distance > 0f)
            yield return piece.Place.Origin + TravelDirection(piece.Motion) * piece.Motion.Distance;
    }

    private static float BoundsRadius((Vector3 Min, Vector3 Max) bounds) =>
        (bounds.Max - bounds.Min).Length() * 0.5f;

    private static Vector3 TravelDirection(SketchMotion motion)
    {
        Vector3 raw = motion.Direction switch
        {
            SketchDirection.Forward => new Vector3(1f, 0f, 0f),
            SketchDirection.Left => new Vector3(0f, 1f, 0f),
            SketchDirection.Up => new Vector3(0f, 0f, 1f),
            _ => motion.CustomDirection,
        };
        return raw.LengthSquared() < 1e-8f ? new Vector3(1f, 0f, 0f) : Vector3.Normalize(raw);
    }

    private static ushort BlendMode(SketchBlend blend) => blend switch
    {
        SketchBlend.Alpha => 2,
        SketchBlend.Mod => 5,
        _ => 4,       // Additive
    };

    // ── Tracks ──────────────────────────────────────────────────────────────

    /// <summary>Translation keys are an OFFSET FROM THE PIVOT, not an absolute position
    /// (the vanilla bone convention). The pivot already holds Origin, so a piece that
    /// does not travel gets one key of zero.</summary>
    private static TrackRef WriteTranslationTrack(Blob b, SketchPiece piece, uint lengthMs)
    {
        SketchMotion motion = piece.Motion;

        // A drawn path wins over the travel box: it is the more specific instruction.
        if (motion.Path.Count > 1)
        {
            var cumulative = new float[motion.Path.Count];
            float total = 0f;
            for (int i = 1; i < motion.Path.Count; i++)
            {
                total += (motion.Path[i] - motion.Path[i - 1]).Length();
                cumulative[i] = total;
            }
            uint span = (uint)MathF.Max(MathF.Round(motion.Duration * 1000f), 1f);
            span = Math.Min(span, lengthMs);

            // Timestamps are whole milliseconds and must be strictly increasing INSIDE the
            // sequence window, so a track holds at most lengthMs + 1 keys. A hand-drawn path
            // can easily carry hundreds of points over a half-second sketch, so thin it by arc
            // length first, keeping the first and last — the two points the owner placed most
            // deliberately. It has to happen HERE rather than in MakeMonotonic: that helper
            // sees the times but not the keys, and dropping one without the other would break
            // the counts-must-match rule that keeps the track alive at all.
            // At most one key per millisecond can fit, but landing exactly on that ceiling
            // means every single key must occupy its own consecutive tick — which arc-length
            // spacing will never do. Halving it leaves room for the real spacing to survive,
            // and 300 keys over a half-second is far more fidelity than a hand drags a mouse.
            int pointCount = motion.Path.Count;
            int maxKeys = (int)Math.Min(pointCount, Math.Max(lengthMs / 2L + 1L, 2L));
            var times = new uint[maxKeys];
            var keys = new Vector3[maxKeys];
            for (int i = 0; i < maxKeys; i++)
            {
                int source = maxKeys == pointCount
                    ? i
                    : (int)Math.Round(i * (pointCount - 1) / (double)(maxKeys - 1));
                source = Math.Clamp(source, 0, pointCount - 1);
                times[i] = total <= 1e-6f
                    ? (uint)(span * (long)i / Math.Max(maxKeys - 1, 1))
                    : (uint)MathF.Round(span * cumulative[source] / total);
                keys[i] = motion.Path[source] - piece.Place.Origin;
            }
            (times, keys) = DelayTrack(times, keys, StartMs(piece, lengthMs));
            return WriteVectorTrack(b, MakeMonotonic(times, lengthMs), keys);
        }

        // "To the target" flies as the stage's missile; the piece itself holds still.
        if (motion.Mode == SketchTravelMode.ToTarget || motion.Distance <= 0f)
            return WriteVectorTrack(b, new uint[] { 0 }, new[] { Vector3.Zero });

        Vector3 end = TravelDirection(motion) * motion.Distance;
        uint travelMs = (uint)MathF.Max(MathF.Round(motion.Duration * 1000f), 1f);
        travelMs = Math.Min(travelMs, lengthMs);

        // Ease: linear is two keys; "out" is four on a decelerating curve (§4.3).
        uint startMs = StartMs(piece, lengthMs);
        if (motion.Ease == SketchEase.Linear)
        {
            (uint[] lt, Vector3[] lk) = DelayTrack(
                new uint[] { 0, travelMs }, new[] { Vector3.Zero, end }, startMs);
            return WriteVectorTrack(b, MakeMonotonic(lt, lengthMs), lk);
        }

        var easeTimes = new uint[4];
        var easeKeys = new Vector3[4];
        for (int i = 0; i < 4; i++)
        {
            float t = i / 3f;
            easeTimes[i] = (uint)MathF.Round(travelMs * t);
            float eased = 1f - (1f - t) * (1f - t);            // quadratic ease-out
            easeKeys[i] = end * eased;
        }
        (easeTimes, easeKeys) = DelayTrack(easeTimes, easeKeys, startMs);
        return WriteVectorTrack(b, MakeMonotonic(easeTimes, lengthMs), easeKeys);
    }

    /// <summary>Facing, plus spin about the piece's own normal. Spin is applied FIRST and
    /// facing second (q = facing * spin), so a spinning crescent turns in its own plane
    /// whichever way the plane has been pointed.</summary>
    private static TrackRef WriteRotationTrack(Blob b, SketchPiece piece, uint lengthMs)
    {
        Vector4 facingRaw = M2BoneParser.EulerToQuaternion(FacingEuler(piece.Place));
        var facing = new Quaternion(facingRaw.X, facingRaw.Y, facingRaw.Z, facingRaw.W);

        // Quarter turns happen in the piece's OWN plane, so they ride the same axis a spin does
        // and compose with the facing rather than replacing it.
        int quarters = ((piece.Place.QuarterTurns % 4) + 4) % 4;
        if (quarters != 0)
            facing = Quaternion.Normalize(facing * Quaternion.CreateFromAxisAngle(
                PieceNormalAxis(piece), quarters * MathF.PI * 0.5f));

        float spin = piece.Motion.SpinDegreesPerSecond;
        float swing = piece.Motion.SwingDegrees;
        // A camera-facing bone has its whole basis REPLACED by SpellMeshSkinningLaw, not
        // multiplied into, so its rotation track is discarded at draw time and a turn here
        // would be authored and then silently ignored. Write the rest pose only, and let the
        // window grey the dials out rather than let the owner drag a lie.
        bool billboarded = piece.Place.Facing == SketchFacing.FaceCamera;
        if (billboarded || (MathF.Abs(spin) < 0.001f && MathF.Abs(swing) < 0.001f))
            return WriteQuaternionTrack(b, new uint[] { 0 }, new[] { facing });

        Vector3 axis = PieceNormalAxis(piece);
        uint startMs = StartMs(piece, lengthMs);

        // A SWING is finite: it turns over Duration and then holds, which is what a slash does.
        // A SPIN runs for the whole life. Both ride the same axis, so they simply add.
        if (MathF.Abs(swing) >= 0.001f)
        {
            uint swingMs = Math.Min((uint)MathF.Max(MathF.Round(piece.Motion.Duration * 1000f), 1f),
                                    lengthMs > startMs ? lengthMs - startMs : 1u);
            int steps = Math.Clamp((int)MathF.Ceiling(MathF.Abs(swing) / SpinDegreesPerKey) + 1, 2, 256);
            var swingTimes = new uint[steps + 1];
            var swingKeys = new Quaternion[steps + 1];
            for (int i = 0; i < steps; i++)
            {
                float t = i / (float)(steps - 1);
                swingTimes[i] = startMs + (uint)MathF.Round(swingMs * t);
                float degrees = swing * t + spin * (swingTimes[i] / 1000f);
                swingKeys[i] = Quaternion.Normalize(facing *
                    Quaternion.CreateFromAxisAngle(axis, degrees * MathF.PI / 180f));
            }
            // Held to the end, exactly as Cleave holds its blade after the sweep completes.
            swingTimes[steps] = lengthMs;
            float endDegrees = swing + spin * (lengthMs / 1000f);
            swingKeys[steps] = Quaternion.Normalize(facing *
                Quaternion.CreateFromAxisAngle(axis, endDegrees * MathF.PI / 180f));

            // A piece that starts late holds its rest pose until it is due.
            (uint[] st, Quaternion[] sk) = startMs > 0
                ? DelayTrack(swingTimes, swingKeys, 0u) : (swingTimes, swingKeys);
            if (startMs > 0)
            {
                var withHold = new uint[st.Length + 1];
                var holdKeys = new Quaternion[sk.Length + 1];
                withHold[0] = 0;
                holdKeys[0] = sk[0];
                Array.Copy(st, 0, withHold, 1, st.Length);
                Array.Copy(sk, 0, holdKeys, 1, sk.Length);
                st = withHold;
                sk = holdKeys;
            }
            return WriteQuaternionTrack(b, MakeMonotonic(st, lengthMs), sk);
        }

        float totalDegrees = spin * (lengthMs / 1000f);
        int keyCount = Math.Max(2, (int)MathF.Ceiling(MathF.Abs(totalDegrees) / SpinDegreesPerKey) + 1);
        keyCount = Math.Min(keyCount, 256);                    // a sane ceiling on a runaway spin

        var times = new uint[keyCount];
        var keys = new Quaternion[keyCount];
        for (int i = 0; i < keyCount; i++)
        {
            float t = i / (float)(keyCount - 1);
            times[i] = (uint)MathF.Round(lengthMs * t);
            float radians = totalDegrees * t * MathF.PI / 180f;
            Quaternion own = Quaternion.CreateFromAxisAngle(axis, radians);
            keys[i] = Quaternion.Normalize(facing * own);
        }
        return WriteQuaternionTrack(b, MakeMonotonic(times, lengthMs), keys);
    }

    /// <summary>§9 default facing by shape, expressed in the raw frame's Euler angles
    /// (roll about X, pitch about Y, yaw about Z) so M2BoneParser.EulerToQuaternion is
    /// the single conversion both sides agree on.
    ///
    /// The un-rotated quad lies in the raw XZ plane with its normal along +Y:
    ///   * Vertical forward - identity: the plane stands on edge and sweeps forward.
    ///   * Vertical side    - yaw -90 about Z: the plane turns to face forward.
    ///   * Flat             - roll +90 about X: the plane lies down, normal up.
    ///   * Face camera      - identity plus the bone's spherical billboard flag.</summary>
    /// <summary>The axis a piece turns about in its own plane: the plane's normal. Ordinary
    /// facings are authored in the raw XZ plane (normal +Y); a camera-facing piece is authored
    /// in the raw YZ plane (normal +X), so it turns about a different axis.</summary>
    private static Vector3 PieceNormalAxis(SketchPiece piece) =>
        piece.Place.Facing == SketchFacing.FaceCamera ? Vector3.UnitX : Vector3.UnitY;

    private static Vector3 FacingEuler(SketchPlace place) => place.Facing switch
    {
        SketchFacing.VerticalSide => new Vector3(0f, 0f, -90f),
        SketchFacing.Flat => new Vector3(90f, 0f, 0f),
        SketchFacing.Custom => place.CustomEuler,
        _ => Vector3.Zero,          // VerticalForward, FaceCamera
    };

    private static TrackRef WriteScaleTrack(Blob b, SketchPiece piece, uint lengthMs)
    {
        float baseScale = MathF.Max(piece.Place.Scale, 0.0001f);
        SketchMotion motion = piece.Motion;

        if (MathF.Abs(motion.GrowFrom - motion.GrowTo) < 0.0001f)
            return WriteVectorTrack(b, new uint[] { 0 },
                                    new[] { new Vector3(baseScale * motion.GrowFrom) });

        uint growMs = (uint)MathF.Max(MathF.Round(motion.Duration * 1000f), 1f);
        growMs = Math.Min(growMs, lengthMs);
        (uint[] gt, Vector3[] gk) = DelayTrack(new uint[] { 0, growMs }, new[]
        {
            new Vector3(baseScale * motion.GrowFrom),
            new Vector3(baseScale * motion.GrowTo),
        }, StartMs(piece, lengthMs));
        return WriteVectorTrack(b, MakeMonotonic(gt, lengthMs), gk);
    }

    /// <summary>The fade, as fixed16 alpha keys on the piece's colour record.
    ///
    /// A track whose keys are all EQUAL is read as a constant, and a constant of zero
    /// makes M2Reader treat the whole batch as invisible and never submit it. An
    /// animated track returns null from that test instead and is always kept — which is
    /// why a fade-in starting at zero is safe but an all-zero track would not be.</summary>
    private static TrackRef WriteAlphaTrack(Blob b, SketchPiece piece, uint lengthMs)
    {
        float baseAlpha = Math.Clamp(piece.Look.Alpha, 0f, 1f);
        short peak = (short)Math.Clamp((int)MathF.Round(baseAlpha * AlphaOpaque), 1, AlphaOpaque);

        float fadeIn = MathF.Max(piece.Motion.FadeIn, 0f);
        float fadeOut = MathF.Max(piece.Motion.FadeOut, 0f);

        var times = new List<uint>();
        var keys = new List<short>();
        uint startMs = StartMs(piece, lengthMs);

        // Invisible until the piece is due. Two keys of zero, not one: a single key at 0 would
        // let the reader interpolate up from the very beginning of the sequence and the piece
        // would ghost in early instead of arriving when it was told to.
        if (startMs > 0)
        {
            times.Add(0); keys.Add(0);
            if (fadeIn <= 0.001f)
            {
                // A hard appearance still needs a step to be a step: one millisecond of ramp
                // is below anything an eye can see and keeps the timestamps distinct.
                times.Add(startMs > 1 ? startMs - 1 : 0); keys.Add(0);
            }
        }

        if (fadeIn > 0.001f)
        {
            uint inMs = Math.Min((uint)MathF.Round(fadeIn * 1000f), lengthMs);
            times.Add(startMs); keys.Add(0);
            times.Add(startMs + inMs); keys.Add(peak);
        }
        else
        {
            times.Add(startMs); keys.Add(peak);
        }

        if (fadeOut > 0.001f)
        {
            uint outMs = Math.Min((uint)MathF.Round(fadeOut * 1000f), lengthMs);
            uint start = lengthMs > outMs ? lengthMs - outMs : 0u;
            if (start > times[^1]) { times.Add(start); keys.Add(peak); }
            times.Add(lengthMs); keys.Add(0);
        }

        uint[] stamped = MakeMonotonic(times.ToArray(), lengthMs);
        return WriteFixed16Track(b, stamped, keys.ToArray());
    }

    private static TrackRef WriteColourTrack(Blob b, Vector3 colour) =>
        WriteVectorTrack(b, new uint[] { 0 }, new[] { colour });

    /// <summary>How many milliseconds into the sequence a piece is due, clamped inside it.</summary>
    private static uint StartMs(SketchPiece piece, uint lengthMs) =>
        Math.Min((uint)MathF.Max(MathF.Round(MathF.Max(piece.Motion.StartAt, 0f) * 1000f), 0f), lengthMs);

    /// <summary>Hold a motion track at its first value until the piece is due, then let it run.
    /// Without this a delayed piece would already be part-way through its travel by the time it
    /// becomes visible.</summary>
    private static (uint[] Times, T[] Keys) DelayTrack<T>(uint[] times, T[] keys, uint startMs)
    {
        if (startMs == 0 || times.Length == 0) return (times, keys);
        var outTimes = new uint[times.Length + 1];
        var outKeys = new T[keys.Length + 1];
        outTimes[0] = 0;
        outKeys[0] = keys[0];
        for (int i = 0; i < times.Length; i++)
        {
            outTimes[i + 1] = times[i] + startMs;
            outKeys[i + 1] = keys[i];
        }
        return (outTimes, outKeys);
    }

    /// <summary>Timestamps must be strictly increasing AND must never leave the sequence's
    /// window. Both readers care: this client discards a range whose endpoints fall outside
    /// the window, and the web reader rejects the whole track on a timestamp past the
    /// duration. Rounding to whole milliseconds is exactly where two keys collide, so nudge
    /// them apart - and if the nudging pushed the last key past the end, compress the whole
    /// run back inside rather than shipping a track one reader will throw away.</summary>
    private static uint[] MakeMonotonic(uint[] times, uint lengthMs)
    {
        for (int i = 1; i < times.Length; i++)
            if (times[i] <= times[i - 1]) times[i] = times[i - 1] + 1;

        if (times.Length == 0 || times[^1] <= lengthMs) return times;

        // More keys than there are whole milliseconds to hold them. No arrangement of
        // strictly-increasing integers fits, and this helper cannot thin the run (it never
        // sees the keys, and the two counts must match), so the producer has to do it before
        // calling. The first version "spread them evenly" as 0, 1, 2, ... — which is outside
        // the window by the branch's own entry condition, i.e. a silent wrong answer exactly
        // where a loud failure belongs.
        if (times.Length > lengthMs + 1)
            throw new InvalidOperationException(
                $"Sketch track has {times.Length} keys but the sequence is only {lengthMs} ms " +
                "long; the caller must thin its keys to at most lengthMs + 1 before writing.");

        // Squeezing back inside the window can re-collide, and re-bumping after a squeeze can
        // walk straight back out of it. With at most lengthMs + 1 keys an EVEN integer spread
        // over [0, lengthMs] always fits and is always strictly increasing, so that is the last
        // resort: it costs some spacing fidelity in a case that only arises when the owner
        // drew far more points than the effect has milliseconds, and it cannot be wrong.
        int last = times.Length - 1;
        for (int i = 0; i <= last; i++)
            times[i] = (uint)((long)i * lengthMs / last);
        for (int i = 1; i <= last; i++)
            if (times[i] <= times[i - 1]) times[i] = times[i - 1] + 1;
        return times;
    }

    // ── Track payload writers ───────────────────────────────────────────────
    //
    // Each writes ranges + timestamps + keys and returns the 28-byte descriptor the
    // owning record embeds. ONE sequence, so the ranges array is a single INCLUSIVE
    // [0, n-1] pair.

    private static TrackRef WriteVectorTrack(Blob b, uint[] times, Vector3[] keys)
    {
        (int rangeOffset, int timeOffset) = WriteRangesAndTimes(b, times, keys.Length);
        int keyOffset = b.Mark();
        foreach (Vector3 key in keys) b.Vec3(key);
        return new TrackRef(InterpLinear, rangeOffset, 1, timeOffset, times.Length, keyOffset, keys.Length);
    }

    private static TrackRef WriteQuaternionTrack(Blob b, uint[] times, Quaternion[] keys)
    {
        (int rangeOffset, int timeOffset) = WriteRangesAndTimes(b, times, keys.Length);
        int keyOffset = b.Mark();
        // Vanilla stores rotation keys as four RAW floats. Packed int16 quaternions are
        // TBC+ and would be read as garbage here.
        foreach (Quaternion key in keys) { b.F32(key.X); b.F32(key.Y); b.F32(key.Z); b.F32(key.W); }
        return new TrackRef(InterpLinear, rangeOffset, 1, timeOffset, times.Length, keyOffset, keys.Length);
    }

    private static TrackRef WriteFixed16Track(Blob b, uint[] times, short[] keys)
    {
        (int rangeOffset, int timeOffset) = WriteRangesAndTimes(b, times, keys.Length);
        int keyOffset = b.Mark();
        foreach (short key in keys) b.I16(key);
        return new TrackRef(InterpLinear, rangeOffset, 1, timeOffset, times.Length, keyOffset, keys.Length);
    }

    private static (int RangeOffset, int TimeOffset) WriteRangesAndTimes(Blob b, uint[] times, int keyCount)
    {
        if (times.Length != keyCount)
            throw new InvalidOperationException(
                $"Sketch track has {times.Length} timestamps and {keyCount} keys; M2Reader wipes any " +
                "track where the two differ.");

        int rangeOffset = b.Mark();
        b.U32(0);
        b.U32((uint)Math.Max(keyCount - 1, 0));                // INCLUSIVE end index

        int timeOffset = b.Mark();
        foreach (uint time in times) b.U32(time);
        return (rangeOffset, timeOffset);
    }

    private readonly record struct TrackRef(
        ushort Interpolation, int RangesOffset, int RangesCount,
        int TimesOffset, int TimesCount, int KeysOffset, int KeysCount)
    {
        public static readonly TrackRef Absent = new(0, 0, 0, 0, 0, 0, 0);
    }

    private readonly record struct BoneRecord(
        uint Flags, short Parent, Vector3 Pivot,
        TrackRef Translation, TrackRef Rotation, TrackRef Scale);

    /// <summary>Every record array must be exactly count x stride bytes long.
    ///
    /// This is the tripwire for the one mistake in a byte writer that costs a day: get a
    /// record's size wrong by a byte and the array still writes, the readers still accept
    /// the file (they index by stride and bounds-check nothing else), and every record
    /// after the first is silently garbage. The strides above stop being a comment that can
    /// rot and become a check that fires at the moment the field is added.</summary>
    private static void ExpectStride(Blob b, int start, int count, int stride, string what)
    {
        int written = b.Length - start;
        if (written != count * stride)
            throw new InvalidOperationException(
                $"Sketch {what} array wrote {written} bytes for {count} record(s); the readers " +
                $"index it at {stride} bytes each and expect {count * stride}.");
    }

    // ── The byte blob ───────────────────────────────────────────────────────

    private sealed class Blob
    {
        private readonly List<byte> _bytes = new(8192);

        /// <summary>Raw length, NOT aligned - the stride assertions measure with this.</summary>
        public int Length => _bytes.Count;

        public int Mark()
        {
            Align(4);
            return _bytes.Count;
        }

        public void Align(int boundary)
        {
            while (_bytes.Count % boundary != 0) _bytes.Add(0);
        }

        public int Append(byte[] data)
        {
            int offset = Mark();
            _bytes.AddRange(data);
            return offset;
        }

        public void Zero(int count) { for (int i = 0; i < count; i++) _bytes.Add(0); }

        public void U8(byte value) => _bytes.Add(value);
        public void I8(sbyte value) => _bytes.Add(unchecked((byte)value));
        public void U16(ushort value) => _bytes.AddRange(BitConverter.GetBytes(value));
        public void I16(short value) => _bytes.AddRange(BitConverter.GetBytes(value));
        public void U32(uint value) => _bytes.AddRange(BitConverter.GetBytes(value));
        public void I32(int value) => _bytes.AddRange(BitConverter.GetBytes(value));
        public void F32(float value) => _bytes.AddRange(BitConverter.GetBytes(value));
        public void Vec3(Vector3 v) { F32(v.X); F32(v.Y); F32(v.Z); }

        /// <summary>The 28-byte AnimationBlockM2 an owning record embeds inline.</summary>
        public void Track(TrackRef track)
        {
            int start = _bytes.Count;
            U16(track.Interpolation);
            I16(-1);                                            // globalSequence: -1, never 0
            U32((uint)track.RangesCount); U32((uint)track.RangesOffset);
            U32((uint)track.TimesCount); U32((uint)track.TimesOffset);
            U32((uint)track.KeysCount); U32((uint)track.KeysOffset);
            if (_bytes.Count - start != TrackStride)
                throw new InvalidOperationException("Sketch animation block is not 28 bytes");
        }

        /// <summary>Every patch site is a HEADER field, so a patch that runs past the end
        /// of the header is a mistyped offset constant - which would quietly corrupt whatever
        /// payload happens to sit there and still produce a file both readers accept.</summary>
        private static void HeaderBounds(int at)
        {
            if (at < 0 || at + 4 > HeaderBytes)
                throw new InvalidOperationException(
                    $"Sketch header patch at 0x{at:X} runs past the 0x{HeaderBytes:X} header.");
        }

        public void PatchU32(int at, uint value)
        {
            HeaderBounds(at);
            byte[] raw = BitConverter.GetBytes(value);
            for (int i = 0; i < 4; i++) _bytes[at + i] = raw[i];
        }

        public void PatchF32(int at, float value)
        {
            HeaderBounds(at);
            byte[] raw = BitConverter.GetBytes(value);
            for (int i = 0; i < 4; i++) _bytes[at + i] = raw[i];
        }

        /// <summary>An absent array is count 0 AND offset 0 - count > 0 with offset 0 reads
        /// as absent, which is a silent, diagnosis-free way to lose data.</summary>
        public void PatchArray(int at, int count, int offset)
        {
            PatchU32(at, (uint)(count == 0 ? 0 : count));
            PatchU32(at + 4, (uint)(count == 0 ? 0 : offset));
        }

        public void PatchAscii(int at, string text)
        {
            HeaderBounds(at);
            byte[] raw = Encoding.ASCII.GetBytes(text);
            for (int i = 0; i < raw.Length; i++) _bytes[at + i] = raw[i];
        }

        public void PatchBounds(int at, Vector3 min, Vector3 max)
        {
            PatchF32(at + 0, min.X); PatchF32(at + 4, min.Y); PatchF32(at + 8, min.Z);
            PatchF32(at + 12, max.X); PatchF32(at + 16, max.Y); PatchF32(at + 20, max.Z);
        }

        public byte[] ToArray() => _bytes.ToArray();
    }
}
