using System.Numerics;
using MSUIClient.Net;

internal static class MonsterMoveClinicalChecks
{
    public static void Run()
    {
        Vector3 start = new(4, 8, 0), endpoint = new(12, 8, 0);
        var direct = Head(0x77, start, moveType: 0);
        direct.WriteU32(0);       // ground spline flags
        direct.WriteU32(1_000);   // duration
        direct.WriteU32(2);       // producer announces two points
        direct.WriteVector3(endpoint); // and writes no packed offsets
        MonsterMove hop = MonsterMoveParser.Parse(direct.ToArray()) ??
            throw new InvalidDataException("two-point monster move was dropped");
        Check(hop.Points.SequenceEqual(new[] { start, endpoint }) && hop.DurationMs == 1_000,
            "count==2 direct-hop law drift");

        Vector3 spot = new(4, 18, 3);
        var spotBody = Head(0x88, start, moveType: 2);
        spotBody.WriteVector3(spot);
        AppendEndpointSpline(spotBody, endpoint);
        MonsterMove spotMove = MonsterMoveParser.Parse(spotBody.ToArray()) ??
            throw new InvalidDataException("spot-facing monster move was dropped");
        Check(spotMove.Facing.Kind == MonsterMoveFacingKind.Spot &&
              Near(MonsterMoveFacingLaw.Resolve(spotMove.Facing, start, _ => null),
                  MathF.PI / 2f),
            "moveType 2 spot-facing law drift");

        var targetBody = Head(0x99, start, moveType: 3);
        targetBody.WriteU64(0x1234);
        AppendEndpointSpline(targetBody, endpoint);
        MonsterMove targetMove = MonsterMoveParser.Parse(targetBody.ToArray()) ??
            throw new InvalidDataException("target-facing monster move was dropped");
        float? targetFacing = MonsterMoveFacingLaw.Resolve(targetMove.Facing, start,
            guid => guid == 0x1234 ? new Vector3(14, 8, 9) : null);
        Check(targetMove.Facing.TargetGuid == 0x1234 && Near(targetFacing, 0),
            "moveType 3 target-facing law drift");

        var entities = new EntityStore();
        var mover = new WorldEntity { Guid = 0x99, Type = ObjectTypeId.Unit, Position = start };
        var target = new WorldEntity
            { Guid = 0x1234, Type = ObjectTypeId.Player, Position = new Vector3(4, 18, 0) };
        entities.AddSynthetic(mover);
        entities.AddSynthetic(target);
        entities.ApplyMonsterMove(targetMove, nowMs: 1);
        Check(Near(mover.Orientation, MathF.PI / 2f),
            "EntityStore did not apply the dictated target-facing receipt snap");

        CheckCreateTimeSplineResume();
        CheckUpdateObjectTransportSurfaces();
    }

    private static void CheckUpdateObjectTransportSurfaces()
    {
        const ulong riderGuid = 0xAA;
        const ulong transportGuid = 0x1FC000000002B074;
        const ulong goGuid = 0xF1100000000000BB;
        var writer = new PacketWriter();
        writer.WriteU32(2);
        writer.WriteU8(1); // packet contains transport-related create data

        writer.WriteU8((byte)UpdateKind.CreateObject);
        writer.WritePackedGuid(riderGuid);
        writer.WriteU8((byte)ObjectTypeId.Unit);
        writer.WriteU8(0x20); // UPDATE_FLAG_LIVING
        writer.WriteU32((uint)MovementFlags.OnTransport);
        writer.WriteU32(123);
        writer.WriteVector3(new Vector3(100f, 200f, 5f));
        writer.WriteF32(1f);
        writer.WriteU64(transportGuid);
        writer.WriteVector3(new Vector3(2f, 3f, 0.5f));
        writer.WriteF32(0.25f);
        writer.WriteF32(0f); // LIVING fall time is f32 in UPDATE_OBJECT
        foreach (float speed in new[] { 2.5f, 7f, 4.5f, 4.7222223f, 2.5f, MathF.PI })
            writer.WriteF32(speed);
        writer.WriteU8(0); // empty descriptor mask

        writer.WriteU8((byte)UpdateKind.CreateObject2);
        writer.WritePackedGuid(goGuid);
        writer.WriteU8((byte)ObjectTypeId.GameObject);
        writer.WriteU8(0x42); // UPDATE_FLAG_HAS_POSITION | UPDATE_FLAG_TRANSPORT
        writer.WriteVector3(new Vector3(-10f, 20f, 7f));
        writer.WriteF32(0.75f);
        writer.WriteU32(456_789); // path-domain cycle anchor
        writer.WriteU8(0); // empty descriptor mask

        ObjectUpdateBuffer updates = UpdateObjectParser.Parse(writer.ToArray());
        TransportPose wanted = new(transportGuid, new Vector3(2f, 3f, 0.5f), 0.25f);
        Check(updates.Count == 2 && updates[0].Movement?.Transport == wanted &&
              updates[0].Movement?.Speeds is { Length: 6 } speeds && speeds[0] == 2.5f &&
              updates[1].Movement?.TransportProgress == 456_789,
            "UPDATE_OBJECT transport rider tail or GO path-progress surface/alignment drift");

        var store = new EntityStore();
        store.Apply(updates[0], nowMs: 9_000);
        store.Apply(updates[1], nowMs: 9_001);
        Check(store.TryGet(riderGuid, out WorldEntity rider) && rider.Transport == wanted &&
              store.TryGet(goGuid, out WorldEntity transport) &&
              transport.TransportProgress == 456_789 &&
              transport.TransportProgressReceivedMs == 9_001,
            "transport rider/local clock fields did not reach the entity layer");

        var relay = new MovementInfo
        {
            Flags = (uint)MovementFlags.OnTransport,
            Position = new Vector3(101f, 201f, 5f),
            Orientation = 1.1f,
            Transport = new TransportPose(transportGuid, new Vector3(4f, 5f, 0.5f), 0.5f),
        };
        rider.Type = ObjectTypeId.Player; // exercise the observed-player relay arm
        store.ApplyRemotePlayerMove(riderGuid, relay, 9_100);
        Check(rider.Transport == relay.Transport,
            "MSG_MOVE observer relay discarded its rider-local transport frame");
    }

    private static void CheckCreateTimeSplineResume()
    {
        const ulong guid = 0xF130000012340001;
        var writer = new PacketWriter();
        writer.WriteU32(1);                         // update count
        writer.WriteU8(0);                          // packet has_transport
        writer.WriteU8((byte)UpdateKind.CreateObject2);
        writer.WritePackedGuid(guid);
        writer.WriteU8((byte)ObjectTypeId.Unit);
        writer.WriteU8(0x20);                       // UPDATE_FLAG_LIVING
        writer.WriteU32((uint)MovementFlags.SplineEnabled);
        writer.WriteU32(123);                       // movement timestamp
        writer.WriteVector3(new Vector3(5, 0, 0));  // server's current create pose
        writer.WriteF32(0);
        writer.WriteF32(0);                         // fall time
        for (int i = 0; i < 6; i++) writer.WriteF32(i + 1);
        writer.WriteU32(0x10_0000);                 // cyclic metadata, linear ground path
        writer.WriteU32(250);                       // already ridden
        writer.WriteU32(1_000);                     // whole duration
        writer.WriteU32(77);                        // spline id
        writer.WriteU32(5);                         // raw control count
        writer.WriteVector3(new Vector3(-10, 0, 0)); // phantom head
        writer.WriteVector3(new Vector3(0, 0, 0));
        writer.WriteVector3(new Vector3(10, 0, 0));
        writer.WriteVector3(new Vector3(20, 0, 0));
        writer.WriteVector3(new Vector3(20, 0, 0)); // virtual tail
        writer.WriteVector3(new Vector3(20, 0, 0)); // duplicated final destination
        writer.WriteU8(0);                          // empty create-field mask

        ObjectUpdateBuffer updates = UpdateObjectParser.Parse(writer.ToArray());
        Check(updates.Count == 1 && updates[0].Movement?.Spline is { } parsed &&
              parsed.Path.SequenceEqual(new[]
              {
                  new Vector3(0, 0, 0), new Vector3(10, 0, 0), new Vector3(20, 0, 0)
              }) && parsed.Id == 77 && parsed.TimePassedMs == 250 &&
              parsed.DurationMs == 1_000 && !parsed.Flying && parsed.Cyclic,
            "create spline parse did not trim phantom controls or retain timing/flags");

        var store = new EntityStore();
        store.Apply(updates[0], nowMs: 5_000);
        Check(store.TryGet(guid, out WorldEntity entity) && entity.Spline is { } resumed &&
              resumed.Id == 77 && !resumed.Flying && resumed.Cyclic,
            "create-time live spline did not reach the entity layer");
        store.TickSplines(5_000);
        Check(MathF.Abs(entity.Position.X - 5f) < .001f,
            "create-time spline did not resume at time_passed/duration");
        store.TickSplines(5_500);
        Check(MathF.Abs(entity.Position.X - 15f) < .001f && entity.IsMoving,
            "resumed create-time spline did not continue from the server-authored fraction");
        store.TickSplines(5_750);
        Check(MathF.Abs(entity.Position.X - 20f) < .001f && !entity.IsMoving,
            "resumed create-time spline did not finish at the endpoint");

        CreatureSpline? finished = CreatureSpline.Resume(
            new CreateSpline([Vector3.Zero, Vector3.One], 9, 1_000, 1_000, false, false),
            nowMs: 5_000);
        CreatureSpline? flying = CreatureSpline.Resume(
            new CreateSpline([Vector3.Zero, Vector3.One], 10, 0, 1_000, true, false),
            nowMs: 5_000);
        Check(finished is null && flying is { Flying: true, Id: 10 },
            "a create pose at or beyond spline duration must remain authoritative and stationary");
    }

    private static PacketWriter Head(ulong guid, Vector3 start, byte moveType)
    {
        var writer = new PacketWriter();
        writer.WritePackedGuid(guid);
        writer.WriteVector3(start);
        writer.WriteU32(7);
        writer.WriteU8(moveType);
        return writer;
    }

    private static void AppendEndpointSpline(PacketWriter writer, Vector3 endpoint)
    {
        writer.WriteU32(0);
        writer.WriteU32(500);
        writer.WriteU32(1);
        writer.WriteVector3(endpoint);
    }

    private static bool Near(float? actual, float expected) =>
        actual is { } value && MathF.Abs(value - expected) < .0001f;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
