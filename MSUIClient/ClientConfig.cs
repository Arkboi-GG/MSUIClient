using System.Text.Json;
using System.Text.Json.Serialization;

namespace MSUIClient;

/// <summary>
/// Runtime configuration, loaded from client-config.json.
///
/// The native client reads the WoW MPQs DIRECTLY off local disk — no asset
/// server, no bake step, no HTTP. That is the single biggest simplification
/// over the abandoned browser build, where the whole export pipeline existed
/// only because a browser cannot open an MPQ.
///
/// PATH RESOLUTION
///   Relative paths in this file resolve against the REPO ROOT, not the working
///   directory. That matters because `dotnet run`, F5 from Visual Studio, and a
///   published exe all use different working directories — a bare relative path
///   would silently mean three different folders. The repo root is found by
///   walking up from the exe looking for MSUIClient.sln.
///
///   So "GameData\Data" always means &lt;repo&gt;/GameData/Data, however you
///   launched. Absolute paths pass through untouched, which is what a
///   distributed build would use.
/// </summary>
public sealed partial class ClientConfig
{
    /// <summary>
    /// WoW 1.12.1 Data directory containing the .MPQ archives.
    /// Relative to the repo root; "GameData\Data" is the self-contained layout.
    /// </summary>
    public string ClientDataPath { get; set; } = "";

    /// <summary>
    /// VMaNGOS vmaps directory (.vmtile + .vmo). Optional: without it terrain
    /// collision still works from MCVT heights, but you will walk through
    /// buildings, trees and fences.
    /// </summary>
    public string? VmapPath { get; set; }

    /// <summary>realmd host, for Phase 2. Unused while the client is offline.</summary>
    public string RealmdHost { get; set; } = "192.168.0.2";
    public int RealmdPort { get; set; } = 3724;

    /// <summary>
    /// Master switch for all developer tooling - the in-game overlay, scene
    /// dumps, vantage capture, the group picker and reason readouts. Everything
    /// behind this flag lives in the DevTools layer (Program.DevTools.cs) and is
    /// meant to ship OFF in a release build; see FOUNDATION_PLAN.md section 12.
    /// Core rendering and movement do not depend on it.
    /// </summary>
    public bool DevTools { get; set; } = true;

    public WindowConfig Window { get; set; } = new();
    public StartConfig Start { get; set; } = new();
    public RenderConfig Render { get; set; } = new();
    public CameraConfig Camera { get; set; } = new();
    public MovementConfig Movement { get; set; } = new();

    /// <summary>Repo root, resolved at load. Also used to locate Shaders/.</summary>
    [JsonIgnore]
    public string RepoRoot { get; private set; } = "";

    /// <summary>True when VmapPath survived validation and holds real files.</summary>
    [JsonIgnore]
    public bool HasVmaps => !string.IsNullOrWhiteSpace(VmapPath);

    public sealed class WindowConfig
    {
        public int Width { get; set; } = 1600;
        public int Height { get; set; } = 900;
        public bool VSync { get; set; } = true;
        public bool Fullscreen { get; set; }
        public string Title { get; set; } = "MSUI Client";

        /// <summary>
        /// ImGui scale factor. The default ImGui font is tiny on a high-DPI
        /// panel; 1.0 is native, 2.0 is comfortable on a 4K laptop screen.
        /// Scales fonts and every widget metric together.
        /// </summary>
        public float UiScale { get; set; } = 1.8f;
    }

    /// <summary>
    /// Where to drop the camera on launch. Defaults to the Northshire human
    /// start from playercreateinfo — map 0, tile [col 32, row 48], a position
    /// the server's own height data agreed with to 0.00.
    /// </summary>
    public sealed class StartConfig
    {
        public int Map { get; set; } = 0;
        public string MapName { get; set; } = "Azeroth";
        public float X { get; set; } = -8949.95f;
        public float Y { get; set; } = -132.493f;
        public float Z { get; set; } = 83.5312f;
        public float Orientation { get; set; } = 0f;

        /// <summary>Resident terrain ring radius. 1 = a moving 3x3 block.</summary>
        public int TileRadius { get; set; } = 1;

        /// <summary>
        /// WMO asset preload ring. 2 = keep the visible 3x3 terrain block but
        /// parse and upload buildings referenced by the surrounding 5x5 block.
        /// The extra RAM buys roughly one full tile of warning before a WMO can
        /// become resident.
        /// </summary>
        public int WmoPreloadRadius { get; set; } = 2;

        /// <summary>
        /// Legacy startup mode. When true, block the first frame until every
        /// speculative WMO and M2 in the outer preload ring is resident. The
        /// default starts after the visible set is ready and warms that ring
        /// through the normal background streaming pipeline.
        /// </summary>
        public bool DrainPreloadsAtStartup { get; set; } = false;
    }

    public sealed class RenderConfig
    {
        public float FieldOfView { get; set; } = 70f;
        public float NearPlane { get; set; } = 0.1f;
        public float FarPlane { get; set; } = 2000f;

        /// <summary>
        /// Multisample antialiasing for geometry silhouettes. Keep the default
        /// single-sampled: integrated GPUs pay a material bandwidth and resolve
        /// cost for 4x, and a post-process AA pass is the better future trade.
        /// </summary>
        public int MsaaSamples { get; set; } = 1;

        /// <summary>
        /// Texture filtering for surfaces viewed at an angle. The driver clamps
        /// this to the hardware limit; 8x removes most terrain/WMO shimmer.
        /// </summary>
        public float Anisotropy { get; set; } = 8f;

        /// <summary>
        /// Draw M2 doodads - trees, rocks, fences, props. Around 785 placements
        /// per tile, so this is the single biggest change to how the world
        /// looks, and the single biggest cost at load.
        /// </summary>
        public bool Doodads { get; set; } = true;

        /// <summary>
        /// Doodads further than this from the camera are skipped. Vanilla's own
        /// small-object draw distance is in this range; raising it costs draw
        /// calls fast, because there are thousands of them.
        /// </summary>
        public float DoodadDistance { get; set; } = 300f;

        /// <summary>
        /// WMO group visibility distance. Vanilla 1.12's unpatched farclip
        /// ceiling was 777 yards; assets remain preloaded beyond this boundary
        /// but are fully fogged and omitted from draw submission.
        /// </summary>
        public float WmoDistance { get; set; } = 777f;

        /// <summary>
        /// Axis basis for M2 COLLISION hulls. An M2 stores render vertices
        /// Y-up and collision vertices Z-up, so the hull needs a conversion the
        /// render mesh does not. 2 = (x,y,z) -> (x,z,-y), measured against the
        /// render bounds of 127 models. 1 is the same axes flipped end for end.
        /// </summary>
        public int DoodadCollisionBasis { get; set; } = 2;

        /// <summary>
        /// Painterly render mode. The reason this project is native: owning the
        /// renderer makes this a shader variant plus an alternate texture set,
        /// rather than a fight with the platform.
        /// </summary>
        public bool Painterly { get; set; } = false;

        /// <summary>
        /// FFXGlow whole-scene bloom (Engine/FfxGlow.cs) - the reference client's
        /// full-screen glow. Composited ADDITIVELY, so the base scene (exterior
        /// lighting, fog and colour) is untouched and only highlight bloom is added
        /// on top. The portal 'glaze' is this pass blooming the additive particle
        /// sprites.
        /// </summary>
        public bool Glow { get; set; } = false;

        /// <summary>
        /// Per-zone glow weight (benilla LightParams.glow: default 0.5, about 0.647
        /// in Elwynn). Multiplies blur^2. Lower it to soften the whole-scene bloom
        /// on the exterior; 0 disables the pass. Only the square-law highlight bloom
        /// is affected, never the base image.
        /// </summary>
        public float GlowGain { get; set; } = 0.5f;

        /// <summary>
        /// Global multiplier on every particle emitter's spawn RATE (thickness of
        /// the particle layer). 1.0 = authored. Lower it to thin a too-dense effect
        /// like the portal into a translucent film; higher packs more in.
        /// </summary>
        public float ParticleDensity { get; set; } = 0.89f;

        /// <summary>
        /// Draw the map's real WoW loading-screen art on the loading curtain
        /// (Map.dbc field 38 -> LoadingScreens.dbc -> BLP). On by default; it
        /// always falls back to the plain dark curtain if the art can't be
        /// resolved or decoded, so this is a kill switch, not a requirement.
        /// Kept out of the in-game menu on purpose - it is a wiring-level choice.
        /// </summary>
        public bool LoadingScreenArt { get; set; } = true;
    }

    /// <summary>
    /// Mouse look and camera collision.
    /// </summary>
    public sealed class CameraConfig
    {
        /// <summary>Radians of rotation per pixel of mouse movement.</summary>
        public float MouseSensitivity { get; set; } = 0.004f;

        /// <summary>
        /// Flip the vertical look axis. False is standard — push the mouse up,
        /// look up.
        /// </summary>
        public bool InvertPitch { get; set; } = false;

        /// <summary>
        /// Pull the camera in when terrain or a building is between it and the
        /// character. Off means the camera happily sits underground and you can
        /// see through the world from below.
        /// </summary>
        public bool Collision { get; set; } = true;

        /// <summary>How far in front of a surface the camera stops, in yards.</summary>
        public float Clearance { get; set; } = 0.35f;

        /// <summary>
        /// Yards per second the camera eases back out once the obstruction is
        /// gone. Pulling in is instant; pushing out is not, because a camera
        /// that snaps outward every time you clear a doorway is nauseating.
        /// </summary>
        public float RestoreSpeed { get; set; } = 8f;
    }

    /// <summary>
    /// Character movement and collision.
    ///
    /// The speed and gravity defaults are vanilla's own constants, not taste.
    /// RunSpeed 7.0 and the 19.29 gravity are what the 1.12 client uses, so
    /// matching them now means Phase 2 movement packets describe motion the
    /// server already expects. Change them for debugging, not for feel.
    /// </summary>
    public sealed class MovementConfig
    {
        /// <summary>Use collision at all.</summary>
        public bool Collision { get; set; } = true;

        /// <summary>
        /// Where solid geometry comes from.
        ///
        ///   "client"  the WMO triangles the renderer already loaded, filtered
        ///             by their MOPY flags. This is what the real 1.12 client
        ///             does — it has no vmaps and never needed them, because
        ///             the geometry it draws is the geometry it collides with.
        ///             One chain, so the wall you see and the wall you hit
        ///             cannot disagree. Needs no GameDatamaps at all.
        ///
        ///   "vmaps"   the server's extracted .vmo meshes. A second copy of the
        ///             same buildings through a second transform. Useful as a
        ///             cross-check against what the server believes, and the
        ///             only source of tree and fence collision until M2 doodads
        ///             are loaded.
        ///
        /// CAVEAT while doodads are unimplemented: "client" gives buildings only.
        /// Trees, fences and rocks are M2 doodads and are not solid yet.
        /// </summary>
        public string CollisionSource { get; set; } = "client";

        /// <summary>
        /// Include MOD_M2 spawns — trees, fences, small props. On means you
        /// cannot walk through a tree trunk. Some cores treat M2 vmaps as
        /// line-of-sight only, so if Elwynn feels obstructed by invisible
        /// canopy, turn this off and re-check.
        /// </summary>
        public bool IncludeM2 { get; set; } = true;

        /// <summary>Vanilla default run speed, yards/second.</summary>
        public float RunSpeed { get; set; } = 7.0f;
        public float WalkSpeed { get; set; } = 2.5f;

        /// <summary>Vanilla MOVE_RUN_BACK speed, yards/second.</summary>
        public float BackwardSpeed { get; set; } = 4.5f;

        public float Radius { get; set; } = 0.4f;
        public float Height { get; set; } = 2.1f;

        /// <summary>
        /// Ledges up to this tall are stepped onto rather than blocking.
        ///
        /// A yard was too generous. Combined with the 55-degree slope gate below
        /// it let the character walk up rocks, crates and terrain that the
        /// reference makes you jump, which is a feel difference you notice
        /// everywhere in the world without ever being able to point at one
        /// obstacle and call it wrong. 0.7 is the reference's own step ceiling.
        /// </summary>
        public float StepHeight { get; set; } = 0.7f;

        /// <summary>
        /// When a previously grounded character moves onto a slightly lower
        /// surface, keep its feet attached by this many yards instead of
        /// producing a one-frame airborne state. This is ground adhesion, not
        /// extra step-up height: it only acts downward and never during a jump.
        /// </summary>
        public float GroundSnapDistance { get; set; } = 0.5f;

        /// <summary>
        /// Delay the visual falling pose for an uncommanded loss of support.
        /// Physics starts immediately; deliberate jumps still animate
        /// immediately. This filters brief misses on stairs and narrow props.
        /// </summary>
        public float FallAnimationDelayMs { get; set; } = 180f;

        /// <summary>
        /// Slopes steeper than this cannot be stood on or climbed.
        ///
        /// Fifty, not fifty-five: cos(50 degrees) is the constant the reference's
        /// own step-versus-fall election tests a surface normal against, so it is
        /// the boundary between "this is a floor" and "this is a wall" that the
        /// world's geometry was authored around.
        /// </summary>
        public float MaxSlopeDegrees { get; set; } = 50f;

        public float Gravity { get; set; } = 19.29110527f;
        public float JumpVelocity { get; set; } = 7.9558f;
        public float TerminalVelocity { get; set; } = 60.148f;

        /// <summary>Free-fly speed, and the Shift multiplier on it.</summary>
        public float FlySpeed { get; set; } = 30f;
        public float FlyBoost { get; set; } = 5f;

        /// <summary>Start in free-fly rather than walking. F toggles at runtime.</summary>
        public bool StartFlying { get; set; } = false;
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    /// <summary>
    /// Walk up from the executable looking for MSUIClient.sln. Falls back to the
    /// exe folder if the build was published outside the repo tree.
    /// </summary>
    public static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MSUIClient.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        return AppContext.BaseDirectory;
    }

    /// <summary>Absolute paths pass through; relative paths resolve against the repo root.</summary>
    public static string ResolvePath(string repoRoot, string path)
        => Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(repoRoot, path));

    public static ClientConfig Load(string? explicitPath = null)
    {
        string repoRoot = FindRepoRoot();
        string path = explicitPath ?? FindConfigFile(repoRoot);

        if (!File.Exists(path))
        {
            var fresh = new ClientConfig { ClientDataPath = @"GameData\Data" };
            File.WriteAllText(path, JsonSerializer.Serialize(fresh, Options));
            throw new FileNotFoundException(
                $"No config found — wrote a default to {path}. " +
                "Set ClientDataPath to your WoW 1.12.1 Data folder and run again.");
        }

        var config = JsonSerializer.Deserialize<ClientConfig>(File.ReadAllText(path), Options)
                     ?? throw new InvalidDataException($"{path} did not parse as JSON");

        config.RepoRoot = repoRoot;
        Console.WriteLine($"[config] file      {path}");
        Console.WriteLine($"[config] repo root {repoRoot}");

        config.Validate();
        return config;
    }

    /// <summary>Next to the exe first (the csproj copies it there), then project folder, then repo root.</summary>
    private static string FindConfigFile(string repoRoot)
    {
        const string name = "client-config.json";

        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, name),
            Path.Combine(repoRoot, "MSUIClient", name),
            Path.Combine(repoRoot, name),
        };

        foreach (var c in candidates)
            if (File.Exists(c)) return c;

        return candidates[0];
    }

    private void Validate()
    {
        if (string.IsNullOrWhiteSpace(ClientDataPath))
            throw new InvalidDataException("ClientDataPath is empty in client-config.json");

        // Resolve in place so everything downstream sees absolute paths.
        ClientDataPath = ResolvePath(RepoRoot, ClientDataPath);

        if (!Directory.Exists(ClientDataPath))
            throw new DirectoryNotFoundException(
                $"ClientDataPath does not exist: {ClientDataPath}\n" +
                "  Run setup-gamedata.ps1 to populate GameData\\Data, or point " +
                "ClientDataPath at an existing WoW 1.12.1 Data folder.");

        var mpqs = Directory.GetFiles(ClientDataPath, "*.MPQ", SearchOption.TopDirectoryOnly);
        if (mpqs.Length == 0)
            throw new InvalidDataException(
                $"No .MPQ files in {ClientDataPath} — is this really the client Data folder?");

        Console.WriteLine($"[config] {mpqs.Length} MPQ archive(s) in {ClientDataPath}");

        ValidateVmaps();
    }

    /// <summary>
    /// Vmaps are optional throughout: every failure here degrades to
    /// terrain-only collision with a printed reason, and never throws. Walking
    /// through a wall is a nuisance; refusing to start is worse.
    /// </summary>
    private void ValidateVmaps()
    {
        if (string.IsNullOrWhiteSpace(VmapPath))
        {
            Console.WriteLine("[config] VmapPath not set — terrain collision only, no buildings");
            VmapPath = null;
            return;
        }

        VmapPath = ResolvePath(RepoRoot, VmapPath);

        if (!Directory.Exists(VmapPath))
        {
            Console.WriteLine($"[config] WARNING VmapPath does not exist: {VmapPath}");
            Console.WriteLine("[config] terrain collision only — copy the server's run/data/vmaps there");
            VmapPath = null;
            return;
        }

        int tiles = Directory.GetFiles(VmapPath, "*.vmtile").Length;
        int models = Directory.GetFiles(VmapPath, "*.vmo").Length;

        if (tiles == 0 && models == 0)
        {
            Console.WriteLine($"[config] WARNING {VmapPath} has no .vmtile or .vmo files — ignoring");
            VmapPath = null;
            return;
        }

        Console.WriteLine($"[config] vmaps: {tiles} tile(s), {models} model(s)");

        if (!Movement.Collision)
            Console.WriteLine("[config] movement.collision is false — vmaps loaded but unused");
    }
}
