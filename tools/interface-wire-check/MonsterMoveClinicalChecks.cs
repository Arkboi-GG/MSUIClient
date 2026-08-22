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
