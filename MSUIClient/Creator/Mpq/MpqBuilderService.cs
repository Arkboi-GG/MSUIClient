using MSUIClient.Creator.Mpq;

namespace MSUIClient.Creator;

/// <summary>
/// Creates MPQ patch archives for WoW client distribution â€” now via a fully
/// MANAGED writer (MpqArchiveWriter), with NO native StormLib dependency on the
/// write path.
///
/// â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
/// HISTORY
///
/// v1 (War3Net): produced archives only War3Net could read â€” saturated hash
/// table (16,384 slots for 12,419 files, no free slot so failed lookups never
/// terminated), block count short of the file count, encryption mpyq refused.
/// The pattern was "our writer, their reader".
///
/// v2 (StormLib P/Invoke): correct, but pulled in libstorm.so and its build/
/// setup steps.
///
/// v3 (this â€” managed): MpqArchiveWriter writes archives in the exact shape
/// StormLib itself writes (v1 header, sectored files, zlib per sector with the
/// store-if-not-smaller rule, (listfile), encrypted tables). The hash table is
/// sized with a guaranteed free slot, and block count equals file count by
/// construction â€” the two v1 failure modes are structurally impossible.
///
/// Build() still SELF-VERIFIES: after writing, it reopens the archive with the
/// managed reader (MpqArchive) and round-trips every file byte-for-byte. A build
/// that does not verify FAILS and never replaces the live archive.
/// â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
///
/// WRITE-OVER-A-MOUNTED-ARCHIVE (the patch-4 lock):
/// MpqReaderService keeps every mounted archive open for the app's lifetime via
/// a FileShare.Read handle. patch-4 (the retexture patch) is mounted. Truncating
/// that file in place while it is held throws a sharing violation on Linux
/// ("...being used by another process"). Build() therefore:
///   1. writes to a temp sibling ("&lt;output&gt;.new"),
///   2. self-verifies the TEMP,
///   3. unmounts the target from the reader (if injected) to release the handle,
///   4. atomically renames the temp over the live file (File.Move overwrite),
///   5. remounts, which also refreshes the reader's cached tables so the app's
///      own reads see the new archive without a restart.
/// The old archive stays intact until the atomic rename, so a failed/aborted
/// build never leaves a missing or half-written patch.
///
/// The public surface (AddFile / AddFileFromDisk / FileCount / TotalSize /
/// GetQueuedPaths / Build / Clear) is unchanged, so no caller needs to change.
/// The MpqReaderService dependency is OPTIONAL (nullable): resolve this service
/// from DI to get it wired automatically. Without it the atomic rename still
/// clears the lock, but the reader will serve the previous patch-4 until the
/// next app restart.
/// </summary>
public class MpqBuilderService
{
    private readonly Dictionary<string, byte[]> _files = new();
    private readonly ILogger<MpqBuilderService>? _logger;
    private readonly MpqReaderService? _reader;
    private readonly object _buildLock = new();

    public MpqBuilderService(ILogger<MpqBuilderService>? logger = null, MpqReaderService? reader = null)
    {
        _logger = logger;
        _reader = reader;
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // Public surface â€” unchanged
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <summary>Add a file to the MPQ with the given virtual path.</summary>
    public void AddFile(string mpqPath, byte[] data)
    {
        string normalizedPath = mpqPath.Replace('/', '\\');
        _files[normalizedPath] = data;
        _logger?.LogInformation("MpqBuilder: Queued {Path} ({Size} bytes)", normalizedPath, data.Length);
    }

    /// <summary>Add a file from disk.</summary>
    public void AddFileFromDisk(string mpqPath, string diskPath)
    {
        if (!File.Exists(diskPath))
            throw new FileNotFoundException($"File not found: {diskPath}");
        AddFile(mpqPath, File.ReadAllBytes(diskPath));
    }

    public int FileCount => _files.Count;
    public long TotalSize => _files.Values.Sum(f => (long)f.Length);
    public IReadOnlyCollection<string> GetQueuedPaths() => _files.Keys;

    /// <summary>
    /// Build the archive to a temp sibling, verify every file round-trips
    /// byte-identical, then atomically replace the live archive and refresh the
    /// mounted reader. Returns false â€” leaving the live archive untouched â€” on
    /// any failure.
    /// </summary>
    public bool Build(string outputPath)
    {
        if (_files.Count == 0)
        {
            _logger?.LogWarning("MpqBuilder: No files to package");
            return false;
        }

        lock (_buildLock)
        {
            // Temp sibling in the same directory â‡’ same filesystem â‡’ the rename
            // below is atomic. The ".new" suffix is not "*.MPQ", so the reader's
            // startup glob will never pick it up.
            string tempPath = outputPath + ".new";
            string archiveName = Path.GetFileName(outputPath);

            try
            {
                var dir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                if (File.Exists(tempPath))
                    File.Delete(tempPath);

                byte[] archive = MpqArchiveWriter.Build(_files.ToList());
                File.WriteAllBytes(tempPath, archive);

                // â”€â”€ SELF-VERIFICATION (against the TEMP, before it can go live) â”€â”€
                // "Build() returned true but the client sees garbage" burned an
                // entire day. It is no longer a possible outcome of this method,
                // and a failed verify never touches the live archive.
                if (!VerifyArchive(tempPath))
                {
                    _logger?.LogError(
                        "MpqBuilder: VERIFICATION FAILED for {Path} â€” discarding temp, live archive untouched",
                        outputPath);
                    try { File.Delete(tempPath); } catch { /* best effort */ }
                    return false;
                }

                // â”€â”€ ATOMIC SWAP â”€â”€
                // Release the reader's handle first (so the rename cannot contend
                // with a held FileShare.Read handle on the live file), rename the
                // verified temp over it, then remount to refresh cached tables.
                _reader?.UnmountArchive(archiveName);
                File.Move(tempPath, outputPath, overwrite: true);
                _reader?.RemountArchive(outputPath);

                var fileInfo = new FileInfo(outputPath);
                _logger?.LogInformation(
                    "MpqBuilder: Created {Path} ({FileCount} files, {Size} bytes) â€” verified round-trip OK",
                    outputPath, _files.Count, fileInfo.Length);
                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "MpqBuilder: Failed to create MPQ at {Path}", outputPath);
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best effort */ }

                // If we unmounted before failing (e.g. File.Move threw), the live
                // file is still the previous archive â€” put it back so the reader
                // isn't left without patch-4.
                try { _reader?.RemountArchive(outputPath); } catch { /* best effort */ }
                return false;
            }
        }
    }

    /// <summary>
    /// Reopen the archive with the managed reader and confirm every queued file
    /// extracts byte-identical.
    /// </summary>
    private bool VerifyArchive(string path)
    {
        using var mpq = Mpq.MpqArchive.Open(path);
        if (mpq == null)
        {
            _logger?.LogError("MpqBuilder: verify â€” cannot reopen archive {Path}", path);
            return false;
        }

        int verified = 0;
        foreach (var (mpqPath, expected) in _files)
        {
            var got = mpq.ReadFile(mpqPath);
            if (got == null)
            {
                _logger?.LogError("MpqBuilder: verify â€” {Path} MISSING from archive", mpqPath);
                return false;
            }
            if (got.Length != expected.Length)
            {
                _logger?.LogError("MpqBuilder: verify â€” {Path} size mismatch (archive={A}, queued={Q})",
                    mpqPath, got.Length, expected.Length);
                return false;
            }
            if (!got.AsSpan().SequenceEqual(expected))
            {
                _logger?.LogError("MpqBuilder: verify â€” {Path} CONTENT differs after round-trip", mpqPath);
                return false;
            }
            verified++;
        }

        _logger?.LogInformation("MpqBuilder: verify â€” {N} file(s) round-tripped byte-identical", verified);
        return true;
    }

    /// <summary>Clear all queued files.</summary>
    public void Clear()
    {
        _files.Clear();
    }
}