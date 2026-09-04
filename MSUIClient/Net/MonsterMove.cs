using System.Numerics;

namespace MSUIClient.Net;

// SMSG_MONSTER_MOVE (0xDD) — creature/NPC locomotion splines. This is what makes
// NPCs WALK; without it every creature stands frozen at its spawn. Ported byte-for-
// byte from benilla-protocol/messages/monster_move.rs + benilla/src/net/motion/
// spline.rs (both cite vmangos PacketBuilder::WriteLinearPath / WriteCatmullRomPath).
//
// Wire layout (verified vs benilla): packed guid, start Vector3, spline-id u32,
// move-type u8, a move-type-keyed facing union, spline-flags u32, duration u32,
// point count u32, then the points. A move-type of 1 (STOP) short-circuits: nothing
// after it. Ground paths are LINEAR — one absolute endpoint then (count-1) PACKED
// 11/11/10-bit signed quarter-yard offsets, each stored as (endpoint - waypoint).
// Flying/taxi paths (flag 0x200) are Catmull-Rom over `count` absolute points.
//
// Everything stays in raw WoW space (X north, Y west, Z up) — the same space
// WorldEntity.Position uses — so no conversion happens here or at draw time.

/// <summary>A parsed SMSG_MONSTER_MOVE: who moves, along which polyline, for how long.</summary>
public sealed class MonsterMove
{
    public ulong Guid;
    public uint SplineId;
    public Vector3 Start;
    public Vector3[] Points = Array.Empty<Vector3>();  // travel order [start .. endpoint], raw WoW space
    public uint DurationMs;
    public bool Flying;          // spline flag 0x200 — also selects the Catmull-Rom point layout
    public bool Stop;            // move type 1 — freeze in place, no path follows
    public MonsterMoveFacing Facing;
}

public enum MonsterMoveFacingKind { None, Spot, Target, Angle }

/// <summary>The moveType-switched facing union carried by SMSG_MONSTER_MOVE.</summary>
public readonly record struct MonsterMoveFacing(
    MonsterMoveFacingKind Kind, Vector3 Spot, ulong TargetGuid, float Angle)
{
    public static MonsterMoveFacing None => default;
    public static MonsterMoveFacing ToSpot(Vector3 spot) =>
        new(MonsterMoveFacingKind.Spot, spot, 0, 0);
    public static MonsterMoveFacing ToTarget(ulong guid) =>
        new(MonsterMoveFacingKind.Target, default, guid, 0);
    public static MonsterMoveFacing ToAngle(float angle) =>
        new(MonsterMoveFacingKind.Angle, default, 0, angle);
}

public static class MonsterMoveFacingLaw
{
    /// <summary>Resolve the receipt-time hard snap in raw WoW coordinates.</summary>
    public static float? Resolve(MonsterMoveFacing facing, Vector3 unitPosition,
        Func<ulong, Vector3?> targetPosition)
    {
        Vector3? destination = facing.Kind switch
        {
            MonsterMoveFacingKind.Spot => facing.Spot,
            MonsterMoveFacingKind.Target => targetPosition(facing.TargetGuid),
            _ => null,
        };
        if (facing.Kind == MonsterMoveFacingKind.Angle) return facing.Angle;
        if (destination is not { } target) return null;
        float dx = target.X - unitPosition.X, dy = target.Y - unitPosition.Y;
        return dx * dx + dy * dy > 1e-6f ? MathF.Atan2(dy, dx) : null;
    }
}

public static class MonsterMoveParser
{
    private const byte MoveTypeStop = 1, MoveTypeFacingSpot = 2, MoveTypeFacingTarget = 3, MoveTypeFacingAngle = 4;
    private const uint SplineFlagFlying = 0x200;   // == Mask_CatmullRom in 1.12

    /// <summary>Decode the packet body. Returns null only on a malformed/underrun packet.</summary>
    public static MonsterMove? Parse(byte[] body)
    {
        try
        {
            var r = new PacketReader(body);
            var mm = new MonsterMove { Guid = r.ReadPackedGuid() };

            Vector3 start = r.ReadVector3();
            mm.Start = start;
            mm.SplineId = r.ReadU32();      // echoed when our own server-driven spline completes
            byte moveType = r.ReadU8();

            switch (moveType)
            {
                case MoveTypeStop:
                    mm.Stop = true;
                    mm.Points = new[] { start };
                    return mm;              // STOP short-circuits: no flags/duration/points follow
                case MoveTypeFacingSpot:
                    mm.Facing = MonsterMoveFacing.ToSpot(r.ReadVector3());
                    break;
                case MoveTypeFacingTarget:
                    mm.Facing = MonsterMoveFacing.ToTarget(r.ReadU64());
                    break;
                case MoveTypeFacingAngle:
                    mm.Facing = MonsterMoveFacing.ToAngle(r.ReadF32());
                    break;
            }

            uint flags = r.ReadU32();
            mm.DurationMs = r.ReadU32();
            mm.Flying = (flags & SplineFlagFlying) != 0;

            uint count = r.ReadU32();
            var points = new List<Vector3>((int)Math.Min(count + 1, 0xFFFF)) { start };

            if (mm.Flying)
            {
                // Catmull-Rom path: `count` absolute control points, verbatim.
                for (uint i = 0; i < count; i++) points.Add(r.ReadVector3());
            }
            else
            {
                if (count == 0)
                {
                    mm.Points = Array.Empty<Vector3>();
                    return mm;
                }
                // Linear path: one endpoint, then offsets. The producer's last_idx>1 guard means
                // count==2 carries no offsets at all (the direct start -> endpoint hop).
                Vector3 endpoint = r.ReadVector3();
                var mids = new List<Vector3>(Math.Max(0, (int)count - 1));
                uint offsetCount = count > 2 ? count - 1 : 0;
                for (uint i = 0; i < offsetCount; i++)
                {
                    int packed = r.ReadI32();
                    mids.Add(endpoint - PacketReader.DecodePackedSplinePoint(packed));
                }
                // mids[0] is a quarter-yard-quantised copy of start; drop it for the exact start.
                for (int i = 1; i < mids.Count; i++) points.Add(mids[i]);
                points.Add(endpoint);
            }

            mm.Points = points.ToArray();
            return mm;
        }
        catch (EndOfStreamException)
        {
            return null;   // truncated / unexpected layout — ignore this one rather than desync
        }
    }
}

/// <summary>
/// An in-progress creature spline. Samples position + facing over time, arc-length
/// parameterised (constant ground speed along the polyline), matching benilla's
/// Spline::sample. Ground segments lerp; flying segments use uniform Catmull-Rom.
/// </summary>
public sealed class CreatureSpline
{
    private readonly Vector3[] _pts;
    private readonly float[] _segLen;
    private readonly float _total;
    private long _startMs;
    private readonly uint _durationMs;
    public bool Flying { get; }
    public uint Id { get; }
    public bool Cyclic { get; }

    public CreatureSpline(Vector3[] points, uint durationMs, bool flying, long startMs,
        uint id = 0, bool cyclic = false)
    {
        _pts = points;
        _durationMs = Math.Max(1u, durationMs);
        Flying = flying;
        _startMs = startMs;
        Id = id;
        Cyclic = cyclic;
        _segLen = new float[Math.Max(0, points.Length - 1)];
        float sum = 0f;
        for (int i = 0; i < _segLen.Length; i++)
        {
            _segLen[i] = Vector3.Distance(points[i], points[i + 1]);
            sum += _segLen[i];
        }
        _total = sum;
    }

    /// <summary>
    /// Resume a create-block spline at the server-authored fraction already travelled. Cyclic
    /// metadata is retained, but like current Benilla the path is sampled for this one remaining
    /// traversal; continuous cyclic following remains a separate transport/motion concern.
    /// </summary>
    public static CreatureSpline? Resume(CreateSpline spline, long nowMs)
    {
        if (spline.DurationMs == 0 || spline.Path.Length < 2 ||
            spline.TimePassedMs >= spline.DurationMs)
            return null;
        return new CreatureSpline(spline.Path, spline.DurationMs, spline.Flying,
            nowMs - spline.TimePassedMs, spline.Id, spline.Cyclic);
    }

    /// <summary>Average speed along the whole path (yd/s) — used to pick walk vs run later.</summary>
    public float AverageSpeed => _total / (_durationMs / 1000f);

    /// <summary>
    /// Exclude a client-side authoritative freeze interval from this spline's clock. Position is
    /// held by EntityStore while frozen; moving the origin forward makes the first thaw sample
    /// continue from that exact fraction instead of jumping to the wall-clock endpoint.
    /// </summary>
    public void RebaseAfterPause(long pausedMs)
    {
        if (pausedMs > 0) _startMs += pausedMs;
    }

    /// <summary>
    /// Position + facing at a moment. Returns TRUE while the move is still running and
    /// FALSE once it is finished (the caller then drops the spline, leaving the unit at
    /// the endpoint). `facing` is a raw WoW orientation (null = keep the current facing).
    /// </summary>
    public bool Sample(long nowMs, out Vector3 pos, out float? facing)
    {
        float frac = (nowMs - _startMs) / (float)_durationMs;
        bool running = frac < 1f;
        frac = Math.Clamp(frac, 0f, 1f);

        if (_pts.Length < 2 || _total <= 1e-4f)
        {
            pos = _pts[_pts.Length - 1];
            facing = null;
            return running;
        }

        float want = frac * _total;
        int seg = 0;
        while (seg < _segLen.Length - 1 && want > _segLen[seg]) { want -= _segLen[seg]; seg++; }
        float len = _segLen[seg] > 1e-4f ? _segLen[seg] : 1f;
        float lt = Math.Clamp(want / len, 0f, 1f);

        Vector3 dir;
        if (Flying)
        {
            pos = CatmullRom(seg, lt, out dir);
        }
        else
        {
            Vector3 a = _pts[seg], b = _pts[seg + 1];
            pos = a + (b - a) * lt;
            dir = b - a;
        }

        facing = (dir.X * dir.X + dir.Y * dir.Y) > 1e-6f ? MathF.Atan2(dir.Y, dir.X) : (float?)null;
        return running;
    }

    private Vector3 CatmullRom(int i, float t, out Vector3 tangent)
    {
        Vector3 p0 = _pts[Math.Max(0, i - 1)];
        Vector3 p1 = _pts[i];
        Vector3 p2 = _pts[Math.Min(_pts.Length - 1, i + 1)];
        Vector3 p3 = _pts[Math.Min(_pts.Length - 1, i + 2)];
        float t2 = t * t, t3 = t2 * t;
        Vector3 c0 = 2f * p1;
        Vector3 c1 = -p0 + p2;
        Vector3 c2 = 2f * p0 - 5f * p1 + 4f * p2 - p3;
        Vector3 c3 = -p0 + 3f * p1 - 3f * p2 + p3;
        tangent = 0.5f * (c1 + c2 * (2f * t) + c3 * (3f * t2));
        return 0.5f * (c0 + c1 * t + c2 * t2 + c3 * t3);
    }
}
