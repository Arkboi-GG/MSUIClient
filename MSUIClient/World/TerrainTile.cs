using System.Numerics;
using Silk.NET.OpenGL;
using MSUIClient.Formats;

namespace MSUIClient.World;

/// <summary>
/// One ADT tile turned into a GPU mesh.
///
/// COORDINATE MAPPING — this is the important part, and it is not a guess.
///
/// The browser build derived vertex positions from the MCNK header's
/// BaseX/BaseY. That was never actually verified: the check that passed
/// (server height sample, delta 0.00 at the Northshire spawn) exercised the
/// HEIGHT GRID, which uses a different mapping — grid row/col from
/// chunk.IndexY/IndexX, and world position from the tile origin.
///
/// So this builds vertices from the mapping that WAS proven:
///
///     originX = (32 - tileRow) * 533.33333        tile's north-west corner
///     originY = (32 - tileCol) * 533.33333
///
///     gridRow = chunk.IndexY * 8 + row            0..128 across the tile
///     gridCol = chunk.IndexX * 8 + col
///
///     worldX  = originX - gridRow * CELL_SIZE     X decreases going south
///     worldY  = originY - gridCol * CELL_SIZE     Y decreases going east
///     worldZ  = chunk.BaseZ + mcvtHeight
///
/// Inner MCVT vertices sit at +0.5 on both grid axes. Because the geometry now
/// shares the mapping the server agreed with, correct heights imply correct
/// positions rather than merely being consistent with them.
///
/// Tessellation is 4 triangles per cell fanned around the inner vertex — what
/// the real client does, and why MCVT carries inner verts at all. Two-triangle
/// quads visibly flatten ridges.
/// </summary>
public sealed class TerrainTile : IDisposable
{
    /// <summary>Position(3) + Normal(3) + TileUV(2) + LayerIndices(4), interleaved.</summary>
    private const int FloatsPerVertex = 12;

    public sealed class Prepared
    {
        public required float[] Vertices;
        public required uint[] Indices;
        public required TerrainTextures.Prepared Textures;
        public int Col, Row, HoleCells;
        public Vector3 BoundsMin, BoundsMax;
    }

    public sealed class Uploaded
    {
        public required Prepared Cpu;
        public required TerrainTextures Textures;
        public uint Vbo, Ebo;
    }

    private readonly GL _gl;
    private readonly uint _vao;
    private readonly uint _vbo;
    private readonly uint _ebo;

    public int Col { get; }
    public int Row { get; }
    public int VertexCount { get; }
    public int IndexCount { get; }
    public int HoleCells { get; }

    public Vector3 BoundsMin { get; }
    public Vector3 BoundsMax { get; }

    /// <summary>Tileset array + alpha atlas for this tile. Bound before Draw.</summary>
    public TerrainTextures? Textures { get; private set; }

    public int TriangleCount => IndexCount / 3;

    private TerrainTile(
        GL gl, uint vao, uint vbo, uint ebo,
        int col, int row, int vertexCount, int indexCount, int holeCells,
        Vector3 boundsMin, Vector3 boundsMax)
    {
        _gl = gl;
        _vao = vao;
        _vbo = vbo;
        _ebo = ebo;
        Col = col;
        Row = row;
        VertexCount = vertexCount;
        IndexCount = indexCount;
        HoleCells = holeCells;
        BoundsMin = boundsMin;
        BoundsMax = boundsMax;
    }

    private void AttachTextures(TerrainTextures textures) => Textures = textures;

    /// <summary>
    /// Read an ADT out of the MPQs and upload it. Returns null when the tile
    /// doesn't exist (ocean, off-continent) or carries no height data.
    /// </summary>
    public static TerrainTile? Load(
        GL gl, AdtTerrainReader.AdtResult? adt, string clientDataPath, int col, int row)
    {
        var prepared = Prepare(adt, clientDataPath, col, row);
        if (prepared is null) return null;
        return Adopt(gl, Upload(gl, gl, prepared));
    }

    public static Prepared? Prepare(
        AdtTerrainReader.AdtResult? adt, string clientDataPath, int col, int row)
    {
        // The ADT arrives already parsed. It used to be read here, and again by
        // the height grid, and again by the building and doodad loaders — four
        // parses of one file per tile. AdtCache does it once now; the tile
        // index inversion (ReadFromMpq takes row, col) lives there too.
        if (adt?.Chunks == null || adt.Chunks.Length == 0) return null;

        var textures = TerrainTextures.Prepare(adt, clientDataPath, col, row);

        double originX = (32 - row) * 533.33333;
        double originY = (32 - col) * 533.33333;
        const float cell = AdtTerrainReader.CELL_SIZE;   // 33.3333 / 8

        var vertices = new List<float>(256 * 145 * FloatsPerVertex);
        var indices = new List<uint>(256 * 64 * 12);

        int holeCells = 0;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        foreach (var chunk in adt.Chunks)
        {
            if (chunk?.Heights == null) continue;

            uint baseVertex = (uint)(vertices.Count / FloatsPerVertex);

            // Layer indices are per-CHUNK but stored per-vertex: all 145 of a
            // chunk's vertices carry the same four values, so the fragment
            // shader can index the tileset array without a per-chunk draw call.
            int chunkIndex = chunk.IndexY * TerrainTextures.ChunksPerSide + chunk.IndexX;
            var chunkLayers = (uint)chunkIndex < (uint)textures.ChunkLayers.Length
                ? textures.ChunkLayers[chunkIndex]
                : new[] { -1, -1, -1, -1 };

            // Emit in MCVT's own interleaved order (9 outer, 8 inner, 9, 8, ...)
            // so vertex i lines up with normal i.
            for (int row9 = 0; row9 < 9; row9++)
            {
                for (int col9 = 0; col9 < 9; col9++)
                {
                    int idx = row9 * 17 + col9;
                    AddVertex(vertices, chunk, adt, originX, originY, cell,
                              chunk.IndexY * 8 + row9, chunk.IndexX * 8 + col9,
                              chunk.Heights[idx], idx, chunkLayers, ref min, ref max);
                }

                if (row9 == 8) break;

                for (int col8 = 0; col8 < 8; col8++)
                {
                    int idx = row9 * 17 + 9 + col8;
                    AddVertex(vertices, chunk, adt, originX, originY, cell,
                              chunk.IndexY * 8 + row9 + 0.5, chunk.IndexX * 8 + col8 + 0.5,
                              chunk.Heights[idx], idx, chunkLayers, ref min, ref max);
                }
            }

            for (int r = 0; r < 8; r++)
            for (int c = 0; c < 8; c++)
            {
                if (chunk.IsHole(c, r)) { holeCells++; continue; }

                uint tl = baseVertex + (uint)(r * 17 + c);
                uint tr = baseVertex + (uint)(r * 17 + c + 1);
                uint bl = baseVertex + (uint)((r + 1) * 17 + c);
                uint br = baseVertex + (uint)((r + 1) * 17 + c + 1);
                uint mid = baseVertex + (uint)(r * 17 + 9 + c);

                // Counter-clockwise seen from +Z (WoW up), matching the
                // GL_CCW front face set in ClientWindow.
                indices.AddRange([tl, mid, tr]);
                indices.AddRange([tr, mid, br]);
                indices.AddRange([br, mid, bl]);
                indices.AddRange([bl, mid, tl]);
            }
        }

        if (vertices.Count == 0 || indices.Count == 0) return null;

        var vertexArray = vertices.ToArray();
        var indexArray = indices.ToArray();

        return new Prepared
        {
            Vertices = vertexArray,
            Indices = indexArray,
            Textures = textures,
            Col = col,
            Row = row,
            HoleCells = holeCells,
            BoundsMin = min,
            BoundsMax = max,
        };
    }

    public static unsafe Uploaded Upload(GL gl, GL ownerGl, Prepared prepared)
    {
        var uploaded = new Uploaded
        {
            Cpu = prepared,
            Textures = TerrainTextures.Upload(gl, prepared.Textures, ownerGl),
            Vbo = gl.GenBuffer(),
            Ebo = gl.GenBuffer(),
        };

        gl.BindBuffer(BufferTargetARB.ArrayBuffer, uploaded.Vbo);
        fixed (float* p = prepared.Vertices)
            gl.BufferData(BufferTargetARB.ArrayBuffer,
                (nuint)(prepared.Vertices.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);

        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, uploaded.Ebo);
        fixed (uint* p = prepared.Indices)
            gl.BufferData(BufferTargetARB.ElementArrayBuffer,
                (nuint)(prepared.Indices.Length * sizeof(uint)), p, BufferUsageARB.StaticDraw);

        return uploaded;
    }

    public static unsafe TerrainTile Adopt(GL gl, Uploaded uploaded)
    {
        uint vao = gl.GenVertexArray();
        gl.BindVertexArray(vao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, uploaded.Vbo);
        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, uploaded.Ebo);

        const uint stride = FloatsPerVertex * sizeof(float);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        gl.EnableVertexAttribArray(2);
        gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, (void*)(6 * sizeof(float)));
        gl.EnableVertexAttribArray(3);
        gl.VertexAttribPointer(3, 4, VertexAttribPointerType.Float, false, stride, (void*)(8 * sizeof(float)));
        gl.BindVertexArray(0);

        var p = uploaded.Cpu;
        Console.WriteLine(
            $"[terrain] tile [{p.Col},{p.Row}] adopted: {p.Vertices.Length / FloatsPerVertex} verts, " +
            $"{p.Indices.Length / 3} tris, {p.HoleCells} hole cells, " +
            $"z {p.BoundsMin.Z:F1}..{p.BoundsMax.Z:F1}");

        var tile = new TerrainTile(gl, vao, uploaded.Vbo, uploaded.Ebo, p.Col, p.Row,
            p.Vertices.Length / FloatsPerVertex, p.Indices.Length, p.HoleCells,
            p.BoundsMin, p.BoundsMax);
        tile.AttachTextures(uploaded.Textures);
        return tile;
    }

    private static void AddVertex(
        List<float> buffer, AdtTerrainReader.McnkChunk chunk, AdtTerrainReader.AdtResult adt,
        double originX, double originY, float cell,
        double gridRow, double gridCol, float relativeHeight, int mcvtIndex,
        int[] chunkLayers, ref Vector3 min, ref Vector3 max)
    {
        float wx = (float)(originX - gridRow * cell);
        float wy = (float)(originY - gridCol * cell);
        float wz = chunk.BaseZ + relativeHeight;

        buffer.Add(wx);
        buffer.Add(wy);
        buffer.Add(wz);

        var n = chunk.NormalAt(mcvtIndex);
        var normal = Vector3.Normalize(new Vector3(n.X, n.Y, n.Z));
        // Degenerate normals appear on a few chunks; point them up rather than
        // letting a NaN poison the lighting.
        if (float.IsNaN(normal.X)) normal = Vector3.UnitZ;
        buffer.Add(normal.X);
        buffer.Add(normal.Y);
        buffer.Add(normal.Z);

        // UV across the whole tile, so one splat texture maps on with no
        // per-chunk atlas maths when texturing lands.
        buffer.Add((float)(gridCol / 128.0));
        buffer.Add((float)(gridRow / 128.0));

        // Tileset array indices for this chunk; -1 marks an unused slot.
        buffer.Add(chunkLayers[0]);
        buffer.Add(chunkLayers[1]);
        buffer.Add(chunkLayers[2]);
        buffer.Add(chunkLayers[3]);

        min = Vector3.Min(min, new Vector3(wx, wy, wz));
        max = Vector3.Max(max, new Vector3(wx, wy, wz));

        _ = adt;
    }

    public unsafe void Draw()
    {
        Textures?.Bind();
        _gl.BindVertexArray(_vao);
        _gl.DrawElements(PrimitiveType.Triangles, (uint)IndexCount, DrawElementsType.UnsignedInt, (void*)0);
    }

    public void Dispose()
    {
        Textures?.Dispose();
        _gl.DeleteVertexArray(_vao);
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteBuffer(_ebo);
    }
}
