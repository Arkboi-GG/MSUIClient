namespace MSUIClient.Net;

public static class AreaTriggerPackets
{
    public static byte[] BuildReport(uint triggerId)
    {
        var writer = new PacketWriter(4);
        writer.WriteU32(triggerId);
        return writer.ToArray();
    }

    public static string ParseMessage(byte[] body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var reader = new PacketReader(body);
        reader.ReadU32(); // redundant cstring length, including its terminator
        string text = reader.ReadCString();
        if (reader.Remaining != 0)
            throw new InvalidDataException(
                $"SMSG_AREA_TRIGGER_MESSAGE has {reader.Remaining} trailing byte(s)");
        return text;
    }
}
