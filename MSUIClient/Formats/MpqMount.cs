using System.Threading;
using MSUIClient.Formats.Mpq;

namespace MSUIClient.Formats;

/// <summary>
/// Opens the client's MPQ archives once and keeps them open for the life of the
/// process.
///
/// WHY THIS EXISTS
///   AdtTerrainReader.ReadFileFromMpqs was written for a web app where a read
///   happens now and then, so it did this per call:
///
///       foreach (var mpq in GetMpqLoadOrder(dataPath))
///           using var archive = MpqArchive.Open(mpq);   // header + hash + block
///
///   That is a directory enumeration plus up to fifteen archive opens, each
///   parsing a header, hash table and block table, for EVERY file read — and a
///   miss walks all fifteen. Loading Northshire reads roughly 660 files
///   (190 models, 474 textures), so startup was doing on the order of ten
///   thousand archive opens. That was the 27 seconds each for buildings and
///   doodads, not the ADT parsing.
///
/// HOW IT HOOKS IN
///   AdtTerrainReader already has a `StormLibExtractor` delegate it tries
///   before the reopen loop. Pointing that at this mount routes every existing
///   call site through it with no changes to AdtTerrainReader at all:
///   ReadFileFromMpqs, ReadBlpPixels and ReadFromMpq all benefit at once.
///
///   Load order is preserved exactly: patches first in reverse-alphabetical
///   order so patch-3 beats patch-2 beats patch, then base archives with
///   terrain.MPQ and model.MPQ first. Getting that order wrong means reading
///   pre-patch versions of files, which would be a subtle and horrible bug.
///
/// THREADING (2026-07-26 — this is the change that unblocks parallel streaming)
///   MpqArchive.ReadFile is FULLY concurrent-safe: it reads only immutable
///   fields (hash/block tables, the file handle), allocates every working buffer
///   locally per call, and uses positioned RandomAccess I/O with no shared file
///   cursor (see MpqArchive.cs's own THREAD SAFETY note). The old global read
///   lock here was therefore over-conservative — it serialized every archive
///   extraction, so eight worker threads decoding a zone's models and textures
///   all funnelled through one lock and streamed in one file at a time (the ~30 s
///   of doodads popping in after a zone load). Reads now run in PARALLEL under a
///   shared read-lock; only Dispose takes the write lock (so a shutdown can't
///   free a handle mid-read). Returned byte arrays are independent; parsing and
///   BLP decoding continue to run concurrently on the workers.
/// </summary>
public sealed class MpqMount : IDisposable
{
    private readonly List<(string Name, MpqArchive Archive)> _archives = [];

    // Reads take the read lock (concurrent); Dispose takes the write lock.
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);

    // Interlocked because reads now run on many threads at once.
    private int _reads, _hits, _misses;

    public int ArchiveCount => _archives.Count;
    public int Reads => _reads;
    public int Hits => _hits;
    public int Misses => _misses;

    public MpqMount(string clientDataPath)
    {
        foreach (var path in LoadOrder(clientDataPath))
        {
            try
            {
                var archive = MpqArchive.Open(path);
                if (archive is not null)
                    _archives.Add((Path.GetFileName(path), archive));
            }
            catch (Exception ex)
            {
                // A bad archive is worth naming, but not worth refusing to
                // start over — the file may simply not be one we need.
                Console.WriteLine($"[mpq] could not open {Path.GetFileName(path)} - {ex.Message}");
            }
        }

        Console.WriteLine($"[mpq] mounted {_archives.Count} archive(s), held open (parallel reads)");
    }

    /// <summary>
    /// Read a file by its internal MPQ path, or null if no archive has it.
    /// Signature matches AdtTerrainReader.StormLibExtractor deliberately.
    /// Safe to call concurrently from the worker pool.
    /// </summary>
    public byte[]? ReadFile(string internalPath)
    {
        _lock.EnterReadLock();
        try
        {
            Interlocked.Increment(ref _reads);

            foreach (var (_, archive) in _archives)
            {
                try
                {
                    var data = archive.ReadFile(internalPath);
                    if (data is not null) { Interlocked.Increment(ref _hits); return data; }
                }
                catch
                {
                    // Wrong archive, or a read this one cannot satisfy. Next.
                }
            }

            Interlocked.Increment(ref _misses);
            return null;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Patches first, reverse alphabetical, then base archives with terrain and
    /// model prioritised. Mirrors AdtTerrainReader.GetMpqLoadOrder, which is
    /// private — duplicated rather than exposed, because the ordering is the
    /// part that must not drift and a copy next to its explanation is easier to
    /// keep honest than a call into another file.
    /// </summary>
    private static List<string> LoadOrder(string clientDataPath)
    {
        var result = new List<string>();
        if (!Directory.Exists(clientDataPath)) return result;

        var all = Directory.GetFiles(clientDataPath, "*.MPQ", SearchOption.TopDirectoryOnly)
            .Concat(Directory.GetFiles(clientDataPath, "*.mpq", SearchOption.TopDirectoryOnly))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        result.AddRange(all
            .Where(f => Path.GetFileName(f).StartsWith("patch", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase));

        result.AddRange(all
            .Where(f => !Path.GetFileName(f).StartsWith("patch", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f =>
            {
                var name = Path.GetFileName(f).ToLowerInvariant();
                if (name == "terrain.mpq") return 0;
                if (name == "model.mpq") return 1;
                return 10;
            }));

        return result;
    }

    public void Report()
        => Console.WriteLine(
            $"[mpq] {Reads:N0} read(s), {Hits:N0} found, {Misses:N0} not present, " +
            $"{_archives.Count} archive(s) still open");

    public void Dispose()
    {
        _lock.EnterWriteLock();
        try
        {
            foreach (var (_, archive) in _archives) archive.Dispose();
            _archives.Clear();
        }
        finally
        {
            _lock.ExitWriteLock();
            _lock.Dispose();
        }
    }
}
