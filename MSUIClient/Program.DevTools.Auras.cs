using MSUIClient.Engine;
using MSUIClient.Formats;
using MSUIClient.Net;
using MSUIClient.World;

namespace MSUIClient;

public sealed partial class GameLoop
{
    // The 1.12 server is allowed to send the duration packet immediately before
    // the descriptor swaps the slot to its new spell, so timers are slot-keyed.
    private readonly Dictionary<byte, (uint DurationMs, double Expires)> _playerAuraDurations = [];

    private readonly record struct AuraSnapshot(byte Slot, uint SpellId, byte Flags, byte Stacks)
    {
        public bool Helpful => Slot < 32;
        public bool Cancelable => Helpful && (Flags & 0x01) != 0;
    }

    private static Dictionary<byte, AuraSnapshot> SnapshotAuras(WorldEntity? entity) =>
        entity?.Fields.Auras().ToDictionary(a => a.Slot,
            a => new AuraSnapshot(a.Slot, a.SpellId, a.Flags, a.Stacks)) ?? [];

    private void ObserveAuraObjectUpdate(ulong guid, Dictionary<byte, AuraSnapshot> before)
    {
        _entities.TryGet(guid, out WorldEntity? unit);
        Dictionary<byte, AuraSnapshot> after = SnapshotAuras(unit);
        foreach ((byte slot, AuraSnapshot oldAura) in before)
        {
            if (after.TryGetValue(slot, out AuraSnapshot current) && current == oldAura) continue;
            EmitAuraVerdict(oldAura, guid, "REMOVE", "SMSG_UPDATE_OBJECT");
            if (_net is not null && guid == _net.PlayerGuid && !after.ContainsKey(slot))
                _playerAuraDurations.Remove(slot);
        }
        foreach ((byte slot, AuraSnapshot aura) in after)
        {
            if (!before.TryGetValue(slot, out AuraSnapshot oldAura) || oldAura.SpellId != aura.SpellId)
                EmitAuraVerdict(aura, guid, "APPLY", "SMSG_UPDATE_OBJECT");
            else if (oldAura.Stacks != aura.Stacks)
                EmitAuraVerdict(aura, guid, "STACK", "SMSG_UPDATE_OBJECT");
        }
    }

    private void ApplyAuraDuration(ReadOnlySpan<byte> body)
    {
        var r = new PacketReader(body.ToArray());
        byte slot = r.ReadU8();
        uint duration = r.ReadU32();
        if (_net is null || !_entities.TryGet(_net.PlayerGuid, out WorldEntity? player)) return;
        Dictionary<byte, AuraSnapshot> auras = SnapshotAuras(player);
        _playerAuraDurations[slot] = (duration, NowSeconds() + duration / 1000.0);
        if (auras.TryGetValue(slot, out AuraSnapshot aura))
            EmitAuraVerdict(aura, _net.PlayerGuid, "DURATION", "SMSG_UPDATE_AURA_DURATION");
    }

    private void CancelPlayerAura(AuraSnapshot aura, string source)
    {
        if (_net is null) return;
        if (!aura.Cancelable)
        {
            EmitAuraVerdict(aura, _net.PlayerGuid, "CANCEL_BLOCKED", source);
            return;
        }
        bool sent = _net.CancelAura(aura.SpellId);
        EmitAuraVerdict(aura, _net.PlayerGuid, sent ? "CANCEL_SEND" : "CANCEL_FAILED", source);
    }

    private bool TryCancelAuraBySpell(uint spellId, string source)
    {
        if (_net is null || !_entities.TryGet(_net.PlayerGuid, out WorldEntity? player)) return false;
        AuraSnapshot? aura = SnapshotAuras(player).Values.FirstOrDefault(a => a.SpellId == spellId);
        if (aura is not { SpellId: not 0 } found) return false;
        CancelPlayerAura(found, source);
        return found.Cancelable;
    }

    private bool SimulateAura(string action, byte slot, uint spellId, byte flags, byte stacks, uint durationMs)
    {
        ulong guid = _net?.PlayerGuid ?? 1;
        var aura = new AuraSnapshot(slot, spellId, flags, stacks);
        if (action.Equals("duration", StringComparison.OrdinalIgnoreCase))
            _playerAuraDurations[slot] = (durationMs, NowSeconds() + durationMs / 1000.0);
        if (action.Equals("cancel", StringComparison.OrdinalIgnoreCase))
        {
            EmitAuraVerdict(aura, guid, aura.Cancelable ? "CANCEL_SEND" : "CANCEL_BLOCKED", "SYNTHETIC_WIRE_REPLAY");
            return true;
        }
        string @event = action.ToUpperInvariant() switch
        {
            "APPLY" => "APPLY",
            "STACK" => "STACK",
            "DURATION" => "DURATION",
            "REMOVE" => "REMOVE",
            _ => ""
        };
        if (@event.Length == 0) return false;
        EmitAuraVerdict(aura, guid, @event, action.Equals("duration", StringComparison.OrdinalIgnoreCase)
            ? "SMSG_UPDATE_AURA_DURATION" : "SMSG_UPDATE_OBJECT");
        if (@event == "REMOVE") _playerAuraDurations.Remove(slot);
        return true;
    }

    private void EmitAuraVerdict(AuraSnapshot aura, ulong guid, string @event, string source)
    {
        double now = NowSeconds();
        uint duration = 0, remaining = 0;
        if (_playerAuraDurations.TryGetValue(aura.Slot, out var timer))
        {
            duration = timer.DurationMs;
            remaining = (uint)Math.Max(0, (timer.Expires - now) * 1000.0);
        }
        SpellInfo? info = _spellCatalog?.TryGet(aura.SpellId, out SpellInfo found) == true ? found : null;
        string display = info is null ? "CATALOG_MISS_VISIBLE" :
            info.Value.IconPath.Length == 0 ? "NO_ICON_HIDDEN" : "ICON_VISIBLE";
        var verdict = new AuraVerdict(now, _net?.PlayerName ?? "", guid, aura.Slot, aura.SpellId,
            info?.Name ?? $"Spell {aura.SpellId}", @event, aura.Helpful, aura.Cancelable,
            aura.Stacks, duration, remaining, display, source);
        _verdicts.Add(verdict);
        Console.WriteLine($"[verdict:spell-aura] {verdict.ToLine()}");
    }
}
