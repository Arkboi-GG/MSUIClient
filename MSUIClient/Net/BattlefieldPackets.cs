namespace MSUIClient.Net;

public enum BattlefieldStatus : uint { None, Queued, Invited, Active }

public sealed record BattlefieldListPacket(ulong Source, uint Map, byte Bracket, IReadOnlyList<uint> Instances)
{
    public static BattlefieldListPacket Parse(byte[] body)
    {
        if (body.Length < 17) throw new InvalidDataException("Short battlefield list");
        var r = new PacketReader(body);
        ulong source = r.ReadU64(); uint map = r.ReadU32(); byte bracket = r.ReadU8(); uint count = r.ReadU32();
        if (map == 0 || count > (uint)r.Remaining / 4 || r.Remaining != (long)count * 4)
            throw new InvalidDataException("Invalid battlefield instance count");
        var instances = new uint[count];
        for (int i = 0; i < instances.Length; i++) instances[i] = r.ReadU32();
        return new(source, map, bracket, Array.AsReadOnly(instances));
    }
}

public readonly record struct BattlefieldStatusPacket(uint Slot, uint Map, byte Bracket, uint Instance,
    BattlefieldStatus Status, uint Time1, uint Time2)
{
    public static BattlefieldStatusPacket Parse(byte[] body)
    {
        if (body.Length < 8) throw new InvalidDataException("Short battlefield status");
        var r = new PacketReader(body); uint slot = r.ReadU32(), map = r.ReadU32();
        if (slot >= 3) throw new InvalidDataException("Invalid battlefield queue slot");
        if (map == 0)
        {
            if (r.Remaining != 0) throw new InvalidDataException("Trailing cleared battlefield status");
            return new(slot, 0, 0, 0, BattlefieldStatus.None, 0, 0);
        }
        if (body.Length < 17) throw new InvalidDataException("Short active battlefield status");
        byte bracket = r.ReadU8(); uint instance = r.ReadU32(); var status = (BattlefieldStatus)r.ReadU32();
        int tail = status switch { BattlefieldStatus.Queued or BattlefieldStatus.Active => 8, BattlefieldStatus.Invited => 4, _ => -1 };
        if (r.Remaining != tail) throw new InvalidDataException("Invalid battlefield status tail");
        uint time1 = r.ReadU32(); uint time2 = tail == 8 ? r.ReadU32() : 0;
        return new(slot, map, bracket, instance, status, time1, time2);
    }
}

/// <summary>Main-character queue state. A status request emits only occupied slots; silence is not a clear.</summary>
public sealed class BattlefieldQueueState
{
    public sealed record Entry(BattlefieldStatusPacket Packet, double ReceivedAt)
    {
        private double AgeMilliseconds(double now) => Math.Max(0, now - ReceivedAt) * 1000;
        public double RemainingMilliseconds(double now) => Math.Max(0, Packet.Time1 - AgeMilliseconds(now));
        public double ElapsedMilliseconds(double now) => Packet.Time2 + AgeMilliseconds(now);
        public bool CanEnter(double now) => Packet.Status == BattlefieldStatus.Invited && RemainingMilliseconds(now) > 0;
    }
    private readonly Entry?[] _slots = new Entry?[3];
    public Entry? this[int slot] => slot >= 0 && slot < 3 ? _slots[slot] : null;
    public void Apply(BattlefieldStatusPacket packet, double now) =>
        _slots[packet.Slot] = packet.Status == BattlefieldStatus.None ? null : new(packet, now);
    public void Clear() => Array.Clear(_slots);
}
