namespace MSUIClient.Net;

public readonly record struct RaidGroupOnlyPacket(uint TimerMs, uint Error);

public sealed class InstanceBootState
{
    private readonly Dictionary<ulong, double> _deadlines = [];

    public static RaidGroupOnlyPacket Parse(byte[] body)
    {
        if (body.Length != 8) throw new InvalidDataException("bad SMSG_RAID_GROUP_ONLY body");
        var reader = new PacketReader(body);
        return new(reader.ReadU32(), reader.ReadU32());
    }

    public bool Apply(ulong owner, RaidGroupOnlyPacket packet, double now)
    {
        bool wasCounting = Remaining(owner, now) > 0;
        if (owner != 0)
        {
            if (packet.TimerMs == 0) _deadlines.Remove(owner);
            else _deadlines[owner] = now + packet.TimerMs / 1000.0;
        }
        return wasCounting;
    }

    public double Remaining(ulong owner, double now) =>
        _deadlines.TryGetValue(owner, out double deadline) ? Math.Max(0, deadline - now) : 0;

    public void Clear() => _deadlines.Clear();
}
