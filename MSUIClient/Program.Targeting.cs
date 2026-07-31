using System.Numerics;
using ImGuiNET;
using Silk.NET.Input;
using MSUIClient.Engine;
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
    private readonly HashSet<ulong> _queriedPlayerNames = [];
    private readonly HashSet<uint> _queriedCreatureNames = [];

    private void ResetTargeting()
    {
        _hoveredGuid = 0;
        _selectionGuid = 0;
        _attackTargetGuid = 0;
        _targetCombatSeen = _combat.AttackRevision;
        _window.ClearWorldClicks();
    }

    private void UpdateTargeting()
    {
        if (_net is not { IsInWorld: true }) return;

        // Reconcile the speculative local attack latch with the authoritative echo.
        if (_combat.AttackRevision != _targetCombatSeen)
        {
            _targetCombatSeen = _combat.AttackRevision;
            ulong previousAttack = _attackTargetGuid;
            _attackTargetGuid = _combat.TryGetAttackTarget(_net.PlayerGuid, out ulong victim)
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
            _hoveredGuid = PickUnit(_window.MousePosition);

        while (_window.TryDequeueWorldClick(out WorldMouseClick click))
        {
            if (_settingsOpen || ImGui.GetIO().WantCaptureMouse) continue;
            ulong picked = PickUnit(click.Position);
            if (click.Button == MouseButton.Left)
                CommitSelection(picked, beginAttack: false); // empty left clears
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
                else CommitSelection(picked, beginAttack: true); // empty right preserves
            }
        }

        if (_creatures is not null)
        {
            _creatures.HoveredGuid = _hoveredGuid;
            _creatures.SelectedGuid = _selectionGuid;
        }
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
        if (_net is null) return;

        bool wasAttacking = _attackTargetGuid != 0 || _combat.IsEngaged(_net.PlayerGuid);
        bool changed = guid != _selectionGuid;
        if (changed && wasAttacking)
        {
            EmitCombat("TargetSwitch", "selection-change", guid,
                $"from=0x{_selectionGuid:X16} to=0x{guid:X16}");
            StopAttack("target-switch");
        }

        if (changed)
        {
            _selectionGuid = guid;
            _net.SetSelection(guid);
            if (guid != 0 && _entities.TryGet(guid, out WorldEntity identity))
            {
                if (identity.IsPlayer && _queriedPlayerNames.Add(guid)) _net.NameQuery(guid);
                else if (identity.IsCreature && identity.Entry != 0 && _queriedCreatureNames.Add(identity.Entry))
                    _net.CreatureQuery(identity.Entry, guid);
            }
        }

        // A running swing follows a valid target switch. A clean right click
        // starts it when it was not already active.
        if (guid != 0 && (beginAttack || (changed && wasAttacking)) &&
            _entities.TryGet(guid, out WorldEntity entity) && CanAttack(entity))
        {
            if (_attackTargetGuid != guid)
            {
                _net.AttackSwing(guid);
                _attackTargetGuid = guid; // speculative until SMSG_ATTACKSTART/STOP
                ObserveCombatIntent(true, guid, changed && wasAttacking ? "target-switch" : "user-start");
            }
        }
    }

    private void StopAttack(string cause = "user-cancel")
    {
        if (_net is null || (_attackTargetGuid == 0 && !_combat.IsEngaged(_net.PlayerGuid))) return;
        _net.AttackStop();
        ObserveCombatIntent(false, _attackTargetGuid, cause);
        _attackTargetGuid = 0;
    }

    private bool CanAttack(WorldEntity target)
    {
        if (_net is null || target.Guid == _net.PlayerGuid || target.IsDead ||
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
            !_entities.TryGet(_net.PlayerGuid, out WorldEntity player) ||
            !_factions.TryGet(player.Fields.FactionTemplate, out FactionTemplateRow own) ||
            !_factions.TryGet(target.Fields.FactionTemplate, out FactionTemplateRow other))
            return FactionReaction.Neutral;
        return own.ReactionToward(other);
    }

    private FactionReaction ReactionTargetTowardPlayer(WorldEntity target)
    {
        if (_net is null || _factions is null ||
            !_entities.TryGet(_net.PlayerGuid, out WorldEntity player) ||
            !_factions.TryGet(target.Fields.FactionTemplate, out FactionTemplateRow other) ||
            !_factions.TryGet(player.Fields.FactionTemplate, out FactionTemplateRow own))
            return FactionReaction.Neutral;
        return other.ReactionToward(own);
    }

    private ulong PickUnit(Vector2 pixel)
    {
        // Benilla vplates.rs:47-50,111-116: last frame's mouse-enabled plate rects feed
        // the shared hover/selection pick before the 3-D world ray.
        for (int i = _vplateHits.Count - 1; i >= 0; i--)
            if (_vplateHits[i].Rect.Contains(pixel)) return _vplateHits[i].Guid;

        var ray = _window.Camera.ScreenPointToRay(pixel, _window.FramebufferSize);
        if (ray is null || _net is null) return 0;

        (Vector3 origin, Vector3 direction) = ray.Value;
        float nearest = TargetPickDistance;
        ulong picked = 0;

        foreach (WorldEntity entity in _entities.Units)
        {
            // Corpses stay pickable - selecting and right-click looting a dead unit is a
            // 1.12 behavior, not an exception. Only NOT_SELECTABLE and the player skip.
            if (entity.Guid == _net.PlayerGuid ||
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
            ? _targetPortraitUsable ? _targetPortrait?.TextureHandle ?? 0 : 0
            : target.IsPlayer && _net is not null && target.Guid == _net.PlayerGuid
                ? _playerPortraitUsable ? _playerPortrait?.TextureHandle ?? 0 : 0
                : 0;
        DrawVanillaUnitFrame(target, new Vector2(250, 4), playerFrame: false,
            name, reaction, portrait, _targetCombatFlash);
    }
}
