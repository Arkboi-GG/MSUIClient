using System.Diagnostics;
using System.Text.Json;
using MSUIClient.Engine;
using MSUIClient.Formats;
using MSUIClient.World.Units;

namespace MSUIClient;

/// <summary>
/// PLAN_17 I2: one bounded, allocation-free-in-frame record of a cold world-load
/// cycle. Arrays are fixed at the instrument's stated first-ten limit; JSON
/// shaping and file IO happen only after the curtain clears and run off-thread.
/// </summary>
public sealed partial class GameLoop
{
    private const int LoadDequeueTraceCapacity = 10;
    private const int LoadFrameTraceCapacity = 1024;
    private const int PostClearExceptionCapacity = 64;
    private const double FirstCreatureDrawTimeoutSeconds = 10.0;

    private LoadTimelineState? _loadTimeline;
    private string _postClearObservationName = "";
    private PostClearObservationState? _postClearObservation;

    private readonly struct LoadQueueDepths(
        int terrain, int wmo, int doodads, int foliage)
    {
        public int Terrain { get; } = terrain;
        public int Wmo { get; } = wmo;
        public int Doodads { get; } = doodads;
        public int Foliage { get; } = foliage;
    }

    private struct LoadPhaseTrace
    {
        public string Name;
        public long StartedStamp;
        public double WallClockMs;
        public string ExitReason;
        public LoadQueueDepths EntryQueues;
        public LoadQueueDepths ExitQueues;
    }

    private struct LoadDequeueTrace
    {
        public string Queue;
        public string Item;
        public float Distance;
        public int Col;
        public int Row;
    }

    private struct LoadFrameTrace
    {
        public WorldLoadPhase LoaderPhase;
        public HitchRecorder.FrameSample Sample;
    }

    private sealed class LoadTimelineState
    {
        public string Name = "";
        public string MapName = "";
        public int MapId;
        public float StartX, StartY, StartZ;
        public long StartedStamp;
        public long CurtainClearStamp;
        public long FirstCreatureDrawStamp;
        public int CurrentPhase = -1;
        public readonly LoadPhaseTrace[] Phases = new LoadPhaseTrace[10];
        public int PhaseCount;
        public readonly LoadDequeueTrace[] Terrain = new LoadDequeueTrace[LoadDequeueTraceCapacity];
        public readonly LoadDequeueTrace[] Wmo = new LoadDequeueTrace[LoadDequeueTraceCapacity];
        public readonly LoadDequeueTrace[] OutdoorDoodad = new LoadDequeueTrace[LoadDequeueTraceCapacity];
        public readonly LoadDequeueTrace[] InteriorDoodad = new LoadDequeueTrace[LoadDequeueTraceCapacity];
        public readonly LoadDequeueTrace[] Foliage = new LoadDequeueTrace[LoadDequeueTraceCapacity];
        public readonly LoadFrameTrace[] Frames = new LoadFrameTrace[LoadFrameTraceCapacity];
        public int TerrainCount, WmoCount, OutdoorDoodadCount, InteriorDoodadCount, FoliageCount;
        public int FrameCount, FrameWrite;
        public WorldLoadPhase OpenFramePhase;
        public int PacketsPumpedDuringLoad;
        public int UnitsKnownAtClear;
        public int UnitsKnownBeforeFramePump;
        public double MaxFrameMsDuringCurtain;
        public HitchRecorder.FrameSample WorstFrame;
        public int Gen0, Gen1, Gen2;
        public long AllocatedBytes;
        public double GcPauseMs;
        public ulong ThreadCycles;
        public bool IncludeNextCompletedFrame;
        public long FallbackArchiveOpensAtStart;
        public long WorstAllocatedBytes;
        public HitchRecorder.FrameSample WorstAllocatedFrame;
        public int FramesOver40, UnmeasuredFramesOver40;
        public readonly HitchRecorder.FrameSample[] Exceptions =
            new HitchRecorder.FrameSample[PostClearExceptionCapacity];
        public int ExceptionCount;
    }

    private sealed class PostClearObservationState
    {
        public readonly object Gate = new();
        public string Name = "";
        public long CurtainClearStamp;
        public double MaxFrameMs;
        public HitchRecorder.FrameSample WorstFrame;
        public int Gen0, Gen1, Gen2;
        public long AllocatedBytes, WorstAllocatedBytes, WorstPostClearAllocatedBytes;
        public HitchRecorder.FrameSample WorstAllocatedFrame, WorstPostClearAllocatedFrame;
        public double GcPauseMs;
        public int FramesOver40, UnmeasuredFramesOver40;
        public readonly HitchRecorder.FrameSample[] Exceptions =
            new HitchRecorder.FrameSample[PostClearExceptionCapacity];
        public int ExceptionCount;
    }

    private LoadQueueDepths CurrentLoadQueueDepths() => new(
        _terrain?.PendingPreloads ?? 0,
        _wmo?.PendingPreloads ?? 0,
        _doodads?.PendingPreloads ?? 0,
        _foliage?.PendingPreloads ?? 0);

    private static string LoadPhaseName(WorldLoadPhase phase) => phase switch
    {
        WorldLoadPhase.Terrain => "Terrain",
        WorldLoadPhase.WarmBuildings => "WarmBuildings",
        WorldLoadPhase.PlaceBuildings => "PlaceBuildings",
        WorldLoadPhase.Liquid => "Liquid",
        WorldLoadPhase.WarmDoodads => "WarmDoodads",
        WorldLoadPhase.PlaceDoodads => "PlaceDoodads",
        WorldLoadPhase.Collision => "Collision",
        WorldLoadPhase.Finish => "Finish",
        WorldLoadPhase.Fade => "Fade",
        _ => "Done",
    };

    private void BeginLoadTimeline()
    {
        long startedStamp = Stopwatch.GetTimestamp();
        _creatureLifecycle.BeginWorldLoad(startedStamp);
        if (!_config.DevTools) return;

        string mapSlug = string.Concat(_config.Start.MapName
            .ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-'));
        if (string.IsNullOrWhiteSpace(mapSlug)) mapSlug = _config.Start.Map.ToString();

        string dir = Path.Combine(_config.RepoRoot, "dumps");
        int next = 1;
        if (Directory.Exists(dir))
        {
            string prefix = $"load-{mapSlug}-";
            foreach (string path in Directory.EnumerateFiles(dir, prefix + "*.json"))
            {
                string stem = Path.GetFileNameWithoutExtension(path);
                if (stem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(stem.AsSpan(prefix.Length), out int n))
                    next = Math.Max(next, n + 1);
            }
        }

        _loadTimeline = new LoadTimelineState
        {
            Name = $"load-{mapSlug}-{next}",
            MapName = _config.Start.MapName,
            MapId = _config.Start.Map,
            StartX = _config.Start.X,
            StartY = _config.Start.Y,
            StartZ = _config.Start.Z,
            StartedStamp = startedStamp,
            OpenFramePhase = WorldLoadPhase.Terrain,
            UnitsKnownBeforeFramePump = _entities.UnitCount,
            FallbackArchiveOpensAtStart = AdtTerrainReader.FallbackArchiveOpens,
        };

        // Startup suppression hides exactly the cold-start window this timeline
        // exists to explain. From BeginWorldLoad onward, every >25 ms curtain
        // frame must be eligible for a hitch record.
        _hitch.SuppressFor(0.0);
    }

    private void StartLoadTimelinePhase(WorldLoadPhase phase)
    {
        LoadTimelineState? state = _loadTimeline;
        if (state is null || state.PhaseCount >= state.Phases.Length) return;
        int index = state.PhaseCount++;
        state.CurrentPhase = index;
        state.Phases[index] = new LoadPhaseTrace
        {
            Name = LoadPhaseName(phase),
            StartedStamp = Stopwatch.GetTimestamp(),
            ExitReason = "pending",
            EntryQueues = CurrentLoadQueueDepths(),
        };
    }

    private void ExitLoadTimelinePhase(string exitReason)
    {
        LoadTimelineState? state = _loadTimeline;
        if (state is null || state.CurrentPhase < 0) return;
        int index = state.CurrentPhase;
        LoadPhaseTrace trace = state.Phases[index];
        trace.WallClockMs = Stopwatch.GetElapsedTime(trace.StartedStamp).TotalMilliseconds;
        trace.ExitReason = exitReason;
        trace.ExitQueues = CurrentLoadQueueDepths();
        state.Phases[index] = trace;
        state.CurrentPhase = -1;
    }

    private void NoteLoadPacketPumped(bool loadActiveAtPumpEntry)
    {
        if (loadActiveAtPumpEntry && _loadTimeline is { CurtainClearStamp: 0 } state)
            state.PacketsPumpedDuringLoad++;
    }

    private void SnapshotLoadUnitsBeforePump()
    {
        if (_loadTimeline is { CurtainClearStamp: 0 } state)
            state.UnitsKnownBeforeFramePump = _entities.UnitCount;
    }

    private void NoteLoadTerrainDequeue(int col, int row)
    {
        LoadTimelineState? state = _loadTimeline;
        if (state is null || state.TerrainCount >= LoadDequeueTraceCapacity) return;
        int distance = Math.Max(Math.Abs(col - _loadCentre.col), Math.Abs(row - _loadCentre.row));
        state.Terrain[state.TerrainCount++] = new LoadDequeueTrace
        {
            Queue = "terrain",
            Col = col,
            Row = row,
            Distance = distance,
        };
    }

    private void NoteLoadAssetDequeue(string queue, string item, float distanceSq)
    {
        LoadTimelineState? state = _loadTimeline;
        if (state is null || state.CurtainClearStamp != 0) return;
        float distance = MathF.Sqrt(MathF.Max(0f, distanceSq));
        var trace = new LoadDequeueTrace { Queue = queue, Item = item, Distance = distance };
        switch (queue)
        {
            case "wmo" when state.WmoCount < LoadDequeueTraceCapacity:
                state.Wmo[state.WmoCount++] = trace;
                break;
            case "outdoor-doodad" when state.OutdoorDoodadCount < LoadDequeueTraceCapacity:
                state.OutdoorDoodad[state.OutdoorDoodadCount++] = trace;
                break;
            case "interior-doodad" when state.InteriorDoodadCount < LoadDequeueTraceCapacity:
                state.InteriorDoodad[state.InteriorDoodadCount++] = trace;
                break;
            case "foliage" when state.FoliageCount < LoadDequeueTraceCapacity:
                state.Foliage[state.FoliageCount++] = trace;
                break;
        }
    }

    private void NoteLoadFrame(in HitchRecorder.FrameSample sample)
    {
        NotePostClearFrame(sample);
        LoadTimelineState? state = _loadTimeline;
        if (state is null || sample.Index == 0) return;
        if (state.CurtainClearStamp != 0 && !state.IncludeNextCompletedFrame) return;

        state.Frames[state.FrameWrite] = new LoadFrameTrace
        {
            LoaderPhase = state.OpenFramePhase,
            Sample = sample,
        };
        state.FrameWrite = (state.FrameWrite + 1) % state.Frames.Length;
        state.FrameCount = Math.Min(state.FrameCount + 1, state.Frames.Length);
        state.OpenFramePhase = _loadPhase;

        if (sample.FrameMs > state.MaxFrameMsDuringCurtain)
        {
            state.MaxFrameMsDuringCurtain = sample.FrameMs;
            state.WorstFrame = sample;
        }
        state.Gen0 += sample.Gen0;
        state.Gen1 += sample.Gen1;
        state.Gen2 += sample.Gen2;
        state.AllocatedBytes += sample.AllocatedBytes;
        if (sample.AllocatedBytes > state.WorstAllocatedBytes)
        {
            state.WorstAllocatedBytes = sample.AllocatedBytes;
            state.WorstAllocatedFrame = sample;
        }
        state.GcPauseMs += sample.GcPauseMs;
        state.ThreadCycles += sample.ThreadCycles;
        if (sample.FrameMs > 40.0)
        {
            state.FramesOver40++;
            if (sample.DominantPhase() == "unmeasured") state.UnmeasuredFramesOver40++;
        }
        if ((sample.FrameMs > 40.0 || sample.Gen2 > 0) &&
            state.ExceptionCount < state.Exceptions.Length)
            state.Exceptions[state.ExceptionCount++] = sample;
        if (state.CurtainClearStamp != 0) state.IncludeNextCompletedFrame = false;
    }

    private void NotePostClearFrame(in HitchRecorder.FrameSample sample)
    {
        PostClearObservationState? state = _postClearObservation;
        if (state is null || sample.Index == 0 ||
            Stopwatch.GetElapsedTime(state.CurtainClearStamp).TotalSeconds > 60.0) return;
        lock (state.Gate)
        {
            if (sample.FrameMs > state.MaxFrameMs)
            {
                state.MaxFrameMs = sample.FrameMs;
                state.WorstFrame = sample;
            }
            state.Gen0 += sample.Gen0;
            state.Gen1 += sample.Gen1;
            state.Gen2 += sample.Gen2;
            state.AllocatedBytes += sample.AllocatedBytes;
            if (sample.AllocatedBytes > state.WorstAllocatedBytes)
            {
                state.WorstAllocatedBytes = sample.AllocatedBytes;
                state.WorstAllocatedFrame = sample;
            }
            if (sample.AllocatedBytes > state.WorstPostClearAllocatedBytes)
            {
                state.WorstPostClearAllocatedBytes = sample.AllocatedBytes;
                state.WorstPostClearAllocatedFrame = sample;
            }
            state.GcPauseMs += sample.GcPauseMs;
            if (sample.FrameMs > 40.0)
            {
                state.FramesOver40++;
                if (sample.DominantPhase() == "unmeasured") state.UnmeasuredFramesOver40++;
            }
            if ((sample.FrameMs > 40.0 || sample.Gen2 > 0) &&
                state.ExceptionCount < state.Exceptions.Length)
                state.Exceptions[state.ExceptionCount++] = sample;
        }
    }

    private void NoteLoadCreatureDraw(int drawn)
    {
        LoadTimelineState? state = _loadTimeline;
        if (state is null || drawn <= 0) return;
        state.FirstCreatureDrawStamp = state.FirstCreatureDrawStamp == 0
            ? Stopwatch.GetTimestamp()
            : state.FirstCreatureDrawStamp;
        if (state.CurtainClearStamp != 0) CompleteLoadTimeline(firstDrawTimedOut: false);
    }

    private void NoteLoadCurtainClear()
    {
        LoadTimelineState? state = _loadTimeline;
        if (state is null) return;
        state.CurtainClearStamp = Stopwatch.GetTimestamp();
        state.UnitsKnownAtClear = state.UnitsKnownBeforeFramePump;
        state.IncludeNextCompletedFrame = true;
        _postClearObservation = new PostClearObservationState
        {
            Name = state.Name,
            CurtainClearStamp = state.CurtainClearStamp,
            MaxFrameMs = state.MaxFrameMsDuringCurtain,
            WorstFrame = state.WorstFrame,
            Gen0 = state.Gen0,
            Gen1 = state.Gen1,
            Gen2 = state.Gen2,
            AllocatedBytes = state.AllocatedBytes,
            WorstAllocatedBytes = state.WorstAllocatedBytes,
            WorstAllocatedFrame = state.WorstAllocatedFrame,
            GcPauseMs = state.GcPauseMs,
            FramesOver40 = state.FramesOver40,
            UnmeasuredFramesOver40 = state.UnmeasuredFramesOver40,
        };
        Array.Copy(state.Exceptions, _postClearObservation.Exceptions, state.ExceptionCount);
        _postClearObservation.ExceptionCount = state.ExceptionCount;
        SchedulePostClearObservation(state.Name, state.StartedStamp, state.CurtainClearStamp);

        if (state.FirstCreatureDrawStamp != 0 || !_config.Server.Enabled)
            CompleteLoadTimeline(firstDrawTimedOut: false);
    }

    private void SchedulePostClearObservation(string loadName, long loadStartedStamp,
        long curtainClearStamp)
    {
        if (string.Equals(_postClearObservationName, loadName,
                StringComparison.OrdinalIgnoreCase)) return;
        _postClearObservationName = loadName;
        string dir = Path.Combine(_config.RepoRoot, "dumps");
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(60));
            try
            {
                CreatureLifecycleTracker.LifecycleSnapshot[] lifecycle =
                    _creatureLifecycle.Snapshot(loadStartedStamp);
                CreatureLifecycleTracker.ProtocolSnapshot[] protocol =
                    _creatureLifecycle.SnapshotProtocol(loadStartedStamp);
                HitchRecorder.LogEvent[] events = _hitch.SnapshotEvents();
                WirePacket[] wireTimeline = _wire.Snapshot().ToArray();
                object? FrameAllocationEvidence(HitchRecorder.FrameSample sample) =>
                    sample.Index == 0 ? null : new
                    {
                        frameIndex = sample.Index,
                        frameMs = sample.FrameMs,
                        dominantPhase = sample.DominantPhase(),
                        allocatedBytes = sample.AllocatedBytes,
                        pauseMs = sample.GcPauseMs,
                        gen2 = sample.Gen2,
                        loadNetworkPumpMs = sample.LoadNetPumpMs,
                        loadStepMs = sample.LoadStepMs,
                        doodadDemandMs = sample.DoodadDemandMs,
                        modelFinalizeMs = sample.WarmMs,
                        unitMs = sample.UnitMs,
                        hudMs = sample.HudMs,
                        renderMs = sample.RenderMs,
                    };
                PostClearObservationState? observation = _postClearObservation;
                object? frameWindow = null;
                if (observation is not null && string.Equals(
                        observation.Name, loadName, StringComparison.OrdinalIgnoreCase))
                {
                    lock (observation.Gate)
                    {
                        HitchRecorder.FrameSample worst = observation.WorstFrame;
                        frameWindow = new
                        {
                            maxFrameMs = observation.MaxFrameMs,
                            framesOver40 = observation.FramesOver40,
                            unmeasuredFramesOver40 = observation.UnmeasuredFramesOver40,
                            worstFrame = worst.Index == 0 ? null : new
                            {
                                frameIndex = worst.Index,
                                frameMs = worst.FrameMs,
                                dominantPhase = worst.DominantPhase(),
                                allocatedBytes = worst.AllocatedBytes,
                                gen2 = worst.Gen2,
                            },
                            gc = new
                            {
                                pauseMs = observation.GcPauseMs,
                                gen0 = observation.Gen0,
                                gen1 = observation.Gen1,
                                gen2 = observation.Gen2,
                                allocatedBytes = observation.AllocatedBytes,
                                worstFrameAllocatedBytes = observation.WorstAllocatedBytes,
                                worstPostClearAllocatedBytes = observation.WorstPostClearAllocatedBytes,
                                worstAllocatedFrame = FrameAllocationEvidence(
                                    observation.WorstAllocatedFrame),
                                worstPostClearAllocatedFrame = FrameAllocationEvidence(
                                    observation.WorstPostClearAllocatedFrame),
                            },
                            exceptionFrames = observation.Exceptions
                                .Take(observation.ExceptionCount)
                                .Select(sample => new
                                {
                                    frameIndex = sample.Index,
                                    frameMs = sample.FrameMs,
                                    dominantPhase = sample.DominantPhase(),
                                    allocatedBytes = sample.AllocatedBytes,
                                    pauseMs = sample.GcPauseMs,
                                    gen0 = sample.Gen0,
                                    gen1 = sample.Gen1,
                                    gen2 = sample.Gen2,
                                    loadNetworkPumpMs = sample.LoadNetPumpMs,
                                    loadStepMs = sample.LoadStepMs,
                                    doodadDemandMs = sample.DoodadDemandMs,
                                    modelFinalizeMs = sample.WarmMs,
                                    unitMs = sample.UnitMs,
                                    hudMs = sample.HudMs,
                                    renderMs = sample.RenderMs,
                                }).ToArray(),
                        };
                    }
                }
                var payload = new
                {
                    load = loadName,
                    takenLocal = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    windowFromCurtainClearMs = Stopwatch.GetElapsedTime(curtainClearStamp)
                        .TotalMilliseconds,
                    frameWindow,
                    creatureLifecycle = lifecycle,
                    outgoingProtocol = protocol,
                    wireTimeline = wireTimeline.Select(packet => new
                    {
                        timeSeconds = packet.Time,
                        direction = packet.Outgoing ? "out" : "in",
                        opcode = packet.Opcode,
                        name = packet.OpcodeName,
                        size = packet.Size,
                    }).ToArray(),
                    events = events.Select(e => e.Text).ToArray(),
                };
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, $"creature-{loadName}.json");
                File.WriteAllText(path, JsonSerializer.Serialize(payload, DumpJson));
                Console.WriteLine($"[creature] 60-second observation wrote {path}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[creature] observation write failed - {ex.Message}");
            }
        });
    }

    private void PollLoadTimelineCompletion()
    {
        LoadTimelineState? state = _loadTimeline;
        if (state?.CurtainClearStamp is not > 0) return;
        if (Stopwatch.GetElapsedTime(state.CurtainClearStamp).TotalSeconds >=
            FirstCreatureDrawTimeoutSeconds)
            CompleteLoadTimeline(firstDrawTimedOut: true);
    }

    private void CompleteLoadTimeline(bool firstDrawTimedOut)
    {
        LoadTimelineState? state = _loadTimeline;
        if (state is null) return;
        _loadTimeline = null;

        HitchRecorder.LogEvent[] events = _hitch.SnapshotEvents();
        WirePacket[] wireTimeline = _wire.Snapshot().ToArray();
        CreatureLifecycleTracker.LifecycleSnapshot[] creatureLifecycle =
            _creatureLifecycle.Snapshot(state.StartedStamp);
        CreatureLifecycleTracker.ProtocolSnapshot[] outgoingProtocol =
            _creatureLifecycle.SnapshotProtocol(state.StartedStamp);
        string dir = Path.Combine(_config.RepoRoot, "dumps");
        _ = Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(dir);
                double clearMs = state.CurtainClearStamp == 0
                    ? 0
                    : Stopwatch.GetElapsedTime(state.StartedStamp, state.CurtainClearStamp).TotalMilliseconds;
                double? firstDrawMs = state.FirstCreatureDrawStamp == 0 || state.CurtainClearStamp == 0
                    ? null
                    : Math.Max(0, Stopwatch.GetElapsedTime(state.CurtainClearStamp,
                        state.FirstCreatureDrawStamp).TotalMilliseconds);

                object[] PhaseObjects() => state.Phases.Take(state.PhaseCount).Select(p => (object)new
                {
                    name = p.Name,
                    wallClockMs = p.WallClockMs,
                    exitReason = p.ExitReason,
                    entryQueues = new { terrain = p.EntryQueues.Terrain, wmo = p.EntryQueues.Wmo,
                        doodads = p.EntryQueues.Doodads, foliage = p.EntryQueues.Foliage },
                    exitQueues = new { terrain = p.ExitQueues.Terrain, wmo = p.ExitQueues.Wmo,
                        doodads = p.ExitQueues.Doodads, foliage = p.ExitQueues.Foliage },
                }).ToArray();

                object[] Dequeues(LoadDequeueTrace[] source, int count) => source.Take(count)
                    .Select(d => (object)new
                    {
                        queue = d.Queue,
                        item = string.IsNullOrEmpty(d.Item) ? $"[{d.Col},{d.Row}]" : d.Item,
                        distance = d.Distance,
                        tile = string.IsNullOrEmpty(d.Item) ? new[] { d.Col, d.Row } : null,
                    }).ToArray();

                object[] TopBuckets(in HitchRecorder.FrameSample f)
                {
                    var buckets = new (string Name, double Ms)[]
                    {
                        ("unaccounted", f.UnaccountedMs),
                        ("update-unmeasured", f.UpdateUnaccountedMs),
                        ("load-network-pump", f.LoadNetPumpMs),
                        ("load-phase-step", f.LoadStepMs),
                        ("movement-collision", f.MoveMs),
                        ("terrain-adopt", f.PumpPreloadsMs),
                        ("collision-accept", f.AcceptCollisionMs),
                        ("doodad-collision-snapshot", f.DoodadCollisionSnapshotMs),
                        ("residency", f.ResidencyMs),
                        ("adt-discovery", f.DiscoverMs),
                        ("doodad-demand-scan", f.DoodadDemandMs),
                        ("model-finalize", f.WarmMs),
                        ("character-update", f.UnitMs),
                        ("camera-collision", f.CameraMs),
                        ("terrain-render", f.TerrainRenderMs),
                        ("wmo-render", f.WmoRenderMs),
                        ("doodad-cull-cpu", f.DoodadCullMs),
                        ("doodad-instance-upload", f.DoodadInstanceUploadMs),
                        ("doodad-draw-submit", f.DoodadDrawMs),
                        ("foliage-rescatter", f.FoliageScatterMs),
                        ("foliage-draw", f.FoliageDrawMs),
                        ("liquid-render", f.LiquidRenderMs),
                        ("character-render", f.CharacterRenderMs),
                        ("creature-model-load", f.CreatureLoadMs),
                        ("creature-render", Math.Max(0.0, f.CreatureRenderMs - f.CreatureLoadMs)),
                        ("selection-render", f.SelectionRenderMs),
                        ("spell-effect-render", f.SpellEffectRenderMs),
                        ("debug-render", f.DebugRenderMs),
                        ("input-poll", f.InputMs),
                        ("imgui-update", f.ImguiUpdateMs),
                        ("hud-build", f.HudMs),
                        ("driver-flush-at-imgui", f.ImguiRenderMs),
                        ("swap-and-events", f.PresentMs),
                        ("gc-pause", f.GcPauseMs),
                    };
                    Array.Sort(buckets, static (a, b) => b.Ms.CompareTo(a.Ms));
                    return buckets.Take(2).Select(b => (object)new { name = b.Name, ms = b.Ms }).ToArray();
                }

                object[] FrameObjects()
                {
                    var result = new object[state.FrameCount];
                    int start = (state.FrameWrite - state.FrameCount + state.Frames.Length) % state.Frames.Length;
                    for (int i = 0; i < state.FrameCount; i++)
                    {
                        LoadFrameTrace trace = state.Frames[(start + i) % state.Frames.Length];
                        HitchRecorder.FrameSample f = trace.Sample;
                        result[i] = new
                        {
                            frameIndex = f.Index,
                            loaderPhase = LoadPhaseName(trace.LoaderPhase),
                            frameMs = f.FrameMs,
                            updateMs = f.UpdateMs,
                            renderMs = f.RenderMs,
                            unaccountedMs = f.UnaccountedMs,
                            updateUnmeasuredMs = f.UpdateUnaccountedMs,
                            dominantPhase = f.DominantPhase(),
                            topBuckets = TopBuckets(f),
                            update = new
                            {
                                moveMs = f.MoveMs,
                                loadNetworkPumpMs = f.LoadNetPumpMs,
                                loadPhaseStepMs = f.LoadStepMs,
                                terrainAdoptMs = f.PumpPreloadsMs,
                                collisionAcceptMs = f.AcceptCollisionMs,
                                doodadCollisionSnapshotMs = f.DoodadCollisionSnapshotMs,
                                residencyMs = f.ResidencyMs,
                                preloadMs = f.PreloadMs,
                                discoverMs = f.DiscoverMs,
                                doodadDemandMs = f.DoodadDemandMs,
                                modelFinalizeMs = f.WarmMs,
                                unitMs = f.UnitMs,
                                cameraMs = f.CameraMs,
                            },
                            render = new
                            {
                                worldMs = f.WorldRenderMs,
                                terrainMs = f.TerrainRenderMs,
                                wmoMs = f.WmoRenderMs,
                                doodadMs = f.DoodadRenderMs,
                                foliageMs = f.FoliageRenderMs,
                                liquidMs = f.LiquidRenderMs,
                                characterMs = f.CharacterRenderMs,
                                creatureMs = f.CreatureRenderMs,
                                creatureLoadMs = f.CreatureLoadMs,
                            },
                            boundary = new
                            {
                                inputMs = f.InputMs,
                                imguiUpdateMs = f.ImguiUpdateMs,
                                hudMs = f.HudMs,
                                imguiRenderMs = f.ImguiRenderMs,
                                presentMs = f.PresentMs,
                            },
                            gc = new { pauseMs = f.GcPauseMs, gen0 = f.Gen0, gen1 = f.Gen1, gen2 = f.Gen2,
                                allocatedBytes = f.AllocatedBytes },
                            thread = new { cycles = f.ThreadCycles, mCyclesPerMs = f.ThreadMCyclesPerMs },
                        };
                    }
                    return result;
                }

                var payload = new
                {
                    name = state.Name,
                    takenLocal = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    map = state.MapName,
                    mapId = state.MapId,
                    start = new[] { state.StartX, state.StartY, state.StartZ },
                    totalWallClockMs = Stopwatch.GetElapsedTime(state.StartedStamp).TotalMilliseconds,
                    curtainClearMs = clearMs,
                    phases = PhaseObjects(),
                    dequeues = new
                    {
                        terrain = Dequeues(state.Terrain, state.TerrainCount),
                        wmo = Dequeues(state.Wmo, state.WmoCount),
                        outdoorDoodad = Dequeues(state.OutdoorDoodad, state.OutdoorDoodadCount),
                        interiorDoodad = Dequeues(state.InteriorDoodad, state.InteriorDoodadCount),
                        foliage = Dequeues(state.Foliage, state.FoliageCount),
                    },
                    packetsPumpedDuringLoad = state.PacketsPumpedDuringLoad,
                    unitsKnownAtClear = state.UnitsKnownAtClear,
                    timeToFirstCreatureDrawMs = firstDrawMs,
                    firstCreatureDrawTimedOut = firstDrawTimedOut,
                    mpqArchiveOpensDuringLoad = Math.Max(0,
                        AdtTerrainReader.FallbackArchiveOpens - state.FallbackArchiveOpensAtStart),
                    creatureLifecycle,
                    outgoingProtocol,
                    wireTimeline = wireTimeline.Select(packet => new
                    {
                        timeSeconds = packet.Time,
                        direction = packet.Outgoing ? "out" : "in",
                        opcode = packet.Opcode,
                        name = packet.OpcodeName,
                        size = packet.Size,
                    }).ToArray(),
                    maxFrameMsDuringCurtain = state.MaxFrameMsDuringCurtain,
                    frames = FrameObjects(),
                    thread = new
                    {
                        cycles = state.ThreadCycles,
                        mCyclesPerMs = clearMs > 0 ? state.ThreadCycles / 1_000_000.0 / clearMs : 0,
                    },
                    gc = new
                    {
                        pauseMs = state.GcPauseMs,
                        gen0 = state.Gen0,
                        gen1 = state.Gen1,
                        gen2 = state.Gen2,
                        allocatedBytes = state.AllocatedBytes,
                        allocatedMb = state.AllocatedBytes / (1024.0 * 1024.0),
                    },
                    events = events.Select(e => e.Text).ToArray(),
                };

                string path = Path.Combine(dir, state.Name + ".json");
                File.WriteAllText(path, JsonSerializer.Serialize(payload, DumpJson));
                Console.WriteLine($"[load] timeline wrote {path}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[load] timeline write failed - {ex.Message}");
            }
        });
    }
}
