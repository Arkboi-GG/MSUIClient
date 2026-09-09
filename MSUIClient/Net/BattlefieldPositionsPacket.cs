using System.Numerics;

namespace MSUIClient.Net;

public readonly record struct BattlefieldPosition(ulong Guid, Vector2 Position);
public sealed record BattlefieldPositionsPacket(IReadOnlyList<BattlefieldPosition> Teammates, BattlefieldPosition? FriendlyFlagCarrier)
{
    public static BattlefieldPositionsPacket Parse(byte[] body)
    {
        var r = new PacketReader(body);
        if (r.Remaining < 5) throw new InvalidDataException("Short battlefield positions");
        uint count = r.ReadU32();
        if (count > 80 || count > (r.Remaining - 1) / 16) throw new InvalidDataException("Invalid battlefield position count");
        BattlefieldPosition Read()
        {
            ulong guid = r.ReadU64(); float x = r.ReadF32(), y = r.ReadF32();
            if (guid == 0 || !float.IsFinite(x) || !float.IsFinite(y)) throw new InvalidDataException("Invalid battlefield position");
            return new(guid, new(x,y));
        }
        var teammates = new BattlefieldPosition[count]; var seen = new HashSet<ulong>();
        for (int i = 0; i < teammates.Length; i++)
        {
            teammates[i] = Read();
            if (!seen.Add(teammates[i].Guid)) throw new InvalidDataException("Duplicate battlefield position");
        }
        byte flags = r.ReadU8();
        if (flags > 1 || r.Remaining != flags * 16) throw new InvalidDataException("Invalid friendly flag-carrier tail");
        BattlefieldPosition? carrier = flags == 1 ? Read() : null;
        return new(Array.AsReadOnly(teammates), carrier);
    }
}
