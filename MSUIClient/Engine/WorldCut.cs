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

    /// <summary>The camera EYE may not sink under the cut (looking up at sliced faces from
    /// beneath is exactly the "weird underneath" view the cut exists to avoid). The rig is the
    /// orbit target, so its floor is the eye floor minus the boom's height above the target.</summary>
    public float RigFloor(float boomDistance, float pitch) =>
        CutZ + 1f - boomDistance * MathF.Sin(MathF.Max(0.05f, pitch));
}
