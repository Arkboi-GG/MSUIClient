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
public sealed class ClientConfig
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

        /// <summary>Tiles to load around the start tile. 1 = a 3x3 block.</summary>
        public int TileRadius { get; set; } = 1;
    }

    public sealed class RenderConfig
    {
        public float FieldOfView { get; set; } = 70f;
        public float NearPlane { get; set; } = 0.1f;
        public float FarPlane { get; set; } = 2000f;

        /// <summary>
        /// Painterly render mode. The reason this project is native: owning the
        /// renderer makes this a shader variant plus an alternate texture set,
        /// rather than a fight with the platform.
        /// </summary>
        public bool Painterly { get; set; } = false;
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
        /// <summary>Use vmap collision when VmapPath is available.</summary>
        public bool Collision { get; set; } = true;

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

        public float Radius { get; set; } = 0.4f;
        public float Height { get; set; } = 2.1f;

        /// <summary>Ledges up to this tall are stepped onto rather than blocking.</summary>
        public float StepHeight { get; set; } = 1.0f;

        /// <summary>Slopes steeper than this cannot be stood on or climbed.</summary>
        public float MaxSlopeDegrees { get; set; } = 55f;

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
