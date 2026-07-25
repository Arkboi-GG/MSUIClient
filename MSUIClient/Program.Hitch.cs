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

        return new HitchRecorder.FramePhases
        {
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
            FoliageRenderMs = _foliageRenderMilliseconds,
            LiquidRenderMs = _liquidRenderMilliseconds,
            CharacterRenderMs = _characterRenderMilliseconds,
            DebugRenderMs = _debugRenderMilliseconds,

            // The frame boundary that used to be invisible.
            InputMs = _window.InputMilliseconds,
            ImguiUpdateMs = _window.ImguiUpdateMilliseconds,
            GuiMs = _window.GuiMilliseconds,
            PresentMs = _window.PresentMilliseconds,

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
                    guiMs = s.GuiMs,
                    presentMs = s.PresentMs,
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
                    foliageMs = s.FoliageRenderMs,
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

        Console.WriteLine(
            $"[hitch] {name}: {s.FrameMs:F0} ms frame at [{s.Col},{s.Row}] " +
            $"({s.X:F0},{s.Y:F0},{s.Z:F0}) -> {s.DominantPhase()}");
        Console.WriteLine(
            $"[hitch]   update {s.UpdateMs:F1} (move {s.MoveMs:F1} resid {s.ResidencyMs:F1} " +
            $"preload {s.PreloadMs:F1} [discover {s.DiscoverMs:F1} demand " +
            $"{s.DoodadDemandMs:F1} finalize {s.WarmMs:F1}] adopt {s.PumpPreloadsMs:F1} " +
            $"collAccept {s.AcceptCollisionMs:F1} collSnap {s.DoodadCollisionSnapshotMs:F1} " +
            $"unmeasured {s.UpdateUnaccountedMs:F1})  " +
            $"render {s.RenderMs:F1}  present {s.PresentMs:F1}  " +
            $"gui {s.GuiMs:F1}  input {s.InputMs:F1}  unaccounted {s.UnaccountedMs:F1}");
        Console.WriteLine(
            $"[hitch]   render split: world {s.WorldRenderMs:F1} = terrain " +
            $"{s.TerrainRenderMs:F1} + wmo {s.WmoRenderMs:F1} + doodad " +
            $"{s.DoodadRenderMs:F1} + foliage {s.FoliageRenderMs:F1}   " +
            $"| liquid {s.LiquidRenderMs:F1}  character {s.CharacterRenderMs:F1}  " +
            $"debug {s.DebugRenderMs:F1}");

        // The half that was never measured. If GPU total approaches the
        // vblank interval while CPU render is a few ms, the frame missed
        // vblank and "present" is the SYMPTOM, not the cause.
        Console.WriteLine(
            $"[hitch]   GPU (delayed): total {s.GpuTotalMs:F1} = terrain " +
            $"{s.GpuTerrainMs:F1} + wmo {s.GpuWmoMs:F1} + doodad {s.GpuDoodadMs:F1} " +
            $"+ character {s.GpuCharacterMs:F1}");

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
