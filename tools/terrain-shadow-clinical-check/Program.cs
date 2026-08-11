using System.Buffers;
using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using MSUIClient.Formats;
using MSUIClient.World;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidDataException(message);
}

static void WriteU32(byte[] data, int offset, uint value) =>
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, sizeof(uint)), value);

static void WriteMagic(byte[] data, int offset, string logicalFourCc)
{
    byte[] bytes = Encoding.ASCII.GetBytes(logicalFourCc);
    Array.Reverse(bytes); // ADT stores logical MCSH as HSCM bytes.
    bytes.CopyTo(data, offset);
}

const int McnkDataStart = 8;
const int McnkHeaderBytes = 128;
const int McshOffset = McnkDataStart + McnkHeaderBytes; // relative to MCNK magic

static byte[] BuildWrappedMcsh(
    int actualPayloadBytes = AdtTerrainReader.MCSH_PACKED_BYTES,
    uint headerShadowSize = AdtTerrainReader.MCSH_PACKED_BYTES,
    uint declaredPayloadSize = AdtTerrainReader.MCSH_PACKED_BYTES)
{
    byte[] data = new byte[McshOffset + 8 + actualPayloadBytes];
    WriteMagic(data, 0, "MCNK");
    WriteU32(data, 4, (uint)(data.Length - McnkDataStart));
    WriteU32(data, McnkDataStart + 0x2C, McshOffset);
    WriteU32(data, McnkDataStart + 0x30, headerShadowSize);
    WriteMagic(data, McshOffset, "MCSH");
    WriteU32(data, McshOffset + 4, declaredPayloadSize);
    return data;
}

MethodInfo parseMcnk = typeof(AdtTerrainReader).GetMethod(
    "ParseMcnk", BindingFlags.Static | BindingFlags.NonPublic) ??
    throw new MissingMethodException("AdtTerrainReader.ParseMcnk");

AdtTerrainReader.McnkChunk Parse(byte[] fixture, int? mcnkSize = null) =>
    (AdtTerrainReader.McnkChunk)(parseMcnk.Invoke(
        null, [fixture, McnkDataStart, mcnkSize ?? fixture.Length - McnkDataStart]) ??
        throw new InvalidDataException("ParseMcnk returned null"));

// The packed format is row-major and LSB-first within each byte.
byte[] packed = new byte[AdtTerrainReader.MCSH_PACKED_BYTES];
packed[0] = 0b1000_0001;       // (0,0), (7,0)
packed[1] = 0b0000_0001;       // (8,0)
packed[8] = 0b0000_0100;       // (2,1)
packed[^1] = 0b1000_0000;      // (63,63)
byte[] expanded = AdtTerrainReader.DecodeMcsh(packed) ??
    throw new InvalidDataException("valid MCSH payload did not decode");
Check(expanded.Length == AdtTerrainReader.MCSH_TEXEL_BYTES,
    "MCSH did not expand to 64x64 R8");
Check(expanded[0] == 255 && expanded[7] == 255 && expanded[8] == 255 &&
      expanded[64 + 2] == 255 && expanded[^1] == 255 &&
      expanded[1] == 0 && expanded[64] == 0,
    "MCSH row/column or LSB-first expansion drifted");
Check(AdtTerrainReader.DecodeMcsh(new byte[AdtTerrainReader.MCSH_PACKED_BYTES - 1]) is null,
    "short MCSH payload was partially decoded");

// Exercise the real private MCNK parser with an IFF-wrapped MCSH fixture.
byte[] wrapped = BuildWrappedMcsh();
packed.CopyTo(wrapped, McshOffset + 8);
byte[] parsed = Parse(wrapped).ShadowMap ??
    throw new InvalidDataException("valid wrapped MCSH was not parsed from MCNK offsets");
Check(parsed.SequenceEqual(expanded),
    "MCNK MCSH parse changed the decoded shadow texels");

// The tolerated legacy form is a raw 512-byte payload at ofsShadow.
byte[] raw = new byte[McshOffset + AdtTerrainReader.MCSH_PACKED_BYTES];
WriteMagic(raw, 0, "MCNK");
WriteU32(raw, 4, (uint)(raw.Length - McnkDataStart));
WriteU32(raw, McnkDataStart + 0x2C, McshOffset);
WriteU32(raw, McnkDataStart + 0x30, AdtTerrainReader.MCSH_PACKED_BYTES);
packed.CopyTo(raw, McshOffset);
Check(Parse(raw).ShadowMap?.SequenceEqual(expanded) == true,
    "raw MCSH payload compatibility path did not decode");

// All malformed offset/size cases fail closed without reading a neighbour MCNK.
byte[] missing = BuildWrappedMcsh();
WriteU32(missing, McnkDataStart + 0x2C, 0);
WriteU32(missing, McnkDataStart + 0x30, 0);
Check(Parse(missing).ShadowMap is null,
    "missing MCSH offsets invented a shadow map");
byte[] tooSmallHeaderSize = BuildWrappedMcsh(
    headerShadowSize: AdtTerrainReader.MCSH_PACKED_BYTES - 1);
Check(Parse(tooSmallHeaderSize).ShadowMap is null,
    "undersized MCNK sizeShadow was accepted");
byte[] tooSmallDeclared = BuildWrappedMcsh(
    declaredPayloadSize: AdtTerrainReader.MCSH_PACKED_BYTES - 1);
Check(Parse(tooSmallDeclared).ShadowMap is null,
    "undersized MCSH IFF payload was accepted");
byte[] truncated = BuildWrappedMcsh(
    actualPayloadBytes: AdtTerrainReader.MCSH_PACKED_BYTES - 1);
Check(Parse(truncated).ShadowMap is null,
    "truncated MCSH payload was accepted");
Check(Parse(wrapped, wrapped.Length - McnkDataStart - 1).ShadowMap is null,
    "MCSH parser crossed its MCNK boundary into following file bytes");
byte[] hugeOffset = BuildWrappedMcsh();
WriteU32(hugeOffset, McnkDataStart + 0x2C, uint.MaxValue);
Check(Parse(hugeOffset).ShadowMap is null,
    "overflowing MCSH offset was accepted");
byte[] hugeSize = BuildWrappedMcsh(headerShadowSize: uint.MaxValue);
Check(Parse(hugeSize).ShadowMap is null,
    "overflowing MCSH size was accepted");

// CPU texture preparation keeps MCAL RGBA and MCSH R8 in independent buffers.
var alpha = Enumerable.Repeat((byte)17, 64 * 64).ToArray();
var shadow = new byte[64 * 64];
shadow[0] = 255;
var adt = new AdtTerrainReader.AdtResult
{
    Chunks =
    [
        new AdtTerrainReader.McnkChunk
        {
            IndexX = 2,
            IndexY = 3,
            ShadowMap = shadow,
            Layers =
            [
                new AdtTerrainReader.MclyLayer { TextureIndex = 0 },
                new AdtTerrainReader.MclyLayer { TextureIndex = 1, AlphaMap = alpha },
            ],
        },
    ],
};
TerrainTextures.Prepared prepared = TerrainTextures.Prepare(adt, "unused", 0, 0);
try
{
    int chunkIndex = 3 * TerrainTextures.ChunksPerSide + 2;
    int alphaBase = chunkIndex * 64 * 64 * 4;
    int shadowBase = chunkIndex * 64 * 64;
    Check(prepared.AlphaLayers[alphaBase] == 17 &&
          prepared.AlphaLayers[alphaBase + 1] == 0 &&
          prepared.AlphaLayers[alphaBase + 2] == 0 &&
          prepared.AlphaLayers[alphaBase + 3] == 0,
        "MCSH packing changed an MCAL blend channel");
    Check(prepared.ShadowLayers[shadowBase] == 255 &&
          prepared.ShadowLayers[shadowBase + 1] == 0,
        "expanded MCSH did not land in its chunk's R8 array layer");
    Check(prepared.ShadowLayers[shadowBase - 1] == 0,
        "MCSH data leaked into a neighbouring chunk layer");
}
finally
{
    if (prepared.Pooled)
    {
        foreach (byte[] pixels in prepared.Pixels)
            ArrayPool<byte>.Shared.Return(pixels);
        ArrayPool<byte>.Shared.Return(prepared.AlphaLayers);
        ArrayPool<byte>.Shared.Return(prepared.ShadowLayers);
        prepared.Pooled = false;
    }
}

// Optional archive-backed confirmation against the Northshire tile. The
// synthetic fixtures above remain the deterministic check; --live proves the
// same offsets and sizes against the user's untouched 1.12 terrain.MPQ.
if (args.Contains("--live", StringComparer.OrdinalIgnoreCase))
{
    string repoRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    string dataPath = Path.Combine(repoRoot, "GameData", "Data");
    AdtTerrainReader.AdtResult live =
        AdtTerrainReader.ReadFromMpq(dataPath, "Azeroth", 48, 32) ??
        throw new InvalidDataException("could not read Azeroth_32_48.adt from GameData");
    int mappedChunks = live.Chunks?.Count(c => c?.ShadowMap is { Length: 4096 }) ?? 0;
    int shadowedTexels = live.Chunks?
        .Where(c => c?.ShadowMap is not null)
        .Sum(c => c!.ShadowMap!.Count(value => value != 0)) ?? 0;
    Check(mappedChunks > 0 && shadowedTexels > 0,
        "Northshire ADT parsed no authored MCSH shadow data");
    Console.WriteLine(
        $"live Azeroth_32_48: {mappedChunks} MCSH chunk(s), {shadowedTexels:N0} shadowed texel(s)");
}

Console.WriteLine("terrain-shadow-clinical-check: PASS");
