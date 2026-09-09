using MSUIClient.Engine.UI;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private void PostCombatNotice(CombatEvent notice)
    {
        if (notice is not (CombatDispel or CombatInstantKill or CombatPartyKill or CombatSpellExecution or CombatEnchantment)) return;
        string UnitName(ulong guid)
        {
            if (TryResolveCombatXpVictim(guid, out string name)) return name;
            if (guid == _net?.PlayerGuid && !string.IsNullOrEmpty(_net.PlayerName)) return _net.PlayerName;
            BeginCombatXpVictimQuery(guid);
            return "Unknown";
        }
        if (notice is CombatEnchantment enchantment)
        {
            string ItemName(uint entry)
            {
                if (_items?.TryGet(entry, out var item) == true && item is not null) return item.Name;
                if (_net is not null) _items?.Require(entry, 0, _net);
                return InventoryGlobalString("UNKNOWN", "Unknown");
            }
            string EnchantmentName(uint id) => _enchantCatalog?.Name(id) is { Length: > 0 } name
                ? name : InventoryGlobalString("UNKNOWN", "Unknown");
            AddChatMessage(EnchantmentNoticeUiLaw.Text(enchantment, ControlledGuid, UnitName,
                ItemName, EnchantmentName, InventoryGlobalString), ChatFrameLaw.MsgType.CombatNotice);
            return;
        }
        string SpellName(uint id) => _spellCatalog?.TryGet(id, out var spell) == true ? spell.Name : $"Spell {id}";
        foreach (string line in CombatNoticeUiLaw.Lines(notice, ControlledGuid, UnitName, SpellName))
            AddChatMessage(line, ChatFrameLaw.MsgType.CombatNotice);
    }
}
