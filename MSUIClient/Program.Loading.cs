using System.Diagnostics;
using System.Numerics;
using Silk.NET.OpenGL;
using MSUIClient.Engine;
using MSUIClient.World;

namespace MSUIClient;

/// <summary>
/// Benilla-style incremental world load (see BENILLA_VS_MSUI_LOADING.md).
///
/// The old path built the entire resident zone - terrain, every building and the
/// collision BVH - synchronously inside the GL Load callback, before the render
/// loop presented a single frame, so the window was frozen for the whole
/// multi-second build with no loading screen. benilla instead shows a loading
/// screen and only lifts it once the world around the player is resident,
/// streaming the far world in behind a fade.
///
/// <see cref="BeginWorldLoad"/> kicks off the async terrain/WMO/doodad streaming
/// the client already uses and returns immediately; <see cref="StepWorldLoad"/>
/// is pumped once per frame from Update and advances a phase machine, each phase
/// reusing the existing streaming methods (QueuePreload / WarmNextPreload /
/// SetResidency / PopulateDoodads / BeginCollisionBuild) rather than the blocking
/// LoadAround / LoadForTiles / LoadCollision.
///
/// CRUCIALLY the curtain is held until the near DOODADS are resident too, not
/// just terrain + buildings - otherwise the screen lifts onto an empty world and
/// the props visibly pop in over the following ~30 s. Draining them behind the
/// curtain (now fast, because MpqMount reads in parallel) means the world is
/// populated the moment you gain control, like 1.12 / benilla.
/// </summary>
public sealed partial class GameLoop
{
    private LoadingScreen? _loadScreen;
    private bool _worldLoading;
    private float _loadProgress;
    private float _loadCurtainAlpha = 1f;
    private int _wmoWarmTotal = 1;
    private int _doodadWarmTotal = 1;
    private (int col, int row) _loadCentre;
    private WorldLoadPhase _loadPhase = WorldLoadPhase.Done;
    private readonly Stopwatch _loadClock = new();
    private readonly Stopwatch _loadPhaseClock = new();

    /// <summary>Main-thread ms spent draining a warm queue per frame while the curtain is up.</summary>
    private const float LoadWarmBudgetMs = 12f;

    /// <summary>Curtain fade-out length once the world is ready.</summary>
    private const float LoadFadeSeconds = 0.5f;

    /// <summary>Force-advance a phase after this long so a hang never sticks on the curtain.</summary>
    private const float LoadPhaseWatchdogSeconds = 30f;

    private enum WorldLoadPhase
    {
        Terrain,
        WarmBuildings,
        PlaceBuildings,
        Liquid,
        WarmDoodads,
        PlaceDoodads,
        Collision,
        Finish,
        Fade,
        Done,
    }

    /// <summary>True while the loading curtain is up (Update skips gameplay).</summary>
    public bool WorldLoading => _worldLoading;

    /// <summary>
    /// Arm the incremental load. Called at the end of Load once every renderer
    /// exists (empty) and the controller has been created. Returns immediately:
    /// the heavy build is driven by StepWorldLoad across the following frames.
    /// </summary>
    private void BeginWorldLoad(GL gl)
    {
        _loadCentre = _residentCentre
            ?? TerrainRenderer.TileAt(_config.Start.X, _config.Start.Y);
        _residentCentre = _loadCentre;

        _loadScreen = new LoadingScreen(gl);
        _worldLoading = true;
        _loadProgress = 0f;
        _loadCurtainAlpha = 1f;
        _loadClock.Restart();
        SetLoadPhase(WorldLoadPhase.Terrain);

        // Start the terrain ring streaming off-thread now (parse + mesh + upload
        // all run on the worker pool + shared-context uploader). One tile past
        // the resident radius, matching the crossing lead.
        _terrain?.QueuePreload(
            TerrainRenderer.TileRing(_loadCentre.col, _loadCentre.row, _config.Start.TileRadius + 1),
            _adts!);

        // Start warming the resident ring's building models off-thread too, so the
        // placement pass below is a cache hit instead of a blocking resolve.
        _wmo?.QueuePreloadForTiles(
            TerrainRenderer.TileRing(_loadCentre.col, _loadCentre.row, _config.Start.TileRadius),
            _adts!);
        _wmoWarmTotal = Math.Max(1, _wmo?.PendingPreloads ?? 1);

        Console.WriteLine("[load] streaming world behind loading screen " +
                          $"(centre tile [{_loadCentre.col},{_loadCentre.row}], " +
                          $"radius {_config.Start.TileRadius})");
    }

    private void SetLoadPhase(WorldLoadPhase phase)
    {
        _loadPhase = phase;
        _loadPhaseClock.Restart();
    }

    private bool PhaseTimedOut => _loadPhaseClock.Elapsed.TotalSeconds > LoadPhaseWatchdogSeconds;

    private void BumpProgress(float p) => _loadProgress = MathF.Max(_loadProgress, p);

    /// <summary>
    /// One frame of the world build. Pumped from Update while WorldLoading. Each
    /// phase does a bounded slice of work (or waits on the async streamers) so no
    /// single frame blocks for long - the world fills in behind the curtain and
    /// the curtain lifts only when the near world (terrain + buildings + doodads
    /// + collision) is resident.
    /// </summary>
    private void StepWorldLoad(float dt)
    {
        // Adopt whatever the async streamers have finished this frame.
        _terrain?.PumpPreloads();
        AcceptReadyCollision();

        var c = _loadCentre;
        int radius = _config.Start.TileRadius;

        switch (_loadPhase)
        {
            case WorldLoadPhase.Terrain:
            {
                var ring = TerrainRenderer.TileRing(c.col, c.row, radius);
                int ready = 0;
                foreach (var t in ring)
                    if (_terrain is not null && _terrain.PreloadReady(new[] { t })) ready++;
                BumpProgress(0.04f + 0.18f * ready / Math.Max(1, ring.Count));

                if (_terrain is null || _terrain.PreloadReady(ring) || PhaseTimedOut)
                {
                    // All resident tiles prepared off-thread -> adopting them here
                    // just creates the small VAOs, no MPQ/parse on this thread.
                    _terrain?.SetResidency(c.col, c.row, radius, _adts!);
                    SetLoadPhase(WorldLoadPhase.WarmBuildings);
                }
                break;
            }

            case WorldLoadPhase.WarmBuildings:
            {
                DrainWarm(() => _wmo?.WarmNextPreload() ?? false);
                int pending = _wmo?.PendingPreloads ?? 0;
                BumpProgress(0.22f + 0.20f * (1f - pending / (float)_wmoWarmTotal));
                if (pending == 0 || PhaseTimedOut)
                    SetLoadPhase(WorldLoadPhase.PlaceBuildings);
                break;
            }

            case WorldLoadPhase.PlaceBuildings:
            {
                // Models are warm, so placement is cache-hit fast. Then queue the
                // outer ring for the first crossings (it streams after the curtain).
                _wmo?.ResetPlacements();
                if (_wmo is not null && _terrain is not null)
                    _wmo.LoadForTiles(_terrain.LoadedTiles, _adts!);
                _wmo?.QueuePreloadForTiles(
                    TerrainRenderer.TileRing(c.col, c.row, WmoPreloadRadius), _adts!);
                BumpProgress(0.48f);
                SetLoadPhase(WorldLoadPhase.Liquid);
                break;
            }

            case WorldLoadPhase.Liquid:
            {
                if (_liquid is not null && _terrain is not null)
                    _liquid.LoadForTiles(_terrain.LoadedTiles, _adts!);

                // Queue the near doodads (outdoor MDDF + resident WMO interiors)
                // so WarmDoodads can drain them behind the curtain.
                if (_doodads is not null && _terrain is not null)
                {
                    var centreV = new Vector2(_config.Start.X, _config.Start.Y);
                    float demand = DoodadDemandRadius;
                    _doodads.QueuePreloadForTiles(_terrain.LoadedTiles, _adts!, centreV, demand);
                    if (_wmo is not null)
                        _doodads.QueuePreloadModels(
                            _wmo.EnumerateDoodads(centreV, demand)
                                .Select(d => d.ModelPath)
                                .Distinct(StringComparer.OrdinalIgnoreCase));
                    _doodadWarmTotal = Math.Max(1, _doodads.PendingPreloads);
                }

                _adts?.Retain(TerrainRenderer.TileRing(c.col, c.row, WmoPreloadRadius));
                BumpProgress(0.50f);
                SetLoadPhase(WorldLoadPhase.WarmDoodads);
                break;
            }

            case WorldLoadPhase.WarmDoodads:
            {
                // THE FIX: drain the near doodads while the curtain is up. With
                // MpqMount reading in parallel the workers prepare these fast, so
                // this is a few seconds, not the ~30 s of visible pop-in it used
                // to be after the screen had already lifted.
                DrainWarm(() => _doodads?.WarmNextPreload() ?? false);
                int pending = _doodads?.PendingPreloads ?? 0;
                BumpProgress(0.50f + 0.40f * (1f - pending / (float)_doodadWarmTotal));
                if (pending == 0 || PhaseTimedOut)
                    SetLoadPhase(WorldLoadPhase.PlaceDoodads);
                break;
            }

            case WorldLoadPhase.PlaceDoodads:
            {
                // Place every warmed model's instances (outdoor + WMO interior) in
                // one shot, behind the curtain, so the reveal is fully populated.
                if (_doodads is not null) PopulateDoodads(c, reportDiagnostics: true);
                BumpProgress(0.92f);
                SetLoadPhase(WorldLoadPhase.Collision);
                break;
            }

            case WorldLoadPhase.Collision:
            {
                // Off-thread BVH (snapshot on this thread, build on a worker). Runs
                // AFTER doodad placement so trees/fences/props are solid on arrival.
                BeginCollisionBuild();
                BumpProgress(0.94f);
                SetLoadPhase(WorldLoadPhase.Finish);
                break;
            }

            case WorldLoadPhase.Finish:
            {
                // Hold the curtain until collision under the player is real, like
                // benilla's player-settling gate - so you never spawn falling
                // through an unbuilt floor.
                if (_collisionBuildTask is not null && !PhaseTimedOut)
                {
                    BumpProgress(0.96f);
                    break;
                }

                // Map.dbc, every WDT, AreaTrigger.dbc and the teleport table.
                // Kept here (after the world build, as the old Load had it) so
                // UpdatePortals - which only runs once loading is done - has it.
                EnsureInstanceData();

                float? ground = _terrain?.SampleHeight(_config.Start.X, _config.Start.Y);
                _controller?.Teleport(_config.Start.X, _config.Start.Y, ground ?? _config.Start.Z);
                if (_controller is not null) _window.Camera.Target = _controller.Position;

                _terrain?.VerifyAgainst(_config.Start.X, _config.Start.Y, _config.Start.Z);
                CompareWmoToCollision();

                // Hand the outer ring to the background discovery streamer,
                // nearest first (what the old Load did at the end).
                if (_terrain is not null)
                {
                    var loaded = _terrain.LoadedTiles.ToHashSet();
                    foreach (var tile in TerrainRenderer.TileRing(c.col, c.row, WmoPreloadRadius)
                                 .Where(t => !loaded.Contains(t))
                                 .OrderBy(t => Math.Abs(t.col - c.col) + Math.Abs(t.row - c.row)))
                        _backgroundDiscovery.Enqueue(tile);
                }

                Console.WriteLine(
                    $"[game] world ready in {_loadClock.Elapsed.TotalSeconds:F2}s - " +
                    $"{_terrain?.TileCount ?? 0} terrain, {_wmo?.InstanceCount ?? 0} WMO, " +
                    $"{_doodads?.InstanceCount ?? 0} doodad placement(s), " +
                    $"WMO {_wmo?.PendingPreloads ?? 0} / M2 {_doodads?.PendingPreloads ?? 0} outer-ring still streaming");

                BumpProgress(1f);
                SetLoadPhase(WorldLoadPhase.Fade);
                break;
            }

            case WorldLoadPhase.Fade:
            {
                _loadCurtainAlpha -= dt / LoadFadeSeconds;
                if (_loadCurtainAlpha <= 0f)
                {
                    _worldLoading = false;
                    _loadPhase = WorldLoadPhase.Done;
                    _loadScreen?.Dispose();
                    _loadScreen = null;
                }
                break;
            }
        }
    }

    /// <summary>
    /// Pump a warm queue this frame while the curtain is up. The doodad pool
    /// finalizes many models per pass (parallel prepares), so a handful of passes
    /// keeps the workers full and adopts ready uploads; the extra passes also
    /// carry the single-job WMO pipeline. Stops early when nothing is left.
    /// </summary>
    private static void DrainWarm(Func<bool> warmOne)
    {
        for (int i = 0; i < 48; i++)
            if (!warmOne()) break;
    }

    /// <summary>Draw the loading curtain over the frame. Called at the end of Render.</summary>
    private void DrawLoadingScreen()
    {
        if (_loadScreen is null) return;
        float alpha = _loadPhase == WorldLoadPhase.Fade ? MathF.Max(0f, _loadCurtainAlpha) : 1f;
        _loadScreen.Render(_loadProgress, alpha);
    }
}
