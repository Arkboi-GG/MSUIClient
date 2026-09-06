using MSUIClient.Engine;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;
using Silk.NET.Input;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private bool ArmedSpellAcceptsGameObject(ulong guid) =>
        _itemCastSpell != 0 && _spellCatalog?.TryGet(_itemCastSpell, out SpellInfo spell) == true &&
        CastTargetLaw.AcceptsGameObject(spell) && _entities.TryGet(guid, out var go) && go.IsGameObject;

    private bool TryHandleObjectSpellClick(WorldMouseClick click, TargetPressPick press)
    {
        if (_itemCastSpell == 0) return false;
        // All armed item/object cursors own world clicks, including an invalid pick.
        // Right-click cancels instead of using a chest, selecting or attacking.
        if (click.Button == MouseButton.Right) { CancelItemTargeting(); return true; }
        ulong guid;
        if (press.Armed) guid = press.GameObjectGuid;
        else
        {
            _ = PickUnit(click.Position, out float unitDistance);
            guid = PickGameObject(click.Position, unitDistance, out _);
        }
        if (!CommitArmedGameObjectCast(guid))
            RefuseCast(_itemCastSpell, "LOCAL_INVALID_OBJECT_TARGET", "Invalid target");
        return true;
    }

    private bool CommitArmedGameObjectCast(ulong guid)
    {
        uint spellId = _itemCastSpell;
        if (!CanAuthorControlledGameplay || _net is not { IsInWorld: true } ||
            !TryGetInteractionBodyPose(out _) || !ArmedSpellAcceptsGameObject(guid) ||
            _spellCatalog?.TryGet(spellId, out SpellInfo spell) != true ||
            !_actions.KnownSpells.Contains(spellId) || spell.Passive) return false;
        double now = NowSeconds();
        if (_actions.IsOnCooldown(spellId, 0, spell, now) ||
            _castBarPhase is CastBarPhase.Casting or CastBarPhase.Channel ||
            RefuseSpellForm(spell) || RefuseSpellReactive(spell, 0, checkTarget: false) ||
            !ControlledActorSpellResourceGate(spell, out _, out _)) return false;
        // Core checks actor range against its DB-authored rotated GO bounds, lock/skill,
        // trap ownership and conditions. Do not read unit reach fields from GO descriptors
        // or replace that shape with a guessed point-distance limit.
        bool sent = _net.CastSpellOnGameObject(spellId, guid);
        EmitCastVerdict(spellId, CastTargetReason.GameObjectTargeting, guid, sent);
        if (!sent) return false;
        CancelItemTargeting();
        _pendingCastSpell = spellId;
        if (spell.StartRecoveryMs > 0) StartActorGlobalCooldown(_actions, ControlledGuid, spell, now);
        return true;
    }
}
