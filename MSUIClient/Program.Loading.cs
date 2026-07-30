using System.Diagnostics;
using System.Numerics;
using Silk.NET.OpenGL;
using MSUIClient.Engine;
using MSUIClient.Formats;
using MSUIClient.World;
// Silk.NET.OpenGL also defines a Texture type; disambiguate to ours, the same
// way TerrainTextures/DoodadRenderer/WmoRenderer do.
using Texture = MSUIClient.Engine.Texture;

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
    private int? _loadScreenMapId;
    private bool _worldLoading;
    private int? _worldLoadingMapId;
    private float _loadProgress;
    private float _loadCurtainAlpha = 1f;

    /// <summary>Monotonic world clock in seconds, pushed to the renderers each
    /// frame to drive the appear fade (benilla model_fade.rs).</summary>
    private float _worldTime;

    /// <summary>False until the loading curtain first lifts. Gates the appear fade
    /// so the initial world is opaque behind the curtain and only objects streamed
    /// in later ease in - benilla arms its fades on the same signal.</summary>
    private bool _worldShown;
    private int _wmoWarmTotal = 1;
    private int _doodadWarmTotal = 1;
    private (int col, int row) _loadCentre;
    private WorldLoadPhase _loadPhase = WorldLoadPhase.Done;
    private readonly Stopwatch _loadClock = new();
    private readonly Stopwatch _loadPhaseClock = new();

    /// <summary>
    /// Decoded loading-screen backdrops keyed by BLP path, so re-entering a
    /// continent doesn't re-decode. Owned here (disposed with the game),
    /// referenced by the transient LoadingScreen through the GL handle only.
    /// benilla caches identically (loading_screen.rs art_cache).
    /// </summary>
    private readonly Dictionary<string, Texture> _loadingArtCache =
        new(StringComparer.OrdinalIgnoreCase);
    private Texture? _loadingBarBorder;
    private Texture? _loadingBarFill;

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
    /// Put the exclusive loading art up at the Enter World click, before the server's
    /// LOGIN_VERIFY_WORLD supplies the authoritative spawn and permits real loading to begin.
    /// This pre-spawn curtain deliberately does not set _worldLoading. Update pumps the socket
    /// in both curtain states; LOGIN_VERIFY_WORLD then starts the guarded world-load cycle.
    /// </summary>
    private void ArmEnterWorldCurtain(GL gl, int mapId)
    {
        _loadScreen?.Dispose();
        _loadScreen = new LoadingScreen(gl);
        _loadScreenMapId = mapId;
        if (_config.Render.LoadingScreenArt) TryLoadLoadingArt(gl, mapId);
        TryLoadLoadingBarArt(gl);
        _loadProgress = 0f;
        _loadCurtainAlpha = 1f;
    }

    /// <summary>
    /// Arm the incremental load. Called at the end of Load once every renderer
    /// exists (empty) and the controller has been created. Returns immediately:
    /// the heavy build is driven by StepWorldLoad across the following frames.
    /// </summary>
    private void BeginWorldLoad(GL gl)
    {
        // PumpNet can receive another enter-world notification while this load
        // is already active. Re-entering would dispose the curtain and reset all
        // phase state mid-drain, so the active map owns exactly one load cycle.
        if (_worldLoading && _worldLoadingMapId == _config.Start.Map) return;

        _worldLoadingMapId = _config.Start.Map;
        _loadCentre = _residentCentre
            ?? TerrainRenderer.TileAt(_config.Start.X, _config.Start.Y);
        _residentCentre = _loadCentre;

        if (_loadScreen is null || _loadScreenMapId != _config.Start.Map)
        {
            _loadScreen?.Dispose();
            _loadScreen = new LoadingScreen(gl);
            _loadScreenMapId = _config.Start.Map;
            if (_config.Render.LoadingScreenArt)
                TryLoadLoadingArt(gl, _config.Start.Map);
            TryLoadLoadingBarArt(gl);
        }
        _worldLoading = true;
        _loadProgress = 0f;
        _loadCurtainAlpha = 1f;
        _loadClock.Restart();
        BeginLoadTimeline();
        SetLoadPhase(WorldLoadPhase.Terrain);

        // Start the terrain ring streaming off-thread now (parse + mesh + upload
        // all run on the worker pool + shared-context uploader). One tile past
        // the resident radius, matching the crossing lead.
        _terrain?.QueuePreload(
            TerrainRenderer.TileRing(_loadCentre.col, _loadCentre.row, _config.Start.TileRadius + 1),
            _adts!, _loadCentre);

        // Start warming the resident ring's building models off-thread too, so the
        // placement pass below is a cache hit instead of a blocking resolve.
        _wmo?.QueuePreloadForTiles(
            TerrainRenderer.TileRing(_loadCentre.col, _loadCentre.row, _config.Start.TileRadius),
            _adts!, new Vector2(_config.Start.X, _config.Start.Y));
        _wmoWarmTotal = Math.Max(1, _wmo?.PendingPreloads ?? 1);

        Console.WriteLine("[load] streaming world behind loading screen " +
                          $"(centre tile [{_loadCentre.col},{_loadCentre.row}], " +
                          $"radius {_config.Start.TileRadius})");
    }

    private void SetLoadPhase(WorldLoadPhase phase)
    {
        _loadPhase = phase;
        _loadPhaseClock.Restart();
        StartLoadTimelinePhase(phase);
    }

    private void AdvanceLoadPhase(WorldLoadPhase phase, bool watchdog = false)
    {
        if (watchdog)
            Console.WriteLine($"[load] WATCHDOG {_loadPhase}");
        ExitLoadTimelinePhase(watchdog ? "watchdog" : "condition-met");
        SetLoadPhase(phase);
    }

    private bool PhaseTimedOut => _loadPhaseClock.Elapsed.TotalSeconds > LoadPhaseWatchdogSeconds;

    private void BumpProgress(float p) => _loadProgress = MathF.Max(_loadProgress, p);

    /// <summary>
    /// Advance the world clock and push it, plus the "world shown" gate, to the
    /// renderers that fade streamed-in geometry. Called at the top of Update so it
    /// runs during the loading build (placements stamped opaque) and afterwards
    /// (new placements stamped NOW and eased in). AppearFade / AppearFadeSeconds
    /// themselves come from the settings apply path, not here.
    /// </summary>
    private void UpdateAppearFadeClock(float dt)
    {
        _worldTime += dt;
        if (_doodads is not null) { _doodads.NowSeconds = _worldTime; _doodads.WorldShown = _worldShown; }
        if (_wmo is not null) { _wmo.NowSeconds = _worldTime; _wmo.WorldShown = _worldShown; }
    }

    /// <summary>
    /// Resolve and set the map's real WoW loading-screen art on the curtain, via
    /// the verified Map.dbc(field 38) -> LoadingScreens.dbc(field 2) -> BLP chain
    /// (benilla loading_screen.rs / benilla-formats). Best-effort: any miss (no
    /// MPQ, no FK for this map, missing BLP, decode failure) simply leaves the
    /// plain dark curtain, so this can never block or fail a load.
    ///
    /// Map.dbc is read directly here rather than via EnsureInstanceData, which
    /// runs in the Finish phase (too late) and also loads every WDT. The two
    /// small DBC reads are cheap and the MPQ caches them.
    /// </summary>
    private void TryLoadLoadingArt(GL gl, int mapId)
    {
        if (_mpq is null || _loadScreen is null) return;
        try
        {
            var mapBytes = _mpq.ReadFile(MapTable.MpqPath);
            if (mapBytes is null) return;
            int screenId = MapTable.Parse(mapBytes)?.Get(mapId)?.LoadingScreenId ?? 0;
            if (screenId == 0) return;   // dev/test map, or the field is absent

            var screenBytes = _mpq.ReadFile(LoadingScreenTable.MpqPath);
            if (screenBytes is null) return;
            string? path = LoadingScreenTable.Parse(screenBytes)?.PathFor(screenId);
            if (string.IsNullOrWhiteSpace(path)) return;

            if (!_loadingArtCache.TryGetValue(path, out var tex))
            {
                var blp = _mpq.ReadFile(path);
                if (blp is null) return;
                var bgra = BlpDecoder.GetPixels(blp, 0, out int w, out int h);
                tex = Texture.From2D(gl, bgra, w, h, mipmaps: false, repeat: false);
                _loadingArtCache[path] = tex;
                Console.WriteLine($"[load] loading-screen art {path} ({w}x{h}) for map {mapId}");
            }
            _loadScreen.SetBackground(tex.Handle);
        }
        catch (Exception e)
        {
            Console.WriteLine($"[load] loading-screen art unavailable (map {mapId}): {e.Message}");
        }
    }

    private void TryLoadLoadingBarArt(GL gl)
    {
        if (_mpq is null || _loadScreen is null) return;
        try
        {
            _loadingBarBorder ??= LoadLoadingTexture(gl,
                @"Interface\Glues\LoadingBar\Loading-BarBorder.blp");
            _loadingBarFill ??= LoadLoadingTexture(gl,
                @"Interface\Glues\LoadingBar\Loading-BarFill.blp");
            _loadScreen.SetBarArt(_loadingBarBorder?.Handle ?? 0, _loadingBarFill?.Handle ?? 0);
        }
        catch (Exception e)
        {
            Console.WriteLine($"[load] 1.12 loading-bar art unavailable: {e.Message}");
        }
    }

    private Texture? LoadLoadingTexture(GL gl, string path)
    {
        byte[]? blp = _mpq?.ReadFile(path);
        if (blp is null) return null;
        byte[] bgra = BlpDecoder.GetPixels(blp, 0, out int width, out int height);
        return Texture.From2D(gl, bgra, width, height, mipmaps: false, repeat: false);
    }

    /// <summary>Dispose the cached loading-screen textures. Called from GameLoop teardown
    /// while the GL context is still current.</summary>
    private void DisposeLoadingArt()
    {
        foreach (var t in _loadingArtCache.Values) t.Dispose();
        _loadingArtCache.Clear();
        _loadingBarBorder?.Dispose();
        _loadingBarFill?.Dispose();
        _loadingBarBorder = null;
        _loadingBarFill = null;
    }

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
                    if (_terrain is not null && _terrain.PreloadReady(t)) ready++;
                BumpProgress(0.04f + 0.18f * ready / Math.Max(1, ring.Count));

                // The player tile and its eight neighbours are the reveal-critical
                // terrain. Nearest-first queueing guarantees these are indices 0..8;
                // the outer ring continues preparing while later phases warm assets.
                var clearRing = TerrainRenderer.TileRing(c.col, c.row, radius: 1);
                bool timedOut = PhaseTimedOut;
                if (_terrain is null || _terrain.PreloadReady(clearRing) || timedOut)
                {
                    // All resident tiles prepared off-thread -> adopting them here
                    // just creates the small VAOs, no MPQ/parse on this thread.
                    _terrain?.SetResidency(c.col, c.row, radius, _adts!);
                    AdvanceLoadPhase(WorldLoadPhase.WarmBuildings, timedOut);
                }
                break;
            }

            case WorldLoadPhase.WarmBuildings:
            {
                DrainWarm(() => _wmo?.WarmNextPreload() ?? false);
                int pending = _wmo?.PendingPreloads ?? 0;
                // Deferred ADT tiles can unfold into several WMO roots. Keep the
                // denominator synchronized with the largest real queue observed.
                _wmoWarmTotal = Math.Max(_wmoWarmTotal, Math.Max(1, pending));
                BumpProgress(0.22f + 0.20f * (1f - pending / (float)_wmoWarmTotal));
                bool timedOut = PhaseTimedOut;
                if (pending == 0 || timedOut)
                    AdvanceLoadPhase(WorldLoadPhase.PlaceBuildings, timedOut);
                break;
            }

            case WorldLoadPhase.PlaceBuildings:
            {
                // Models are warm, so placement is cache-hit fast. Then queue the
                // outer ring for the first crossings (it streams after the curtain).
                _wmo?.ResetPlacements();
                if (_wmo is not null && _terrain is not null)
                    _wmo.LoadForTiles(_terrain.LoadedTiles, _adts!, warmedOnly: true);
                _wmo?.QueuePreloadForTiles(
                    TerrainRenderer.TileRing(c.col, c.row, WmoPreloadRadius), _adts!,
                    new Vector2(_config.Start.X, _config.Start.Y));
                BumpProgress(0.48f);
                AdvanceLoadPhase(WorldLoadPhase.Liquid);
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
                                .OrderBy(d => Vector2.DistanceSquared(
                                    new Vector2(d.Transform.M41, d.Transform.M42), centreV))
                                .Select(d => (d.ModelPath, Vector2.DistanceSquared(
                                    new Vector2(d.Transform.M41, d.Transform.M42), centreV)))
                                .DistinctBy(d => d.ModelPath, StringComparer.OrdinalIgnoreCase),
                            "interior-doodad");
                    _doodadWarmTotal = Math.Max(1, _doodads.PendingPreloads);
                }

                _adts?.Retain(TerrainRenderer.TileRing(c.col, c.row, WmoPreloadRadius));
                BumpProgress(0.50f);
                AdvanceLoadPhase(WorldLoadPhase.WarmDoodads);
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
                bool timedOut = PhaseTimedOut;
                if (pending == 0 || timedOut)
                    AdvanceLoadPhase(WorldLoadPhase.PlaceDoodads, timedOut);
                break;
            }

            case WorldLoadPhase.PlaceDoodads:
            {
                // Place every warmed model's instances (outdoor + WMO interior) in
                // one shot, behind the curtain, so the reveal is fully populated.
                if (_doodads is not null) PopulateDoodads(c, reportDiagnostics: true);
                BumpProgress(0.92f);
                AdvanceLoadPhase(WorldLoadPhase.Collision);
                break;
            }

            case WorldLoadPhase.Collision:
            {
                // Off-thread BVH (snapshot on this thread, build on a worker). Runs
                // AFTER doodad placement so trees/fences/props are solid on arrival.
                BeginCollisionBuild();
                BumpProgress(0.94f);
                AdvanceLoadPhase(WorldLoadPhase.Finish);
                break;
            }

            case WorldLoadPhase.Finish:
            {
                // Hold the curtain until collision under the player is real, like
                // benilla's player-settling gate - so you never spawn falling
                // through an unbuilt floor.
                bool timedOut = PhaseTimedOut;
                if (_collisionBuildTask is not null && !timedOut)
                {
                    BumpProgress(0.96f);
                    break;
                }

                // Map.dbc, every WDT, AreaTrigger.dbc and the teleport table.
                // Kept here (after the world build, as the old Load had it) so
                // UpdatePortals - which only runs once loading is done - has it.
                EnsureInstanceData();

                float? ground = _terrain?.SampleHeight(_config.Start.X, _config.Start.Y);
                // Networked spawns are server-authoritative: the server placed us on the
                // Stormwind WMO floor (Z well above the terrain skirt under the city).
                // Re-sampling terrain here would drop us below the city and we would have
                // to fly back up. Trust the server Z when networked; the controller's own
                // ground resolution settles onto the WMO floor (collision is built by now).
                _controller?.Teleport(_config.Start.X, _config.Start.Y,
                    _config.Server.Enabled && _worldLoadStarted ? _config.Start.Z : (ground ?? _config.Start.Z));
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
                // The world is now on screen: arm the appear fade so anything
                // streamed in from here on eases in instead of popping. The near
                // world placed behind the curtain stays opaque (stamped while
                // _worldShown was false), and the curtain's own fade covers it.
                _worldShown = true;
                AdvanceLoadPhase(WorldLoadPhase.Fade, timedOut);
                break;
            }

            case WorldLoadPhase.Fade:
            {
                _loadCurtainAlpha -= dt / LoadFadeSeconds;
                if (_loadCurtainAlpha <= 0f)
                {
                    ExitLoadTimelinePhase("condition-met");
                    _worldLoading = false;
                    _worldLoadingMapId = null;
                    _loadPhase = WorldLoadPhase.Done;
                    _loadScreen?.Dispose();
                    _loadScreen = null;
                    _loadScreenMapId = null;
                    NoteLoadCurtainClear();
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
        long started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started).TotalMilliseconds < LoadWarmBudgetMs)
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
