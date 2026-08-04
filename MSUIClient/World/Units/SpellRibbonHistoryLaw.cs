using System.Numerics;

namespace MSUIClient.World.Units;

/// <summary>
/// Pure vanilla ribbon history law. Bone/root transforms create only the live head and newly
/// committed world-space edges. Old edges retain their committed frame, while a private clamped
/// clock drives ribbon look tracks and raw elapsed time drives edge expiry.
/// </summary>
public static class SpellRibbonHistoryLaw
{
    public const int MaxEdges = 512;
    public static readonly Vector3 ParsedAuthoredYAxis = -Vector3.UnitZ;

    public readonly record struct Edge(Vector3 Top, Vector3 Bottom, float BornRawAge);
    public readonly record struct Step(float SimulationSeconds, bool Commit);

    public sealed class State
    {
        public readonly List<Edge> Edges = [];
        public float SourceAge { get; internal set; }
        public float RawAge { get; internal set; }
        public float ClipAge { get; internal set; }
        public float Accumulator { get; internal set; }
        public bool Initialized { get; internal set; }
    }

    public static float SimulationStep(float elapsedSeconds)
        => float.IsFinite(elapsedSeconds) ? Math.Clamp(elapsedSeconds, 0f, .1f) : 0f;

    public static Step AdvanceLive(State state, float sourceAge, float edgesPerSecond,
        float edgeLifetime, float gravity)
    {
        float safeAge = float.IsFinite(sourceAge) ? Math.Max(0f, sourceAge) : state.SourceAge;
        if (!state.Initialized)
        {
            state.Initialized = true;
            // The source instance and its ribbon share a birth edge. If the first render lands
            // slightly after that edge, consume the elapsed interval instead of silently losing
            // the first emission slice.
            state.SourceAge = 0f;
            state.RawAge = 0f;
        }

        float rawDelta = Math.Max(0f, safeAge - state.SourceAge);
        state.SourceAge = safeAge;
        state.RawAge += rawDelta;
        return Advance(state, rawDelta, edgesPerSecond, edgeLifetime, gravity,
            allowCommit: true);
    }

    public static Step AdvanceDrain(State state, float rawElapsedSeconds, float edgeLifetime,
        float gravity)
    {
        float rawDelta = float.IsFinite(rawElapsedSeconds) ? Math.Max(0f, rawElapsedSeconds) : 0f;
        state.RawAge += rawDelta;
        return Advance(state, rawDelta, 0f, edgeLifetime, gravity, allowCommit: false);
    }

    private static Step Advance(State state, float rawDelta, float edgesPerSecond,
        float edgeLifetime, float gravity, bool allowCommit)
    {
        float dt = SimulationStep(rawDelta);
        state.ClipAge += dt;
        float lifetime = float.IsFinite(edgeLifetime) ? Math.Max(.25f, edgeLifetime) : .25f;
        for (int i = state.Edges.Count - 1; i >= 0; i--)
        {
            Edge edge = state.Edges[i];
            if (state.RawAge - edge.BornRawAge >= lifetime)
            {
                state.Edges.RemoveAt(i);
                continue;
            }
            if (gravity != 0f && float.IsFinite(gravity) && dt > 0f)
            {
                Vector3 sag = Vector3.UnitZ * (2f * gravity * dt);
                state.Edges[i] = edge with { Top = edge.Top - sag, Bottom = edge.Bottom - sag };
            }
        }

        bool commit = false;
        if (allowCommit && float.IsFinite(edgesPerSecond) && edgesPerSecond > 0f)
        {
            state.Accumulator += edgesPerSecond * dt;
            if (state.Accumulator >= 1f)
            {
                state.Accumulator -= MathF.Floor(state.Accumulator);
                commit = state.Edges.Count < MaxEdges;
            }
        }
        return new Step(dt, commit);
    }

    public static void Commit(State state, Vector3 top, Vector3 bottom)
    {
        if (state.Edges.Count < MaxEdges)
            state.Edges.Add(new Edge(top, bottom, state.RawAge));
    }

    /// <summary>M2 skin matrices already contain the inverse-pivot fold.</summary>
    public static Vector3 NodeWorld(Vector3 parsedPosition, Matrix4x4 skin,
        Matrix4x4 rootTransform)
        => Vector3.Transform(parsedPosition, skin * rootTransform);

    /// <summary>
    /// The reference spans ribbon height along authored WoW local +Y. MSUI parses raw
    /// (x,y,z) as (x,z,-y), so that authored axis is parsed local -Z.
    /// Scale is deliberately discarded: the reference reads the live owner's rotation.
    /// </summary>
    public static Vector3 CrossSectionAxis(Matrix4x4 skin, Matrix4x4 rootTransform)
    {
        Matrix4x4 world = skin * rootTransform;
        if (Matrix4x4.Decompose(world, out _, out Quaternion rotation, out _) &&
            rotation.LengthSquared() > 1e-8f)
        {
            Vector3 axis = Vector3.Transform(ParsedAuthoredYAxis,
                Quaternion.Normalize(rotation));
            if (axis.LengthSquared() > 1e-8f) return Vector3.Normalize(axis);
        }
        Vector3 fallback = Vector3.TransformNormal(ParsedAuthoredYAxis, world);
        return fallback.LengthSquared() > 1e-8f ? Vector3.Normalize(fallback) : ParsedAuthoredYAxis;
    }

    public static float EdgeAge01(State state, in Edge edge, float edgeLifetime)
    {
        float lifetime = float.IsFinite(edgeLifetime) ? Math.Max(.25f, edgeLifetime) : .25f;
        return Math.Clamp((state.RawAge - edge.BornRawAge) / lifetime, 0f, 1f);
    }
}
