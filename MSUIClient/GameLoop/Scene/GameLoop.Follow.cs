using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;
using Silk.NET.Input;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private const uint AutoFollowStunnedFlag = 0x0004_0000u;

    private ulong _autoFollowGuid;
    private string _autoFollowName = "";
    private bool _autoFollowMoving;
    private byte _autoFollowManualMovementMask;
    private bool _autoFollowBothMouseDown;
    private string _autoFollowStatusText = "";
    private double? _autoFollowStatusFadeStartedAt;

    /// <summary>
    /// FollowByName(menu.name, 1), after UnitPopup has already resolved that exact name to its
    /// guid. A streamed-out roster member is a resolver miss: it leaves an existing follow alone
    /// and raises no error, just like the reference by-name funnel.
    /// </summary>
    private void StartAutoFollow(ulong guid, string exactName)
    {
        if (RefuseTacticalFreezeLiveCommand("following another player")) return;
        if (RefuseTacticalFrozenActor(guid, "follow them")) return;
        if (guid == 0 ||
            _net is not { IsInWorld: true } || _controller is null || _freeView ||
            _controlState != ControlState.OwnChar ||
            !_entities.TryGet(guid, out WorldEntity followee) ||
            !_entities.TryGet(LocalPlayerGuid, out WorldEntity player))
            return;

        AutoFollowRefusal refusal = AutoFollowUiLaw.StartRefusal(
            followee.IsPlayer, CanAssistFollowTarget(followee), player.IsDead,
            (player.Fields.UnitFlags & AutoFollowStunnedFlag) != 0,
            _castBarPhase is CastBarPhase.Casting or CastBarPhase.Channel);
        if (refusal != AutoFollowRefusal.None)
        {
            string key = AutoFollowUiLaw.RefusalGlobalString(refusal)!;
            ShowUiError(InventoryGlobalString(key, AutoFollowRefusalFallback(refusal)));
            return;
        }

        _autoFollowGuid = guid;
        _autoFollowName = exactName.Trim();
        _autoFollowMoving = false;
        _autoFollowStatusText = AutoFollowUiLaw.BeginText(_autoFollowName);
        _autoFollowStatusFadeStartedAt = null;
    }

    /// <summary>
    /// The shared by-name tier used by /follow: case-insensitive exact first, otherwise longest
    /// common prefix, then strictly nearest 3D centre within a prefix-length tie.
    /// </summary>
    private bool TryResolveAutoFollowByName(string query, out ulong guid, out string name)
    {
        guid = 0;
        name = "";
        string wanted = query.Trim();
        var controller = _controller;
        if (wanted.Length == 0 || controller is null) return false;

        bool bestExact = false;
        int bestPrefix = 0;
        float bestDistanceSquared = float.PositiveInfinity;
        foreach (WorldEntity candidate in _entities.Entities.Values)
        {
            if (!candidate.IsPlayer || candidate.IsDead || !CanAssistFollowTarget(candidate) ||
                !TryAutoFollowName(candidate.Guid, out string candidateName)) continue;

            bool exact = wanted.Equals(candidateName, StringComparison.OrdinalIgnoreCase);
            int prefix = exact ? wanted.Length : AutoFollowCommonPrefix(wanted, candidateName);
            if (prefix < 1) continue;
            float distanceSquared = Vector3.DistanceSquared(controller.Position, candidate.Position);
            bool beats = exact && !bestExact || exact == bestExact &&
                (prefix > bestPrefix || prefix == bestPrefix && distanceSquared < bestDistanceSquared);
            if (!beats) continue;
            guid = candidate.Guid;
            name = candidateName;
            bestExact = exact;
            bestPrefix = prefix;
            bestDistanceSquared = distanceSquared;
        }
        return guid != 0;
    }

    private bool TryAutoFollowName(ulong guid, out string name)
    {
        if (guid == LocalPlayerGuid && _net?.PlayerName is { Length: > 0 } own)
        {
            name = own;
            return true;
        }
        return _playerNames.TryGetValue(guid, out name!) && name.Length > 0;
    }

    private string AutoFollowTargetName(ulong guid, WorldEntity target)
    {
        if (TryAutoFollowName(guid, out string name)) return name;
        return target.IsCreature
            ? ResolveCreatureOrPetName(target, _creatureNames.GetValueOrDefault(target.Entry, ""))
            : "";
    }

    private static int AutoFollowCommonPrefix(string left, string right)
    {
        int count = Math.Min(left.Length, right.Length);
        int i = 0;
        while (i < count && char.ToUpperInvariant(left[i]) == char.ToUpperInvariant(right[i])) i++;
        return i;
    }

    private static string AutoFollowRefusalFallback(AutoFollowRefusal refusal) => refusal switch
    {
        AutoFollowRefusal.InvalidTarget => "You can't follow that unit.",
        AutoFollowRefusal.PlayerDead => "You can't do that when you're dead.",
        AutoFollowRefusal.Stunned => "You are stunned.",
        AutoFollowRefusal.Busy => "You are too busy to follow anything.",
        _ => "You can't follow that unit.",
    };

    private bool CanAssistFollowTarget(WorldEntity target)
    {
        if (!target.IsPlayer) return false;
        if (target.Guid == LocalPlayerGuid ||
            _partyMembers.Any(member => member.Guid == target.Guid)) return true;
        return ReactionPlayerToward(target) == FactionReaction.Friendly;
    }

    private bool StopAutoFollow(bool showStatus)
    {
        if (_autoFollowGuid == 0) return false;
        _autoFollowGuid = 0;
        _autoFollowMoving = false;
        if (showStatus && _autoFollowName.Length > 0)
        {
            _autoFollowStatusText = AutoFollowUiLaw.EndText(_autoFollowName);
            _autoFollowStatusFadeStartedAt = NowSeconds();
        }
        else
        {
            _autoFollowName = "";
            _autoFollowStatusText = "";
            _autoFollowStatusFadeStartedAt = null;
        }
        return true;
    }

    /// <summary>PLAYER_ENTERING_WORLD/session teardown clears both mode and status silently.</summary>
    private void ResetAutoFollowSession()
    {
        _autoFollowGuid = 0;
        _autoFollowName = "";
        _autoFollowMoving = false;
        _autoFollowManualMovementMask = 0;
        _autoFollowBothMouseDown = false;
        _autoFollowStatusText = "";
        _autoFollowStatusFadeStartedAt = null;
    }

    /// <summary>
    /// Synthesizes the same forward axis ordinary MoveForward uses. It owns no translation and
    /// sends no special packet; the normal controller and movement sender remain authoritative.
    /// </summary>
    private void ApplyAutoFollowInput(ref float forward, float dt, bool typing, bool mouseSteering)
    {
        byte movementMask = AutoFollowMovementMask(typing);
        bool movementStarted = (movementMask & ~_autoFollowManualMovementMask) != 0;
        _autoFollowManualMovementMask = movementMask;

        bool bothMouse = _window.MouseLeftDown && _window.MouseRightDown;
        bool bothMouseEngaged = bothMouse && !_autoFollowBothMouseDown;
        _autoFollowBothMouseDown = bothMouse;

        if (_autoFollowGuid == 0) return;

        var controller = _controller;
        if (controller is null)
        {
            StopAutoFollow(showStatus: true);
            return;
        }
        bool hasPlayer = _entities.TryGet(LocalPlayerGuid, out WorldEntity player);
        bool lostMover = _net is not { IsInWorld: true } ||
            _freeView || _controlState != ControlState.OwnChar || _movementScript is not null ||
            _movementRooted || _taxiLocked || !hasPlayer || player.IsDead ||
            (player.Fields.UnitFlags & AutoFollowStunnedFlag) != 0;
        if (movementStarted || bothMouseEngaged || mouseSteering || lostMover)
        {
            StopAutoFollow(showStatus: true);
            return;
        }

        if (!_entities.TryGet(_autoFollowGuid, out WorldEntity followee) ||
            !followee.IsPlayer || followee.IsDead)
        {
            StopAutoFollow(showStatus: true);
            return;
        }

        float speed = controller.EffectiveRunSpeed * MathF.Max(.05f, controller.SpeedMultiplier);
        AutoFollowMotion motion = AutoFollowUiLaw.Tick(
            followee.Position - controller.Position, _window.Camera.Yaw,
            _autoFollowMoving, speed, dt);
        if (motion.EndsFollow)
        {
            StopAutoFollow(showStatus: true);
            return;
        }

        _window.Camera.Yaw = motion.Yaw;
        _autoFollowMoving = motion.MovingLatch;
        if (motion.Forward) forward = Math.Clamp(forward + 1f, -1f, 1f);
    }

    private byte AutoFollowMovementMask(bool typing)
    {
        if (typing) return 0;
        byte mask = 0;
        if (BindingDown(GameBinding.MoveForward) || InputKeyDown(Key.Up)) mask |= 1 << 0;
        if (BindingDown(GameBinding.MoveBackward) || InputKeyDown(Key.Down)) mask |= 1 << 1;
        if (BindingDown(GameBinding.TurnLeft) || InputKeyDown(Key.Left)) mask |= 1 << 2;
        if (BindingDown(GameBinding.TurnRight) || InputKeyDown(Key.Right)) mask |= 1 << 3;
        if (BindingDown(GameBinding.StrafeLeft)) mask |= 1 << 4;
        if (BindingDown(GameBinding.StrafeRight)) mask |= 1 << 5;
        return mask;
    }

    private void DrawAutoFollowStatus()
    {
        if (_autoFollowStatusText.Length == 0) return;
        bool active = _autoFollowGuid != 0;
        double elapsed = _autoFollowStatusFadeStartedAt is double stoppedAt
            ? NowSeconds() - stoppedAt : 0;
        float alpha = AutoFollowUiLaw.StatusAlpha(active, elapsed);
        if (alpha <= 0f)
        {
            _autoFollowStatusText = "";
            _autoFollowStatusFadeStartedAt = null;
            return;
        }

        Vector2 display = ImGui.GetIO().DisplaySize;
        GameText.DrawCenteredWithAlpha(ImGui.GetBackgroundDrawList(),
            AutoFollowUiLaw.StatusFontObject, _autoFollowStatusText,
            AutoFollowUiLaw.StatusCenter(display), GameplayUiScale(), alpha);
    }
}
