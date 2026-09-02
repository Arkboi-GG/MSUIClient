using System.Numerics;

namespace MSUIClient.Net;

public readonly record struct BinderConfirmPacket(ulong BinderGuid);
public readonly record struct PlayerBoundPacket(ulong BinderGuid, uint AreaId);
/// <summary>
/// SMSG_BINDPOINTUPDATE: x, y, z, mapId, areaId (vmangos BindpointUpdate::AppendBodyTo,
/// Packets/Misc.cpp). The trailing AreaTable id is what the "$z" home token names. It used
/// to be missing here, so RequireConsumed threw on every login ("4 trailing byte(s)" in
/// msui-console.log) and the bind point was never stored. Reported 2026-09-01.
/// </summary>
public readonly record struct BindPointPacket(Vector3 Position, uint MapId, uint AreaId);

/// <summary>Exact build-5875 binder packet bodies.</summary>
public static class BinderPackets
{
    public static BinderConfirmPacket ParseConfirm(ReadOnlySpan<byte> body)
    {
        var reader = new PacketReader(body.ToArray());
        var packet = new BinderConfirmPacket(reader.ReadU64());
        RequireConsumed(reader, nameof(BinderConfirmPacket));
        return packet;
    }

    public static PlayerBoundPacket ParsePlayerBound(ReadOnlySpan<byte> body)
    {
        var reader = new PacketReader(body.ToArray());
        var packet = new PlayerBoundPacket(reader.ReadU64(), reader.ReadU32());
        RequireConsumed(reader, nameof(PlayerBoundPacket));
        return packet;
    }

    public static BindPointPacket ParseBindPoint(ReadOnlySpan<byte> body)
    {
        var reader = new PacketReader(body.ToArray());
        var packet = new BindPointPacket(
            new Vector3(reader.ReadF32(), reader.ReadF32(), reader.ReadF32()),
            reader.ReadU32(), reader.ReadU32());
        RequireConsumed(reader, nameof(BindPointPacket));
        return packet;
    }

    private static void RequireConsumed(PacketReader reader, string packet)
    {
        if (reader.Remaining != 0)
            throw new InvalidDataException($"{packet} has {reader.Remaining} trailing byte(s)");
    }
}
