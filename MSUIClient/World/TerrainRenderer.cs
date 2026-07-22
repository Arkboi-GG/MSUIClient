using System.Numerics;
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
    private readonly Dictionary<(int col, int row), TerrainTile> _tiles = [];

    /// <summary>Height grids kept CPU-side for ground queries, keyed like the tiles.</summary>
    private readonly Dictionary<(int col, int row), float[]> _heights = [];

    private Shader _shader = null!;

    public const int HeightGridSide = 129;
    public const float GridSize = 533.33333f;

    public int TileCount => _tiles.Count;
    public int DrawnLastFrame { get; private set; }
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
    public Vector3 FogColor { get; set; } = new(0.56f, 0.71f, 0.85f);
    public float FogStart { get; set; } = 350f;
    public float FogEnd { get; set; } = 900f;

    public TerrainRenderer(GL gl, ClientConfig config)
    {
        _gl = gl;
        _config = config;
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

    /// <summary>Load a square block of tiles centred on a world position.</summary>
    public void LoadAround(float worldX, float worldY, int radius)
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

            var tile = TerrainTile.Load(_gl, _config.ClientDataPath, _config.Start.MapName, col, row);
            if (tile is null) { missing++; continue; }

            _tiles[(col, row)] = tile;
            _heights[(col, row)] = BuildHeightGrid(_config.ClientDataPath, _config.Start.MapName, col, row);
            loaded++;
        }

        var elapsed = DateTime.UtcNow - started;
        Console.WriteLine(
            $"[terrain] {loaded} tile(s) loaded, {missing} absent, " +
            $"{TotalTriangles:N0} triangles, {elapsed.TotalSeconds:F1}s");
    }

    /// <summary>
    /// CPU-side 129x129 grid of absolute heights for ground queries. Same
    /// mapping the mesh uses, so what you stand on matches what you see.
    /// </summary>
    private static float[] BuildHeightGrid(string clientDataPath, string mapName, int col, int row)
    {
        var grid = new float[HeightGridSide * HeightGridSide];

        var adt = AdtTerrainReader.ReadFromMpq(clientDataPath, mapName, row, col);
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
        if (_shader is null || _tiles.Count == 0) { DrawnLastFrame = 0; return; }

        _shader.Use();
        _shader.Set("uViewProjection", camera.ViewProjection);
        _shader.Set("uCameraPos", camera.Position);
        _shader.Set("uSunDirection", SunDirection);
        _shader.Set("uFogStart", FogStart);
        _shader.Set("uFogEnd", FogEnd);
        _shader.Set("uFogColor", FogColor);
        _shader.Set("uDebugMode", DebugMode);
        _shader.Set("uTextureScale", TextureScale);

        // Sampler bindings are fixed: unit 0 tileset array, unit 1 alpha atlas.
        _shader.Set("uTileset", 0);
        _shader.Set("uAlphaAtlas", 1);

        var planes = camera.FrustumPlanes();
        int drawn = 0;

        foreach (var tile in _tiles.Values)
        {
            if (!Camera.BoxInFrustum(planes, tile.BoundsMin, tile.BoundsMax)) continue;
            tile.Draw();
            drawn++;
        }

        DrawnLastFrame = drawn;
        _gl.BindVertexArray(0);
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
        foreach (var tile in _tiles.Values) tile.Dispose();
        _tiles.Clear();
        _heights.Clear();
        _shader?.Dispose();
    }
}
