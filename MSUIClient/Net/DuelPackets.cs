namespace MSUIClient.Net;

public readonly record struct DuelRequestedWire(ulong Arbiter, ulong Challenger);
public readonly record struct DuelWinnerWire(bool Fled, string Winner, string Loser);

/// <summary>Byte-exact build-5875 duel packet readers and reply body.</summary>
public static class DuelPackets
{
    public static DuelRequestedWire ParseRequested(byte[] body)
    {
        var reader = new PacketReader(body);
        var result = new DuelRequestedWire(reader.ReadU64(), reader.ReadU64());
        RequireConsumed(reader, nameof(Op.SMSG_DUEL_REQUESTED));
        return result;
    }

    public static void ParseEmpty(byte[] body, Op opcode)
    {
        if (body.Length != 0)
            throw new InvalidDataException($"{opcode} trailing bytes {body.Length}");
    }

    public static bool ParseComplete(byte[] body)
    {
        var reader = new PacketReader(body);
        bool started = reader.ReadU8() != 0;
        RequireConsumed(reader, nameof(Op.SMSG_DUEL_COMPLETE));
        return started;
    }

    public static DuelWinnerWire ParseWinner(byte[] body)
    {
        int winnerEnd = Array.IndexOf(body, (byte)0, 1);
        int loserEnd = winnerEnd < 0 ? -1 : Array.IndexOf(body, (byte)0, winnerEnd + 1);
        if (winnerEnd < 0 || loserEnd < 0)
            throw new InvalidDataException($"{Op.SMSG_DUEL_WINNER} has an unterminated name");
        var reader = new PacketReader(body);
        var result = new DuelWinnerWire(reader.ReadU8() != 0,
            reader.ReadCString(), reader.ReadCString());
        RequireConsumed(reader, nameof(Op.SMSG_DUEL_WINNER));
        return result;
    }

    public static uint ParseCountdownSeconds(byte[] body)
    {
        var reader = new PacketReader(body);
        uint seconds = reader.ReadU32() / 1000;
        RequireConsumed(reader, nameof(Op.SMSG_DUEL_COUNTDOWN));
        return seconds;
    }

    public static byte[] BuildReplyBody(ulong arbiter)
    {
        var writer = new PacketWriter(8);
        writer.WriteU64(arbiter);
        return writer.ToArray();
    }

    private static void RequireConsumed(PacketReader reader, string packet)
    {
        if (reader.Remaining != 0)
            throw new InvalidDataException($"{packet} trailing bytes {reader.Remaining}");
    }
}
