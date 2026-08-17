using System.Numerics;
using MSUIClient.Formats;

namespace MSUIClient.World;

/// <summary>
/// Resolves vanilla's authored exterior lighting for a position and a time of
/// day (PLAN_09_EXTERIOR_LIGHTING.md).
///
///   Light.dbc          which lighting setup applies where, and how it fades
///     -> LightParams.dbc     one setting-set per weather state
///        -> LightIntBand.dbc    18 colour curves over the day
///        -> LightFloatBand.dbc   6 scalar curves over the day
///
/// THIS CLASS RESOLVES; IT DOES NOT APPLY. Nothing here touches GL, and nothing
/// here decides what the renderer does with the answer. That separation is the
/// whole point of the probe: for the first time the client can say what the data
/// intends at a spot, INDEPENDENTLY of what it is currently drawing, so "the sky
/// looks off" becomes a subtraction instead of an opinion.
///
/// Units are already normal by the time values reach here - the readers undo the
/// x36 on distances and convert half-minutes to hours. See DbcReader.
/// </summary>
public sealed class ExteriorLighting
{
    private LightTable? _lights;
    private LightParamsTable? _params;
    private LightIntBandTable? _intBands;
    private LightFloatBandTable? _floatBands;
    private LightSkyboxTable? _skyboxes;   // PLAN_18 Phase 2 (optional)

    public bool Ready => _lights is not null && _params is not null &&
                         _intBands is not null && _floatBands is not null;

    /// <summary>Why the probe is empty, when it is. Shown in the HUD.</summary>
    public string Status { get; private set; } = "not loaded";

    public void Load(string clientDataPath)
    {
        var light = AdtTerrainReader.ReadFileFromMpqs(clientDataPath, LightTable.MpqPath);
        var lparams = AdtTerrainReader.ReadFileFromMpqs(clientDataPath, LightParamsTable.MpqPath);
        var ints = AdtTerrainReader.ReadFileFromMpqs(clientDataPath, LightIntBandTable.MpqPath);
        var floats = AdtTerrainReader.ReadFileFromMpqs(clientDataPath, LightFloatBandTable.MpqPath);

        if (light is null || lparams is null || ints is null || floats is null)
        {
            Status = "one or more Light DBCs not found in the MPQs";
            Console.WriteLine($"[light] {Status} - exterior lighting stays on its constants");
            return;
        }

        _lights = LightTable.Parse(light);
        _params = LightParamsTable.Parse(lparams);
        _intBands = LightIntBandTable.Parse(ints);
        _floatBands = LightFloatBandTable.Parse(floats);

        // Optional (PLAN_18 Phase 2): the zone-skybox model table. A missing/failed
        // LightSkybox.dbc just means no skybox is ever resolved; the sky still works.
        var skyboxes = AdtTerrainReader.ReadFileFromMpqs(clientDataPath, LightSkyboxTable.MpqPath);
        _skyboxes = skyboxes is not null ? LightSkyboxTable.Parse(skyboxes) : null;

        if (!Ready)
        {
            Status = "a Light DBC failed to parse (see [dbc] lines above)";
            Console.WriteLine($"[light] {Status} - exterior lighting stays on its constants");
            return;
        }

        Status = $"{_lights!.Count} zone(s), {_params!.Count} params, " +
                 $"{_intBands!.Count} int band(s), {_floatBands!.Count} float band(s)";
        Console.WriteLine($"[light] loaded: {Status}");
    }

    // ── Coordinate convention (unresolved, so it is a switch not a guess) ──
    //
    // The probe's second run settled that our compare is in the wrong space:
    //
    //     zone extent  X 3200..32800   Y -234..436   Z 13208..32800
    //     player at   (-8950, -132, 84)
    //
    // Y is the SMALL axis, so the DBC stores height in Y - these are Y-up
    // coordinates and ours are Z-up. And the horizontal range sits inside
    // 0..34133, which is 64 tiles x 533.33: the map in POSITIVE space, where
    // ours is centred on +/-17066. So there is an axis swap and an origin shift,
    // and four plausible combinations of the two.
    //
    // Rather than pick one and see if it looks right, all of them are evaluated
    // and reported. Exactly one will put the player inside a zone at a sane
    // distance; the rest will be tens of thousands of yards out. That is a
    // decisive test, not a preference - the same shape as the Swap R/B check.

    /// <summary>Half the map: 64 tiles x 533.33333 / 2. The centred/positive offset.</summary>
    public const float MapHalfYards = 17066.666f;

    public sealed record CoordConvention(string Name, Func<Vector3, Vector3> ToWorld);

    /// <summary>
    /// dbc -> our world space. Every candidate treats dbc.Y as height, which the
    /// extent already establishes; they differ in how the horizontal pair maps.
    /// </summary>
    public static readonly CoordConvention[] Conventions =
    [
        new("raw (x,y,z) - the original guess",
            v => v),
        new("y-up only: X=x, Y=z, Z=y",
            v => new Vector3(v.X, v.Z, v.Y)),
        new("flip, swapped: X=H-z, Y=H-x",
            v => new Vector3(MapHalfYards - v.Z, MapHalfYards - v.X, v.Y)),
        new("flip, direct:  X=H-x, Y=H-z",
            v => new Vector3(MapHalfYards - v.X, MapHalfYards - v.Z, v.Y)),
        new("offset, swapped: X=z-H, Y=x-H",
            v => new Vector3(v.Z - MapHalfYards, v.X - MapHalfYards, v.Y)),
        new("offset, direct:  X=x-H, Y=z-H",
            v => new Vector3(v.X - MapHalfYards, v.Z - MapHalfYards, v.Y)),
    ];

    /// <summary>Which convention Resolve uses. Auto-detected; overridable for A/B.</summary>
    public int ConventionIndex { get; set; }

    private bool _conventionDetected;

    /// <summary>What detection concluded, and by how much. Shown in the HUD.</summary>
    public string ConventionReport { get; private set; } = "not detected yet";

    /// <summary>
    /// Pick the convention automatically, once, from a real player position.
    ///
    /// The first version of this printed a table and left a human to choose from
    /// a dropdown, which is a bad instrument: it computed the answer and then
    /// asked someone to act on it. An instrument that knows the answer should
    /// apply it and say what it did. The dropdown stays, as an override.
    ///
    /// Ranking: containment first, then nearest distance. The margin is reported
    /// because a narrow win means the test did not really decide - and a caller
    /// who cannot see the margin cannot know that.
    /// </summary>
    public void DetectConvention(uint mapId, Vector3 position, bool force = false)
    {
        if (!Ready || (_conventionDetected && !force)) return;

        var scores = ScoreConventions(mapId, position);
        if (scores.Count == 0) return;

        int best = 0;
        for (int i = 1; i < scores.Count; i++)
        {
            bool better = scores[i].Containing > scores[best].Containing ||
                          (scores[i].Containing == scores[best].Containing &&
                           scores[i].NearestYards < scores[best].NearestYards);
            if (better) best = i;
        }

        // Runner-up, for the margin.
        int second = -1;
        for (int i = 0; i < scores.Count; i++)
        {
            if (i == best) continue;
            if (second < 0 || scores[i].NearestYards < scores[second].NearestYards) second = i;
        }

        ConventionIndex = best;
        _conventionDetected = true;

        float margin = second >= 0 && scores[best].NearestYards > 0.01f
            ? scores[second].NearestYards / scores[best].NearestYards
            : 0f;

        ConventionReport = $"{Conventions[best].Name} - nearest {scores[best].NearestYards:F0} yd" +
                           (margin > 0f ? $", {margin:F1}x clearer than the runner-up" : "");

        Console.WriteLine($"[light] coordinate convention detected: {ConventionReport}");
        foreach (var (name, containing, nearest, nearestId) in scores)
            Console.WriteLine($"[light]   {(name == Conventions[best].Name ? "->" : "  ")} " +
                              $"{name,-34} containing {containing,3}  nearest {nearest,9:F0} yd " +
                              $"(light {nearestId})");

        if (margin > 0f && margin < 2f)
            Console.WriteLine("[light]   WARNING: margin under 2x - this test did not clearly decide. " +
                              "Re-detect from a different spot before trusting it.");
    }

    public CoordConvention Convention =>
        Conventions[Math.Clamp(ConventionIndex, 0, Conventions.Length - 1)];

    private Vector3 WorldPositionOf(LightZone zone) => Convention.ToWorld(zone.Position);

    /// <summary>
    /// Score every candidate convention against a position: how many zones would
    /// contain it, and how far the nearest one is. The right convention is the
    /// one that produces containment; the wrong ones sit tens of thousands of
    /// yards away and cannot be mistaken for it.
    /// </summary>
    public List<(string Name, int Containing, float NearestYards, uint NearestId)>
        ScoreConventions(uint mapId, Vector3 position)
    {
        var result = new List<(string, int, float, uint)>();
        if (!Ready) return result;

        var zones = _lights!.ForMap(mapId).Where(z => !z.IsMapDefault).ToList();
        foreach (var convention in Conventions)
        {
            int containing = 0;
            float nearest = float.MaxValue;
            uint nearestId = 0;
            foreach (var zone in zones)
            {
                float d = Vector3.Distance(position, convention.ToWorld(zone.Position));
                if (d < nearest) { nearest = d; nearestId = zone.Id; }
                if (zone.FalloffEnd > 0f && d <= zone.FalloffEnd) containing++;
            }
            result.Add((convention.Name, containing,
                        nearest == float.MaxValue ? 0f : nearest, nearestId));
        }
        return result;
    }

    /// <summary>One zone's contribution to a resolved sample. For the probe.</summary>
    public readonly record struct Contribution(
        uint LightId, uint ParamsId, bool IsDefault, float DistanceYards,
        float FalloffStart, float FalloffEnd, float Weight);

    /// <summary>
    /// Everything the data says about one place at one time. Colours are the 18
    /// LightIntBand slots, floats the 6 LightFloatBand slots, both already
    /// blended across contributing zones.
    /// </summary>
    public sealed class Sample
    {
        public Vector3[] Colors = new Vector3[LightIntBandTable.BandsPerParams];
        public float[] Floats = new float[LightFloatBandTable.BandsPerParams];
        public List<Contribution> Contributors = [];
        public bool HasData;

        public Vector3 Diffuse => Colors[0];
        public Vector3 Ambient => Colors[1];
        public Vector3 SkyTop => Colors[2];
        public Vector3 SkyMiddle => Colors[3];
        public Vector3 SkyBand1 => Colors[4];
        public Vector3 SkyBand2 => Colors[5];
        public Vector3 SkySmog => Colors[6];
        public Vector3 FogColor => Colors[7];
        public Vector3 SunColor => Colors[8];

        // The cloud palette (PLAN_18), consumed by the CloudField kernel. Roles per
        // benilla's byte-exact colour pass: sub-10 sun-glow, 11 slope, 12 base.
        public Vector3 CloudSunGlow => Colors[LightIntBandTable.CloudSunGlowBand];
        public Vector3 CloudSlope => Colors[LightIntBandTable.CloudSlopeBand];
        public Vector3 CloudBase => Colors[LightIntBandTable.CloudBaseBand];

        /// <summary>Cloud coverage density C in [0,1] - the kernel's threshold input.</summary>
        public float CloudDensity => Floats[LightFloatBandTable.CloudDensityBand];

        /// <summary>Yards. LightFloatBand band 0, already un-scaled from x36.</summary>
        public float FogEnd => Floats[LightFloatBandTable.FogEndBand];

        /// <summary>
        /// Yards, DERIVED. The data stores a 0..0.999 multiplier rather than a
        /// second distance, so the authored relationship between the two is kept
        /// rather than flattened into two independent knobs.
        /// </summary>
        public float FogStart => FogEnd * Floats[LightFloatBandTable.FogStartMultiplierBand];
    }

    /// <summary>
    /// Resolve the authored lighting at a world position and time.
    ///
    /// Blending, and why it is not "nearest wins": every zone carries an inner
    /// radius where it applies fully and an outer radius where it stops applying
    /// at all. Snapping to the nearest zone would pop at every zone edge - the
    /// same class of defect as rebuilding placements at a tile boundary. So the
    /// map-wide default is the base, and each zone is lerped on top of it by its
    /// own falloff weight, farthest first so the nearest zone lands last and
    /// dominates.
    /// </summary>
    public Sample Resolve(uint mapId, Vector3 position, float timeHours)
    {
        var sample = new Sample();
        if (!Ready) return sample;

        var zones = _lights!.ForMap(mapId);

        // Base: the map-wide default (position 0,0,0 with no radius). A map
        // without one falls back to the GLOBAL default - light 1, Azeroth's map
        // default. Without the fallback a map like AhnQirajTemple (531: one zone
        // light 8000+ yd out of reach, no default) resolved to NOTHING, and since
        // no-data leaves the atmosphere untouched, the scene kept whichever map's
        // light was applied last - lit if you teleported in, near-black if you
        // booted there (2026-08-16, the "dark as fuck after the audio rebuild"
        // incident - the audio was innocent; the rebuild forced the boot).
        var mapDefault = zones.FirstOrDefault(z => z.IsMapDefault)
                         ?? _lights.ForMap(0).FirstOrDefault(z => z.IsMapDefault);
        if (mapDefault is not null)
        {
            ReadInto(sample, mapDefault.ParamsClear, timeHours);
            sample.HasData = true;
            sample.Contributors.Add(new Contribution(
                mapDefault.Id, mapDefault.ParamsClear, true, 0f, 0f, 0f, 1f));
        }

        // Farthest first, so the closest zone is applied last.
        var scored = new List<(LightZone Zone, float Distance, float Weight)>();
        foreach (var zone in zones)
        {
            if (zone.IsMapDefault) continue;
            float distance = Vector3.Distance(position, WorldPositionOf(zone));
            float weight = FalloffWeight(distance, zone.FalloffStart, zone.FalloffEnd);
            if (weight <= 0f) continue;
            scored.Add((zone, distance, weight));
        }
        scored.Sort((a, b) => b.Distance.CompareTo(a.Distance));

        var scratch = new Sample();
        foreach (var (zone, distance, weight) in scored)
        {
            ReadInto(scratch, zone.ParamsClear, timeHours);

            for (int i = 0; i < sample.Colors.Length; i++)
                sample.Colors[i] = Vector3.Lerp(sample.Colors[i], scratch.Colors[i], weight);
            for (int i = 0; i < sample.Floats.Length; i++)
                sample.Floats[i] = sample.Floats[i] + (scratch.Floats[i] - sample.Floats[i]) * weight;

            sample.HasData = true;
            sample.Contributors.Add(new Contribution(
                zone.Id, zone.ParamsClear, false, distance,
                zone.FalloffStart, zone.FalloffEnd, weight));
        }

        return sample;
    }

    /// <summary>
    /// 1 inside the inner radius, 0 outside the outer, linear between.
    ///
    /// A zone with FalloffEnd == 0 that is NOT the map default has no reach and
    /// contributes nothing; treating it as infinite would let a stray row
    /// repaint the whole map.
    /// </summary>
    private static float FalloffWeight(float distance, float start, float end)
    {
        if (end <= 0f) return 0f;
        if (distance <= start) return 1f;
        if (distance >= end) return 0f;
        float span = end - start;
        return span <= 0f ? 1f : 1f - (distance - start) / span;
    }

    private void ReadInto(Sample sample, uint paramsId, float hours)
    {
        for (int b = 0; b < LightIntBandTable.BandsPerParams; b++)
            sample.Colors[b] = _intBands!.SampleColor(paramsId, b, hours);
        for (int b = 0; b < LightFloatBandTable.BandsPerParams; b++)
            sample.Floats[b] = _floatBands!.Sample(paramsId, b, hours);
    }

    /// <summary>
    /// True when a band has no authored keys at all. The probe needs to say
    /// "unauthored" rather than show (0,0,0), because black is a legitimate
    /// authored colour at midnight and the two must not look the same.
    /// </summary>
    public bool ColorBandAuthored(uint paramsId, int band)
        => _intBands?.Band(paramsId, band) is not null;

    /// <summary>The raw keys of a colour band, for the probe.</summary>
    public string DescribeColorBand(uint paramsId, int band)
        => _intBands?.Band(paramsId, band)?.Describe() ?? "(no keys)";

    public bool FloatBandAuthored(uint paramsId, int band)
        => _floatBands?.Band(paramsId, band) is not null;

    public LightParamsRow? Params(uint id) => _params?.Get(id);

    /// <summary>The skybox MODEL path for a LightParams.lightSkyboxID, or null (PLAN_18 Phase 2).</summary>
    public string? SkyboxPath(uint skyboxId) => _skyboxes?.Path(skyboxId);

    /// <summary>
    /// The nearest zones on a map REGARDLESS of whether they reach the point.
    ///
    /// This exists because of what the probe's first run showed: at Northshire
    /// only the map default applied, and a panel that hides zero-weight zones
    /// cannot tell "there is genuinely no zone here" apart from "our position is
    /// in the wrong coordinate space and every zone is 30,000 yards away". Those
    /// have completely different fixes, so the distances have to be visible even
    /// when nothing qualifies.
    /// </summary>
    public List<(LightZone Zone, float Distance)> NearestZones(uint mapId, Vector3 position, int count)
    {
        if (!Ready) return [];
        return _lights!.ForMap(mapId)
            .Where(z => !z.IsMapDefault)
            .Select(z => (Zone: z, Distance: Vector3.Distance(position, WorldPositionOf(z))))
            .OrderBy(x => x.Distance)
            .Take(count)
            .ToList();
    }

    /// <summary>
    /// The bounding box of every non-default zone on a map, in yards. A sanity
    /// check on the x36 conversion and the coordinate convention in one line: if
    /// the player is at (-8950, -132) and the zones span a range that does not
    /// contain it, the positions are not in our world space.
    /// </summary>
    public string DescribeZoneExtent(uint mapId)
    {
        if (!Ready) return "(not loaded)";
        var zones = _lights!.ForMap(mapId).Where(z => !z.IsMapDefault).ToList();
        if (zones.Count == 0) return "(no positioned zones on this map)";

        // Raw, deliberately - this line is what revealed the convention error,
        // and it only does that if it shows the numbers as stored.
        float minX = zones.Min(z => z.Position.X), maxX = zones.Max(z => z.Position.X);
        float minY = zones.Min(z => z.Position.Y), maxY = zones.Max(z => z.Position.Y);
        float minZ = zones.Min(z => z.Position.Z), maxZ = zones.Max(z => z.Position.Z);
        return $"{zones.Count} zone(s) RAW  X {minX:F0}..{maxX:F0}  " +
               $"Y {minY:F0}..{maxY:F0}  Z {minZ:F0}..{maxZ:F0}";
    }
}
