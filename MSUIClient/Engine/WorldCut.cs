using System.Numerics;

namespace MSUIClient.Engine;

/// <summary>
/// The Command View's interior cut: everything inside the XY footprint and above CutZ is
/// discarded by the WMO, doodad and terrain fragment shaders, so a building's roof and upper
/// walls open up over the commanded party while the floor and lower walls keep the room.
/// Interface Options -> Command View -> "Cut the roof off" (ON by default since 2026-09-01;
/// still a toggle). World space; each renderer converts to its camera-relative
/// shader space with <see cref="RelativeRect"/> / <see cref="RelativeZ"/>.
/// </summary>
public readonly record struct WorldCut(Vector2 Min, Vector2 Max, float CutZ)
{
    public Vector4 RelativeRect(Vector3 camera) =>
        new(Min.X - camera.X, Min.Y - camera.Y, Max.X - camera.X, Max.Y - camera.Y);

    public float RelativeZ(Vector3 camera) => CutZ - camera.Z;

    public bool Contains(float x, float y) =>
        x >= Min.X && x <= Max.X && y >= Min.Y && y <= Max.Y;

    /// <summary>The fragment shaders' strict footprint test, shared by the plane and the slice.</summary>
    public bool InsideFootprint(Vector3 point) =>
        point.X > Min.X && point.X < Max.X &&
        point.Y > Min.Y && point.Y < Max.Y;

    /// <summary>Exact fragment-shader plane predicate, including its strict rectangle bounds.</summary>
    public bool Cuts(Vector3 point) => point.Z > CutZ && InsideFootprint(point);

    /// <summary>Most sight lines a frame carries; the shaders declare the same count.</summary>
    public const int MaxSightLines = 8;

    /// <summary>A sight tunnel is this wide at the camera and this narrow at the unit, so the
    /// floor 1.5 yd under a unit's chest and the wall behind it are never carved.</summary>
    public const float SightRadiusNear = 3.0f;
    public const float SightRadiusFar = 0.7f;

    /// <summary>Canopy cut: doodads (trees, props) within this many yards of a party member are
    /// sliced above the cut height, the way a building is - a tunnel alone left the tree's
    /// crown hiding the party (owner, 2026-09-02).</summary>
    public const float CanopyRadius = 9f;

    /// <summary>Only party members this close to the camera get a tunnel.</summary>
    public const float SightRangeYards = 120f;

    /// <summary>Exact CPU counterpart of the WMO/doodad camera-to-chest tunnel.</summary>
    public static bool SightCuts(Vector3 camera, Vector3 point, Vector3 target)
    {
        Vector3 to = target - camera;
        float lengthSquared = MathF.Max(to.LengthSquared(), 1.0e-4f);
        Vector3 relativePoint = point - camera;
        float t = Math.Clamp(Vector3.Dot(relativePoint, to) / lengthSquared, 0f, 1f);
        if (t >= 0.985f) return false;

        float radius = SightRadiusNear + (SightRadiusFar - SightRadiusNear) * t;
        Vector3 offAxis = relativePoint - to * t;
        return offAxis.LengthSquared() < radius * radius;
    }

    public static bool SightCutsAny(
        Vector3 camera,
        Vector3 point,
        IReadOnlyList<Vector3>? targets)
    {
        if (targets is null) return false;
        for (int i = 0; i < targets.Count && i < MaxSightLines; i++)
            if (SightCuts(camera, point, targets[i])) return true;
        return false;
    }

    /// <summary>The camera EYE may not sink under the cut (looking up at sliced faces from
    /// beneath is exactly the "weird underneath" view the cut exists to avoid). The rig is the
    /// orbit target, so its floor is the eye floor minus the boom's height above the target.</summary>
    public float RigFloor(float boomDistance, float pitch) =>
        CutZ + 1f - boomDistance * MathF.Sin(MathF.Max(0.05f, pitch));
}

/// <summary>
/// The Command View's camera-side slice around the primary selection. Inside the roof cut's
/// footprint, within <see cref="Radius"/> of the primary, anything higher than its feet plus
/// <see cref="FloorMargin"/> AND nearer to the camera than its chest (measured along the view
/// direction, plus <see cref="DepthSlack"/>) is discarded.
///
/// Why a slice and not a higher roof plane or point tunnels (owner, 2026-09-02): a spiral stair
/// stacks every level of the climb on one XY footprint. The roof plane at feet + 4.5 yd keeps
/// the next half-turn of treads and the slab above, which sit squarely between the camera and
/// the unit; tunnels only poke holes through them. One plane perpendicular to the view, clipped
/// to the local cylinder and floored just above the feet, removes the near half of the shaft
/// down to a plinth while keeping the floor, the flight below and the far-side flight above
/// (the landing the unit is climbing toward). Camera looking down means geometry directly
/// overhead is nearer than the chest, so it goes too.
///
/// World space; the shaders receive the camera-relative form (vWorldPos is camera-relative).
/// </summary>
public readonly record struct WorldSlice(Vector3 Forward, float Depth, float FloorZ, Vector2 Centre)
{
    /// <summary>How much of the near wall survives, measured above the primary's feet.</summary>
    public const float FloorMargin = 1.0f;

    /// <summary>The slice plane sits this far past the chest so the unit itself is never cut.</summary>
    public const float DepthSlack = 0.5f;

    /// <summary>Horizontal reach around the primary. Keeps the dollhouse local: the far wing of
    /// the same building keeps its 4.5 yd roof cut instead of dropping to a plinth.</summary>
    public const float Radius = 20f;

    /// <summary>Same chest lift the sight tunnels use.</summary>
    public const float ChestLift = 1.2f;

    public static WorldSlice From(Vector3 camera, Vector3 cameraForward, Vector3 primaryFeet)
    {
        Vector3 forward = cameraForward.LengthSquared() > 1e-8f
            ? Vector3.Normalize(cameraForward)
            : new Vector3(0f, 0f, -1f);
        Vector3 chest = primaryFeet + new Vector3(0f, 0f, ChestLift);
        return new WorldSlice(
            forward,
            Vector3.Dot(chest - camera, forward) + DepthSlack,
            primaryFeet.Z + FloorMargin,
            new Vector2(primaryFeet.X, primaryFeet.Y));
    }

    /// <summary>Faces this upright (|normal.z| at or above it) are floor-like and never sliced:
    /// the slice removes a shaft's near walls, not a ramp or a sloping cave floor rising toward
    /// the camera (owner, 2026-09-03). Same constant as wmo.frag / doodad.frag.</summary>
    public const float FloorLikeNormalZ = 0.6f;

    /// <summary>Exact CPU counterpart of the shader slice, for picking. <paramref name="normal"/>
    /// is the face normal when the caller has one (collision hits do; winding is not consistent,
    /// so only its magnitude is judged).</summary>
    public bool Cuts(Vector3 camera, WorldCut footprint, Vector3 point, Vector3? normal = null)
    {
        if (normal is Vector3 n && MathF.Abs(n.Z) >= FloorLikeNormalZ) return false;
        if (point.Z <= FloorZ || !footprint.InsideFootprint(point)) return false;
        if (Vector3.Dot(point - camera, Forward) >= Depth) return false;
        Vector2 flat = new(point.X - Centre.X, point.Y - Centre.Y);
        return flat.LengthSquared() < Radius * Radius;
    }

    public float RelativeFloorZ(Vector3 camera) => FloorZ - camera.Z;
    public Vector2 RelativeCentre(Vector3 camera) => new(Centre.X - camera.X, Centre.Y - camera.Y);
}
