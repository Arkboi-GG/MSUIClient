using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private void ApplyAuraDuration(byte[] body, ulong owner)
    {
        if (body.Length != 5) throw new InvalidDataException("Aura duration requires slot and milliseconds");
        var r = new PacketReader(body);
        byte slot = r.ReadU8();
        uint duration = r.ReadU32();
        if (slot >= 48) throw new InvalidDataException("Aura duration slot exceeds 1.12 aura fields");
        if (_net is null || owner == 0 || owner != ControlledGuid) return;
        // The server is allowed to send this before the descriptor update that names the slot,
        // including before the initial player object has finished parsing. Preserve the slot-keyed
        // timer unnamed; TryPlayerAuraTimer joins it to the newly visible aura by arrival time.
        WorldEntity? player = _entities.TryGet(owner, out WorldEntity? foundPlayer)
            ? foundPlayer : null;
        Dictionary<byte, AuraSnapshot> auras = SnapshotAuras(player);
        double now = NowSeconds();
        _playerAuraDurations[slot] = new AuraTimer(duration, now + duration / 1000.0, now);
        if (auras.TryGetValue(slot, out AuraSnapshot aura))
            EmitAuraVerdict(aura, owner, "DURATION", "SMSG_UPDATE_AURA_DURATION");
    }

}
