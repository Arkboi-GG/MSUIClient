using System.Text.Json;

namespace MSUIClient.Net;

/// <summary>
/// Every client mirrors the server's published World Content Packs before it mounts its archives
/// (shared_docs/WORLD_BUILDER.md §7). The server is the authority: a client whose patch-7.MPQ or
/// collision is older than the server's pack build renders the wrong terrain and falls through pack
/// buildings. So at startup: ask the web app for the latest build; if the local sidecar
/// (patch-7.MPQ.json: build id + sha1, written by every download) differs, download patch-7 and sync
/// the pack collision first. Unreachable web app = keep what we have and say so.
/// </summary>
public static class WorldPackStartupSync
{
    public const string PatchName = "patch-7.MPQ";

    public sealed record LocalBuild(int BuildId, string? Sha1);

    public static LocalBuild? ReadLocal(string dataPath)
    {
        try
        {
            string sidecar = Path.Combine(dataPath, PatchName + ".json");
            if (!File.Exists(sidecar) || !File.Exists(Path.Combine(dataPath, PatchName))) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(sidecar));
            return new LocalBuild(doc.RootElement.GetProperty("buildId").GetInt32(),
                doc.RootElement.TryGetProperty("sha1", out var s) ? s.GetString() : null);
        }
        catch { return null; }
    }

    public static void WriteLocal(string dataPath, int buildId, string? sha1) =>
        File.WriteAllText(Path.Combine(dataPath, PatchName + ".json"),
            JsonSerializer.Serialize(new { buildId, sha1, syncedUtc = DateTime.UtcNow }));

    /// <summary>What startup learned, plus the collision sync still running behind it (null = nothing to do).</summary>
    public sealed record StartupResult(string Status, Task<string>? Collision);

    /// <summary>
    /// Live state of a blocking startup sync, read by the window's progress card from the GL thread while the sync runs
    /// on a worker (owner, 2026-10-14: a long black window that looks frozen is the worst part of a slow boot).
    /// </summary>
    public sealed class Progress
    {
        private volatile string _stage = "Checking for world content updates";
        private long _done, _total = -1;
        public string Stage { get => _stage; set => _stage = value; }
        public long Done => Interlocked.Read(ref _done);
        /// <summary>Bytes expected, or null while unknown.</summary>
        public long? Total => Interlocked.Read(ref _total) is var t and >= 0 ? t : null;
        public void Report(long done, long? total) { Interlocked.Exchange(ref _done, done); Interlocked.Exchange(ref _total, total ?? -1); }
    }

    /// <summary>
    /// Bring patch-7 + pack collision to the server's latest build without holding the window hostage.
    /// Blocking: the version check (<paramref name="checkTimeout"/>, seconds) and, only when the build moved,
    /// the patch-7 download (<paramref name="downloadTimeout"/>) - the archive must be on disk before the
    /// mount, and a mounted MPQ cannot be swapped outside the World Builder. The sidecar is written the
    /// moment patch-7 lands. Collision (vmaps/mmaps, a minute or more on a fresh build) syncs in the
    /// background; its own per-folder manifests decide whether it is current, so an interrupted sync is
    /// simply resumed on the next launch instead of re-downloading everything.
    /// </summary>
    public static StartupResult EnsureCurrent(WorldPackClient client, string baseUrl, string dataPath, string vmapDir,
        string mmapDir, TimeSpan checkTimeout, TimeSpan downloadTimeout, Progress? progress = null)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return new("world packs: no web app configured - skipped", null);
        try
        {
            WorldPackClient.BuildInfo? server;
            using (var check = new CancellationTokenSource(checkTimeout))
                server = client.LastBuildAsync(baseUrl).WaitAsync(check.Token).GetAwaiter().GetResult();
            if (server is null) return new("world packs: the server has published none", null);

            string patch;
            var local = ReadLocal(dataPath);
            if (local is not null && local.BuildId == server.BuildId && (server.Sha1 is null || local.Sha1 == server.Sha1))
                patch = $"build #{server.BuildId} is current";
            else
            {
                using var download = new CancellationTokenSource(downloadTimeout);
                if (progress is not null) progress.Stage = $"Downloading world content (build #{server.BuildId})";
                long size = client.DownloadPatchAsync(baseUrl, Path.Combine(dataPath, PatchName), download.Token,
                        progress is null ? null : progress.Report)
                    .WaitAsync(download.Token).GetAwaiter().GetResult();
                WriteLocal(dataPath, server.BuildId, server.Sha1);
                patch = $"patch-7 updated to build #{server.BuildId} ({size / 1024} KiB)";
            }

            if (WorldPackCollisionSync.SyncedBuild(vmapDir) == server.BuildId &&
                WorldPackCollisionSync.SyncedBuild(mmapDir) == server.BuildId)
                return new($"world packs: {patch}; collision current", null);

            int buildId = server.BuildId;
            var collision = Task.Run(async () =>
            {
                var sync = await WorldPackCollisionSync.SyncAsync(client, baseUrl, buildId, vmapDir, mmapDir);
                return $"world packs: build #{buildId} {sync.Summary}";
            });
            return new($"world packs: {patch}; collision syncing in the background", collision);
        }
        catch (Exception ex)
        {
            string why = ex.GetBaseException() is OperationCanceledException ? "timed out" : ex.GetBaseException().Message;
            return new($"world packs: could not sync ({why}) - continuing with build #{ReadLocal(dataPath)?.BuildId.ToString() ?? "none"}", null);
        }
    }
}
