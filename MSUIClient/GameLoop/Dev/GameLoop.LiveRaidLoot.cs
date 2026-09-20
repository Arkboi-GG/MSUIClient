// Opt-in mission QA ingress for ordinary received raid loot and equipment.
using System.Numerics;
using System.Text.Json;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private bool TryLiveRaidMasterLootSetup()
    {
        if (_liveRunOptions is null || _raidQaAttemptDirectory is null ||
            _raidQaStage is not (CommanderRaidAttemptStage.Recovery or CommanderRaidAttemptStage.Preparation) ||
            LocalPlayerGuid != 787 || ControlledGuid != LocalPlayerGuid || _config.Start.Map != 409 ||
            _partyLeaderGuid != LocalPlayerGuid || _partyMembers.Count != 39 ||
            _partyMembers.Select(member => member.Guid).Distinct().Count() != 39 ||
            _partyMembers.Any(member => !IsAuthorizedRaidQaBot(member.Guid)) ||
            _net is not { IsInWorld: true } || RaidQaAnyCombat() ||
            RefuseTacticalFreezeLiveCommand("setting raid loot method")) return false;
        if (_partyLootMethod == 2 && _partyMasterLooterGuid == LocalPlayerGuid) return true;
        bool sent = _net.GroupLootMethod(2, LocalPlayerGuid, 2);
        WriteRaidQaEvent("loot-method-request", new { method=2, master=LocalPlayerGuid, threshold=2, sent });
        return sent; // Verify the received group method before the pull.
    }

    private bool TryLiveMasterLoot(byte slot, ulong recipient)
    {
        if (_liveRunOptions is null || _raidQaAttemptDirectory is null ||
            _raidQaStage != CommanderRaidAttemptStage.Recovery || LocalPlayerGuid != 787 || _config.Start.Map != 409 ||
            (recipient != 787 && !IsAuthorizedRaidQaBot(recipient)) ||
            _net is not { IsInWorld: true } || !_loot.IsOpen ||
            _partyLootMethod != 2 || _partyMasterLooterGuid != ControlledGuid ||
            !_lootMasterCandidates.Contains(recipient) ||
            !_loot.Items.Any(item => item.Slot == slot && item.SlotType == 2)) return false;
        if (RefuseTacticalFreezeLiveCommand("assigning loot") ||
            RefuseTacticalFrozenActor(_loot.Source, "assign its loot") ||
            RefuseTacticalFrozenActor(recipient, "assign loot to them")) return false;
        if (!TryGetInteractionBodyPose(out var body) ||
            !_entities.TryGet(ControlledGuid, out var actor) || actor.IsDead || actor.InCombat ||
            !_entities.TryGet(_loot.Source, out var source) ||
            Vector3.DistanceSquared(body.Position, source.Position) >
                WorldCursorUiLaw.UnitMeleeReachSquared(actor.Fields.CombatReach, source.Fields.CombatReach)) return false;
        bool sent = _net.LootMasterGive(_loot.Source, slot, recipient);
        EmitInterface("loot", "master-give", sent ? "SENT" : "SEND_FAILED", _loot.Source,
            $"slot={slot};target=0x{recipient:X16};actor=0x{ControlledGuid:X16}");
        WriteRaidQaEvent("loot-master-request", new { source = _loot.Source, slot, recipient, actor = ControlledGuid, sent });
        return sent; // A sent packet is not an inventory receipt or an equipped upgrade.
    }

    private bool TryLiveEquipRaidLoot(uint entry, ulong itemGuid)
    {
        if (_liveRunOptions is null || _raidQaStage != CommanderRaidAttemptStage.Recovery ||
            LocalPlayerGuid != 787 || _config.Start.Map != 409 ||
            (ControlledGuid != 787 && !IsAuthorizedRaidQaBot(ControlledGuid)) ||
            !CanAuthorControlledOrSelf || _net is null || _items is null ||
            !_entities.TryGet(ControlledGuid, out var actor) || actor.IsDead || actor.InCombat ||
            actor.Fields.PlayerIsGhost || RefuseTacticalFreezeLiveCommand("equipping raid loot")) return false;
        var copies = EnumerateActionItemCopies(actor, entry).Where(x => x.Item.Guid == itemGuid).ToArray();
        if (copies.Length != 1 || copies[0].Worn) return false;
        _items.Require(entry, itemGuid, _net);
        if (!_items.TryGet(entry, out ItemTemplate? template) || template is null || template.InventoryType == 0) return false;
        bool sent = _net.AutoEquipItem(copies[0].Bag, copies[0].Slot);
        WriteRaidQaEvent("loot-equip-request", new { actor = ControlledGuid, entry, itemGuid, sent });
        return sent;
    }

    private bool WriteLiveRaidInventoryReport(string label)
    {
        if (_liveRunOptions is null || _raidQaAttemptDirectory is null || _items is null ||
            _net is null || !_entities.TryGet(ControlledGuid, out var actor) ||
            label.Length == 0 || label.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')) return false;
        var entries = _entities.Entities.Values.Select(x => x.Entry).Distinct().ToArray();
        var items = entries.SelectMany(entry => EnumerateActionItemCopies(actor, entry)).ToArray();
        foreach(var item in items) _items.Require(item.Item.Entry, item.Item.Guid, _net);
        using var output = new FileStream(Path.Combine(_raidQaAttemptDirectory, label + ".json"), FileMode.CreateNew, FileAccess.Write);
        JsonSerializer.Serialize(output, new { utc=DateTime.UtcNow, actor=ControlledGuid,
            selectedAmmo=actor.Fields.PlayerAmmoId,
            selectedAmmoCount=CarriedAmmoCount(actor, actor.Fields.PlayerAmmoId),
            rangedItemGuid=actor.Fields.PlayerInventorySlot(17),
            learnedShots=ActionsFor(ControlledGuid).KnownSpells
                .Select(id => _spellCatalog?.TryGet(id, out var spell) == true ? spell : default)
                .Where(spell => spell.Id != 0 && spell.Name.StartsWith("Shoot", StringComparison.Ordinal))
                .Select(spell => new { spell.Id, spell.Name, spell.Passive, spell.Ranged,
                    spell.EquippedItemClass, spell.EquippedItemSubclassMask, spell.RangeIndex,
                    range=_spellCatalog!.TryGetRange(spell.RangeIndex, out var range) ? range : default }),
            items=items.Select(x => new { guid=x.Item.Guid, entry=x.Item.Entry, count=x.Item.Fields.ItemStackCount,
                bag=x.Bag, slot=x.Slot, worn=x.Worn, template=_items.TryGet(x.Item.Entry,out ItemTemplate? item)?item:null }) },
            new JsonSerializerOptions { IncludeFields=true });
        return true;
    }

    private bool WriteLiveLootReport(string label)
    {
        if (_liveRunOptions is null || _raidQaAttemptDirectory is null ||
            label.Length == 0 || label.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')) return false;
        string path = Path.Combine(_raidQaAttemptDirectory, label + ".json");
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        JsonSerializer.Serialize(output, new { utc = DateTime.UtcNow, actor = ControlledGuid,
            source = _loot.Source, sourceEntry = _entities.TryGet(_loot.Source, out var source) ? source.Entry : 0,
            map = _config.Start.Map, open = _loot.IsOpen, gold = _loot.Gold, items = _loot.Items,
            method = _partyLootMethod, master = _partyMasterLooterGuid,
            candidates = _lootMasterCandidates.ToArray() });
        return true;
    }
}
