using System.Numerics;

namespace MSUIClient.World;

/// <summary>
/// The procedural sky-cloud coverage field - a faithful C# port of the reference
/// client's cloud kernel (WoW.exe 0x6cffc0 noise + 0x6cfb00 colour pass), via
/// benilla's byte-exact transcription (benilla-app/src/clouds/{kernel,tables}.rs,
/// diffed against WoW.exe and wow-re crates/lighting/src/clouds.rs).
///
/// The real client keeps a scrolling 128x128 byte tile of cloud coverage,
/// regenerated in 32-row bands at ~10 Hz: 4-octave toroidal value noise
/// (lacunarity 2, persistence 0.5) -> raw byte, thresholded by the authored
/// Light.dbc cloud density C (T = trunc((1-C)*255)) and shaped through a fixed
/// 256-byte tone curve. Each band regen ends with the COLOUR PASS: the coverage
/// bytes become RGBA texels (gradient slope*p+base from IntBand 11/12, plus a
/// sun-aligned glow from IntBand 10, alpha = the coverage byte) - and THAT image
/// is what gets sampled by the sky.
///
/// MSUIClient renders the tile in the SCREEN-SPACE sky pass (SkyRenderer +
/// sky.frag) rather than benilla's camera-centred dome mesh: the same colored
/// tile is uploaded as a texture and sampled per pixel by the azimuthal
/// projection of the view ray (project_cells, ported to sky.frag). The kernel
/// here owns everything CPU-side; SkyRenderer owns the upload + the draw.
///
/// Deviations from the bytes are benilla's, all in never-hit/non-visual domains
/// (acos clamp above ~70 deg; toroidal LUT wrap at the measure-zero u==1 edge;
/// the gradient table uses the reference's MSVC-LCG formula with a fixed seed 1;
/// the colour buffer seeds alpha 0 so an unprimed field never flashes white).
/// </summary>
public sealed class CloudField
{
    /// <summary>Tile side at SkyCloudLOD 0 (cols = 128 &lt;&lt; LOD; we implement LOD 0).</summary>
    public const int Cols = 128;
    /// <summary>log2(Cols) - the row-pitch shift the sampler uses.</summary>
    public const int Shift = 7;
    /// <summary>Rows regenerated per fire (default 32).</summary>
    public const int RowsPerTick = 32;
    /// <summary>Octave count (constant 4).</summary>
    public const int Octaves = 4;
    /// <summary>Regen countdown reset (0.1 s) - the ~10 Hz cadence.</summary>
    public const float RegenPeriod = 0.1f;

    /// <summary>Per-octave lattice frequencies, LOD 0 row of the base table ((16 &gt;&gt; LOD) &lt;&lt; oct).</summary>
    private static readonly ushort[] BaseFreq = { 16, 32, 64, 128 };

    /// <summary>1/255 as the binary stores it (0x3b808081).</summary>
    private static readonly float Inv255 = BitConverter.UInt32BitsToSingle(0x3b808081u);

    // ── Per-fire inputs to the colour pass (0x6cfb00's per-frame setup), resolved
    //    by the caller from the authored Light.dbc cloud bands + the sun. ─────────
    public struct CloudFrame
    {
        /// <summary>Sun-glow palette (IntBand sub-10), sRGB 0..1.</summary>
        public Vector3 Sun;
        /// <summary>Gradient slope (IntBand sub-11).</summary>
        public Vector3 Slope;
        /// <summary>Gradient base (IntBand sub-12).</summary>
        public Vector3 GBase;
        /// <summary>Storm blend bcc (weather); 0 = clear. Feeds the glow z-bias and the dim.</summary>
        public float Bcc;
        /// <summary>Camera-&gt;glow-body direction in the tile frame (+Y up).</summary>
        public Vector3 GlowDir;
        /// <summary>The glow day-envelope factor (0 disables the sun glow, e.g. at night).</summary>
        public float GlowTrack;

        public bool Equals(in CloudFrame o) =>
            Sun == o.Sun && Slope == o.Slope && GBase == o.GBase &&
            Bcc == o.Bcc && GlowDir == o.GlowDir && GlowTrack == o.GlowTrack;
    }

    // ── Static tables (dumped from the binary / rebuilt by its exact builders) ──

    // The permutation table (0x86f2d0) - 256 bytes, always indexed & 0xff.
    private static readonly byte[] Perm =
    {
        225, 155, 210, 108, 175, 199, 221, 144, 203, 116,  70, 213,  69, 158,  33, 252,
          5,  82, 173, 133, 222, 139, 174,  27,   9,  71,  90, 246,  75, 130,  91, 191,
        169, 138,   2, 151, 194, 235,  81,   7,  25, 113, 228, 159, 205, 253, 134, 142,
        248,  65, 224, 217,  22, 121, 229,  63,  89, 103,  96, 104, 156,  17, 201, 129,
         36,   8, 165, 110, 237, 117, 231,  56, 132, 211, 152,  20, 181, 111, 239, 218,
        170, 163,  51, 172, 157,  47,  80, 212, 176, 250,  87,  49,  99, 242, 136, 189,
        162, 115,  44,  43, 124,  94, 150,  16, 141, 247,  32,  10, 198, 223, 255,  72,
         53, 131,  84,  57, 220, 197,  58,  50, 208,  11, 241,  28,   3, 192,  62, 202,
         18, 215, 153,  24,  76,  41,  15, 179,  39,  46,  55,   6, 128, 167,  23, 188,
        106,  34, 187, 140, 164,  73, 112, 182, 244, 195, 227,  13,  35,  77, 196, 185,
         26, 200, 226, 119,  31, 123, 168, 125, 249,  68, 183, 230, 177, 135, 160, 180,
         12,   1, 243, 148, 102, 166,  38, 238, 251,  37, 240, 126,  64,  74, 161,  40,
        184, 149, 171, 178, 101,  66,  29,  59, 146,  61, 254, 107,  42,  86, 154,   4,
        236, 232, 120,  21, 233, 209,  45,  98, 193, 114,  78,  19, 206,  14, 118, 127,
         48,  79, 147,  85,  30, 207, 219,  54,  88, 234, 190, 122,  95,  67, 143, 109,
        137, 214, 145,  93,  92, 100, 245,   0, 216, 186,  60,  83, 105,  97, 204,  52,
    };

    // The tone curve (0xce91d8), built once by dn_tone_curve (gamma 0.96). Frozen bytes.
    private static readonly byte[] Curve =
    {
          0,   6,  12,  18,  23,  29,  34,  40,  45,  50,  55,  60,  65,  69,  74,  78,
         82,  87,  91,  95,  98, 102, 106, 110, 113, 116, 120, 123, 126, 129, 132, 135,
        138, 141, 144, 147, 149, 152, 154, 157, 159, 161, 164, 166, 168, 170, 172, 174,
        176, 178, 180, 182, 183, 185, 187, 188, 190, 192, 193, 195, 196, 197, 199, 200,
        202, 203, 204, 205, 206, 208, 209, 210, 211, 212, 213, 214, 215, 216, 217, 218,
        219, 220, 220, 221, 222, 223, 224, 224, 225, 226, 227, 227, 228, 229, 229, 230,
        230, 231, 232, 232, 233, 233, 234, 234, 235, 235, 236, 236, 237, 237, 237, 238,
        238, 239, 239, 239, 240, 240, 240, 241, 241, 241, 242, 242, 242, 243, 243, 243,
        243, 244, 244, 244, 245, 245, 245, 245, 245, 246, 246, 246, 246, 247, 247, 247,
        247, 247, 247, 248, 248, 248, 248, 248, 248, 249, 249, 249, 249, 249, 249, 249,
        249, 250, 250, 250, 250, 250, 250, 250, 250, 250, 251, 251, 251, 251, 251, 251,
        251, 251, 251, 251, 251, 252, 252, 252, 252, 252, 252, 252, 252, 252, 252, 252,
        252, 252, 252, 252, 252, 252, 253, 253, 253, 253, 253, 253, 253, 253, 253, 253,
        253, 253, 253, 253, 253, 253, 253, 253, 253, 253, 253, 253, 253, 253, 253, 253,
        253, 253, 253, 254, 254, 254, 254, 254, 254, 254, 254, 254, 254, 254, 254, 254,
        254, 254, 254, 254, 254, 254, 254, 254, 254, 254, 254, 254, 254, 254, 254, 254,
    };

    private readonly float[] _gradient = BuildGradient();
    private readonly float[] _fade = BuildFade();

    // The gradient table build: gradient[i] = 1 - 2*rand()/32767 with MSVC's LCG.
    // Seed fixed at 1 (CRT default) for run-to-run determinism (benilla note).
    private static float[] BuildGradient()
    {
        var g = new float[256];
        uint seed = 1;
        for (int i = 0; i < 256; i++)
        {
            seed = unchecked(seed * 214_013u + 2_531_011u);
            uint r = (seed >> 16) & 0x7fff;
            g[i] = 1.0f - 2.0f * r / 32767.0f;
        }
        return g;
    }

    // The fade table build: the raised-cosine ease 0.5*(1 - cos(i*pi/256)).
    private static float[] BuildFade()
    {
        var f = new float[256];
        for (int i = 0; i < 256; i++)
            f[i] = 0.5f * (1.0f - MathF.Cos(i * MathF.PI / 256.0f));
        return f;
    }

    // ── State ────────────────────────────────────────────────────────────────
    private readonly byte[] _tile = new byte[Cols * Cols];          // coverage bytes (byte/255 = R)
    private readonly float[] _accum = new float[Cols * Cols];       // float accumulation tile
    private readonly float[] _derivX = new float[Cols * Cols];      // shape derivative (glow normal)
    private readonly float[] _derivY = new float[Cols * Cols];
    private readonly float[] _prevrow = new float[Cols];            // previous-row scratch
    private readonly byte[] _rgba = new byte[Cols * Cols * 4];      // colored texels (uploaded)
    private int _scroll;
    private ushort _phase;
    private float _countdown;
    private bool _primed;

    public CloudField()
    {
        // Seed alpha 0 (benilla cosmetic deviation): an unprimed field never flashes white.
        for (int i = 0; i < Cols * Cols; i++)
        {
            _rgba[i * 4 + 0] = 255; _rgba[i * 4 + 1] = 255; _rgba[i * 4 + 2] = 255; _rgba[i * 4 + 3] = 0;
        }
    }

    /// <summary>The colored RGBA texels (Cols*Cols*4, row-major) for the texture upload.</summary>
    public byte[] Rgba => _rgba;

    /// <summary>Raw coverage bytes (byte/255 = R). For tests/diagnostics.</summary>
    public byte[] Tile => _tile;

    /// <summary>Whether the first full rebuild has run.</summary>
    public bool Primed => _primed;

    /// <summary>Test hook: set the noise-space phase (the time axis).</summary>
    public void SetPhase(ushort phase) => _phase = phase;

    private uint P(uint i) => Perm[i & 0xff];

    /// <summary>
    /// Advance the countdown and regenerate one 32-row band when it expires
    /// (the reference's self-throttle). Returns whether the tile changed.
    /// </summary>
    public bool Tick(float dt, float density, in CloudFrame frame)
    {
        _countdown -= dt;
        if (_countdown > 0.0f) return false;
        _countdown = RegenPeriod;
        Regen(density, RowsPerTick, frame);
        _primed = true;
        return true;
    }

    /// <summary>Full-tile rebuild: regenerate every row at once (init/zone/discontinuity).</summary>
    public void Rebuild(float density, in CloudFrame frame)
    {
        _scroll = 0;
        Regen(density, Cols, frame);
        _countdown = RegenPeriod;
        _primed = true;
    }

    /// <summary>Re-run the colour pass over the whole tile without touching the coverage.</summary>
    public void Recolor(in CloudFrame frame) => ColorBand(0, Cols, frame);

    private struct Octave
    {
        public ushort Freq, RowKey, ColKey;
        public float Amp;
        public uint X0, X1, Y0, Y1;
        public float G00, G00d, G10, G10d, G01, G01d, G11, G11d;
        public uint Cached;
    }

    // One regeneration fire: `rows` tile rows starting at _scroll (noise + quantize +
    // the colour pass), then the scroll/phase advance. Named-field transcription of
    // WoW.exe 0x6cffc0 (benilla clouds/kernel.rs::regen).
    private void Regen(float density, int rows, in CloudFrame frame)
    {
        // Threshold refresh: T = trunc((1-C)*255). Density clamped for safety.
        int threshold = (int)((1.0f - Math.Clamp(density, 0.0f, 1.0f)) * 255.0f);

        // Phase-selected permutation slice pair: the time axis.
        uint seed = (uint)(_phase >> 8);
        uint sliceA = P(seed);
        uint sliceB = P(seed + 1);
        double fadeT = _fade[_phase & 0xff];

        int scroll = _scroll;
        var oct = new Octave[Octaves];
        for (int c = 0; c < Octaves; c++)
        {
            ushort freq = BaseFreq[c];
            oct[c] = new Octave
            {
                Freq = freq,
                RowKey = unchecked((ushort)((uint)scroll * freq)),
                ColKey = 0,
                Amp = 1.0f / (1u << c),
                Cached = uint.MaxValue,
            };
        }

        int bandEnd = Math.Min(scroll + rows, Cols);
        Array.Clear(_accum, scroll * Cols, (bandEnd - scroll) * Cols);

        for (int row = 0; row < rows; row++)
        {
            int baseIdx = (scroll + row) * Cols;
            // Per-row lattice setup: hash the row cell through the two time slices;
            // re-seed the column walk from the phase; invalidate the corner cache.
            for (int o = 0; o < Octaves; o++)
            {
                uint bv = (uint)(oct[o].RowKey >> 8);
                oct[o].X0 = P(sliceA + bv);
                oct[o].X1 = P(sliceA + bv + 1);
                oct[o].Y0 = P(bv + sliceB);
                oct[o].Y1 = P(bv + 1 + sliceB);
                oct[o].ColKey = _phase;
                oct[o].Cached = uint.MaxValue;
            }
            float prevAccum = 0.0f;
            for (int col = 0; col < Cols; col++)
            {
                int cell = baseIdx + col;
                for (int oi = 0; oi < Octaves; oi++)
                {
                    ref Octave o = ref oct[oi];
                    double fadeRow = _fade[o.RowKey & 0xff];
                    uint cz = (uint)(o.ColKey >> 8);
                    if (cz != o.Cached)
                    {
                        o.Cached = cz;
                        uint s0 = o.X0 + cz, s1 = o.X1 + cz, s2 = o.Y0 + cz, s3 = o.Y1 + cz;
                        o.G00 = _gradient[P(s0)]; o.G00d = _gradient[P(s0 + 1)] - o.G00;
                        o.G10 = _gradient[P(s1)]; o.G10d = _gradient[P(s1 + 1)] - o.G10;
                        o.G01 = _gradient[P(s2)]; o.G01d = _gradient[P(s2 + 1)] - o.G01;
                        o.G11 = _gradient[P(s3)]; o.G11d = _gradient[P(s3 + 1)] - o.G11;
                    }
                    double fx = _fade[o.ColKey & 0xff];
                    double v7 = fx * o.G00d + o.G00;
                    double v8 = (float)(fx * o.G01d + o.G01);   // one f32 round-trip (v8)
                    double v7b = (fx * o.G10d + o.G10 - v7) * fadeRow + v7;
                    double v8b = (fx * o.G11d + o.G11 - v8) * fadeRow + v8;
                    o.ColKey = unchecked((ushort)(o.ColKey + o.Freq));
                    double acc = _accum[cell];
                    float stored = (float)(((v8b - v7b) * fadeT + v7b) * o.Amp + acc);
                    _accum[cell] = stored;
                    // The octave-2 derivative leg (glow surface normal). scale = 1 at LOD 0.
                    if (oi == 2)
                    {
                        double scale = (float)(1 << ((Shift - 7) & 0x1f));
                        _derivX[cell] = (float)(((double)prevAccum - stored) * scale);
                        _derivY[cell] = (float)(((double)_prevrow[col] - stored) * scale);
                        prevAccum = stored;
                        _prevrow[col] = stored;
                    }
                }
            }
            for (int o = 0; o < Octaves; o++)
                oct[o].RowKey = unchecked((ushort)(oct[o].RowKey + oct[o].Freq));
        }

        // Palette-quantize the band into the byte tile: the binary's float-bits pack, threshold, tone curve.
        for (int cell = scroll * Cols; cell < bandEnd * Cols; cell++)
        {
            uint q = BitConverter.SingleToUInt32Bits((float)((double)_accum[cell] * 64.0 + 128.0 + 512.0));
            int idx = (int)((q >> 14) & 0xff) - threshold;
            _tile[cell] = idx >= 0 ? Curve[idx] : (byte)0;
        }

        ColorBand(scroll, rows, frame);

        // Scroll advance with wrap: the wrap bumps the noise-space phase (the time axis moves).
        _scroll += rows;
        if (_scroll >= Cols) { _phase = unchecked((ushort)(_phase + 1)); _scroll = 0; }
    }

    // The colour pass (WoW.exe 0x6cfb00): per cell t = the coverage byte. t==0 copies the
    // previous cell's RGB at alpha 0 (the filtering-friendly hole fill); else the gradient
    // slope*p + gbase, plus the sun-aligned glow sun*(cos*intensity). Channels clamp at 1.
    private void ColorBand(int start, int rows, in CloudFrame frame)
    {
        float zBias = (float)((double)frame.Bcc * 192.0 + 64.0);
        float intensity = (float)((double)frame.GlowTrack * (1.0 - (double)frame.Bcc * 0.75));
        (float su, float sv)? body = null;
        if (intensity > 0f && frame.GlowDir.LengthSquared() > 1e-8f)
        {
            var (scol, srow, _) = SkyProject(frame.GlowDir);
            body = (scol, srow);
        }
        int end = Math.Min(start + rows, Cols);
        for (int row = start; row < end; row++)
        {
            float rowBase = row;
            for (int col = 0; col < Cols; col++)
            {
                int g = row * Cols + col;
                byte t = _tile[g];
                if (t == 0)
                {
                    if (col != 0)
                    {
                        _rgba[g * 4 + 0] = _rgba[(g - 1) * 4 + 0];
                        _rgba[g * 4 + 1] = _rgba[(g - 1) * 4 + 1];
                        _rgba[g * 4 + 2] = _rgba[(g - 1) * 4 + 2];
                        _rgba[g * 4 + 3] = 0;
                    }
                    continue;
                }
                // The angle byte: n = (((255 - t) >> 1) + 0x40) & 0xff in [64, 191].
                uint n = (uint)(byte)(((255 - t) >> 1) + 0x40);
                double p = (double)Inv255 * n;
                double ch0 = (float)((double)frame.Slope.X * p + frame.GBase.X);
                double ch1 = (float)((double)frame.Slope.Y * p + frame.GBase.Y);
                double ch2 = (float)((double)frame.Slope.Z * p + frame.GBase.Z);
                if (body is { } b && intensity > 0.0f)
                {
                    // V = (Su - col, Sv - row, zBias); S = (dx, dy, 1).
                    float vx = (float)((double)b.su - col);
                    float vy = (float)((double)b.sv - rowBase);
                    float vz = zBias;
                    float sx = _derivX[g], sy = _derivY[g];
                    float lenVsq = (float)(((double)vz * vz + (double)vy * vy) + (double)vx * vx);
                    float lenSsq = (float)(((double)sx * sx + (double)sy * sy) + 1.0);
                    double dot = (double)vx * sx + (double)vy * sy + (double)vz;
                    double cosT = dot * ((double)Fisr(lenVsq) * (double)Fisr(lenSsq));
                    if (cosT > 0.0)
                    {
                        double m = cosT * intensity;
                        ch0 = (float)((double)frame.Sun.X * m + ch0);
                        ch1 = (float)((double)frame.Sun.Y * m + ch1);
                        ch2 = (float)((double)frame.Sun.Z * m + ch2);
                    }
                }
                _rgba[g * 4 + 0] = PackChannel(ch0);
                _rgba[g * 4 + 1] = PackChannel(ch1);
                _rgba[g * 4 + 2] = PackChannel(ch2);
                _rgba[g * 4 + 3] = t;
            }
        }
    }

    // ── Projection helpers (also ported to sky.frag for the screen-space sample) ──

    /// <summary>
    /// The azimuthal tile projection: a camera-relative offset d (tile frame, +Y up)
    /// -&gt; fractional tile cell (col, row). LUT centre = zenith, radius grows with the
    /// angle off a +cos(pi/4)-shifted zenith axis, saturating at 45 deg. Null for a
    /// degenerate zero offset.
    /// </summary>
    public static (float col, float row)? ProjectCells(Vector3 d)
    {
        double len = d.Length();
        if (len < 1e-6) return null;
        double quarterPi = MathF.PI / 4.0;
        double c = (double)d.Y + Math.Cos(MathF.PI / 4.0);
        double theta = Math.Acos(Math.Clamp(c / len, -1.0, 1.0));
        double phase = Math.Min(theta, quarterPi) / quarterPi * 0.5;
        double hyp = Math.Sqrt((double)d.X * d.X + (double)d.Z * d.Z);
        double cx, cy;
        if (hyp > 1e-5) { double inv = 1.0 / hyp; cx = inv * d.X; cy = inv * d.Z; }
        else { cx = 0.0; cy = 0.0; }
        return ((float)((cx * phase + 0.5) * Cols), (float)((cy * phase + 0.5) * Cols));
    }

    /// <summary>
    /// The screen-space sky projection used by BOTH the CPU glow placement (above)
    /// and the GPU cloud sampling (sky.frag): a camera-relative sky direction in the
    /// tile frame (+Y up) -&gt; tile cell (col, row) and the radius from centre
    /// (0 zenith .. 0.5 horizon; below-horizon clamps to the rim).
    ///
    /// This DELIBERATELY DEVIATES from benilla's project_cells + dome. benilla
    /// renders the tile on a camera-centred DOME mesh (its UVs are the ring/azimuth
    /// map) and reserves project_cells for the SCALED-offset glare-occlusion sampler
    /// - fed a unit direction, project_cells clamps the whole upper sky to the tile
    /// centre. A screen-space pass needs a real hemisphere-to-disk map, and the only
    /// hard requirement is that the render sampling and the sun-glow placement agree.
    /// They do, by sharing this one azimuthal-equidistant function. (SYSTEM notes:
    /// PLAN_18; project_cells/Coverage are kept for a future glare pass.)
    /// </summary>
    public static (float col, float row, float radius) SkyProject(Vector3 dir)
    {
        float colat = MathF.Acos(Math.Clamp(dir.Y, -1f, 1f));   // 0 at zenith, pi/2 at horizon
        float r = 0.5f * MathF.Min(colat, MathF.PI * 0.5f) / (MathF.PI * 0.5f);
        float horiz = MathF.Sqrt(dir.X * dir.X + dir.Z * dir.Z);
        float nx = horiz > 1e-5f ? dir.X / horiz : 0f;
        float nz = horiz > 1e-5f ? dir.Z / horiz : 0f;
        return ((nx * r + 0.5f) * Cols, (nz * r + 0.5f) * Cols, r);
    }

    /// <summary>Sample coverage R in [0,1] toward a camera-relative offset d (tile frame, +Y up).</summary>
    public float Coverage(Vector3 d)
    {
        var uv = ProjectCells(d);
        if (uv is not { } p) return _tile[(Cols / 2) * Cols + Cols / 2] / 255.0f;
        int col = (int)p.col, row = (int)p.row;
        int cell = ((row & (Cols - 1)) << Shift) + (col & (Cols - 1));
        return _tile[cell] / 255.0f;
    }

    // The binary's integer fast-inverse-sqrt leaf: a one-shot seed, NO Newton step.
    private static float Fisr(float x) =>
        BitConverter.UInt32BitsToSingle(0x5f39_97bbu - ((BitConverter.SingleToUInt32Bits(x) >> 1) & 0x3fff_ffffu));

    // Pack one colour channel to a byte exactly as the binary does: clamp <=1 (no lower
    // clamp), then bits(ch*255 + 512) >> 14 (floor(ch*255) after f32 rounding).
    private static byte PackChannel(double ch)
    {
        double clamped = ch < 1.0 ? ch : 1.0;
        return (byte)(BitConverter.SingleToUInt32Bits((float)(clamped * 255.0 + 512.0)) >> 14);
    }

    // ── The cloud sun-glow day envelope (0xce9ab8, benilla daynight::cloud_glow_track) ──
    // An internal static 8-key track (NOT a Light.dbc band): ~1.0 across the day, notching
    // to 0 at the twilight boundaries. The stored keys are non-monotonic on purpose; the
    // array-order scan makes keys 6/7 unreachable and wraps t>0.9236 back to 1.0.
    private static readonly (float Dp, float V)[] CloudGlowCurve =
    {
        (0.16667f, 1.0f), (0.19444f, 0.0f), (0.20139f, 0.0f), (0.22917f, 1.0f),
        (0.89583f, 1.0f), (0.92361f, 0.0f), (0.88889f, 0.0f), (0.91667f, 1.0f),
    };

    /// <summary>The cloud sun-glow envelope at a game hour (0..24). 1 by day, 0 at twilight notches.</summary>
    public static float GlowTrack(float hours) => InterpDayNight(CloudGlowCurve, WrapDp(hours / 24.0f));

    /// <summary>The glow body pick: the SUN drives the glow in [04:50, 22:10], the moon otherwise.</summary>
    public static bool GlowIsSun(float hours)
    {
        float dp = WrapDp(hours / 24.0f);
        return dp >= 0.201_388_9f && dp <= 0.923_611_1f;
    }

    // Vanilla DayNight::InterpTable: wrap-around linear interpolation over dp in [0,1).
    private static float InterpDayNight((float Dp, float V)[] table, float dp)
    {
        int n = table.Length;
        int a = 0;
        while (a < n && dp > table[a].Dp) a++;
        int lo;
        if (a == n || a == 0) { lo = n - 1; a = a == n ? 0 : a; }
        else { lo = a - 1; }
        float span = table[a].Dp - table[lo].Dp;
        if (span < 0.0f) span += 1.0f;
        float into = dp - table[lo].Dp;
        if (into < 0.0f) into += 1.0f;
        float t = span != 0.0f ? into / span : 0.0f;
        return table[lo].V + t * (table[a].V - table[lo].V);
    }

    private static float WrapDp(float dp)
    {
        dp %= 1.0f;
        return dp < 0.0f ? dp + 1.0f : dp;
    }
}
