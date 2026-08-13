using System.Numerics;
using System.Diagnostics;
using Silk.NET.OpenGL;
using MSUIClient.Engine;
using MSUIClient.Formats;
using Shader = MSUIClient.Engine.Shader; 

namespace MSUIClient.World;

/// <summary>
/// Owns the loaded terrain tiles: loads a block around a position, draws them
/// with frustum culling, and answers ground-height queries.
///
/// No bake step, no manifest, no HTTP. The ADT comes out of the MPQ and goes to
/// the GPU in one pass — this is the whole reason the client is native.
/// </summary>
public sealed class TerrainRenderer : IDisposable
{
    private readonly GL _gl;
    private readonly ClientConfig _config;
    private readonly GpuUploadWorker _uploads;
    private readonly AssetWorkerPool _workers;
    private readonly Dictionary<(int col, int row), TerrainTile> _tiles = [];
    private readonly Dictionary<(int col, int row), Task<PreloadedTile?>> _preloads = [];
    private readonly HashSet<(int col, int row)> _missingPreloads = [];

    /// <summary>
    /// One fully-prepared tile: the GPU package plus the two CPU grids that must
    /// be installed with it. They travel together through the task rather than
    /// sitting in parallel dictionaries, so a worker never writes renderer state
    /// (handbook 5.4) and a grid can never be adopted for the wrong tile.
    /// </summary>
    private sealed class PreloadedTile
    {
        public TerrainTile.Uploaded? Uploaded;
        public float[] Heights = [];
        public float[] InnerHeights = [];
        public byte[] Holes = [];
    }
    private HashSet<(int col, int row)> _desired = [];

    /// <summary>Height grids kept CPU-side for ground queries, keyed like the tiles.</summary>
    private readonly Dictionary<(int col, int row), float[]> _heights = [];

    /// <summary>
    /// 128x128 per-cell INNER (fan-centre) heights, in lockstep with _heights. The render
    /// mesh is 4 triangles per cell fanned around this vertex (TerrainTile), so decal
    /// projection needs it to re-emit geometry exactly coplanar with the drawn ground.
    /// </summary>
    private readonly Dictionary<(int col, int row), float[]> _innerHeights = [];

    /// <summary>
    /// Per-quad hole masks, one per loaded tile, in lockstep with _heights.
    /// The mesh has always skipped holed quads; the height grid never did,
    /// which is why the player could stand on invisible ground across a mine
    /// mouth and climb the mountain instead of walking in.
    /// </summary>
    private readonly Dictionary<(int col, int row), byte[]> _holes = [];
    private int _holeQuadCount;
    private bool _holeCountDirty = true;

    private Shader _shader = null!;

    public const int HeightGridSide = 129;

    /// <summary>Quads per tile edge — one less than the vertex grid.</summary>
    public const int QuadGridSide = HeightGridSide - 1;

    public const float GridSize = 533.33333f;

    public int TileCount => _tiles.Count;
    public int PendingPreloads => _preloads.Count;
    /// <summary>
    /// True when <see cref="UnloadAll"/> can reclaim every queued terrain
    /// package without waiting for worker or upload work. This is a snapshot;
    /// callers must remain on the renderer's owning thread and avoid queueing
    /// more work between this check and the unload.
    /// </summary>
    public bool AllPreloadsCompleted => _preloads.Values.All(static task => task.IsCompleted);
    public Action<int, int>? PreloadDequeued { get; set; }
    public int DrawnLastFrame { get; private set; }
    public int DrawCallsLastFrame { get; private set; }
    public int TrianglesLastFrame { get; private set; }
    public double RenderMilliseconds { get; private set; }
    public void NoteNotRendered() => RenderMilliseconds = 0;
    public int TotalTriangles => _tiles.Values.Sum(t => t.TriangleCount);

    /// <summary>
    /// Keys of the tiles currently loaded. Collision loads exactly this set, so
    /// the two can never disagree about which part of the world exists — a
    /// mismatch would show up as invisible walls or walk-through buildings at
    /// the edge of the block, which is a miserable thing to debug.
    /// </summary>
    public IEnumerable<(int col, int row)> LoadedTiles => _tiles.Keys;

    /// <summary>Texture count on the first loaded tile — a quick HUD sanity read.</summary>
    public int FirstTileTextureCount
        => _tiles.Values.FirstOrDefault()?.Textures?.TextureCount ?? 0;

    /// <summary>0 textured, 1 normals, 2 UVs, 3 flat, 4 splat mask, 5 untextured.</summary>
    public int DebugMode { get; set; }

    /// <summary>
    /// Honour MCNK holes when answering ground queries. Off restores the old
    /// behaviour, where the height grid reported solid ground through a
    /// doorway the mesh had already cut away.
    /// </summary>
    public bool ApplyHoles { get; set; } = true;

    /// <summary>Holed quads across the loaded tiles — a HUD sanity read.</summary>
    public int HoleQuadCount
    {
        get
        {
            if (_holeCountDirty)
            {
                int n = 0;
                foreach (var grid in _holes.Values)
                    foreach (byte b in grid)
                        if (b != 0) n++;
                _holeQuadCount = n;
                _holeCountDirty = false;
            }
            return _holeQuadCount;
        }
    }

    private void SetHoles((int col, int row) key, byte[] grid)
    {
        _holes[key] = grid;
        _holeCountDirty = true;
    }

    private void RemoveHoles((int col, int row) key)
    {
        if (_holes.Remove(key)) _holeCountDirty = true;
    }

    /// <summary>Tileset repeats per chunk. Vanilla is about 8; tune it live.</summary>
    public float TextureScale { get; set; } = 8f;

    public Vector3 SunDirection { get; set; } = Vector3.Normalize(new Vector3(0.45f, 0.35f, 0.82f));
    public Vector3 SunColor { get; set; } = new(1.00f, 0.95f, 0.85f);
    public float SunIntensity { get; set; } = 1.15f;
    public Vector3 AmbientColor { get; set; } = new(0.42f, 0.50f, 0.60f);
    public float AmbientIntensity { get; set; } = 0.85f;
    /// <summary>
    /// Opacity of the artists' MCSH terrain shadow. The 1.12 pixel-shader path
    /// scales the full diffuse modulate by (0.3 * lit + 0.7), i.e. a flat 30%
    /// reduction in authored shadow with no colour tint.
    /// </summary>
    public float AuthoredShadowStrength { get; set; } = 0.3f;
    public Vector3 FogColor { get; set; } = new(0.56f, 0.71f, 0.85f);
    public float FogStart { get; set; } = 350f;
    public float FogEnd { get; set; } = 900f;
    public float VisibilityDistance { get; set; } = float.PositiveInfinity;

    /// <summary>
    /// Frustum-cull per MCNK inside a tile rather than only per tile.
    ///
    /// Off restores the single-draw-per-tile behaviour, which is the A/B: if
    /// terrain ever shows a wedge missing at the screen edge, that is this, and
    /// one click proves it.
    /// </summary>
    public bool ChunkCulling { get; set; } = true;

    public TerrainRenderer(
        GL gl, ClientConfig config, GpuUploadWorker uploads, AssetWorkerPool workers)
    {
        _gl = gl;
        _config = config;
        _uploads = uploads;
        _workers = workers;
    }

    public void LoadShaders(string shaderDir)
    {
        _shader = Shader.FromFiles(_gl,
            Path.Combine(shaderDir, "terrain.vert"),
            Path.Combine(shaderDir, "terrain.frag"));
    }

    /// <summary>Tile index from a WoW position. First number from Y, second from X.</summary>
    public static (int col, int row) TileAt(float worldX, float worldY)
        => ((int)MathF.Floor(32f - worldY / GridSize),
            (int)MathF.Floor(32f - worldX / GridSize));

    /// <summary>World-space centre of a tile.</summary>
    public static Vector2 TileCenter(int col, int row)
        => new((32f - row) * GridSize - GridSize * 0.5f,
               (32f - col) * GridSize - GridSize * 0.5f);

    public static HashSet<(int col, int row)> TileRing(int centreCol, int centreRow, int radius)
    {
        var result = new HashSet<(int col, int row)>();
        for (int dc = -radius; dc <= radius; dc++)
        for (int dr = -radius; dr <= radius; dr++)
        {
            int col = centreCol + dc;
            int row = centreRow + dr;
            if (col is >= 0 and <= 63 && row is >= 0 and <= 63)
                result.Add((col, row));
        }
        return result;
    }

    /// <summary>
    /// Make the GPU/height residency exactly match a tile ring. Shared world
    /// assets live in the other renderers; terrain tiles themselves are cheap
    /// enough to dispose when they leave the ring.
    /// </summary>
    public bool SetResidency(int centreCol, int centreRow, int radius, AdtCache adts)
    {
        var desired = TileRing(centreCol, centreRow, radius);
        _desired = desired;
        bool changed = false;

        foreach (var key in _tiles.Keys.Where(k => !desired.Contains(k)).ToArray())
        {
            _tiles[key].Dispose();
            _tiles.Remove(key);
            _heights.Remove(key);
            _innerHeights.Remove(key);
            RemoveHoles(key);
            changed = true;
        }

        foreach (var (col, row) in desired.OrderBy(k => Math.Abs(k.col - centreCol) + Math.Abs(k.row - centreRow)))
        {
            if (_tiles.ContainsKey((col, row))) continue;

            if (_preloads.TryGetValue((col, row), out var preload))
            {
                if (!preload.IsCompleted) continue;
                _preloads.Remove((col, row));
                PreloadedTile? ready;
                try { ready = preload.GetAwaiter().GetResult(); }
                catch (Exception ex)
                {
                    Console.WriteLine($"[terrain-preload] tile [{col},{row}] failed - {ex.Message}");
                    _missingPreloads.Add((col, row));
                    continue;
                }
                if (ready?.Uploaded is null)
                {
                    // Remember the absence. Without this the tile is re-queued
                    // every frame, because the _preloads entry has just been
                    // removed and nothing else records that it does not exist.
                    _missingPreloads.Add((col, row));
                    continue;
                }
                _tiles[(col, row)] = TerrainTile.Adopt(_gl, ready.Uploaded);
                _heights[(col, row)] = ready.Heights;
                SetHoles((col, row), ready.Holes);
                changed = true;
                continue;
            }

            var adt = adts.Get(col, row);
            var tile = TerrainTile.Load(_gl, adt, _config.ClientDataPath, col, row);
            if (tile is null) continue;

            _tiles[(col, row)] = tile;
            _heights[(col, row)] = BuildHeightGrid(adt);
            SetHoles((col, row), BuildHoleGrid(adt));
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Prepare the lead ring off the render thread and upload it through the
    /// shared GL context. These tiles remain unpublished until residency asks
    /// for them; the render thread only creates their small VAO container.
    ///
    /// EVERY part of the work is off-thread now, and that is the whole point of
    /// this method. Until 2026-07-24 it fetched the ADT, built the height grid
    /// and built the hole grid on the CALLING thread and sent only the mesh to a
    /// worker - so a cold tile on the entering edge paid a full MPQ read,
    /// decompress and parse inside Update. The hitch recorder measured that at
    /// ~109 ms of a 172 ms tile-crossing freeze, invisible to the [stream] timer
    /// because that timer starts afterwards (handbook 3.27).
    ///
    /// AdtCache.QueueLoad already existed and already parses on the pool. The
    /// fix is to use it, not to add machinery.
    /// </summary>
    public void QueuePreload(
        IEnumerable<(int col, int row)> tiles, AdtCache adts,
        (int col, int row)? streamCentre = null)
    {
        if (streamCentre is { } centre)
            tiles = tiles
                .OrderBy(t => Math.Max(Math.Abs(t.col - centre.col), Math.Abs(t.row - centre.row)))
                .ThenBy(t => Math.Abs(t.col - centre.col) + Math.Abs(t.row - centre.row));

        foreach (var (col, row) in tiles)
        {
            var key = (col, row);
            if (_tiles.ContainsKey(key) ||
                _preloads.ContainsKey(key) ||
                _missingPreloads.Contains(key)) continue;

            // Registered synchronously so one tile is never queued twice. That
            // dictionary write is the only work this thread does here.
            _preloads[key] = PreparePreloadAsync(key, adts);
            PreloadDequeued?.Invoke(col, row);
        }
    }

    public bool PreloadReady((int col, int row) key)
        => _tiles.ContainsKey(key) ||
           _missingPreloads.Contains(key) ||
           (_preloads.TryGetValue(key, out var task) && task.IsCompleted);

    public bool PreloadReady(IEnumerable<(int col, int row)> tiles)
        => tiles.All(PreloadReady);

    /// <summary>
    /// Parse, mesh and build both CPU grids for one tile on the worker pool,
    /// then upload through the shared context. One worker job covers the grids
    /// and the mesh because they share the parsed ADT - splitting them would
    /// serialize two hops for no gain.
    /// </summary>
    private async Task<PreloadedTile?> PreparePreloadAsync(
        (int col, int row) key, AdtCache adts)
    {
        var adt = await adts.QueueLoad(key.col, key.row, _workers).ConfigureAwait(false);
        if (adt is null) return null;

        var cpu = await _workers.Run(() => new PreloadedTile
            {
                Heights = BuildHeightGrid(adt),
                Holes = BuildHoleGrid(adt),
                Uploaded = null,
            }).ConfigureAwait(false);

        var prepared = await _workers.Run(() => TerrainTile.Prepare(
            adt, _config.ClientDataPath, key.col, key.row)).ConfigureAwait(false);
        if (prepared is null) return null;

        cpu.Uploaded = await _uploads.Enqueue(
            $"terrain [{key.col},{key.row}]",
            uploadGl => TerrainTile.Upload(uploadGl, _gl, prepared)).ConfigureAwait(false);

        return cpu;
    }

    /// <summary>Adopt ready terrain belonging to the current desired ring.</summary>
    public void PumpPreloads()
    {
        foreach (var key in _desired.ToArray())
        {
            if (_tiles.ContainsKey(key) ||
                !_preloads.TryGetValue(key, out var task) ||
                !task.IsCompleted) continue;

            _preloads.Remove(key);
            try
            {
                var ready = task.GetAwaiter().GetResult();
                if (ready?.Uploaded is null) { _missingPreloads.Add(key); continue; }
                _tiles[key] = TerrainTile.Adopt(_gl, ready.Uploaded);
                _heights[key] = ready.Heights;
                SetHoles(key, ready.Holes);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[terrain-preload] tile [{key.col},{key.row}] failed - {ex.Message}");
                _missingPreloads.Add(key);
            }
        }
    }

    /// <summary>
    /// CPU-side 129x129 grid of absolute heights for ground queries. Same
    /// mapping the mesh uses, so what you stand on matches what you see.
    /// </summary>
    private static float[] BuildHeightGrid(AdtTerrainReader.AdtResult? adt)
    {
        var grid = new float[HeightGridSide * HeightGridSide];

        if (adt?.Chunks == null) return grid;

        foreach (var chunk in adt.Chunks)
        {
            if (chunk?.Heights == null) continue;

            for (int r = 0; r <= 8; r++)
            for (int c = 0; c <= 8; c++)
            {
                int gr = chunk.IndexY * 8 + r;
                int gc = chunk.IndexX * 8 + c;
                if (gr >= HeightGridSide || gc >= HeightGridSide) continue;
                grid[gr * HeightGridSide + gc] = chunk.WorldHeightAt(c, r);
            }
        }

        return grid;
    }

    /// <summary>
    /// CPU-side 128x128 grid of the per-cell inner (fan-centre) heights — the
    /// vertex the render mesh fans four triangles around. Indexed like the hole
    /// grid: inner[r * 128 + c] belongs to the quad with corners (r,c)..(r+1,c+1).
    /// </summary>
    private static float[] BuildInnerHeightGrid(AdtTerrainReader.AdtResult? adt)
    {
        var grid = new float[QuadGridSide * QuadGridSide];
        if (adt?.Chunks == null) return grid;
        foreach (var chunk in adt.Chunks)
        {
            if (chunk?.Heights == null) continue;
            for (int r = 0; r < 8; r++)
            for (int c = 0; c < 8; c++)
            {
                int gr = chunk.IndexY * 8 + r;
                int gc = chunk.IndexX * 8 + c;
                if (gr >= QuadGridSide || gc >= QuadGridSide) continue;
                grid[gr * QuadGridSide + gc] = chunk.BaseZ + chunk.InnerHeight(c, r);
            }
        }
        return grid;
    }

    /// <summary>
    /// Emit the RENDERED terrain triangles (the 4-triangle fan per cell, real inner
    /// vertex) that intersect a world-XY rectangle. This is what ground-decal
    /// projection drapes spell markers/rings over: the emitted geometry is exactly
    /// coplanar with the drawn ground, so a depth-biased decal can never clip
    /// through a slope. Holed and no-data cells are skipped (they aren't drawn).
    /// </summary>
    public void GatherGroundTriangles(float minX, float minY, float maxX, float maxY,
        List<(Vector3 A, Vector3 B, Vector3 C)> output)
    {
        const float cell = GridSize / (HeightGridSide - 1);
        foreach (((int col, int row) key, float[] grid) in _heights)
        {
            float originX = (32 - key.row) * GridSize;
            float originY = (32 - key.col) * GridSize;
            // world X spans [originX - GridSize, originX]; reject tiles outside the rect.
            if (originX < minX || originX - GridSize > maxX ||
                originY < minY || originY - GridSize > maxY) continue;

            int r0 = Math.Clamp((int)MathF.Floor((originX - maxX) / cell), 0, QuadGridSide - 1);
            int r1 = Math.Clamp((int)MathF.Ceiling((originX - minX) / cell), 1, QuadGridSide);
            int c0 = Math.Clamp((int)MathF.Floor((originY - maxY) / cell), 0, QuadGridSide - 1);
            int c1 = Math.Clamp((int)MathF.Ceiling((originY - minY) / cell), 1, QuadGridSide);
            _holes.TryGetValue(key, out byte[]? holeGrid);
            _innerHeights.TryGetValue(key, out float[]? inner);

            for (int r = r0; r < r1; r++)
            for (int c = c0; c < c1; c++)
            {
                if (ApplyHoles && holeGrid is not null &&
                    holeGrid.Length == QuadGridSide * QuadGridSide &&
                    holeGrid[r * QuadGridSide + c] != 0) continue;
                float h00 = grid[r * HeightGridSide + c];
                float h01 = grid[r * HeightGridSide + c + 1];
                float h10 = grid[(r + 1) * HeightGridSide + c];
                float h11 = grid[(r + 1) * HeightGridSide + c + 1];
                if (h00 == 0 && h01 == 0 && h10 == 0 && h11 == 0) continue; // no MCVT

                float x0 = originX - r * cell, x1 = x0 - cell;
                float y0 = originY - c * cell, y1 = y0 - cell;
                var v00 = new Vector3(x0, y0, h00);
                var v01 = new Vector3(x0, y1, h01);
                var v10 = new Vector3(x1, y0, h10);
                var v11 = new Vector3(x1, y1, h11);
                float midH = inner is not null && inner.Length == QuadGridSide * QuadGridSide &&
                             inner[r * QuadGridSide + c] != 0
                    ? inner[r * QuadGridSide + c]
                    : (h00 + h01 + h10 + h11) * .25f;
                var mid = new Vector3(x0 - cell * .5f, y0 - cell * .5f, midH);

                output.Add((v00, mid, v01));
                output.Add((v01, mid, v11));
                output.Add((v11, mid, v10));
                output.Add((v10, mid, v00));
            }
        }
    }

    /// <summary>
    /// Per-quad hole mask for one tile: 128x128 bytes, 1 where the MCNK holes
    /// field says the ground is cut away. Indexed by the quad's lower corner,
    /// exactly like the height grid, so holes[r * 128 + c] guards the quad
    /// whose corners are heights (r,c)..(r+1,c+1).
    ///
    /// Vanilla stores this as a uint16 per chunk at MCNK+0x3C: sixteen bits in
    /// a 4x4 layout, each bit covering a 2x2 block of the chunk's 8x8 quads.
    /// It is how Blizzard cuts a doorway through a hillside so a dungeon WMO's
    /// tunnel mouth is reachable — in Azeroth_32_48 the only two chunks with a
    /// non-zero mask are the two sitting directly under md_goldmine.wmo. (The
    /// 64-bit high-resolution variant is MoP 5.3+ and does not exist here.)
    /// </summary>
    private static byte[] BuildHoleGrid(AdtTerrainReader.AdtResult? adt)
    {
        var grid = new byte[QuadGridSide * QuadGridSide];

        if (adt?.Chunks == null) return grid;

        foreach (var chunk in adt.Chunks)
        {
            if (chunk is null || chunk.Holes == 0) continue;

            for (int r = 0; r < 8; r++)
            for (int c = 0; c < 8; c++)
            {
                if (!chunk.IsHole(c, r)) continue;
                int gr = chunk.IndexY * 8 + r;
                int gc = chunk.IndexX * 8 + c;
                if (gr >= QuadGridSide || gc >= QuadGridSide) continue;
                grid[gr * QuadGridSide + gc] = 1;
            }
        }

        return grid;
    }

    /// <summary>True if the quad under a world position was cut away.</summary>
    public bool IsHoleAt(float worldX, float worldY)
    {
        SampleHeight(worldX, worldY, out bool hole);
        return hole;
    }

    /// <summary>
    /// Bilinear ground height at a WoW position, or null off the loaded tiles.
    /// Cheap enough to call every frame — this is what the character stands on.
    /// </summary>
    public float? SampleHeight(float worldX, float worldY)
        => SampleHeight(worldX, worldY, out _);

    /// <summary>
    /// As above, but also reports whether the query landed in a terrain hole.
    /// The two null cases are not the same thing and callers must not treat
    /// them alike: a hole is a deliberate opening the artists cut so you can
    /// walk into a WMO, so the caller should look for a collision surface (or
    /// fall through), whereas a plain null means "no data" and freezing is the
    /// safer answer. Vanilla draws the same line between INVALID_HEIGHT and
    /// VMAP_INVALID_HEIGHT_VALUE.
    /// </summary>
    public float? SampleHeight(float worldX, float worldY, out bool hole)
    {
        hole = false;

        var key = TileAt(worldX, worldY);
        if (!_heights.TryGetValue(key, out var grid)) return null;

        float originX = (32 - key.row) * GridSize;
        float originY = (32 - key.col) * GridSize;
        const float cell = GridSize / (HeightGridSide - 1);

        float fr = (originX - worldX) / cell;
        float fc = (originY - worldY) / cell;

        int r0 = (int)MathF.Floor(fr);
        int c0 = (int)MathF.Floor(fc);
        if (r0 < 0 || c0 < 0 || r0 >= HeightGridSide - 1 || c0 >= HeightGridSide - 1) return null;

        // TerrainTile already drops holed quads from the mesh. Skipping the
        // same quads here is the whole fix: otherwise the height grid keeps
        // answering with terrain that is neither drawn nor there, and the
        // player walks up an invisible hillside instead of into the mine.
        if (ApplyHoles &&
            _holes.TryGetValue(key, out var holeGrid) &&
            holeGrid.Length == QuadGridSide * QuadGridSide &&
            holeGrid[r0 * QuadGridSide + c0] != 0)
        {
            hole = true;
            return null;
        }

        float tr = fr - r0;
        float tc = fc - c0;

        float At(int r, int c) => grid[r * HeightGridSide + c];

        float h00 = At(r0, c0), h01 = At(r0, c0 + 1);
        float h10 = At(r0 + 1, c0), h11 = At(r0 + 1, c0 + 1);

        // All-zero means the chunk had no MCVT; refuse rather than dropping the
        // player to sea level.
        if (h00 == 0 && h01 == 0 && h10 == 0 && h11 == 0) return null;

        return h00 * (1 - tr) * (1 - tc)
             + h01 * (1 - tr) * tc
             + h10 * tr * (1 - tc)
             + h11 * tr * tc;
    }

    public void Render(Camera camera) => Render(camera, null);

    /// <summary>
    /// Draw terrain with an optional absolute-world clip plane. The caller owns
    /// GL_CLIP_DISTANCE0 state; the ordinary active-world overload leaves it
    /// disabled and therefore pays no fragment-discard cost.
    /// </summary>
    public void Render(Camera camera, WorldClipPlane? worldClipPlane)
    {
        long started = Stopwatch.GetTimestamp();
        DrawCallsLastFrame = 0;
        TrianglesLastFrame = 0;
        if (_shader is null || _tiles.Count == 0)
        {
            DrawnLastFrame = 0;
            RenderMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            return;
        }

        _shader.Use();
        _shader.Set("uViewProjection", camera.RelativeViewProjection);
        _shader.Set("uCameraOrigin", camera.Position);
        _shader.Set("uWorldClipPlane", worldClipPlane is { IsValid: true } clip
            ? clip.RelativeEquation(camera.Position)
            : new Vector4(0f, 0f, 0f, 1f));
        _shader.Set("uCameraPos", Vector3.Zero);
        // Normalised HERE, not per pixel. The shader used to call normalize() on
        // this every fragment — on a uniform, over a surface that covers most of
        // the screen. wmo.frag and doodad.frag already did it this way.
        _shader.Set("uSunDirection", SafeNormalize(SunDirection));
        _shader.Set("uSunColor", SunColor);
        _shader.Set("uSunIntensity", SunIntensity);
        _shader.Set("uAmbientColor", AmbientColor);
        _shader.Set("uAmbientIntensity", AmbientIntensity);
        _shader.Set("uAuthoredShadowStrength", AuthoredShadowStrength);
        _shader.Set("uFogStart", FogStart);
        _shader.Set("uFogEnd", FogEnd);
        _shader.Set("uFogColor", FogColor);
        _shader.Set("uDebugMode", DebugMode);
        _shader.Set("uTextureScale", TextureScale);

        // Sampler bindings are fixed: unit 0 tileset, unit 1 MCAL, unit 2 MCSH.
        _shader.Set("uTileset", 0);
        _shader.Set("uAlphaArray", 1);
        _shader.Set("uShadowArray", 2);

        var viewProjection = camera.RelativeViewProjection;
        var cameraPosition = camera.Position;
        int drawn = 0;

        foreach (var tile in _tiles.Values)
        {
            if (DistanceToBox(cameraPosition, tile.BoundsMin, tile.BoundsMax) > VisibilityDistance)
                continue;
            if (!Camera.BoxInFrustum(viewProjection,
                    tile.BoundsMin - cameraPosition,
                    tile.BoundsMax - cameraPosition)) continue;

            // The tile-level test above is now only a cheap reject. The real
            // culling is per MCNK inside Draw, because a 533-yard box with one
            // corner on screen used to submit all ~32,700 of its triangles.
            if (ChunkCulling) tile.Draw(viewProjection, cameraPosition);
            else tile.Draw();

            drawn++;
            DrawCallsLastFrame += tile.DrawCallsLastCall;
            TrianglesLastFrame += tile.TrianglesDrawnLastCall;
        }

        DrawnLastFrame = drawn;
        _gl.BindVertexArray(0);
        RenderMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    private static float DistanceToBox(Vector3 point, Vector3 min, Vector3 max)
    {
        float dx = MathF.Max(MathF.Max(min.X - point.X, 0f), point.X - max.X);
        float dy = MathF.Max(MathF.Max(min.Y - point.Y, 0f), point.Y - max.Y);
        float dz = MathF.Max(MathF.Max(min.Z - point.Z, 0f), point.Z - max.Z);
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>
    /// Startup self-check: compares the sampled ground height at a known spawn
    /// against its expected value. Northshire's human start is z = 83.5312, and
    /// the server agreed with this parse to 0.00 — so a large delta here means
    /// something regressed, not that the reference is wrong.
    /// </summary>
    public void VerifyAgainst(float worldX, float worldY, float expectedZ)
    {
        var (col, row) = TileAt(worldX, worldY);
        float? h = SampleHeight(worldX, worldY);

        if (h is null)
        {
            Console.WriteLine($"[verify] FAIL — no height at ({worldX:F1}, {worldY:F1}), tile [{col},{row}]");
            return;
        }

        float delta = h.Value - expectedZ;
        string verdict = MathF.Abs(delta) < 5f ? "PASS" : "FAIL";
        Console.WriteLine(
            $"[verify] {verdict} — tile [{col},{row}] sampled {h.Value:F2}, " +
            $"expected {expectedZ:F2}, delta {delta:F2}");
    }

    /// <summary>
    /// Release every tile and every preload, keeping the shader and this
    /// object alive. This is the terrain half of a map change (PLAN_13 H2).
    ///
    /// SetResidency IS NOT A SUBSTITUTE. It keeps any tile whose key is in the
    /// new desired ring, and tile keys are (col, row) on a grid every map
    /// shares - Deadmines occupies col 30..35 row 30..35, which Azeroth also
    /// has. Moving residency across a map boundary would therefore KEEP the old
    /// map's meshes wherever the two ranges overlap, and they would render as
    /// Elwynn hillside inside the dungeon with nothing logged.
    ///
    /// The in-flight preload drain is not optional either: those tasks own
    /// GL buffers and textures uploaded on the shared context, and dropping
    /// their references without deleting them leaks VRAM every round trip.
    /// </summary>
    public void UnloadAll()
    {
        try { Task.WhenAll(_preloads.Values).GetAwaiter().GetResult(); }
        catch { /* A failed terrain package must not block the unload. */ }

        foreach (var tile in _tiles.Values) tile.Dispose();
        foreach (var task in _preloads.Values)
            if (task.Status == TaskStatus.RanToCompletion &&
                task.Result is { Uploaded: { } uploaded })
            {
                uploaded.Textures.Dispose();
                _gl.DeleteBuffer(uploaded.Vbo);
                _gl.DeleteBuffer(uploaded.Ebo);
            }

        int released = _tiles.Count;
        _tiles.Clear();
        _heights.Clear();
        _holes.Clear();
        _holeCountDirty = true;
        _preloads.Clear();

        // Absences are per-map. "No ADT at [40,20]" is true of Deadmines and
        // false of Azeroth, and keeping the note would permanently hole the
        // new map exactly where the old one was ocean.
        _missingPreloads.Clear();
        _desired = [];

        Console.WriteLine($"[terrain] unloaded {released} tile(s)");
    }

    /// <summary>
    /// Normalize, tolerating a zero vector. The sun direction is a knob and the
    /// HUD can drive it to zero mid-drag; a NaN uniform would black the whole
    /// terrain and read as a shader bug.
    /// </summary>
    private static Vector3 SafeNormalize(Vector3 v)
    {
        float lengthSq = v.LengthSquared();
        return lengthSq < 1e-12f ? Vector3.UnitZ : v / MathF.Sqrt(lengthSq);
    }

    public void Dispose()
    {
        UnloadAll();
        _shader?.Dispose();
    }
}

