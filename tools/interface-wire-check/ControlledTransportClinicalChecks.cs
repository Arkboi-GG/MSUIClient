using System.Numerics;
using MSUIClient;
using MSUIClient.Engine;
using MSUIClient.Net;

internal static class ControlledTransportClinicalChecks
{
    public static void Run()
    {
        TransportRiderLaw.WorldPose world = TransportRiderLaw.Compose(
            new Vector3(100f, 200f, 30f), MathF.PI * 0.5f,
            new Vector3(2f, 1f, 4f), MathF.PI * 0.25f);
        Check(Vector3.Distance(world.Position, new Vector3(99f, 202f, 34f)) < 1e-4f &&
              MathF.Abs(world.Orientation - MathF.PI * 0.75f) < 1e-4f,
            "controlled platform composition drifted");

        var original = MovementInfo.Create(new Vector3(10f, 20f, 30f), 1.25f,
            MovementFlags.Forward | MovementFlags.OnTransport);
        original.Transport = new TransportPose(0xF130000012345678,
            new Vector3(3f, -2f, 1.5f), 0.75f);
        var writer = new PacketWriter();
        original.Write(writer);
        MovementInfo decoded = MovementInfo.Read(new PacketReader(writer.ToArray()));
        Check(decoded.Transport == original.Transport &&
              ((MovementFlags)decoded.Flags & MovementFlags.OnTransport) != 0,
            "ON_TRANSPORT wire tail did not round-trip");

        string root = ClientConfig.FindRepoRoot();
        string program = SourceText.Read(Path.Combine(root, "MSUIClient", "Program.cs"));
        string transports = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop",
            "Scene", "GameLoop.Transports.cs"));
        string sender = SourceText.Read(Path.Combine(root, "MSUIClient", "Net",
            "LocalMovementSender.cs"));
        string net = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop",
            "Scene", "GameLoop.Net.cs"));
        string wmo = SourceText.Read(Path.Combine(root, "MSUIClient", "World", "Wmo",
            "WmoRenderer.cs"));
        string doodads = SourceText.Read(Path.Combine(root, "MSUIClient", "World", "Doodads",
            "DoodadRenderer.cs"));
        Check(program.IndexOf("UpdateGameObjectTransports();", StringComparison.Ordinal) <
              program.IndexOf("_controller.Update(dt, input)", StringComparison.Ordinal) &&
              program.Contains("CarryControlledTransportRider();", StringComparison.Ordinal) &&
              program.Contains("ReconcileControlledTransportRider();", StringComparison.Ordinal) &&
              transports.Contains("ProbeMovingTransportGround", StringComparison.Ordinal) &&
              transports.Contains("ride.LocalPosition", StringComparison.Ordinal) &&
              transports.Contains("TryUpdateDynamicTransform(go.Guid", StringComparison.Ordinal) &&
              sender.Contains("MovementFlags.OnTransport", StringComparison.Ordinal) &&
              sender.Contains("info.Transport = controller.Transport", StringComparison.Ordinal) &&
              net.Contains("_pendingTransfer = transfer", StringComparison.Ordinal) &&
              net.Contains("_entities.ClearExcept(liveRide.Guid)", StringComparison.Ordinal) &&
              net.Contains("crossingRide.LocalPosition, enter.Orientation", StringComparison.Ordinal) &&
              wmo.Contains("TryRaycastDynamicCollision", StringComparison.Ordinal) &&
              wmo.Contains("if (instance.DynamicGuid != 0) continue;", StringComparison.Ordinal) &&
              doodads.Contains("TryRaycastDynamicCollision", StringComparison.Ordinal) &&
              doodads.Contains("LiveCollision = liveCollision", StringComparison.Ordinal),
            "controlled transport production wiring drifted");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
