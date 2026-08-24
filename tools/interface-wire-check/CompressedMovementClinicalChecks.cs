using System.IO.Compression;
using System.Numerics;
using MSUIClient;
using MSUIClient.Net;

internal static class CompressedMovementClinicalChecks
{
    public static void Run()
    {
        byte[] first = RelayBody(8, new Vector3(1, 2, 3), MovementFlags.Forward);
        byte[] second = RelayBody(9, new Vector3(4, 5, 6), MovementFlags.None);
        var records = new PacketWriter();
        WriteRecord(records, Op.MSG_MOVE_START_FORWARD, first);
        WriteRecord(records, Op.MSG_MOVE_STOP, second);
        IReadOnlyList<CompressedMovementRelay> parsed =
            CompressedMovementPackets.Parse(Envelope(records.ToArray()));
        Check(parsed.Count == 2 &&
              parsed[0].Opcode == Op.MSG_MOVE_START_FORWARD &&
              parsed[0].Relay.Guid == 8 &&
              parsed[0].Relay.Movement.Position == new Vector3(1, 2, 3) &&
              parsed[1].Opcode == Op.MSG_MOVE_STOP && parsed[1].Relay.Guid == 9,
            "compressed movement batch did not preserve record order/opcodes/relays");

        ExpectInvalid(() => CompressedMovementPackets.Parse(Envelope([1, 0])));
        var nested = new PacketWriter();
        nested.WriteU8(2);
        nested.WriteU16((ushort)Op.SMSG_COMPRESSED_MOVES);
        ExpectInvalid(() => CompressedMovementPackets.Parse(Envelope(nested.ToArray())));
        var foreign = new PacketWriter();
        foreign.WriteU8(2);
        foreign.WriteU16((ushort)Op.SMSG_WEATHER);
        ExpectInvalid(() => CompressedMovementPackets.Parse(Envelope(foreign.ToArray())));

        string root = ClientConfig.FindRepoRoot();
        string dispatch = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Net.cs"));
        Check(dispatch.Contains("case Op.SMSG_COMPRESSED_MOVES:", StringComparison.Ordinal) &&
              dispatch.Contains("CompressedMovementPackets.Parse(body)", StringComparison.Ordinal) &&
              dispatch.Contains("compressed.Relay", StringComparison.Ordinal) &&
              dispatch.Contains("relay.Guid == ControlledGuid && ControllerOwnsControlledBodyPose",
                  StringComparison.Ordinal) &&
              dispatch.Contains("ApplyServerAuthoredSelfMove(relay)", StringComparison.Ordinal),
            "compressed movement dispatch or self/observer split drift");
    }

    private static byte[] RelayBody(ulong guid, Vector3 position, MovementFlags flags)
    {
        var writer = new PacketWriter();
        writer.WritePackedGuid(guid);
        new MovementInfo
        {
            Flags = (uint)flags,
            Timestamp = 123,
            Position = position,
            Orientation = .5f,
            FallTime = 0,
        }.Write(writer);
        return writer.ToArray();
    }

    private static void WriteRecord(PacketWriter writer, Op opcode, byte[] body)
    {
        writer.WriteU8(checked((byte)(body.Length + 2)));
        writer.WriteU16((ushort)opcode);
        writer.WriteBytes(body);
    }

    private static byte[] Envelope(byte[] records)
    {
        using var output = new MemoryStream();
        using (var binary = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true))
            binary.Write(records.Length);
        using (var zlib = new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true))
            zlib.Write(records);
        return output.ToArray();
    }

    private static void ExpectInvalid(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        catch (EndOfStreamException) { return; }
        throw new InvalidDataException("malformed compressed movement batch was accepted");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
