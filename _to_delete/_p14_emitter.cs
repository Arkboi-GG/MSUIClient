// (excerpt of Formats/M2Reader.cs, namespace MSUIClient.Formats, usings: System.Numerics, System.Text)

═══
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
public class M2ParticleEmitter
{
    public uint ParticleId { get; set; }
    public uint Flags { get; set; }

    /// <summary>Emitter origin in the owning bone's space.</summary>
    public float PosX, PosY, PosZ;

    /// <summary>Index into the model's bone list. The emitter rides this bone.</summary>
    public ushort Bone { get; set; }

    /// <summary>Index into the model's TEXTURES array - NOT through TextureLookup.</summary>
    public ushort Texture { get; set; }

    /// <summary>0 opaque, 1 alpha-key, 2 alpha, 3 no-alpha-add, 4 ADD, 5 mod, 6 mod2x.</summary>
    public byte BlendingType { get; set; }

    /// <summary>0 plane, 1 sphere, 2 spline, 3 bone.</summary>
    public byte EmitterType { get; set; }

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
    public float ZSource { get; set; }

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
        0 => "opaque", 1 => "alpha-key", 2 => "alpha", 3 => "no-alpha-add",
        4 => "ADD", 5 => "mod", 6 => "mod2x", _ => $"?{BlendingType}",
    };

    public string TypeName => EmitterType switch
    {
        0 => "plane", 1 => "sphere", 2 => "spline", 3 => "bone", _ => $"?{EmitterType}",
    };
}

// ════════════════════════════════════════════════════════════════════════════


// ---- and the parse method from the same file ----
    private const int PARTICLE_EMITTER_STRIDE = 504;

    /// <summary>Offset of the first of the ten M2Tracks inside an emitter.</summary>
    private const int PARTICLE_TRACK_BASE = 52;

    private const int PARTICLE_TRACK_COUNT = 10;

    private static void ParseParticleEmitters(byte[] data, uint count, uint offset, M2Model model)
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
                PosX = BitConverter.ToSingle(data, o + 8),
                PosY = BitConverter.ToSingle(data, o + 12),
                PosZ = BitConverter.ToSingle(data, o + 16),
                Bone = ReadUInt16(data, o + 20),
                Texture = ReadUInt16(data, o + 22),
                BlendingType = data[o + 40],
                EmitterType = data[o + 41],
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
                values[t] = nKeys >= 1 && ofsKeys != 0 && ofsKeys + 4 <= data.Length
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
            e.ZSource = values[9];

            model.ParticleEmitters.Add(e);
        }
    }

}
