using System.Numerics;
using ImGuiNET;
using Silk.NET.Input;
using MSUIClient.Engine;
using MSUIClient.Net;
using MSUIClient.World.Encounters;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// Encounter Lab ⇄ RTS free view: command the simulated raid like any RTS unit.
//
// The puppets are real synthetic world entities, so the free view's existing
// pick and marquee already SEE them — this file is the routing layer that turns
// a selection of raid puppets into SIM orders instead of server orders. The
// verbs, all anchored at the scrub head:
//
//   RightClick ground          run there (the "can he make it" walk, travel paid)
//   Shift+RightClick           chain the next leg onto the last order
//   Ctrl+RightClick            teleport there — the paused what-if: "if he stood
//                              HERE at this exact moment", the fight reflowing
//                              around the answer
//   Alt+RightClick             arrival facing: "…and then face this way" (the
//                              tank's back to the wall)
//
// Server bots keep their own path untouched: a selection with ANY raid puppet
// in it belongs to the sim, and the order never reaches SuiOrder.
// ─────────────────────────────────────────────────────────────────────────────

public sealed partial class GameLoop
{
    /// <summary>Chain memory: the keys the last shift-order addressed. A shift
    /// order to the SAME set appends AfterPrevious legs; any other order, or a
    /// selection change, starts a fresh chain.</summary>
    private readonly List<string> _encounterOrderChainKeys = [];

    /// <summary>Scenario key of a friendly raid puppet, or null when the guid is
    /// no puppet (or is the boss — she is inspected, never commanded).</summary>
    private string? EncounterRaidPuppetKey(ulong guid)
    {
        if (!_encounterLabOpen || guid == 0) return null;
        foreach ((string key, ulong puppetGuid) in _encounterPuppets)
        {
            if (puppetGuid != guid) continue;
            foreach (EncounterActorSpec actor in _encounterScenario)
                if (actor.Key == key)
                    return actor.Role == EncounterActorRole.Friendly ? key : null;
            return null;
        }
        return null;
    }

    /// <summary>Free-view left click on a raid puppet: select it for orders. No
    /// take-command, no camera move — a sim body has no bars to drive.</summary>
    private bool HandleEncounterPuppetSelect(ulong pickedUnit)
    {
        if (EncounterRaidPuppetKey(pickedUnit) is not { } key) return false;
        _freecamSelection.Clear();
        _freecamSelection.Add(pickedUnit);
        string name = _encounterScenario.FirstOrDefault(a => a.Key == key)?.Name ?? key;
        AddChatMessage(EncounterStagingActive
            ? $"{name} (sim): Shift+Click stages waypoints · GO runs the plan · " +
              "Ctrl+RClick teleports · Alt+RClick faces."
            : $"{name} (sim): RClick run @ {_encounterViewMs / 1000f:0.0}s · " +
              "Shift chains · Ctrl teleports (what-if) · Alt faces.");
        return true;
    }

    /// <summary>Marquee pass over the raid puppets — the box picks up sim bodies
    /// exactly like bots. Called from CommitMarqueeSelection.</summary>
    private void AddEncounterPuppetsToMarquee(Vector2 min, Vector2 max, Vector2 display)
    {
        if (!_encounterLabOpen) return;
        foreach ((string key, ulong guid) in _encounterPuppets)
        {
            if (EncounterRaidPuppetKey(guid) is null) continue;
            if (!_entities.TryGet(guid, out WorldEntity unit)) continue;
            if (!_window.Camera.TryWorldToScreen(unit.Position, display, out Vector2 screen)) continue;
            if (screen.X >= min.X && screen.X <= max.X &&
                screen.Y >= min.Y && screen.Y <= max.Y &&
                !_freecamSelection.Contains(guid))
                _freecamSelection.Add(guid);
        }
    }

    /// <summary>
    /// Free-view right click while raid puppets are selected: the order goes to
    /// the SIM. Returns true when consumed. A mixed selection belongs to the sim
    /// too — one gesture must never fan out into two different worlds.
    /// </summary>
    private bool HandleEncounterRtsOrder(WorldMouseClick click)
    {
        if (!_encounterLabOpen) return false;

        List<string> keys = [];
        foreach (ulong guid in _freecamSelection)
            if (EncounterRaidPuppetKey(guid) is { } key && !keys.Contains(key))
                keys.Add(key);
        if (keys.Count == 0) return false;

        if (!TryPickGround(click.Position, out Vector3 point)) return true;
        int atMs = _encounterViewMs;

        // Alt: no movement at all — the last order (staged leg first, then real
        // moves, then the spawn pose) turns to face the clicked point. "Stand
        // there, back to the wall" is two clicks.
        if (click.AltDown)
        {
            foreach (string key in keys)
            {
                if (_encounterStagedOrders.TryGetValue(key, out var stagedLegs) &&
                    stagedLegs.Count > 0)
                {
                    Vector3 last = stagedLegs[^1].Position;
                    stagedLegs[^1] = stagedLegs[^1] with
                    { ArrivalFacing = EncounterGeometryLaw.Facing(last, point) };
                    continue;
                }
                if (_encounterScenario.FirstOrDefault(a => a.Key == key) is not { } actor)
                    continue;
                Vector3 from = actor.Moves is { Count: > 0 } moves
                    ? moves[^1].Position : actor.Position;
                FaceScenarioActor(key, EncounterGeometryLaw.Facing(from, point));
            }
            RebuildEncounterSimKeepingView();
            AddChatMessage($"{OrderKeysLabel(keys)}: facing set.");
            return true;
        }

        bool teleport = click.CtrlDown;

        // Pre-pull authoring: every non-teleport click QUEUES. Nothing moves
        // until GO — set the whole raid's waypoints, read the dotted plan,
        // then send everyone at once. Repeated clicks append legs.
        if (!teleport && EncounterStagingActive)
        {
            for (int i = 0; i < keys.Count; i++)
            {
                Vector3 target = point;
                if (i > 0)
                {
                    float angle = (i - 1) * (MathF.Tau / 8f);
                    target += new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0f) * 2.5f;
                }
                StageEncounterLeg(keys[i], target);
            }
            _rtsMoveMarkers.Add((point, NowSeconds(), RtsNeutralTint));
            AddChatMessage($"{OrderKeysLabel(keys)}: waypoint staged " +
                           $"({EncounterStagedCount} total) — GO or Play runs the plan.");
            return true;
        }

        bool chain = click.ShiftDown && !teleport &&
                     _encounterOrderChainKeys.SequenceEqual(keys);

        // A multi-body order fans around the click so the raid does not stack on
        // one dot: first body on the point, the rest on a tight ring.
        for (int i = 0; i < keys.Count; i++)
        {
            Vector3 target = point;
            if (i > 0)
            {
                float angle = (i - 1) * (MathF.Tau / 8f);
                target += new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0f) * 2.5f;
            }
            AppendScenarioMove(keys[i], atMs, target,
                anchor: chain ? MoveAnchor.AfterPrevious : MoveAnchor.AtTime,
                teleport: teleport);
        }
        RebuildEncounterSimKeepingView();

        _encounterOrderChainKeys.Clear();
        if (click.ShiftDown && !teleport) _encounterOrderChainKeys.AddRange(keys);

        _rtsMoveMarkers.Add((point, NowSeconds(),
            teleport ? RtsHostileTint : click.ShiftDown ? RtsNeutralTint : RtsFriendlyTint));
        AddChatMessage(teleport
            ? $"{OrderKeysLabel(keys)}: what-if reposition @ {atMs / 1000f:0.0}s — fight reflowed."
            : chain
                ? $"{OrderKeysLabel(keys)}: chained next leg."
                : $"{OrderKeysLabel(keys)}: run ordered @ {atMs / 1000f:0.0}s.");
        return true;
    }

    private string OrderKeysLabel(List<string> keys)
    {
        if (keys.Count == 1)
            return _encounterScenario.FirstOrDefault(a => a.Key == keys[0])?.Name ?? keys[0];
        return $"{keys.Count} raid bodies";
    }

    // ── Waypoint orientation: grab a dot, move to spin freely, click to set ─────
    //
    // The facing already rides each leg (EncounterStagedLeg.ArrivalFacing and
    // TimedMove.ArrivalFacing). The owner spins it FREELY (continuous radians, no
    // steps): Shift+Right-click a waypoint dot to GRAB its orientation, then move
    // the mouse — the arrow tracks the cursor live to any angle — and click again to
    // SET it. Right-click, not left (the left button is owned by selection/marquee);
    // the button is not HELD during the spin, so camera mouselook (which owns
    // right-DRAG) never engages. On empty ground Shift+Right stays an order.

    /// <summary>Screen-space grab radius for a waypoint dot, in pixels.</summary>
    private const float EncounterWaypointGrabPixels = 22f;

    // A live spin session: the grabbed waypoint and the angle the cursor is dictating.
    private bool _encounterOrientSpinning;
    private string? _encounterOrientKey;
    private int _encounterOrientLeg = -1;
    private bool _encounterOrientStaged;
    private Vector3 _encounterOrientAnchor;
    private float _encounterOrientFacing = float.NaN;

    /// <summary>Free-view Shift+Right-click on a waypoint dot: GRAB it for a free spin (the
    /// arrow then follows the cursor until the next click sets it — see HandleFreeCamWorldClick,
    /// which ends a live spin on any click). Returns false (fall through to the normal order)
    /// when Shift+Right lands on empty ground.</summary>
    private bool HandleEncounterWaypointOrient(WorldMouseClick click)
    {
        if (!_encounterLabOpen || !_freeView) return false;
        if (click.Button != MouseButton.Right || !click.ShiftDown ||
            click.CtrlDown || click.AltDown) return false;
        if (!TryPickEncounterWaypoint(click.Position, out string key, out int leg, out bool staged))
            return false;

        _encounterOrientSpinning = true;
        _encounterOrientKey = key;
        _encounterOrientLeg = leg;
        _encounterOrientStaged = staged;
        _encounterOrientAnchor = WaypointWorld(key, leg, staged);
        float current = GetWaypointFacing(key, leg, staged);
        _encounterOrientFacing = float.IsNaN(current) ? DefaultWaypointFacing(key, leg, staged) : current;
        AddChatMessage($"{EncounterActorName(key)}: spinning waypoint {leg + 1} — " +
                       "move the mouse to aim, click to set.");
        return true;
    }

    /// <summary>Per-frame while a spin is live: the arrow tracks the cursor's ground point, so
    /// the facing is free and continuous. Staged legs (Lab-side, cheap) update in place so the
    /// ring tracks live; a committed move rides the preview arrow and is written once on commit,
    /// dodging a per-frame sim rebuild. Called every frame from UpdateEncounterLab.</summary>
    private void UpdateEncounterOrientSpin()
    {
        if (!_encounterOrientSpinning) return;
        if (!_encounterLabOpen || !_freeView) { EndEncounterOrientSpin(commit: true); return; }
        if (ImGui.GetIO().WantCaptureMouse) return;   // pointer over the panel: hold the angle
        if (_encounterOrientKey is not { } key) return;
        if (!TryPickGround(_window.MousePosition, out Vector3 ground)) return;

        float facing = EncounterGeometryLaw.Facing(_encounterOrientAnchor, ground);
        if (float.IsNaN(facing)) return;
        _encounterOrientFacing = facing;
        if (_encounterOrientStaged)
            SetWaypointFacing(key, _encounterOrientLeg, staged: true, facing);
    }

    /// <summary>End a spin. On commit the final angle is written (a committed move rebuilds the
    /// sim once here); either way the session clears.</summary>
    private void EndEncounterOrientSpin(bool commit)
    {
        if (_encounterOrientSpinning && commit && _encounterOrientKey is { } key &&
            !float.IsNaN(_encounterOrientFacing))
        {
            SetWaypointFacing(key, _encounterOrientLeg, _encounterOrientStaged, _encounterOrientFacing);
            AddChatMessage($"{EncounterActorName(key)}: waypoint {_encounterOrientLeg + 1} " +
                           $"set to {EncounterFacingLabel(_encounterOrientFacing)}.");
        }
        _encounterOrientSpinning = false;
        _encounterOrientKey = null;
        _encounterOrientLeg = -1;
        _encounterOrientFacing = float.NaN;
    }

    private Vector3 WaypointWorld(string key, int leg, bool staged)
    {
        if (staged)
            return _encounterStagedOrders.TryGetValue(key, out List<EncounterStagedLeg>? legs) &&
                   leg >= 0 && leg < legs.Count ? legs[leg].Position : Vector3.Zero;
        return _encounterScenario.FirstOrDefault(a => a.Key == key)?.Moves is { } moves &&
               leg >= 0 && leg < moves.Count ? moves[leg].Position : Vector3.Zero;
    }

    /// <summary>Nearest on-screen waypoint dot within the grab radius — staged legs first (the
    /// active pre-GO plan), then committed non-teleport moves.</summary>
    private bool TryPickEncounterWaypoint(Vector2 mouse, out string key, out int leg, out bool staged)
    {
        key = "";
        leg = -1;
        staged = false;
        Vector2 display = ImGui.GetIO().DisplaySize;
        if (display.X < 1f || display.Y < 1f) return false;

        float bestSq = EncounterWaypointGrabPixels * EncounterWaypointGrabPixels;
        string? bestKey = null;
        int bestLeg = -1;
        bool bestStaged = false;

        void Consider(string k, int i, bool st, Vector3 world)
        {
            if (!_window.Camera.TryWorldToScreen(world, display, out Vector2 px)) return;
            float dsq = (px - mouse).LengthSquared();
            if (dsq > bestSq) return;
            bestSq = dsq;
            bestKey = k;
            bestLeg = i;
            bestStaged = st;
        }

        foreach ((string k, List<EncounterStagedLeg> legs) in _encounterStagedOrders)
            for (int i = 0; i < legs.Count; i++)
                Consider(k, i, true, legs[i].Position);

        foreach (EncounterActorSpec actor in _encounterScenario)
        {
            if (actor.Role != EncounterActorRole.Friendly ||
                actor.Moves is not { Count: > 0 } moves) continue;
            for (int i = 0; i < moves.Count; i++)
                if (!moves[i].Teleport) Consider(actor.Key, i, false, moves[i].Position);
        }

        if (bestKey is null) return false;
        key = bestKey;
        leg = bestLeg;
        staged = bestStaged;
        return true;
    }

    private float GetWaypointFacing(string key, int leg, bool staged)
    {
        if (staged)
            return _encounterStagedOrders.TryGetValue(key, out List<EncounterStagedLeg>? legs) &&
                   leg >= 0 && leg < legs.Count
                ? legs[leg].ArrivalFacing : float.NaN;
        EncounterActorSpec? actor = _encounterScenario.FirstOrDefault(a => a.Key == key);
        return actor?.Moves is { } moves && leg >= 0 && leg < moves.Count
            ? moves[leg].ArrivalFacing : float.NaN;
    }

    private void SetWaypointFacing(string key, int leg, bool staged, float facing)
    {
        if (staged)
        {
            if (_encounterStagedOrders.TryGetValue(key, out List<EncounterStagedLeg>? legs) &&
                leg >= 0 && leg < legs.Count)
                legs[leg] = legs[leg] with { ArrivalFacing = facing };
            return;   // staged legs are Lab-side and drawn directly — no rebuild
        }
        SetScenarioMoveFacing(key, leg, facing);
        RebuildEncounterSimKeepingView();
    }

    /// <summary>The facing a freshly-oriented waypoint starts at: pointed at the boss's spawn,
    /// the raid's default, ready to spin from there.</summary>
    private float DefaultWaypointFacing(string key, int leg, bool staged)
    {
        Vector3 at = staged
            ? (_encounterStagedOrders.TryGetValue(key, out List<EncounterStagedLeg>? legs) &&
               leg < legs.Count ? legs[leg].Position : Vector3.Zero)
            : (_encounterScenario.FirstOrDefault(a => a.Key == key)?.Moves is { } moves &&
               leg < moves.Count ? moves[leg].Position : Vector3.Zero);
        EncounterActorSpec? boss = _encounterScenario.FirstOrDefault(a => a.Role == EncounterActorRole.Boss);
        return boss is { } b ? EncounterGeometryLaw.Facing(at, b.Position) : 0f;
    }

    private string EncounterActorName(string key) =>
        _encounterScenario.FirstOrDefault(a => a.Key == key)?.Name ?? key;

    /// <summary>Set the arrival facing of one committed move by index.</summary>
    private void SetScenarioMoveFacing(string key, int index, float facing)
    {
        for (int i = 0; i < _encounterScenario.Count; i++)
        {
            if (_encounterScenario[i].Key != key) continue;
            if (_encounterScenario[i].Moves is not { Count: > 0 } moves ||
                index < 0 || index >= moves.Count) return;
            var updated = moves.ToList();
            updated[index] = updated[index] with { ArrivalFacing = facing };
            _encounterScenario[i] = _encounterScenario[i] with { Moves = updated };
            return;
        }
    }

    private static string EncounterFacingLabel(float facing)
    {
        float deg = facing * (180f / MathF.PI) % 360f;
        if (deg < 0f) deg += 360f;
        return $"{deg:0}°";
    }
}
