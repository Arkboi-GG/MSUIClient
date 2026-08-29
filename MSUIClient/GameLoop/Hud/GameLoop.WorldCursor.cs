using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private WorldCursorKind? WorldCursorServiceKind(WorldEntity unit)
    {
        if (!unit.IsCreature || unit.IsDead ||
            ReactionTargetTowardPlayer(unit) == FactionReaction.Hostile) return null;
        uint? status = _questStatuses.TryGetValue(unit.Guid, out uint questStatus)
            ? questStatus : null;
        return WorldCursorUiLaw.ServiceKind(unit.NpcFlags, status);
    }

    private void DrawWorldHoverCursor()
    {
        if (_gameplayArt is null || _window.MouseCaptured || _settingsOpen) return;
        bool pointerOverUi = ImGui.GetIO().WantCaptureMouse;
        if (_itemCastSpell != 0)
        {
            DrawBagHoverCursor(WorldCursorUiLaw.ItemTargeting(pointerOverUi).Stem);
            return;
        }
        if (_rtsUnitCastSpellId != 0)
        {
            bool valid = !pointerOverUi && _hoveredGuid != 0 &&
                _entities.TryGet(_hoveredGuid, out WorldEntity candidate) &&
                _spellCatalog?.TryGet(_rtsUnitCastSpellId, out SpellInfo spell) == true &&
                CastTargetLaw.Resolve(spell,
                    CastCandidate(candidate, _hoveredGuid == _rtsUnitCastPrimary),
                    self: null, autoSelfCast: false).Kind == CastTargetKind.Unit;
            DrawBagHoverCursor(new WorldCursorState(WorldCursorKind.Cast,
                Unable: !valid).Stem);
            return;
        }
        if (pointerOverUi || _groundCastSpell != 0) return;

        // Point is the reference's base in-world cursor. A unit verdict replaces it; empty world,
        // non-interactive geometry and gameobjects without a data-driven cursor retain Point.
        string stem = WorldCursorKind.Point.ToString();
        if (_hoveredGameObjectGuid != 0 &&
            _entities.TryGet(_hoveredGameObjectGuid, out WorldEntity hoveredGo) &&
            hoveredGo.IsGameObject)
        {
            RequireGameObjectTemplate(hoveredGo);
            EnsureLockCatalog();
            _gameObjectTemplates.TryGetValue(hoveredGo.Entry, out GameObjectTemplate? template);
            uint firstLockType = template is null ? 0 :
                _locks?.FirstCursorLockType(template.LockId) ?? 0;
            GameObjectLockOutcome lockOutcome = ResolveGameObjectLock(hoveredGo);
            bool lockMet = !lockOutcome.BlocksUsable(
                (hoveredGo.Fields.GameObjectFlags & WorldCursorUiLaw.GameObjectLocked) != 0);
            GameObjectInteractionFacts facts = ResolveGameObjectInteractionFacts(hoveredGo);
            bool goSessionScoped = hoveredGo.GameObjectType is 9 or 19;
            WorldBodyPose goActorBody;
            bool goBodyAvailable = goSessionScoped
                ? TryGetSessionBodyPose(out goActorBody)
                : TryGetControlledBodyPose(out goActorBody);
            float goDistanceSquared = goBodyAvailable &&
                (goSessionScoped || CanAuthorControlledGameplay)
                    ? Vector3.DistanceSquared(goActorBody.Position, hoveredGo.Position)
                    : float.PositiveInfinity;
            WorldCursorState? goState = WorldCursorUiLaw.GameObject(
                unchecked((int)hoveredGo.GameObjectType), hoveredGo.Fields.GameObjectFlags,
                hoveredGo.Fields.GameObjectDynamicFlags, firstLockType,
                facts.FishingChannelOwned, facts.MeetingStoneQueued,
                facts.HostileTowardPlayer, lockMet, goDistanceSquared);
            if (goState is not null) stem = goState.Value.Stem;
            DrawBagHoverCursor(stem);
            return;
        }
        if (_hoveredGuid == 0 || !_entities.TryGet(_hoveredGuid, out WorldEntity unit) ||
            !_entities.TryGet(ControlledGuid, out WorldEntity player))
        {
            DrawBagHoverCursor(stem);
            return;
        }

        uint? questStatus = _questStatuses.TryGetValue(unit.Guid, out uint status)
            ? status : null;
        WorldCursorKind? serviceKind = WorldCursorServiceKind(unit);
        // Loot, skinning and NPC services are session-character interactions. Attacks are
        // routed through the controlled combat body. Neither route ever measures from the
        // Free View camera rig.
        // [SUI] P4b: an NPC service (gossip / vendor / trainer) is now performed AS
        // the driven bot, so its cursor must measure from the CONTROLLED body when
        // possessing (the interaction body), not the parked main — otherwise the
        // hand never turns into the gossip bubble while driving a bot up to an NPC.
        // A corpse's loot/skin stays the session character's, and attacks route
        // through the controlled combat body, both unchanged.
        WorldEntity actor = player;
        WorldBodyPose actorPose;
        bool hasActorPose;
        if (serviceKind is not null)
            hasActorPose = TryGetInteractionBodyPose(out actorPose);
        else if (unit.IsDead && _entities.TryGet(LocalPlayerGuid, out WorldEntity sessionPlayer))
        {
            actor = sessionPlayer;
            hasActorPose = TryGetSessionBodyPose(out actorPose);
        }
        else
            hasActorPose = TryGetControlledBodyPose(out actorPose);
        float distanceSquared = hasActorPose
            ? Vector3.DistanceSquared(actorPose.Position, unit.Position)
            : float.PositiveInfinity;
        PlayerActions skinningActions = unit.IsDead ? OwnActions : _actions;
        bool knowsSkinning = _skillLines is not null && skinningActions.KnownSpells
            .Any(spell => _skillLines.SpellLine(spell) == 393);
        WorldCursorState? state = WorldCursorUiLaw.Unit(unit.IsPlayer, unit.IsDead,
            unit.Fields.Lootable, (unit.Fields.UnitFlags & WorldCursorUiLaw.Skinnable) != 0,
            knowsSkinning, CanAttack(unit),
            serviceKind is not null,
            unit.NpcFlags, questStatus, distanceSquared, actor.Fields.CombatReach,
            unit.Fields.CombatReach, autoLoot: false, ImGui.GetIO().KeyShift);
        if (state is not null) stem = state.Value.Stem;
        DrawBagHoverCursor(stem);
    }
}
