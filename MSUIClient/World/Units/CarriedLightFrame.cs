using System.Numerics;
using MSUIClient.Engine;

namespace MSUIClient.World.Units;

public readonly record struct CarriedLightPlacement(string Key, Vector3 Position, Vector3 Color);

/// <summary>
/// Previous-frame carried M2 light pack. The render order draws terrain before the animated
/// carrier, so publishing after unit posing gives every earlier surface a coherent next-frame
/// light set instead of mixing current and stale skeletons within one frame.
/// </summary>
public static class CarriedLightFrame
{
    public const int MaxCandidates = 8;
    private static CarriedLightPlacement[] _current = [];

    public static IReadOnlyList<CarriedLightPlacement> Current => _current;

    public static void Commit(IEnumerable<CarriedLightPlacement> placements, Vector3 camera)
    {
        _current = placements
            .GroupBy(light => light.Key, StringComparer.Ordinal)
            .Select(group => group.Last())
            .OrderBy(light => Vector3.DistanceSquared(light.Position, camera))
            .Take(MaxCandidates)
            .ToArray();
    }

    public static void Upload(Shader shader, Vector3 camera)
    {
        shader.Set("uPointLightCount", _current.Length);
        for (int i = 0; i < MaxCandidates; i++)
        {
            Vector3 position = i < _current.Length ? _current[i].Position - camera : Vector3.Zero;
            Vector3 color = i < _current.Length ? _current[i].Color : Vector3.Zero;
            shader.Set($"uPointLightPos[{i}]", position);
            shader.Set($"uPointLightColor[{i}]", color);
        }
    }

    public static float Attenuation(float distance)
    {
        float d = MathF.Max(0f, distance);
        return 1f / MathF.Max(.001f, .7f * d + .03f * d * d);
    }
}
