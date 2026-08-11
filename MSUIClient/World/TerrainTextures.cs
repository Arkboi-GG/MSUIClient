using Silk.NET.OpenGL;
using System.Buffers;
using MSUIClient.Engine;
using MSUIClient.Formats;
using Texture = MSUIClient.Engine.Texture; 

namespace MSUIClient.World;

/// <summary>
/// The texture side of one ADT tile: the tileset as a GL array texture, plus
/// per-chunk array textures for blend masks and baked MCSH terrain shadows.
///
/// HOW VANILLA TERRAIN TEXTURING WORKS
///   Each ADT lists up to ~16 texture paths in MTEX. Each of its 256 MCNK
///   chunks picks up to 4 of them (MCLY) and blends them with 64x64 alpha maps
///   (MCAL). Layer 0 is the base and has no alpha — it shows wherever the
///   others don't cover it.
///
/// HOW WE RENDER IT
///   All MTEX textures go into one GL_TEXTURE_2D_ARRAY, so a whole tile binds
///   once. Every chunk's three alpha masks go into a SECOND array texture, one
///   64x64 RGBA layer per chunk: R = layer 1, G = layer 2, B = layer 3. Each
///   vertex carries its chunk's four tileset indices and its own alpha layer.
///
/// THE ALPHA MASKS USED TO BE AN ATLAS, AND THAT WAS THE SEAM
///   They were packed edge to edge into one 1024x1024 image and sampled with a
///   tile-wide UV. A chunk boundary then lands exactly on an integer texel
///   coordinate, so the bilinear tap there is a literal 50/50 blend of two
///   DIFFERENT chunks' blend weights — applied to this chunk's four texture
///   indices, which are usually a different set of ground textures entirely.
///   One alpha texel is 533.33/16/64 = 0.52 yd, so that is roughly a yard of
///   wrong-texture smear along every chunk edge in the world.
///
///   No sampler state fixes it: inside an atlas the neighbours really are
///   adjacent, so CLAMP_TO_EDGE only clamps at the atlas border. A half-texel
///   inset in the shader works, and was the first fix, but it is a workaround
///   for a layout that should not have been an atlas. Array layers cannot
///   address each other at all, which makes the problem structurally impossible
///   rather than avoided — and it costs nothing: same 4 MB, one bind either way.
///   This is what benilla does (benilla-assets/src/terrain.rs:145-147).
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
    public sealed class Prepared
    {
        public List<byte[]> Pixels = [];
        public List<string> Names = [];

        /// <summary>
        /// Every chunk's 64x64 RGBA mask, back to back, layer index = chunk
        /// index (cy * 16 + cx). One contiguous buffer so the upload is a single
        /// TexImage3D rather than 256 sub-image calls.
        /// </summary>
        public byte[] AlphaLayers = [];

        /// <summary>
        /// Every chunk's expanded 64x64 R8 MCSH mask, back to back. Kept
        /// separate from AlphaLayers so adding authored light structure cannot
        /// alter any of the three MCAL blend weights.
        /// </summary>
        public byte[] ShadowLayers = [];

        public int[][] ChunkLayers = [];
        public int Width;
        public int Height;
        public bool Pooled;
    }

    public const int AlphaSize = 64;
    public const int ShadowSize = AdtTerrainReader.MCSH_SIZE;
    public const int ChunksPerSide = 16;

    /// <summary>Alpha array layers: one per chunk.</summary>
    public const int ChunkCount = ChunksPerSide * ChunksPerSide;   // 256

    /// <summary>Bytes in one chunk's RGBA mask.</summary>
    private const int AlphaLayerBytes = AlphaSize * AlphaSize * 4;

    /// <summary>Bytes in one chunk's expanded R8 MCSH mask.</summary>
    private const int ShadowLayerBytes = ShadowSize * ShadowSize;

    /// <summary>Flip the within-chunk alpha axes. See the class remarks.</summary>
    public static bool TransposeAlpha { get; set; }

    private Texture? _tileset;
    private Texture? _alphaArray;
    private Texture? _shadowArray;

    public int TextureCount => _tileset?.Layers ?? 0;
    public bool Ready => _tileset is not null && _alphaArray is not null && _shadowArray is not null;
    public IReadOnlyList<string> TextureNames { get; private set; } = [];

    /// <summary>
    /// Per-chunk layer indices into the tileset array, [chunkIndex][0..3].
    /// -1 means the chunk has no layer in that slot.
    /// </summary>
    public int[][] ChunkLayers { get; private set; } = [];

    public static TerrainTextures Build(
        GL gl, AdtTerrainReader.AdtResult adt, string clientDataPath, int col, int row)
        => Upload(gl, Prepare(adt, clientDataPath, col, row));

    public static Prepared Prepare(
        AdtTerrainReader.AdtResult adt, string clientDataPath, int col, int row)
    {
        // ── tileset array ────────────────────────────────────────────────────
        var names = adt.Textures;
        var pixels = new List<byte[]>(names.Count);
        var kept = new List<string>(names.Count);
        var remap = new Dictionary<int, int>();      // MTEX index -> array layer

        int expectedW = 0, expectedH = 0;

        for (int i = 0; i < names.Count; i++)
        {
            var decoded = AdtTerrainReader.ReadBlpPixelsPooled(clientDataPath, names[i]);
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
                ArrayPool<byte>.Shared.Return(bgra);
                continue;
            }

            remap[i] = pixels.Count;
            pixels.Add(bgra);
            kept.Add(names[i]);
        }

        // ── alpha/shadow arrays + per-chunk layer indices ───────────────────
        int alphaBytes = ChunkCount * AlphaLayerBytes;
        var alphaLayers = ArrayPool<byte>.Shared.Rent(alphaBytes);
        Array.Clear(alphaLayers, 0, alphaBytes);
        int shadowBytes = ChunkCount * ShadowLayerBytes;
        var shadowLayers = ArrayPool<byte>.Shared.Rent(shadowBytes);
        Array.Clear(shadowLayers, 0, shadowBytes);
        var layers = new int[ChunkCount][];

        for (int i = 0; i < layers.Length; i++) layers[i] = [-1, -1, -1, -1];

        if (adt.Chunks is not null)
        {
            foreach (var chunk in adt.Chunks)
            {
                if (chunk is null) continue;

                int cx = chunk.IndexX, cy = chunk.IndexY;
                if (cx is < 0 or >= ChunksPerSide || cy is < 0 or >= ChunksPerSide) continue;

                int chunkIndex = cy * ChunksPerSide + cx;

                // MCSH already uses the same row-major orientation as MCAL:
                // x/column -> chunk U, y/row -> chunk V. Missing or malformed
                // maps leave this layer zero, which means fully authored light.
                if (chunk.ShadowMap is { Length: >= ShadowLayerBytes } shadow)
                    System.Buffer.BlockCopy(
                        shadow, 0, shadowLayers,
                        chunkIndex * ShadowLayerBytes, ShadowLayerBytes);

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

                    // This chunk's own layer. Within it, U runs along the tile's
                    // Y/west axis and V along the X/north axis — the same
                    // mapping TerrainTile uses for its UVs.
                    int layerBase = chunkIndex * AlphaLayerBytes;

                    for (int py = 0; py < AlphaSize; py++)
                    for (int px = 0; px < AlphaSize; px++)
                    {
                        byte value = alpha[py * AlphaSize + px];

                        int ax = TransposeAlpha ? py : px;
                        int ay = TransposeAlpha ? px : py;

                        alphaLayers[layerBase + (ay * AlphaSize + ax) * 4 + (li - 1)] = value;
                    }
                }
            }
        }

        return new Prepared
        {
            Pixels = pixels,
            Names = kept,
            AlphaLayers = alphaLayers,
            ShadowLayers = shadowLayers,
            ChunkLayers = layers,
            Width = expectedW,
            Height = expectedH,
            Pooled = true,
        };
    }

    public static TerrainTextures Upload(GL gl, Prepared prepared, GL? ownerGl = null)
    {
        var result = new TerrainTextures
        {
            TextureNames = prepared.Names,
            ChunkLayers = prepared.ChunkLayers,
        };

        try
        {
            if (prepared.Pixels.Count > 0)
                result._tileset = Texture.Array2D(
                    gl, prepared.Pixels, prepared.Width, prepared.Height, ownerGl: ownerGl);

            result._alphaArray = Texture.ArrayRgbaNoMips(
                gl, prepared.AlphaLayers, AlphaSize, AlphaSize, ChunkCount, ownerGl);
            result._shadowArray = Texture.ArrayR8NoMips(
                gl, prepared.ShadowLayers, ShadowSize, ShadowSize, ChunkCount, ownerGl);
        }
        finally
        {
            if (prepared.Pooled)
            {
                foreach (byte[] pixels in prepared.Pixels)
                    ArrayPool<byte>.Shared.Return(pixels);
                ArrayPool<byte>.Shared.Return(prepared.AlphaLayers);
                ArrayPool<byte>.Shared.Return(prepared.ShadowLayers);
                prepared.Pixels.Clear();
                prepared.AlphaLayers = [];
                prepared.ShadowLayers = [];
                prepared.Pooled = false;
            }
        }
        return result;
    }

    /// <summary>Bind tileset, MCAL, and MCSH arrays to units 0, 1, and 2.</summary>
    public void Bind()
    {
        _tileset?.Bind(0);
        _alphaArray?.Bind(1);
        _shadowArray?.Bind(2);
    }

    public void Dispose()
    {
        _tileset?.Dispose();
        _alphaArray?.Dispose();
        _shadowArray?.Dispose();
    }
}
