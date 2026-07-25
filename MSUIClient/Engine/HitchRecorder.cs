using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace MSUIClient.Engine;

// ============================================================================
// The hitch recorder (PLAN_07_HITCH_RECORDER.md).
//
// The client could not previously answer "what was it doing during that
// freeze". ClientWindow.FrameMs is a 0.5-second average, built to be a stable
// readout - the opposite of what spike detection needs - and every phase timer
// in GameLoop is a scalar overwritten before anyone reads it. So the evidence
// for a felt stutter was gone by the time a human could look at it.
//
// This class is that missing memory: a preallocated ring of per-frame samples,
// a ring of the [tag] console lines that fired alongside them, and a threshold
// that fires without anyone having to notice anything.
//
// Deliberately free of GL, game types and rendering knowledge - GameLoop hands
// it numbers. It allocates nothing per frame; an instrument that allocates in
// the hot path manufactures the hitches it is meant to measure.
// ============================================================================
public sealed class HitchRecorder
{
    // ---- Tunables (HUD-bound; see the Hitch recorder panel) ----

    /// <summary>Master switch. Recording costs one struct write per frame.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// A frame longer than this is a hitch. 100 ms default: far above a missed
    /// vsync (16.7) so ordinary pacing never trips it, far below the ~1000 ms
    /// freeze being hunted.
    /// </summary>
    /// <summary>
    /// 25 ms, not 100. At 60 Hz vsync a frame over 16.7 ms has already dropped
    /// one - so a 100 ms threshold only ever saw the freezes, never the
    /// micro-stutter that remains once those are fixed. Raise it back when
    /// hunting something large.
    /// </summary>
    public double ThresholdMs { get; set; } = 25.0;

    /// <summary>
    /// Frames of history kept and written with each record. The cause usually
    /// starts before the frame that stalls - a queue fills, a residency change
    /// is requested - so the setup matters more than the symptom.
    /// </summary>
    public int WindowFrames { get; set; } = 180;

    /// <summary>Minimum gap between records, so one bad street writes one file.</summary>
    public double CooldownSeconds { get; set; } = 3.0;

    /// <summary>Records per session. Suppressed trips are counted, never silent.</summary>
    public int SessionCap { get; set; } = 40;

    // ---- Ring state ----

    private const int RingCapacity = 600;          // ~10 s at 60 fps
    private const int EventCapacity = 256;

    private readonly FrameSample[] _ring = new FrameSample[RingCapacity];
    private int _ringWrite;
    private int _ringCount;

    private readonly LogEvent[] _events = new LogEvent[EventCapacity];
    private int _eventWrite;
    private int _eventCount;
    private readonly object _eventLock = new();

    private long _frameStartStamp;
    private long _frameCounter;
    private bool _hasPending;
    private FrameSample _pending;

    private long _lastRecordStamp;
    private long _suppressUntilStamp;

    /// <summary>Trips that were correctly detected but not written (cooldown/cap).</summary>
    public int SuppressedCount { get; private set; }

    /// <summary>Records written this session.</summary>
    public int RecordedCount { get; private set; }

    /// <summary>The frame that most recently crossed the threshold.</summary>
    public FrameSample Tripped { get; private set; }

    /// <summary>Short summaries of every record written, newest last. For the HUD.</summary>
    public List<HitchSummary> History { get; } = new();

    /// <summary>Requested by the HUD's self-test button; consumed by GameLoop.</summary>
    public double PendingForcedStallMs { get; set; }

    // ---- Per-frame API ----

    /// <summary>
    /// Call at the TOP of the game's Update, once per frame, passing the phase
    /// numbers for the frame that just finished. Returns true if that frame
    /// crossed the threshold and a record should be written.
    ///
    /// The boundary is Update-entry to Update-entry, and that placement is the
    /// whole correctness argument. Silk runs Update then Render then swap, so a
    /// period measured render-entry to render-entry contains Render N but
    /// Update N+1 - and closing the sample with Update N's number then reports
    /// the wrong frame's work. The first version of this class did exactly that
    /// and blamed a 171 ms stall on "outside update and render" while the
    /// residency publication that likely caused it sat in the Update it had
    /// excluded. Measured from Update entry, every phase below is inside the
    /// period it is attributed to.
    /// </summary>
    public bool FrameBoundary(in FramePhases phases)
    {
        long now = Stopwatch.GetTimestamp();
        bool tripped = false;

        if (_hasPending)
        {
            _pending.FrameMs = Stopwatch.GetElapsedTime(_frameStartStamp, now).TotalMilliseconds;

            _pending.UpdateMs = phases.UpdateMs;
            _pending.MoveMs = phases.MoveMs;
            _pending.PumpPreloadsMs = phases.PumpPreloadsMs;
            _pending.AcceptCollisionMs = phases.AcceptCollisionMs;
            _pending.DoodadCollisionSnapshotMs = phases.DoodadCollisionSnapshotMs;
            _pending.ResidencyMs = phases.ResidencyMs;
            _pending.PreloadMs = phases.PreloadMs;
            _pending.DiscoverMs = phases.DiscoverMs;
            _pending.DoodadDemandMs = phases.DoodadDemandMs;
            _pending.WarmMs = phases.WarmMs;
            _pending.UnitMs = phases.UnitMs;
            _pending.CameraMs = phases.CameraMs;

            _pending.RenderMs = phases.RenderMs;
            _pending.WorldRenderMs = phases.WorldRenderMs;
            _pending.TerrainRenderMs = phases.TerrainRenderMs;
            _pending.WmoRenderMs = phases.WmoRenderMs;
            _pending.DoodadRenderMs = phases.DoodadRenderMs;
            _pending.FoliageRenderMs = phases.FoliageRenderMs;
            _pending.LiquidRenderMs = phases.LiquidRenderMs;
            _pending.CharacterRenderMs = phases.CharacterRenderMs;
            _pending.DebugRenderMs = phases.DebugRenderMs;

            _pending.InputMs = phases.InputMs;
            _pending.ImguiUpdateMs = phases.ImguiUpdateMs;
            _pending.GuiMs = phases.GuiMs;
            _pending.PresentMs = phases.PresentMs;

            _pending.GpuTotalMs = phases.GpuTotalMs;
            _pending.GpuTerrainMs = phases.GpuTerrainMs;
            _pending.GpuWmoMs = phases.GpuWmoMs;
            _pending.GpuDoodadMs = phases.GpuDoodadMs;
            _pending.GpuCharacterMs = phases.GpuCharacterMs;

            _pending.X = phases.X;
            _pending.Y = phases.Y;
            _pending.Z = phases.Z;
            _pending.Col = phases.Col;
            _pending.Row = phases.Row;

            _pending.ResidentTiles = phases.ResidentTiles;
            _pending.WmoQueued = phases.WmoQueued;
            _pending.M2Queued = phases.M2Queued;
            _pending.DiscoveryTiles = phases.DiscoveryTiles;

            tripped = Commit(_pending, now);
        }

        _frameStartStamp = now;
        _pending = default;
        _pending.Index = ++_frameCounter;
        _hasPending = true;

        return tripped;
    }

    private bool Commit(in FrameSample sample, long now)
    {
        if (!Enabled) return false;

        _ring[_ringWrite] = sample;
        _ringWrite = (_ringWrite + 1) % RingCapacity;
        if (_ringCount < RingCapacity) _ringCount++;

        if (sample.FrameMs <= ThresholdMs) return false;

        // Warm-up and teleport grace. Startup and a vantage jump both produce
        // legitimately enormous frames that are not the bug being hunted.
        if (now < _suppressUntilStamp) return false;

        if (RecordedCount >= SessionCap)
        {
            SuppressedCount++;
            return false;
        }

        if (_lastRecordStamp != 0 &&
            Stopwatch.GetElapsedTime(_lastRecordStamp, now).TotalSeconds < CooldownSeconds)
        {
            SuppressedCount++;
            return false;
        }

        _lastRecordStamp = now;
        Tripped = sample;
        return true;
    }

    /// <summary>
    /// Ignore trips for a while. Called at startup and after a vantage load,
    /// where a huge frame is expected and means nothing.
    /// </summary>
    public void SuppressFor(double seconds)
        => _suppressUntilStamp = Stopwatch.GetTimestamp() + (long)(seconds * Stopwatch.Frequency);

    /// <summary>
    /// The most recent frames, oldest first, capped to <see cref="WindowFrames"/>.
    /// Copied out synchronously so the ring can keep moving while the record is
    /// serialized on a worker.
    /// </summary>
    public FrameSample[] SnapshotWindow()
    {
        if (_ringCount == 0) return Array.Empty<FrameSample>();

        int want = Math.Clamp(WindowFrames, 1, _ringCount);
        var outp = new FrameSample[want];
        int start = (_ringWrite - want + RingCapacity) % RingCapacity;
        for (int i = 0; i < want; i++)
            outp[i] = _ring[(start + i) % RingCapacity];
        return outp;
    }

    /// <summary>Tagged console lines seen recently, oldest first.</summary>
    public LogEvent[] SnapshotEvents()
    {
        lock (_eventLock)
        {
            var outp = new LogEvent[_eventCount];
            int start = (_eventWrite - _eventCount + EventCapacity) % EventCapacity;
            for (int i = 0; i < _eventCount; i++)
                outp[i] = _events[(start + i) % EventCapacity];
            return outp;
        }
    }

    /// <summary>
    /// Record one tagged console line against the current frame. Fed by the
    /// console tee, so [stream], [gpu-upload], [collision-async] and friends land
    /// here with no call-site changes anywhere in the codebase - which is the
    /// whole point: correlating them by hand against a remembered moment is the
    /// step this plan exists to delete.
    ///
    /// Called from worker threads as well as the render thread.
    /// </summary>
    public void NoteEvent(string line)
    {
        lock (_eventLock)
        {
            _events[_eventWrite] = new LogEvent
            {
                FrameIndex = _frameCounter,
                Text = line.Length > 300 ? line.Substring(0, 300) : line,
            };
            _eventWrite = (_eventWrite + 1) % EventCapacity;
            if (_eventCount < EventCapacity) _eventCount++;
        }
    }

    public void NoteRecorded(HitchSummary summary)
    {
        RecordedCount++;
        History.Add(summary);
        if (History.Count > 64) History.RemoveAt(0);
    }

    // ---- Types ----

    /// <summary>One frame. A value type in a preallocated array: no per-frame GC.</summary>
    public struct FrameSample
    {
        public long Index;

        /// <summary>True wall-clock period, render-entry to render-entry.</summary>
        public double FrameMs;

        public double UpdateMs;
        public double RenderMs;

        public double MoveMs;
        public double PumpPreloadsMs;
        public double AcceptCollisionMs;
        public double DoodadCollisionSnapshotMs;
        public double ResidencyMs;
        public double PreloadMs;
        public double DiscoverMs;
        public double DoodadDemandMs;
        public double WarmMs;
        public double UnitMs;
        public double CameraMs;

        public double WorldRenderMs;

        // World is terrain + WMO + doodads + foliage in one bracket, which was
        // enough to clear foliage and no help at all after that. These three
        // come from the renderers' own existing RenderMilliseconds.
        public double TerrainRenderMs;
        public double WmoRenderMs;
        public double DoodadRenderMs;
        public double FoliageRenderMs;
        public double LiquidRenderMs;
        public double CharacterRenderMs;
        public double DebugRenderMs;

        // GPU EXECUTION, delayed by a frame or two - the half of the frame that
        // went unmeasured for this whole investigation. CPU submission time says
        // nothing about how long the GPU then took, and a frame that misses
        // vblank waits a whole extra interval in the swap. That shows up as
        // "present", which is why present cannot be read as "driver's fault"
        // without these numbers beside it.
        public double GpuTotalMs;
        public double GpuTerrainMs;
        public double GpuWmoMs;
        public double GpuDoodadMs;
        public double GpuCharacterMs;

        // The frame boundary, previously invisible (handbook 3.30).
        public double InputMs;
        public double ImguiUpdateMs;
        public double GuiMs;
        public double PresentMs;

        public float X, Y, Z;
        public int Col, Row;

        public int ResidentTiles;
        public int WmoQueued;
        public int M2Queued;
        public int DiscoveryTiles;

        /// <summary>
        /// Time inside the frame that neither Update nor Render accounts for:
        /// swap, present, driver, input, ImGui. THE field this plan was written
        /// for - if a 1000 ms frame lands here, the fix is at the window/driver
        /// boundary and not one line of streaming code needs touching.
        /// </summary>
        public double UnaccountedMs => Math.Max(
            0.0,
            FrameMs - UpdateMs - RenderMs - InputMs - ImguiUpdateMs - GuiMs - PresentMs);

        /// <summary>
        /// Time inside Update that none of its sub-timers covers: input
        /// handling, key edges, and anything added later without a bracket.
        /// Watch it - a phase breakdown that does not sum is not a breakdown.
        /// </summary>
        public double UpdateUnaccountedMs => Math.Max(
            0.0,
            UpdateMs - MoveMs - ResidencyMs - PreloadMs - UnitMs - CameraMs
                     - PumpPreloadsMs - AcceptCollisionMs - DoodadCollisionSnapshotMs);

        /// <summary>Which measured phase held the most time. Names the suspect.</summary>
        public string DominantPhase()
        {
            double best = UnaccountedMs;
            string name = "unmeasured";

            void Consider(double ms, string label)
            {
                if (ms > best) { best = ms; name = label; }
            }

            Consider(ResidencyMs, "residency");
            Consider(PumpPreloadsMs, "terrain-adopt");
            Consider(AcceptCollisionMs, "collision-accept");
            Consider(DoodadCollisionSnapshotMs, "doodad-collision-snapshot");
            Consider(UpdateUnaccountedMs, "update-unmeasured");
            Consider(DiscoverMs, "adt-discovery");
            Consider(DoodadDemandMs, "doodad-demand-scan");
            Consider(WarmMs, "model-finalize");
            Consider(MoveMs, "movement-collision");
            Consider(UnitMs, "character-update");
            Consider(CameraMs, "camera-collision");
            Consider(TerrainRenderMs, "terrain-render");
            Consider(WmoRenderMs, "wmo-render");
            Consider(DoodadRenderMs, "doodad-render");
            Consider(FoliageRenderMs, "foliage-scatter-render");
            Consider(LiquidRenderMs, "liquid-render");
            Consider(CharacterRenderMs, "character-render");
            Consider(DebugRenderMs, "debug-render");

            Consider(PresentMs, "present-swap-driver");
            Consider(GpuTotalMs, "gpu-execution");
            Consider(GuiMs, "hud-imgui");
            Consider(ImguiUpdateMs, "imgui-update");
            Consider(InputMs, "input-poll");

            return name;
        }
    }

    /// <summary>What GameLoop hands over at the end of each frame.</summary>
    public struct FramePhases
    {
        public double UpdateMs, MoveMs, ResidencyMs, PreloadMs, UnitMs, CameraMs;
        public double PumpPreloadsMs, AcceptCollisionMs, DoodadCollisionSnapshotMs;
        public double DiscoverMs, DoodadDemandMs, WarmMs;
        public double RenderMs, WorldRenderMs, CharacterRenderMs, DebugRenderMs;
        public double FoliageRenderMs, LiquidRenderMs;
        public double TerrainRenderMs, WmoRenderMs, DoodadRenderMs;
        public double InputMs, ImguiUpdateMs, GuiMs, PresentMs;
        public double GpuTotalMs, GpuTerrainMs, GpuWmoMs, GpuDoodadMs, GpuCharacterMs;
        public float X, Y, Z;
        public int Col, Row;
        public int ResidentTiles, WmoQueued, M2Queued, DiscoveryTiles;
    }

    public struct LogEvent
    {
        public long FrameIndex;
        public string Text;
    }

    public sealed class HitchSummary
    {
        public string Name { get; set; } = "";
        public double FrameMs { get; set; }
        public double UnaccountedMs { get; set; }
        public string Phase { get; set; } = "";
        public int Col { get; set; }
        public int Row { get; set; }
    }

    // ---- Console tee ----

    /// <summary>
    /// Wraps Console.Out so every line beginning with '[' is copied into the
    /// event ring as well as printed. Chosen over editing hundreds of
    /// Console.WriteLine call sites: zero risk to existing output, and it picks
    /// up tags in files this change never opened.
    /// </summary>
    public static void InstallConsoleTee(HitchRecorder recorder)
    {
        var original = Console.Out;
        Console.SetOut(new TeeWriter(original, recorder));
    }

    private sealed class TeeWriter : TextWriter
    {
        private readonly TextWriter _inner;
        private readonly HitchRecorder _recorder;
        private readonly StringBuilder _line = new(256);
        private readonly object _gate = new();

        public TeeWriter(TextWriter inner, HitchRecorder recorder)
        {
            _inner = inner;
            _recorder = recorder;
        }

        public override Encoding Encoding => _inner.Encoding;

        // Each override hands the WHOLE string to the real console in one call
        // and only splits lines in a local buffer. Forwarding char-by-char would
        // multiply console I/O by the length of every message - an instrument
        // that slows the thing it measures reports its own overhead as the bug.
        public override void Write(char value)
        {
            _inner.Write(value);
            Accumulate(value);
        }

        public override void Write(string? value)
        {
            if (value is null) return;
            _inner.Write(value);
            Accumulate(value);
        }

        public override void WriteLine(string? value)
        {
            _inner.WriteLine(value);
            if (value is not null) Accumulate(value);
            Accumulate('\n');
        }

        public override void WriteLine()
        {
            _inner.WriteLine();
            Accumulate('\n');
        }

        public override void Flush() => _inner.Flush();

        private void Accumulate(string s)
        {
            for (int i = 0; i < s.Length; i++) Accumulate(s[i]);
        }

        private void Accumulate(char value)
        {
            string? complete = null;

            lock (_gate)
            {
                if (value == '\n')
                {
                    if (_line.Length > 0)
                    {
                        complete = _line.ToString().TrimEnd('\r');
                        _line.Clear();
                    }
                }
                else if (_line.Length < 1024)
                {
                    _line.Append(value);
                }
            }

            // Outside the lock: NoteEvent takes its own, and nesting them would
            // be a deadlock waiting for a worker thread to print.
            if (complete is not null && complete.Length > 0 && complete[0] == '[')
                _recorder.NoteEvent(complete);
        }
    }
}
