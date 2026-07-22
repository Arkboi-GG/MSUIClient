using Silk.NET.OpenGL;
using MSUIClient.Engine;
using MSUIClient.Formats;
using Texture = MSUIClient.Engine.Texture; 

namespace MSUIClient.World;

/// <summary>
/// The texture side of one ADT tile: the tileset as a GL array texture, plus a
/// packed alpha atlas holding every chunk's blend masks.
///
/// HOW VANILLA TERRAIN TEXTURING WORKS
///   Each ADT lists up to ~16 texture paths in MTEX. Each of its 256 MCNK
///   chunks picks up to 4 of them (MCLY) and blends them with 64x64 alpha maps
///   (MCAL). Layer 0 is the base and has no alpha — it shows wherever the
///   others don't cover it.
///
/// HOW WE RENDER IT
///   All MTEX textures go into one GL_TEXTURE_2D_ARRAY, so a whole tile binds
///   once. Every chunk's three alpha masks pack into a single 1024x1024 RGBA
///   atlas (16x16 chunks x 64x64 texels): R = layer 1, G = layer 2, B = layer 3.
///   Each vertex carries its chunk's four array indices. One draw call per tile.
///
/// ALPHA ORIENTATION IS UNVERIFIED
///   AdtTerrainReader normalises every MCAL encoding to a 64x64 stride-64
///   buffer indexed [py * 64 + px], but whether px runs along the chunk's
///   north-south or east-west axis was never pinned down — the SuperUI splat
///   path carries swapAxes/transposeAlpha flags for exactly this reason.
///   <see cref="TransposeAlpha"/> flips it at build time. If the ground shows
///   the right materials in the wrong places, that's the switch.
/// </summary>
public sealed class TerrainTextures : IDisposable
{
    public const int AlphaSize = 64;
    public const int ChunksPerSide = 16;
    public const int AtlasSize = ChunksPerSide * AlphaSize;   // 1024

    /// <summary>Flip the within-chunk alpha axes. See the class remarks.</summary>
    public static bool TransposeAlpha { get; set; }

    private Texture? _tileset;
    private Texture? _alphaAtlas;

    public int TextureCount => _tileset?.Layers ?? 0;
    public bool Ready => _tileset is not null && _alphaAtlas is not null;
    public IReadOnlyList<string> TextureNames { get; private set; } = [];

    /// <summary>
    /// Per-chunk layer indices into the tileset array, [chunkIndex][0..3].
    /// -1 means the chunk has no layer in that slot.
    /// </summary>
    public int[][] ChunkLayers { get; private set; } = [];

    public static TerrainTextures Build(
        GL gl, AdtTerrainReader.AdtResult adt, string clientDataPath, int col, int row)
    {
        var result = new TerrainTextures();

        // ── tileset array ────────────────────────────────────────────────────
        var names = adt.Textures;
        var pixels = new List<byte[]>(names.Count);
        var kept = new List<string>(names.Count);
        var remap = new Dictionary<int, int>();      // MTEX index -> array layer

        int expectedW = 0, expectedH = 0;

        for (int i = 0; i < names.Count; i++)
        {
            var decoded = AdtTerrainReader.ReadBlpPixels(clientDataPath, names[i]);
            if (decoded is null)
            {
                Console.WriteLine($"[terrain] tile [{col},{row}] missing texture: {names[i]}");
                continue;
            }

            var (bgra, w, h) = decoded.Value;

            if (expectedW == 0) { expectedW = w; expectedH = h; }
            else if (w != expectedW || h != expectedH)
            {
                // Array textures demand uniform dimensions. Vanilla tilesets are
                // uniformly 256x256; anything else is an oddity worth seeing.
                Console.WriteLine(
                    $"[terrain] tile [{col},{row}] texture {names[i]} is {w}x{h}, " +
                    $"expected {expectedW}x{expectedH} — skipped");
                continue;
            }

            remap[i] = pixels.Count;
            pixels.Add(bgra);
            kept.Add(names[i]);
        }

        if (pixels.Count > 0)
        {
            result._tileset = Texture.Array2D(gl, pixels, expectedW, expectedH);
            result.TextureNames = kept;
        }

        // ── alpha atlas + per-chunk layer indices ────────────────────────────
        var atlas = new byte[AtlasSize * AtlasSize * 4];
        var layers = new int[ChunksPerSide * ChunksPerSide][];

        for (int i = 0; i < layers.Length; i++) layers[i] = [-1, -1, -1, -1];

        if (adt.Chunks is not null)
        {
            foreach (var chunk in adt.Chunks)
            {
                if (chunk is null) continue;

                int cx = chunk.IndexX, cy = chunk.IndexY;
                if (cx is < 0 or >= ChunksPerSide || cy is < 0 or >= ChunksPerSide) continue;

                int chunkIndex = cy * ChunksPerSide + cx;

                for (int li = 0; li < chunk.Layers.Length && li < 4; li++)
                {
                    int mtex = chunk.Layers[li].TextureIndex;
                    layers[chunkIndex][li] = remap.TryGetValue(mtex, out int mapped) ? mapped : -1;
                }

                // Layers 1..3 carry alpha; layer 0 is the implicit base.
                for (int li = 1; li < chunk.Layers.Length && li < 4; li++)
                {
                    var alpha = chunk.Layers[li].AlphaMap;
                    if (alpha is null || alpha.Length < AlphaSize * AlphaSize) continue;

                    for (int py = 0; py < AlphaSize; py++)
                    for (int px = 0; px < AlphaSize; px++)
                    {
                        byte value = alpha[py * AlphaSize + px];

                        // Atlas X follows the tile's Y/west axis (chunk IndexX),
                        // atlas Y follows the X/north axis (chunk IndexY) — the
                        // same mapping TerrainTile uses for its UVs.
                        int ax = cx * AlphaSize + (TransposeAlpha ? py : px);
                        int ay = cy * AlphaSize + (TransposeAlpha ? px : py);

                        atlas[(ay * AtlasSize + ax) * 4 + (li - 1)] = value;
                    }
                }
            }
        }

        result._alphaAtlas = Texture.FromRgbaNoMips(gl, atlas, AtlasSize, AtlasSize);
        result.ChunkLayers = layers;

        Console.WriteLine(
            $"[terrain] tile [{col},{row}] textures: {pixels.Count}/{names.Count} loaded " +
            $"({expectedW}x{expectedH}), alpha atlas {AtlasSize}x{AtlasSize}");

        return result;
    }

    /// <summary>Bind tileset to unit 0 and alpha atlas to unit 1.</summary>
    public void Bind()
    {
        _tileset?.Bind(0);
        _alphaAtlas?.Bind(1);
    }

    public void Dispose()
    {
        _tileset?.Dispose();
        _alphaAtlas?.Dispose();
    }
}
