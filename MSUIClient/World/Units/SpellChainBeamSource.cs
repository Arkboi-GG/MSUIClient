using MSUIClient.Formats;

namespace MSUIClient.World.Units;

public readonly record struct SpellChainBeamInstance(
    long Id,
    ulong Caster,
    uint SpellId,
    IReadOnlyList<ulong> Targets,
    SpellChainEffectInfo Effect,
    ushort DestinationAttachment,
    uint StrandCount,
    bool Persistent,
    double Started,
    double Expires);

/// <summary>
/// Reference-owned state for spell chain beams. The target list lives on the caster, has both
/// SPELL_GO and SMSG_SPELL_UPDATE_CHAIN_TARGETS producers, and is consumed once by the next chain
/// CharProc. Rendering remains a separate concern so this lifecycle can be tested without GL.
/// </summary>
public sealed class SpellChainBeamSource
{
    private readonly SpellVisualCatalog _visuals;
    private readonly Dictionary<ulong, ulong[]> _hops = [];
    private readonly List<SpellChainBeamInstance> _beams = [];
    private long _nextId;

    public SpellChainBeamSource(SpellVisualCatalog visuals) => _visuals = visuals;

    public int PendingCasterCount => _hops.Count;
    public int ActiveCount => _beams.Count;

    public void StoreHops(ulong caster, IReadOnlyList<ulong> targets)
    {
        // The reference filler clears before every fill and drops the caster's own guid while
        // preserving every other entry in wire order (including duplicates).
        _hops[caster] = targets.Where(guid => guid != caster).ToArray();
    }

    public bool Play(ulong caster, uint spellId, uint visualId, in SpellVisualKitInfo kit,
        uint liveChannelSpell, ulong? liveChannelObject, double now)
    {
        if (!SpellVisualCatalog.TryGetChainProc(kit, out SpellChainProcInfo proc)) return false;

        // Consumption happens on every chain-proc exit path, including missing targets/effect.
        _hops.Remove(caster, out ulong[]? stored);
        stored ??= [];
        IReadOnlyList<ulong> targets = stored;
        if (liveChannelSpell == spellId && stored.Length <= 1 && liveChannelObject is > 0)
            targets = [liveChannelObject.Value];

        if (spellId == 0 || proc.BeamCount == 0 || targets.Count == 0 ||
            !_visuals.TryGetChainEffect(proc.EffectId, out SpellChainEffectInfo effect))
            return false;

        if (proc.Persistent)
            _beams.RemoveAll(beam => beam.Persistent && beam.Caster == caster &&
                beam.SpellId == spellId);

        ushort destination = _visuals.TryGetStages(visualId, out SpellVisualStages stages)
            ? stages.MissileAttachment : SpellVisualCatalog.NoMissileAttachment;
        double expires = proc.Persistent ? double.PositiveInfinity :
            now + targets.Count * effect.BoltLifeMs / 1000.0;
        _beams.Add(new SpellChainBeamInstance(++_nextId, caster, spellId,
            targets.ToArray(), effect, destination, proc.BeamCount, proc.Persistent,
            now, expires));
        return true;
    }

    public void BeginCast(ulong caster)
        => _beams.RemoveAll(beam => beam.Persistent && beam.Caster == caster);

    public void Reap(ulong caster, uint spellId)
        => _beams.RemoveAll(beam => beam.Persistent && beam.Caster == caster &&
            beam.SpellId == spellId);

    public void ClearUnit(ulong caster)
    {
        _hops.Remove(caster);
        _beams.RemoveAll(beam => beam.Caster == caster);
    }

    public void Clear()
    {
        _hops.Clear();
        _beams.Clear();
    }

    public IReadOnlyList<SpellChainBeamInstance> Snapshot(double now,
        Func<ulong, SpellUnitPose>? unitPose = null)
    {
        _beams.RemoveAll(beam => !beam.Persistent && now >= beam.Expires ||
            unitPose is not null && !unitPose(beam.Caster).Found);
        return _beams.ToArray();
    }
}

/// <summary>Pure beam geometry/timing laws shared by the renderer and clinical checks.</summary>
public static class SpellChainBeamLaw
{
    public const float PerpendicularEpsilon = .001f;
    public const float AdvectionKeep = .75f;
    public const int MaxSubdivisions = 256;

    public static bool HopVisible(in SpellChainBeamInstance beam, int hop, double now)
    {
        if (hop < 0 || hop >= beam.Targets.Count) return false;
        if (beam.Persistent) return true;
        double start = beam.Started + hop * beam.Effect.BoltStaggerMs / 1000.0;
        double end = start + beam.Effect.BoltLifeMs / 1000.0;
        return now >= start && now < end && now < beam.Expires;
    }

    public static int SubdivisionCount(float length, float averageSegmentLength)
    {
        if (!float.IsFinite(length) || length <= 0f) return 2;
        if (!float.IsFinite(averageSegmentLength) || averageSegmentLength <= 0f)
            return 2;
        return Math.Clamp((int)(length / averageSegmentLength + 2f), 2, MaxSubdivisions);
    }

    public static void FreshPolyline(System.Numerics.Vector3 from,
        System.Numerics.Vector3 to, float noiseScale, ref uint random,
        List<System.Numerics.Vector3> output, float averageSegmentLength)
    {
        float length = System.Numerics.Vector3.Distance(from, to);
        int segments = SubdivisionCount(length, averageSegmentLength);
        output.Clear();
        output.Capacity = Math.Max(output.Capacity, segments + 1);
        float amplitude = length * (float.IsFinite(noiseScale) ? noiseScale : 0f);
        for (int i = 0; i <= segments; i++)
        {
            float t = i / (float)segments;
            System.Numerics.Vector3 point = System.Numerics.Vector3.Lerp(from, to, t);
            if (i != 0 && i != segments)
                point += new System.Numerics.Vector3(Symmetric(ref random),
                    Symmetric(ref random), Symmetric(ref random)) * amplitude;
            output.Add(point);
        }
    }

    public static void Advect(List<System.Numerics.Vector3> live,
        IReadOnlyList<System.Numerics.Vector3> fresh)
    {
        if (live.Count != fresh.Count)
        {
            live.Clear();
            live.AddRange(fresh);
            return;
        }
        int last = live.Count - 1;
        if (last < 0) return;
        live[0] = fresh[0];
        live[last] = fresh[last];
        for (int i = 1; i < last; i++)
            live[i] = live[i] * AdvectionKeep + fresh[i] * (1f - AdvectionKeep);
    }

    private static float Symmetric(ref uint state)
    {
        if (state == 0) state = 0x9E3779B9u;
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return (state >> 8) / 16777216f * 2f - 1f;
    }
}
