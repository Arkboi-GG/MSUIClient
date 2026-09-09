namespace MSUIClient.Net;

public sealed class InstanceOwnershipState
{
    private sealed class Snapshot(bool saved)
    {
        public bool Saved = saved;
        public readonly HashSet<uint> Maps = [];
    }
    private readonly Dictionary<ulong, Snapshot> _owners = [];

    public bool? HasSavedInstances(ulong owner) => _owners.TryGetValue(owner, out var state) ? state.Saved : null;
    public IReadOnlyCollection<uint> Maps(ulong owner) =>
        _owners.TryGetValue(owner, out var state) ? state.Maps : Array.Empty<uint>();

    public bool ApplyOwnership(ulong owner, byte[] body)
    {
        uint value = Read(body, Op.SMSG_UPDATE_INSTANCE_OWNERSHIP);
        if (value > 1) throw new InvalidDataException("invalid instance ownership flag");
        if (owner != 0) _owners[owner] = new(value == 1);
        return value == 1;
    }

    public void ApplyLastInstance(ulong owner, byte[] body)
    {
        uint map = Read(body, Op.SMSG_UPDATE_LAST_INSTANCE);
        // The core prefixes this list of permanent map binds with ownership.
        if (_owners.TryGetValue(owner, out var state) && state.Saved) state.Maps.Add(map);
    }

    public void ApplyDetails(ulong owner, IReadOnlyList<RaidLockout> rows)
    {
        var state = new Snapshot(rows.Count != 0);
        foreach (var row in rows) state.Maps.Add(row.MapId);
        if (owner != 0) _owners[owner] = state;
    }

    public void Clear() => _owners.Clear();

    private static uint Read(byte[] body, Op opcode)
    {
        if (body.Length != 4) throw new InvalidDataException($"bad {opcode} body");
        return new PacketReader(body).ReadU32();
    }
}
