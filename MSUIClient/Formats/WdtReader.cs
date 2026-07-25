using System.Text;

namespace MSUIClient.Formats;

/// <summary>
/// Reader for WDT — the per-map table of contents.
///
/// WHY THE CLIENT NEEDS THIS
///   A map is not "an ADT directory". The WDT is what says which of the 64x64
///   tiles exist at all, and — for a whole class of maps — that there are no
///   tiles and the world is one WMO placed at the origin. Without it, entering
///   a dungeon means guessing at filenames.
///
/// THE FINDING THAT SHAPED THIS CLASS (PLAN_13_INSTANCES.md section 1)
///   The obvious mental model is "a dungeon is one big WMO". It is WRONG for
///   four of the dungeons this project cares about most. Read out of the
///   archives, instance maps come in two structurally different kinds:
///
///     MPHD flags 0x0001  ->  0 ADT tiles, 1 MODF.  Wailing Caverns, the
///                            Stockade, Gnomeregan, Blackrock Depths, Uldaman,
///                            Sunken Temple, Onyxia, Molten Core.
///     MPHD flags 0x0000  ->  real tiles, 0 MODF.   Deadmines (36 tiles),
///                            Shadowfang (25), Scarlet Monastery (36),
///                            Razorfen Kraul (6).
///
///   Deadmines — the handbook's stated dungeon target — is a TERRAIN map, and
///   loads through the same path Azeroth does with fewer tiles. So the branch
///   that matters downstream is UsesGlobalWmo, never "is this a dungeon".
///
/// FORMAT
///   IFF-style, same as ADT: char[4] magic (stored reversed) + uint32 size +
///   data. Chunks:
///     MVER  version, 18 for 1.12
///     MPHD  32 bytes of flags; bit 0 = "this map is one global WMO"
///     MAIN  64*64 entries of 8 bytes; entry [y*64 + x], low bit of the first
///           uint32 means "an ADT exists for this tile"
///     MWMO  null-separated WMO path strings (empty on terrain maps)
///     MODF  one 64-byte placement, present only on global-WMO maps
///
/// THE TILE CONVENTION, verified rather than assumed
///   MAIN's x is the client's COL and MAIN's y is the client's ROW. Confirmed
///   by following AdtCache.Get(col, row) into ReadFromMpq(..., gridX: row,
///   gridY: col) into the path {map}_{gridY}_{gridX}.adt, which is
///   {map}_{col}_{row}.adt. Getting this backwards transposes every map about
///   its diagonal, which for the square dungeon maps looks like nothing at all.
///
/// NO GL, NO GAME LOGIC — Formats/ rule, same as every other reader here.
/// </summary>
public sealed class WdtFile
{
    /// <summary>MPHD flag bit 0: the map's content is one WMO, placed by MODF.</summary>
    public const uint FlagGlobalWmo = 0x0001;

    public uint Version { get; private set; }
    public uint Flags { get; private set; }

    /// <summary>
    /// True when MPHD bit 0 is set. On these maps there is NO terrain at all:
    /// no heightmap, no MCLQ liquid, no ground effects, no terrain collision.
    /// Anything that reads terrain must tolerate never getting an answer.
    /// </summary>
    public bool UsesGlobalWmo => (Flags & FlagGlobalWmo) != 0;

    private readonly bool[] _has = new bool[64 * 64];
    private readonly List<(int Col, int Row)> _tiles = [];

    /// <summary>Every (col, row) MAIN says has an ADT, in row-major MAIN order.</summary>
    public IReadOnlyList<(int Col, int Row)> Tiles => _tiles;
    public int TileCount => _tiles.Count;

    public int MinCol { get; private set; } = -1;
    public int MaxCol { get; private set; } = -1;
    public int MinRow { get; private set; } = -1;
    public int MaxRow { get; private set; } = -1;

    /// <summary>The single MWMO path on a global-WMO map; null on terrain maps.</summary>
    public string? GlobalWmoPath { get; private set; }

    /// <summary>
    /// The single MODF placement on a global-WMO map. Reuses the ADT reader's
    /// record type on purpose — MODF is the same 64 bytes in both files, and a
    /// second copy of that struct is a second thing to get wrong.
    ///
    /// MEASURED ACROSS ALL 21 GLOBAL-WMO MAPS IN 1.12, every one of these
    /// placements is degenerate in the same way:
    ///
    ///     nameId 0   uniqueId 0xFFFFFFFF   flags 0   doodadSet 0   nameSet 0
    ///     position (0,0,0)                 rotation (0,0,0)
    ///
    /// The bounding box is the ONLY field that varies. So stage 3's placement
    /// maths has nothing to get wrong except the box, and a global-WMO map that
    /// renders in the wrong place is a bug in the transform, not in the data.
    ///
    /// uniqueId is -1 rather than a real id. Nothing here reads it, and
    /// AdtTerrainReader.ParseModf leaves it commented out for the ADT case too,
    /// so no instance key collides on it. Worth knowing before something starts
    /// keying placements by uniqueId.
    /// </summary>
    public AdtTerrainReader.WmoInstance? GlobalWmo { get; private set; }

    public bool HasTile(int col, int row)
        => (uint)col < 64 && (uint)row < 64 && _has[row * 64 + col];

    /// <summary>
    /// Centre of the occupied tile block, rounded down. Meaningless when
    /// TileCount is 0 — check first.
    /// </summary>
    public (int Col, int Row) CentreTile
        => _tiles.Count == 0 ? (32, 32) : ((MinCol + MaxCol) / 2, (MinRow + MaxRow) / 2);

    /// <summary>
    /// The tile to actually put somebody on: CentreTile when it has an ADT,
    /// otherwise the occupied tile closest to it.
    ///
    /// These are not always the same tile, and assuming they are is a real bug
    /// rather than a theoretical one. `development` occupies 18 tiles spread
    /// across col 0..63, and the centre of that block is [31,1] - which has no
    /// ADT. Any map whose tiles do not form a solid rectangle can do this.
    /// </summary>
    public (int Col, int Row) SpawnTile
    {
        get
        {
            var (cc, cr) = CentreTile;
            if (_tiles.Count == 0 || HasTile(cc, cr)) return (cc, cr);

            var best = _tiles[0];
            long bestD = long.MaxValue;
            foreach (var t in _tiles)
            {
                long dc = t.Col - cc, dr = t.Row - cr;
                long d = dc * dc + dr * dr;
                if (d >= bestD) continue;
                bestD = d;
                best = t;
            }
            return best;
        }
    }

    /// <summary>
    /// Read a map's WDT out of the archives. mapDirectory is Map.dbc's
    /// Directory column ("Azeroth", "DeadminesInstance"), and the file is
    /// always World\Maps\{dir}\{dir}.wdt.
    /// </summary>
    public static WdtFile? Read(string clientDataPath, string mapDirectory)
    {
        if (string.IsNullOrWhiteSpace(mapDirectory)) return null;
        string path = $@"World\Maps\{mapDirectory}\{mapDirectory}.wdt";
        var bytes = AdtTerrainReader.ReadFileFromMpqs(clientDataPath, path);
        return bytes is null ? null : Parse(bytes);
    }

    public static WdtFile? Parse(byte[] data)
    {
        if (data.Length < 8) return null;

        var wdt = new WdtFile();
        var mwmoByOffset = new Dictionary<uint, string>();
        int modfOffset = -1, modfSize = 0;
        bool sawAnyChunk = false;

        int pos = 0;
        while (pos + 8 <= data.Length)
        {
            uint magic = BitConverter.ToUInt32(data, pos);
            uint size = BitConverter.ToUInt32(data, pos + 4);
            int body = pos + 8;
            if (size > int.MaxValue || body + (long)size > data.Length) break;
            sawAnyChunk = true;

            if (magic == MagicMver)
            {
                if (size >= 4) wdt.Version = BitConverter.ToUInt32(data, body);

                // Every one of the 44 WDTs in 1.12 is version 18. SuperUI's
                // reader REJECTS anything else outright; this one warns and
                // carries on, because a panel row reading "MVER 21" names the
                // problem and a null does not. If this ever fires, stop
                // trusting MAIN's layout - that is what the version guards.
                if (wdt.Version != 0 && wdt.Version != 18)
                    Console.WriteLine($"[wdt] MVER {wdt.Version}, expected 18 - " +
                                      "this is not vanilla data and MAIN may not be 64x64x8");
            }
            else if (magic == MagicMphd)
            {
                if (size >= 4) wdt.Flags = BitConverter.ToUInt32(data, body);
            }
            else if (magic == MagicMain)
            {
                wdt.ReadMain(data, body, (int)size);
            }
            else if (magic == MagicMwmo)
            {
                ReadStringBlock(data, body, (int)size, mwmoByOffset);
            }
            else if (magic == MagicModf)
            {
                modfOffset = body;
                modfSize = (int)size;
            }

            pos = body + (int)size;
        }

        if (!sawAnyChunk) return null;

        // MODF last, so MWMO is populated whatever order they appear in.
        if (modfOffset >= 0 && modfSize >= 64)
            wdt.ReadModf(data, modfOffset, mwmoByOffset);

        if (wdt.GlobalWmo is not null)
            wdt.GlobalWmoPath = wdt.GlobalWmo.ModelPath;
        else if (mwmoByOffset.TryGetValue(0u, out var only))
            wdt.GlobalWmoPath = only;

        return wdt;
    }

    private void ReadMain(byte[] data, int offset, int size)
    {
        const int entry = 8;
        int count = Math.Min(size / entry, 64 * 64);
        for (int i = 0; i < count; i++)
        {
            uint flags = BitConverter.ToUInt32(data, offset + i * entry);
            if ((flags & 1) == 0) continue;

            // MAIN is indexed [y * 64 + x]; MAIN x is the client's col and
            // MAIN y is the client's row. See the class summary.
            int col = i % 64;
            int row = i / 64;

            _has[row * 64 + col] = true;
            _tiles.Add((col, row));

            if (MinCol < 0 || col < MinCol) MinCol = col;
            if (MaxCol < 0 || col > MaxCol) MaxCol = col;
            if (MinRow < 0 || row < MinRow) MinRow = row;
            if (MaxRow < 0 || row > MaxRow) MaxRow = row;
        }
    }

    private void ReadModf(byte[] data, int pos, Dictionary<uint, string> mwmoByOffset)
    {
        uint nameId = BitConverter.ToUInt32(data, pos);

        // The WDT has no MWID, so nameId is a direct byte offset into MWMO.
        // These files carry exactly one string at offset 0, so the fallback is
        // never expected to fire — but a silent "Unknown_0" is a better failure
        // than an exception, and it shows up in the panel.
        string path = mwmoByOffset.TryGetValue(nameId, out var found)
            ? found
            : (mwmoByOffset.TryGetValue(0u, out var first) ? first : $"Unknown_{nameId}");

        GlobalWmo = new AdtTerrainReader.WmoInstance
        {
            ModelPath = path,
            PosX = BitConverter.ToSingle(data, pos + 8),
            PosY = BitConverter.ToSingle(data, pos + 12),
            PosZ = BitConverter.ToSingle(data, pos + 16),
            RotX = BitConverter.ToSingle(data, pos + 20),
            RotY = BitConverter.ToSingle(data, pos + 24),
            RotZ = BitConverter.ToSingle(data, pos + 28),
            BbMinX = BitConverter.ToSingle(data, pos + 32),
            BbMinY = BitConverter.ToSingle(data, pos + 36),
            BbMinZ = BitConverter.ToSingle(data, pos + 40),
            BbMaxX = BitConverter.ToSingle(data, pos + 44),
            BbMaxY = BitConverter.ToSingle(data, pos + 48),
            BbMaxZ = BitConverter.ToSingle(data, pos + 52),
            DoodadSet = BitConverter.ToUInt16(data, pos + 58),
        };
    }

    /// <summary>Null-separated ASCII strings, keyed by byte offset within the chunk.</summary>
    private static void ReadStringBlock(byte[] data, int offset, int size, Dictionary<uint, string> into)
    {
        int start = 0;
        for (int i = 0; i < size; i++)
        {
            if (data[offset + i] != 0) continue;
            if (i > start)
                into[(uint)start] = Encoding.ASCII.GetString(data, offset + start, i - start);
            start = i + 1;
        }
        if (start < size)
            into[(uint)start] = Encoding.ASCII.GetString(data, offset + start, size - start);
    }

    // Chunk magic is stored reversed in the file: logical "MVER" is bytes
    // R,E,V,M. Same helper shape as AdtTerrainReader.ChunkId.
    private static uint ChunkId(string id)
    {
        byte[] b = Encoding.ASCII.GetBytes(id);
        Array.Reverse(b);
        return BitConverter.ToUInt32(b, 0);
    }

    private static readonly uint MagicMver = ChunkId("MVER");
    private static readonly uint MagicMphd = ChunkId("MPHD");
    private static readonly uint MagicMain = ChunkId("MAIN");
    private static readonly uint MagicMwmo = ChunkId("MWMO");
    private static readonly uint MagicModf = ChunkId("MODF");
}
