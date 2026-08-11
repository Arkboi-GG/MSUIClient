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
    /// <summary>Bumped when a rename or a units change needs migration handling.
    /// v2: portal culling (PLAN_10) became the shipped default.
    /// v3: ForceTwoSided went back to being a diagnostic, off by default.
    /// v4: painterly detail became an absolute gain and gained explicit calm/dither controls.
    /// v5: painterly band strength separated subtle flattening from the band count.</summary>
    public int Version { get; set; } = 5;

    /// <summary>Name of the preset last selected, or "Custom". Cosmetic; the values below are the truth.</summary>
    public string ActivePreset { get; set; } = "Custom";

    /// <summary>The last character highlighted on the character-select screen.</summary>
    public ulong LastCharacterGuid { get; set; }

    /// <summary>
    /// What the client launches into: "Client" (the networked SuperUI client) or
    /// "Creator" (the offline spell-creator sandbox). Empty means never chosen -
    /// treated as "Client". Set from the login screen's Launch Options menu and
    /// sticky across sessions. Batch instruments (portrait/variant/movement/live-run)
    /// ignore it entirely.
    /// </summary>
    public string LaunchMode { get; set; } = "";

    public DisplaySettings Display { get; set; } = new();
    public CreatorSettings Creator { get; set; } = new();
    public ViewSettings View { get; set; } = new();
    public DetailSettings Detail { get; set; } = new();
    public ClutterSettings Clutter { get; set; } = new();
    public WaterSettings Water { get; set; } = new();
    public LightingSettings Lighting { get; set; } = new();
    public ControlSettings Controls { get; set; } = new();
    public StreamingSettings Streaming { get; set; } = new();

    // ── groups ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Creator-mode preferences, dialable live from the creator bar's UI panel.
    /// UiScale sizes the panels/buttons/widgets; TextScale sizes the text
    /// independently - one knob scaling both reads as zoom, not as a setting.
    /// The look (race/sex/dials/equipment) is sticky: whatever was worn when a
    /// creator session ended comes back in the next one.
    /// </summary>
    public sealed class CreatorSettings
    {
        public float UiScale { get; set; } = 1f;     // live - MODAL widget/panel sizes
        public float TextScale { get; set; } = 1f;   // live - MODAL font size only

        /// <summary>
        /// Exposes the spell workshop's format-level controls (individual M2
        /// emitters today; bones, tracks, ribbons and other internals as they are
        /// added). The default workshop stays concerned with the recognizable
        /// spell phases, their look and their audio.
        /// </summary>
        public bool SpellAdvancedMode { get; set; }

        // The top menu bar (Character/Gear/Teleport/Target/Spells/UI) sizes
        // independently of the modals - its own button and caption dials.
        public float BarScale { get; set; } = 1f;
        public float BarTextScale { get; set; } = 1f;

        /// <summary>Chrome fill opacity for the creator panels (0.3 - 1).</summary>
        public float PanelAlpha { get; set; } = 0.62f;

        /// <summary>Multiplier over the creator panels' window padding.</summary>
        public float PaddingScale { get; set; } = 1f;

        /// <summary>Multiplier over the creator panels' item spacing.</summary>
        public float SpacingScale { get; set; } = 1f;

        public byte Race { get; set; } = 1;          // ChrRaces id, Human
        public byte Sex { get; set; }                // 0 male, 1 female
        public int[] Dials { get; set; } = new int[5];   // skin, face, hairStyle, hairColor, facialHair
        public List<CreatorPieceSetting> Equipment { get; set; } = new();

        // The last creator-session location. LocMap -1 = never saved; the world
        // then loads at the client-config start position as before.
        public int LocMap { get; set; } = -1;
        public string LocMapName { get; set; } = "";
        public float LocX { get; set; }
        public float LocY { get; set; }
        public float LocZ { get; set; }
        public float LocYaw { get; set; }

        /// <summary>Per-panel ordering of the drill-down sections, user-arranged by
        /// dragging headers. Keyed by panel id; unknown ids are ignored on load.</summary>
        public Dictionary<string, List<string>> SectionOrder { get; set; } = new();

        /// <summary>Sections torn off into their own floating windows, as "panel/section".</summary>
        public List<string> PoppedSections { get; set; } = new();

        /// <summary>Expanded/collapsed state of every drill-down, by stable id - the
        /// arrangement you leave a panel in is the arrangement it reopens with.</summary>
        public Dictionary<string, bool> SectionOpen { get; set; } = new();

        /// <summary>Per-modal layout dials (the gear button on each window). These
        /// multiply ON TOP of the shared modal dials above, so one window can have
        /// its own "perfect" layout without moving the others.</summary>
        public Dictionary<string, PanelTuneSetting> PanelTuning { get; set; } = new();

        /// <summary>Hand-placed widget positions from the gear popup's "Move
        /// buttons" edit mode: [panel][widget key] = offset from the widget's
        /// natural flow position, in unscaled units (scale-independent).</summary>
        public Dictionary<string, Dictionary<string, float[]>> WidgetOffsets { get; set; } = new();
    }

    public sealed class PanelTuneSetting
    {
        public float Text { get; set; } = 1f;      // font size in this window
        public float Widget { get; set; } = 1f;    // slider/input/thumbnail sizing
        public float Button { get; set; } = 1f;    // red button size
        public float Icon { get; set; } = 1f;      // section headers + the +/- art
        public float Spacing { get; set; } = 1f;   // row spacing

        public bool IsNeutral =>
            Text == 1f && Widget == 1f && Button == 1f && Icon == 1f && Spacing == 1f;
    }

    /// <summary>One worn creator piece, as persisted (display id is the visual truth).</summary>
    public sealed class CreatorPieceSetting
    {
        public string Name { get; set; } = "";
        public uint DisplayId { get; set; }
        public int InventoryType { get; set; }
    }

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
        public bool Fullscreen { get; set; }                      // live (Alt+Enter toggles too)
        public bool VSync { get; set; } = true;                   // live
        public int MsaaSamples { get; set; } = 4;                 // restart
        public bool MultisamplingEnabled { get; set; } = true;    // live (the GL enable, not the count)
        public float Anisotropy { get; set; } = 8f;               // restart
        public float UiScale { get; set; } = 1.8f;                // live
        public float FontScale { get; set; } = 1f;                // live - text only, independent of UiScale
        public bool TexturedFrame { get; set; } = true;           // live - WowSkin.Textured

        // Painterly mode (Engine/PainterlyPass.cs) - all live. The shipped
        // crisp-flat profile keeps the source art legible and adds only light
        // value/edge structure. config render.painterly true is a hard-on
        // override for scripted runs.
        public bool Painterly { get; set; }                            // live
        public bool PainterlyUi { get; set; }                          // live, independently styles HUD art
        public float PainterlyBands { get; set; } = 18f;               // live, 3..24 painted value steps
        public float PainterlyBandStrength { get; set; } = 0.30f;      // live, 0..1 blend toward quantized values
        public float PainterlyDetail { get; set; } = 1f;               // live, 0..2 absolute residual gain; 1=source
        public float PainterlyInk { get; set; } = 0.10f;               // live, 0..1 boundary darkening
        public float PainterlyInkThreshold { get; set; } = 0.19f;      // live, 0.01..0.5 edge noise gate
        public float PainterlySilhouette { get; set; } = 0.22f;        // live, 0..1 depth-edge ink
        public float PainterlyDepthFade { get; set; } = 0.35f;         // live, 0..1 aerial perspective strength
        public float PainterlyCalmStart { get; set; } = 60f;           // live, world distance
        public float PainterlyCalmEnd { get; set; } = 240f;            // live, world distance
        public float PainterlySaturation { get; set; } = 1.07f;        // live, 0..2 colour richness; 1=source
        public float PainterlyContrast { get; set; } = 0.18f;          // live, 0..1 value S-curve before banding
        public float PainterlyLift { get; set; } = 1.01f;              // live, 0.5..2 midtone gamma lift; 1=source
        public float PainterlyWarmth { get; set; } = 0.08f;            // live, 0..1 sun/shade split tone
        public float PainterlyGrain { get; set; } = 0f;                // live, 0..1 canvas grain
        public float PainterlyDither { get; set; } = 0.04f;            // live, 0..1 stable band dither
        public int PainterlyCanvasHeight { get; set; } = 1440;         // live, 0=native; HUD remains native
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
        /// <summary>
        /// Draw every WMO batch two-sided. A DIAGNOSTIC, defaulted off.
        ///
        /// It shipped on, and it was the most expensive setting in the client:
        /// backface culling disabled for the pass that is 72-86% of GPU time in
        /// a city, so every wall paid double setup and double fill. No quality
        /// preset overrode it either, which is much of why Low never helped.
        /// Turn it on to tell "the geometry is missing" from "the winding is
        /// inward" in one click — that is what it is for.
        /// </summary>
        public bool ForceTwoSided { get; set; }
        public float WmoAlphaCutoff { get; set; } = 0.35f;
        public int ImpostorMaxVertices { get; set; } = 2000;
        public float InsideMargin { get; set; }
        public float InteriorCullDistance { get; set; } = 120f;
        public float ShellNearGuard { get; set; } = 196f;
        public bool OcclusionCulling { get; set; }
        public float OcclusionMinDistance { get; set; } = 40f;

        // PLAN_10 portal-traversal interior visibility (hides Stormwind's roof from
        // inside, holds the cathedral silhouette across the approach). ON by default
        // now that it is verified in-game - this is the expected 1.12 behaviour. The
        // WMO panel toggle stays for A/B; set false here to boot with it off.
        public bool WmoPortalCulling { get; set; } = true;

        // Per-object appear fade (benilla model_fade.rs): streamed-in doodads and
        // buildings ease in over AppearFadeSeconds instead of popping. On by
        // default; set false to restore the original hard pop-in.
        public bool AppearFade { get; set; } = true;
        public float AppearFadeSeconds { get; set; } = 2f;
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

        /// <summary>Suppress land clutter in cells under water deeper than
        /// <see cref="LiquidFoliageMaxDepth"/>. Grass does not grow in the river.</summary>
        public bool SkipDeepLiquidCells { get; set; } = true;

        /// <summary>Water depth, in yards, above which a cell stops scattering.
        /// Kept small on purpose so reeds at the shallow margin survive.</summary>
        public float LiquidFoliageMaxDepth { get; set; } = 0.75f;

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
        /// PLAN_12's A/B: take ocean/river colour from LightIntBand 13-16 instead
        /// of the hand-tuned constants.
        ///
        /// **DEFAULT FLIPPED TO FALSE, 2026-07-26.** This shipped default-ON and
        /// it ruins the water: `water.frag` MULTIPLIES the animated liquid texture
        /// by the band colour, Azeroth's authored river-close is
        /// `(0.000, 0.114, 0.161)` with red exactly zero, and vanilla's
        /// `lake_a.N.blp` frames ARE the bright animated highlight layer. Multiply
        /// the highlights by near-black and the river goes dark and monocolour --
        /// which is exactly what it did.
        ///
        /// The band INDEXING is correct (verified against wowdev and against our
        /// own sky, which is right). The values are real. **The interpretation is
        /// wrong: these are not a texture tint.** Two more signs -- the authored
        /// alphas are shallow 0.65 / deep 0.50 and ocean 1.00 / 0.75, i.e. shallow
        /// MORE opaque than deep, which is backwards for depth and sensible for
        /// camera distance; and across all 426 LightParams the close/far pairs have
        /// no systematic brightness ordering at all (river 156 vs 95, ocean 91 vs
        /// 84), which they would if they were shallow/deep.
        ///
        /// WoWee settles it: it loads all 18 colour bands, consumes seven
        /// (ambient, diffuse, fog, four sky), comments *"more channels exist
        /// (ocean, river, shadow, etc.)"* and hardcodes water colour per liquid
        /// type instead. See SYSTEM_WATER.md section 5.
        ///
        /// Leave this OFF until someone establishes what these bands actually
        /// drive in the real client. Off is bit-identical to the tuned look.
        /// </summary>
        public bool UseAuthoredColors { get; set; }

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
        /// River/lake body colour. THE WATER TEXTURE SUPPLIES NO COLOUR --
        /// lake_a.1.blp is a near-black greyscale highlight mask, measured mean
        /// RGB (0.014, 0.014, 0.014) -- so this is where the river gets its
        /// colour. Shallow and deep are derived from it. SYSTEM_WATER.md section 8.
        /// </summary>
        public float RiverBodyR { get; set; } = 0.13f;
        public float RiverBodyG { get; set; } = 0.16f;
        public float RiverBodyB { get; set; } = 0.17f;

        /// <summary>Ocean body colour. Same story as RiverBody.</summary>
        public float OceanBodyR { get; set; } = 0.04f;
        public float OceanBodyG { get; set; } = 0.16f;
        public float OceanBodyB { get; set; } = 0.38f;

        /// <summary>How hard the animated highlight mask is added over the body.
        /// 0 = a completely still surface, useful for judging the body colour alone.</summary>
        public float HighlightGain { get; set; } = 4.0f;

        /// <summary>
        /// The walking wake (PLAN_16): the trail you leave wading through water,
        /// stamped from Blizzard's own XTextures\splash\wake.blp. WakeStrength 0
        /// is the kill switch and restores a bit-identical pre-PLAN_16 surface.
        /// </summary>
        public bool WakeEnabled { get; set; } = true;
        public float WakeStrength { get; set; } = 0.9f;
        public float WakeLength { get; set; } = 4.5f;
        public float WakeWidth { get; set; } = 2.6f;
        public float WakeAhead { get; set; } = 0.6f;
        public float WakeFullSpeed { get; set; } = 2.5f;
        public float WakeFade { get; set; } = 0.45f;
        public float WakeRepeat { get; set; } = 2.5f;
        public float WakeWorldLock { get; set; } = 1.0f;
        public float WakeOpacity { get; set; } = 0.40f;
        public float WakeColorR { get; set; } = 0.30f;
        public float WakeColorG { get; set; } = 0.36f;
        public float WakeColorB { get; set; } = 0.42f;
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
        public float TerrainShadowStrength { get; set; } = 0.3f;
        public float UnitShadowOpacity { get; set; } = 0.42f;

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

        /// <summary>CRPG/RTS command strips beside the party portraits (roles, hold, patrol).</summary>
        public bool RtsCommands { get; set; }
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

    public static SettingsStore Load(string repoRoot, string? overridePath = null)
    {
        string path = string.IsNullOrWhiteSpace(overridePath)
            ? System.IO.Path.Combine(repoRoot, "settings.json")
            : System.IO.Path.GetFullPath(overridePath);

        try
        {
            if (File.Exists(path))
            {
                string rawJson = File.ReadAllText(path);
                bool serializedPainterlyDetail = HasSerializedPainterlyDetail(rawJson);
                var parsed = JsonSerializer.Deserialize<FileShape>(
                    rawJson, GameSettings.Json);

                if (parsed is not null)
                {
                    // Composites regenerate on load rather than being trusted from
                    // the file: a hand-edited percentage should take effect, and a
                    // curve change in a new build should reach an old file.
                    parsed.Settings.ResolveComposites();
                    Migrate(parsed.Settings, serializedPainterlyDetail);

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

    /// <summary>
    /// One-time forward migrations keyed on <see cref="GameSettings.Version"/>, so a
    /// new shipped default reaches an existing settings.json instead of being pinned
    /// to a stale value. Each step is idempotent and bumps the version; the user's
    /// later choices (saved at the new version) are then respected.
    /// </summary>
    private static bool HasSerializedPainterlyDetail(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });

        return TryGetProperty(document.RootElement, "Settings", out JsonElement settings) &&
               TryGetProperty(settings, "Display", out JsonElement display) &&
               TryGetProperty(display, "PainterlyDetail", out _);

        static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        value = property.Value;
                        return true;
                    }
                }
            }

            value = default;
            return false;
        }
    }

    private static void Migrate(GameSettings s, bool serializedPainterlyDetail)
    {
        // v1 -> v2: WMO portal culling (PLAN_10) became the shipped default - it is
        // the expected 1.12 behaviour (hides Stormwind's roof from inside, holds the
        // cathedral silhouette on approach). Force it on once for pre-v2 files.
        if (s.Version < 2)
        {
            s.Detail.WmoPortalCulling = true;
            s.Version = 2;
        }

        // v2 -> v3: ForceTwoSided goes back to being the diagnostic it was
        // written as. It shipped ON, which disabled backface culling for the
        // whole WMO pass - the pass that is most of the frame in a city, on a
        // GPU with no hidden-surface removal. Settings.Detail wins over the
        // renderer's own default (see ApplyDetail), so without this step an
        // existing settings.json would pin the old value forever and the change
        // would never reach anyone who has already run the client.
        if (s.Version < 3)
        {
            s.Detail.ForceTwoSided = false;
            s.Version = 3;
        }

        // v3 -> v4: Detail used to be an additive boost where zero still kept
        // all source detail. It is now an honest absolute gain: 0 removes the
        // residual and 1 preserves it. Preserve old files' appearance first;
        // users can then simplify from a meaningful zero. The new calm and
        // dither fields need their new defaults seeded as part of the versioned
        // transition. Property initializers apply to missing JSON fields, so use
        // the raw-file presence check to distinguish a real legacy Detail value
        // from the new absolute-gain initializer.
        if (s.Version < 4)
        {
            s.Display.PainterlyDetail = serializedPainterlyDetail
                ? Math.Clamp(1f + s.Display.PainterlyDetail, 0f, 2f)
                : 1f;
            s.Display.PainterlyCalmStart = 35f;
            s.Display.PainterlyCalmEnd = 180f;
            s.Display.PainterlyDither = 0.18f;
            s.Display.PainterlyUi = s.Display.Painterly;
            s.Version = 4;
        }

        // v4 -> v5: band count no longer implies full-strength posterization.
        // Preserve every existing painterly choice and seed only the new blend.
        if (s.Version < 5)
        {
            s.Display.PainterlyBandStrength = 0.30f;
            s.Version = 5;
        }
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
