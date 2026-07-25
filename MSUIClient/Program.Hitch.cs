using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ImGuiNET;
using MSUIClient.Engine;
using MSUIClient.World;

namespace MSUIClient;

// ============================================================================
// DevTools - the hitch recorder's GameLoop seam (PLAN_07_HITCH_RECORDER.md).
//
// Same rule as Program.DevTools.cs: this is developer TOOLING. It observes core
// state and writes dev data; core never depends on it. Kept in its own partial
// rather than added to Program.DevTools.cs only because the two concerns
// (vantages/dump vs hitch capture) are separately readable at ~300 lines each.
//
// What this file is FOR, restated because it is easy to lose:
// turning "it freezes for a second at certain coords" into a vantage you can
// reload, a phase name, and a file nobody had to remember to capture.
// ============================================================================
public sealed partial class GameLoop
{
    private readonly HitchRecorder _hitch = new();
    private int _hitchSequence;
    private bool _hitchTeeInstalled;

    /// <summary>
    /// Wall-clock span of the whole Render body, measured rather than summed
    /// from the pass timers - summing silently omits anything not yet bracketed
    /// (liquid and the atmosphere apply, today), and an instrument that quietly
    /// loses time is worse than no instrument.
    /// </summary>
    private double _renderSpanMilliseconds;

    // GC baselines, advanced once per frame in CurrentFramePhases. Counters, not
    // measurements: every value below is a delta against the previous frame.
    private int _gcGen0Baseline;
    private int _gcGen1Baseline;
    private int _gcGen2Baseline;
    private long _gcAllocatedBaseline;
    private TimeSpan _gcPauseBaseline;

    // ── Was the thread running at all? (2026-07-25) ─────────────────────────
    //
    // A 34 ms frame arrived with update 0.0, render 0.4, gcPause 0.0, allocated
    // 0.00 MB, GPU 0.8 and no uploads. Our code did nothing and the frame still
    // took two vblanks. Every workload explanation is spent, so the question is
    // no longer "what were we doing" but "were we running at all".
    //
    // QueryThreadCycleTime counts cycles this thread actually retired. No
    // calibration is needed to read it, because the comparison is against the
    // frame's own wall time:
    //
    //   LOW cycles on a long frame  -> the thread was NOT running. Blocked in a
    //                                  kernel wait or descheduled. A driver
    //                                  vsync wait looks like this.
    //   HIGH cycles on a long frame -> the thread WAS running, burning CPU. A
    //                                  driver busy-wait spin looks like this,
    //                                  and Intel's GL driver is known to do it.
    //
    // The two have opposite fixes, and nothing measured so far can tell them
    // apart. GetThreadTimes cannot answer this - its resolution is the ~15.6 ms
    // scheduler tick, which is the same size as the thing being measured.
    private ulong _threadCyclesBaseline;
    private bool _threadCyclesUnavailable;

    /// <summary>Pseudo-handle for the calling thread; does not need closing.</summary>
    private static readonly IntPtr CurrentThreadPseudoHandle = (IntPtr)(-2);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(
        System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool QueryThreadCycleTime(IntPtr threadHandle, out ulong cycleTime);

    /// <summary>
    /// Cycles retired by the render thread so far, or 0 where unavailable.
    /// Never throws: an instrument that can crash the client is not shippable
    /// tooling, and this one is a diagnostic on a hot path.
    /// </summary>
    private ulong ReadThreadCycles()
    {
        if (_threadCyclesUnavailable || !OperatingSystem.IsWindows()) return 0;
        try
        {
            if (QueryThreadCycleTime(CurrentThreadPseudoHandle, out ulong cycles)) return cycles;
        }
        catch
        {
            // Fall through - reported once, then never attempted again.
        }

        _threadCyclesUnavailable = true;
        Console.WriteLine("[hitch] QueryThreadCycleTime unavailable - thread cycle column will read 0");
        return 0;
    }

    /// <summary>
    /// Arm the recorder. Called once during startup, after the console exists.
    /// The generous initial grace covers the tail of loading, where enormous
    /// frames are expected and mean nothing.
    /// </summary>
    private void InitHitchRecorder()
    {
        if (!_config.DevTools)
        {
            // Tooling, not a feature: a release build must not write files or
            // pay for the ring.
            _hitch.Enabled = false;
            return;
        }

        if (!_hitchTeeInstalled)
        {
            HitchRecorder.InstallConsoleTee(_hitch);
            _hitchTeeInstalled = true;
        }

        _hitch.SuppressFor(5.0);
        Console.WriteLine(
            $"[hitch] recorder armed - threshold {_hitch.ThresholdMs:F0} ms, " +
            $"window {_hitch.WindowFrames} frames, cooldown {_hitch.CooldownSeconds:F0}s, " +
            $"cap {_hitch.SessionCap}");
    }

    /// <summary>
    /// Gather this frame's numbers for the ring. Reads the same scalars the HUD
    /// binds (Program.cs) - the point of the recorder is that those scalars stop
    /// being destroyed every frame, not that new ones get invented.
    /// </summary>
    private HitchRecorder.FramePhases CurrentFramePhases()
    {
        var p = _controller?.Position ?? System.Numerics.Vector3.Zero;
        var tile = TerrainRenderer.TileAt(p.X, p.Y);

        // GC deltas for the frame that just closed. All five reads are counter
        // lookups - GetTotalAllocatedBytes(precise: false) reads the per-thread
        // allocation contexts without walking the heap, and GetTotalPauseDuration
        // is a running total the runtime already maintains. Nothing here
        // collects, and nothing here can perturb what it measures.
        int gen0 = GC.CollectionCount(0);
        int gen1 = GC.CollectionCount(1);
        int gen2 = GC.CollectionCount(2);
        long allocated = GC.GetTotalAllocatedBytes(precise: false);
        TimeSpan pause = GC.GetTotalPauseDuration();

        int dGen0 = gen0 - _gcGen0Baseline;
        int dGen1 = gen1 - _gcGen1Baseline;
        int dGen2 = gen2 - _gcGen2Baseline;
        long dAllocated = allocated - _gcAllocatedBaseline;
        double dPauseMs = (pause - _gcPauseBaseline).TotalMilliseconds;

        _gcGen0Baseline = gen0;
        _gcGen1Baseline = gen1;
        _gcGen2Baseline = gen2;
        _gcAllocatedBaseline = allocated;
        _gcPauseBaseline = pause;

        ulong threadCycles = ReadThreadCycles();
        ulong dThreadCycles = threadCycles >= _threadCyclesBaseline
            ? threadCycles - _threadCyclesBaseline
            : 0;
        _threadCyclesBaseline = threadCycles;

        return new HitchRecorder.FramePhases
        {
            ThreadCycles = dThreadCycles,

            Gen0 = dGen0,
            Gen1 = dGen1,
            Gen2 = dGen2,
            AllocatedBytes = dAllocated,
            GcPauseMs = dPauseMs,

            UpdateMs = _updateMilliseconds,
            MoveMs = _movementMilliseconds,
            PumpPreloadsMs = _pumpPreloadsMilliseconds,
            AcceptCollisionMs = _acceptCollisionMilliseconds,
            DoodadCollisionSnapshotMs = _doodadCollisionSnapshotMilliseconds,
            ResidencyMs = _residencyMilliseconds,
            PreloadMs = _preloadMilliseconds,
            DiscoverMs = _discoverMilliseconds,
            DoodadDemandMs = _doodadDemandMilliseconds,
            WarmMs = _warmMilliseconds,
            UnitMs = _characterUpdateMilliseconds,
            CameraMs = _cameraCollisionMilliseconds,

            RenderMs = _renderSpanMilliseconds,
            WorldRenderMs = _worldRenderMilliseconds,
            TerrainRenderMs = _terrain?.RenderMilliseconds ?? 0,
            WmoRenderMs = _wmo?.RenderMilliseconds ?? 0,
            DoodadRenderMs = _doodads?.RenderMilliseconds ?? 0,
            DoodadCullMs = _doodads?.CullMilliseconds ?? 0,
            DoodadInstanceUploadMs = _doodads?.InstanceUploadMilliseconds ?? 0,
            DoodadDrawMs = _doodads?.DrawMilliseconds ?? 0,
            DoodadFirstTouchModels = _doodads?.FirstTouchModelsLastFrame ?? 0,
            DoodadUploadedModels = _doodads?.UploadedModelsLastFrame ?? 0,
            DoodadCullModels = _doodads?.CullModelsLastFrame ?? 0,
            DoodadCullInstances = _doodads?.CullInstancesLastFrame ?? 0,
            FoliageRenderMs = _foliageRenderMilliseconds,
            FoliageScatterMs = _foliageScatterMilliseconds,
            FoliageDrawMs = _foliageDrawMilliseconds,
            LiquidRenderMs = _liquidRenderMilliseconds,
            CharacterRenderMs = _characterRenderMilliseconds,
            DebugRenderMs = _debugRenderMilliseconds,

            // The frame boundary that used to be invisible.
            InputMs = _window.InputMilliseconds,
            ImguiUpdateMs = _window.ImguiUpdateMilliseconds,
            GuiMs = _window.GuiMilliseconds,
            HudMs = _window.HudMilliseconds,
            ImguiRenderMs = _window.ImguiRenderMilliseconds,
            PresentMs = _window.PresentMilliseconds,

            // The H2 discriminator (SYSTEM_STREAMING 5.2): was a shared-context
            // upload in flight while the driver blocked? Consume exactly once
            // per frame - this is that call site and there must not be another.
            UploadsInFlight = _uploads?.InFlight ?? 0,
            UploadsCompleted = _uploads?.ConsumeCompletedSinceLastFrame() ?? 0,

            // Delayed results - these belong to a frame slightly earlier than
            // this one, which is inherent to non-blocking timer queries. Good
            // enough to tell a 5 ms GPU frame from a 25 ms one, which is the
            // question that has gone unasked all session.
            GpuTotalMs = _gpuProfiler?.MeasuredTotalMilliseconds ?? 0,
            GpuTerrainMs = _gpuProfiler?[GpuFrameProfiler.Pass.Terrain] ?? 0,
            GpuWmoMs = _gpuProfiler?[GpuFrameProfiler.Pass.Wmo] ?? 0,
            GpuDoodadMs = _gpuProfiler?[GpuFrameProfiler.Pass.Doodads] ?? 0,
            GpuCharacterMs = _gpuProfiler?[GpuFrameProfiler.Pass.Character] ?? 0,

            X = p.X,
            Y = p.Y,
            Z = p.Z,
            Col = tile.col,
            Row = tile.row,

            ResidentTiles = _terrain?.TileCount ?? 0,
            WmoQueued = _wmo?.PendingPreloads ?? 0,
            M2Queued = _doodads?.PendingPreloads ?? 0,
            DiscoveryTiles = _backgroundDiscovery.Count,
        };
    }

    /// <summary>
    /// Write dumps/hitch_&lt;n&gt;_&lt;col&gt;_&lt;row&gt;.json and save a matching vantage.
    ///
    /// The vantage is the part that answers Nico's actual report. "Certain
    /// coords" only becomes actionable once the coords are reloadable, and the
    /// whole foundation layer already exists to do that - this just makes the
    /// client the one pressing Save.
    /// </summary>
    private void WriteHitchRecord()
    {
        var s = _hitch.Tripped;
        int n = ++_hitchSequence;
        string name = $"hitch-{s.Col}-{s.Row}-{n}";

        // Snapshot synchronously; the ring keeps moving while we serialize.
        var window = _hitch.SnapshotWindow();
        var events = _hitch.SnapshotEvents();
        long tripIndex = s.Index;

        // Auto-vantage: reproducible coords, which is the whole ask.
        Vantage? vantage = null;
        try
        {
            vantage = CaptureVantage(name);
            _vantages ??= VantageStore.Load(_config.RepoRoot);
            _vantages.Upsert(vantage);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[hitch] could not save vantage - {ex.Message}");
        }

        var payload = new
        {
            name,
            takenLocal = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            map = _config.Start.MapName,
            thresholdMs = _hitch.ThresholdMs,

            hitch = new
            {
                frameIndex = s.Index,
                frameMs = s.FrameMs,
                updateMs = s.UpdateMs,
                renderMs = s.RenderMs,

                // Every phase of the frame is now named. Large unaccounted
                // means a region still has no timer around it - report it
                // rather than reading past it.
                unaccountedMs = s.UnaccountedMs,
                dominantPhase = s.DominantPhase(),

                // The boundary handbook 3.30 called unmeasured. presentMs is
                // the swap and the platform event pump: a vsync wait, or the
                // render context stalling behind shared-context uploads.
                gpu = new
                {
                    totalMs = s.GpuTotalMs,
                    terrainMs = s.GpuTerrainMs,
                    wmoMs = s.GpuWmoMs,
                    doodadMs = s.GpuDoodadMs,
                    characterMs = s.GpuCharacterMs,
                },

                boundary = new
                {
                    inputMs = s.InputMs,
                    imguiUpdateMs = s.ImguiUpdateMs,

                    // guiMs is the sum and is kept only so the residual
                    // balances. Read the two halves, never the sum: hudMs is our
                    // HUD code, imguiRenderMs is the frame's last GL call and
                    // therefore where a driver stall lands.
                    guiMs = s.GuiMs,
                    hudMs = s.HudMs,
                    imguiRenderMs = s.ImguiRenderMs,

                    presentMs = s.PresentMs,

                    // Non-zero here beside a large imguiRenderMs is H2
                    // confirmed: the shared upload context serialized the render
                    // context. Zero here beside a large imguiRenderMs refutes it
                    // and the next suspect is the swap chain itself.
                    uploadsInFlight = s.UploadsInFlight,
                    uploadsCompleted = s.UploadsCompleted,
                },

                // Overlapping, not additive: a pause is already inside whichever
                // phase was running when it hit. Read gcPauseMs against frameMs
                // FIRST - if it holds the frame, every other number below is the
                // bucket that happened to be unlucky, not a cause.
                thread = new
                {
                    cycles = s.ThreadCycles,
                    mCyclesPerMs = s.ThreadMCyclesPerMs,
                },

                gc = new
                {
                    pauseMs = s.GcPauseMs,
                    gen0 = s.Gen0,
                    gen1 = s.Gen1,
                    gen2 = s.Gen2,
                    allocatedBytes = s.AllocatedBytes,
                    allocatedMb = s.AllocatedBytes / (1024.0 * 1024.0),
                },

                update = new
                {
                    movementMs = s.MoveMs,
                    pumpPreloadsMs = s.PumpPreloadsMs,
                    acceptCollisionMs = s.AcceptCollisionMs,
                    doodadCollisionSnapshotMs = s.DoodadCollisionSnapshotMs,
                    unmeasuredMs = s.UpdateUnaccountedMs,
                    residencyMs = s.ResidencyMs,
                    preloadMs = s.PreloadMs,
                    discoverMs = s.DiscoverMs,
                    doodadDemandMs = s.DoodadDemandMs,
                    modelFinalizeMs = s.WarmMs,
                    characterMs = s.UnitMs,
                    cameraCollisionMs = s.CameraMs,
                },
                render = new
                {
                    worldMs = s.WorldRenderMs,
                    terrainMs = s.TerrainRenderMs,
                    wmoMs = s.WmoRenderMs,
                    doodadMs = s.DoodadRenderMs,
                    doodad = new
                    {
                        cullMs = s.DoodadCullMs,
                        instanceUploadMs = s.DoodadInstanceUploadMs,
                        drawSubmitMs = s.DoodadDrawMs,
                        unmeasuredMs = s.DoodadUnaccountedMs,
                        firstTouchModels = s.DoodadFirstTouchModels,
                        uploadedModels = s.DoodadUploadedModels,
                        cullModels = s.DoodadCullModels,
                        cullInstances = s.DoodadCullInstances,
                        cullNsPerInstance = s.DoodadCullNsPerInstance,
                    },
                    foliageMs = s.FoliageRenderMs,
                    foliage = new
                    {
                        rescatterMs = s.FoliageScatterMs,
                        drawMs = s.FoliageDrawMs,
                    },
                    liquidMs = s.LiquidRenderMs,
                    characterMs = s.CharacterRenderMs,
                    debugMs = s.DebugRenderMs,
                },
            },

            where = new
            {
                position = new[] { s.X, s.Y, s.Z },
                tile = new[] { s.Col, s.Row },
                vantage = name,
            },

            residency = new
            {
                residentTiles = s.ResidentTiles,
                wmoQueued = s.WmoQueued,
                m2Queued = s.M2Queued,
                pendingDiscovery = s.DiscoveryTiles,
                lastStreamSeconds = _lastStreamSeconds,
                collisionBuildSeconds = _collisionBuildSeconds,
            },

            // Every tagged console line still in the ring, offset relative to the
            // stalled frame. Negative offsets are the run-up - usually where the
            // cause is, since the symptom is by definition the last thing to
            // happen.
            events = events.Select(e => new
            {
                frameOffset = e.FrameIndex - tripIndex,
                text = e.Text,
            }).ToArray(),

            suppressedTrips = _hitch.SuppressedCount,
            recordedThisSession = _hitch.RecordedCount + 1,

            frames = window.Select(f => new
            {
                i = f.Index - tripIndex,
                frameMs = f.FrameMs,
                updateMs = f.UpdateMs,
                renderMs = f.RenderMs,
                unaccountedMs = f.UnaccountedMs,
                presentMs = f.PresentMs,
                guiMs = f.GuiMs,

                // Per-frame, not just on the tripped frame. The whole reason the
                // gui bucket was believed was that the ring showed guiMs ~16 on
                // every frame and nobody could see it was a wait wandering
                // between phases. These two columns make that visible directly.
                hudMs = f.HudMs,
                imguiRenderMs = f.ImguiRenderMs,
                uploadsInFlight = f.UploadsInFlight,
                uploadsCompleted = f.UploadsCompleted,

                // Per frame, because the run-up matters more than the trip: a
                // steady allocation rate climbing for 60 frames before a gen2
                // is a different bug from one 40 MB spike.
                gcPauseMs = f.GcPauseMs,
                gen2 = f.Gen2,
                allocMb = f.AllocatedBytes / (1024.0 * 1024.0),

                // These three are the whole point of this round. The frames on
                // either side of a crossing cull the SAME instances. If
                // doodadCullMs is 55 on the crossing frame and 0.3 on its
                // neighbours at the same doodadCullInstances, the work did not
                // change and the memory did.
                // Per frame, so a hitch can be compared against the 16.7 ms
                // frames around it. Those are ALSO waiting on vsync, so if the
                // rate is the same on both, the hitch is just a longer wait of
                // the same kind - not a different event.
                threadMCyclesPerMs = f.ThreadMCyclesPerMs,

                doodadCullMs = f.DoodadCullMs,
                doodadCullInstances = f.DoodadCullInstances,
                doodadCullNsPerInstance = f.DoodadCullNsPerInstance,
                residencyMs = f.ResidencyMs,
                preloadMs = f.PreloadMs,
                worldMs = f.WorldRenderMs,
                wmoQ = f.WmoQueued,
                m2Q = f.M2Queued,
                tiles = f.ResidentTiles,
                pos = new[] { f.X, f.Y, f.Z },
            }).ToArray(),

            vantageState = vantage,
        };

        _hitch.NoteRecorded(new HitchRecorder.HitchSummary
        {
            Name = name,
            FrameMs = s.FrameMs,
            UnaccountedMs = s.UnaccountedMs,
            Phase = s.DominantPhase(),
            Col = s.Col,
            Row = s.Row,
        });

        var (p50, p95) = _hitch.WindowPercentiles();
        Console.WriteLine(
            $"[hitch] {name}: {s.FrameMs:F0} ms frame at [{s.Col},{s.Row}] " +
            $"({s.X:F0},{s.Y:F0},{s.Z:F0}) -> {s.DominantPhase()}" +
            $"   (window p50 {p50:F0} p95 {p95:F0} ms" +
            (s.FrameMs <= p50 * 1.2 ? " - AT BASELINE, this scene is slow, not hitching)" : ")"));
        Console.WriteLine(
            $"[hitch]   update {s.UpdateMs:F1} (move {s.MoveMs:F1} resid {s.ResidencyMs:F1} " +
            $"preload {s.PreloadMs:F1} [discover {s.DiscoverMs:F1} demand " +
            $"{s.DoodadDemandMs:F1} finalize {s.WarmMs:F1}] adopt {s.PumpPreloadsMs:F1} " +
            $"collAccept {s.AcceptCollisionMs:F1} collSnap {s.DoodadCollisionSnapshotMs:F1} " +
            $"unmeasured {s.UpdateUnaccountedMs:F1})  " +
            $"render {s.RenderMs:F1}  present {s.PresentMs:F1}  " +
            $"hud {s.HudMs:F1}  imguiFlush {s.ImguiRenderMs:F1}  " +
            $"input {s.InputMs:F1}  unaccounted {s.UnaccountedMs:F1}");
        Console.WriteLine(
            $"[hitch]   render split: world {s.WorldRenderMs:F1} = terrain " +
            $"{s.TerrainRenderMs:F1} + wmo {s.WmoRenderMs:F1} + doodad " +
            $"{s.DoodadRenderMs:F1} + foliage {s.FoliageRenderMs:F1} " +
            $"[rescatter {s.FoliageScatterMs:F1} + draw {s.FoliageDrawMs:F1}]   " +
            $"| liquid {s.LiquidRenderMs:F1}  character {s.CharacterRenderMs:F1}  " +
            $"debug {s.DebugRenderMs:F1}");

        // The doodad pass broken into its three unrelated jobs. cull is our
        // arithmetic and scales with placements; upload and draw are driver
        // calls. firstTouch is the tell for a shared-context first-bind stall,
        // which the uploads counter cannot see because those uploads finished
        // frames earlier.
        Console.WriteLine(
            $"[hitch]   doodad {s.DoodadRenderMs:F1} = cull {s.DoodadCullMs:F1} + " +
            $"instanceUpload {s.DoodadInstanceUploadMs:F1} + drawSubmit {s.DoodadDrawMs:F1} " +
            $"+ unmeasured {s.DoodadUnaccountedMs:F1}   " +
            $"({s.DoodadUploadedModels} model(s) uploaded, " +
            $"{s.DoodadFirstTouchModels} first-touch)");

        // The rate, not the total. Compare it against the doodadCull column in
        // the frames ring below: the neighbouring frames walk the SAME
        // instances, so an identical instance count at a fraction of the time
        // rules out workload and leaves the state of the memory.
        Console.WriteLine(
            $"[hitch]   doodad cull: {s.DoodadCullInstances} instance(s) over " +
            $"{s.DoodadCullModels} model(s) = " +
            $"{s.DoodadCullNsPerInstance:F0} ns/instance " +
            $"(~50-100 is normal arithmetic; 1000+ is memory, not maths)");

        // The half that was never measured. If GPU total approaches the
        // vblank interval while CPU render is a few ms, the frame missed
        // vblank and "present" is the SYMPTOM, not the cause.
        Console.WriteLine(
            $"[hitch]   GPU (delayed): total {s.GpuTotalMs:F1} = terrain " +
            $"{s.GpuTerrainMs:F1} + wmo {s.GpuWmoMs:F1} + doodad {s.GpuDoodadMs:F1} " +
            $"+ character {s.GpuCharacterMs:F1}");

        // The one line that decides H2. A big imguiFlush with uploads in flight
        // is the shared context serializing the render context; a big imguiFlush
        // on a quiet frame is not, and sends the search to the swap chain.
        Console.WriteLine(
            $"[hitch]   uploads: {s.UploadsInFlight} in flight, " +
            $"{s.UploadsCompleted} completed during the frame  " +
            $"(imguiFlush {s.ImguiRenderMs:F1} ms, gpu {s.GpuTotalMs:F1} ms)");

        // Read this line before any of the ones above it. A pause that holds the
        // frame means the phase split is telling you which bucket was unlucky,
        // not what went wrong - and no amount of moving work off the main thread
        // will help, because a collection stops every thread regardless of which
        // one allocated.
        Console.WriteLine(
            $"[hitch]   GC: pause {s.GcPauseMs:F1} ms of {s.FrameMs:F0} ms frame  " +
            $"gen0 {s.Gen0} gen1 {s.Gen1} gen2 {s.Gen2}  " +
            $"allocated {s.AllocatedBytes / (1024.0 * 1024.0):F2} MB this frame");

        // Were we even running? On a long frame where nothing we own was busy,
        // this is the only remaining question, and it has exactly two answers.
        Console.WriteLine(
            $"[hitch]   thread: {s.ThreadCycles / 1_000_000.0:F1}M cycles over " +
            $"{s.FrameMs:F0} ms = {s.ThreadMCyclesPerMs:F2} M/ms  " +
            $"(~4-5 = running and spinning; <1 = blocked or descheduled)");

        // Serialize and write off the render thread. An instrument that stalls
        // the frame to report a stall is not an instrument.
        string dir = Path.Combine(_config.RepoRoot, "dumps");
        _ = Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(
                    Path.Combine(dir, name + ".json"),
                    JsonSerializer.Serialize(payload, DumpJson));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[hitch] could not write record - {ex.Message}");
            }
        });
    }

    /// <summary>
    /// The Hitch recorder HUD panel. Deliberately shows the running numbers -
    /// worst frame seen, suppressed count - so the instrument can be trusted
    /// before its output is.
    /// </summary>
    private void DrawHitchPanel()
    {
        if (!ImGui.CollapsingHeader("Hitch recorder (PLAN_07)")) return;

        bool enabled = _hitch.Enabled;
        if (ImGui.Checkbox("Record hitches", ref enabled)) _hitch.Enabled = enabled;

        float threshold = (float)_hitch.ThresholdMs;
        if (ImGui.SliderFloat("Threshold (ms)", ref threshold, 20f, 500f, "%.0f"))
            _hitch.ThresholdMs = threshold;

        int window = _hitch.WindowFrames;
        if (ImGui.SliderInt("Frames kept", ref window, 30, 600))
            _hitch.WindowFrames = window;

        ImGui.Text($"recorded {_hitch.RecordedCount}/{_hitch.SessionCap}   " +
                   $"suppressed {_hitch.SuppressedCount}");

        // Live, so the split can be watched while walking rather than only in
        // records. Under vsync the ~16 ms wait wanders between phases frame to
        // frame; seeing it move is the fastest way to recognise it as a wait
        // and not a cost. hud should sit near 0.25 and never move.
        ImGui.Separator();
        ImGui.Text($"hud {_window.HudMilliseconds,6:F2} ms   " +
                   $"imgui flush {_window.ImguiRenderMilliseconds,6:F2} ms   " +
                   $"present {_window.PresentMilliseconds,6:F2} ms");
        ImGui.Text($"uploads in flight: {_uploads?.InFlight ?? 0}");
        ImGui.Text($"GC gen0/1/2: {GC.CollectionCount(0)} / {GC.CollectionCount(1)} / " +
                   $"{GC.CollectionCount(2)}   pause total " +
                   $"{GC.GetTotalPauseDuration().TotalMilliseconds:F0} ms");
        ImGui.Text($"heap {GC.GetTotalMemory(forceFullCollection: false) / (1024.0 * 1024.0):F0} MB");
        ImGui.TextDisabled("watch gen2: each one is a stop-the-world pause");
        ImGui.Separator();

        // Self-test. PLAN_07 section 7 step 1: an instrument that has never been
        // shown to fire is not evidence. This makes it fire on demand.
        if (ImGui.Button("Force 800 ms stall (self-test)"))
            _hitch.PendingForcedStallMs = 800.0;
        ImGui.SameLine();
        if (ImGui.Button("Clear grace")) _hitch.SuppressFor(0.0);

        if (_hitch.History.Count == 0)
        {
            ImGui.TextDisabled("no hitches recorded yet");
            return;
        }

        ImGui.Separator();
        for (int i = _hitch.History.Count - 1; i >= 0; i--)
        {
            var h = _hitch.History[i];
            if (ImGui.Button($"Go##hitch_{h.Name}"))
            {
                _vantages ??= VantageStore.Load(_config.RepoRoot);
                var v = _vantages.Find(h.Name);
                if (v is not null) ApplyVantage(v);
            }
            ImGui.SameLine();
            ImGui.Text($"{h.FrameMs,5:F0} ms  [{h.Col},{h.Row}]  {h.Phase}" +
                       (h.UnaccountedMs > 1.0 ? $"  (unacct {h.UnaccountedMs:F0})" : ""));
        }
    }
}
