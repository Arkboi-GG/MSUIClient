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
                guid = _creatorNextSpawnGuid++;
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
                // Run/walk/fly animation comes from a stub spline: the renderer
                // reads Spline.AverageSpeed (and Flying) to pick the clip, and in
                // creator mode nothing else ever samples or clears it - so it is
                // a pure per-frame "this body is moving this fast" signal. A jump
                // over 20 yd is a scrub teleport, not motion; no run flicker.
                Vector3 previous = _encounterPuppetPrev.GetValueOrDefault(actor.Key, actor.Position);
                float travelled = Vector3.Distance(previous, actor.Position);
                entity.Spline = dt > 0f && travelled > 0.005f && travelled < 20f
                    ? new CreatureSpline([previous, actor.Position],
                        (uint)MathF.Max(dt * 1000f, 1f), actor.Flying,
                        MovementInfo.ClientUptimeMs())
                    : null;
                _encounterPuppetPrev[actor.Key] = actor.Position;

                entity.Position = actor.Position;
                entity.Orientation = actor.Facing;
            }
        }

        // Dead, removed from the scenario, or stripped of a display: the model goes.
        foreach (string key in _encounterPuppets.Keys.ToArray())
        {
            if (alive.Contains(key)) continue;
            _entities.RemoveSynthetic(_encounterPuppets[key]);
            _encounterPuppets.Remove(key);
            _encounterPuppetPrev.Remove(key);
        }
    }

    private void ClearEncounterPuppets()
    {
        if (_encounterPuppets.Count == 0) return;
        foreach (ulong guid in _encounterPuppets.Values) _entities.RemoveSynthetic(guid);
        _encounterPuppets.Clear();
        _encounterPuppetPrev.Clear();
    }
}
