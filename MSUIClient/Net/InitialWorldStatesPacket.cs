namespace MSUIClient.Net;

public sealed record InitialWorldStatesPacket(uint Map, uint Zone, IReadOnlyList<(uint Id, uint Value)> Values)
{
    public static InitialWorldStatesPacket Parse(byte[] body)
    {
        if (body.Length < 10) throw new InvalidDataException("Short world-state init");
        var reader = new PacketReader(body);
        uint map = reader.ReadU32(), zone = reader.ReadU32(); ushort count = reader.ReadU16();
        if (reader.Remaining != count * 8) throw new InvalidDataException("Invalid world-state init count");
        var values = new (uint Id, uint Value)[count];
        for (int i = 0; i < count; i++) values[i] = (reader.ReadU32(), reader.ReadU32());
        return new(map, zone, Array.AsReadOnly(values));
    }
}
