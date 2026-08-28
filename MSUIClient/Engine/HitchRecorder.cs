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

    /// <summary>The frame most recently closed by <see cref="FrameBoundary"/>.</summary>
    public FrameSample LastCompleted { get; private set; }

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
            _pending.LoadNetPumpMs = phases.LoadNetPumpMs;
            _pending.LoadStepMs = phases.LoadStepMs;
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
            _pending.DoodadCullMs = phases.DoodadCullMs;
            _pending.DoodadInstanceUploadMs = phases.DoodadInstanceUploadMs;
            _pending.DoodadDrawMs = phases.DoodadDrawMs;
            _pending.DoodadFirstTouchModels = phases.DoodadFirstTouchModels;
            _pending.DoodadUploadedModels = phases.DoodadUploadedModels;
            _pending.DoodadCullModels = phases.DoodadCullModels;
            _pending.DoodadCullInstances = phases.DoodadCullInstances;
            _pending.FoliageRenderMs = phases.FoliageRenderMs;
            _pending.FoliageScatterMs = phases.FoliageScatterMs;
            _pending.FoliageDrawMs = phases.FoliageDrawMs;
            _pending.LiquidRenderMs = phases.LiquidRenderMs;
            _pending.CharacterRenderMs = phases.CharacterRenderMs;
            _pending.CreatureRenderMs = phases.CreatureRenderMs;
            _pending.CreatureLoadMs = phases.CreatureLoadMs;
            _pending.CreatureLoadsThisFrame = phases.CreatureLoadsThisFrame;
            _pending.CreatureCacheEntries = phases.CreatureCacheEntries;
            _pending.SelectionRenderMs = phases.SelectionRenderMs;
            _pending.SpellEffectRenderMs = phases.SpellEffectRenderMs;
            _pending.DebugRenderMs = phases.DebugRenderMs;

            _pending.InputMs = phases.InputMs;
            _pending.ImguiUpdateMs = phases.ImguiUpdateMs;
            _pending.GuiMs = phases.GuiMs;
            _pending.HudMs = phases.HudMs;
            _pending.ImguiRenderMs = phases.ImguiRenderMs;
            _pending.PresentMs = phases.PresentMs;

            _pending.UploadsInFlight = phases.UploadsInFlight;
            _pending.UploadsCompleted = phases.UploadsCompleted;

            _pending.Gen0 = phases.Gen0;
            _pending.Gen1 = phases.Gen1;
            _pending.Gen2 = phases.Gen2;
            _pending.AllocatedBytes = phases.AllocatedBytes;
            _pending.GcPauseMs = phases.GcPauseMs;
            _pending.ThreadCycles = phases.ThreadCycles;

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

            LastCompleted = _pending;
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
    /// Median and 95th-percentile frame time over the ring, in ms.
    ///
    /// WHY A RECORD NEEDS THIS. ThresholdMs is absolute, so the moment a
    /// scene's BASELINE crosses it the recorder stops reporting spikes and
    /// starts reporting the scene. Standing still in Stormwind at a steady
    /// 30-31 ms produced four consecutive records, three seconds apart, that
    /// looked exactly like four hitches and were in fact one slow scene - and
    /// the one frame among them that WAS an outlier (38 ms, GPU 25 vs 13) did
    /// not stand out at all. See SYSTEM_STREAMING.md section 5A.20.
    ///
    /// Printing p50 next to the frame time makes that distinction free: a
    /// 31 ms frame against a 30 ms median is the baseline, and a 38 ms frame
    /// against the same median is the thing worth reading. The TRIP rule is
    /// deliberately left absolute - a relative rule set anywhere above 1.2x
    /// would have suppressed the 38 ms frame, which was the most informative
    /// one in the run.
    /// </summary>
    public (double P50, double P95) WindowPercentiles()
    {
        int n = _ringCount;
        if (n == 0) return (0.0, 0.0);

        var times = new double[n];
        for (int i = 0; i < n; i++)
        {
            int idx = (_ringWrite - n + i + RingCapacity) % RingCapacity;
            times[i] = _ring[idx].FrameMs;
        }
        Array.Sort(times);
        return (times[n / 2], times[Math.Min(n - 1, (int)(n * 0.95))]);
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

        /// <summary>True wall-clock period, Update-entry to Update-entry.</summary>
        public double FrameMs;

        public double UpdateMs;
        public double LoadNetPumpMs;
        public double LoadStepMs;
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

        /// <summary>
        /// The periodic full foliage re-scatter, split out of FoliageRenderMs.
        /// It fires roughly once a second while walking and rebuilds the whole
        /// resident set; the other ~60 frames it is zero. Averaging the two
        /// together reported a small constant and hid a periodic spike.
        /// </summary>
        public double FoliageScatterMs;

        /// <summary>Drawing foliage. Every frame.</summary>
        public double FoliageDrawMs;
        public double LiquidRenderMs;
        public double CharacterRenderMs;
        public double CreatureRenderMs;
        public double CreatureLoadMs;
        public int CreatureLoadsThisFrame;
        public int CreatureCacheEntries;
        public double SelectionRenderMs;
        public double SpellEffectRenderMs;
        public double DebugRenderMs;

        // ── Inside the doodad pass (2026-07-25) ─────────────────────────────
        //
        // DoodadRenderMs hit 60.3 on a crossing frame while the GPU drew the
        // same pass in 0.1 ms. One number could not say whether that was our
        // cull arithmetic over 6,695 placements or the driver stalling on first
        // touch of models uploaded from the shared context. These three sum to
        // DoodadRenderMs and settle it.

        /// <summary>Distance + frustum rejection. Ours, pure CPU, scales with placements.</summary>
        public double DoodadCullMs;

        /// <summary>Per-model glBufferData of instance data. Driver call.</summary>
        public double DoodadInstanceUploadMs;

        /// <summary>Texture binds, uniform sets, DrawElementsInstanced. Driver calls.</summary>
        public double DoodadDrawMs;

        /// <summary>Models drawn this frame that were not drawn last frame.</summary>
        public int DoodadFirstTouchModels;

        /// <summary>Models that issued a glBufferData this frame.</summary>
        public int DoodadUploadedModels;

        /// <summary>_byModel entries walked by the cull this frame.</summary>
        public int DoodadCullModels;

        /// <summary>Instances examined by the cull this frame.</summary>
        public int DoodadCullInstances;

        /// <summary>
        /// Nanoseconds of cull per instance examined. THE number, because it
        /// converts a one-off measurement into a rate that can be compared
        /// against the neighbouring frames in the ring - which walk the SAME
        /// instances. Identical instance count with a 100x time difference
        /// cannot be explained by workload, only by the state of the memory
        /// those instances live in.
        /// </summary>
        public double DoodadCullNsPerInstance => DoodadCullInstances > 0
            ? DoodadCullMs * 1_000_000.0 / DoodadCullInstances
            : 0.0;

        /// <summary>
        /// Time in the doodad pass that the three-way split does not cover. The
        /// split is a breakdown, so it has to be checked like one.
        /// </summary>
        public double DoodadUnaccountedMs => Math.Max(
            0.0, DoodadRenderMs - DoodadCullMs - DoodadInstanceUploadMs - DoodadDrawMs);

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

        /// <summary>HudMs + ImguiRenderMs. Kept whole so the residual balances.</summary>
        public double GuiMs;

        /// <summary>Our HUD code building ImGui windows. Pure CPU, ~0.25 ms, flat.</summary>
        public double HudMs;

        /// <summary>
        /// The frame's LAST GL submission. Not "ImGui cost" - the driver's
        /// implicit flush lands here, so this is the bucket that holds a stall
        /// the rest of the frame caused. If this is large while GpuTotalMs is
        /// small, the GPU was idle and the CPU was blocked in the driver.
        /// </summary>
        public double ImguiRenderMs;

        public double PresentMs;

        /// <summary>
        /// Shared-context uploads still outstanding as this frame CLOSED - the
        /// phases are gathered at the next Update entry, so this is an
        /// end-of-frame sample, not a during-frame maximum. An upload that
        /// started and finished inside the frame reads 0 here and 1 in
        /// <see cref="UploadsCompleted"/>. Read both.
        /// </summary>
        public int UploadsInFlight;

        /// <summary>
        /// Shared-context uploads that finished during this frame. A true delta
        /// over the frame, so this is the reliable half of the pair.
        /// </summary>
        public int UploadsCompleted;

        // ── GC, added 2026-07-25 ────────────────────────────────────────────
        //
        // Added because a record finally arrived that no GL explanation fits:
        // 33 ms frame, hud 16.4 (our ImGui window building - no GL, no I/O),
        // imguiFlush 0.1, uploads 0, GPU 0.9. Managed code doing nothing but
        // building strings does not block for a vblank. A collection does, and
        // it stops every thread wherever they happen to be - which is exactly
        // the "wanders between phases" behaviour that has looked like a driver
        // throttle all along.
        //
        // It also explains the thing that should have been the loudest clue:
        // FOUR correct, measured fixes moved work off the main thread and the
        // felt stutter survived every one. Moving an allocation to a worker
        // thread does not stop it triggering a collection, and a collection
        // pauses the render thread regardless of who allocated. See the
        // 505,037-triangle expansion and the 194,861-node BVH - both are
        // Large Object Heap allocations, and LOH allocation forces gen2.

        /// <summary>Gen0 collections during this frame. Cheap and frequent; noise unless large.</summary>
        public int Gen0;

        /// <summary>Gen1 collections during this frame.</summary>
        public int Gen1;

        /// <summary>
        /// Gen2 collections during this frame. THE field to read first. Any
        /// non-zero value on a hitch frame ends the investigation.
        /// </summary>
        public int Gen2;

        /// <summary>
        /// Managed bytes allocated process-wide during this frame, all threads.
        /// Names the pressure even on frames that did not collect.
        /// </summary>
        public long AllocatedBytes;

        /// <summary>
        /// Milliseconds this frame spent in GC pause, from
        /// <c>GC.GetTotalPauseDuration()</c>. Process-wide and exact - not
        /// inferred from collection counts. If this holds most of a hitch
        /// frame, no renderer or streaming change can fix it and the work is in
        /// allocation, not in GL.
        /// </summary>
        public double GcPauseMs;

        /// <summary>
        /// Cycles the render thread actually retired during this frame
        /// (QueryThreadCycleTime; 0 where unavailable). Read against
        /// <see cref="FrameMs"/>, never on its own.
        /// </summary>
        public ulong ThreadCycles;

        /// <summary>
        /// Millions of retired cycles per millisecond of wall time. THE
        /// discriminator for a frame where nothing we own was running.
        ///
        /// A fully busy thread on this i7-12800H sits around 4-5 M/ms. So on a
        /// long frame:
        ///   ~4-5 - the thread WAS running and burning CPU. A driver busy-wait
        ///          spin, which Intel's GL driver is known to do. The fix is to
        ///          stop it spinning: swap interval, adaptive vsync, or our own
        ///          frame pacing.
        ///   &lt;1  - the thread was NOT running. Blocked in a kernel wait or
        ///          descheduled by the OS. A completely different fix.
        /// No calibration needed: the comparison is against this frame's own
        /// wall clock and the two answers are an order of magnitude apart.
        /// </summary>
        public double ThreadMCyclesPerMs => FrameMs > 0.0
            ? ThreadCycles / 1_000_000.0 / FrameMs
            : 0.0;

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
            UpdateMs - LoadNetPumpMs - LoadStepMs
                     - MoveMs - ResidencyMs - PreloadMs - UnitMs - CameraMs
                     - PumpPreloadsMs - AcceptCollisionMs - DoodadCollisionSnapshotMs);

        /// <summary>Which measured phase held the most time. Names the suspect.</summary>
        public string DominantPhase()
        {
            // GC is checked first and separately because it is NOT a peer of the
            // other buckets. A collection pauses the thread wherever it happens
            // to be, so its cost is already inside whichever phase was running -
            // adding it to the ranking would double-count it, and ranking it
            // against the phase it is hiding inside would always lose. A pause
            // holding a third of the frame is the cause of that frame no matter
            // which bucket got charged, so it wins outright.
            if (GcPauseMs > 1.0 && GcPauseMs > FrameMs * 0.33)
                return Gen2 > 0 ? "gc-pause-gen2" : "gc-pause";

            double best = UnaccountedMs;
            string name = "unmeasured";

            void Consider(double ms, string label)
            {
                if (ms > best) { best = ms; name = label; }
            }

            Consider(ResidencyMs, "residency");
            Consider(LoadNetPumpMs, "load-network-pump");
            Consider(LoadStepMs, "load-phase-step");
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
            // Named at the sub-phase, because "doodad-render" was true and
            // useless: it did not distinguish our cull arithmetic from a driver
            // stall on first touch, and those have opposite fixes.
            Consider(DoodadCullMs, "doodad-cull-cpu");
            Consider(DoodadInstanceUploadMs, "doodad-instance-upload");
            Consider(DoodadDrawMs, "doodad-draw-submit");
            Consider(DoodadUnaccountedMs, "doodad-render-unmeasured");
            // Was one bucket named "foliage-scatter-render", which named both
            // jobs and identified neither. The scatter is periodic and large;
            // the draw is per frame and small.
            Consider(FoliageScatterMs, "foliage-rescatter");
            Consider(FoliageDrawMs, "foliage-draw");
            Consider(LiquidRenderMs, "liquid-render");
            Consider(CharacterRenderMs, "character-render");
            Consider(CreatureLoadMs, "creature-model-load");
            Consider(Math.Max(0.0, CreatureRenderMs - CreatureLoadMs), "creature-render");
            Consider(SelectionRenderMs, "selection-render");
            Consider(SpellEffectRenderMs, "spell-effect-render");
            Consider(DebugRenderMs, "debug-render");

            // "present-swap-driver" smuggled a conclusion into a bucket that
            // only measures render-end to next-update-entry. On the Iris Xe the
            // driver does not block in the swap at all - present stays under a
            // millisecond on 26 ms frames - so the name was wrong twice over.
            Consider(PresentMs, "swap-and-events");
            Consider(GpuTotalMs, "gpu-execution");

            // Was one "hud-imgui" bucket, which blamed our HUD for driver
            // stalls. HudMs is our code; ImguiRenderMs is the frame's last GL
            // call and is where a driver flush lands. Never merge them again.
            Consider(HudMs, "hud-build");
            Consider(ImguiRenderMs, "driver-flush-at-imgui");
            Consider(ImguiUpdateMs, "imgui-update");
            Consider(InputMs, "input-poll");

            return name;
        }
    }

    /// <summary>What GameLoop hands over at the end of each frame.</summary>
    public struct FramePhases
    {
        public double UpdateMs, LoadNetPumpMs, LoadStepMs;
        public double MoveMs, ResidencyMs, PreloadMs, UnitMs, CameraMs;
        public double PumpPreloadsMs, AcceptCollisionMs, DoodadCollisionSnapshotMs;
        public double DiscoverMs, DoodadDemandMs, WarmMs;
        public double RenderMs, WorldRenderMs, CharacterRenderMs, DebugRenderMs;
        public double CreatureRenderMs, CreatureLoadMs, SelectionRenderMs, SpellEffectRenderMs;
        public int CreatureLoadsThisFrame, CreatureCacheEntries;
        public double FoliageRenderMs, LiquidRenderMs;
        public double FoliageScatterMs, FoliageDrawMs;
        public double TerrainRenderMs, WmoRenderMs, DoodadRenderMs;
        public double DoodadCullMs, DoodadInstanceUploadMs, DoodadDrawMs;
        public int DoodadFirstTouchModels, DoodadUploadedModels;
        public int DoodadCullModels, DoodadCullInstances;
        public double InputMs, ImguiUpdateMs, GuiMs, HudMs, ImguiRenderMs, PresentMs;
        public double GpuTotalMs, GpuTerrainMs, GpuWmoMs, GpuDoodadMs, GpuCharacterMs;
        public float X, Y, Z;
        public int Col, Row;
        public int ResidentTiles, WmoQueued, M2Queued, DiscoveryTiles;
        public int UploadsInFlight, UploadsCompleted;
        public int Gen0, Gen1, Gen2;
        public long AllocatedBytes;
        public double GcPauseMs;
        public ulong ThreadCycles;
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
        // Every '['-tagged line also lands in msui-console.log next to the exe,
        // truncated per session. The console scrollback dies with the terminal,
        // and diagnosing a live-play report ("audio got choppy in the abbey")
        // has repeatedly stalled on exactly that: the [soundscape]/[audio]
        // evidence existed for a moment and nobody could read it afterwards.
        StreamWriter? file = null;
        try
        {
            file = new StreamWriter(
                Path.Combine(AppContext.BaseDirectory, "msui-console.log"),
                append: false) { AutoFlush = true };
            file.WriteLine($"# MSUI console log - session {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        }
        catch
        {
            file = null;   // diagnostics must never keep the game from starting
        }
        Console.SetOut(new TeeWriter(original, recorder, file));
    }

    private sealed class TeeWriter : TextWriter
    {
        private readonly TextWriter _inner;
        private readonly HitchRecorder _recorder;
        private StreamWriter? _file;
        private readonly StringBuilder _line = new(256);
        private readonly object _gate = new();
        private readonly object _fileGate = new();

        public TeeWriter(TextWriter inner, HitchRecorder recorder, StreamWriter? file)
        {
            _inner = inner;
            _recorder = recorder;
            _file = file;
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
            {
                _recorder.NoteEvent(complete);
                if (_file is not null)
                {
                    try
                    {
                        lock (_fileGate)
                            _file?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {complete}");
                    }
                    catch
                    {
                        _file = null;   // disk trouble: stop trying, keep playing
                    }
                }
            }
        }
    }
}
