using System.Numerics;
using MSUIClient;
using MSUIClient.Net;

internal static class SelfSplineClinicalChecks
{
    public static void Run()
    {
        var info = new MovementInfo
        {
            Flags = 0,
            Timestamp = 0x11223344,
            Position = new Vector3(1, 2, 3),
            Orientation = .5f,
            FallTime = 0x55667788,
        };
        byte[] body = WorldSession.BuildMoveSplineDoneBody(info, 0xAABBCCDD);
        Check(body.SequenceEqual(Convert.FromHexString(
                "00000000443322110000803F00000040000040400000003F88776655" +
                "DDCCBBAA0000803F")),
            "CMSG_MOVE_SPLINE_DONE MovementInfo/id/completion-fraction golden drift");

        var packet = new PacketWriter();
        packet.WritePackedGuid(8);
        packet.WriteVector3(Vector3.Zero);
        packet.WriteU32(77);
        packet.WriteU8(0);
        packet.WriteU32(0);
        packet.WriteU32(100);
        packet.WriteU32(1);
        packet.WriteVector3(new Vector3(10, 0, 0));
        MonsterMove move = MonsterMoveParser.Parse(packet.ToArray()) ??
            throw new InvalidDataException("self spline fixture did not parse");
        Check(move.SplineId == 77, "SMSG_MONSTER_MOVE spline id was not retained");
        var ride = new CreatureSpline(move.Points, move.DurationMs, false, 1_000,
            move.SplineId);
        bool running = ride.Sample(1_100, out Vector3 endpoint, out _);
        Check(!running && endpoint == new Vector3(10, 0, 0) && ride.Id == 77,
            "self ride did not finish with the server's spline id at its endpoint");

        string root = ClientConfig.FindRepoRoot();
        string taxi = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Taxi.cs"));
        string loop = SourceText.Read(Path.Combine(root, "MSUIClient", "Program.cs"));
        string dispatch = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Net.cs"));
        Check(taxi.Contains("ObserveServerRideSpline(MonsterMove move)", StringComparison.Ordinal) &&
              taxi.Contains("_serverRideStoppedId = move.SplineId", StringComparison.Ordinal) &&
              taxi.Contains("uint completedId = ride.Id", StringComparison.Ordinal) &&
              taxi.Contains("_net?.MoveSplineDone(movement, splineId)", StringComparison.Ordinal) &&
              loop.Contains("bool serverRideActive = UpdateServerRide()", StringComparison.Ordinal) &&
              loop.Contains("if (!serverRideActive) _controller.Update(dt, input)",
                  StringComparison.Ordinal) &&
              dispatch.Contains("AbortServerRideForTeleport();", StringComparison.Ordinal),
            "self ride ownership, newest-stop id, physics park, ack, or teleport-abort drift");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
