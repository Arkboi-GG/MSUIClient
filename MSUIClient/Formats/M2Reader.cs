using System.Numerics;
using System.Text;

namespace MSUIClient.Formats;

// ════════════════════════════════════════════════════════════════════════════
// M2Model — parsed M2 model (geometry + texture refs + skeleton + attachments
//                            + sequences + per-bone TRS animation tracks)
// ════════════════════════════════════════════════════════════════════════════

public class M2Model
{
    public uint Version { get; set; }
    public string Name { get; set; } = "";

    public List<M2Vertex> Vertices { get; set; } = new();
    public List<ushort> Indices { get; set; } = new();
    public List<M2Submesh> Submeshes { get; set; } = new();
    public List<M2Batch> Batches { get; set; } = new();
    public List<M2TextureRef> Textures { get; set; } = new();
    public List<ushort> TextureLookup { get; set; } = new();

    // ── Skeleton ─────────────────────────────────────────────────────────────
    public List<M2Bone> Bones { get; set; } = new();
    public List<short> KeyBoneLookup { get; set; } = new();
    public List<M2Attachment> Attachments { get; set; } = new();
    public List<short> AttachmentLookup { get; set; } = new();

    // ── Render flags ─────────────────────────────────────────────────────────
    public List<M2RenderFlag> RenderFlags { get; set; } = new();

    /// <summary>
    /// Particle emitters (PLAN_14). 18% of the archives' 15,214 models have at
    /// least one, and for some - InstancePortal, every torch - the emitters ARE
    /// the model; the mesh is a stub.
    /// </summary>
    public List<M2ParticleEmitter> ParticleEmitters { get; set; } = new();

    // ── Transparency tracks (Session N — static evaluation only) ─────────────
    public List<float> TransparencyStaticAlphas { get; set; } = new();
    public List<ushort> TransparencyLookup { get; set; } = new();

    // ── Sequences (Session O — animation) ────────────────────────────────────
    //
    // Vanilla 1.12 stores all animation data INLINE in the M2 (no .anim file
    // spillover — that's a WotLK+ feature). Each entry below names a logical
    // animation by its AnimationData.dbc Id (0=Stand, 4=Walk, 5=Run,
    // 26=AttackUnarmed, etc.) and gives its duration. The per-bone
    // M2AnimTrack<T> structures key off the SEQUENCE INDEX (position in
    // this list), not the AnimationId — so animationId-to-index resolution
    // happens up front via TryFindSequenceIndexByAnimationId.
    public List<M2Sequence> Sequences { get; set; } = new();

    /// <summary>
    /// Camera selected by cameraLookup[0]. This is the model-authored camera
    /// used by the vanilla unit-frame portrait bake, not an engine-derived
    /// bounds camera.
    /// </summary>
    public M2PortraitCamera? PortraitCamera { get; set; }

    /// <summary>
    /// Durations, in milliseconds, of animation loops that run independently
    /// of the selected Stand/Walk/Run sequence. Character models use these for
    /// blinks: separate eye polygons are scaled on and off by a global loop.
    /// </summary>
    public List<uint> GlobalSequenceDurations { get; set; } = new();

    /// <summary>
    /// The model's OWN collision mesh, separate from the render mesh.
    ///
    /// This is how the real client makes trees and props solid, and why it has
    /// never needed a vmap file: an M2 ships the geometry it is drawn from AND
    /// the simplified geometry it is collided against, in the same file. The
    /// collision hull is typically a few dozen triangles around the trunk while
    /// the render mesh is thousands including every leaf.
    ///
    /// Plenty of doodads have none at all — a torch or a floor rug is meant to
    /// be walked through. Empty here is normal, not a parse failure, and is
    /// exactly why the vmap extractor never wrote a .vmo for those models.
    /// </summary>
    public List<ushort> CollisionIndices { get; set; } = new();
    public List<Vector3> CollisionVertices { get; set; } = new();

    public bool HasCollision => CollisionVertices.Count > 0 && CollisionIndices.Count >= 3;

    public bool IsValid => Vertices.Count > 0 && Indices.Count >= 3;
    public bool HasSkeleton => Bones.Count > 0;

    /// <summary>
    /// Resolve a batch's "is this drawn at all in idle pose?" alpha.
    /// Chain: batch.TextureWeightIndex → TransparencyLookup[idx] →
    ///        TransparencyStaticAlphas[idx]. Any link in the chain
    ///        missing → return 1.0 (fully visible, safe fallback).
    /// </summary>
    public float GetStaticAlphaForBatch(M2Batch batch)
    {
        ushort ti = batch.TextureWeightIndex;
        if (ti >= TransparencyLookup.Count) return 1.0f;

        ushort trackIdx = TransparencyLookup[ti];
        if (trackIdx >= TransparencyStaticAlphas.Count) return 1.0f;

        return TransparencyStaticAlphas[trackIdx];
    }

    /// <summary>
    /// Find a sequence by its AnimationData.dbc ID (e.g. 0 = Stand, 4 = Walk).
    /// Returns the index into <see cref="Sequences"/>, or -1 if no sequence
    /// in this model matches that animation ID.
    ///
    /// A vanilla character M2 has ~150 sequences and many of them are
    /// variations of the same animationId (variationId 1, 2, 3...). This
    /// returns the FIRST match — variationId 0, which is the standard
    /// version. Callers wanting a variation can iterate Sequences directly.
    /// </summary>
    public int TryFindSequenceIndexByAnimationId(int animationId)
    {
        for (int i = 0; i < Sequences.Count; i++)
        {
            if (Sequences[i].AnimationId == animationId &&
                Sequences[i].VariationId == 0)
                return i;
        }
        // Fallback: any variation
        for (int i = 0; i < Sequences.Count; i++)
        {
            if (Sequences[i].AnimationId == animationId)
                return i;
        }
        return -1;
    }
}

/// <summary>
/// One entry in the M2's renderFlags array. See M2Reader.ParseRenderFlags.
/// </summary>
public class M2RenderFlag
{
    public ushort Flags { get; set; }
    public ushort BlendingMode { get; set; }

    public bool Unlit => (Flags & 0x01) != 0;
    public bool TwoSided => (Flags & 0x04) != 0;
    public bool NoZWrite => (Flags & 0x10) != 0;
}

public struct M2Vertex
{
    public float PosX, PosY, PosZ;
    public float NormX, NormY, NormZ;
    public float TexU, TexV;

    public byte BoneWeight0, BoneWeight1, BoneWeight2, BoneWeight3;
    public byte BoneIndex0, BoneIndex1, BoneIndex2, BoneIndex3;
}

public class M2Submesh
{
    public ushort Id { get; set; }              // geoset ID (e.g. 1303 = boot variant 3)
    public ushort VertexStart { get; set; }
    public ushort VertexCount { get; set; }
    public ushort IndexStart { get; set; }
    public ushort IndexCount { get; set; }
}

public class M2Batch
{
    public byte Flags { get; set; }
    public sbyte PriorityPlane { get; set; }
    public ushort ShaderId { get; set; }
    public ushort SubmeshIndex { get; set; }
    public ushort GeosetIndex { get; set; }
    public short ColorIndex { get; set; }
    public ushort MaterialIndex { get; set; }
    public ushort MaterialLayer { get; set; }
    public ushort TextureCount { get; set; }
    public ushort TextureIndex { get; set; }
    public ushort TextureTransformIndex { get; set; }
    public ushort TextureWeightIndex { get; set; }
}

public class M2TextureRef
{
    public uint Type { get; set; }
    public uint Flags { get; set; }
    public string Filename { get; set; } = "";
}

/// <summary>
/// A skeleton joint in the M2 model. 108-byte stride in vanilla 1.12.
///
/// Bind-pose pivot is stored as Vector3 at +96, in WoW Z-up; M2Reader
/// converts to glTF Y-up before storing.
///
/// Animation tracks (Session O):
///   - Translation: Vector3 per key, OFFSET FROM the bone's pivot. M2's
///     animation convention is T(pivot) * T(translation) * R(rotation) *
///     S(scale) * T(-pivot), so translation moves the bone away from its
///     bind-pose pivot. SkinnedGlbWriter folds this in by adding the
///     M2 translation track to the glTF node's rest-pose local position.
///   - Rotation:    Vector4 quaternion per key. Vanilla uses unpacked floats;
///     TBC+ switched to int16 PACK_QUATERNION.
///   - Scale:       Vector3 per key.
///
/// Each track stores a SHARED timestamps/keys array with per-sequence
/// {start, end} ranges into that shared array. See M2AnimTrack&lt;T&gt;.
/// </summary>
public class M2Bone
{
    public int KeyBoneId { get; set; }
    public uint Flags { get; set; }
    public short ParentBone { get; set; }   // -1 = root
    public ushort SubmeshId { get; set; }
    public Vector3 Pivot { get; set; }      // glTF Y-up after conversion

    // ── Animation tracks (Session O) ─────────────────────────────────────────
    // All three may be empty (UsesSequence returns false for every sequence)
    // for static bones — common for finger/accessory bones that don't animate.
    public M2AnimTrack<Vector3> Translation { get; set; } = new();
    public M2AnimTrack<Vector4> Rotation { get; set; } = new();
    public M2AnimTrack<Vector3> Scale { get; set; } = new();
}

/// <summary>
/// A semantic attachment point on the character skeleton. 48-byte stride.
/// </summary>
public class M2Attachment
{
    public uint Id { get; set; }
    public uint BoneIndex { get; set; }
    public Vector3 Position { get; set; }   // glTF Y-up after conversion
}

/// <summary>
/// One animation sequence header (vanilla 1.12 AnimationSequenceM2 layout).
/// 68-byte stride.
///
/// === Field semantics ===
///   AnimationId: index into AnimationData.dbc. Vanilla key values:
///     0   Stand           4   Walk            5   Run
///     11  ShuffleLeft     12  ShuffleRight    13  Walkbackwards
///     14  Sleep           15  SleepUp         16  SitGround
///     26  AttackUnarmed   27  Attack1H        28  Attack2H
///     31  ParryUnarmed    34  ShieldBlock     37  ReadyUnarmed
///     45  Death           67  CombatWound     ... (and many more)
///   See client-side `animation-names.js` for the full table.
///
///   VariationId: many animationIds have variants (Stand has 1-4 idle
///     fidgets, attacks have left/right swings, etc.) — variationId 0
///     is the canonical version.
///
///   StartTimestamp / EndTimestamp: absolute timeline positions in MS on
///     the SHARED per-track timeline (the timestamps array of each
///     M2AnimTrack). Duration of this sequence = end - start.
///
///   Flags: bit 0x20 = looping. Other bits TBD.
/// </summary>
public class M2Sequence
{
    public ushort AnimationId { get; set; }
    public ushort VariationId { get; set; }
    public uint StartTimestamp { get; set; }
    public uint EndTimestamp { get; set; }
    public float MoveSpeed { get; set; }
    public uint Flags { get; set; }

    /// <summary>
    /// Milliseconds the client cross-fades INTO this sequence from whatever was
    /// playing before. Field +32 of AnimationSequenceM2.
    ///
    /// It was skipped for as long as playback was a hard cut, which made it dead
    /// data. It is not: it is the per-clip blend duration the reference client
    /// uses, so a run cycle eases into a stand instead of snapping to its first
    /// frame. Zero is authored on clips that genuinely want no fade.
    /// </summary>
    public uint BlendTimeMs { get; set; }

    public uint DurationMs => EndTimestamp > StartTimestamp
        ? EndTimestamp - StartTimestamp
        : 0;
    public bool IsLooping => (Flags & 0x20) != 0;
}

/// <summary>
/// One vanilla M2 animation track.
///
/// === Wire layout (vanilla AnimationBlockM2, 28 bytes) ===
///   +0   uint16  interpolationType  (0=none, 1=linear, 2=hermite, 3=bezier)
///   +2   int16   globalSequence     (-1 = use per-sequence ranges)
///   +4   M2Array ranges             (one AnimationRange{start,end} per sequence)
///  +12   M2Array timestamps         (uint32, shared across all sequences)
///  +20   M2Array keys               (T per key, shared across all sequences)
///
/// === Per-sequence indexing ===
/// All sequences share ONE timestamps array and ONE keys array. For
/// animation_index N, the slice that belongs to it is
/// [Ranges[N].Start .. Ranges[N].End). Within that slice:
///   - Timestamps[i] is an ABSOLUTE position on the shared timeline (ms)
///   - To convert to "time since start of this sequence" (for glTF, which
///     needs per-clip relative times), subtract Timestamps[Ranges[N].Start]
///     from each timestamp in the range.
///
/// === Why a per-sequence subdivision is necessary ===
/// glTF animations have one timeline per clip; they can't reference into a
/// shared global timeline. So we must slice the M2's shared keys/timestamps
/// into per-sequence sub-arrays at bake time.
///
/// === GlobalSequence ===
/// When globalSequence &gt; -1, the track loops independently of any
/// AnimationData sequence. These tracks are excluded from
/// <see cref="UsesSequence"/> because M2Animator samples them against an
/// independent global clock instead of restarting them with every movement
/// clip. Character models use this mechanism for blinking.
/// </summary>
public class M2AnimTrack<T> where T : struct
{
    public ushort InterpolationType { get; set; }
    public short GlobalSequence { get; set; } = -1;

    /// <summary>One [start, end) index pair per sequence.</summary>
    public List<AnimationRange> Ranges { get; set; } = new();
    /// <summary>Shared timestamp array (absolute timeline positions, milliseconds).</summary>
    public List<uint> Timestamps { get; set; } = new();
    /// <summary>Shared keyframe values, parallel to Timestamps.</summary>
    public List<T> Keys { get; set; } = new();

    public bool IsLinear => InterpolationType == 1;

    /// <summary>
    /// True if this track has at least one keyframe whose timestamp falls
    /// within the given sequence's absolute time window.
    ///
    /// A valid <c>Ranges[sequenceIndex]</c> slice wins, matching WoWee. Some
    /// vanilla character M2s repeat a whole-track sentinel range for every
    /// sequence, so ranges are validated against the sequence header's
    /// timestamp window and that window remains the fallback.
    /// </summary>
    public bool UsesSequence(int sequenceIndex, uint startTimestampMs, uint endTimestampMs)
    {
        if (GlobalSequence > -1) return false; // sampled independently at runtime
        if (Timestamps.Count == 0) return false;
        if (endTimestampMs < startTimestampMs) return false;

        if (TryGetRangeSlice(sequenceIndex, startTimestampMs, endTimestampMs, out _, out _))
            return true;

        // Quick reject if the whole sequence window lies outside the
        // track's timeline.
        if (Timestamps[0] > endTimestampMs) return false;
        if (Timestamps[Timestamps.Count - 1] < startTimestampMs) return false;

        return true;
    }

    /// <summary>
    /// Enumerate (timeRelativeMs, key) pairs for a given sequence,
    /// identified by its absolute <paramref name="startTimestampMs"/> and
    /// <paramref name="endTimestampMs"/> on the shared track timeline.
    ///
    /// Returned times are relative to <paramref name="startTimestampMs"/>
    /// (so the caller's clip starts at t=0). We INCLUDE keyframes at both
    /// endpoints (inclusive window) so the clip's last keyframe lines up
    /// with the sequence duration — important because glTF clip duration
    /// is derived from <c>max(keyframe times)</c>.
    /// </summary>
    public IEnumerable<(uint timeMs, T value)> EnumerateSequenceKeys(
        int sequenceIndex, uint startTimestampMs, uint endTimestampMs)
    {
        if (!UsesSequence(sequenceIndex, startTimestampMs, endTimestampMs)) yield break;

        if (TryGetRangeSlice(sequenceIndex, startTimestampMs, endTimestampMs,
                             out int rangeStart, out int rangeEnd))
        {
            // AnimationRange.End is INCLUSIVE — it is the index of the sequence's
            // LAST keyframe. For a looping clip that final key sits at the band
            // end and holds the loop-closing pose (equal to the key at the band
            // start). Iterating [start, end) dropped it, so every clip ran a
            // keyframe short of its own duration: the pose held its penultimate
            // frame for the tail (up to ~200 ms) then SNAPPED back to the start —
            // the "animation resets instead of looping" bug. Iterate INCLUSIVE and
            // rebase to the band start so the clip runs 0..duration. A key whose
            // timestamp falls outside the band is a whole-track sentinel reaching
            // into another sequence — skip it (matches benilla's in_band filter).
            for (int i = rangeStart; i <= rangeEnd; i++)
            {
                uint t = Timestamps[i];
                if (t < startTimestampMs || t > endTimestampMs) continue;
                yield return (t - startTimestampMs, Keys[i]);
            }
            yield break;
        }

        for (int i = 0; i < Timestamps.Count; i++)
        {
            uint t = Timestamps[i];
            if (t < startTimestampMs) continue;
            if (t > endTimestampMs) break; // Timestamps is monotonic
            yield return (t - startTimestampMs, Keys[i]);
        }
    }

    private bool TryGetRangeSlice(int sequenceIndex,
                                  uint startTimestampMs, uint endTimestampMs,
                                  out int start, out int end)
    {
        start = end = 0;
        if (sequenceIndex < 0 || sequenceIndex >= Ranges.Count) return false;

        var range = Ranges[sequenceIndex];
        // AnimationRange.End is an INCLUSIVE key index, so it must be a valid slot
        // in both arrays; End == Start is a legal single-key range.
        if (range.End < range.Start ||
            range.End >= (uint)Timestamps.Count || range.End >= (uint)Keys.Count)
            return false;

        start = (int)range.Start;
        end = (int)range.End;

        // Some vanilla character files repeat a whole-track sentinel range for
        // every animation. Reject it when its bracket keys do not fit this
        // sequence's window and let the header-window fallback select the real
        // keys. Both endpoints are inclusive: Timestamps[start] and Timestamps[end].
        uint first = Timestamps[start];
        uint last = Timestamps[end];
        if (first < startTimestampMs || first > endTimestampMs ||
            last < startTimestampMs || last > endTimestampMs)
        {
            start = end = 0;
            return false;
        }

        return true;
    }
}

/// <summary>One sequence's slice indices into the shared keys/timestamps arrays.</summary>
public struct AnimationRange
{
    public uint Start;
    public uint End;
}

// ════════════════════════════════════════════════════════════════════════════
// PARTICLE EMITTERS — PLAN_14_PARTICLES.md §3.
//
// THE STRIDE IS 504, NOT THE 476 EVERY REFERENCE QUOTES. It was derived from
// the bytes, not looked up: take 80 models with three or more emitters, then
// require of every candidate (field offset, stride) pair that EVERY emitter in
// EVERY model satisfies bone < nBones and texture < nTextures. Exactly one pair
// in the range 380..556 survives - (+20, 504), at 80/80. Using 476 would have
// desynchronised every emitter after the first.
//
// The ten M2Tracks at +52 are confirmed the same way: 200/200 models validate
// ten consecutive 28-byte tracks, and the eleventh fails on 200/200. That is a
// boundary, not a threshold.
//
// WHAT IS NOT PARSED HERE, AND WHY. The 172 bytes from +332 to +504 hold
// colour, alpha, scale, spin, drag, tumble and wind. A first reconstruction of
// that region was WRONG (see PLAN_14 §3.3) and is not repeated on a guess. The
// byte sweep says +480/+488/+496 are M2Array-shaped at 100% across 910
// emitters, so the struct ends with arrays - but which is which is stage 2's
// job, using the sweep that already worked rather than a wiki struct.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// One M2 particle emitter, as far as the layout is CONFIRMED. Fields the
/// research has not settled are deliberately absent rather than present and
/// wrong - a zero that means "not read" is indistinguishable from a zero that
/// means zero, and that is how a parsing bug becomes a look bug.
/// </summary>
public enum ParticleShape { Plane, Sphere, Spline }

public class M2ParticleEmitter
{
    public uint ParticleId { get; set; }
    public uint Flags { get; set; }

    /// <summary>
    /// Emitter origin, **converted to the same Y-up space as the render
    /// vertices** - `(x, y, z) -> (x, z, -y)`, exactly what ParseVertices and
    /// the bone pivots do.
    ///
    /// THIS CONVERSION IS NOT OPTIONAL AND ITS ABSENCE IS INVISIBLE. The
    /// doodad pipeline's BuildPlacement yaws about Y and then applies
    /// PlacementToWorld, because "an M2's render vertices are already in
    /// placement space" - and they are, because ParseVertices swapped them.
    /// Reading the emitter position raw leaves it in the M2's own Z-up space,
    /// so InstancePortal's 2.737 "up the disc's axis" became 2.737 SIDEWAYS
    /// after the heading, and the portal appeared low and off to one side with
    /// nothing logged. It looked like a maths bug in the emitter for two
    /// rounds; it was a coordinate space.
    ///
    /// Measured: the emitter's raw position equals its bone's raw pivot exactly
    /// (0, 0, 2.737), and M2Reader already swaps that pivot. Not swapping here
    /// meant the two disagreed about where the same point is.
    /// </summary>
    public float PosX, PosY, PosZ;

    /// <summary>Index into the model's bone list. The emitter rides this bone.</summary>
    public ushort Bone { get; set; }

    /// <summary>Index into the model's TEXTURES array - NOT through TextureLookup.</summary>
    public ushort Texture { get; set; }

    /// <summary>0 opaque, 1 alpha-key, 2 alpha, 3 no-alpha-add, 4 ADD, 5 mod, 6 mod2x.</summary>
    public byte BlendingType { get; set; }

    /// <summary>Raw byte at +0x29 - the PADDING byte before the real u16 emitterType at
    /// +0x2a, so it is unreliable; use <see cref="Shape"/> for the kernel.</summary>
    public byte EmitterType { get; set; }

    /// <summary>
    /// Emission SHAPE (benilla-formats/particles.rs:642, :735): the file emitterType is a
    /// u16 at +0x2a. 2 = Sphere, 3 = Spline, else Plane. THIS picks the kernel. The
    /// InstancePortal is a SPHERE - born on a ring at radius ~areaLength and pulled radially
    /// INWARD (the outer-ring-coming-in swirl). Reading it as a plane (born near the centre)
    /// is why the portal emanated OUTWARD from the middle.
    /// </summary>
    public ParticleShape Shape { get; set; }

    public byte ParticleType { get; set; }
    public byte HeadOrTail { get; set; }

    /// <summary>Sprite sheet dimensions. 1x1 means the texture is one cell.</summary>
    public ushort TextureRows { get; set; }
    public ushort TextureCols { get; set; }

    // ── The ten tracks at +52, static values only ────────────────────────────
    //
    // Same contract as TransparencyStaticAlphas above: a track with exactly one
    // key is a constant and is read; an animated track is reported by its key
    // count and left for the runtime. Every InstancePortal track is a constant,
    // so the portal needs nothing more than this.

    /// <summary>
    /// Yards per second along the emission direction. **CAN BE NEGATIVE, and
    /// that is not a parsing error** - InstancePortal emits at -3.333, which is
    /// what makes a portal pull inward instead of fountaining. Do not clamp.
    /// </summary>
    public float EmissionSpeed { get; set; }

    public float SpeedVariation { get; set; }

    /// <summary>Cone half-angle in radians. pi is a full hemisphere.</summary>
    public float VerticalRange { get; set; }

    public float HorizontalRange { get; set; }
    public float Gravity { get; set; }

    /// <summary>Particle life in seconds.</summary>
    public float Lifespan { get; set; }

    /// <summary>Particles per second. Steady-state population is this times Lifespan.</summary>
    public float EmissionRate { get; set; }

    public float EmissionAreaLength { get; set; }
    public float EmissionAreaWidth { get; set; }
    /// <summary>
    /// The tenth emission track (+0x130): **zSource** - a pull toward a point
    /// (0,0,zSource) above/below the emitter, 0 = unused. benilla parity
    /// (benilla-formats/particles.rs:827, track @ +0x130). This is NOT drag -
    /// drag is a separate plain f32 at +0x194 (<see cref="Drag"/>). MSUI called
    /// this "Deceleration" for one draft, which conflated the two. 0.000 on both
    /// InstancePortal emitters.
    /// </summary>
    public float ZSource { get; set; }

    /// <summary>
    /// Velocity **drag** - a plain f32 at file +0x194 (NOT a track), applied each
    /// frame as `vel -= min(dt*drag, 1)*vel` (benilla-formats/particles.rs:828,
    /// clamped-linear, sim.rs). 0.000 on both InstancePortal emitters; nonzero on
    /// props like CandelabraTallWall01 (drag 10) that author a fast zero-gravity
    /// jet and rely on drag to contain it.
    /// </summary>
    public float Drag { get; set; }

    // ── The ramp block at +332, derived 2026-07-26 (PLAN_14 §3.4) ────────────
    //
    // Confirmed the same way as the stride: +332 is in [0,1] on 1086/1086
    // emitters, and the three floats at +348 are finite and non-negative on
    // 1086/1086. Their SHAPE settles it - 510 of them grow then shrink, 252
    // shrink, 225 grow. That is what a particle scale ramp looks like and what
    // nothing else does.
    //
    // The earlier guess of three 16-byte FBlocks here was wrong (§3.3). This is
    // not that: the keys are INLINE and there are exactly three of them, with
    // MidPoint saying where in the particle's life the middle key falls.

    /// <summary>
    /// Where the middle colour/scale key sits in the particle's life, 0..1.
    /// InstancePortal uses 0.20 and 0.30 - the flash happens early.
    /// </summary>
    public float MidPoint { get; set; } = 0.5f;

    /// <summary>
    /// Start / middle / end colour, straight BGRA bytes. InstancePortal's first
    /// emitter is (210,158,91) = RGB (91,158,210), a light blue - which is what
    /// a 1.12 instance portal looks like, and is the check that this block is
    /// read the right way round.
    /// </summary>
    public uint[] ColorKeys { get; set; } = new uint[3];

    /// <summary>Start / middle / end size in yards. Portal: 0.278 -> 0.972 -> 0.028.</summary>
    public float[] ScaleKeys { get; set; } = new float[3];

    // ── Head-quad flipbook cell ramp (benilla-formats/particles.rs:770-798) ───
    //
    // The flame's sprite-sheet cell is driven PER PARTICLE by the particle's own
    // age, not a global clock: each particle walks the FlameLick 4x4 sheet over
    // its life. Two segments split at MidPoint, each an authored (begin,end) cell
    // pair with a per-segment repeat count. Byte offsets confirmed against benilla:
    // seg A (begin +0x168, end +0x16a), seg B (begin +0x16e, end +0x170); the
    // repeat count is wedged between the pairs at +0x16c / +0x172. A 1x1 sheet has
    // no flipbook and these stay 0 (SampleHeadCell short-circuits to cell 0).

    /// <summary>Per-segment head-cell (begin, end) atlas indices — [0] = seg A, [1] = seg B.</summary>
    public ushort[] HeadCellBegin { get; set; } = new ushort[2];
    public ushort[] HeadCellEnd { get; set; } = new ushort[2];
    /// <summary>Per-segment flipbook repeat count (+0x16c / +0x172); 1 = one pass over the segment.</summary>
    public float[] HeadCellRepeat { get; set; } = new float[2] { 1f, 1f };

    // ── The emitter's bone spin (PLAN_14 §11) ────────────────────────────────
    //
    // THIS IS THE SWIRL. InstancePortal's two emitter bones carry an 18-key
    // rotation track and NOTHING else - no translation, no scale - and the keys
    // are a steady turn about local X, about 20 degrees apart, a full revolution
    // every 3334 ms. The emission plane is the bone's local XY, so a full turn
    // about X sweeps that plane through every orientation and throws the
    // particles out into a disc.
    //
    // Without it the emitter is static, the hemisphere collapses to a blob near
    // the origin, and the portal reads as a small off-centre haze instead of
    // something that fills the doorway. Measured, not guessed: the bone flags
    // are 0x0200 (animated) and the track is right there.
    //
    // Held RAW, in the M2's own Z-up space. M2Model.Bones applies the glTF Y-up
    // swap for the character pipeline, and particles work in world space through
    // the placement matrix, so borrowing that would need the swap undone again.

    /// <summary>Timestamps of the emitter bone's rotation keys, absolute ms.</summary>
    public uint[] BoneRotationTimes { get; set; } = [];

    /// <summary>
    /// Rotation keys, swapped into the render vertices' Y-up space the same way
    /// M2Reader swaps bone rotations: `(x, y, z, w) -> (x, z, -y, w)`. Vanilla
    /// stores four FLOATS per key, not packed int16.
    /// </summary>
    public Vector4[] BoneRotationKeys { get; set; } = [];

    /// <summary>Sequence bounds in ms. InstancePortal's runs 3333..6667, not 0..3334.</summary>
    public uint SequenceStart { get; set; }
    public uint SequenceEnd { get; set; }

    /// <summary>Loop period in ms for <see cref="BoneRotationKeys"/>: its GLOBAL SEQUENCE's
    /// duration, or 0 when the track rides the playing sequence instead.</summary>
    public float BoneRotationLoopMs { get; set; }

    public bool HasBoneSpin =>
        BoneRotationKeys.Length > 1 && (BoneRotationLoopMs > 0f || SequenceEnd > SequenceStart);

    // ── GLOBAL SEQUENCES: why a track's loop is not the sequence's loop ─────────────────────
    //
    // A track that declares a global sequence (gseq >= 0 at track+2) runs on ITS OWN clock, of
    // the duration in the model's globalSequences table - independently of, and usually far
    // shorter than, the animation sequence being played.
    //
    // Every animated bone on UI_MainMenu's login gate is exactly this case: bones 13/14/15/16
    // (rotation) and 32..47 (translation) all declare global sequence 0, whose duration is
    // 13333 ms, and their keys span 0..13333. Sequence 0 is 40000 ms. Looping those keys over
    // the SEQUENCE span - which is what this file did - plays the real 13.3 s of motion, then
    // clamps to the last key and parks everything for the remaining 26.7 s before snapping back.
    //
    // Parked is what the login screen showed: the drifting motes stop, and because their
    // emitters author EmissionSpeed 0, every particle born during those 26.7 s lands on the
    // same frozen point and stacks additively into a fat static blob. The animation was never
    // missing - it just ran out and stayed out, which is precisely how Nico described it.

    /// <summary>
    /// Map wall-clock seconds onto a track's own timeline: its global sequence's period when it
    /// has one, else the playing sequence's [start, end] window (timestamps there are ABSOLUTE
    /// and need not start at zero - InstancePortal's sequence runs 3333..6667).
    /// </summary>
    private float TrackTime(double elapsedSeconds, float loopMs)
    {
        double ms = elapsedSeconds * 1000.0;
        if (loopMs > 0f) return (float)(ms % loopMs);
        double span = SequenceEnd - SequenceStart;
        return span > 0.0 ? (float)(SequenceStart + ms % span) : 0f;
    }

    // ── The twinkle block (+0x180..+0x18c) ──────────────────────────────────
    //
    // benilla-formats/particles.rs:837-840. Missing from MSUI until 2026-07-29, which is
    // why UI_MainMenu's two brazier GLOWS (emitters 25 and 27, twinkleScale min 0 / max 1)
    // burned as steady discs instead of pulsing.

    /// <summary>LUT walk rate: the noise index is floor(clamp(TwinkleSpeed*age, 0, 255)) + phase.</summary>
    public float TwinkleSpeed { get; set; }

    /// <summary>Draw GATE. While this is &lt; 1, a frame whose noise sample exceeds it emits NO
    /// quad at all - the reference's hard scintillation (benilla quads.rs, byte-verified
    /// 0x7b2adc). Placed content authors 1.0, so the gate is normally inert.</summary>
    public float TwinklePercent { get; set; } = 1f;

    public float TwinkleMin { get; set; } = 1f;
    public float TwinkleMax { get; set; } = 1f;

    /// <summary>
    /// The gated twinkle SIZE multiplier for a noise sample in [0,1). Identity when the authored
    /// range is degenerate - {0,0} and {1,1} alike burn steady (benilla ParticleEmitterDef::twinkle;
    /// the old "base + rand" reading collapsed the kobold candle to zero).
    /// </summary>
    public float Twinkle(float noise)
        => MathF.Abs(TwinkleMax - TwinkleMin) < 1e-6f
            ? 1f
            : noise * (TwinkleMax - TwinkleMin) + TwinkleMin;

    // ── The emitter's BONE CHAIN ────────────────────────────────────────────
    //
    // The emitter's bone composes each particle's BIRTH (benilla particles.rs:10-11): an emitter
    // riding an animated bone leaves a TRAIL, because the birth position moves while the particles
    // already spawned stay where they were born. MSUI only ever sampled the emitter bone's
    // ROTATION (BoneRotationKeys, and only on the model-space path), so a TRANSLATION-driven
    // emitter never moved at all.
    //
    // That is not a corner case. UI_MainMenu's 16 GLOWBALL emitters (indices 1..16) author
    // EmissionSpeed 0, gravity 0 and drag 0 - every bit of their motion is in bones 32..47, which
    // carry 8 translation keys each under parents 13/14 (19 rotation keys each). With the bone
    // ignored, each emitter piled its whole ~90-particle steady-state population (rate 30-45/s x
    // 2 s life) onto ONE point; ninety additive sprites at alpha ~0.03 saturate, so the login
    // screen grew 16 fat motionless flares where the OG has drifting motes.

    /// <summary>
    /// One joint of the emitter bone's chain, in the render vertices' Y-up space: the bind pivot
    /// plus sequence 0's translation and rotation keys. Applied as the M2 bone law
    /// `p' = pivot + T(t) + R(t)*(p - pivot)`, innermost joint first.
    /// </summary>
    public sealed class BoneNode
    {
        public Vector3 Pivot;
        public uint[] TransTimes = [];
        public Vector3[] TransKeys = [];
        public uint[] RotTimes = [];
        public Vector4[] RotKeys = [];

        /// <summary>Loop period in ms, per track: the track's GLOBAL SEQUENCE duration when it
        /// declares one, else 0 = "loop over the playing sequence's span". See
        /// <see cref="M2ParticleEmitter.TrackTime"/> - getting this wrong is what parked the
        /// login's motes.</summary>
        public float TransLoopMs;
        public float RotLoopMs;

        /// <summary>Whether this joint carries real animation. A single key is a CONSTANT and is
        /// still applied once the chain exists, but it does not on its own justify building one -
        /// that would silently move every static emitter in the world.</summary>
        public bool Animates => TransKeys.Length > 1 || RotKeys.Length > 1;
    }

    /// <summary>The emitter's own bone then its parents, innermost first. EMPTY when no joint on
    /// the chain animates, so a static emitter keeps its previous behaviour exactly.</summary>
    public BoneNode[] BoneChain { get; set; } = [];

    public bool HasBoneMotion => BoneChain.Length > 0;

    /// <summary>
    /// The emitter's origin at a wall-clock time, walked up the animated bone chain and looping
    /// sequence 0. Returns <paramref name="bindPosition"/> untouched when the chain is static.
    /// </summary>
    public Vector3 SampleBonePosition(double elapsedSeconds, Vector3 bindPosition)
    {
        if (!HasBoneMotion) return bindPosition;

        // Each track gets its OWN clock: on this model the rotations and the translations happen
        // to share global sequence 0, but nothing in the format says they must.
        var p = bindPosition;
        foreach (var node in BoneChain)
        {
            var q = SampleQuat(node.RotTimes, node.RotKeys,
                               TrackTime(elapsedSeconds, node.RotLoopMs));
            p = node.Pivot
              + SampleVec(node.TransTimes, node.TransKeys,
                          TrackTime(elapsedSeconds, node.TransLoopMs))
              + Vector3.Transform(p - node.Pivot, q);
        }
        return p;
    }

    private static Vector3 SampleVec(uint[] times, Vector3[] keys, float t)
    {
        if (keys.Length == 0) return Vector3.Zero;
        int last = keys.Length - 1;
        if (last == 0 || t <= times[0]) return keys[0];
        if (t >= times[last]) return keys[last];
        int i = 0;
        while (i < last && times[i + 1] < t) i++;
        float span = times[i + 1] - times[i];
        float f = span > 0f ? (t - times[i]) / span : 0f;
        return keys[i] + (keys[i + 1] - keys[i]) * f;
    }

    private static Quaternion SampleQuat(uint[] times, Vector4[] keys, float t)
    {
        if (keys.Length == 0) return Quaternion.Identity;
        int last = keys.Length - 1;
        if (last == 0 || t <= times[0]) return Q(keys[0]);
        if (t >= times[last]) return Q(keys[last]);
        int i = 0;
        while (i < last && times[i + 1] < t) i++;
        float span = times[i + 1] - times[i];
        float f = span > 0f ? (t - times[i]) / span : 0f;
        return Quaternion.Slerp(Q(keys[i]), Q(keys[i + 1]), f);
    }

    /// <summary>
    /// The emitter bone's rotation at a wall-clock time, looping the sequence.
    /// Identity when the bone does not animate.
    /// </summary>
    public Quaternion SampleBoneRotation(double elapsedSeconds)
    {
        if (!HasBoneSpin) return Quaternion.Identity;

        float t = TrackTime(elapsedSeconds, BoneRotationLoopMs);

        var times = BoneRotationTimes;
        int last = times.Length - 1;
        if (t <= times[0]) return Q(BoneRotationKeys[0]);
        if (t >= times[last]) return Q(BoneRotationKeys[last]);

        int i = 0;
        while (i < last && times[i + 1] < t) i++;

        float span2 = times[i + 1] - times[i];
        float f = span2 > 0f ? (float)((t - times[i]) / span2) : 0f;
        return Quaternion.Slerp(Q(BoneRotationKeys[i]), Q(BoneRotationKeys[i + 1]), f);
    }

    private static Quaternion Q(Vector4 v) => new(v.X, v.Y, v.Z, v.W);

    /// <summary>Key counts for the ten tracks, in declaration order. >1 means animated.</summary>
    public int[] TrackKeyCounts { get; set; } = new int[10];

    /// <summary>Sample the three-key ramp at life fraction t, honouring MidPoint.</summary>
    public void SampleRamp(float t, out Vector4 rgba, out float scale)
    {
        float mid = MathF.Min(MathF.Max(MidPoint, 0.001f), 0.999f);
        int a, b;
        float f;
        if (t <= mid) { a = 0; b = 1; f = t / mid; }
        else { a = 1; b = 2; f = (t - mid) / (1f - mid); }

        // benilla's endpoint inset (benilla-formats/particles.rs:196): keep the ramp
        // off its exact 0/1 endpoints so the first/last key never fully owns a frame,
        // applied to the within-segment fraction.
        f = f * 0.99f + 0.005f;

        scale = ScaleKeys[a] + (ScaleKeys[b] - ScaleKeys[a]) * f;

        var ca = Bgra(ColorKeys[a]);
        var cb = Bgra(ColorKeys[b]);
        rgba = ca + (cb - ca) * f;
    }

    private static Vector4 Bgra(uint packed)
        => new(((packed >> 16) & 0xFF) / 255f,   // R sits in the third byte
               ((packed >> 8) & 0xFF) / 255f,
               (packed & 0xFF) / 255f,
               ((packed >> 24) & 0xFF) / 255f);

    /// <summary>
    /// The head-quad flipbook cell INDEX at life fraction t (0..1), driven by the particle's own
    /// age — benilla's OverLife::sample + CellRamp::sample (benilla-formats/particles.rs:97-218).
    /// Returns 0 for a non-flipbook (1x1) sheet. Two segments split at MidPoint; each segment
    /// fraction gets the same endpoint inset as <see cref="SampleRamp"/>, then the CellRamp maps
    /// it to floor(base + span*ct) &amp; 0xFF (forward arm base=begin,span=end-begin+1; reverse arm
    /// base=begin+1,span=end-begin-1). The per-segment repeat count cycles the FLIPBOOK ONLY.
    /// </summary>
    public int SampleHeadCell(float t)
    {
        int cells = Math.Max(1, (int)TextureRows) * Math.Max(1, (int)TextureCols);
        if (cells <= 1) return 0;
        t = Math.Clamp(t, 0f, 1f);
        float mid = Math.Clamp(MidPoint, 1e-3f, 1f);
        int seg;
        float st;
        if (t <= mid) { seg = 0; st = t / mid; }
        else { seg = 1; st = (t - mid) / MathF.Max(1f - mid, 1e-3f); }
        st = Math.Clamp(st, 0f, 1f) * 0.99f + 0.005f;              // endpoint inset (as SampleRamp)
        float rep = HeadCellRepeat[seg];
        float ct = rep != 1f ? st * rep - MathF.Floor(st * rep) : st;   // repeat wrap: fract(t*rep)
        return CellSample(HeadCellBegin[seg], HeadCellEnd[seg], ct);
    }

    // benilla CellRamp::new + ::sample: index = floor(base + span*t) & 0xFF (mod-256 column wrap).
    private static int CellSample(ushort begin, ushort end, float ct)
    {
        int b = begin, e = end, baseC, span;
        if (e >= b) { baseC = b; span = e - b + 1; }
        else { baseC = b + 1; span = e - b - 1; }
        return (int)MathF.Floor(baseC + span * ct) & 0xFF;
    }

    /// <summary>
    /// The head cell as a UV sub-rectangle (u0, v0, du, dv) for the sprite sheet — (0,0,1,1) for a
    /// non-flipbook (1x1) emitter, so the model-space swirls stay byte-identical. Atlas walk mirrors
    /// benilla quads.rs:111-128 exactly: col = idx % cols (the column wraps; cols is a power of two
    /// on every real sheet), row = (idx / cols) % rows (benilla lets the row run off and relies on
    /// repeat addressing to land back on row 0 — the modulo reproduces that net result in-atlas).
    /// </summary>
    public Vector4 SampleHeadCellRect(float t)
    {
        int cols = Math.Max(1, (int)TextureCols);
        int rows = Math.Max(1, (int)TextureRows);
        if (cols * rows <= 1) return new Vector4(0f, 0f, 1f, 1f);
        int idx = SampleHeadCell(t);
        int cx = idx % cols;
        int cy = (idx / cols) % rows;
        return new Vector4(cx / (float)cols, cy / (float)rows, 1f / cols, 1f / rows);
    }

    public bool AnyTrackAnimated
    {
        get
        {
            foreach (int k in TrackKeyCounts) if (k > 1) return true;
            return false;
        }
    }

    /// <summary>Expected live sprite count at steady state.</summary>
    public float SteadyStatePopulation => MathF.Max(EmissionRate, 0f) * MathF.Max(Lifespan, 0f);

    public string BlendName => BlendingType switch
    {
        0 => "opaque",
        1 => "alpha-key",
        2 => "alpha",
        3 => "no-alpha-add",
        4 => "ADD",
        5 => "mod",
        6 => "mod2x",
        _ => $"?{BlendingType}",
    };

    public string TypeName => Shape switch
    {
        ParticleShape.Sphere => "sphere",
        ParticleShape.Spline => "spline",
        _ => "plane",
    };
}

// ════════════════════════════════════════════════════════════════════════════
// M2Reader — parses the vanilla M2 (v256, "MD20") binary
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Reads WoW 1.12.1 (vanilla, build 5875) M2 model files.
///
/// === Header layout (v256) ===
/// 0x000  char[4]   magic = "MD20"
/// 0x004  uint32    version (256)
/// 0x008  M2Array   name
/// 0x010  uint32    globalFlags
/// 0x014  M2Array   globalLoops
/// 0x01C  M2Array   sequences            ← Session O (stride 68)
/// 0x024  M2Array   sequenceIdxHashById
/// 0x02C  M2Array   playableAnimLookup
/// 0x034  M2Array   bones                ← stride 108
/// 0x03C  M2Array   keyBoneLookup
/// 0x044  M2Array   vertices             ← stride 48
/// 0x04C  uint32    nViews
/// 0x050  uint32    ofsViews
/// 0x054  M2Array   colors
/// 0x05C  M2Array   textures
/// 0x064  M2Array   transparency
/// 0x06C  M2Array   textureFlipbooks
/// 0x074  M2Array   uvAnimations
/// 0x07C  M2Array   textureReplace
/// 0x084  M2Array   renderFlags
/// 0x08C  M2Array   boneLookup
/// 0x094  M2Array   textureLookup
/// 0x09C  M2Array   textureUnits
/// 0x0A4  M2Array   transparencyLookup
/// 0x0AC  M2Array   uvAnimationLookup
/// 0x0B4..0x0E8  bounding box / collision data (floats)
/// 0x0EC  M2Array   collisionTriangles
/// 0x0F4  M2Array   collisionVertices
/// 0x0FC  M2Array   collisionNormals
/// 0x104  M2Array   attachments          ← stride 48
/// 0x10C  M2Array   attachmentLookup     (shorts, indexed by semantic ID)
/// ...
///
/// </summary>
public class M2Reader
{
    private const int VERTEX_STRIDE = 48;
    private const int BONE_STRIDE = 108;
    private const int ATTACHMENT_STRIDE = 48;
    private const int SEQUENCE_STRIDE_VANILLA = 68;
    private const int ANIM_BLOCK_STRIDE_VANILLA = 28;
    private const int RANGE_STRIDE = 8;     // 2 × uint32

    /// <summary>
    /// Read the collision arrays at 0x0EC / 0x0F4.
    ///
    ///   0x0EC  count + offset, collision triangle indices (uint16)
    ///   0x0F4  count + offset, collision vertices (3 floats)
    ///   0x0FC  count + offset, collision normals - not needed; the collision
    ///          world derives face normals itself and does not trust winding
    ///
    /// Everything here is optional. A model with no collision hull returns with
    /// empty lists and that is a normal, common outcome.
    /// </summary>
    private static void ParseCollision(byte[] data, M2Model model)
    {
        uint nIndices = ReadUInt32(data, 0x0EC);
        uint ofsIndices = ReadUInt32(data, 0x0F0);
        uint nVertices = ReadUInt32(data, 0x0F4);
        uint ofsVertices = ReadUInt32(data, 0x0F8);

        if (nVertices == 0 || nIndices < 3) return;
        if (ofsVertices == 0 || ofsIndices == 0) return;
        if (ofsVertices + nVertices * 12 > data.Length) return;
        if (ofsIndices + nIndices * 2 > data.Length) return;

        for (int i = 0; i < nVertices; i++)
        {
            int o = (int)ofsVertices + i * 12;
            model.CollisionVertices.Add(new Vector3(
                BitConverter.ToSingle(data, o),
                BitConverter.ToSingle(data, o + 4),
                BitConverter.ToSingle(data, o + 8)));
        }

        for (int i = 0; i < nIndices; i++)
            model.CollisionIndices.Add(BitConverter.ToUInt16(data, (int)ofsIndices + i * 2));
    }

    public static M2Model? Parse(byte[] data)
    {
        if (data == null || data.Length < 0x110) return null;

        try
        {
            var magic = Encoding.ASCII.GetString(data, 0, 4);
            if (magic != "MD20") return null;

            var model = new M2Model
            {
                Version = ReadUInt32(data, 0x04)
            };

            // Vanilla only (v256). WotLK (264+) splits .skin into external files
            // AND uses 8-byte PACK_QUATERNION for rotation keys (vs vanilla's
            // 16-byte unpacked float Quaternion) — out of scope for this reader.
            if (model.Version >= 264) return null;

            // ── Name ────────────────────────────────────────────────────────
            uint nName = ReadUInt32(data, 0x08);
            uint ofsName = ReadUInt32(data, 0x0C);
            if (nName > 0 && ofsName > 0 && ofsName + nName <= data.Length)
                model.Name = Encoding.ASCII.GetString(data, (int)ofsName, (int)nName).TrimEnd('\0');

            ParseGlobalSequences(data, ReadUInt32(data, 0x014), ReadUInt32(data, 0x018), model);

            // ── Sequences (Session O) ───────────────────────────────────────
            // Parsed BEFORE bones, because bone TRS tracks reference sequence
            // index by position — knowing the sequence count up front lets us
            // size the Ranges array sanely.
            ParseSequences(data, ReadUInt32(data, 0x01C), ReadUInt32(data, 0x020), model);

            // ── Bones (with animation tracks — Session O) ───────────────────
            uint nBones = ReadUInt32(data, 0x034);
            uint ofsBones = ReadUInt32(data, 0x038);
            ParseBones(data, nBones, ofsBones, model);

            uint nKeyBoneLookup = ReadUInt32(data, 0x03C);
            uint ofsKeyBoneLookup = ReadUInt32(data, 0x040);
            ParseKeyBoneLookup(data, nKeyBoneLookup, ofsKeyBoneLookup, model);

            // ── Collision mesh (the model's own, not the render geometry) ───
            ParseCollision(data, model);

            // ── Vertices ────────────────────────────────────────────────────
            uint nVertices = ReadUInt32(data, 0x044);
            uint ofsVertices = ReadUInt32(data, 0x048);
            if (nVertices == 0 || ofsVertices == 0 || ofsVertices >= data.Length)
                return null;
            if (!ParseVertices(data, nVertices, ofsVertices, model))
                return null;

            // ── Views (vanilla = inlined; we always read view 0) ────────────
            uint nViews = ReadUInt32(data, 0x04C);
            uint ofsViews = ReadUInt32(data, 0x050);
            if (nViews == 0 || ofsViews == 0 || ofsViews >= data.Length)
                return null;
            if (!ParseInlinedView(data, ofsViews, model))
                return null;

            // ── Textures + lookups + render flags + transparency ────────────
            ParseTextures(data, ReadUInt32(data, 0x05C), ReadUInt32(data, 0x060), model);
            ParseTextureLookup(data, ReadUInt32(data, 0x094), ReadUInt32(data, 0x098), model);
            ParseRenderFlags(data, ReadUInt32(data, 0x084), ReadUInt32(data, 0x088), model);
            ParseParticleEmitters(data, ReadUInt32(data, 0x13C), ReadUInt32(data, 0x140), model,
                ReadUInt32(data, 0x034), ReadUInt32(data, 0x038),
                ReadUInt32(data, 0x01C), ReadUInt32(data, 0x020));
            ParseTransparencyStaticAlphas(data,
                ReadUInt32(data, 0x064), ReadUInt32(data, 0x068), model);
            ParseTransparencyLookup(data,
                ReadUInt32(data, 0x0A4), ReadUInt32(data, 0x0A8), model);

            // ── Attachments ─────────────────────────────────────────────────
            uint nAttachments = ReadUInt32(data, 0x104);
            uint ofsAttachments = ReadUInt32(data, 0x108);
            ParseAttachments(data, nAttachments, ofsAttachments, model);

            uint nAttachmentLookup = ReadUInt32(data, 0x10C);
            uint ofsAttachmentLookup = ReadUInt32(data, 0x110);
            ParseAttachmentLookup(data, nAttachmentLookup, ofsAttachmentLookup, model);

            return model.IsValid ? model : null;
        }
        catch
        {
            return null;
        }
    }

    // ── Vertices ────────────────────────────────────────────────────────────
    //
    // 48-byte M2Vertex:
    //   +0   float[3] position
    //  +12   uint8[4] boneWeights  (sum = 255)
    //  +16   uint8[4] boneIndices
    //  +20   float[3] normal
    //  +32   float[2] uv0
    //  +40   float[2] uv1
    //
    // Coordinate transform: WoW (Z-up, +X forward, +Y right) → glTF (Y-up,
    // -Z forward, +X right): (x, y, z) → (x, z, -y). 
    private static bool ParseVertices(byte[] data, uint count, uint offset, M2Model model)
    {
        if (offset + count * VERTEX_STRIDE > data.Length) return false;

        model.Vertices.Capacity = (int)count;
        for (uint i = 0; i < count; i++)
        {
            int off = (int)(offset + i * VERTEX_STRIDE);

            float px = ReadFloat(data, off + 0);
            float py = ReadFloat(data, off + 4);
            float pz = ReadFloat(data, off + 8);

            byte bw0 = data[off + 12], bw1 = data[off + 13], bw2 = data[off + 14], bw3 = data[off + 15];
            byte bi0 = data[off + 16], bi1 = data[off + 17], bi2 = data[off + 18], bi3 = data[off + 19];

            float nx = ReadFloat(data, off + 20);
            float ny = ReadFloat(data, off + 24);
            float nz = ReadFloat(data, off + 28);

            float u = ReadFloat(data, off + 32);
            float v = ReadFloat(data, off + 36);

            model.Vertices.Add(new M2Vertex
            {
                PosX = px,
                PosY = pz,
                PosZ = -py,
                NormX = nx,
                NormY = nz,
                NormZ = -ny,
                TexU = u,
                TexV = v,
                BoneWeight0 = bw0,
                BoneWeight1 = bw1,
                BoneWeight2 = bw2,
                BoneWeight3 = bw3,
                BoneIndex0 = bi0,
                BoneIndex1 = bi1,
                BoneIndex2 = bi2,
                BoneIndex3 = bi3,
            });
        }

        return model.Vertices.Count > 0;
    }

    // ── Sequences (Session O) ───────────────────────────────────────────────
    //
    // Vanilla AnimationSequenceM2 layout (68 bytes):
    //   +0   uint16 id              (AnimationData.dbc index)
    //   +2   uint16 variationId
    //   +4   uint32 startTimestamp  (ms on shared anim timeline)
    //   +8   uint32 endTimestamp
    //  +12   float  movespeed       (skipped)
    //  +16   uint32 flags           (bit 0x20 = looping)
    //  +20   uint16 frequency       (skipped)
    //  +22   uint16 padding
    //  +24   uint32 minimumRepetitions (skipped)
    //  +28   uint32 maximumRepetitions (skipped)
    //  +32   uint32 blendTime       (ms; the cross-fade duration into this clip)
    //  +36   M2Box  bounds          (24 bytes, skipped)
    //  +60   float  boundsRadius    (skipped)
    //  +64   int16  nextAnimationId (skipped — sequence chaining is TODO)
    //  +66   uint16 aliasNextId     (skipped)
    private static void ParseSequences(byte[] data, uint count, uint offset, M2Model model)
    {
        if (count == 0 || offset == 0) return;
        if (offset + count * SEQUENCE_STRIDE_VANILLA > data.Length) return;

        model.Sequences.Capacity = (int)count;
        for (uint i = 0; i < count; i++)
        {
            int off = (int)(offset + i * SEQUENCE_STRIDE_VANILLA);
            model.Sequences.Add(new M2Sequence
            {
                AnimationId = ReadUInt16(data, off + 0),
                VariationId = ReadUInt16(data, off + 2),
                StartTimestamp = ReadUInt32(data, off + 4),
                EndTimestamp = ReadUInt32(data, off + 8),
                MoveSpeed = ReadFloat(data, off + 12),
                Flags = ReadUInt32(data, off + 16),
                BlendTimeMs = ReadUInt32(data, off + 32),
            });
        }
    }

    // ── Bones ───────────────────────────────────────────────────────────────
    //
    // ModelBoneM2<vanilla> layout (108 bytes — verified empirically
    //
    //   +0    int32   keyBoneId
    //   +4    uint32  flags
    //   +8    int16   parentBone
    //  +10    uint16  submeshId
    //  +12    AnimationBlockM2<Vector3>     translation   (28 bytes)
    //  +40    AnimationBlockM2<Quaternion>  rotation      (28 bytes)
    //  +68    AnimationBlockM2<Vector3>     scale         (28 bytes)
    //  +96    float[3] pivot                              (12 bytes)
    //
    // CRITICAL: vanilla stores rotation keys as unpacked Vector4 (4 floats =
    // 16 bytes per key), NOT as PACK_QUATERNION (4 int16s = 8 bytes per key).
    // The packed format was introduced in TBC. the 8-byte PACK_QUATERNION path is only
    // reached on TBC+. Getting this wrong would produce garbage rotations
    // that look superficially valid (since the byte pattern overlaps).
    private static void ParseGlobalSequences(byte[] data, uint count, uint offset, M2Model model)
    {
        if (count == 0 || offset == 0) return;
        if (offset + count * 4 > data.Length) return;

        model.GlobalSequenceDurations.Capacity = (int)count;
        for (uint i = 0; i < count; i++)
            model.GlobalSequenceDurations.Add(ReadUInt32(data, (int)(offset + i * 4)));
    }

    private static void ParseBones(byte[] data, uint count, uint offset, M2Model model)
    {
        if (count == 0 || offset == 0) return;
        if (offset + count * BONE_STRIDE > data.Length) return;

        int sequenceCount = model.Sequences.Count;

        model.Bones.Capacity = (int)count;
        for (uint i = 0; i < count; i++)
        {
            int off = (int)(offset + i * BONE_STRIDE);

            int keyBoneId = (int)ReadUInt32(data, off + 0);
            uint flags = ReadUInt32(data, off + 4);
            short parent = (short)ReadUInt16(data, off + 8);
            ushort submeshId = ReadUInt16(data, off + 10);

            // Pivot at +96, with Z-up → Y-up swap 
            float px = ReadFloat(data, off + 96);
            float py = ReadFloat(data, off + 100);
            float pz = ReadFloat(data, off + 104);
            var pivot = new Vector3(px, pz, -py);

            // ── Animation tracks (Session O) ────────────────────────────────
            //
            //   Translation: (x, y, z)    → (x, z, -y)
            //   Rotation:    (x, y, z, w) → (x, z, -y, w)   ← axis swap mirrors position
            //   Scale:       (x, y, z)    → (x, z, y)        (magnitudes, no sign flip)
            //
            // === Why not WMV's fix_quaternion ===
            // WMV uses (qx,qy,qz,qw) → (-qx,-qz,qy,qw). That works in WMV
            // because its matrix builder is column-major OpenGL with a
            // specific quaternion-to-matrix sign convention. When we tried
            // that mapping the body assembled correctly but every joint
            // rotated the wrong way (right hand rotation produced left
            // hand motion etc.) — classic handedness mismatch between
            // M2's source axes and glTF's destination axes.
            //
            // The mapping below applies the SAME axis swap to the quat's
            // imaginary part as is applied to position vectors. That is
            // the textbook way to transport a rotation through a
            // coordinate-system change: if T maps positions, then a
            // rotation R becomes T · R · T⁻¹. For an axis-swap T (which
            // is its own inverse here, since (x,z,-y) applied twice gives
            // (x,-y,-z)... actually it isn't self-inverse, but the
            // imaginary-part-only swap is the correct sandwich product
            // for unit quats representing pure rotations under this kind
            // of basis change). w stays put because it encodes angle, not
            // axis.
            //
            // If joints STILL rotate backward after this change, the
            // alternative is to negate w as well (Option 1 in the Session O
            // handoff "Possible bugs" section).
            var translation = ParseAnimTrack<Vector3>(
                data, off + 12, sequenceCount,
                keyStride: 12,
                readKey: (d, o) =>
                {
                    float x = ReadFloat(d, o + 0);
                    float y = ReadFloat(d, o + 4);
                    float z = ReadFloat(d, o + 8);
                    return new Vector3(x, z, -y);
                });

            var rotation = ParseAnimTrack<Vector4>(
                data, off + 40, sequenceCount,
                keyStride: 16,
                readKey: (d, o) =>
                {
                    float qx = ReadFloat(d, o + 0);
                    float qy = ReadFloat(d, o + 4);
                    float qz = ReadFloat(d, o + 8);
                    float qw = ReadFloat(d, o + 12);
                    return new Vector4(qx, qz, -qy, qw);
                });

            var scale = ParseAnimTrack<Vector3>(
                data, off + 68, sequenceCount,
                keyStride: 12,
                readKey: (d, o) =>
                {
                    float x = ReadFloat(d, o + 0);
                    float y = ReadFloat(d, o + 4);
                    float z = ReadFloat(d, o + 8);
                    return new Vector3(x, z, y);
                });

            model.Bones.Add(new M2Bone
            {
                KeyBoneId = keyBoneId,
                Flags = flags,
                ParentBone = parent,
                SubmeshId = submeshId,
                Pivot = pivot,
                Translation = translation,
                Rotation = rotation,
                Scale = scale,
            });
        }
    }

    // ── Animation track parser (Session O) ──────────────────────────────────
    //
    // Reads one vanilla AnimationBlockM2<T> (28-byte struct) into an
    // M2AnimTrack<T>:
    //
    //   +0   uint16  interpolationType
    //   +2   int16   globalSequence
    //   +4   M2Array ranges
    //  +12   M2Array timestamps
    //  +20   M2Array keys
    //
    // For each sequence the model has, there's a corresponding AnimationRange
    // entry at ranges[sequenceIdx] indicating which slice of timestamps[] and
    // keys[] belongs to that sequence. See M2AnimTrack class doc.
    //
    // The readKey delegate handles per-T-type byte parsing PLUS coordinate
    // conversion in one step. Caller pre-computes the appropriate Z-up→Y-up
    // transform for T (translation, rotation, and scale each use different
    // transforms — see ParseBones).
    private delegate T KeyReader<T>(byte[] data, int offset);

    private static M2AnimTrack<T> ParseAnimTrack<T>(
        byte[] data, int blockOffset, int sequenceCount,
        int keyStride, KeyReader<T> readKey) where T : struct
    {
        var track = new M2AnimTrack<T>();

        if (blockOffset + ANIM_BLOCK_STRIDE_VANILLA > data.Length)
            return track;

        track.InterpolationType = ReadUInt16(data, blockOffset + 0);
        track.GlobalSequence = (short)ReadUInt16(data, blockOffset + 2);

        uint nRanges = ReadUInt32(data, blockOffset + 4);
        uint ofsRanges = ReadUInt32(data, blockOffset + 8);
        uint nTimestamps = ReadUInt32(data, blockOffset + 12);
        uint ofsTimestamps = ReadUInt32(data, blockOffset + 16);
        uint nKeys = ReadUInt32(data, blockOffset + 20);
        uint ofsKeys = ReadUInt32(data, blockOffset + 24);

        // Ranges: one entry per sequence. Stride 8 (2 × uint32).
        // We DON'T cap at sequenceCount because some character M2s appear to
        // have a sentinel/extra range — let it through, callers index by
        // sequence and out-of-range access falls through to "no animation".
        if (nRanges > 0 && ofsRanges > 0 &&
            ofsRanges + nRanges * RANGE_STRIDE <= data.Length)
        {
            track.Ranges.Capacity = (int)nRanges;
            for (uint i = 0; i < nRanges; i++)
            {
                int o = (int)(ofsRanges + i * RANGE_STRIDE);
                track.Ranges.Add(new AnimationRange
                {
                    Start = ReadUInt32(data, o + 0),
                    End = ReadUInt32(data, o + 4),
                });
            }
        }

        // Timestamps: uint32 ms positions. Shared across all sequences.
        if (nTimestamps > 0 && ofsTimestamps > 0 &&
            ofsTimestamps + nTimestamps * 4 <= data.Length)
        {
            track.Timestamps.Capacity = (int)nTimestamps;
            for (uint i = 0; i < nTimestamps; i++)
                track.Timestamps.Add(ReadUInt32(data, (int)(ofsTimestamps + i * 4)));
        }

        // Keys: T per entry. Caller delegates parse + transform.
        if (nKeys > 0 && ofsKeys > 0 &&
            ofsKeys + nKeys * keyStride <= data.Length)
        {
            track.Keys.Capacity = (int)nKeys;
            for (uint i = 0; i < nKeys; i++)
                track.Keys.Add(readKey(data, (int)(ofsKeys + i * keyStride)));
        }

        // Sanity: timestamps and keys must have the same count. If they don't, the track is malformed — treat as empty so
        // UsesSequence returns false rather than crashing on misaligned reads.
        if (track.Timestamps.Count != track.Keys.Count)
        {
            track.Timestamps.Clear();
            track.Keys.Clear();
            track.Ranges.Clear();
        }

        return track;
    }

    private static void ParseKeyBoneLookup(byte[] data, uint count, uint offset, M2Model model)
    {
        if (count == 0 || offset == 0) return;
        if (offset + count * 2 > data.Length) return;

        for (uint i = 0; i < count; i++)
        {
            short v = (short)ReadUInt16(data, (int)(offset + i * 2));
            model.KeyBoneLookup.Add(v);
        }
    }

    // ── Attachments ─────────────────────────────────────────────────────────
    //
    // 48-byte M2Attachment:
    //   +0   uint32 id              (semantic attachment ID, e.g. 1 = HandRight)
    //   +4   uint32 boneIndex
    //   +8   float[3] position      (MODEL SPACE — see SkinnedGlbWriter Session L)
    //  +20   AnimationBlockM2<bool> animateAttached (28 bytes — skipped)
    private static void ParseAttachments(byte[] data, uint count, uint offset, M2Model model)
    {
        if (count == 0 || offset == 0) return;
        if (offset + count * ATTACHMENT_STRIDE > data.Length) return;

        model.Attachments.Capacity = (int)count;
        for (uint i = 0; i < count; i++)
        {
            int off = (int)(offset + i * ATTACHMENT_STRIDE);

            uint id = ReadUInt32(data, off + 0);
            uint boneIdx = ReadUInt32(data, off + 4);

            float px = ReadFloat(data, off + 8);
            float py = ReadFloat(data, off + 12);
            float pz = ReadFloat(data, off + 16);

            var pos = new Vector3(px, pz, -py);

            model.Attachments.Add(new M2Attachment
            {
                Id = id,
                BoneIndex = boneIdx,
                Position = pos,
            });
        }
    }

    private static void ParseAttachmentLookup(byte[] data, uint count, uint offset, M2Model model)
    {
        if (count == 0 || offset == 0) return;
        if (offset + count * 2 > data.Length) return;

        for (uint i = 0; i < count; i++)
        {
            short v = (short)ReadUInt16(data, (int)(offset + i * 2));
            model.AttachmentLookup.Add(v);
        }
    }

    // ── Inlined view ────────────────────────────────────────────────────────
    private static bool ParseInlinedView(byte[] data, uint viewOffset, M2Model model)
    {
        if (viewOffset + 44 > data.Length) return false;

        int off = (int)viewOffset;

        uint nLocalVerts = ReadUInt32(data, off + 0);
        uint ofsLocalVerts = ReadUInt32(data, off + 4);
        uint nTriIndices = ReadUInt32(data, off + 8);
        uint ofsTriIndices = ReadUInt32(data, off + 12);
        uint nSubmeshes = ReadUInt32(data, off + 24);
        uint ofsSubmeshes = ReadUInt32(data, off + 28);
        uint nBatches = ReadUInt32(data, off + 32);
        uint ofsBatches = ReadUInt32(data, off + 36);

        if (nLocalVerts == 0 || ofsLocalVerts == 0 || ofsLocalVerts + nLocalVerts * 2 > data.Length)
            return false;
        if (nTriIndices == 0 || ofsTriIndices == 0 || ofsTriIndices + nTriIndices * 2 > data.Length)
            return false;

        var localVertexMap = new ushort[nLocalVerts];
        for (uint i = 0; i < nLocalVerts; i++)
            localVertexMap[i] = ReadUInt16(data, (int)(ofsLocalVerts + i * 2));

        model.Indices.Capacity = (int)nTriIndices;
        for (uint i = 0; i < nTriIndices; i++)
        {
            ushort localIdx = ReadUInt16(data, (int)(ofsTriIndices + i * 2));
            model.Indices.Add(localIdx < nLocalVerts ? localVertexMap[localIdx] : (ushort)0);
        }

        if (nSubmeshes > 0 && ofsSubmeshes > 0 && ofsSubmeshes + nSubmeshes * 32 <= data.Length)
        {
            for (uint i = 0; i < nSubmeshes; i++)
            {
                int sOff = (int)(ofsSubmeshes + i * 32);
                model.Submeshes.Add(new M2Submesh
                {
                    Id = ReadUInt16(data, sOff + 0),
                    VertexStart = ReadUInt16(data, sOff + 4),
                    VertexCount = ReadUInt16(data, sOff + 6),
                    IndexStart = ReadUInt16(data, sOff + 8),
                    IndexCount = ReadUInt16(data, sOff + 10),
                });
            }
        }

        if (nBatches > 0 && ofsBatches > 0 && ofsBatches + nBatches * 24 <= data.Length)
        {
            for (uint i = 0; i < nBatches; i++)
            {
                int bOff = (int)(ofsBatches + i * 24);
                model.Batches.Add(new M2Batch
                {
                    Flags = data[bOff + 0],
                    PriorityPlane = unchecked((sbyte)data[bOff + 1]),
                    ShaderId = ReadUInt16(data, bOff + 2),
                    SubmeshIndex = ReadUInt16(data, bOff + 4),
                    GeosetIndex = ReadUInt16(data, bOff + 6),
                    ColorIndex = (short)ReadUInt16(data, bOff + 8),
                    MaterialIndex = ReadUInt16(data, bOff + 10),
                    MaterialLayer = ReadUInt16(data, bOff + 12),
                    TextureCount = ReadUInt16(data, bOff + 14),
                    TextureIndex = ReadUInt16(data, bOff + 16),
                    TextureTransformIndex = ReadUInt16(data, bOff + 18),
                    TextureWeightIndex = ReadUInt16(data, bOff + 20),
                });
            }
        }

        return model.Indices.Count >= 3;
    }

    private static void ParseTextures(byte[] data, uint count, uint offset, M2Model model)
    {
        if (count == 0 || offset == 0) return;
        const int TEX_SIZE = 16;
        if (offset + count * TEX_SIZE > data.Length) return;

        for (uint i = 0; i < count; i++)
        {
            int tOff = (int)(offset + i * TEX_SIZE);
            uint type = ReadUInt32(data, tOff);
            uint flags = ReadUInt32(data, tOff + 4);
            uint nFilename = ReadUInt32(data, tOff + 8);
            uint ofsFilename = ReadUInt32(data, tOff + 12);

            string filename = "";
            if (nFilename > 1 && ofsFilename > 0 && ofsFilename + nFilename <= data.Length)
                filename = Encoding.ASCII.GetString(data, (int)ofsFilename, (int)nFilename).TrimEnd('\0');

            model.Textures.Add(new M2TextureRef { Type = type, Flags = flags, Filename = filename });
        }
    }

    private static void ParseTextureLookup(byte[] data, uint count, uint offset, M2Model model)
    {
        if (count == 0 || offset == 0) return;
        if (offset + count * 2 > data.Length) return;
        for (uint i = 0; i < count; i++)
            model.TextureLookup.Add(ReadUInt16(data, (int)(offset + i * 2)));
    }

    private static void ParseRenderFlags(byte[] data, uint count, uint offset, M2Model model)
    {
        if (count == 0 || offset == 0) return;
        const int RENDERFLAG_STRIDE = 4;
        if (offset + count * RENDERFLAG_STRIDE > data.Length) return;

        model.RenderFlags.Capacity = (int)count;
        for (uint i = 0; i < count; i++)
        {
            int off = (int)(offset + i * RENDERFLAG_STRIDE);
            model.RenderFlags.Add(new M2RenderFlag
            {
                Flags = ReadUInt16(data, off + 0),
                BlendingMode = ReadUInt16(data, off + 2),
            });
        }
    }

    // ── Transparency static alphas (Session N — see GlbWriter docs) ─────────
    //
    // Same AnimationBlockM2 wire format as bone TRS tracks (28 bytes), but
    // we only care about keys[0] of the first sequence for static evaluation.
    // The full Session N decode lives here verbatim from the prior version.
    private static void ParseTransparencyStaticAlphas(byte[] data, uint count, uint offset, M2Model model)
    {
        if (count == 0 || offset == 0) return;
        if (offset + count * ANIM_BLOCK_STRIDE_VANILLA > data.Length) return;

        model.TransparencyStaticAlphas.Capacity = (int)count;
        for (uint i = 0; i < count; i++)
        {
            int off = (int)(offset + i * ANIM_BLOCK_STRIDE_VANILLA);

            uint nKeys = ReadUInt32(data, off + 20);
            uint ofsKeys = ReadUInt32(data, off + 24);

            float alpha = 1.0f;

            if (nKeys > 0 && ofsKeys > 0 && ofsKeys + 2 <= data.Length)
            {
                short firstKey = (short)ReadUInt16(data, (int)ofsKeys);
                alpha = firstKey / 32767f;
                if (alpha < 0f) alpha = 0f;
                if (alpha > 1f) alpha = 1f;
            }

            model.TransparencyStaticAlphas.Add(alpha);
        }
    }

    private static void ParseTransparencyLookup(byte[] data, uint count, uint offset, M2Model model)
    {
        if (count == 0 || offset == 0) return;
        if (offset + count * 2 > data.Length) return;

        model.TransparencyLookup.Capacity = (int)count;
        for (uint i = 0; i < count; i++)
            model.TransparencyLookup.Add(ReadUInt16(data, (int)(offset + i * 2)));
    }

    // ── Binary helpers ──────────────────────────────────────────────────────

    private static uint ReadUInt32(byte[] data, int offset)
        => offset + 4 > data.Length ? 0u : BitConverter.ToUInt32(data, offset);

    private static ushort ReadUInt16(byte[] data, int offset)
        => offset + 2 > data.Length ? (ushort)0 : BitConverter.ToUInt16(data, offset);

    private static float ReadFloat(byte[] data, int offset)
        => offset + 4 > data.Length ? 0f : BitConverter.ToSingle(data, offset);
    // ── Particle emitters (PLAN_14 §3) ───────────────────────────────────────

    /// <summary>Derived, not looked up. See the M2ParticleEmitter class summary.</summary>
    private const int PARTICLE_EMITTER_STRIDE = 504;

    /// <summary>Offset of the first of the ten M2Tracks inside an emitter.</summary>
    private const int PARTICLE_TRACK_BASE = 52;

    private const int PARTICLE_TRACK_COUNT = 10;

    private static void ParseParticleEmitters(byte[] data, uint count, uint offset, M2Model model,
                                              uint boneCount, uint boneOffset,
                                              uint seqCount, uint seqOffset)
    {
        if (count == 0 || offset == 0) return;
        if (offset + (long)count * PARTICLE_EMITTER_STRIDE > data.Length)
        {
            Console.WriteLine($"[m2] '{model.Name}': {count} particle emitter(s) at 0x{offset:X} " +
                              $"overruns the file - NOT parsed");
            return;
        }

        for (int i = 0; i < count; i++)
        {
            int o = (int)offset + i * PARTICLE_EMITTER_STRIDE;

            var e = new M2ParticleEmitter
            {
                ParticleId = ReadUInt32(data, o + 0),
                Flags = ReadUInt32(data, o + 4),
                // Z-up -> Y-up, the same swap ParseVertices applies. See the
                // field's summary for what happens without it.
                PosX = BitConverter.ToSingle(data, o + 8),
                PosY = BitConverter.ToSingle(data, o + 16),
                PosZ = -BitConverter.ToSingle(data, o + 12),
                Bone = ReadUInt16(data, o + 20),
                Texture = ReadUInt16(data, o + 22),
                BlendingType = data[o + 40],
                EmitterType = data[o + 41],
                // Real emitter type is the u16 at +0x2a (benilla-formats/particles.rs:642,735);
                // +0x29 is padding. 2 = Sphere, 3 = Spline, else Plane - THIS picks the kernel.
                Shape = ReadUInt16(data, o + 0x2a) switch
                {
                    2 => ParticleShape.Sphere,
                    3 => ParticleShape.Spline,
                    _ => ParticleShape.Plane,
                },
                ParticleType = data[o + 44],
                HeadOrTail = data[o + 45],
                TextureRows = ReadUInt16(data, o + 48),
                TextureCols = ReadUInt16(data, o + 50),
                MidPoint = BitConverter.ToSingle(data, o + 332),
            };

            for (int k = 0; k < 3; k++)
            {
                e.ColorKeys[k] = ReadUInt32(data, o + 336 + k * 4);
                e.ScaleKeys[k] = BitConverter.ToSingle(data, o + 348 + k * 4);
            }

            // Head-quad flipbook cell ramps (benilla-formats/particles.rs:787-796). The ten u16s
            // at +0x168..+0x17b are {headA, headB, tailA, tailB} with a per-segment repeat count
            // WEDGED after each head pair: head A (begin +0x168, end +0x16a), repeat[0] +0x16c,
            // head B (begin +0x16e, end +0x170), repeat[1] +0x172. Read faithfully (a u16 -> float
            // repeat, no coercion): real flame emitters author 1 = one pass across the sheet.
            e.HeadCellBegin[0] = ReadUInt16(data, o + 0x168);
            e.HeadCellEnd[0] = ReadUInt16(data, o + 0x16A);
            e.HeadCellRepeat[0] = ReadUInt16(data, o + 0x16C);
            e.HeadCellBegin[1] = ReadUInt16(data, o + 0x16E);
            e.HeadCellEnd[1] = ReadUInt16(data, o + 0x170);
            e.HeadCellRepeat[1] = ReadUInt16(data, o + 0x172);

            // The ten tracks. Static evaluation only, exactly like
            // TransparencyStaticAlphas: one key is a constant and is read, more
            // than one is reported by count and left for the runtime. Every
            // InstancePortal track is a constant.
            var values = new float[PARTICLE_TRACK_COUNT];
            for (int t = 0; t < PARTICLE_TRACK_COUNT; t++)
            {
                int to = o + PARTICLE_TRACK_BASE + t * ANIM_BLOCK_STRIDE_VANILLA;
                uint nKeys = ReadUInt32(data, to + 20);
                uint ofsKeys = ReadUInt32(data, to + 24);
                e.TrackKeyCounts[t] = (int)nKeys;
                // `ofsKeys + 4 <= data.Length` would be unchecked uint arithmetic:
                // a misparsed offset near uint.MaxValue wraps to something small,
                // passes, and then (int)ofsKeys is NEGATIVE and BitConverter
                // throws - taking the whole model load down. Subtract instead.
                values[t] = nKeys >= 1 && ofsKeys != 0 && ofsKeys <= (uint)data.Length - 4u
                    ? BitConverter.ToSingle(data, (int)ofsKeys)
                    : 0f;
            }

            e.EmissionSpeed = values[0];
            e.SpeedVariation = values[1];
            e.VerticalRange = values[2];
            e.HorizontalRange = values[3];
            e.Gravity = values[4];
            e.Lifespan = values[5];
            e.EmissionRate = values[6];
            e.EmissionAreaLength = values[7];
            e.EmissionAreaWidth = values[8];
            e.ZSource = values[9];                          // +0x130 track
            e.Drag = o + 0x194 + 4 <= data.Length            // +0x194 plain f32
                ? BitConverter.ToSingle(data, o + 0x194) : 0f;

            // The twinkle block. Guarded because the record tail is the part of the 0x1f8
            // stride we derived rather than read from a spec; a short/garbled record must not
            // take the whole model load down.
            if (o + 0x190 <= data.Length)
            {
                float tsp = BitConverter.ToSingle(data, o + 0x180);
                float tpc = BitConverter.ToSingle(data, o + 0x184);
                float tmn = BitConverter.ToSingle(data, o + 0x188);
                float tmx = BitConverter.ToSingle(data, o + 0x18C);
                if (float.IsFinite(tsp) && float.IsFinite(tpc) &&
                    float.IsFinite(tmn) && float.IsFinite(tmx))
                {
                    e.TwinkleSpeed = tsp;
                    e.TwinklePercent = tpc;
                    e.TwinkleMin = tmn;
                    e.TwinkleMax = tmx;
                }
            }

            ReadEmitterBoneSpin(data, e, boneCount, boneOffset, seqCount, seqOffset);
            ReadEmitterBoneChain(data, e, boneCount, boneOffset, seqCount, seqOffset);
            model.ParticleEmitters.Add(e);
        }
    }

    /// <summary>
    /// Pull the emitter bone's rotation keys for sequence 0.
    ///
    /// Vanilla's animation block is FLAT - one timestamp list, one key list, and
    /// a `ranges` array giving the [first, last] index belonging to each
    /// sequence. That is the pre-WotLK shape; the nested array-of-arrays came
    /// later, and reading it that way here produces keys that are not unit
    /// quaternions, which is exactly how this was checked.
    ///
    /// The keys are four FLOATS. Packed int16 quaternions are also a later
    /// format - the same probe reads 1/18 unit quaternions as int16 and 18/18 as
    /// float, which is not a close call.
    /// </summary>
    private static void ReadEmitterBoneSpin(
        byte[] data, M2ParticleEmitter e,
        uint boneCount, uint boneOffset, uint seqCount, uint seqOffset)
    {
        if (e.Bone >= boneCount || boneOffset == 0) return;

        long boneAt = boneOffset + (long)e.Bone * BONE_STRIDE;
        long track = boneAt + 40;                       // +40 = rotation block
        if (track + ANIM_BLOCK_STRIDE_VANILLA > data.Length) return;

        uint nRanges = ReadUInt32(data, (int)track + 4);
        uint ofsRanges = ReadUInt32(data, (int)track + 8);
        uint nTimes = ReadUInt32(data, (int)track + 12);
        uint ofsTimes = ReadUInt32(data, (int)track + 16);
        uint nKeys = ReadUInt32(data, (int)track + 20);
        uint ofsKeys = ReadUInt32(data, (int)track + 24);

        if (nKeys < 2 || nTimes < 2 || ofsKeys == 0 || ofsTimes == 0) return;
        if (ofsTimes > (uint)data.Length - nTimes * 4u) return;
        if (ofsKeys > (uint)data.Length - nKeys * 16u) return;

        // Sequence 0's slice. No ranges means "all of them".
        int first = 0, last = (int)Math.Min(nKeys, nTimes) - 1;
        if (nRanges >= 1 && ofsRanges != 0 && ofsRanges + 8 <= data.Length)
        {
            first = (int)ReadUInt32(data, (int)ofsRanges);
            last = (int)ReadUInt32(data, (int)ofsRanges + 4);
        }
        if (first < 0 || last < first) return;
        last = (int)Math.Min(last, Math.Min(nKeys, nTimes) - 1);

        int n = last - first + 1;
        if (n < 2) return;

        var times = new uint[n];
        var keys = new Vector4[n];
        for (int i = 0; i < n; i++)
        {
            int k = first + i;
            times[i] = ReadUInt32(data, (int)ofsTimes + k * 4);
            int ko = (int)ofsKeys + k * 16;
            // Axis swap mirrors the position: (x, y, z, w) -> (x, z, -y, w).
            // A spin left in the M2's own space turns the emission plane about
            // an axis that does not exist downstream.
            keys[i] = new Vector4(
                BitConverter.ToSingle(data, ko),
                BitConverter.ToSingle(data, ko + 8),
                -BitConverter.ToSingle(data, ko + 4),
                BitConverter.ToSingle(data, ko + 12));
        }

        e.BoneRotationTimes = times;
        e.BoneRotationKeys = keys;
        e.BoneRotationLoopMs = TrackLoopMs(data, track);

        // The sequence's own bounds, because the timestamps are ABSOLUTE and
        // need not start at zero: InstancePortal's sequence runs 3333..6667.
        if (seqCount >= 1 && seqOffset != 0 &&
            seqOffset + SEQUENCE_STRIDE_VANILLA <= data.Length)
        {
            e.SequenceStart = ReadUInt32(data, (int)seqOffset + 4);
            e.SequenceEnd = ReadUInt32(data, (int)seqOffset + 8);
        }
        if (e.SequenceEnd <= e.SequenceStart)
        {
            e.SequenceStart = times[0];
            e.SequenceEnd = times[n - 1];
        }
    }

    /// <summary>
    /// Build the emitter's animated bone chain (own bone, then parents) for sequence 0. See
    /// <see cref="M2ParticleEmitter.BoneChain"/> for why an emitter that never moves is not the
    /// same thing as an emitter whose PARTICLES never move.
    ///
    /// Leaves BoneChain empty unless some joint genuinely animates, so every static emitter in
    /// the world keeps its exact previous birth position.
    /// </summary>
    private static void ReadEmitterBoneChain(
        byte[] data, M2ParticleEmitter e,
        uint boneCount, uint boneOffset, uint seqCount, uint seqOffset)
    {
        if (boneCount == 0 || boneOffset == 0 || e.Bone >= boneCount) return;

        // ReadEmitterBoneSpin only sets these when the emitter's OWN bone carries >= 2 rotation
        // keys, and UI_MainMenu's drifting glow bones (32..47) are translation-only - without
        // this the chain would be built and then never sampled (HasBoneMotion false).
        if (e.SequenceEnd <= e.SequenceStart && seqCount >= 1 && seqOffset != 0 &&
            seqOffset + SEQUENCE_STRIDE_VANILLA <= data.Length)
        {
            e.SequenceStart = ReadUInt32(data, (int)seqOffset + 4);
            e.SequenceEnd = ReadUInt32(data, (int)seqOffset + 8);
        }

        var chain = new List<M2ParticleEmitter.BoneNode>();
        int bone = e.Bone;
        bool animates = false;

        // Depth cap AND a visited set: a malformed parent index that points back down the chain
        // would otherwise spin here forever inside model load.
        var seen = new HashSet<int>();
        for (int depth = 0; depth < 16 && bone >= 0 && bone < boneCount && seen.Add(bone); depth++)
        {
            long at = boneOffset + (long)bone * BONE_STRIDE;
            if (at < 0 || at + BONE_STRIDE > data.Length) break;

            var node = new M2ParticleEmitter.BoneNode
            {
                // Z-up -> Y-up, the same swap ParseVertices and the emitter position apply.
                Pivot = new Vector3(
                    ReadFloat(data, (int)at + 96),
                    ReadFloat(data, (int)at + 104),
                    -ReadFloat(data, (int)at + 100)),
            };
            ReadBoneVec3Track(data, at + 12, out node.TransTimes, out node.TransKeys);
            ReadBoneQuatTrack(data, at + 40, out node.RotTimes, out node.RotKeys);
            node.TransLoopMs = TrackLoopMs(data, at + 12);
            node.RotLoopMs = TrackLoopMs(data, at + 40);

            animates |= node.Animates;
            chain.Add(node);
            bone = (short)ReadUInt16(data, (int)at + 8);   // parent
        }

        e.BoneChain = animates ? chain.ToArray() : Array.Empty<M2ParticleEmitter.BoneNode>();
    }

    /// <summary>
    /// Sequence 0's slice of a vanilla animation block: one flat timestamp list, one flat key
    /// list, and a `ranges` array giving [first, last] per sequence. Same shape ReadEmitterBoneSpin
    /// documents; factored out so the translation and rotation tracks share one bounds check.
    /// </summary>
    private static bool TrackSlice(byte[] data, long track, int keySize,
                                   out int first, out int count, out uint ofsTimes, out uint ofsKeys)
    {
        first = 0; count = 0; ofsTimes = 0; ofsKeys = 0;
        if (track < 0 || track + ANIM_BLOCK_STRIDE_VANILLA > data.Length) return false;

        uint nRanges = ReadUInt32(data, (int)track + 4);
        uint ofsRanges = ReadUInt32(data, (int)track + 8);
        uint nTimes = ReadUInt32(data, (int)track + 12);
        ofsTimes = ReadUInt32(data, (int)track + 16);
        uint nKeys = ReadUInt32(data, (int)track + 20);
        ofsKeys = ReadUInt32(data, (int)track + 24);

        if (nKeys < 1 || nTimes < 1 || ofsKeys == 0 || ofsTimes == 0) return false;
        // Sanity before the unsigned subtractions below, which would otherwise WRAP on a
        // misparsed count and let a wild offset through.
        if (nKeys > 1_000_000u || nTimes > 1_000_000u) return false;
        if (ofsTimes > (uint)data.Length - nTimes * 4u) return false;
        if (ofsKeys > (uint)data.Length - nKeys * (uint)keySize) return false;

        int last = (int)Math.Min(nKeys, nTimes) - 1;
        if (nRanges >= 1 && ofsRanges != 0 && ofsRanges + 8 <= data.Length)
        {
            first = (int)ReadUInt32(data, (int)ofsRanges);
            last = (int)ReadUInt32(data, (int)ofsRanges + 4);
        }
        if (first < 0 || last < first) return false;
        last = Math.Min(last, (int)Math.Min(nKeys, nTimes) - 1);
        if (last < first) return false;

        count = last - first + 1;
        return true;
    }

    /// <summary>
    /// A track's loop period in ms: the duration of the GLOBAL SEQUENCE it declares (gseq, an i16
    /// at track+2), or 0 when it declares none and rides the playing sequence instead. The global
    /// sequence table is count@0x14 / offset@0x18 - read off the header here rather than threaded
    /// through every caller, because a track is meaningless without the model it came from.
    ///
    /// See M2ParticleEmitter.TrackTime for why this matters: UI_MainMenu's animated bones are all
    /// on global sequence 0 (13333 ms) inside a 40000 ms sequence.
    /// </summary>
    private static float TrackLoopMs(byte[] data, long track)
    {
        if (track < 0 || track + 4 > data.Length) return 0f;
        short gseq = (short)ReadUInt16(data, (int)track + 2);
        if (gseq < 0) return 0f;

        uint count = ReadUInt32(data, 0x14);
        uint offset = ReadUInt32(data, 0x18);
        if (count == 0 || offset == 0 || (uint)gseq >= count) return 0f;

        long at = offset + (long)gseq * 4;
        if (at < 0 || at + 4 > data.Length) return 0f;
        uint duration = ReadUInt32(data, (int)at);
        return duration > 0u ? duration : 0f;
    }

    private static void ReadBoneVec3Track(byte[] data, long track, out uint[] times, out Vector3[] keys)
    {
        times = []; keys = [];
        if (!TrackSlice(data, track, 12, out int first, out int n, out uint ofsTimes, out uint ofsKeys))
            return;

        times = new uint[n];
        keys = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            int k = first + i;
            times[i] = ReadUInt32(data, (int)ofsTimes + k * 4);
            int ko = (int)ofsKeys + k * 12;
            keys[i] = new Vector3(
                BitConverter.ToSingle(data, ko),
                BitConverter.ToSingle(data, ko + 8),
                -BitConverter.ToSingle(data, ko + 4));
        }
    }

    private static void ReadBoneQuatTrack(byte[] data, long track, out uint[] times, out Vector4[] keys)
    {
        times = []; keys = [];
        if (!TrackSlice(data, track, 16, out int first, out int n, out uint ofsTimes, out uint ofsKeys))
            return;

        times = new uint[n];
        keys = new Vector4[n];
        for (int i = 0; i < n; i++)
        {
            int k = first + i;
            times[i] = ReadUInt32(data, (int)ofsTimes + k * 4);
            int ko = (int)ofsKeys + k * 16;
            keys[i] = new Vector4(
                BitConverter.ToSingle(data, ko),
                BitConverter.ToSingle(data, ko + 8),
                -BitConverter.ToSingle(data, ko + 4),
                BitConverter.ToSingle(data, ko + 12));
        }
    }
}

public readonly record struct M2PortraitCamera(
    float FieldOfView,
    float FarClip,
    float NearClip,
    Vector3 Position,
    Vector3 Target,
    float Roll);
