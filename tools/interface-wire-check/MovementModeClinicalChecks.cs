using System.Numerics;
using MSUIClient;
using MSUIClient.Net;

internal static class MovementModeClinicalChecks
{
    public static void Run()
    {
        byte[] body = Convert.FromHexString("012a07000000");
        (Op Opcode, MovementModeKind Kind, bool Apply)[] cases =
        [
            (Op.SMSG_FORCE_MOVE_ROOT, MovementModeKind.Root, true),
            (Op.SMSG_FORCE_MOVE_UNROOT, MovementModeKind.Root, false),
            (Op.SMSG_MOVE_WATER_WALK, MovementModeKind.WaterWalk, true),
            (Op.SMSG_MOVE_LAND_WALK, MovementModeKind.WaterWalk, false),
            (Op.SMSG_MOVE_FEATHER_FALL, MovementModeKind.FeatherFall, true),
            (Op.SMSG_MOVE_NORMAL_FALL, MovementModeKind.FeatherFall, false),
            (Op.SMSG_MOVE_SET_HOVER, MovementModeKind.Hover, true),
            (Op.SMSG_MOVE_UNSET_HOVER, MovementModeKind.Hover, false),
        ];
        foreach ((Op opcode, MovementModeKind kind, bool apply) in cases)
            Check(MovementModePackets.Parse(opcode, body) ==
                      new MovementModeChange(0x2A, kind, 7, apply),
                $"movement-mode parse drift for {opcode}");

        Check(MovementModePackets.Flag(MovementModeKind.Root) == MovementFlags.Root &&
              MovementModePackets.Flag(MovementModeKind.WaterWalk) ==
                  (MovementFlags)0x1000_0000 &&
              MovementModePackets.Flag(MovementModeKind.FeatherFall) ==
                  (MovementFlags)0x2000_0000 &&
              MovementModePackets.Flag(MovementModeKind.Hover) ==
                  (MovementFlags)0x4000_0000,
            "movement-mode flag map drift");

        var info = new MovementInfo
        {
            Flags = 0,
            Timestamp = 0x1122_3344,
            Position = new Vector3(1, 2, 3),
            Orientation = 0.5f,
            FallTime = 0,
        };
        byte[] rootAck = WorldSession.BuildMoveRootAckBody(0x2A, 7, info);
        byte[] modeAck = WorldSession.BuildMoveModeAckBody(0x2A, 7, info, true);
        Check(modeAck.Length == rootAck.Length + 4 &&
              modeAck.AsSpan(0, rootAck.Length).SequenceEqual(rootAck) &&
              modeAck.AsSpan(rootAck.Length).SequenceEqual(new byte[] { 1, 0, 0, 0 }),
            "non-root movement-mode apply-tail layout drift");
        Check(MovementModePackets.AckOpcode(MovementModeKind.Root, true) ==
                  Op.CMSG_FORCE_MOVE_ROOT_ACK &&
              MovementModePackets.AckOpcode(MovementModeKind.Root, false) ==
                  Op.CMSG_FORCE_MOVE_UNROOT_ACK &&
              MovementModePackets.AckOpcode(MovementModeKind.WaterWalk, true) ==
                  Op.CMSG_MOVE_WATER_WALK_ACK &&
              MovementModePackets.AckOpcode(MovementModeKind.FeatherFall, false) ==
                  Op.CMSG_MOVE_FEATHER_FALL_ACK &&
              MovementModePackets.AckOpcode(MovementModeKind.Hover, true) ==
                  Op.CMSG_MOVE_HOVER_ACK,
            "movement-mode ack opcode map drift");

        string root = ClientConfig.FindRepoRoot();
        string dispatch = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Net.cs"));
        string modes = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.MovementModes.cs"));
        string controller = SourceText.Read(Path.Combine(root, "MSUIClient", "Player",
            "CharacterController.cs"));
        string sender = SourceText.Read(Path.Combine(root, "MSUIClient", "Net",
            "LocalMovementSender.cs"));
        string runtime = SourceText.Read(Path.Combine(root, "MSUIClient", "Program.cs"));
        Check(dispatch.Contains("case Op.SMSG_MOVE_UNSET_HOVER", StringComparison.Ordinal) &&
              modes.Contains("MoveModeAck", StringComparison.Ordinal) &&
              controller.Contains("FeatherFalling ? 7f", StringComparison.Ordinal) &&
              controller.Contains("liquid-water-walk", StringComparison.Ordinal) &&
              controller.Contains("groundZ += 1f", StringComparison.Ordinal) &&
              sender.Contains("controller.GrantedMovementFlags", StringComparison.Ordinal) &&
              sender.Contains("flags |= serverGrantedFlags;", StringComparison.Ordinal) &&
              sender.Contains("flags &= ~RootIncompatibleFlags;", StringComparison.Ordinal) &&
              runtime.Contains(
                  "_movementRooted ? MovementFlags.Root : MovementFlags.None",
                  StringComparison.Ordinal),
            "movement-mode apply/effect/outbound-flag wiring drift");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
