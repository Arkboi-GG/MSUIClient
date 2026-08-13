using System.Numerics;

namespace MSUIClient.World.Portals;

/// <summary>
/// Server-authoritative description of one summoned portal.  The fields mirror
/// the optional portal-descriptor wire message; keeping the wire identity on the
/// scene object prevents an old async preview from being published for a newer
/// spawn at the same gameobject GUID.
/// </summary>
public readonly record struct PortalDescriptor
{
    public byte Version { get; init; }
    public byte Result { get; init; }
    public ushort Flags { get; init; }
    public uint RequestId { get; init; }
    public ulong PortalGuid { get; init; }
    public uint SpawnGeneration { get; init; }
    public uint DescriptorRevision { get; init; }
    public ulong Ticket { get; init; }
    public uint PortalEntry { get; init; }
    public uint TeleportSpellId { get; init; }
    public uint RemainingLifetimeMs { get; init; }
    public Vector3 SourceCenter { get; init; }
    public float SourceYaw { get; init; }
    public float HalfWidth { get; init; }
    public float HalfHeight { get; init; }
    public float PlaneEpsilon { get; init; }
    public uint PreviewMapId { get; init; }
    public Vector3 PreviewPosition { get; init; }
    public float PreviewOrientation { get; init; }

    /// <summary>A stable key for rejecting completion from a superseded descriptor.</summary>
    public readonly (ulong Guid, uint Spawn, uint Revision, ulong Ticket) Identity
        => (PortalGuid, SpawnGeneration, DescriptorRevision, Ticket);

    public readonly bool IsFinite =>
        Finite(SourceCenter) && float.IsFinite(SourceYaw) &&
        float.IsFinite(HalfWidth) && float.IsFinite(HalfHeight) &&
        float.IsFinite(PlaneEpsilon) && Finite(PreviewPosition) &&
        float.IsFinite(PreviewOrientation);

    /// <summary>
    /// Structural validation only.  Result/flag policy belongs to the wire
    /// adapter because older servers are permitted to assign new enum values.
    /// </summary>
    public readonly bool IsValid =>
        IsFinite && PortalGuid != 0 && RemainingLifetimeMs > 0 &&
        HalfWidth > 0.05f && HalfHeight > 0.05f &&
        PlaneEpsilon >= 0f && PreviewMapId <= int.MaxValue;

    public readonly PortalFrame SourceFrame => PortalFrame.FromYaw(SourceCenter, SourceYaw);
    public readonly PortalFrame DestinationFrame
        // The wire preview position is the stock spell_target_position landing
        // pose (feet), not a second authored aperture centre. Until the later
        // exit-frame protocol exists, derive a matching vertical doorway from
        // that support point so the transformed camera is not buried four yards
        // below destination ground.
        => PortalFrame.FromYaw(
            PreviewPosition + Vector3.UnitZ * (HalfHeight + 0.1f),
            PreviewOrientation);

    private static bool Finite(in Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}

/// <summary>
/// Orthonormal portal basis. Right and Up span the visible aperture; Normal is
/// the direction through it. Source and destination frames use the same handed
/// convention, so a camera offset/direction can be transferred coefficient by
/// coefficient without introducing a map-coordinate conversion.
/// </summary>
public readonly record struct PortalFrame(
    Vector3 Center,
    Vector3 Right,
    Vector3 Up,
    Vector3 Normal)
{
    public static PortalFrame FromYaw(Vector3 center, float yaw)
    {
        // In WoW's Z-up world yaw zero faces +X. The aperture's horizontal
        // screen-right vector follows the camera convention (sin,-cos,0).
        Vector3 normal = new(MathF.Cos(yaw), MathF.Sin(yaw), 0f);
        Vector3 right = new(MathF.Sin(yaw), -MathF.Cos(yaw), 0f);
        return new PortalFrame(center, right, Vector3.UnitZ, normal);
    }

    public bool TryNormalize(out PortalFrame normalized)
    {
        normalized = default;
        if (!Finite(Center) || !Finite(Right) || !Finite(Up) || !Finite(Normal))
            return false;

        Vector3 normal = SafeNormalize(Normal, Vector3.Zero);
        if (normal == Vector3.Zero) return false;

        Vector3 right = Right - normal * Vector3.Dot(Right, normal);
        right = SafeNormalize(right, Vector3.Zero);
        if (right == Vector3.Zero)
        {
            right = SafeNormalize(Vector3.Cross(Up, normal), Vector3.Zero);
            if (right == Vector3.Zero) return false;
        }

        Vector3 up = SafeNormalize(Vector3.Cross(normal, right), Vector3.Zero);
        if (up == Vector3.Zero) return false;
        if (Vector3.Dot(up, Up) < 0f)
        {
            right = -right;
            up = -up;
        }

        normalized = new PortalFrame(Center, right, up, normal);
        return true;
    }

    public Vector3 TransformDirection(in Vector3 direction, in PortalFrame source)
        => Right * Vector3.Dot(direction, source.Right)
         + Up * Vector3.Dot(direction, source.Up)
         + Normal * Vector3.Dot(direction, source.Normal);

    public Vector3 TransformPoint(in Vector3 point, in PortalFrame source)
        => Center + TransformDirection(point - source.Center, source);

    private static Vector3 SafeNormalize(in Vector3 value, in Vector3 fallback)
    {
        float lengthSquared = value.LengthSquared();
        return lengthSquared > 1e-12f ? value / MathF.Sqrt(lengthSquared) : fallback;
    }

    private static bool Finite(in Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
