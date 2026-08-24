using System.Numerics;
using MSUIClient;
using MSUIClient.Net;

internal static class RemoteMovementClinicalChecks
{
    public static void Run()
    {
        Op[] exactFamily =
        [
            Op.MSG_MOVE_START_FORWARD, Op.MSG_MOVE_START_BACKWARD, Op.MSG_MOVE_STOP,
            Op.MSG_MOVE_START_STRAFE_LEFT, Op.MSG_MOVE_START_STRAFE_RIGHT,
            Op.MSG_MOVE_STOP_STRAFE, Op.MSG_MOVE_JUMP, Op.MSG_MOVE_START_TURN_LEFT,
            Op.MSG_MOVE_START_TURN_RIGHT, Op.MSG_MOVE_STOP_TURN,
            Op.MSG_MOVE_START_PITCH_UP, Op.MSG_MOVE_START_PITCH_DOWN,
            Op.MSG_MOVE_STOP_PITCH, Op.MSG_MOVE_SET_RUN_MODE, Op.MSG_MOVE_SET_WALK_MODE,
            Op.MSG_MOVE_FALL_LAND, Op.MSG_MOVE_START_SWIM, Op.MSG_MOVE_STOP_SWIM,
            Op.MSG_MOVE_SET_FACING, Op.MSG_MOVE_SET_PITCH, Op.MSG_MOVE_HEARTBEAT,
        ];
        Check(exactFamily.All(MovementRelayPackets.IsRelayOpcode) &&
              !MovementRelayPackets.IsRelayOpcode(Op.MSG_MOVE_TELEPORT_ACK),
            "relayed MSG_MOVE_* opcode family drift");

        const ulong guid = 0xF130000012340001;
        var movement = new MovementInfo
        {
            Flags = (uint)(MovementFlags.Forward | MovementFlags.Swimming),
            Timestamp = 987,
            Position = new Vector3(1, 2, 3),
            Orientation = 1.25f,
            Pitch = -.2f,
            FallTime = 42,
        };
        var writer = new PacketWriter();
        writer.WritePackedGuid(guid);
        movement.Write(writer);
        MovementRelay parsed = MovementRelayPackets.Parse(
            Op.MSG_MOVE_HEARTBEAT, writer.ToArray());
        Check(parsed.Guid == guid && parsed.Movement.Flags == movement.Flags &&
              parsed.Movement.Timestamp == 987 && parsed.Movement.Position == movement.Position &&
              parsed.Movement.Orientation == 1.25f && parsed.Movement.Pitch == -.2f &&
              parsed.Movement.FallTime == 42,
            "packed-guid plus MovementInfo relay decode drift");
        ExpectInvalid(() => MovementRelayPackets.Parse(
            Op.MSG_MOVE_TELEPORT_ACK, writer.ToArray()));
        ExpectInvalid(() => MovementRelayPackets.Parse(
            Op.MSG_MOVE_HEARTBEAT, [.. writer.ToArray(), 0]));

        var store = new EntityStore();
        var player = new WorldEntity
        {
            Guid = guid,
            Type = ObjectTypeId.Player,
            Position = Vector3.Zero,
        };
        store.AddSynthetic(player);
        store.ApplyRemotePlayerMove(guid, new MovementInfo
        {
            Flags = (uint)MovementFlags.Forward,
            Position = Vector3.Zero,
            Orientation = 1f,
        }, nowMs: 100);
        store.ApplyRemotePlayerMove(guid, new MovementInfo
        {
            Flags = (uint)MovementFlags.Forward,
            Position = new Vector3(5, 0, 0),
            Orientation = 1.5f,
        }, nowMs: 600);
        store.TickSplines(850);
        Check(MathF.Abs(player.Position.X - 2.5f) < .001f &&
              MathF.Abs(player.Orientation - 1.5f) < .001f && player.IsMoving,
            "remote player relay did not interpolate position while preserving authoritative aim");
        store.ApplyRemotePlayerMove(guid, new MovementInfo
        {
            Flags = 0,
            Position = new Vector3(5, 0, 0),
            Orientation = 2f,
        }, nowMs: 1_100);
        Check(player.Position == new Vector3(5, 0, 0) && player.Orientation == 2f &&
              !player.IsMoving,
            "remote player stop relay must snap to the authoritative pose");

        const uint onTransport = (uint)MovementFlags.OnTransport;
        uint merged = ServerAuthoredMovementLaw.MergeFlags(
            onTransport | (uint)MovementFlags.Forward,
            (uint)(MovementFlags.Backward | MovementFlags.WaterWalking));
        Check((merged & onTransport) != 0 &&
              (merged & (uint)MovementFlags.Forward) == 0 &&
              (merged & (uint)MovementFlags.Backward) != 0 &&
              (merged & (uint)MovementFlags.WaterWalking) != 0 &&
              MathF.Abs(ServerAuthoredMovementLaw.FacingDelta(6.1f, 0.1f) -
                        (0.1f + MathF.Tau - 6.1f)) < .0001f,
            "server-authored self-move flag merge or shortest-facing-delta law drift");
        store.ApplyServerAuthoredMove(guid, new MovementInfo
        {
            Flags = (uint)MovementFlags.Backward,
            Position = new Vector3(80f, 90f, 10f),
            Orientation = 0.2f,
        }, 2_000);
        Check(player.Position == new Vector3(80f, 90f, 10f) &&
              player.Orientation == 0.2f && !player.IsMoving,
            "server-authored self move did not hard-snap the entity pose");

        string root = ClientConfig.FindRepoRoot();
        string dispatch = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Net.cs"));
        Check(dispatch.Contains("MovementRelayPackets.Parse((Op)opcode, body)",
                  StringComparison.Ordinal) &&
              dispatch.Contains("relay.Guid == ControlledGuid && ControllerOwnsControlledBodyPose",
                  StringComparison.Ordinal) &&
              dispatch.Contains("ApplyServerAuthoredSelfMove(relay)", StringComparison.Ordinal) &&
              dispatch.Contains("_entities.ApplyRemotePlayerMove(", StringComparison.Ordinal),
            "movement relay remote/self/free-view dispatch split drift");
        string selfMove = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.ServerAuthoredMovement.cs"));
        Check(selfMove.Contains("ServerAuthoredMovementLaw.MergeFlags", StringComparison.Ordinal) &&
              selfMove.Contains("_controller.Teleport", StringComparison.Ordinal) &&
              selfMove.Contains("_window.Camera.Yaw", StringComparison.Ordinal) &&
              selfMove.Contains("_window.Camera.Target = _controller.Position",
                  StringComparison.Ordinal) &&
              !selfMove.Contains("SendMovement", StringComparison.Ordinal) &&
              !selfMove.Contains("TeleportAck", StringComparison.Ordinal),
            "bare self-addressed move lost hard-snap/orbit/no-ack semantics");
    }

    private static void ExpectInvalid(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidDataException("malformed movement relay was accepted");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
