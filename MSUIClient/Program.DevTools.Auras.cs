using MSUIClient.Engine;
using MSUIClient.Formats;
using MSUIClient.Net;
using MSUIClient.World;

namespace MSUIClient;

public sealed partial class GameLoop
{
    // The 1.12 server is allowed to send the duration packet immediately before
    // the descriptor swaps the slot to its new spell, so timers are slot-keyed.
    private readonly record struct AuraTimer(uint DurationMs, double Expires,
        double Received, uint SpellId);
    private readonly Dictionary<byte, AuraTimer> _playerAuraDurations = [];
    private readonly List<(byte Slot, uint SpellId)> _playerAuraOrder = [];

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
        bool playerUpdate = _net is not null && guid == _net.PlayerGuid;
        foreach ((byte slot, AuraSnapshot oldAura) in before)
        {
            if (after.TryGetValue(slot, out AuraSnapshot current) &&
                current.SpellId == oldAura.SpellId) continue;
            EmitAuraVerdict(oldAura, guid, "REMOVE", "SMSG_UPDATE_OBJECT");
            if (playerUpdate && !after.ContainsKey(slot))
                _playerAuraDurations.Remove(slot);
        }
        foreach ((byte slot, AuraSnapshot aura) in after)
        {
            if (!before.TryGetValue(slot, out AuraSnapshot oldAura) || oldAura.SpellId != aura.SpellId)
                EmitAuraVerdict(aura, guid, "APPLY", "SMSG_UPDATE_OBJECT");
            else if (oldAura.Stacks != aura.Stacks)
                EmitAuraVerdict(aura, guid, "STACK", "SMSG_UPDATE_OBJECT");
        }
        if (playerUpdate)
        {
            var exact = after.Values.Select(a => (a.Slot, a.SpellId)).ToHashSet();
            _playerAuraOrder.RemoveAll(x => !exact.Contains(x));
            foreach (AuraSnapshot aura in after.Values.OrderBy(a => a.Slot))
                if (!_playerAuraOrder.Contains((aura.Slot, aura.SpellId)))
                    _playerAuraOrder.Add((aura.Slot, aura.SpellId));

            double now = NowSeconds();
            foreach (AuraSnapshot aura in after.Values)
            {
                if (!_playerAuraDurations.TryGetValue(aura.Slot, out AuraTimer timer) ||
                    timer.SpellId == aura.SpellId) continue;
                if (now - timer.Received <= 1.0)
                    _playerAuraDurations[aura.Slot] = timer with { SpellId = aura.SpellId };
                else
                    _playerAuraDurations.Remove(aura.Slot);
            }
        }
    }

    private void ApplyAuraDuration(ReadOnlySpan<byte> body)
    {
        var r = new PacketReader(body.ToArray());
        byte slot = r.ReadU8();
        uint duration = r.ReadU32();
        if (_net is null || !_entities.TryGet(_net.PlayerGuid, out WorldEntity? player)) return;
        Dictionary<byte, AuraSnapshot> auras = SnapshotAuras(player);
        double now = NowSeconds();
        uint spell = auras.TryGetValue(slot, out AuraSnapshot current) ? current.SpellId : 0;
        _playerAuraDurations[slot] = new AuraTimer(duration, now + duration / 1000.0,
            now, spell);
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
        {
            double now = NowSeconds();
            _playerAuraDurations[slot] = new AuraTimer(durationMs,
                now + durationMs / 1000.0, now, spellId);
        }
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
        if (_playerAuraDurations.TryGetValue(aura.Slot, out AuraTimer timer) &&
            timer.SpellId == aura.SpellId)
        {
            duration = timer.DurationMs;
            remaining = (uint)Math.Max(0, (timer.Expires - now) * 1000.0);
        }
        SpellInfo? info = _spellCatalog?.TryGet(aura.SpellId, out SpellInfo found) == true ? found : null;
        string display = info is null || info.Value.IconPath.Length == 0
            ? "FALLBACK_ICON_VISIBLE" : "ICON_VISIBLE";
        var verdict = new AuraVerdict(now, _net?.PlayerName ?? "", guid, aura.Slot, aura.SpellId,
            info?.Name ?? $"Spell {aura.SpellId}", @event, aura.Helpful, aura.Cancelable,
            aura.Stacks, duration, remaining, display, source);
        _verdicts.Add(verdict);
        Console.WriteLine($"[verdict:spell-aura] {verdict.ToLine()}");
    }

    private IReadOnlyList<AuraSnapshot> OrderedAuras(WorldEntity unit)
    {
        Dictionary<byte, AuraSnapshot> snapshot = SnapshotAuras(unit);
        if (_net is null || unit.Guid != _net.PlayerGuid)
            return snapshot.Values.OrderBy(a => a.Slot).ToArray();
        var ordered = new List<AuraSnapshot>(snapshot.Count);
        foreach (var key in _playerAuraOrder)
            if (snapshot.TryGetValue(key.Slot, out AuraSnapshot aura) && aura.SpellId == key.SpellId)
                ordered.Add(aura);
        foreach (AuraSnapshot aura in snapshot.Values.OrderBy(a => a.Slot))
            if (!ordered.Any(x => x.Slot == aura.Slot && x.SpellId == aura.SpellId))
                ordered.Add(aura);
        return ordered;
    }

    private bool TryVisibleAuraSpell(uint spellId, out SpellInfo? spell)
    {
        spell = _spellCatalog?.TryGet(spellId, out SpellInfo found) == true ? found : null;
        if (spell is { HiddenClientSide: true }) return false;
        return spell?.AuraIds?.Any(type => type is 44 or 45 or 151) != true;
    }
}
