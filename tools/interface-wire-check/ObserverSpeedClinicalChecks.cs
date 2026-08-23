using System.Numerics;
using MSUIClient;
using MSUIClient.Net;

internal static class ObserverSpeedClinicalChecks
{
    public static void Run()
    {
        ObserverSpeedChange spline = MovementSpeedPackets.ParseObserverSpeedChange(
            Op.SMSG_SPLINE_SET_RUN_SPEED, Convert.FromHexString("010800006041"));
        Check(spline == new ObserverSpeedChange(
                  8, MovementSpeedKind.Run, 14f, null),
            "SMSG_SPLINE_SET_* packed-guid/f32 shape drift");

        var info = new MovementInfo
        {
            Flags = (uint)MovementFlags.Forward,
            Timestamp = 42,
            Position = new Vector3(4, 5, 6),
            Orientation = .75f,
            FallTime = 3,
        };
        var writer = new PacketWriter();
        writer.WritePackedGuid(8);
        info.Write(writer);
        writer.WriteF32(3.5f);
        ObserverSpeedChange move = MovementSpeedPackets.ParseObserverSpeedChange(
            Op.MSG_MOVE_SET_SWIM_SPEED, writer.ToArray());
        Check(move.Guid == 8 && move.Kind == MovementSpeedKind.Swim && move.Speed == 3.5f &&
              move.Movement is { } parsed && parsed.Flags == info.Flags &&
              parsed.Position == info.Position && parsed.Orientation == info.Orientation,
            "MSG_MOVE_SET_* packed-guid/MovementInfo/f32 shape drift");

        ExpectInvalid(() => MovementSpeedPackets.ParseObserverSpeedChange(
            Op.SMSG_SPLINE_SET_RUN_SPEED, Convert.FromHexString("01080000C07F")));
        ExpectInvalid(() => MovementSpeedPackets.ParseObserverSpeedChange(
            Op.MSG_MOVE_HEARTBEAT, writer.ToArray()));

        string root = ClientConfig.FindRepoRoot();
        string dispatch = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Net.cs"));
        string apply = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.MovementSpeed.cs"));
        Check(dispatch.Contains("case Op.SMSG_SPLINE_SET_TURN_RATE", StringComparison.Ordinal) &&
              dispatch.Contains("case Op.MSG_MOVE_SET_TURN_RATE", StringComparison.Ordinal) &&
              dispatch.Contains("ApplyObserverSpeedChange(net, (Op)opcode, body)",
                  StringComparison.Ordinal) &&
              apply.Contains("change.Movement is { } movement", StringComparison.Ordinal) &&
              apply.Contains("ApplyEntitySpeed(change.Guid, change.Kind, change.Speed)",
                  StringComparison.Ordinal) &&
              !apply.Contains("ObserverSpeedChangeAck", StringComparison.Ordinal),
            "observer speed-change dispatch, pose leg, application, or no-ack law drift");
    }

    private static void ExpectInvalid(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidDataException("invalid observer speed packet was accepted");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
