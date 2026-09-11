using System.Diagnostics;
using System.Numerics;
using ImGuiNET;
using MSUIClient.World.Units;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// Creator effect clock (shared_docs/SPELL_CREATOR_IDE.md §2.2)
//
// The whole spell presentation stack runs on ONE clock. Outside the creator
// world it is a pass-through (uptime for `now`, the frame dt for the particle
// simulator) - client mode is byte-for-byte what it was. In the creator world
// the clock can pause, run slow, step a frame and scrub.
//
// WHY A REPLAY, NOT A FREEZE
//   Freezing is easy (delta 0). Editing WHILE frozen is the point: a knob turn
//   rebuilds the model bytes, and the held picture must re-lay itself with the
//   change and NOTHING else changed. Pool seeds derive from the pool key, so a
//   re-simulation from the earliest live instance's start to the paused time
//   with fixed steps reproduces the same random draws. Pausing performs one
//   replay too, so the paused picture is already the deterministic one before
//   the first edit lands.
//
// KNOWN LIMITS (stated, not hidden)
//   The caster pose used during replay is the pose at the paused instant: the
//   effect M2's own bones are re-walked, a bone sweep on the BODY is not. A
//   missile's trail replays from its paused position, not its flight path.
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    /// <summary>The creator-owned tactical-freeze lock: the spawned targets ride the same
    /// per-guid animation latch the real freeze uses, so an impact victim's flinch holds.</summary>
    private const ulong CreatorPauseLockId = 0xC0DE_0000_0000_0001UL;
    private const float CreatorClockStepSeconds = 1f / 30f;
    private const float CreatorReplayStepSeconds = 1f / 60f;
    private const double CreatorReplayMaxSpanSeconds = 12.0;

    private bool _creatorClockStarted;
    private bool _creatorClockPaused;
    private float _creatorClockSpeed = 1f;
    private double _creatorClockNow;
    private float _creatorClockDelta;
    private bool _creatorPauseLockApplied;
    private int _creatorPauseLockCount = -1;
    private double _creatorReplayMilliseconds;
    private int _creatorReplaySteps;

    private bool CreatorClockEngaged => _creatorWorldRequested;

    /// <summary>The spell stack's `now`: the creator clock in the creator world, uptime otherwise.
    /// Every spawn, tick and render read on the spell path goes through this one property.</summary>
    internal double SpellClockNow =>
        CreatorClockEngaged && _creatorClockStarted ? _creatorClockNow : NowSeconds();

    internal bool CreatorSpellPaused => CreatorClockEngaged && _creatorClockPaused;

    /// <summary>The particle simulator's step for this frame. Consumed once per render so a
    /// render without an update in between cannot double-advance the clock.</summary>
    private float TakeSpellClockDelta(float frameDt)
    {
        if (!CreatorClockEngaged) return frameDt;
        float delta = _creatorClockDelta;
        _creatorClockDelta = 0f;
        return delta;
    }

    /// <summary>Once per Update, before the spell presentation tick.</summary>
    private void AdvanceCreatorClock(float dt)
    {
        if (!CreatorClockEngaged)
        {
            _creatorClockDelta = 0f;
            return;
        }
        if (!_creatorClockStarted)
        {
            // Start from uptime so nothing spawned before the world came up jumps in age.
            _creatorClockNow = NowSeconds();
            _creatorClockStarted = true;
        }
        if (_creatorClockPaused)
        {
            _creatorClockDelta = 0f;
            ApplyCreatorPauseLock();
            return;
        }
        float step = MathF.Max(0f, dt) * Math.Clamp(_creatorClockSpeed, 0.05f, 1f);
        _creatorClockNow += step;
        _creatorClockDelta = step;
    }

    private void SetCreatorClockPaused(bool paused)
    {
        if (!CreatorClockEngaged || paused == _creatorClockPaused) return;
        _creatorClockPaused = paused;
        _creatorClockDelta = 0f;
        ApplyCreatorPauseLock();
        if (paused)
        {
            StopAllCreatorAudio();
            ReplayCreatorEffects();
        }
    }

    /// <summary>Hold (or release) the spawned targets through the tactical-freeze pose law.
    /// The local body is held through UnitState.FreezePose instead, so the freeze UI's
    /// "this body is frozen" ladder never fires for a creator pause.</summary>
    private void ApplyCreatorPauseLock()
    {
        bool want = CreatorSpellPaused;
        int count = want ? _creatorSpawns.Count : 0;
        if (want == _creatorPauseLockApplied && count == _creatorPauseLockCount) return;
        TacticalFreezePoseLaw.ApplyLockSnapshot(CreatorPauseLockId, want,
            want ? _creatorSpawns.Select(s => s.Guid).ToArray() : Array.Empty<ulong>());
        _creatorPauseLockApplied = want;
        _creatorPauseLockCount = count;
    }

    /// <summary>The earliest live instance start the replay walks from, capped at a span.</summary>
    private double CreatorReplayFloor(double now)
    {
        double from = now;
        if (_spellEffects is not null)
            foreach (var live in _spellEffects.LiveInstances())
            {
                double start = live.Missile ? (live.Launched ? live.LaunchedAt : now) : live.Started;
                if (start < from) from = start;
            }
        return Math.Max(from, now - CreatorReplayMaxSpanSeconds);
    }

    /// <summary>The latest authored end among the live instances (holds are open-ended).</summary>
    private double CreatorReplayCeiling(double now)
    {
        double to = now;
        if (_spellEffects is not null)
            foreach (var live in _spellEffects.LiveInstances())
                if (!double.IsInfinity(live.Ends) && live.Ends > to) to = live.Ends;
        return Math.Min(to, now + CreatorReplayMaxSpanSeconds);
    }

    /// <summary>Clear the particle pools and re-simulate every live instance from the replay
    /// floor to the paused time with fixed steps. Same key-derived seeds, same steps, same
    /// picture - except for whatever was edited.</summary>
    private void ReplayCreatorEffects()
    {
        if (!CreatorSpellPaused || _spellParticles is null || _spellEffects is null) return;
        long started = Stopwatch.GetTimestamp();
        double now = _creatorClockNow;
        double from = CreatorReplayFloor(now);
        Vector3 eye = _window.Camera.Position;
        Vector3 forward = _window.Camera.Forward;

        _spellParticles.ResetPools();
        int steps = 0;
        double t = from;
        while (t < now)
        {
            float step = (float)Math.Min(CreatorReplayStepSeconds, now - t);
            t += step;
            _spellParticles.Simulate(step, eye, _spellEffects.EmitterInstances(
                t, SpellEffectUnitPose, _spellFxBillboardJointPoseB, eye, forward),
                SpellParticleGroundHeight);
            steps++;
        }
        // A zero-length replay still touches the pools so the gizmos have frames to draw.
        if (steps == 0)
            _spellParticles.Simulate(0f, eye, _spellEffects.EmitterInstances(
                now, SpellEffectUnitPose, _spellFxBillboardJointPoseB, eye, forward),
                SpellParticleGroundHeight);
        _creatorReplaySteps = steps;
        _creatorReplayMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    /// <summary>Scrub: move the paused clock and replay to it. Clamped to the live span.</summary>
    private void SetCreatorClockTime(double target)
    {
        if (!CreatorSpellPaused) return;
        double floor = CreatorReplayFloor(_creatorClockNow);
        double ceiling = CreatorReplayCeiling(_creatorClockNow);
        _creatorClockNow = Math.Clamp(target, floor, Math.Max(floor, ceiling));
        ReplayCreatorEffects();
    }

    private void StepCreatorClock(float seconds) => SetCreatorClockTime(_creatorClockNow + seconds);

    // ── panel section ────────────────────────────────────────────────────────

    /// <summary>Step buttons, the scrub slider and the time readout on ONE line - shared by
    /// the Clock section and the IDE's paused timeline strip.</summary>
    private void DrawCreatorScrub(float sliderWidth)
    {
        float cs = CreatorUiScale;
        if (CreatorButton("< 1/30 s", 76f * cs)) StepCreatorClock(-CreatorClockStepSeconds);
        ImGui.SameLine();
        if (CreatorButton("1/30 s >", 76f * cs)) StepCreatorClock(CreatorClockStepSeconds);
        ImGui.SameLine();
        double floor = CreatorReplayFloor(_creatorClockNow);
        double ceiling = Math.Max(floor, CreatorReplayCeiling(_creatorClockNow));
        float span = (float)Math.Max(ceiling - floor, 0.001);
        float at = (float)(_creatorClockNow - floor);
        ImGui.SetNextItemWidth(MathF.Max(sliderWidth, 60f * cs));
        if (ImGui.SliderFloat("##scrub", ref at, 0f, span, "%.3f s"))
            SetCreatorClockTime(floor + at);
        ImGui.SameLine();
        ImGui.TextDisabled($"t = {at:0.000} / {span:0.00} s");
    }

    private void DrawCreatorClockBody()
    {
        float cs = CreatorUiScale;
        if (!CreatorClockEngaged)
        {
            ImGui.TextDisabled("The effect clock runs once the creator world is up.");
            return;
        }

        bool paused = _creatorClockPaused;
        if (CreatorButton(paused ? "Resume" : "Pause", 80f * cs)) SetCreatorClockPaused(!paused);
        CreatorHelp("Pause the WHOLE spell presentation: particles, effect meshes and ribbons, " +
            "the caster's body and the spawned targets all hold their exact frame. Every knob " +
            "keeps working while paused - the held picture re-lays itself with the change and " +
            "nothing else changed (a deterministic replay from the phase start). Resume picks up " +
            "where it stopped.");

        if (paused)
        {
            ImGui.SameLine();
            DrawCreatorScrub(CreatorControlWidth);
            CreatorHelp("Move through the live effect: 0 is the earliest live phase's start, the " +
                "far end its authored end (holds are open-ended, so they scrub over a 12 s window). " +
                "Every position is a fresh replay, so scrubbing backward is exact.");
            ImGui.TextDisabled($"(replay {_creatorReplaySteps} steps, {_creatorReplayMilliseconds:0.0} ms)");
            ImGui.TextDisabled("Limits: the body holds the paused pose during replay; a missile's " +
                               "trail replays from where the missile is now.");
        }
        else
        {
            ImGui.SetNextItemWidth(CreatorControlWidth);
            ImGui.SliderFloat("Speed", ref _creatorClockSpeed, 0.1f, 1f, "%.2fx");
            if (CreatorResetKnob("clockspeed")) _creatorClockSpeed = 1f;
            CreatorHelp("Slow motion for the whole spell stack - the cheap first look at a burst " +
                "that is over in a quarter of a second. 1x is real time.");
        }

        if (_spellEffects is null) return;
        ImGui.Spacing();
        ImGui.TextDisabled("LIVE PHASES");
        int shown = 0;
        foreach (var live in _spellEffects.LiveInstances())
        {
            if (shown++ >= 8) { ImGui.TextDisabled("..."); break; }
            double age = _creatorClockNow - (live.Missile ? live.LaunchedAt : live.Started);
            string end = double.IsInfinity(live.Ends)
                ? "hold"
                : $"ends in {live.Ends - _creatorClockNow:0.00} s";
            ImGui.TextDisabled($"{live.Stage,-8} {Path.GetFileName(live.Path)}   age {age:0.00} s   {end}");
        }
        if (shown == 0) ImGui.TextDisabled("(nothing playing - Loop a phase first)");
    }
}
