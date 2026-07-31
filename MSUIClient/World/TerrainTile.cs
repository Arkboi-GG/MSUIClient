using System.Numerics;
using Silk.NET.OpenGL;
using MSUIClient.Engine;
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
    /// <summary>
    /// Position(3) + Normal(3) + ChunkUV(2) + LayerIndices(4) + AlphaLayer(1),
    /// interleaved.
    ///
    /// THE UV IS PER CHUNK NOW, NOT PER TILE. It used to run 0..1 across the
    /// whole ADT, which meant the fragment shader had to wrap it with fract() to
    /// get a tiling coordinate — and a fract() inside a triangle is a derivative
    /// cliff that made every chunk edge sample the deepest mip. A per-chunk 0..1
    /// coordinate needs no wrap: tiling is a multiply, GL_REPEAT does the rest,
    /// and the discontinuity between chunks sits on a vertex boundary between
    /// two separate triangles, where derivatives stay correct on both sides.
    ///
    /// AlphaLayer is this vertex's chunk index, addressing its own layer of the
    /// alpha array texture. Constant across a chunk, so it interpolates back to
    /// itself and the shader reads it flat.
    /// </summary>
    private const int FloatsPerVertex = 13;

    /// <summary>
    /// One MCNK's slice of the tile's index buffer, plus its own bounds.
    ///
    /// WHY THIS EXISTS. Culling used to be per TILE — a 533-yard box. One corner
    /// of that in the frustum submitted the whole ~32,700-triangle mesh, so at a
    /// 59-degree field of view most of what reached the rasteriser was behind the
    /// camera or off to the side. Worse, the triangles that did miss the screen
    /// were mostly the distant, sub-pixel ones, which are the single worst thing
    /// to hand a tiled binner: each still costs a 2x2 quad.
    ///
    /// Chunks are emitted contiguously by Prepare, so a chunk is a contiguous
    /// index range and neighbouring visible chunks merge into one draw call.
    /// Vanilla culled per MCNK too.
    /// </summary>
    public readonly struct ChunkRange(int indexStart, int indexCount, Vector3 min, Vector3 max)
    {
        public readonly int IndexStart = indexStart;
        public readonly int IndexCount = indexCount;
        public readonly Vector3 Min = min;
        public readonly Vector3 Max = max;
    }

    public sealed class Prepared
    {
        public required float[] Vertices;
        public required uint[] Indices;
        public int VertexFloatCount;
        public int IndexCount;
        public required TerrainTextures.Prepared Textures;
        public required ChunkRange[] Chunks;
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

    /// <summary>Per-MCNK index ranges and bounds, for the frustum test in Draw.</summary>
    private readonly ChunkRange[] _chunks;

    public int Col { get; }
    public int Row { get; }
    public int VertexCount { get; }
    public int IndexCount { get; }
    public int HoleCells { get; }

    public Vector3 BoundsMin { get; }
    public Vector3 BoundsMax { get; }

    /// <summary>Tileset array + per-chunk alpha array for this tile. Bound before Draw.</summary>
    public TerrainTextures? Textures { get; private set; }

    public int TriangleCount => IndexCount / 3;

    private TerrainTile(
        GL gl, uint vao, uint vbo, uint ebo,
        int col, int row, int vertexCount, int indexCount, int holeCells,
        Vector3 boundsMin, Vector3 boundsMax, ChunkRange[] chunks)
    {
        _gl = gl;
        _vao = vao;
        _vbo = vbo;
        _ebo = ebo;
        _chunks = chunks;
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

        var vertices = new float[256 * 145 * FloatsPerVertex];
        var indices = new uint[256 * 64 * 12];
        int vertexFloatCount = 0, indexCount = 0;

        int holeCells = 0;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        var chunkRanges = new List<ChunkRange>(256);

        foreach (var chunk in adt.Chunks)
        {
            if (chunk?.Heights == null) continue;

            uint baseVertex = (uint)(vertexFloatCount / FloatsPerVertex);

            // This chunk's own bounds, accumulated by AddVertex alongside the
            // tile-wide pair, and its slice of the index buffer.
            int chunkIndexStart = indexCount;
            var chunkMin = new Vector3(float.MaxValue);
            var chunkMax = new Vector3(float.MinValue);

            // Layer indices are per-CHUNK but stored per-vertex: all 145 of a
            // chunk's vertices carry the same four values, so the fragment
            // shader can index the tileset array without a per-chunk draw call.
            int chunkIndex = chunk.IndexY * TerrainTextures.ChunksPerSide + chunk.IndexX;
            bool chunkIndexValid = (uint)chunkIndex < (uint)textures.ChunkLayers.Length;
            var chunkLayers = chunkIndexValid
                ? textures.ChunkLayers[chunkIndex]
                : new[] { -1, -1, -1, -1 };

            // The same index also selects this chunk's layer of the alpha array,
            // so it has to pass the same guard. A malformed ADT with IndexY 16
            // would otherwise emit layer 256, which GL clamps to 255 — silently
            // blending against a completely different chunk's mask instead of
            // falling back to the base texture. Out of range means layer 0,
            // which is zero-filled, which means "base only".
            float alphaLayer = chunkIndexValid ? chunkIndex : 0;

            // Emit in MCVT's own interleaved order (9 outer, 8 inner, 9, 8, ...)
            // so vertex i lines up with normal i.
            for (int row9 = 0; row9 < 9; row9++)
            {
                for (int col9 = 0; col9 < 9; col9++)
                {
                    int idx = row9 * 17 + col9;
                    AddVertex(vertices, ref vertexFloatCount, chunk, adt, originX, originY, cell,
                              chunk.IndexY * 8 + row9, chunk.IndexX * 8 + col9,
                              chunk.Heights[idx], idx, chunkLayers, alphaLayer, ref min, ref max);
                }

                if (row9 == 8) break;

                for (int col8 = 0; col8 < 8; col8++)
                {
                    int idx = row9 * 17 + 9 + col8;
                    AddVertex(vertices, ref vertexFloatCount, chunk, adt, originX, originY, cell,
                              chunk.IndexY * 8 + row9 + 0.5, chunk.IndexX * 8 + col8 + 0.5,
                              chunk.Heights[idx], idx, chunkLayers, alphaLayer, ref min, ref max);
                }
            }

            // Chunk bounds from the vertices just emitted. Reading them back out
            // of the buffer rather than threading two more ref params through
            // AddVertex keeps the one place that computes a world position the
            // one place that computes it.
            for (int v = (int)baseVertex * FloatsPerVertex; v < vertexFloatCount; v += FloatsPerVertex)
            {
                var p = new Vector3(vertices[v], vertices[v + 1], vertices[v + 2]);
                chunkMin = Vector3.Min(chunkMin, p);
                chunkMax = Vector3.Max(chunkMax, p);
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
                indices[indexCount++] = tl; indices[indexCount++] = mid; indices[indexCount++] = tr;
                indices[indexCount++] = tr; indices[indexCount++] = mid; indices[indexCount++] = br;
                indices[indexCount++] = br; indices[indexCount++] = mid; indices[indexCount++] = bl;
                indices[indexCount++] = bl; indices[indexCount++] = mid; indices[indexCount++] = tl;
            }

            // A chunk that is entirely holes emits no indices and is simply not
            // a drawable range.
            int chunkIndexCount = indexCount - chunkIndexStart;
            if (chunkIndexCount > 0)
                chunkRanges.Add(new ChunkRange(chunkIndexStart, chunkIndexCount, chunkMin, chunkMax));
        }

        if (vertexFloatCount == 0 || indexCount == 0) return null;

        return new Prepared
        {
            Vertices = vertices,
            Indices = indices,
            VertexFloatCount = vertexFloatCount,
            IndexCount = indexCount,
            Textures = textures,
            Chunks = [.. chunkRanges],
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
                    (nuint)(prepared.VertexFloatCount * sizeof(float)), p, BufferUsageARB.StaticDraw);

        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, uploaded.Ebo);
        fixed (uint* p = prepared.Indices)
            gl.BufferData(BufferTargetARB.ElementArrayBuffer,
                    (nuint)(prepared.IndexCount * sizeof(uint)), p, BufferUsageARB.StaticDraw);

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
        gl.EnableVertexAttribArray(4);
        gl.VertexAttribPointer(4, 1, VertexAttribPointerType.Float, false, stride, (void*)(12 * sizeof(float)));
        gl.BindVertexArray(0);

        var p = uploaded.Cpu;
        Console.WriteLine(
            $"[terrain] tile [{p.Col},{p.Row}] adopted: {p.VertexFloatCount / FloatsPerVertex} verts, " +
            $"{p.IndexCount / 3} tris, {p.HoleCells} hole cells, " +
            $"z {p.BoundsMin.Z:F1}..{p.BoundsMax.Z:F1}");

        var tile = new TerrainTile(gl, vao, uploaded.Vbo, uploaded.Ebo, p.Col, p.Row,
            p.VertexFloatCount / FloatsPerVertex, p.IndexCount, p.HoleCells,
            p.BoundsMin, p.BoundsMax, p.Chunks);
        tile.AttachTextures(uploaded.Textures);
        return tile;
    }

    private static void AddVertex(
        float[] buffer, ref int count, AdtTerrainReader.McnkChunk chunk,
        AdtTerrainReader.AdtResult adt,
        double originX, double originY, float cell,
        double gridRow, double gridCol, float relativeHeight, int mcvtIndex,
        int[] chunkLayers, float alphaLayer, ref Vector3 min, ref Vector3 max)
    {
        float wx = (float)(originX - gridRow * cell);
        float wy = (float)(originY - gridCol * cell);
        float wz = chunk.BaseZ + relativeHeight;

        buffer[count++] = wx;
        buffer[count++] = wy;
        buffer[count++] = wz;

        var n = chunk.NormalAt(mcvtIndex);
        var normal = Vector3.Normalize(new Vector3(n.X, n.Y, n.Z));
        // Degenerate normals appear on a few chunks; point them up rather than
        // letting a NaN poison the lighting.
        if (float.IsNaN(normal.X)) normal = Vector3.UnitZ;
        buffer[count++] = normal.X;
        buffer[count++] = normal.Y;
        buffer[count++] = normal.Z;

        // UV within THIS CHUNK, 0..1 over its 8 cells. gridRow/gridCol are
        // tile-wide, so subtracting the chunk's own origin gives 0..8 (inner
        // MCVT vertices land on the halves) and dividing by 8 normalises it.
        //
        // U follows the column axis and V the row axis, which is the same
        // orientation the alpha masks are written in (TerrainTextures maps px to
        // the column and py to the row), so the mask lines up with no transform.
        buffer[count++] = (float)((gridCol - chunk.IndexX * 8) / 8.0);
        buffer[count++] = (float)((gridRow - chunk.IndexY * 8) / 8.0);

        // Tileset array indices for this chunk; -1 marks an unused slot.
        buffer[count++] = chunkLayers[0];
        buffer[count++] = chunkLayers[1];
        buffer[count++] = chunkLayers[2];
        buffer[count++] = chunkLayers[3];

        // This chunk's layer in the alpha array. Same index TerrainTextures
        // packed it at, range-checked by the caller, and the reason a
        // neighbour's mask is now unreachable at any UV.
        buffer[count++] = alphaLayer;

        min = Vector3.Min(min, new Vector3(wx, wy, wz));
        max = Vector3.Max(max, new Vector3(wx, wy, wz));

        _ = adt;
    }

    /// <summary>Draw the whole tile. The pre-chunk-culling path, kept for tools.</summary>
    public unsafe void Draw()
    {
        Textures?.Bind();
        _gl.BindVertexArray(_vao);
        _gl.DrawElements(PrimitiveType.Triangles, (uint)IndexCount, DrawElementsType.UnsignedInt, (void*)0);
        TrianglesDrawnLastCall = TriangleCount;
        DrawCallsLastCall = 1;
    }

    /// <summary>
    /// Draw only the MCNKs that survive the frustum, merging neighbours into as
    /// few calls as possible.
    ///
    /// Chunks are contiguous in the index buffer, so a run of visible ones is a
    /// single range. In practice the visible set of a tile is spatially coherent,
    /// so this is a handful of calls rather than 256 — and on a tile that is
    /// half off-screen it submits about half the triangles, which is the point.
    /// </summary>
    public unsafe void Draw(Matrix4x4 viewProjection, Vector3 cameraPosition)
    {
        if (_chunks.Length == 0) { Draw(); return; }

        Textures?.Bind();
        _gl.BindVertexArray(_vao);

        int triangles = 0, calls = 0;
        int runStart = -1, runEnd = 0;

        for (int i = 0; i < _chunks.Length; i++)
        {
            ref readonly var chunk = ref _chunks[i];

            bool visible = Camera.BoxInFrustum(viewProjection,
                chunk.Min - cameraPosition,
                chunk.Max - cameraPosition);

            if (visible)
            {
                // Extend the current run, or open one. Runs stay contiguous:
                // a gap closes the run rather than being drawn through, or we
                // would be submitting the chunks we just rejected.
                if (runStart < 0) { runStart = chunk.IndexStart; runEnd = chunk.IndexStart + chunk.IndexCount; }
                else if (chunk.IndexStart == runEnd) { runEnd = chunk.IndexStart + chunk.IndexCount; }
                else
                {
                    Flush(runStart, runEnd, ref triangles, ref calls);
                    runStart = chunk.IndexStart;
                    runEnd = chunk.IndexStart + chunk.IndexCount;
                }
            }
            else if (runStart >= 0)
            {
                Flush(runStart, runEnd, ref triangles, ref calls);
                runStart = -1;
            }
        }

        if (runStart >= 0) Flush(runStart, runEnd, ref triangles, ref calls);

        TrianglesDrawnLastCall = triangles;
        DrawCallsLastCall = calls;
    }

    private unsafe void Flush(int start, int end, ref int triangles, ref int calls)
    {
        int count = end - start;
        if (count <= 0) return;

        _gl.DrawElements(PrimitiveType.Triangles, (uint)count,
            DrawElementsType.UnsignedInt, (void*)((nint)start * sizeof(uint)));

        triangles += count / 3;
        calls++;
    }

    /// <summary>Triangles and draw calls the last Draw actually submitted.</summary>
    public int TrianglesDrawnLastCall { get; private set; }
    public int DrawCallsLastCall { get; private set; }

    public void Dispose()
    {
        Textures?.Dispose();
        _gl.DeleteVertexArray(_vao);
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteBuffer(_ebo);
    }
}
