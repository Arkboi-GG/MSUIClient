using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.OpenGL;
using MSUIClient.Engine;
using MSUIClient.Formats;
using Shader = MSUIClient.Engine.Shader;
using Texture = MSUIClient.Engine.Texture;

namespace MSUIClient.World;

/// <summary>
/// Broad clutter categories, read from a ground-effect model's name code — the
/// 2-3 letter tag just before the trailing number (ElwGra01 -> Grass,
/// ElwRoc01 -> Rock, ApkBus01 -> Bush). Retail hand-curated which of these
/// appeared where — most visibly, it kept road pebbles out of the starting
/// zones — and the raw DBCs don't encode that. A per-kind toggle lets that
/// curation be reproduced by hand instead of scattering everything the data
/// technically allows.
/// </summary>
public enum FoliageKind { Grass, Flower, Bush, Rock, Plant, Mushroom, Other }

/// <summary>
/// Ground-effect foliage: the grass tufts, ferns and flowers vanilla scatters on
/// the terrain near the camera. Authentic chain:
///
///   MCLY.EffectId (per texture layer, per chunk)
///     -> GroundEffectTexture.dbc  (up to 4 doodad IDs + weights + density)
///        -> GroundEffectDoodad.dbc (the grass M2 model path)
///
/// For each terrain cell near the camera we find the dominant texture layer (the
/// one with the highest alpha), read its ground-effect id, and scatter density-
/// many little M2s at random position / yaw / scale on the terrain surface.
///
/// Rendering reuses the doodad pipeline exactly - one interleaved VBO (pos3 +
/// normal3 + uv2) per model, a per-instance mat4 as four vec4 attributes at
/// locations 3..6 (divisor 1), drawn with DrawElementsInstanced. Positions are
/// camera-relative for float precision. Grass gets its own shader (grass.vert/
/// frag) for wind sway, distance fade and alpha-cutout.
/// </summary>
public sealed class FoliageRenderer : IDisposable
{
    private const int FloatsPerVertex = 8;   // pos(3) + normal(3) + uv(2)

    private sealed class Batch
    {
        public int IndexStart;
        public int IndexCount;
        public Texture? Texture;
    }

    private sealed class GrassModel : IDisposable
    {
        public uint Vao, Vbo, Ebo, InstanceVbo;
        public List<Batch> Batches = [];
        public int TriangleCount;
        private GL? _gl;
        public void Attach(GL gl) => _gl = gl;
        public void Dispose()
        {
            if (_gl is null) return;
            _gl.DeleteVertexArray(Vao);
            _gl.DeleteBuffer(Vbo);
            _gl.DeleteBuffer(Ebo);
            _gl.DeleteBuffer(InstanceVbo);
        }
    }

    // Model-space (M2 is Y-up) -> WoW world (Z-up). Inverse of the M2 reader's
    // WoW->Y-up conversion: (x, y, z) -> (x, -z, y).
    private static readonly Matrix4x4 YUpToZUp = new(
        1, 0, 0, 0,
        0, 0, 1, 0,
        0, -1, 0, 0,
        0, 0, 0, 1);

    private readonly GL _gl;
    private readonly ClientConfig _config;
    private Shader? _shader;

    private readonly Dictionary<string, GrassModel?> _models = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Texture?> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<GrassModel, List<Matrix4x4>> _instances = [];
    private readonly List<Matrix4x4> _relBuffer = [];

    private GroundEffectDoodadTable? _doodads;
    private GroundEffectTextureTable? _recipes;

    private Vector2 _lastScatterXY;
    private bool _hasScattered;
    private int _missing;
    private readonly HashSet<string> _loggedMisses = new(StringComparer.OrdinalIgnoreCase);

    public bool Enabled { get; set; } = true;
    public int InstanceCount { get; private set; }
    public int ModelCount => _models.Count(m => m.Value is not null);
    public long TrianglesLastFrame { get; private set; }

    // ── Cost, split (2026-07-25) ────────────────────────────────────────────
    //
    // Program.cs timed Scatter and Render together as one
    // _foliageRenderMilliseconds. They are unrelated jobs on completely
    // different schedules: Render runs every frame and costs very little,
    // Scatter runs roughly once a second while walking and rebuilds the entire
    // resident set from scratch. One number over two schedules cannot be read -
    // the same mistake SYSTEM_STREAMING 1.2 already records three times.

    /// <summary>The last full re-scatter. Zero on the frames that skip it.</summary>
    /// <summary>
    /// Cost of the most recent scatter. STICKY - it keeps the last value across
    /// the many frames that do not scatter, which is what a panel wants and what
    /// a per-frame phase split must never be given. Use
    /// <see cref="ScatterMillisecondsThisFrame"/> for the latter.
    /// </summary>
    public double ScatterMilliseconds { get; private set; }

    /// <summary>
    /// Cost of the scatter that ran THIS frame, or 0 if none did.
    ///
    /// WHY BOTH EXIST. Scatter runs about once a second while walking and is a
    /// full rebuild; every other frame returns at the throttle. Program.cs was
    /// copying the sticky value into the frame's phase split every frame, so a
    /// 33 ms frame reported "rescatter 2347.0" - a number larger than the frame
    /// containing it - and HitchRecorder.DominantPhase, which ranks the phases
    /// by cost, therefore labelled EVERY hitch "foliage-rescatter" until the
    /// next scatter replaced the number. The real cause of those frames was
    /// present at 28.8 ms against 5.9 ms of GPU.
    ///
    /// An instrument that names the wrong cause is worse than no instrument,
    /// because it is believed. Handbook 8.7 - a phase timer that lies quietly.
    /// </summary>
    public double ScatterMillisecondsThisFrame { get; private set; }

    /// <summary>Drawing the instances. Every frame, normally small.</summary>
    public double DrawMilliseconds { get; private set; }

    /// <summary>Cells examined and candidate tufts rolled by the last scatter.</summary>
    public int ScatterCells { get; private set; }
    public int ScatterCandidates { get; private set; }

    /// <summary>Scatters performed this session. The rate is the cost driver.</summary>
    public int ScatterCount { get; private set; }

    /// <summary>
    /// Tiles the last scatter skipped because their ADT was not parsed yet.
    /// Non-zero means the next frame retries rather than blocking on a parse.
    /// </summary>
    public int DeferredTiles { get; private set; }

    public bool DbcsReady => _recipes is not null && _recipes.Count > 0;
    public int EffectCount => _recipes?.Count ?? 0;

    /// <summary>Force the next frame to re-scatter (after a coverage knob changes).</summary>
    public void ForceRescatter() => _hasScattered = false;

    // ---------------- live tuning knobs ----------------
    public float Radius { get; set; } = 45f;          // scatter/draw radius (yards)
    public float DensityScale { get; set; } = 0.5f;   // multiplies the DBC density
    public int MaxPerCell { get; set; } = 6;          // cap doodads per ~4yd cell

    /// <summary>
    /// Decide each 8x8 cell's texture layer from the MCNK 0x40 map the artists
    /// baked in, instead of guessing it by sampling the alpha maps at the cell
    /// centre. This is what the retail client does and it is the whole reason
    /// grass never creeps onto the Northshire cobblestone - those cells name the
    /// road layer, whose recipe holds one pebble and no grass at all.
    /// Off = the old alpha-sampling guess, kept for A/B comparison.
    /// </summary>
    public bool UseCellLayerMap { get; set; } = true;

    /// <summary>
    /// Honour the MCNK 0x50 noEffectDoodad bitmap - a hand-authored per-cell
    /// "place nothing here". In Azeroth_32_48 (Northshire) it covers 303 cells,
    /// 195 of them road, and it is the second half of why the road reads clean.
    /// </summary>
    public bool UseNoDoodadMask { get; set; } = true;

    /// <summary>Cells skipped by the no-doodad mask on the last scatter.</summary>
    public int MaskedCells { get; private set; }

    /// <summary>
    /// Don't scatter into cells the MCNK holes field cut away. Those quads are
    /// not drawn and have no ground under them - they are the doorway the
    /// artists carved so a dungeon WMO's entrance is reachable. Scattering
    /// there is what puts shrubs growing through the mine's wooden beams.
    /// </summary>
    public bool SkipHoles { get; set; } = true;

    /// <summary>Cells skipped because the terrain there is a hole.</summary>
    public int HoleCells { get; private set; }
    public int MaxInstances { get; set; } = 24000;    // hard cap
    public float RescatterDistance { get; set; } = 8f;// rescatter after moving this far
    public float Scale { get; set; } = 1.0f;          // global size multiplier
    public float ScaleJitter { get; set; } = 0.25f;   // +/- random size
    public float WindStrength { get; set; } = 0.06f;
    public float WindSpeed { get; set; } = 1.4f;
    /// <summary>
    /// Derive the fade window from <see cref="Radius"/> instead of using the
    /// two sliders below.
    ///
    /// THIS DEFAULTS ON BECAUSE THE SLIDERS LIE OTHERWISE. FadeEnd was a fixed
    /// 45 yd while Radius went to 120, so raising Radius scattered grass that
    /// grass.frag then alpha-faded to nothing - "the slider does nothing past
    /// about 30 yards" (it starts thinning at FadeStart=30, which is exactly
    /// where the effect became invisible). Coverage and visibility were two
    /// knobs for one intent. Untick to tune the window by hand.
    /// </summary>
    public bool LinkFadeToRadius { get; set; } = true;

    /// <summary>Where thinning begins, as a fraction of Radius, when linked.</summary>
    public float FadeStartFraction { get; set; } = 0.66f;

    public float FadeStart { get; set; } = 30f;
    public float FadeEnd { get; set; } = 45f;

    /// <summary>Fade window actually sent to the shader this frame.</summary>
    public float EffectiveFadeEnd => LinkFadeToRadius ? Radius : FadeEnd;

    /// <summary>Fade window actually sent to the shader this frame.</summary>
    public float EffectiveFadeStart
        => LinkFadeToRadius ? Radius * MathF.Max(0f, MathF.Min(1f, FadeStartFraction)) : FadeStart;
    public float AlphaCutoff { get; set; } = 0.4f;
    public float Brightness { get; set; } = 1.0f;

    // atmosphere, pushed each frame
    public Vector3 SunDirection { get; set; } = Vector3.UnitZ;
    public Vector3 SunColor { get; set; } = Vector3.One;
    public float SunIntensity { get; set; } = 1f;
    public Vector3 AmbientColor { get; set; } = new(0.5f, 0.5f, 0.5f);
    public float AmbientIntensity { get; set; } = 0.6f;
    public Vector3 FogColor { get; set; } = new(0.56f, 0.71f, 0.85f);
    public float FogStart { get; set; } = 350f;
    public float FogEnd { get; set; } = 777f;
    public float Time { get; set; }

    // ---------------- per-kind curation ----------------
    // One visibility toggle and one thin-out factor (0..1, a keep-probability)
    // per clutter kind, plus a live placed-count for the HUD. Changing any of
    // them forces a re-scatter, same as the coverage knobs.
    private static readonly int KindCount = Enum.GetValues<FoliageKind>().Length;
    private readonly bool[] _kindEnabled = new bool[KindCount];
    private readonly float[] _kindDensity = new float[KindCount];
    private readonly int[] _kindInstances = new int[KindCount];

    public bool KindEnabled(FoliageKind k) => _kindEnabled[(int)k];
    public void SetKindEnabled(FoliageKind k, bool on)
    {
        if (_kindEnabled[(int)k] == on) return;
        _kindEnabled[(int)k] = on;
        _hasScattered = false;   // rebuild so the change is visible immediately
    }

    public float KindDensity(FoliageKind k) => _kindDensity[(int)k];
    public void SetKindDensity(FoliageKind k, float keep)
    {
        keep = Math.Clamp(keep, 0f, 1f);
        if (_kindDensity[(int)k] == keep) return;
        _kindDensity[(int)k] = keep;
        _hasScattered = false;
    }

    public int KindInstances(FoliageKind k) => _kindInstances[(int)k];

    /// <summary>
    /// Map a ground-effect model path to a broad clutter kind by its name code —
    /// the letters just before the trailing number ("ElwRoc01" -> "roc" -> Rock).
    /// Zone-prefixed variants that carry an extra letter (Durotar's "DurIRo01")
    /// still land on the right 3-letter tail. Anything unrecognised is Other.
    /// </summary>
    public static FoliageKind Classify(string modelPath)
    {
        string name = Path.GetFileNameWithoutExtension(modelPath);
        int end = name.Length;
        while (end > 0 && char.IsDigit(name[end - 1])) end--;
        int start = Math.Max(0, end - 3);
        string code = name[start..end].ToLowerInvariant();
        return code switch
        {
            "gra" or "igr" => FoliageKind.Grass,
            "flo" or "ifl" => FoliageKind.Flower,
            "bus" or "ibu" or "scr" or "shr" => FoliageKind.Bush,
            "roc" or "iro" => FoliageKind.Rock,
            "wea" or "pla" or "tho" or "cre" or "vin" or "sap" or "bra" => FoliageKind.Plant,
            "mus" or "fun" or "spo" => FoliageKind.Mushroom,
            _ => FoliageKind.Other,
        };
    }

    public FoliageRenderer(GL gl, ClientConfig config)
    {
        _gl = gl;
        _config = config;
        for (int i = 0; i < KindCount; i++) { _kindEnabled[i] = true; _kindDensity[i] = 1f; }
    }

    public void LoadShaders(string shaderDir)
    {
        _shader = Shader.FromFiles(_gl,
            Path.Combine(shaderDir, "grass.vert"),
            Path.Combine(shaderDir, "grass.frag"));
    }

    /// <summary>Load the two ground-effect DBCs from the client MPQs.</summary>
    public void LoadDbcs()
    {
        var dd = AdtTerrainReader.ReadFileFromMpqs(_config.ClientDataPath, GroundEffectDoodadTable.MpqPath);
        var dt = AdtTerrainReader.ReadFileFromMpqs(_config.ClientDataPath, GroundEffectTextureTable.MpqPath);
        if (dd is null || dt is null)
        {
            Console.WriteLine("[foliage] GroundEffect DBC(s) not found in the MPQs - foliage disabled");
            return;
        }
        _doodads = GroundEffectDoodadTable.Parse(dd);
        if (_doodads is null) { Console.WriteLine("[foliage] GroundEffectDoodad parse failed"); return; }
        _recipes = GroundEffectTextureTable.Parse(dt, _doodads);
        if (_recipes is null || _recipes.Count == 0)
            Console.WriteLine("[foliage] no usable ground-effect recipes - foliage will be empty");
    }

    /// <summary>
    /// Rebuild the resident grass instances around the camera. Throttled: only
    /// re-scatters after the camera has moved <see cref="RescatterDistance"/>.
    /// </summary>
    public void Scatter(Camera camera, AdtCache adts, IEnumerable<(int col, int row)> tiles, TerrainRenderer terrain)
    {
        // Cleared FIRST, before every early return, so a frame that does not
        // scatter reports zero rather than the last scatter's cost.
        ScatterMillisecondsThisFrame = 0.0;

        if (!Enabled || _shader is null || _recipes is null || _recipes.Count == 0) return;

        var cam2 = new Vector2(camera.Position.X, camera.Position.Y);
        if (_hasScattered && Vector2.DistanceSquared(cam2, _lastScatterXY) < RescatterDistance * RescatterDistance)
            return;
        long scatterStarted = Stopwatch.GetTimestamp();
        _lastScatterXY = cam2;
        _hasScattered = true;

        foreach (var list in _instances.Values) list.Clear();
        Array.Clear(_kindInstances);
        MaskedCells = 0;
        HoleCells = 0;
        ScatterCells = 0;
        ScatterCandidates = 0;
        DeferredTiles = 0;

        int total = 0;
        float radiusSq = Radius * Radius;
        const float cell = AdtTerrainReader.CELL_SIZE;
        float camX = camera.Position.X, camY = camera.Position.Y;

        foreach (var (col, row) in tiles)
        {
            // TryPeek, never Get. AdtCache.Get waits on a pending parse
            // (`return pending.GetAwaiter().GetResult();`) and this runs inside
            // Render on the main thread - the exact latent bug
            // SYSTEM_STREAMING 3.1 lists against FoliageRenderer:270 after the
            // same call cost the WMO ring 61 ms. It measures ~0 today only
            // because these tiles are cache hits.
            //
            // A tile that has not been parsed is skipped and counted, and the
            // next frame retries rather than waiting. TryPeek returning true
            // with a null adt means "known to have no ADT" - an answer, not a
            // miss - so that case must NOT set the retry flag or it would spin
            // forever on ocean tiles.
            if (!adts.TryPeek(col, row, out var adt)) { DeferredTiles++; continue; }
            if (adt?.Chunks is null) continue;

            double originX = (32 - row) * 533.33333;
            double originY = (32 - col) * 533.33333;

            foreach (var chunk in adt.Chunks)
            {
                if (chunk is null || chunk.Layers.Length == 0) continue;

                double chunkX = originX - chunk.IndexY * 8 * cell;
                double chunkY = originY - chunk.IndexX * 8 * cell;

                // Reject whole chunk if its centre is well outside the radius.
                double cxw = chunkX - 4 * cell, cyw = chunkY - 4 * cell;
                double ddx = cxw - camX, ddy = cyw - camY;
                float guard = Radius + 24f;
                if (ddx * ddx + ddy * ddy > guard * guard) continue;

                for (int cy = 0; cy < 8; cy++)
                for (int cx = 0; cx < 8; cx++)
                {
                    // Vanilla decides clutter per cell, not per chunk, and it
                    // reads both answers out of the MCNK header rather than
                    // deriving them. Keep both switchable so the old
                    // alpha-derived behaviour is still one click away.
                    if (UseNoDoodadMask && chunk.NoGroundEffect(cx, cy)) { MaskedCells++; continue; }

                    // A holed cell has no terrain at all - it is the cut the
                    // artists made for a dungeon entrance. SampleHeight now
                    // refuses these too, but rejecting the whole cell up front
                    // is cheaper and gives the HUD something to read.
                    if (SkipHoles && chunk.IsHole(cx, cy)) { HoleCells++; continue; }

                    int dom = UseCellLayerMap && chunk.HasGroundEffectLayerMap
                            ? chunk.GroundEffectLayer(cx, cy)
                            : DominantLayer(chunk, cx, cy);
                    if (dom < 0 || dom >= chunk.Layers.Length) dom = 0;

                    int effect = chunk.Layers[dom].EffectId;
                    if (effect < 0) continue;

                    var recipe = _recipes.Get(effect);
                    if (recipe is null || recipe.Doodads.Length == 0) continue;

                    int perCell = Math.Clamp((int)MathF.Round(recipe.Density * DensityScale), 0, MaxPerCell);
                    if (perCell <= 0) continue;

                    ScatterCells++;
                    ScatterCandidates += perCell;

                    var rng = new Random(HashCode.Combine(col, row, chunk.IndexX, chunk.IndexY, cx, cy));
                    double cellX = chunkX - cy * cell;
                    double cellY = chunkY - cx * cell;

                    for (int i = 0; i < perCell; i++)
                    {
                        // ── Draw EVERY random value for this tuft FIRST ──────
                        //
                        // This ordering is the whole fix for "the grass moves
                        // around as I walk", and it is not a style choice.
                        //
                        // The seed is per cell and deterministic, so the tufts
                        // were supposed to be stable. But the draws used to be
                        // interleaved with the rejection tests, and one of those
                        // tests - the radius check - depends on WHERE THE CAMERA
                        // IS. When it rejected tuft i, the loop skipped the
                        // model, keep, yaw and scale draws for that tuft. So the
                        // stream position at tuft i+1 depended on the camera,
                        // and every remaining tuft in that cell got a different
                        // position, model, rotation and size on every re-scatter.
                        //
                        // Cells fully inside the radius never showed it. Cells
                        // straddling the radius edge reshuffled constantly - and
                        // as you walk, every cell takes its turn at the edge.
                        //
                        // SYSTEM_FOLIAGE 1.1 says "grass that reshuffles when
                        // you turn around is the giveaway that this seeding got
                        // broken". The seeding was fine. The CONSUMPTION was not.
                        //
                        // Rule to keep: the rng stream position must depend only
                        // on (cell, i). Never on the camera, and never on a
                        // toggle. No `continue` may appear above this block.
                        float px = (float)(cellX - rng.NextDouble() * cell);
                        float py = (float)(cellY - rng.NextDouble() * cell);
                        string modelPath = PickWeighted(recipe.Doodads, rng);
                        double keepRoll = rng.NextDouble();
                        float yaw = (float)(rng.NextDouble() * Math.PI * 2.0);
                        float jitter = (float)rng.NextDouble();

                        // ── Rejections below consume nothing ─────────────────
                        //
                        // Ordered cheapest-first, which is also a real saving:
                        // SampleHeight used to run before the kind filter, so
                        // every in-radius candidate paid a height lookup even
                        // when its kind was switched off and it was about to be
                        // discarded.
                        float dxp = px - camX, dyp = py - camY;
                        if (dxp * dxp + dyp * dyp > radiusSq) continue;

                        // Per-kind curation: skip a doodad whose kind is hidden,
                        // and thin the rest by the kind's keep-probability. This
                        // is how a rock-only road recipe ends up placing nothing
                        // when Rock is switched off.
                        var kind = Classify(modelPath);
                        if (!_kindEnabled[(int)kind]) continue;
                        float keep = _kindDensity[(int)kind];
                        if (keep <= 0f || (keep < 1f && keepRoll > keep)) continue;

                        var gm = ResolveModel(modelPath);
                        if (gm is null) continue;

                        float? h = terrain.SampleHeight(px, py);
                        if (h is null) continue;

                        float s = Scale * (1f - ScaleJitter + jitter * ScaleJitter * 2f);

                        var m = YUpToZUp
                              * Matrix4x4.CreateScale(s)
                              * Matrix4x4.CreateRotationZ(yaw)
                              * Matrix4x4.CreateTranslation(px, py, h.Value);

                        if (!_instances.TryGetValue(gm, out var il)) { il = []; _instances[gm] = il; }
                        il.Add(m);
                        _kindInstances[(int)kind]++;

                        if (++total >= MaxInstances) goto done;
                    }
                }
            }
        }

    done:
        InstanceCount = total;
        ScatterCount++;

        // A tile that was not parsed yet leaves a hole in the foliage, and the
        // throttle would otherwise hold that hole until the camera moved
        // another RescatterDistance. Clearing the flag makes the next frame
        // retry - the cost of one extra scatter, versus a visible bald patch.
        if (DeferredTiles > 0) _hasScattered = false;

        ScatterMilliseconds = Stopwatch.GetElapsedTime(scatterStarted).TotalMilliseconds;
        ScatterMillisecondsThisFrame = ScatterMilliseconds;

        Console.WriteLine($"[foliage] scattered {total} grass instance(s) over {_instances.Count(kv => kv.Value.Count > 0)} " +
            $"model(s); {ModelCount} model(s) loaded, {_missing} missing");
        Console.WriteLine($"[foliage]   {ScatterMilliseconds:F1} ms over {ScatterCells} cell(s), " +
            $"{ScatterCandidates} candidate(s) rolled, {total} kept" +
            (DeferredTiles > 0 ? $"  ({DeferredTiles} tile(s) deferred, retrying next frame)" : ""));
    }

    private static int DominantLayer(AdtTerrainReader.McnkChunk chunk, int cx, int cy)
    {
        int px = Math.Clamp(cx * 8 + 4, 0, 63);
        int py = Math.Clamp(cy * 8 + 4, 0, 63);

        int best = 0, bestA = 0, sum = 0;
        for (int li = 1; li < chunk.Layers.Length; li++)
        {
            var a = chunk.Layers[li].AlphaMap;
            if (a is null || a.Length < 64 * 64) continue;
            int val = a[py * 64 + px];
            sum += val;
            if (val > bestA) { bestA = val; best = li; }
        }
        int baseCoverage = 255 - Math.Min(sum, 255);   // layer 0 shows through the rest
        return bestA > baseCoverage ? best : 0;
    }

    private static string PickWeighted((string Model, int Weight)[] doodads, Random rng)
    {
        int total = 0;
        foreach (var d in doodads) total += d.Weight;
        if (total <= 0) return doodads[0].Model;
        int pick = rng.Next(total);
        foreach (var d in doodads)
        {
            if (pick < d.Weight) return d.Model;
            pick -= d.Weight;
        }
        return doodads[^1].Model;
    }

    private GrassModel? ResolveModel(string path)
    {
        if (_models.TryGetValue(path, out var cached)) return cached;

        byte[]? bytes = null;
        foreach (var cand in Candidates(path))
        {
            bytes = AdtTerrainReader.ReadFileFromMpqs(_config.ClientDataPath, cand);
            if (bytes is not null) break;
        }

        GrassModel? gm = null;
        string reason = "";
        if (bytes is null)
        {
            reason = "file not found in MPQs";
        }
        else
        {
            var m2 = M2Reader.Parse(bytes);
            if (m2 is null) reason = "M2Reader.Parse returned null (bad magic / version)";
            else if (!m2.IsValid) reason = $"M2 invalid (verts {m2.Vertices.Count}, idx {m2.Indices.Count})";
            else
            {
                gm = BuildModel(m2);
                if (gm is null)
                    reason = $"no drawable batch (batches {m2.Batches.Count}, submeshes {m2.Submeshes.Count}, " +
                             $"texRefs {m2.Textures.Count}, texLookup {m2.TextureLookup.Count})";
            }
        }

        _models[path] = gm;
        if (gm is null)
        {
            _missing++;
            if (_loggedMisses.Add(path))
                Console.WriteLine($"[foliage] model FAILED ('{path}'): {reason}");
        }
        return gm;
    }

    // GroundEffectDoodad stores BARE model filenames ("ElwGra01.mdl"), but the
    // models live under these folders in the MPQs and are .m2 there - not .mdl or
    // .mdx. The overwhelming majority are in World\NoDXT\Detail; a handful sit in
    // World\Detail. Without prepending a folder, every lookup reads from the
    // archive root and misses, so nothing scatters.
    private static readonly string[] FoliageDirs =
    {
        @"World\NoDXT\Detail\",
        @"World\Detail\",
    };

    private static IEnumerable<string> Candidates(string path)
    {
        // As-authored first, in case a DBC ever stores a full path.
        foreach (var p in ExtVariants(path)) yield return p;

        // Bare filename (the real case here): try it under each ground-effect
        // folder. ExtVariants also swaps .mdl/.mdx for the .m2 that is actually
        // in the archive.
        bool bare = !path.Contains('\\') && !path.Contains('/');
        if (bare)
            foreach (var dir in FoliageDirs)
                foreach (var p in ExtVariants(dir + path))
                    yield return p;
    }

    private static IEnumerable<string> ExtVariants(string path)
    {
        yield return path;
        if (path.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase)) yield return path[..^4] + ".m2";
        else if (path.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)) yield return path[..^4] + ".m2";
    }

    private unsafe GrassModel? BuildModel(M2Model m2)
    {
        int vcount = m2.Vertices.Count;
        if (vcount == 0 || m2.Indices.Count < 3) return null;

        var verts = new float[vcount * FloatsPerVertex];
        for (int i = 0; i < vcount; i++)
        {
            var v = m2.Vertices[i];
            int o = i * FloatsPerVertex;
            verts[o + 0] = v.PosX; verts[o + 1] = v.PosY; verts[o + 2] = v.PosZ;
            verts[o + 3] = v.NormX; verts[o + 4] = v.NormY; verts[o + 5] = v.NormZ;
            verts[o + 6] = v.TexU; verts[o + 7] = v.TexV;
        }
        var idx = m2.Indices.ToArray();

        var model = new GrassModel { TriangleCount = idx.Length / 3 };
        model.Attach(_gl);

        model.Vao = _gl.GenVertexArray();
        _gl.BindVertexArray(model.Vao);

        model.Vbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, model.Vbo);
        fixed (float* p = verts)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(verts.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);

        model.Ebo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, model.Ebo);
        fixed (ushort* p = idx)
            _gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(idx.Length * sizeof(ushort)), p, BufferUsageARB.StaticDraw);

        const uint stride = FloatsPerVertex * sizeof(float);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        _gl.EnableVertexAttribArray(2);
        _gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, (void*)(6 * sizeof(float)));

        model.InstanceVbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, model.InstanceVbo);
        const uint instanceStride = 16 * sizeof(float);
        for (uint r = 0; r < 4; r++)
        {
            uint loc = 3 + r;
            _gl.EnableVertexAttribArray(loc);
            _gl.VertexAttribPointer(loc, 4, VertexAttribPointerType.Float, false, instanceStride, (void*)(r * 4 * sizeof(float)));
            _gl.VertexAttribDivisor(loc, 1);
        }

        _gl.BindVertexArray(0);

        foreach (var b in m2.Batches)
        {
            if (b.SubmeshIndex >= m2.Submeshes.Count) continue;
            var sm = m2.Submeshes[b.SubmeshIndex];
            var tex = ResolveTexture(m2, b);
            if (tex is null) continue;   // no texture -> nothing to draw for grass
            model.Batches.Add(new Batch { IndexStart = sm.IndexStart, IndexCount = sm.IndexCount, Texture = tex });
        }

        if (model.Batches.Count == 0) { model.Dispose(); return null; }
        return model;
    }

    private Texture? ResolveTexture(M2Model m2, M2Batch b)
    {
        int ti = b.TextureIndex;
        if (ti < 0 || ti >= m2.TextureLookup.Count) return null;
        int real = m2.TextureLookup[ti];
        if (real < 0 || real >= m2.Textures.Count) return null;
        string path = m2.Textures[real].Filename;
        if (string.IsNullOrEmpty(path)) return null;

        if (_textures.TryGetValue(path, out var cached)) return cached;

        var px = AdtTerrainReader.ReadBlpPixels(_config.ClientDataPath, path);
        Texture? tex = px is null ? null : Texture.From2D(_gl, px.Value.bgra, px.Value.width, px.Value.height);
        _textures[path] = tex;
        return tex;
    }

    public unsafe void Render(Camera camera)
    {
        TrianglesLastFrame = 0;
        DrawMilliseconds = 0;
        if (!Enabled || _shader is null || _instances.Count == 0) return;

        long drawStarted = Stopwatch.GetTimestamp();
        _shader.Use();
        _shader.Set("uViewProjection", camera.RelativeViewProjection);
        _shader.Set("uCameraOrigin", camera.Position);
        _shader.Set("uTime", Time);
        _shader.Set("uWindStrength", WindStrength);
        _shader.Set("uWindSpeed", WindSpeed);
        _shader.Set("uSunDirection", SunDirection);
        _shader.Set("uSunColor", SunColor);
        _shader.Set("uSunIntensity", SunIntensity);
        _shader.Set("uAmbientColor", AmbientColor);
        _shader.Set("uAmbientIntensity", AmbientIntensity);
        _shader.Set("uFogStart", FogStart);
        _shader.Set("uFogEnd", FogEnd);
        _shader.Set("uFogColor", FogColor);
        _shader.Set("uBrightness", Brightness);
        _shader.Set("uFadeStart", EffectiveFadeStart);
        _shader.Set("uFadeEnd", EffectiveFadeEnd);
        _shader.Set("uAlphaCutoff", AlphaCutoff);
        _shader.Set("uTexture", 0);

        // Opaque alpha-cutout: depth test + write, no blend. Grass cards are
        // two-sided, so face culling stays off.
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);
        _gl.Disable(EnableCap.CullFace);

        var eye = camera.Position;
        foreach (var (model, list) in _instances)
        {
            if (list.Count == 0) continue;

            _relBuffer.Clear();
            foreach (var m in list)
            {
                var rm = m;
                rm.M41 -= eye.X; rm.M42 -= eye.Y; rm.M43 -= eye.Z;
                _relBuffer.Add(rm);
            }

            _gl.BindVertexArray(model.Vao);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, model.InstanceVbo);
            var span = CollectionsMarshal.AsSpan(_relBuffer);
            fixed (Matrix4x4* p = span)
                _gl.BufferData(BufferTargetARB.ArrayBuffer,
                    (nuint)(span.Length * sizeof(Matrix4x4)), p, BufferUsageARB.StreamDraw);

            uint ic = (uint)_relBuffer.Count;
            foreach (var b in model.Batches)
            {
                if (b.Texture is null) continue;
                b.Texture.Bind(0);
                _gl.DrawElementsInstanced(PrimitiveType.Triangles, (uint)b.IndexCount,
                    DrawElementsType.UnsignedShort, (void*)(b.IndexStart * sizeof(ushort)), ic);
                TrianglesLastFrame += (long)(b.IndexCount / 3) * ic;
            }
        }

        _gl.Enable(EnableCap.CullFace);
        _gl.BindVertexArray(0);
        DrawMilliseconds = Stopwatch.GetElapsedTime(drawStarted).TotalMilliseconds;
    }

    public void Dispose()
    {
        foreach (var m in _models.Values) m?.Dispose();
        _models.Clear();
        foreach (var t in _textures.Values) t?.Dispose();
        _textures.Clear();
        _instances.Clear();
    }
}
