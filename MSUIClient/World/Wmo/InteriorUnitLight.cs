using System.Numerics;
using System.Runtime.InteropServices;

namespace MSUIClient.World.Wmo;

/// <summary>
/// Interior light for the dynamic M2s - creatures, remote players, the local
/// body, mounts and their attached items - cached per unit with a frame budget
/// and a short settle.
///
/// The law lives in <see cref="WmoRenderer.ResolveInteriorLight"/>: a model
/// standing in an INTERIOR cell is lit by the floor's baked MOCV under its feet,
/// exactly as the room's own props (MODD.color) are; anywhere else it takes the
/// sky. This class only decides WHEN to ask (a unit that has not moved keeps its
/// answer; the world under it changing re-asks) and smooths the answer so a
/// doorway crossing is a short fade rather than a pop - the 1.12 client eases
/// its model light over roughly a second too.
///
/// The value handed to the shaders is (rgb = MOCV / 255, a = interior weight,
/// 0 meaning daylight). Weight 0 is also GL's default for an unset uniform, so
/// a draw path that never sets it - the glue booth, a portrait - lights as it
/// always did.
/// </summary>
public sealed class InteriorUnitLight
{
    private struct Entry
    {
        public Vector3 Feet;
        public Vector3 Target;
        public float TargetWeight;
        public Vector3 Color;
        public float Weight;
        public double ResolvedAt;
        public double SeenAt;
        public double SettledAt;
        public int Version;
        public bool Resolved;
    }

    private readonly Dictionary<ulong, Entry> _entries = new();
    private readonly List<ulong> _prune = [];
    private double _now, _dt, _prunedAt;
    private int _version;

    public WmoRenderer? Wmo { get; set; }

    /// <summary>Terrain height under (x, y), so a unit on a hill over a cave is
    /// outdoors - the same guard every other floor ray in the renderer uses.</summary>
    public Func<float, float, float?>? TerrainHeight { get; set; }

    /// <summary>One switch with the props: off restores the sky-lit look exactly.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Floor rays per frame. Beyond it a stale answer is reused for a frame;
    /// with a ~1 yd move threshold and a 2 s refresh the steady state is a handful.</summary>
    public int BudgetPerFrame { get; set; } = 24;

    /// <summary>Move (yd) before a unit's answer is re-asked.</summary>
    public float MoveThreshold { get; set; } = 0.75f;

    /// <summary>Seconds before a resting unit's answer is re-asked anyway (doors, hulls).</summary>
    public double RefreshSeconds { get; set; } = 2.0;

    /// <summary>Settle rate (1/s) of the shader value toward the resolved answer.</summary>
    public float SettleRate { get; set; } = 4f;

    public int ResolvesThisFrame { get; private set; }
    public int Tracked => _entries.Count;

    /// <summary>How many tracked units currently sit in an interior cell (HUD).</summary>
    public int InteriorCount
    {
        get
        {
            int n = 0;
            foreach (var e in _entries.Values) if (e.TargetWeight > 0.5f) n++;
            return n;
        }
    }

    public void BeginFrame(double nowSeconds)
    {
        _dt = Math.Clamp(nowSeconds - _now, 0.0, 0.1);
        _now = nowSeconds;
        ResolvesThisFrame = 0;
        _version = Wmo?.ResidentVersion ?? 0;
        if (_now - _prunedAt > 5.0)
        {
            _prunedAt = _now;
            _prune.Clear();
            foreach (var (guid, e) in _entries)
                if (_now - e.SeenAt > 30.0) _prune.Add(guid);
            foreach (ulong guid in _prune) _entries.Remove(guid);
        }
    }

    /// <summary>The light for <paramref name="guid"/> standing at <paramref name="feet"/>
    /// this frame. Safe to call more than once per unit per frame (body, mount, items).</summary>
    public Vector4 For(ulong guid, Vector3 feet)
    {
        if (!Enabled || Wmo is null) return Vector4.Zero;

        ref Entry e = ref CollectionsMarshal.GetValueRefOrAddDefault(_entries, guid, out bool exists);
        bool stale = !exists || !e.Resolved || e.Version != _version ||
                     Vector3.DistanceSquared(e.Feet, feet) > MoveThreshold * MoveThreshold ||
                     _now - e.ResolvedAt > RefreshSeconds;
        if (stale && ResolvesThisFrame < BudgetPerFrame)
        {
            ResolvesThisFrame++;
            Vector3? color = Wmo.ResolveInteriorLight(feet, TerrainHeight?.Invoke(feet.X, feet.Y));
            // Leaving a room fades the WEIGHT only; the last room colour is kept so the
            // blend passes through "less of that light", never through black.
            if (color is Vector3 c) e.Target = c;
            e.TargetWeight = color is null ? 0f : 1f;
            e.Feet = feet;
            e.ResolvedAt = _now;
            e.Version = _version;
            if (!e.Resolved)
            {
                e.Color = e.Target;
                e.Weight = e.TargetWeight;
                e.Resolved = true;
            }
        }
        e.SeenAt = _now;
        if (e.Resolved && e.SettledAt != _now)
        {
            e.SettledAt = _now;
            float k = Math.Clamp((float)_dt * SettleRate, 0f, 1f);
            e.Color += (e.Target - e.Color) * k;
            e.Weight += (e.TargetWeight - e.Weight) * k;
        }
        return e.Resolved ? new Vector4(e.Color, e.Weight) : Vector4.Zero;
    }

    public void Forget(ulong guid) => _entries.Remove(guid);
    public void Clear() => _entries.Clear();
}
