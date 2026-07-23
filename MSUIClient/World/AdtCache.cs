using MSUIClient.Formats;

namespace MSUIClient.World;

/// <summary>
/// Parses each tile's ADT once and hands the same result to everyone who needs it.
///
/// WHY THIS EXISTS
///   Four separate consumers were each reading and parsing the same file:
///   TerrainTile for the mesh, TerrainRenderer for the CPU height grid,
///   WmoRenderer for MODF placements, DoodadRenderer for MDDF placements. Nine
///   tiles therefore cost thirty-six full ADT parses at boot, and an ADT parse
///   is decompression plus 256 chunks of height, normal and alpha data. That is
///   most of the startup time and none of it was doing anything new.
///
/// LIFETIME IS DELIBERATELY SHORT
///   A parsed ADT is not small — 256 chunks of MCVT, MCNR and MCAL each — so
///   holding nine of them forever would trade seconds for a lot of resident
///   memory. The cache is meant to live for one load pass and then be cleared:
///   load terrain, buildings and doodads for a batch of tiles, then drop it.
///   <see cref="Forget"/> exists for the streaming case, where tiles arrive and
///   leave a few at a time.
///
/// A null result is cached as null. Ocean tiles genuinely have no ADT, and
/// re-attempting a missing file once per consumer is exactly the waste this
/// class exists to remove.
/// </summary>
public sealed class AdtCache
{
    private readonly string _clientDataPath;
    private readonly string _mapName;
    private readonly Dictionary<(int col, int row), AdtTerrainReader.AdtResult?> _cache = [];

    /// <summary>Files actually parsed.</summary>
    public int Parses { get; private set; }

    /// <summary>Requests served from an existing parse.</summary>
    public int Hits { get; private set; }

    public AdtCache(string clientDataPath, string mapName)
    {
        _clientDataPath = clientDataPath;
        _mapName = mapName;
    }

    /// <summary>
    /// The parsed ADT for a tile, or null if it does not exist.
    ///
    /// Note the argument order: ReadFromMpq takes (gridX = row, gridY = col),
    /// inverted from the {col}_{row} filename. That inversion has bitten this
    /// project before, so it is applied here once and nowhere else.
    /// </summary>
    public AdtTerrainReader.AdtResult? Get(int col, int row)
    {
        if (_cache.TryGetValue((col, row), out var cached))
        {
            Hits++;
            return cached;
        }

        var adt = AdtTerrainReader.ReadFromMpq(_clientDataPath, _mapName, row, col);
        _cache[(col, row)] = adt;
        Parses++;
        return adt;
    }

    /// <summary>Drop one tile, for when streaming unloads it.</summary>
    public void Forget(int col, int row) => _cache.Remove((col, row));

    /// <summary>Keep parsed data only for the currently resident ring.</summary>
    public void Retain(IReadOnlySet<(int col, int row)> resident)
    {
        foreach (var key in _cache.Keys.Where(k => !resident.Contains(k)).ToArray())
            _cache.Remove(key);
    }

    public int HeldTiles => _cache.Count;

    /// <summary>
    /// Drop everything. Call once a load pass is finished — the parsed data is
    /// large and nothing needs it again until tiles change.
    /// </summary>
    public void Clear()
    {
        int held = _cache.Count;
        _cache.Clear();

        Console.WriteLine(
            $"[adt] {Parses} parse(s), {Hits} reuse(s) — released {held} cached tile(s)");

        Parses = 0;
        Hits = 0;
    }
}
