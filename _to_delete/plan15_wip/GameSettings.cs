using System.Text.Json;
using System.Text.Json.Serialization;

namespace MSUIClient.Engine;

/// <summary>
/// The player's preferences: everything the settings modal owns, and nothing
/// else. Loaded from settings.json at the repo root, written back when the modal
/// is accepted, and applied to the live renderers by GameLoop (Program.Settings.cs).
///
/// WHY THIS IS NOT PART OF ClientConfig
///   client-config.json is per-machine WIRING - MPQ paths, vmap paths, the realmd
///   host, the start position, the DevTools flag - and is gitignored for exactly
///   that reason. This is TASTE. Keeping them apart means the settings page never
///   rewrites the file that holds the paths, and a machine move carries one of the
///   two rather than both tangled together.
///
/// WHY THIS IS NOT A Vantage
///   A vantage is a place and an instant: it exists to reproduce one frame, and
///   loading one is SUPPOSED to stomp your fog values. Settings outlive every
///   place. Merging the two types would make "reproduce that frame" silently
///   overwrite a preference, so ApplyVantage deliberately does not write here.
///   See PLAN_11 section 10.
///
/// WHY IT IS PLAIN DATA
///   No renderer references, no GL, no ImGui. GameLoop owns the translation in
///   both directions (ApplySettings / CaptureSettings) because it is the only
///   thing that knows which renderers exist yet. Keeping this file ignorant is
///   what lets Program.Main read it BEFORE the window exists, which is required
///   for the restart-scoped controls (resolution, sample count, anisotropy).
/// </summary>
public sealed class GameSettings
{
    /// <summary>Bumped when a rename or a units change needs migration handling.</summary>
    public int Version { get; set; } = 1;

    /// <summary>Name of the preset last selected, or "Custom". Cosmetic; the values below are the truth.</summary>
    public string ActivePreset { get; set; } = "Custom";

    public DisplaySettings Display { get; set; } = new();
    public ViewSettings View { get; set; } = new();
    public DetailSettings Detail { get; set; } = new();
    public ClutterSettings Clutter { get; set; } = new();
    public WaterSettings Water { get; set; } = new();
    public LightingSettings Lighting { get; set; } = new();
    public ControlSettings Controls { get; set; } = new();
    public StreamingSettings Streaming { get; set; } = new();

    // ── groups ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Window, buffers and the UI itself. Three of these cannot change without a
    /// restart: Silk requests the sample count at window creation, the resolution
    /// is the window, and anisotropy is selected once per texture at upload.
    /// They are still written immediately so the next boot picks them up.
    /// </summary>
    public sealed class DisplaySettings
    {
        public int WindowWidth { get; set; } = 1600;              // restart
        public int WindowHeight { get; set; } = 900;              // restart
        public bool VSync { get; set; } = true;                   // live
        public int MsaaSamples { get; set; } = 1;                 // restart
        public bool MultisamplingEnabled { get; set; } = true;    // live (the GL enable, not the count)
        public float Anisotropy { get; set; } = 8f;               // restart
        public float UiScale { get; set; } = 1.8f;                // live
        public bool TexturedFrame { get; set; } = true;           // live - WowSkin.Textured
    }

    /// <summary>
    /// How far you can see. DistancePercent is the composite: while
    /// DistanceCustom is false it GENERATES the five values under it through
    /// <see cref="ResolveViewDistance"/>, so two machines at the same percentage
    /// see the same thing. Touching any of the five sets DistanceCustom and the
    /// generator stops.
    /// </summary>
    public sealed class ViewSettings
    {
        public float DistancePercent { get; set; } = 60f;
        public bool DistanceCustom { get; set; }

        public float FieldOfView { get; set; } = 70f;

        public bool FogEnabled { get; set; } = true;
        public float FogStart { get; set; } = 350f;
        public float FogEnd { get; set; } = 777f;
        public bool CullAtFogEnd { get; set; } = true;
        public bool CoupleFarPlaneToFog { get; set; } = true;

        public float BuildingDistance { get; set; } = 777f;
        public float NearPlane { get; set; } = 0.1f;
        public float FarPlane { get; set; } = 2000f;
    }

    /// <summary>Doodads and buildings. Two composites, both with the same custom rule as ViewSettings.</summary>
    public sealed class DetailSettings
    {
        public float ObjectDetailPercent { get; set; } = 55f;
        public bool ObjectDetailCustom { get; set; }

        public float BuildingDetailPercent { get; set; } = 70f;
        public bool BuildingDetailCustom { get; set; }

        // Doodads (M2 props - trees, rocks, fences, furniture).
        public bool Doodads { get; set; } = true;
        public float DoodadDistance { get; set; } = 300f;
        public bool DoodadInstancing { get; set; } = true;
        public bool DoodadFrustumCulling { get; set; } = true;
        public bool DoodadFlatCullBounds { get; set; } = true;
        public float DoodadAlphaCutoff { get; set; } = 0.5f;
        public bool DoodadDemandStreaming { get; set; } = true;

        // Buildings (WMO).
        public bool Buildings { get; set; } = true;
        public bool WmoFrustumCulling { get; set; } = true;
        public bool DistanceLodShells { get; set; } = true;
        public bool ForceTwoSided { get; set; } = true;
        public float WmoAlphaCutoff { get; set; } = 0.35f;
        public int ImpostorMaxVertices { get; set; } = 2000;
        public float InsideMargin { get; set; }
        public float InteriorCullDistance { get; set; } = 120f;
        public float ShellNearGuard { get; set; } = 196f;
        public bool OcclusionCulling { get; set; }
        public float OcclusionMinDistance { get; set; } = 40f;
    }

    /// <summary>
    /// Ground effects - the grass, ferns, flowers and road pebbles. Defaults
    /// mirror FoliageRenderer's own field initialisers; see SYSTEM_FOLIAGE.md
    /// section 4 for what each one means. The three 1.12 switches at the bottom
    /// are authenticity, not performance: turning them off is how the road grows
    /// grass again.
    /// </summary>
    public sealed class ClutterSettings
    {
        public bool Enabled { get; set; } = true;
        public float Density { get; set; } = 0.5f;
        public float Radius { get; set; } = 45f;

        public int MaxPerCell { get; set; } = 6;
        public float Scale { get; set; } = 1.0f;
        public float ScaleJitter { get; set; } = 0.25f;
        public int MaxInstances { get; set; } = 24000;
        public float RescatterDistance { get; set; } = 8f;

        public float WindStrength { get; set; } = 0.06f;
        public float WindSpeed { get; set; } = 1.4f;

        public bool LinkFadeToRadius { get; set; } = true;
        public float FadeStartFraction { get; set; } = 0.66f;
        public float FadeStart { get; set; } = 30f;
        public float FadeEnd { get; set; } = 45f;

        public float AlphaCutoff { get; set; } = 0.4f;
        public float Brightness { get; set; } = 1.0f;

        public bool UseCellLayerMap { get; set; } = true;
        public bool UseNoDoodadMask { get; set; } = true;
        public bool SkipHoles { get; set; } = true;

        /// <summary>
        /// Per-kind curation, keyed by FoliageKind name so a renamed or added
        /// enum member cannot corrupt an old file - an unknown key is ignored and
        /// a missing key keeps the renderer's default.
        /// </summary>
        public Dictionary<string, bool> KindEnabled { get; set; } = new();
        public Dictionary<string, float> KindDensity { get; set; } = new();
    }

    /// <summary>
    /// Liquid look. Defaults are LiquidRenderer's own, which are SYSTEM_WATER.md
    /// Draft 2's near-opaque textured surface - NOT Draft 1's Gerstner waves.
    /// WaveAmplitude 0 is deliberate and is the reversal that doc records.
    /// </summary>
    public sealed class WaterSettings
    {
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// PLAN_12's A/B. On, ocean and river take their close/far colours and
        /// shallow/deep alphas from LightIntBand 13-16 and LightParams. Off is
        /// bit-identical to the pre-PLAN_12 look, which is what makes the
        /// comparison trustworthy rather than approximately trustworthy.
        /// </summary>
        public bool UseAuthoredColors { get; set; } = true;

        public float DetailPercent { get; set; } = 70f;
        public bool DetailCustom { get; set; }

        public float TextureScale { get; set; } = 0.16f;
        public float AnimationFps { get; set; } = 12f;
        public float FrameBlend { get; set; }
        public float TexBrightness { get; set; } = 1f;
        public float TexContrast { get; set; } = 1f;
        public float TintR { get; set; } = 1f;
        public float TintG { get; set; } = 1f;
        public float TintB { get; set; } = 1f;

        public float Opacity { get; set; } = 1.0f;
        public float ShoreFade { get; set; } = 0.85f;
        public float ShoreWidth { get; set; } = 1.2f;

        public float DepthDarken { get; set; } = 0.78f;
        public float DepthRate { get; set; } = 0.12f;

        public float Brightness { get; set; } = 0.90f;
        public float AmbientAmount { get; set; } = 0.6f;
        public float SunAmount { get; set; } = 0.30f;
        public float SkySheen { get; set; } = 0.14f;

        public float WaveAmplitude { get; set; }
        public float WaveSpeed { get; set; } = 1.0f;

        /// <summary>
        /// PLAN_15's A/B: draw MLIQ liquid inside WMOs — Stormwind's canals,
        /// Ironforge's lava channels, Undercity's slime, fountains and indoor
        /// pools. Off is bit-identical to the pre-PLAN_15 client.
        /// </summary>
        public bool DrawWmoLiquid { get; set; } = true;

        /// <summary>
        /// Assumed depth of WMO liquid, in yards. A labelled stand-in, not a look
        /// preference — PLAN_15 D3. WMO pools have no terrain beneath them to
        /// subtract, so until the collision-BVH raycast is built this is what the
        /// shader's depth term gets. Baked per vertex, so changing it rebuilds.
        /// </summary>
        public float WmoLiquidDepth { get; set; } = 3.0f;
    }

    /// <summary>
    /// Sky, sun and ambient. UseAuthoredData is the important one: on, the client
    /// resolves Light.dbc for your position and time; off, it falls back to the
    /// invented constants SYSTEM_EXTERIOR_LIGHTING.md replaced. Leave it on.
    ///
    /// TimeOfDay is here because cycling is a preference, but it is ALSO a
    /// DevTools instrument when pinned - the one control both surfaces keep.
    /// </summary>
    public sealed class LightingSettings
    {
        public bool DynamicLighting { get; set; } = true;
        public bool UseAuthoredData { get; set; } = true;

        public float SunStrength { get; set; } = 1f;
        public float AmbientStrength { get; set; } = 1f;

        /// <summary>Interior baked light scale. 2.0 is vanilla - see SYSTEM_WMO_INTERIOR_LIGHTING.md.</summary>
        public float InteriorBrightness { get; set; } = 2.0f;

        /// <summary>
        /// Doodad baked light scale. MUST track InteriorBrightness or a barrel
        /// detaches from the floor it stands on - SYSTEM_DOODAD_LIGHTING.md's one
        /// invariant. The modal links them unless you unlink deliberately.
        /// </summary>
        public float DoodadInteriorBrightness { get; set; } = 2.0f;
        public bool LinkInteriorBrightness { get; set; } = true;

        public bool WmoVertexColors { get; set; } = true;
        public bool DoodadInteriorLighting { get; set; } = true;

        public bool SkyEnabled { get; set; } = true;
        public float SkyStopMiddle { get; set; } = 0.45f;
        public float SkyStopBand1 { get; set; } = 0.18f;
        public float SkyStopBand2 { get; set; } = 0.06f;

        public bool CycleTimeOfDay { get; set; }
        public float GameHoursPerMinute { get; set; } = 1f;
        public float TimeOfDay { get; set; } = 12f;
    }

    /// <summary>Mouse, camera feel and the free-look knobs a player would expect.</summary>
    public sealed class ControlSettings
    {
        public float MouseSensitivity { get; set; } = 1f;   // multiplier on config.Camera.MouseSensitivity
        public bool InvertPitch { get; set; }
        public bool RawCursor { get; set; } = true;
        public bool CameraCollision { get; set; } = true;
        public float CameraClearance { get; set; } = 0.35f;
        public float CameraRestoreSpeed { get; set; } = 8f;
        public float MaxCameraDistance { get; set; } = 40f;
        public float EyeHeight { get; set; } = 2.2f;
        public float TurnSpeedDegrees { get; set; } = 180f;
    }

    /// <summary>
    /// Residency. Every one of these is restart-scoped except the demand-stream
    /// switch, because the ring sizes are read when the world is built. Read
    /// SYSTEM_STREAMING.md before changing what these mean.
    /// </summary>
    public sealed class StreamingSettings
    {
        public int TileRadius { get; set; } = 1;                  // restart
        public int WmoPreloadRadius { get; set; } = 2;            // restart
        public bool DrainPreloadsAtStartup { get; set; }          // restart
    }

    // ── composites ───────────────────────────────────────────────────────────
    //
    // A composite is a REAL VALUE, not a label. Percent maps to a specific tuple
    // through a documented curve so two machines at 62% look the same. A preset
    // button that scatters four values and then forgets it did is what makes
    // settings menus untrustworthy - PLAN_11 H4.

    private static float Lerp(float a, float b, float t) => a + (b - a) * Math.Clamp(t, 0f, 1f);

    /// <summary>
    /// View distance percent -> fog, building distance and the far plane.
    /// The curve is deliberately gentle at the top: vanilla's unpatched farclip
    /// ceiling was 777 yards, which lands at about 42%, so the whole upper half
    /// of the slider is already beyond what the real client could do.
    /// </summary>
    public void ResolveViewDistance()
    {
        if (View.DistanceCustom) return;

        float t = Math.Clamp(View.DistancePercent / 100f, 0f, 1f);

        View.FogEnd = Lerp(200f, 1600f, t);
        View.FogStart = View.FogEnd * 0.45f;
        View.BuildingDistance = Math.Clamp(View.FogEnd, 300f, 1250f);
        View.FarPlane = Math.Clamp(View.FogEnd * 1.35f, 500f, 4000f);
    }

    /// <summary>Object detail percent -> doodad draw distance and whether nearby-only streaming is on.</summary>
    public void ResolveObjectDetail()
    {
        if (Detail.ObjectDetailCustom) return;

        float t = Math.Clamp(Detail.ObjectDetailPercent / 100f, 0f, 1f);

        Detail.DoodadDistance = Lerp(80f, 800f, t);

        // Above about three quarters the ring is large enough that demand
        // streaming costs more in pop-in than it saves in residency.
        Detail.DoodadDemandStreaming = t < 0.75f;
    }

    /// <summary>
    /// Building detail percent -> the impostor / occlusion set. Note it does NOT
    /// touch BuildingDistance: that belongs to view distance, and two composites
    /// writing one value is how a settings page starts lying to you.
    /// </summary>
    public void ResolveBuildingDetail()
    {
        if (Detail.BuildingDetailCustom) return;

        float t = Math.Clamp(Detail.BuildingDetailPercent / 100f, 0f, 1f);

        // Higher detail = a LOWER impostor threshold, because fewer groups get
        // classified as distance-only shells and more real geometry is drawn.
        Detail.ImpostorMaxVertices = (int)MathF.Round(Lerp(4000f, 700f, t));
        Detail.InteriorCullDistance = Lerp(60f, 220f, t);
        Detail.ShellNearGuard = Lerp(120f, 260f, t);

        // Occlusion culling costs BVH traversal per group and only pays on weak
        // hardware, which is the bottom of this slider.
        Detail.OcclusionCulling = t < 0.4f;
    }

    /// <summary>Water detail percent -> the animation and softness knobs. Never touches the Draft 2 colour set.</summary>
    public void ResolveWaterDetail()
    {
        if (Water.DetailCustom) return;

        float t = Math.Clamp(Water.DetailPercent / 100f, 0f, 1f);

        Water.AnimationFps = Lerp(4f, 24f, t);
        Water.FrameBlend = Lerp(0f, 1f, t);
        Water.ShoreFade = Lerp(1f, 0.75f, t);
        Water.ShoreWidth = Lerp(0.2f, 2.0f, t);
    }

    /// <summary>Run every composite that is not in custom mode. Cheap; call it after any composite moves.</summary>
    public void ResolveComposites()
    {
        ResolveViewDistance();
        ResolveObjectDetail();
        ResolveBuildingDetail();
        ResolveWaterDetail();
    }

    // ── quality presets ──────────────────────────────────────────────────────

    /// <summary>The five built-in levels. Code-defined so they cannot rot in a stale file.</summary>
    public static readonly string[] QualityNames = ["Low", "Fair", "Good", "High", "Ultra"];

    /// <summary>
    /// Overwrite this object with a built-in quality level. Everything it does
    /// not name is left alone deliberately - the 1.12 authenticity switches, the
    /// water colour set and the lighting data source are not quality dials and a
    /// preset has no business moving them.
    /// </summary>
    public void ApplyQuality(string name)
    {
        int level = Array.FindIndex(QualityNames,
            n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        if (level < 0) return;

        float t = level / (float)(QualityNames.Length - 1);   // 0 .. 1

        View.DistanceCustom = false;
        Detail.ObjectDetailCustom = false;
        Detail.BuildingDetailCustom = false;
        Water.DetailCustom = false;

        View.DistancePercent = Lerp(18f, 100f, t);
        Detail.ObjectDetailPercent = Lerp(15f, 100f, t);
        Detail.BuildingDetailPercent = Lerp(20f, 100f, t);
        Water.DetailPercent = Lerp(25f, 100f, t);

        Clutter.Enabled = level >= 1;
        Clutter.Density = Lerp(0.15f, 1.2f, t);
        Clutter.Radius = Lerp(20f, 90f, t);
        Clutter.MaxPerCell = (int)MathF.Round(Lerp(2f, 14f, t));
        Clutter.MaxInstances = (int)MathF.Round(Lerp(6000f, 40000f, t));

        Detail.DoodadInstancing = true;
        Detail.DoodadFlatCullBounds = true;

        Display.MultisamplingEnabled = level >= 3;
        Display.MsaaSamples = level >= 4 ? 4 : 1;
        Display.Anisotropy = Lerp(1f, 16f, t);

        Water.Enabled = true;
        Lighting.SkyEnabled = true;

        ResolveComposites();
        ActivePreset = QualityNames[level];
    }

    // ── serialisation ────────────────────────────────────────────────────────

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    /// <summary>
    /// A deep copy, taken when the modal opens so Cancel has something real to
    /// restore. Round-tripping through JSON rather than hand-writing a copy
    /// constructor is deliberate: a copy constructor is one more place to forget
    /// a field when a setting is added, and this runs once per modal open.
    /// </summary>
    public GameSettings Clone()
        => JsonSerializer.Deserialize<GameSettings>(JsonSerializer.Serialize(this, Json), Json)
           ?? new GameSettings();

    public static GameSettings Defaults() => new();
}

/// <summary>
/// A named set of settings the user saved. Built-in quality levels are NOT
/// stored here - they are code (<see cref="GameSettings.ApplyQuality"/>) so an
/// old settings.json cannot pin them to a stale definition.
/// </summary>
public sealed class SettingsPreset
{
    public string Name { get; set; } = "";
    public GameSettings Settings { get; set; } = new();
}

/// <summary>
/// The file itself. Same shape and same promises as <see cref="VantageStore"/>:
/// repo-root JSON, human-readable, hand-editable, and it NEVER throws on read -
/// a missing or malformed file logs a line and starts from defaults, because
/// refusing to start over a preferences file would be absurd.
/// </summary>
public sealed class SettingsStore
{
    private sealed class FileShape
    {
        public GameSettings Settings { get; set; } = new();
        public List<SettingsPreset> Presets { get; set; } = new();
    }

    private readonly string _path;

    public GameSettings Settings { get; private set; }
    public List<SettingsPreset> Presets { get; }

    /// <summary>True when the file did not exist and the defaults are in play.</summary>
    public bool IsFresh { get; private set; }

    private SettingsStore(string path, GameSettings settings, List<SettingsPreset> presets, bool fresh)
    {
        _path = path;
        Settings = settings;
        Presets = presets;
        IsFresh = fresh;
    }

    /// <summary>Not named Path: a member called Path would hide System.IO.Path inside this class.</summary>
    public string FilePath => _path;

    public static SettingsStore Load(string repoRoot)
    {
        string path = System.IO.Path.Combine(repoRoot, "settings.json");

        try
        {
            if (File.Exists(path))
            {
                var parsed = JsonSerializer.Deserialize<FileShape>(
                    File.ReadAllText(path), GameSettings.Json);

                if (parsed is not null)
                {
                    // Composites regenerate on load rather than being trusted from
                    // the file: a hand-edited percentage should take effect, and a
                    // curve change in a new build should reach an old file.
                    parsed.Settings.ResolveComposites();

                    Console.WriteLine($"[settings] {path}  " +
                                      $"preset '{parsed.Settings.ActivePreset}', " +
                                      $"{parsed.Presets.Count} saved preset(s)");
                    return new SettingsStore(path, parsed.Settings, parsed.Presets, false);
                }
            }
            else
            {
                Console.WriteLine($"[settings] no {path} - starting from shipped defaults");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[settings] could not read {path} - using defaults ({ex.Message})");
        }

        var fresh = GameSettings.Defaults();
        fresh.ResolveComposites();
        return new SettingsStore(path, fresh, new List<SettingsPreset>(), true);
    }

    /// <summary>Replace the live settings object (used by Cancel and by preset load).</summary>
    public void Replace(GameSettings settings) => Settings = settings;

    public SettingsPreset? FindPreset(string name)
    {
        foreach (var p in Presets)
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                return p;
        return null;
    }

    /// <summary>Add or overwrite a named preset from the current settings, then persist.</summary>
    public void SavePreset(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();

        var snapshot = Settings.Clone();
        snapshot.ActivePreset = name;

        var existing = FindPreset(name);
        if (existing is not null) existing.Settings = snapshot;
        else Presets.Add(new SettingsPreset { Name = name, Settings = snapshot });

        Save();
    }

    public void DeletePreset(string name)
    {
        var existing = FindPreset(name);
        if (existing is null) return;
        Presets.Remove(existing);
        Save();
    }

    public void Save()
    {
        try
        {
            var shape = new FileShape { Settings = Settings, Presets = Presets };
            File.WriteAllText(_path, JsonSerializer.Serialize(shape, GameSettings.Json));
            IsFresh = false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[settings] could not write {_path} - {ex.Message}");
        }
    }
}
