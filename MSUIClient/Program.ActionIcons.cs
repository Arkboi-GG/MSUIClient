using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private string ResolveSpellActionIcon(in SpellInfo spell, WorldEntity? player)
    {
        WeaponIconSubstitution substitution = ActionIconLaw.Substitution(spell);
        if (substitution == WeaponIconSubstitution.None) return spell.IconPath;
        int equipmentSlot = substitution == WeaponIconSubstitution.MainHand ? 15 : 17;
        string? icon = ResolveEquippedItemIcon(player, equipmentSlot, out uint? subclass);
        return ActionIconLaw.Resolve(spell, icon, subclass);
    }

    private bool EmitAttackIconEvidence()
    {
        if (_spellCatalog?.TryGet(6603, out SpellInfo attack) != true || _net is null ||
            !_entities.TryGet(_net.PlayerGuid, out WorldEntity player)) return false;
        ulong weaponGuid = player.Fields.PlayerInventorySlot(15);
        uint entry = weaponGuid != 0 && _entities.TryGet(weaponGuid, out WorldEntity weapon) ? weapon.Entry : 0;
        string path = ResolveSpellActionIcon(attack, player);
        EmitInterface("action-icon", "attack", "RESOLVED", weaponGuid,
            $"spell=6603;slot=15;entry={entry};icon={SanitizeEvidence(path)};fallback={entry == 0}");
        return path.Length > 0;
    }

    private string? ResolveEquippedItemIcon(WorldEntity? player, int equipmentSlot,
        out uint? subclass)
    {
        subclass = null;
        if (player is null || _items is null || _net is null) return null;
        ulong guid = player.Fields.PlayerInventorySlot(equipmentSlot);
        if (guid == 0 || !_entities.TryGet(guid, out WorldEntity item)) return null;
        _items.Require(item.Entry, item.Guid, _net);
        if (!_items.TryGet(item.Entry, out ItemTemplate? template) || template is null) return null;
        subclass = template.Subclass;
        return template.IconPath;
    }
}
