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
        if (_gameplayArt is null || _window.MouseCaptured ||
            ImGui.GetIO().WantCaptureMouse || _settingsOpen || _groundCastSpell != 0 ||
            _itemCastSpell != 0) return;

        // Point is the reference's base in-world cursor. A unit verdict replaces it; empty world,
        // non-interactive geometry and gameobjects without a data-driven cursor retain Point.
        string stem = WorldCursorKind.Point.ToString();
        if (_hoveredGameObjectGuid != 0 &&
            _entities.TryGet(_hoveredGameObjectGuid, out WorldEntity hoveredGo) &&
            hoveredGo.IsGameObject && _entities.TryGet(ControlledGuid, out WorldEntity goPlayer))
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
            WorldCursorState? goState = WorldCursorUiLaw.GameObject(
                unchecked((int)hoveredGo.GameObjectType), hoveredGo.Fields.GameObjectFlags,
                hoveredGo.Fields.GameObjectDynamicFlags, firstLockType,
                facts.FishingChannelOwned, facts.MeetingStoneQueued,
                facts.HostileTowardPlayer, lockMet, Vector3.DistanceSquared(
                    _controller?.Position ?? goPlayer.Position, hoveredGo.Position));
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

        float distanceSquared = Vector3.DistanceSquared(
            _controller?.Position ?? player.Position, unit.Position);
        uint? questStatus = _questStatuses.TryGetValue(unit.Guid, out uint status)
            ? status : null;
        bool knowsSkinning = _skillLines is not null && _actions.KnownSpells
            .Any(spell => _skillLines.SpellLine(spell) == 393);
        WorldCursorState? state = WorldCursorUiLaw.Unit(unit.IsPlayer, unit.IsDead,
            unit.Fields.Lootable, (unit.Fields.UnitFlags & WorldCursorUiLaw.Skinnable) != 0,
            knowsSkinning, CanAttack(unit),
            WorldCursorServiceKind(unit) is not null,
            unit.NpcFlags, questStatus, distanceSquared, player.Fields.CombatReach,
            unit.Fields.CombatReach, autoLoot: false, ImGui.GetIO().KeyShift);
        if (state is not null) stem = state.Value.Stem;
        DrawBagHoverCursor(stem);
    }
}
