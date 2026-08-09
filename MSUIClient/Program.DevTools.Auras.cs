using MSUIClient.Engine;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;
using MSUIClient.World;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private const uint IceBlockSpellId = 11958;

    // The 1.12 server is allowed to send the duration packet immediately before
    // the descriptor swaps the slot to its new spell, so timers are slot-keyed.
    private readonly record struct AuraTimer(uint DurationMs, double Expires, double Received);
    private readonly Dictionary<byte, AuraTimer> _playerAuraDurations = [];
    private readonly List<(byte Slot, uint SpellId)> _playerAuraOrder = [];
    private readonly Dictionary<(byte Slot, uint SpellId), double> _playerAuraAppeared = [];
    private ulong _playerAuraOwnerGuid;

    private void ResetPlayerAuras()
    {
        _playerAuraDurations.Clear();
        _playerAuraOrder.Clear();
        _playerAuraAppeared.Clear();
        _playerAuraOwnerGuid = 0;
    }

    private void EnterPlayerAuraWorld(ulong playerGuid)
    {
        // SMSG_NEW_WORLD is also the cross-map teleport edge.  The reference keeps its
        // insertion-order cache and slot-keyed duration stamps across that loading gap; clear
        // only when this is a different character/session owner.
        if (BuffUiLaw.PreserveAcrossWorldEnter(_playerAuraOwnerGuid, playerGuid)) return;
        ResetPlayerAuras();
        _playerAuraOwnerGuid = playerGuid;
    }

    private bool PlayerHasAura(uint spellId)
    {
        if (_net is null || !_entities.TryGet(_net.PlayerGuid, out WorldEntity? player))
            return false;
        return player.Fields.Auras().Any(a => a.SpellId == spellId);
    }

    private void UpdateIceBlockFreezeState()
    {
        bool auraActive = PlayerHasAura(IceBlockSpellId);
        if (auraActive && !_iceBlockFrozen)
        {
            _iceBlockFrozen = true;
            _iceBlockFacing = _controller?.Yaw ?? _window.Camera.Yaw;
            Console.WriteLine($"[movement] Ice Block pose frozen at facing={_iceBlockFacing:F4}");
        }
        else if (_iceBlockFrozen && !auraActive)
        {
            _iceBlockFrozen = false;
            if (_controller is not null)
                _window.Camera.SetFacingKeepingView(_controller.Yaw);
            Console.WriteLine("[movement] Ice Block pose released");
        }
    }

    private readonly record struct AuraSnapshot(byte Slot, uint SpellId, byte Flags, byte Level,
        byte Stacks)
    {
        public bool Helpful => Slot < 32;
        public bool Cancelable => Helpful && (Flags & 0x01) != 0;
    }

    private static Dictionary<byte, AuraSnapshot> SnapshotAuras(WorldEntity? entity) =>
        entity?.Fields.Auras().ToDictionary(a => a.Slot,
            a => new AuraSnapshot(a.Slot, a.SpellId, a.Flags, a.Level, a.Stacks)) ?? [];

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
            BuffUiLaw.AuraKey[] prior = _playerAuraOrder
                .Select(x => new BuffUiLaw.AuraKey(x.Slot, x.SpellId)).ToArray();
            BuffUiLaw.AuraKey[] next = BuffUiLaw.ReconcileOrder(prior,
                after.Values.Select(a => new BuffUiLaw.AuraKey(a.Slot, a.SpellId)));
            HashSet<BuffUiLaw.AuraKey> priorSet = prior.ToHashSet();
            HashSet<BuffUiLaw.AuraKey> nextSet = next.ToHashSet();
            foreach (BuffUiLaw.AuraKey removed in prior.Where(x => !nextSet.Contains(x)))
                _playerAuraAppeared.Remove((removed.Slot, removed.SpellId));
            double now = NowSeconds();
            foreach (BuffUiLaw.AuraKey added in next.Where(x => !priorSet.Contains(x)))
                _playerAuraAppeared[(added.Slot, added.SpellId)] = now;
            _playerAuraOrder.Clear();
            _playerAuraOrder.AddRange(next.Select(x => (x.Slot, x.SpellId)));

            // Duration stamps are deliberately not removed with an empty/recycled slot.  A
            // fresh apply's duration packet arrives before its descriptor, so occupancy-based
            // pruning deletes the packet that matters.  TryPlayerAuraTimer performs the
            // appeared-at freshness join at the read site instead.
        }
    }

    private void ApplyAuraDuration(ReadOnlySpan<byte> body)
    {
        var r = new PacketReader(body.ToArray());
        byte slot = r.ReadU8();
        uint duration = r.ReadU32();
        if (_net is null) return;
        // The server is allowed to send this before the descriptor update that names the slot,
        // including before the initial player object has finished parsing. Preserve the slot-keyed
        // timer unnamed; TryPlayerAuraTimer joins it to the newly visible aura by arrival time.
        WorldEntity? player = _entities.TryGet(_net.PlayerGuid, out WorldEntity? foundPlayer)
            ? foundPlayer : null;
        Dictionary<byte, AuraSnapshot> auras = SnapshotAuras(player);
        double now = NowSeconds();
        _playerAuraDurations[slot] = new AuraTimer(duration, now + duration / 1000.0, now);
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
        var aura = new AuraSnapshot(slot, spellId, flags, 0, stacks);
        if (action.Equals("duration", StringComparison.OrdinalIgnoreCase))
        {
            double now = NowSeconds();
            _playerAuraDurations[slot] = new AuraTimer(durationMs,
                now + durationMs / 1000.0, now);
            _playerAuraAppeared.TryAdd((slot, spellId), now);
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
        if (@event == "REMOVE")
            _playerAuraAppeared.Remove((slot, spellId));
        return true;
    }

    private bool TryPlayerAuraTimer(AuraSnapshot aura, out AuraTimer timer)
    {
        timer = default;
        return _playerAuraDurations.TryGetValue(aura.Slot, out timer) &&
               _playerAuraAppeared.TryGetValue((aura.Slot, aura.SpellId), out double appeared) &&
               BuffUiLaw.DurationBelongsToAura(timer.Received, appeared);
    }

    private void EmitAuraVerdict(AuraSnapshot aura, ulong guid, string @event, string source)
    {
        double now = NowSeconds();
        uint duration = 0, remaining = 0;
        if (TryPlayerAuraTimer(aura, out AuraTimer timer))
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

    private static string AuraTimeText(double remainingSeconds)
    {
        remainingSeconds = Math.Max(0, remainingSeconds);
        return remainingSeconds >= 3600 ? $"{Math.Ceiling(remainingSeconds / 3600)}h" :
            remainingSeconds >= 60 ? $"{Math.Ceiling(remainingSeconds / 60)}m" :
            $"{Math.Ceiling(remainingSeconds)}s";
    }
}
