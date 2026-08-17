using System.Numerics;
using MSUIClient.Net;
using MSUIClient.World.Encounters;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// Encounter Lab puppets: REAL RENDERED MODELS driven by the simulation.
//
// The overlays answer "where is everything and what lands where"; they answered
// it with labelled circles, and the first human to open the tool asked, at once,
// "where are the models?" - correctly. A fight staged by bodies should show
// bodies. Each scenario actor that carries a DisplayId gets a synthetic world
// entity (the same seam the creator's Target menu spawns through), and every
// frame its position and facing are copied from the sim state at the scrub head:
// scrub the bar and Onyxia flies her route; a body dies and its model leaves the
// world, the overlay's grey cross staying behind as the record.
//
// The sim NEVER reads these. Puppets are presentation, spawned and despawned
// wholesale; the snapshot ring stays the single authority on where anything is.
// ─────────────────────────────────────────────────────────────────────────────

public sealed partial class GameLoop
{
    private readonly Dictionary<string, ulong> _encounterPuppets = [];
    private readonly Dictionary<string, Vector3> _encounterPuppetPrev = [];
    private readonly Dictionary<string, Vector3> _encounterPuppetVel = [];

    /// <summary>Key → guid, held ACROSS rebuilds. Every scenario edit rebuilds the
    /// sim and respawns the puppets; without a stable guid per key, the free-view
    /// selection (a guid list) would go stale on the very edit it just ordered.</summary>
    private readonly Dictionary<string, ulong> _encounterPuppetGuidReserve = [];

    /// <summary>One held item on a puppet: the real UNIT_VIRTUAL_ITEM field
    /// contents, read from each look's creature_equip_template + item_template on
    /// the vmangos box (2026-08-17). The renderer attaches and sheathes from
    /// exactly these fields for server units; puppets speak the same language.</summary>
    private readonly record struct PuppetHeldItem(
        uint Display, byte ItemClass, byte Subclass, byte Material, byte InventoryType, byte Sheath);

    private static readonly Dictionary<uint, PuppetHeldItem[]> PuppetWeapons = new()
    {
        // Stormwind City Guard / Stormwind Guard: sword + shield.
        [3167] = [new(7483, 2, 7, 1, 13, 3), new(2080, 4, 6, 1, 14, 4)],
        [3258] = [new(7483, 2, 7, 1, 13, 3), new(2080, 4, 6, 1, 14, 4)],
        [3280] = [new(7491, 2, 8, 1, 13, 1)],    // Wu Shen's blade
        [1515] = [new(7439, 2, 4, 2, 13, 3)],    // Osric's mace
        [1985] = [new(7477, 2, 4, 2, 13, 3)],    // Dughan's mace
        [2968] = [new(21251, 2, 10, 2, 17, 2)],  // Malin's staff
        [3287] = [new(7480, 2, 6, 1, 17, 2)],    // Ilsa's polearm
        [3344] = [new(10654, 2, 10, 2, 17, 2)],  // Anetta's staff
        [1495] = [new(1926, 2, 10, 2, 17, 2)],   // Laurena's staff
    };

    private void SyncEncounterPuppets(float dt)
    {
        if (!_encounterLabOpen || !Settings.EncounterLab.ShowModels ||
            _encounterSim is not { } sim)
        {
            ClearEncounterPuppets();
            return;
        }

        HashSet<string> alive = [];
        foreach (SimActor actor in sim.Actors)
        {
            if (!actor.Alive || actor.Spec.DisplayId == 0) continue;
            alive.Add(actor.Key);

            if (!_encounterPuppets.TryGetValue(actor.Key, out ulong guid))
            {
                if (!_encounterPuppetGuidReserve.TryGetValue(actor.Key, out guid))
                    _encounterPuppetGuidReserve[actor.Key] = guid = _creatorNextSpawnGuid++;
                ObjectFields fields = ObjectFields.ForSyntheticUnit(
                    (int)actor.Spec.DisplayId, MathF.Max(actor.Spec.DisplayScale, 0.01f));
                if (PuppetWeapons.TryGetValue(actor.Spec.DisplayId, out PuppetHeldItem[]? held))
                {
                    for (int slot = 0; slot < held.Length && slot < 3; slot++)
                    {
                        PuppetHeldItem item = held[slot];
                        fields.SetU32((ushort)(ObjectFields.UNIT_VIRTUAL_ITEM_SLOT_DISPLAY + slot),
                            item.Display);
                        fields.SetU32((ushort)(ObjectFields.UNIT_VIRTUAL_ITEM_INFO + slot * 2),
                            item.ItemClass | (uint)item.Subclass << 8 |
                            (uint)item.Material << 16 | (uint)item.InventoryType << 24);
                        fields.SetU32((ushort)(ObjectFields.UNIT_VIRTUAL_ITEM_INFO + slot * 2 + 1),
                            item.Sheath);
                    }
                }
                _entities.AddSynthetic(new WorldEntity
                {
                    Guid = guid,
                    Type = ObjectTypeId.Unit,
                    Fields = fields,
                    Position = actor.Position,
                    Orientation = actor.Facing,
                });
                _encounterPuppets[actor.Key] = guid;
                _encounterPuppetPrev[actor.Key] = actor.Position;
            }

            if (_entities.TryGet(guid, out WorldEntity entity))
            {
                // The sim advances in fixed 100 ms steps; snapping the model to each
                // step rendered a 10 Hz slideshow. An exponential lerp hid the 0.25 yd
                // roam steps but NOT the 0.9 yd/step run of a chase: it decelerated into
                // each crumb then re-launched, pumping the run clip's rate at 10 Hz (the
                // "stutter walk"). A critically-damped follow (SmoothDamp) instead carries
                // VELOCITY between crumbs, so a steadily-advancing chase reads as steady
                // running and the reconstructed spline speed stays flat. A jump over 20 yd
                // is a scrub teleport and snaps; the stub spline stays the renderer's "this
                // body moves this fast" animation signal.
                Vector3 previous = _encounterPuppetPrev.GetValueOrDefault(actor.Key, actor.Position);
                Vector3 target = actor.Position;
                float jump = Vector3.Distance(previous, target);
                Vector3 rendered;
                if (jump >= 20f || dt <= 0f)
                {
                    rendered = target;
                    _encounterPuppetVel[actor.Key] = Vector3.Zero;   // teleport: kill momentum
                }
                else
                {
                    Vector3 velocity = _encounterPuppetVel.GetValueOrDefault(actor.Key);
                    rendered = SmoothDamp(previous, target, ref velocity, EncounterPuppetSmoothTime, dt);
                    _encounterPuppetVel[actor.Key] = velocity;
                }

                float travelled = Vector3.Distance(previous, rendered);
                entity.Spline = dt > 0f && travelled > 0.005f && jump < 20f
                    ? new CreatureSpline([previous, rendered],
                        (uint)MathF.Max(dt * 1000f, 1f), actor.Flying,
                        MovementInfo.ClientUptimeMs())
                    : null;
                _encounterPuppetPrev[actor.Key] = rendered;

                entity.Position = rendered;
                float facingEase = dt > 0f ? 1f - MathF.Exp(-dt * 10f) : 1f;
                entity.Orientation = jump >= 20f
                    ? actor.Facing
                    : SmoothFacing(entity.Orientation, actor.Facing, facingEase);
            }
        }

        // Dead, removed from the scenario, or stripped of a display: the model goes.
        foreach (string key in _encounterPuppets.Keys.ToArray())
        {
            if (alive.Contains(key)) continue;
            _entities.RemoveSynthetic(_encounterPuppets[key]);
            _encounterPuppets.Remove(key);
            _encounterPuppetPrev.Remove(key);
            _encounterPuppetVel.Remove(key);
        }
    }

    /// <summary>Follow time constant for the puppet SmoothDamp. Small enough that the model
    /// stays under its sim marker, large enough to iron the 10 Hz chase sawtooth flat.</summary>
    private const float EncounterPuppetSmoothTime = 0.13f;

    /// <summary>Critically-damped follow (Unity's SmoothDamp): eases position AND velocity
    /// toward a moving target, so a steadily-advancing chase produces steady rendered speed
    /// instead of the decelerate-then-relaunch pulse a plain lerp gives. Overshoot past the
    /// (current) target is clamped so a stationary target settles cleanly.</summary>
    private static Vector3 SmoothDamp(Vector3 current, Vector3 target,
        ref Vector3 velocity, float smoothTime, float dt)
    {
        smoothTime = MathF.Max(smoothTime, 1e-4f);
        float omega = 2f / smoothTime;
        float x = omega * dt;
        float exp = 1f / (1f + x + 0.48f * x * x + 0.235f * x * x * x);
        Vector3 change = current - target;
        Vector3 temp = (velocity + omega * change) * dt;
        velocity = (velocity - omega * temp) * exp;
        Vector3 output = target + (change + temp) * exp;
        if (Vector3.Dot(target - current, output - target) > 0f)   // overshot: settle on target
        {
            output = target;
            velocity = (output - target) / dt;
        }
        return output;
    }

    /// <summary>Shortest-arc facing blend, so a roam turn sweeps instead of
    /// twitching at the sim's step rate.</summary>
    private static float SmoothFacing(float from, float to, float t)
    {
        float delta = MathF.IEEERemainder(to - from, MathF.Tau);
        return from + delta * t;
    }

    private void ClearEncounterPuppets()
    {
        if (_encounterPuppets.Count == 0) return;
        foreach (ulong guid in _encounterPuppets.Values) _entities.RemoveSynthetic(guid);
        _encounterPuppets.Clear();
        _encounterPuppetPrev.Clear();
        _encounterPuppetVel.Clear();
    }
}
