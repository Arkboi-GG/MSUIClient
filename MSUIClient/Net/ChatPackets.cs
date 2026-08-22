using System.Text;

namespace MSUIClient.Net;

/// <summary>Decoded build-5875 <c>SMSG_MESSAGECHAT</c> body.</summary>
public sealed record ChatMessagePacket(
    byte Type,
    uint Language,
    ulong SenderGuid,
    ulong TargetGuid,
    string SenderName,
    string Channel,
    string Text,
    byte ChatTag);

/// <summary>The exact per-type wire shapes used by VMaNGOS/Benilla chat.</summary>
public static class ChatPackets
{
    public readonly record struct PlayedTime(uint Total, uint Level);
    public readonly record struct RandomRoll(uint Minimum, uint Maximum, uint Result, ulong Guid);

    /// <summary>
    /// CMSG_CHAT_IGNORED is a raw, unpacked build-5875 GUID. It is emitted only when an
    /// incoming whisper is suppressed by the local ignore list.
    /// </summary>
    public static byte[] BuildIgnoredBody(ulong senderGuid)
    {
        var w = new PacketWriter(8);
        w.WriteU64(senderGuid);
        return w.ToArray();
    }

    public static ChatMessagePacket ParseMessage(byte[] body)
    {
        var r = new PacketReader(body);
        byte type = r.ReadU8();
        uint language = r.ReadU32();
        ulong senderGuid = 0, targetGuid = 0;
        string senderName = "", channel = "";

        switch (type)
        {
            case 0x0D or 0x1A or 0x59 or 0x5A:
                senderName = ReadLenString(r);
                targetGuid = r.ReadU64();
                break;
            case 0x00 or 0x01 or 0x05:
                senderGuid = r.ReadU64();
                targetGuid = r.ReadU64(); // same sender GUID in the reference packet
                break;
            case 0x0B or 0x0C:
                senderGuid = r.ReadU64();
                senderName = ReadLenString(r);
                targetGuid = r.ReadU64();
                break;
            case 0x0E:
                channel = r.ReadCString();
                r.ReadU32(); // player rank
                senderGuid = r.ReadU64();
                break;
            default:
                senderGuid = r.ReadU64();
                break;
        }

        string text = ReadLenString(r);
        byte chatTag = r.ReadU8();
        if (r.Remaining != 0)
            throw new InvalidDataException($"SMSG_MESSAGECHAT has {r.Remaining} trailing byte(s)");
        return new ChatMessagePacket(type, language, senderGuid, targetGuid,
            senderName, channel, text, chatTag);
    }

    public static string ParsePlayerNotFound(byte[] body)
    {
        var r = new PacketReader(body);
        string name = r.ReadCString();
        RequireEmpty(r, "SMSG_CHAT_PLAYER_NOT_FOUND");
        return name;
    }

    public static PlayedTime ParsePlayedTime(byte[] body)
    {
        var r = new PacketReader(body);
        var value = new PlayedTime(r.ReadU32(), r.ReadU32());
        RequireEmpty(r, "SMSG_PLAYED_TIME");
        return value;
    }

    public static RandomRoll ParseRandomRoll(byte[] body)
    {
        var r = new PacketReader(body);
        var value = new RandomRoll(r.ReadU32(), r.ReadU32(), r.ReadU32(), r.ReadU64());
        RequireEmpty(r, "MSG_RANDOM_ROLL");
        return value;
    }

    private static string ReadLenString(PacketReader r)
    {
        uint wireLength = r.ReadU32();
        if (wireLength == 0) return "";
        if (wireLength > int.MaxValue || wireLength > r.Remaining)
            throw new InvalidDataException($"chat string length {wireLength} exceeds remaining {r.Remaining}");
        byte[] bytes = r.ReadBytes((int)wireLength);
        int textLength = bytes.Length > 0 && bytes[^1] == 0 ? bytes.Length - 1 : bytes.Length;
        return Encoding.UTF8.GetString(bytes, 0, textLength);
    }

    private static void RequireEmpty(PacketReader r, string packet)
    {
        if (r.Remaining != 0)
            throw new InvalidDataException($"{packet} has {r.Remaining} trailing byte(s)");
    }
}
