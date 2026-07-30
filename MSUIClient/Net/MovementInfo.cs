using System.Diagnostics;
using System.Numerics;

namespace MSUIClient.Net;

// The MovementInfo body carried by every MSG_MOVE_* (both directions) and the
// teleport ack. Ported byte-for-byte from benilla-protocol/messages/movement.rs
// (verified there against vmangos MovementInfo::Read, build 1.12.1). Positions
// are raw WoW coordinates (X north, Y west, Z up) — the native client's space,
// no conversion.

[Flags]
public enum MovementFlags : uint
{
    None          = 0,
    Forward       = 0x00000001,
    Backward      = 0x00000002,
    StrafeLeft    = 0x00000004,
    StrafeRight   = 0x00000008,
    TurnLeft      = 0x00000010,
    TurnRight     = 0x00000020,
    WalkMode      = 0x00000100,
    FallingFar    = 0x00004000,
    Falling       = 0x00002000, // JUMPING — carries the ballistic tail
    Swimming      = 0x00200000, // carries the swim-pitch f32
    SplineEnabled = 0x00400000,
    OnTransport   = 0x02000000, // carries the transport pose tail (1.12 bit 25, NOT the TBC 0x200)
    SplineElevation = 0x04000000,
}

public readonly record struct TransportPose(ulong Guid, Vector3 Pos, float Orientation);

/// <summary>Ballistic launch params — present iff the JUMPING flag is set. Order: zspeed, cos, sin, xyspeed.</summary>
public readonly record struct JumpInfo(float ZSpeed, float CosAngle, float SinAngle, float XySpeed);

public sealed class MovementInfo
{
    public uint Flags;
    public uint Timestamp;
    public Vector3 Position;
    public float Orientation;
    public TransportPose? Transport;
    public float Pitch;      // swim pitch, radians (+up); 0 when not swimming
    public uint FallTime;    // ms airborne (u32 on the wire)
    public JumpInfo? Jump;

    public void Write(PacketWriter w)
    {
        w.WriteU32(Flags);
        w.WriteU32(Timestamp);
        w.WriteVector3(Position);
        w.WriteF32(Orientation);

        if ((Flags & (uint)MovementFlags.OnTransport) != 0)
        {
            var t = Transport ?? default;
            w.WriteU64(t.Guid);           // full u64, never packed here
            w.WriteVector3(t.Pos);
            w.WriteF32(t.Orientation);
        }
        if ((Flags & (uint)MovementFlags.Swimming) != 0)
            w.WriteF32(Pitch);

        w.WriteU32(FallTime);

        if ((Flags & (uint)MovementFlags.Falling) != 0)
        {
            var j = Jump ?? default;
            w.WriteF32(j.ZSpeed);
            w.WriteF32(j.CosAngle);
            w.WriteF32(j.SinAngle);
            w.WriteF32(j.XySpeed);
        }
        // benilla never sets SPLINE_ELEVATION outbound, so no trailing float is written.
    }

    public static MovementInfo Read(PacketReader r)
    {
        uint flags = r.ReadU32();
        var mi = new MovementInfo
        {
            Flags = flags,
            Timestamp = r.ReadU32(),
            Position = r.ReadVector3(),
            Orientation = r.ReadF32(),
        };
        if ((flags & (uint)MovementFlags.OnTransport) != 0)
            mi.Transport = new TransportPose(r.ReadU64(), r.ReadVector3(), r.ReadF32());
        if ((flags & (uint)MovementFlags.Swimming) != 0)
            mi.Pitch = r.ReadF32();
        mi.FallTime = r.ReadU32();
        if ((flags & (uint)MovementFlags.Falling) != 0)
            mi.Jump = new JumpInfo(r.ReadF32(), r.ReadF32(), r.ReadF32(), r.ReadF32());
        if ((flags & (uint)MovementFlags.SplineElevation) != 0)
            r.ReadF32(); // parsed to stay aligned, discarded
        return mi;
    }

    /// <summary>Build a MovementInfo stamped with the client-uptime clock (see <see cref="ClientUptimeMs"/>).</summary>
    public static MovementInfo Create(Vector3 pos, float orientation, MovementFlags flags) => new()
    {
        Flags = (uint)flags,
        Timestamp = ClientUptimeMs(),
        Position = pos,
        Orientation = orientation,
    };

    private static readonly Stopwatch _uptime = Stopwatch.StartNew();

    /// <summary>
    /// Monotonic ms since the client started (matches the real 1.12 client's GetTickCount(), NOT
    /// wall clock). vmangos only uses it as a non-zero guard and for deltas, so it must stay non-zero
    /// and wrap cleanly as a u32 (~49.7 days).
    /// </summary>
    public static uint ClientUptimeMs() => Math.Max(1u, unchecked((uint)_uptime.ElapsedMilliseconds));
}
