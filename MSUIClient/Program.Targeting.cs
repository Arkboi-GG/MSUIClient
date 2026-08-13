using System.Numerics;
using ImGuiNET;
using Silk.NET.Input;
using MSUIClient.Engine;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

/// <summary>
/// Local target acquisition and the small server command state machine shared by
/// selection and auto-attack. GPU-skinned creatures use conservative vertical
/// cylinders; static world collision still wins when it is strictly nearer.
/// </summary>
public sealed partial class GameLoop
{
    private const uint NotSelectable = 1u << 25;
    private const uint AttackDisqualifiers =
        (1u << 1) | (1u << 7) | (1u << 16) | (1u << 20) | (1u << 25);
    private const float TargetPickDistance = 200f;

    private ulong _hoveredGuid;
    private ulong _selectionGuid;
    private ulong _attackTargetGuid;
    private long _targetCombatSeen;
    private readonly Dictionary<ulong, string> _playerNames = [];
    private readonly Dictionary<uint, string> _creatureNames = [];
    private readonly Dictionary<uint, CreatureQueryInfo?> _creatureQueryRecords = [];
    private readonly HashSet<ulong> _queriedPlayerNames = [];
    private readonly HashSet<uint> _queriedCreatureNames = [];

    private void ResetTargeting()
    {
        CloseInspect(playSound: false);
        _hoveredGuid = 0;
        _hoveredGameObjectGuid = 0;
        _selectionGuid = 0;
        _attackTargetGuid = 0;
        // Resolved hit and negative records are template identities and survive zoning/session
        // teardown. Only an unanswered writer ask must become re-askable.
        _queriedCreatureNames.Clear();
        _targetCombatSeen = _combat.AttackRevision;
        _window.ClearWorldClicks();
    }

    private bool TryBeginCreatureQuery(uint entry) =>
        entry != 0 && !_creatureQueryRecords.ContainsKey(entry) &&
        _queriedCreatureNames.Add(entry);

    private void UpdateTargeting()
    {
        // Creator mode targets its locally spawned practice dummies with no net at all.
        if (_net is not { IsInWorld: true } && !_creatorWorldRequested) return;

        // Reconcile the speculative local attack latch with the authoritative echo.
        if (_net is not null && _combat.AttackRevision != _targetCombatSeen)
        {
            _targetCombatSeen = _combat.AttackRevision;
            ulong previousAttack = _attackTargetGuid;
            _attackTargetGuid = _combat.TryGetAttackTarget(ControlledGuid, out ulong victim)
                ? victim
                : 0;
            if (previousAttack != _attackTargetGuid)
                ObserveCombatIntent(_attackTargetGuid != 0,
                    _attackTargetGuid != 0 ? _attackTargetGuid : previousAttack,
                    _attackTargetGuid != 0 ? "server-start" : _lastCombatStopCause);
        }

        // A dead target STAYS selected (the 1.12 client keeps the corpse in the target frame,
        // which is what the frame's "DEAD" line and corpse looting both rely on). Only a
        // despawn clears the selection.
        if (_selectionGuid != 0 && !_entities.TryGet(_selectionGuid, out _))
            CommitSelection(0, beginAttack: false);

        if (!_window.MouseCaptured && !ImGui.GetIO().WantCaptureMouse && !_settingsOpen)
        {
            _hoveredGuid = PickUnit(_window.MousePosition, out float unitHit);
            // Vanilla nearest-wins: a gameobject hovers only when its hit is
            // strictly nearer than any unit hit, and then it owns the hover -
            // the two hovers are exclusive by construction. Drives the doodad
            // highlight tint and the world-GO name tooltip.
            _hoveredGameObjectGuid = PickGameObject(_window.MousePosition, unitHit, out _);
            if (_hoveredGameObjectGuid != 0) _hoveredGuid = 0;
        }

        // Armed ground AoE: track the terrain point under the cursor every frame so the
        // render pass can draw the 1.12 targeting rune circle there in realtime.
        _groundCursorPoint = _groundCastSpell != 0 && !_window.MouseCaptured &&
            !ImGui.GetIO().WantCaptureMouse && TryPickGround(_window.MousePosition, out Vector3 aim)
            ? aim : null;

        // Targeting-cursor mode (armed ground AoE): a world left-click binds the terrain
        // point under the cursor and commits the cast; a right-click cancels. Matches the
        // 1.12 SpellIsTargeting machine — while armed, clicks never select or attack.
        // (The "Select target area" cursor hint is drawn from the action-bar ImGui pass —
        // this method runs in the update phase, where touching ImGui draw lists crashes.)
        while (_window.TryDequeueWorldClick(out WorldMouseClick click))
        {
            if (_settingsOpen || ImGui.GetIO().WantCaptureMouse) continue;
            // NPC dev window: an armed edit mode (waypoint drawing / spawn move) owns
            // every world click, ahead of the free-view router - no stray RTS orders
            // while placing path nodes. No-op unless a mode is armed.
            if (HandleDevEditClick(click)) continue;
            // CRPG free view: clicks are selection + RTS orders, never target/attack/loot.
            // Keyed on the CAMERA, not the control state — commanding a toon from the sky
            // is still the sky, and its clicks are still orders.
            if (_freeView)
            {
                HandleFreeCamWorldClick(click);
                continue;
            }
            if (_groundCastSpell != 0)
            {
                uint armed = _groundCastSpell;
                _groundCastSpell = 0;
                if (click.Button == MouseButton.Left && TryPickGround(click.Position, out Vector3 spot))
                    CommitGroundCast(armed, spot);
                continue;
            }
            ulong picked = PickUnit(click.Position, out float pickedUnitHit);
            // A gameobject strictly in front of any unit owns a right-click:
            // vanilla routes it to the object's interaction (mailbox opens
            // mail, chest sends CMSG_GAMEOBJ_USE), never to selection.
            // UseGameObject already gates range, type and world-state.
            ulong goClicked = click.Button == MouseButton.Right
                ? PickGameObject(click.Position, pickedUnitHit, out _)
                : 0;
            if (goClicked != 0)
            {
                UseGameObject(goClicked);
                continue;
            }
            if (click.Button == MouseButton.Left)
            {
                // NPC dev window focus set: Ctrl+LeftClick multi-selects for the
                // "Selected only" overlay scope and consumes the click.
                if (HandleDevFocusClick(picked)) continue;
                CommitSelection(picked, beginAttack: false); // empty left clears
            }
            else if (click.Button == MouseButton.Right && picked != 0)
            {
                // Right-click routes by classification (benilla target/click.rs): a dead unit
                // carrying UNIT_DYNFLAG_LOOTABLE opens its loot; other corpses just select;
                // live hostiles begin the swing.
                if (_entities.TryGet(picked, out WorldEntity corpse) && corpse.IsDead)
                {
                    CommitSelection(picked, beginAttack: false);
                    if (corpse.IsCreature && corpse.Fields.Lootable) RequestLoot(picked);
                }
                else if (_entities.TryGet(picked, out WorldEntity npc) &&
                         npc.IsCreature && (npc.NpcFlags & GossipNpcFlags) != 0)
                {
                    CommitSelection(picked, beginAttack: false);
                    RequestGossip(picked);
                }
                else if (_entities.TryGet(picked, out WorldEntity player) && player.IsPlayer)
                {
                    CommitSelection(picked, beginAttack: false);
                    if (UnitFrameMenuWhich(player) is { } which)
                        OpenUnitPopup(picked, which, click.Position, InspectBinding.Target);
                }
                else CommitSelection(picked, beginAttack: true); // empty right preserves
            }
        }

        if (_creatures is not null)
        {
            _creatures.HoveredGuid = _hoveredGuid;
            _creatures.SelectedGuid = _selectionGuid;
            // Free-view multi-selection wears the same target highlight as single targets;
            // while a marquee drag is live, the members it covers light up as a preview.
            _creatures.GroupSelectedGuids.Clear();
            foreach (ulong guid in _freecamSelection)
                _creatures.GroupSelectedGuids.Add(guid);
            AddMarqueePreview(_creatures.GroupSelectedGuids);
        }
        // The hovered gameobject brightens exactly like a hovered creature; the
        // doodad renderer applies the same 64/255 boost to that one dynamic
        // placement in both its opaque and blended passes.
        if (_doodads is not null)
            _doodads.HighlightedDynamicKey = _hoveredGameObjectGuid;
        UpdateInspectLifecycle();
    }

    private void DrawSelectionRing()
    {
        if (_selectionRing is null || _selectionGuid == 0 ||
            !_entities.TryGet(_selectionGuid, out WorldEntity target)) return;
        FactionReaction reaction = ReactionTargetTowardPlayer(target);
        Vector3 color = target.IsDead && !target.IsPlayer ? new Vector3(.498f)
            : target.IsPlayer ? new Vector3(.376f, .376f, 1f)
            : reaction switch
            {
                FactionReaction.Hostile => new Vector3(1, 0, 0),
                FactionReaction.Friendly => new Vector3(0, 1, 0),
                _ => new Vector3(1, 1, 0),
            };
        if (_attackTargetGuid == target.Guid)
        {
            float wave = MathF.Abs((MovementInfo.ClientUptimeMs() / 1000f % 1f) * 2f - 1f);
            color = new Vector3(1, .502f * wave, 0);
        }
        float radius = _creatures?.SelectionRadius(target) ?? .7f * MathF.Max(.01f, target.Scale);
        _selectionRing.Render(_window.Camera, target.Position, radius, color);
    }

    private void CommitSelection(ulong guid, bool beginAttack)
    {
        if (_net is null && !_creatorWorldRequested) return;

        bool wasAttacking = _attackTargetGuid != 0 ||
            (_net is not null && _combat.IsEngaged(ControlledGuid));
        bool changed = guid != _selectionGuid;
        if (changed) StopPetAttackForOldTargetChange(_selectionGuid, guid);
        if (changed && wasAttacking)
        {
            EmitCombat("TargetSwitch", "selection-change", guid,
                $"from=0x{_selectionGuid:X16} to=0x{guid:X16}");
            StopAttack("target-switch");
        }

        if (changed)
        {
            _selectionGuid = guid;
            _net?.SetSelection(guid);
            if (guid != 0 && _net is not null && _entities.TryGet(guid, out WorldEntity identity))
            {
                if (identity.IsPlayer && _queriedPlayerNames.Add(guid)) _net.NameQuery(guid);
                else if (identity.IsCreature && TryBeginCreatureQuery(identity.Entry))
                    _net.CreatureQuery(identity.Entry, guid);
            }
        }

        // A running swing follows a valid target switch. A clean right click
        // starts it when it was not already active. Never offline - the creator
        // dummy is scenery, not an opponent.
        if (_net is not null && guid != 0 && (beginAttack || (changed && wasAttacking)) &&
            _entities.TryGet(guid, out WorldEntity entity) && CanAttack(entity))
        {
            if (_attackTargetGuid != guid)
            {
                if (!ObserveAttackPrecondition(entity)) return;
                _net.AttackSwing(guid);
                _attackTargetGuid = guid; // speculative until SMSG_ATTACKSTART/STOP
                ObserveCombatIntent(true, guid, changed && wasAttacking ? "target-switch" : "user-start");
            }
        }
    }

    private bool TryClearTargetOnEscape()
    {
        if (_selectionGuid == 0) return false;
        CommitSelection(0, beginAttack: false);
        return true;
    }

    private void StopAttack(string cause = "user-cancel")
    {
        if (_net is null || (_attackTargetGuid == 0 && !_combat.IsEngaged(ControlledGuid))) return;
        _net.AttackStop();
        ObserveCombatIntent(false, _attackTargetGuid, cause);
        _attackTargetGuid = 0;
    }

    private bool CanAttack(WorldEntity target)
    {
        if (_net is null || target.Guid == ControlledGuid || target.IsDead ||
            (target.Fields.UnitFlags & AttackDisqualifiers) != 0)
            return false;

        // PvP/duel/group reaction is a later slice. Do not turn arbitrary nearby
        // players into hostile targets while that state is absent.
        if (target.IsPlayer) return false;

        return ReactionPlayerToward(target) != FactionReaction.Friendly;
    }

    private FactionReaction ReactionPlayerToward(WorldEntity target)
    {
        if (_net is null || _factions is null ||
            !_entities.TryGet(ControlledGuid, out WorldEntity player) ||
            !_factions.TryGet(player.Fields.FactionTemplate, out FactionTemplateRow own) ||
            !_factions.TryGet(target.Fields.FactionTemplate, out FactionTemplateRow other))
            return FactionReaction.Neutral;
        return own.ReactionToward(other);
    }

    private FactionReaction ReactionTargetTowardPlayer(WorldEntity target)
    {
        if (_net is null || _factions is null ||
            !_entities.TryGet(ControlledGuid, out WorldEntity player) ||
            !_factions.TryGet(target.Fields.FactionTemplate, out FactionTemplateRow other) ||
            !_factions.TryGet(player.Fields.FactionTemplate, out FactionTemplateRow own))
            return FactionReaction.Neutral;
        return other.ReactionToward(own);
    }

    /// <summary>Terrain point currently under the cursor while ground-targeting is armed
    /// (null when not armed or nothing pickable). Feeds the rune-circle marker draw.</summary>
    private Vector3? _groundCursorPoint;

    /// <summary>
    /// Resolve the terrain/world point under a window pixel for a ground-target cast.
    /// Prefers the collision mesh; falls back to marching the camera ray against the
    /// terrain heightfield and bisecting the crossing.
    /// </summary>
    private bool TryPickGround(Vector2 pixel, out Vector3 point)
    {
        point = default;
        var ray = _window.Camera.ScreenPointToRay(pixel, _window.FramebufferSize);
        if (ray is null) return false;
        (Vector3 origin, Vector3 direction) = ray.Value;
        const float maxDistance = 250f;
        if (_collision?.Raycast(origin, direction, maxDistance) is { } hit)
        {
            point = hit.Point;
            return true;
        }
        if (_terrain is null) return false;
        float previous = 0f;
        for (float t = 1f; t <= maxDistance; t += 1f)
        {
            Vector3 sample = origin + direction * t;
            if (_terrain.SampleHeight(sample.X, sample.Y) is float ground && sample.Z <= ground)
            {
                float lo = previous, hi = t;
                for (int i = 0; i < 16; i++)
                {
                    float mid = (lo + hi) * .5f;
                    Vector3 m = origin + direction * mid;
                    if (_terrain.SampleHeight(m.X, m.Y) is float g && m.Z <= g) hi = mid;
                    else lo = mid;
                }
                Vector3 found = origin + direction * hi;
                point = found with { Z = _terrain.SampleHeight(found.X, found.Y) ?? found.Z };
                return true;
            }
            previous = t;
        }
        return false;
    }

    private ulong PickUnit(Vector2 pixel) => PickUnit(pixel, out _);

    /// <summary>Same pick, plus how FAR the hit is — so the gameobject picker
    /// can lose to a unit in front of it. A nameplate rect hit reports 0 (UI
    /// always wins); no unit reports +infinity.</summary>
    private ulong PickUnit(Vector2 pixel, out float hitDistance)
    {
        hitDistance = float.PositiveInfinity;

        // Benilla vplates.rs:47-50,111-116: last frame's mouse-enabled plate rects feed
        // the shared hover/selection pick before the 3-D world ray.
        for (int i = _vplateHits.Count - 1; i >= 0; i--)
            if (_vplateHits[i].Rect.Contains(pixel))
            {
                hitDistance = 0f;
                return _vplateHits[i].Guid;
            }

        var ray = _window.Camera.ScreenPointToRay(pixel, _window.FramebufferSize);
        if (ray is null) return 0;

        (Vector3 origin, Vector3 direction) = ray.Value;
        float nearest = TargetPickDistance;
        ulong picked = 0;

        foreach (WorldEntity entity in _entities.Units)
        {
            // Corpses stay pickable - selecting and right-click looting a dead unit is a
            // 1.12 behavior, not an exception. Only NOT_SELECTABLE and the player skip.
            // The controlled-unit skip lifts in the FREE VIEW: the controller is a
            // detached camera there and that body is just another toon on the field —
            // clicking it is how you take command and get the control halo.
            if ((entity.Guid == ControlledGuid && !_freeView) ||
                (entity.Fields.UnitFlags & NotSelectable) != 0)
                continue;

            float scale = _creatures?.PickScale(entity) ?? MathF.Max(0.01f, entity.Scale);
            float radius = MathF.Max(0.35f, 0.70f * scale);
            float height = MathF.Max(1.4f, 2.2f * scale);
            if (RayVerticalCylinder(origin, direction, entity.Position, radius, height, out float hit) &&
                hit < nearest)
            {
                nearest = hit;
                picked = entity.Guid;
            }
        }

        if (picked != 0 && _collision?.Raycast(origin, direction, nearest) is { } worldHit &&
            worldHit.Distance < nearest - 0.01f)
            return 0;
        if (picked != 0) hitDistance = nearest;
        return picked;
    }

    private static bool RayVerticalCylinder(
        Vector3 origin, Vector3 direction, Vector3 feet, float radius, float height, out float hit)
    {
        float nearest = float.PositiveInfinity;
        float ox = origin.X - feet.X, oy = origin.Y - feet.Y;
        float a = direction.X * direction.X + direction.Y * direction.Y;
        float b = 2f * (ox * direction.X + oy * direction.Y);
        float c = ox * ox + oy * oy - radius * radius;

        if (a > 1e-7f)
        {
            float discriminant = b * b - 4f * a * c;
            if (discriminant >= 0f)
            {
                float root = MathF.Sqrt(discriminant);
                float t0 = (-b - root) / (2f * a);
                float t1 = (-b + root) / (2f * a);
                TestSide(t0);
                TestSide(t1);
            }
        }

        if (MathF.Abs(direction.Z) > 1e-7f)
        {
            TestCap((feet.Z - origin.Z) / direction.Z);
            TestCap((feet.Z + height - origin.Z) / direction.Z);
        }
        hit = nearest;
        return float.IsFinite(nearest);

        void TestSide(float t)
        {
            if (t < 0f || t >= nearest) return;
            float z = origin.Z + direction.Z * t;
            if (z >= feet.Z && z <= feet.Z + height) nearest = t;
        }

        void TestCap(float t)
        {
            if (t < 0f || t >= nearest) return;
            float x = ox + direction.X * t, y = oy + direction.Y * t;
            if (x * x + y * y <= radius * radius) nearest = t;
        }
    }

    private void DrawTargetFrame()
    {
        if (_selectionGuid == 0 || !_entities.TryGet(_selectionGuid, out WorldEntity target)) return;
        FactionReaction reaction = ReactionTargetTowardPlayer(target);
        string name = target.IsPlayer
            ? _playerNames.GetValueOrDefault(target.Guid, "Player")
            : _creatureNames.GetValueOrDefault(target.Entry, $"Creature {target.Entry}");
        uint portrait = target.IsCreature && _portraitTargetGuid == target.Guid
            ? UnitFramePortrait(_targetPortrait, _targetPortraitUsable)
            : target.IsPlayer && _net is not null && target.Guid == ControlledGuid
                ? UnitFramePortrait(_playerPortrait, _playerPortraitUsable)
                : 0;
        DrawVanillaUnitFrame(target, new Vector2(250, 4), playerFrame: false,
            name, reaction, portrait, _targetCombatFlash);
    }
}
