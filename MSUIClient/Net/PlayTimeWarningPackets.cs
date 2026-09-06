namespace MSUIClient.Net;

public readonly record struct PlayTimeWarningPacket(uint Flag, int SecondsLeft);

public static class PlayTimeWarningPackets
{
    public static PlayTimeWarningPacket Parse(byte[] body)
    {
        if (body.Length != 8) throw new InvalidDataException("Play-time warning requires flag and signed seconds");
        var r = new PacketReader(body);
        return new(r.ReadU32(), r.ReadI32());
    }
}
