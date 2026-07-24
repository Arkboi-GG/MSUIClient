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
    private readonly Dictionary<(int col, int row), Task<TerrainTile.Uploaded?>> _preloads = [];
    private readonly Dictionary<(int col, int row), float[]> _preloadHeights = [];
    private readonly HashSet<(int col, int row)> _missingPreloads = [];
    private HashSet<(int col, int row)> _desired = [];

    /// <summary>Height grids kept CPU-side for ground queries, keyed like the tiles.</summary>
    private readonly Dictionary<(int col, int row), float[]> _heights = [];

    private Shader _shader = null!;

    public const int HeightGridSide = 129;
    public const float GridSize = 533.33333f;

    public int TileCount => _tiles.Count;
    public int DrawnLastFrame { get; private set; }
    public int DrawCallsLastFrame { get; private set; }
    public int TrianglesLastFrame { get; private set; }
    public double RenderMilliseconds { get; private set; }
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

    /// <summary>Tileset repeats per chunk. Vanilla is about 8; tune it live.</summary>
    public float TextureScale { get; set; } = 8f;

    public Vector3 SunDirection { get; set; } = Vector3.Normalize(new Vector3(0.45f, 0.35f, 0.82f));
    public Vector3 SunColor { get; set; } = new(1.00f, 0.95f, 0.85f);
    public float SunIntensity { get; set; } = 1.15f;
    public Vector3 AmbientColor { get; set; } = new(0.42f, 0.50f, 0.60f);
    public float AmbientIntensity { get; set; } = 0.85f;
    public Vector3 FogColor { get; set; } = new(0.56f, 0.71f, 0.85f);
    public float FogStart { get; set; } = 350f;
    public float FogEnd { get; set; } = 900f;
    public float VisibilityDistance { get; set; } = float.PositiveInfinity;

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

    /// <summary>Load a square block of tiles centred on a world position.</summary>
    public void LoadAround(float worldX, float worldY, int radius, AdtCache adts)
    {
        var (centreCol, centreRow) = TileAt(worldX, worldY);
        Console.WriteLine(
            $"[terrain] loading around ({worldX:F1}, {worldY:F1}) -> " +
            $"tile [{centreCol},{centreRow}], radius {radius}");

        var started = DateTime.UtcNow;
        int loaded = 0, missing = 0;

        for (int dc = -radius; dc <= radius; dc++)
        for (int dr = -radius; dr <= radius; dr++)
        {
            int col = centreCol + dc;
            int row = centreRow + dr;
            if (col is < 0 or > 63 || row is < 0 or > 63) continue;
            if (_tiles.ContainsKey((col, row))) continue;

            // One parse, two consumers: the GPU mesh and the CPU height grid.
            var adt = adts.Get(col, row);

            var tile = TerrainTile.Load(_gl, adt, _config.ClientDataPath, col, row);
            if (tile is null) { missing++; continue; }

            _tiles[(col, row)] = tile;
            _heights[(col, row)] = BuildHeightGrid(adt);
            loaded++;
        }

        var elapsed = DateTime.UtcNow - started;
        Console.WriteLine(
            $"[terrain] {loaded} tile(s) loaded, {missing} absent, " +
            $"{TotalTriangles:N0} triangles, {elapsed.TotalSeconds:F1}s");
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
            changed = true;
        }

        foreach (var (col, row) in desired.OrderBy(k => Math.Abs(k.col - centreCol) + Math.Abs(k.row - centreRow)))
        {
            if (_tiles.ContainsKey((col, row))) continue;

            if (_preloads.TryGetValue((col, row), out var preload))
            {
                if (!preload.IsCompleted) continue;
                _preloads.Remove((col, row));
                TerrainTile.Uploaded? uploaded;
                try { uploaded = preload.GetAwaiter().GetResult(); }
                catch (Exception ex)
                {
                    Console.WriteLine($"[terrain-preload] tile [{col},{row}] failed - {ex.Message}");
                    continue;
                }
                if (uploaded is null) continue;
                _tiles[(col, row)] = TerrainTile.Adopt(_gl, uploaded);
                _heights[(col, row)] = _preloadHeights.GetValueOrDefault((col, row), []);
                _preloadHeights.Remove((col, row));
                changed = true;
                continue;
            }

            var adt = adts.Get(col, row);
            var tile = TerrainTile.Load(_gl, adt, _config.ClientDataPath, col, row);
            if (tile is null) continue;

            _tiles[(col, row)] = tile;
            _heights[(col, row)] = BuildHeightGrid(adt);
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Prepare the one-tile lead ring on CPU workers and upload it through the
    /// shared GL context. These tiles remain unpublished until residency asks
    /// for them; the render thread only creates their small VAO container.
    /// </summary>
    public void QueuePreload(
        IEnumerable<(int col, int row)> tiles, AdtCache adts)
    {
        foreach (var (col, row) in tiles)
        {
            var key = (col, row);
            if (_tiles.ContainsKey(key) || _preloads.ContainsKey(key)) continue;

            var adt = adts.Get(col, row);
            if (adt is null)
            {
                _missingPreloads.Add(key);
                continue;
            }
            _preloadHeights[key] = BuildHeightGrid(adt);

            var preparation = _workers.Run(() => TerrainTile.Prepare(
                adt, _config.ClientDataPath, col, row));
            _preloads[key] = CompleteTerrainPreload(preparation, col, row);
        }
    }

    public bool PreloadReady(IEnumerable<(int col, int row)> tiles)
        => tiles.All(key =>
            _tiles.ContainsKey(key) ||
            _missingPreloads.Contains(key) ||
            (_preloads.TryGetValue(key, out var task) && task.IsCompleted));

    private async Task<TerrainTile.Uploaded?> CompleteTerrainPreload(
        Task<TerrainTile.Prepared?> preparation, int col, int row)
    {
        var prepared = await preparation.ConfigureAwait(false);
        if (prepared is null) return null;
        return await _uploads.Enqueue(
            $"terrain [{col},{row}]",
            uploadGl => TerrainTile.Upload(uploadGl, _gl, prepared)).ConfigureAwait(false);
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
                var uploaded = task.GetAwaiter().GetResult();
                if (uploaded is null) continue;
                _tiles[key] = TerrainTile.Adopt(_gl, uploaded);
                _heights[key] = _preloadHeights.GetValueOrDefault(key, []);
                _preloadHeights.Remove(key);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[terrain-preload] tile [{key.col},{key.row}] failed - {ex.Message}");
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
    /// Bilinear ground height at a WoW position, or null off the loaded tiles.
    /// Cheap enough to call every frame — this is what the character stands on.
    /// </summary>
    public float? SampleHeight(float worldX, float worldY)
    {
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

    public void Render(Camera camera)
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
        _shader.Set("uCameraPos", Vector3.Zero);
        _shader.Set("uSunDirection", SunDirection);
        _shader.Set("uSunColor", SunColor);
        _shader.Set("uSunIntensity", SunIntensity);
        _shader.Set("uAmbientColor", AmbientColor);
        _shader.Set("uAmbientIntensity", AmbientIntensity);
        _shader.Set("uFogStart", FogStart);
        _shader.Set("uFogEnd", FogEnd);
        _shader.Set("uFogColor", FogColor);
        _shader.Set("uDebugMode", DebugMode);
        _shader.Set("uTextureScale", TextureScale);

        // Sampler bindings are fixed: unit 0 tileset array, unit 1 alpha atlas.
        _shader.Set("uTileset", 0);
        _shader.Set("uAlphaAtlas", 1);

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
            tile.Draw();
            drawn++;
            DrawCallsLastFrame++;
            TrianglesLastFrame += tile.TriangleCount;
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

    public void Dispose()
    {
        try { Task.WhenAll(_preloads.Values).GetAwaiter().GetResult(); }
        catch { /* Continue shutdown after a failed terrain package. */ }
        foreach (var tile in _tiles.Values) tile.Dispose();
        foreach (var task in _preloads.Values)
            if (task.Status == TaskStatus.RanToCompletion && task.Result is { } uploaded)
            {
                uploaded.Textures.Dispose();
                _gl.DeleteBuffer(uploaded.Vbo);
                _gl.DeleteBuffer(uploaded.Ebo);
            }
        _tiles.Clear();
        _heights.Clear();
        _preloads.Clear();
        _preloadHeights.Clear();
        _missingPreloads.Clear();
        _shader?.Dispose();
    }
}
