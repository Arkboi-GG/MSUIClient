using System.Numerics;
using MSUIClient.World.Portals;

namespace MSUIClient.Net;

/// <summary>Version-1 result for <see cref="Op.SMSG_SUI_PORTAL_DESCRIPTOR"/>.</summary>
public enum PortalDescriptorResult : byte
{
    Ok = 0,
    Denied = 1,
    Unsupported = 2,
    Expired = 3,
    Failed = 4,
}

[Flags]
public enum PortalDescriptorFlags : ushort
{
    None = 0,
    OneWay = 1 << 0,
    PartyOnly = 1 << 1,
    ClickFallback = 1 << 2,
    SameMapHint = 1 << 3,
    // Reserved in version 1. Stock Mage portals are one-way.
    Bidirectional = 1 << 4,
}

public enum PortalLoadResult : byte
{
    Ready = 0,
    Failed = 1,
}

public enum PortalStateCode : byte
{
    Ready = 0,
    Revoked = 1,
    Blocked = 2,
    Entering = 3,
    Expired = 4,
    Failed = 5,
}

public readonly record struct PortalPreparePacket(
    byte Version,
    ushort RequestFlags,
    uint RequestId,
    ulong PortalGuid);

/// <summary>
/// Network DTO for SMSG_SUI_PORTAL_DESCRIPTOR. The scene layer deliberately owns
/// its own descriptor type; keeping the wire DTO distinct prevents parser and
/// speculative-world lifetime concerns from becoming coupled.
/// </summary>
public readonly record struct PortalDescriptorPacket(
    byte Version,
    PortalDescriptorResult Result,
    PortalDescriptorFlags Flags,
    uint RequestId,
    ulong PortalGuid,
    uint SpawnGeneration,
    uint DescriptorRevision,
    ulong Ticket,
    uint PortalEntry,
    uint TeleportSpellId,
    uint RemainingLifetimeMs,
    Vector3 SourceCenter,
    float SourceYaw,
    float HalfWidth,
    float HalfHeight,
    float PlaneEpsilon,
    uint PreviewMapId,
    Vector3 PreviewPosition,
    float PreviewOrientation);

public readonly record struct PortalReadyPacket(
    byte Version,
    PortalLoadResult LoadResult,
    ulong PortalGuid,
    uint SpawnGeneration,
    uint DescriptorRevision,
    ulong Ticket);

public readonly record struct PortalStatePacket(
    byte Version,
    PortalStateCode State,
    byte Reason,
    ulong PortalGuid,
    uint SpawnGeneration,
    uint DescriptorRevision,
    ulong Ticket,
    uint LeaseOrRetryMs);

/// <summary>
/// Backwards-compatible capability suffix carried after the fixed
/// SMSG_SUI_CONTROL_ACK prefix. Old SUI cores send no suffix; a current core
/// writes the self-identifying magic followed by a bit mask.
/// </summary>
public static class SuiCapabilityWire
{
    public const uint Magic = 0x3149_5553; // "SUI1" on the little-endian wire
    public const uint RealPortalsV1 = 1u << 0;
    public const uint PortalPrewarmCatalogV1 = 1u << 1;
    public const uint FactionControlGroupsV1 = 1u << 2;
    public const int TrailerLength = 8;

    public const byte PrewarmCatalogVersion = 1;
    public const int PrewarmCatalogHeaderLength = 4;
    public const ushort PrewarmCatalogRowLength = 32;

    public static bool TryRead(PacketReader reader, out uint capabilities)
    {
        capabilities = 0;
        if (reader.Remaining < TrailerLength) return false;

        uint magic = reader.ReadU32();
        uint advertised = reader.ReadU32();
        if (magic != Magic) return false;

        capabilities = advertised;
        return true;
    }

    /// <summary>
    /// Read the historical capability trailer and, only when its dedicated bit
    /// is advertised, the immediately following portal-prewarm catalog:
    /// u8 version, u8 row count, u16 row length, then fixed-size rows containing
    /// summon/entry/use/map, XYZ and orientation. A v1 catalog is all-or-nothing
    /// and must contain each stock Mage portal exactly once.
    /// </summary>
    public static bool TryRead(
        PacketReader reader,
        out uint capabilities,
        out PortalPrewarmHint[] portalPrewarmCatalog)
    {
        capabilities = 0;
        portalPrewarmCatalog = [];
        if (!TryRead(reader, out uint advertised)) return false;
        // Preserve independently negotiated blocks even when the optional
        // catalog extension is malformed. Callers may keep portal-v1 enabled
        // while disabling only cast-start prewarm.
        capabilities = advertised;

        if ((advertised & PortalPrewarmCatalogV1) == 0)
        {
            return true;
        }

        if (reader.Remaining < PrewarmCatalogHeaderLength) return false;
        byte version = reader.ReadU8();
        byte rowCount = reader.ReadU8();
        ushort rowLength = reader.ReadU16();
        if (version != PrewarmCatalogVersion ||
            rowCount != PortalPrewarmLaw.CatalogCount ||
            rowLength != PrewarmCatalogRowLength ||
            reader.Remaining < rowCount * rowLength)
            return false;

        var catalog = new PortalPrewarmHint[rowCount];
        for (int i = 0; i < catalog.Length; i++)
        {
            catalog[i] = new PortalPrewarmHint(
                reader.ReadU32(),
                reader.ReadU32(),
                reader.ReadU32(),
                reader.ReadU32(),
                reader.ReadVector3(),
                reader.ReadF32());
        }

        if (!PortalPrewarmLaw.IsCompleteCatalog(catalog)) return false;

        portalPrewarmCatalog = catalog;
        return true;
    }
}

/// <summary>
/// Exact-length, little-endian codecs for the correlated REAL_PORTALS protocol.
/// Version and reserved bytes are checked here so higher layers never receive a
/// packet whose layout they cannot safely interpret.
/// </summary>
public static class PortalWire
{
    public const byte ProtocolVersion = 1;
    public const int PrepareLength = 16;
    public const int DescriptorLength = 92;
    public const int ReadyLength = 28;
    public const int StateLength = 32;

    private const PortalDescriptorFlags AllDescriptorFlags =
        PortalDescriptorFlags.OneWay |
        PortalDescriptorFlags.PartyOnly |
        PortalDescriptorFlags.ClickFallback |
        PortalDescriptorFlags.SameMapHint |
        PortalDescriptorFlags.Bidirectional;

    public static PortalPreparePacket Prepare(
        uint requestId, ulong portalGuid, ushort requestFlags = 0) =>
        new(ProtocolVersion, requestFlags, requestId, portalGuid);

    public static PortalReadyPacket Ready(
        PortalLoadResult loadResult,
        ulong portalGuid,
        uint spawnGeneration,
        uint descriptorRevision,
        ulong ticket) =>
        new(ProtocolVersion, loadResult, portalGuid, spawnGeneration, descriptorRevision, ticket);

    public static byte[] BuildPrepare(in PortalPreparePacket packet)
    {
        ValidatePrepare(packet);
        var w = new PacketWriter(PrepareLength);
        w.WriteU8(packet.Version);
        w.WriteU8(0);
        w.WriteU16(packet.RequestFlags);
        w.WriteU32(packet.RequestId);
        w.WriteU64(packet.PortalGuid);
        return ExactBody(w, PrepareLength, nameof(PortalPreparePacket));
    }

    public static PortalPreparePacket ParsePrepare(byte[] body)
    {
        var r = ExactReader(body, PrepareLength, nameof(PortalPreparePacket));
        byte version = r.ReadU8();
        RequireZero(r.ReadU8(), "portal prepare reserved byte");
        var packet = new PortalPreparePacket(
            version, r.ReadU16(), r.ReadU32(), r.ReadU64());
        RequireConsumed(r, nameof(PortalPreparePacket));
        ValidatePrepare(packet);
        return packet;
    }

    public static byte[] BuildDescriptor(in PortalDescriptorPacket packet)
    {
        ValidateDescriptor(packet);
        var w = new PacketWriter(DescriptorLength);
        w.WriteU8(packet.Version);
        w.WriteU8((byte)packet.Result);
        w.WriteU16((ushort)packet.Flags);
        w.WriteU32(packet.RequestId);
        w.WriteU64(packet.PortalGuid);
        w.WriteU32(packet.SpawnGeneration);
        w.WriteU32(packet.DescriptorRevision);
        w.WriteU64(packet.Ticket);
        w.WriteU32(packet.PortalEntry);
        w.WriteU32(packet.TeleportSpellId);
        w.WriteU32(packet.RemainingLifetimeMs);
        w.WriteVector3(packet.SourceCenter);
        w.WriteF32(packet.SourceYaw);
        w.WriteF32(packet.HalfWidth);
        w.WriteF32(packet.HalfHeight);
        w.WriteF32(packet.PlaneEpsilon);
        w.WriteU32(packet.PreviewMapId);
        w.WriteVector3(packet.PreviewPosition);
        w.WriteF32(packet.PreviewOrientation);
        return ExactBody(w, DescriptorLength, nameof(PortalDescriptorPacket));
    }

    public static PortalDescriptorPacket ParseDescriptor(byte[] body)
    {
        var r = ExactReader(body, DescriptorLength, nameof(PortalDescriptorPacket));
        var packet = new PortalDescriptorPacket(
            r.ReadU8(),
            ReadDescriptorResult(r.ReadU8()),
            (PortalDescriptorFlags)r.ReadU16(),
            r.ReadU32(),
            r.ReadU64(),
            r.ReadU32(),
            r.ReadU32(),
            r.ReadU64(),
            r.ReadU32(),
            r.ReadU32(),
            r.ReadU32(),
            r.ReadVector3(),
            r.ReadF32(),
            r.ReadF32(),
            r.ReadF32(),
            r.ReadF32(),
            r.ReadU32(),
            r.ReadVector3(),
            r.ReadF32());
        RequireConsumed(r, nameof(PortalDescriptorPacket));
        ValidateDescriptor(packet);
        return packet;
    }

    public static byte[] BuildReady(in PortalReadyPacket packet)
    {
        ValidateReady(packet);
        var w = new PacketWriter(ReadyLength);
        w.WriteU8(packet.Version);
        w.WriteU8((byte)packet.LoadResult);
        w.WriteU16(0);
        w.WriteU64(packet.PortalGuid);
        w.WriteU32(packet.SpawnGeneration);
        w.WriteU32(packet.DescriptorRevision);
        w.WriteU64(packet.Ticket);
        return ExactBody(w, ReadyLength, nameof(PortalReadyPacket));
    }

    public static PortalReadyPacket ParseReady(byte[] body)
    {
        var r = ExactReader(body, ReadyLength, nameof(PortalReadyPacket));
        byte version = r.ReadU8();
        PortalLoadResult loadResult = ReadLoadResult(r.ReadU8());
        RequireZero(r.ReadU16(), "portal ready reserved field");
        var packet = new PortalReadyPacket(
            version, loadResult, r.ReadU64(), r.ReadU32(), r.ReadU32(), r.ReadU64());
        RequireConsumed(r, nameof(PortalReadyPacket));
        ValidateReady(packet);
        return packet;
    }

    public static byte[] BuildState(in PortalStatePacket packet)
    {
        ValidateState(packet);
        var w = new PacketWriter(StateLength);
        w.WriteU8(packet.Version);
        w.WriteU8((byte)packet.State);
        w.WriteU8(packet.Reason);
        w.WriteU8(0);
        w.WriteU64(packet.PortalGuid);
        w.WriteU32(packet.SpawnGeneration);
        w.WriteU32(packet.DescriptorRevision);
        w.WriteU64(packet.Ticket);
        w.WriteU32(packet.LeaseOrRetryMs);
        return ExactBody(w, StateLength, nameof(PortalStatePacket));
    }

    public static PortalStatePacket ParseState(byte[] body)
    {
        var r = ExactReader(body, StateLength, nameof(PortalStatePacket));
        byte version = r.ReadU8();
        PortalStateCode state = ReadState(r.ReadU8());
        byte reason = r.ReadU8();
        RequireZero(r.ReadU8(), "portal state reserved byte");
        var packet = new PortalStatePacket(
            version, state, reason, r.ReadU64(), r.ReadU32(), r.ReadU32(),
            r.ReadU64(), r.ReadU32());
        RequireConsumed(r, nameof(PortalStatePacket));
        ValidateState(packet);
        return packet;
    }

    private static void ValidatePrepare(in PortalPreparePacket packet)
    {
        RequireVersion(packet.Version, nameof(PortalPreparePacket));
        if (packet.RequestFlags != 0)
            throw new InvalidDataException(
                $"{nameof(PortalPreparePacket)} version 1 request flags must be zero");
        RequireCorrelation(packet.PortalGuid, ticket: null, nameof(PortalPreparePacket));
    }

    private static void ValidateDescriptor(in PortalDescriptorPacket packet)
    {
        RequireVersion(packet.Version, nameof(PortalDescriptorPacket));
        _ = ReadDescriptorResult((byte)packet.Result);
        if ((packet.Flags & ~AllDescriptorFlags) != 0)
            throw new InvalidDataException(
                $"{nameof(PortalDescriptorPacket)} contains unknown flag bits 0x{(ushort)(packet.Flags & ~AllDescriptorFlags):X4}");
        RequireCorrelation(packet.PortalGuid,
            packet.Result == PortalDescriptorResult.Ok ? packet.Ticket : null,
            nameof(PortalDescriptorPacket));
        RequireFinite(packet.SourceCenter, "portal source center");
        RequireFinite(packet.SourceYaw, "portal source yaw");
        RequireFinite(packet.HalfWidth, "portal half width");
        RequireFinite(packet.HalfHeight, "portal half height");
        RequireFinite(packet.PlaneEpsilon, "portal plane epsilon");
        RequireFinite(packet.PreviewPosition, "portal preview position");
        RequireFinite(packet.PreviewOrientation, "portal preview orientation");

        if (packet.Result == PortalDescriptorResult.Ok &&
            (packet.HalfWidth <= 0f || packet.HalfHeight <= 0f || packet.PlaneEpsilon < 0f))
            throw new InvalidDataException(
                "successful portal descriptor requires positive aperture dimensions and non-negative plane epsilon");
    }

    private static void ValidateReady(in PortalReadyPacket packet)
    {
        RequireVersion(packet.Version, nameof(PortalReadyPacket));
        _ = ReadLoadResult((byte)packet.LoadResult);
        RequireCorrelation(packet.PortalGuid, packet.Ticket, nameof(PortalReadyPacket));
    }

    private static void ValidateState(in PortalStatePacket packet)
    {
        RequireVersion(packet.Version, nameof(PortalStatePacket));
        _ = ReadState((byte)packet.State);
        RequireCorrelation(packet.PortalGuid, packet.Ticket, nameof(PortalStatePacket));
        if (packet.State == PortalStateCode.Ready && packet.LeaseOrRetryMs == 0)
            throw new InvalidDataException("READY portal state requires a nonzero lease");
        if (packet.State != PortalStateCode.Ready && packet.LeaseOrRetryMs != 0)
            throw new InvalidDataException("only READY portal state may carry a lease");
    }

    private static PortalDescriptorResult ReadDescriptorResult(byte value) => value switch
    {
        0 => PortalDescriptorResult.Ok,
        1 => PortalDescriptorResult.Denied,
        2 => PortalDescriptorResult.Unsupported,
        3 => PortalDescriptorResult.Expired,
        4 => PortalDescriptorResult.Failed,
        _ => throw new InvalidDataException($"unknown portal descriptor result {value}"),
    };

    private static PortalLoadResult ReadLoadResult(byte value) => value switch
    {
        0 => PortalLoadResult.Ready,
        1 => PortalLoadResult.Failed,
        _ => throw new InvalidDataException($"unknown portal load result {value}"),
    };

    private static PortalStateCode ReadState(byte value) => value switch
    {
        0 => PortalStateCode.Ready,
        1 => PortalStateCode.Revoked,
        2 => PortalStateCode.Blocked,
        3 => PortalStateCode.Entering,
        4 => PortalStateCode.Expired,
        5 => PortalStateCode.Failed,
        _ => throw new InvalidDataException($"unknown portal state {value}"),
    };

    private static PacketReader ExactReader(byte[] body, int length, string label)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (body.Length != length)
            throw new InvalidDataException($"{label} expected {length} bytes, got {body.Length}");
        return new PacketReader(body);
    }

    private static byte[] ExactBody(PacketWriter writer, int length, string label)
    {
        if (writer.Length != length)
            throw new InvalidOperationException($"{label} wrote {writer.Length} bytes, expected {length}");
        return writer.ToArray();
    }

    private static void RequireConsumed(PacketReader reader, string label)
    {
        if (reader.Remaining != 0)
            throw new InvalidDataException($"{label} has {reader.Remaining} trailing byte(s)");
    }

    private static void RequireVersion(byte version, string label)
    {
        if (version != ProtocolVersion)
            throw new InvalidDataException(
                $"{label} version {version} is unsupported (expected {ProtocolVersion})");
    }

    private static void RequireZero(ulong value, string label)
    {
        if (value != 0)
            throw new InvalidDataException($"{label} must be zero, got {value}");
    }

    private static void RequireCorrelation(ulong portalGuid, ulong? ticket, string label)
    {
        if (portalGuid == 0)
            throw new InvalidDataException($"{label} has an empty portal GUID");
        if (ticket == 0)
            throw new InvalidDataException($"{label} has an empty correlation ticket");
    }

    private static void RequireFinite(Vector3 value, string label)
    {
        RequireFinite(value.X, $"{label} X");
        RequireFinite(value.Y, $"{label} Y");
        RequireFinite(value.Z, $"{label} Z");
    }

    private static void RequireFinite(float value, string label)
    {
        if (!float.IsFinite(value))
            throw new InvalidDataException($"{label} must be finite");
    }
}
