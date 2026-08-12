using System.Diagnostics;
using System.Numerics;
using Silk.NET.OpenGL;
using MSUIClient.Engine;
using MSUIClient.Formats;
using MSUIClient.Net;
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
    private int _loadFadeWarmStage;
    private bool _preWorldHudPrimed;

    /// <summary>Monotonic world clock in seconds, pushed to the renderers each
    /// frame to drive the appear fade (benilla model_fade.rs).</summary>
    private float _worldTime;

    /// <summary>False until the loading curtain first lifts. Gates the appear fade
    /// so the initial world is opaque behind the curtain and only objects streamed
    /// in later ease in - benilla arms its fades on the same signal.</summary>
    private bool _worldShown;
    private int _wmoWarmTotal = 1;
    private int _doodadWarmTotal = 1;
    private int _placeDoodadStage;
    private (int col, int row)[] _loadDoodadTiles = [];
    private int _loadDoodadTileIndex;
    private IEnumerator<(string ModelPath, Matrix4x4 Transform, Vector4 Light,
        int WmoInstanceId, int[] OwnerGroups)>? _loadInteriorDoodads;
    private double _loadOutdoorPlacementMs, _loadInteriorPlacementMs;
    private int _loadInteriorRequested, _loadInteriorPlaced;
    private int _liquidStage, _liquidTileIndex, _interiorPreloadIndex;
    private int _finishStage;
    private bool _loadSpawnFailureLogged;
    private bool _loadSpawnRecoveryAttempted;
    private bool _loadSpawnRecoveryAwaitingWorldport;
    private long _loadSpawnRecoveryStamp;
    private (int col, int row)[] _liquidTiles = [];
    private IEnumerator<(string ModelPath, Matrix4x4 Transform, Vector4 Light,
        int WmoInstanceId, int[] OwnerGroups)>? _interiorPreloadEnumerator;
    private readonly List<(string Path, float DistanceSq)> _interiorPreloadCandidates =
        new(4096);
    private (int col, int row) _loadCentre;
    /// <summary>
    /// The WDT-level placement for maps made from one global WMO (BRD, the
    /// Stockade, Molten Core, etc.). Null means the active map uses ADT tiles.
    /// This remains set after loading so runtime tile residency cannot tear the
    /// instance back down when the player crosses an imaginary ADT boundary.
    /// </summary>
    private AdtTerrainReader.WmoInstance? _globalWmoPlacement;
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
        _loadFadeWarmStage = 0;
        _preWorldHudPrimed = false;
        _placeDoodadStage = 0;
    }

    /// <summary>
    /// Drop a curtain raised for a server transfer that was subsequently
    /// refused. Never interrupts a load which already owns the curtain.
    /// </summary>
    private void CancelPendingWorldCurtain()
    {
        if (_worldLoading) return;
        _loadScreen?.Dispose();
        _loadScreen = null;
        _loadScreenMapId = null;
        _loadProgress = 0f;
        _loadCurtainAlpha = 0f;
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

        WdtFile? activeWdt = WdtFile.Read(_config.ClientDataPath, _config.Start.MapName);
        _globalWmoPlacement = activeWdt is { UsesGlobalWmo: true }
            ? activeWdt.GlobalWmo
              ?? throw new InvalidDataException(
                  $"{_config.Start.MapName}.wdt declares a global WMO but has no MODF placement")
            : null;
        if (_controller is not null)
            _controller.TerrainAbsentByDesign = _globalWmoPlacement is not null;

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
        _loadFadeWarmStage = 0;
        _placeDoodadStage = 0;
        _loadDoodadTiles = [];
        _loadDoodadTileIndex = 0;
        _loadInteriorDoodads?.Dispose();
        _loadInteriorDoodads = null;
        _loadOutdoorPlacementMs = _loadInteriorPlacementMs = 0;
        _loadInteriorRequested = _loadInteriorPlaced = 0;
        _liquidStage = _liquidTileIndex = _interiorPreloadIndex = 0;
        _finishStage = 0;
        _loadSpawnFailureLogged = false;
        _loadSpawnRecoveryAttempted = false;
        _loadSpawnRecoveryAwaitingWorldport = false;
        _loadSpawnRecoveryStamp = 0;
        _liquidTiles = [];
        _interiorPreloadEnumerator?.Dispose();
        _interiorPreloadEnumerator = null;
        _interiorPreloadCandidates.Clear();

        _loadClock.Restart();
        BeginLoadTimeline();
        SetLoadPhase(WorldLoadPhase.Terrain);

        // Start the terrain ring streaming off-thread now (parse + mesh + upload
        // all run on the worker pool + shared-context uploader). One tile past
        // the resident radius, matching the crossing lead.
        if (_globalWmoPlacement is { } globalWmo)
        {
            // Global-WMO maps have no terrain or ADT MODF lists. Their only
            // building is named directly by the WDT and must be warm before the
            // collision phase can safely place the player.
            _terrain?.UnloadAll();
            _liquid?.UnloadAll();
            _wmo?.QueuePreloadGlobal(globalWmo);
        }
        else
        {
            _terrain?.QueuePreload(
                TerrainRenderer.TileRing(
                    _loadCentre.col, _loadCentre.row, _config.Start.TileRadius + 1),
                _adts!, _loadCentre);

            // Start warming the resident ring's building models off-thread too, so the
            // placement pass below is a cache hit instead of a blocking resolve.
            _wmo?.QueuePreloadForTiles(
                TerrainRenderer.TileRing(_loadCentre.col, _loadCentre.row,
                    _config.Start.TileRadius),
                _adts!, new Vector2(_config.Start.X, _config.Start.Y));
        }
        _wmoWarmTotal = Math.Max(1, _wmo?.PendingPreloads ?? 1);

        Console.WriteLine(_globalWmoPlacement is null
            ? "[load] streaming world behind loading screen " +
              $"(centre tile [{_loadCentre.col},{_loadCentre.row}], " +
              $"radius {_config.Start.TileRadius})"
            : "[load] streaming global WMO behind loading screen " +
              $"({_globalWmoPlacement.ModelPath})");
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
                if (_globalWmoPlacement is not null)
                {
                    // A global-WMO map deliberately has no terrain tiles. Do
                    // not wait for a 3x3 ring of ADTs that cannot exist.
                    BumpProgress(0.22f);
                    AdvanceLoadPhase(WorldLoadPhase.WarmBuildings);
                    break;
                }

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
                if (_globalWmoPlacement is { } globalWmo)
                {
                    bool placed = _wmo?.LoadGlobal(globalWmo, warmedOnly: true) == true ||
                                  _wmo?.LoadGlobal(globalWmo, warmedOnly: false) == true;
                    if (!placed)
                        Console.WriteLine($"[wmo-global] FAILED to place {globalWmo.ModelPath}");
                }
                else
                {
                    if (_wmo is not null && _terrain is not null)
                        _wmo.LoadForTiles(_terrain.LoadedTiles, _adts!, warmedOnly: true);
                    _wmo?.QueuePreloadForTiles(
                        TerrainRenderer.TileRing(c.col, c.row, WmoPreloadRadius), _adts!,
                        new Vector2(_config.Start.X, _config.Start.Y));
                }
                BumpProgress(0.48f);
                AdvanceLoadPhase(WorldLoadPhase.Liquid);
                break;
            }

            case WorldLoadPhase.Liquid:
            {
                if (_liquidStage == 0)
                {
                    if (_liquid is not null && _terrain is not null)
                    _liquid.LoadForTiles(_terrain.LoadedTiles, _adts!);
                    _liquidTiles = _terrain?.LoadedTiles.ToArray() ?? [];
                    _liquidStage = 1;
                    break;
                }

                // Queue the near doodads (outdoor MDDF + resident WMO interiors)
                // incrementally so enumeration/sorting cannot form one long
                // loader frame under worker contention.
                var centreV = new Vector2(_config.Start.X, _config.Start.Y);
                float demand = DoodadDemandRadius;
                if (_liquidStage == 1 && _liquidTileIndex < _liquidTiles.Length)
                {
                    _doodads?.QueuePreloadForTiles(
                        [_liquidTiles[_liquidTileIndex++]], _adts!, centreV, demand);
                    break;
                }
                if (_liquidStage == 1)
                {
                    _interiorPreloadEnumerator = _wmo?.EnumerateDoodads(
                        centreV, demand).GetEnumerator();
                    _liquidStage = 2;
                    break;
                }
                if (_liquidStage == 2)
                {
                    long started = Stopwatch.GetTimestamp();
                    bool complete = _interiorPreloadEnumerator is null;
                    while (!complete &&
                           Stopwatch.GetElapsedTime(started).TotalMilliseconds < 2.0)
                    {
                        if (!_interiorPreloadEnumerator!.MoveNext())
                        {
                            complete = true;
                            break;
                        }
                        var d = _interiorPreloadEnumerator.Current;
                        var delta = new Vector2(d.Transform.M41, d.Transform.M42) - centreV;
                        _interiorPreloadCandidates.Add((d.ModelPath, delta.LengthSquared()));
                    }
                    if (!complete) break;
                    _interiorPreloadEnumerator?.Dispose();
                    _interiorPreloadEnumerator = null;
                    _interiorPreloadCandidates.Sort(
                        static (a, b) => a.DistanceSq.CompareTo(b.DistanceSq));
                    _liquidStage = 3;
                    break;
                }
                if (_liquidStage == 3)
                {
                    long started = Stopwatch.GetTimestamp();
                    while (_interiorPreloadIndex < _interiorPreloadCandidates.Count &&
                           Stopwatch.GetElapsedTime(started).TotalMilliseconds < 2.0)
                    {
                        _doodads?.QueuePreloadModels(
                            [_interiorPreloadCandidates[_interiorPreloadIndex++]],
                            "interior-doodad");
                    }
                    if (_interiorPreloadIndex < _interiorPreloadCandidates.Count) break;
                    _liquidStage = 4;
                }

                _doodadWarmTotal = Math.Max(1, _doodads?.PendingPreloads ?? 0);

                if (_globalWmoPlacement is null)
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
                if (_placeDoodadStage == 0)
                {
                    _loadDoodadTiles = _terrain?.LoadedTiles.ToArray() ?? [];
                    _loadDoodadTileIndex = 0;
                    _placeDoodadStage = 1;
                    BumpProgress(0.91f);
                    break;
                }

                // A single nine-ADT walk varied from 34.88 to 73.34 ms under
                // live load. One ADT per curtain frame keeps the same placement
                // keys and makes the work bounded independently of contention.
                if (_placeDoodadStage == 1 &&
                    _loadDoodadTileIndex < _loadDoodadTiles.Length)
                {
                    long started = Stopwatch.GetTimestamp();
                    if (_doodads is not null)
                        _doodads.LoadForTiles(
                            [_loadDoodadTiles[_loadDoodadTileIndex]], _adts!,
                            TerrainRenderer.TileCenter(c.col, c.row), ObjectResidencyRadius,
                            reportDiagnostics: false);
                    _loadOutdoorPlacementMs +=
                        Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    _loadDoodadTileIndex++;
                    break;
                }

                if (_placeDoodadStage == 1)
                {
                    Vector2 centre = _globalWmoPlacement is null
                        ? TerrainRenderer.TileCenter(c.col, c.row)
                        : new Vector2(_config.Start.X, _config.Start.Y);
                    _loadInteriorDoodads = _wmo?.EnumerateDoodads(
                        centre, ObjectResidencyRadius).GetEnumerator();
                    _placeDoodadStage = 2;
                    break;
                }

                // WMO furniture is an iterator so it can be adopted under the
                // same 2 ms main-thread budget as S6 finalization.
                long interiorStarted = Stopwatch.GetTimestamp();
                bool interiorComplete = _loadInteriorDoodads is null;
                while (!interiorComplete &&
                       Stopwatch.GetElapsedTime(interiorStarted).TotalMilliseconds < 2.0)
                {
                    if (!_loadInteriorDoodads!.MoveNext())
                    {
                        interiorComplete = true;
                        break;
                    }
                    var d = _loadInteriorDoodads.Current;
                    _loadInteriorRequested++;
                    if (_doodads?.AddPlaced(d.ModelPath, d.Transform, d.Light,
                            d.WmoInstanceId, d.OwnerGroups) == true)
                        _loadInteriorPlaced++;
                }
                _loadInteriorPlacementMs +=
                    Stopwatch.GetElapsedTime(interiorStarted).TotalMilliseconds;
                if (!interiorComplete) break;

                _loadInteriorDoodads?.Dispose();
                _loadInteriorDoodads = null;
                _outdoorPlacementMilliseconds = _loadOutdoorPlacementMs;
                _interiorPlacementMilliseconds = _loadInteriorPlacementMs;
                _placementsRequested = _loadInteriorRequested;
                if (_loadInteriorRequested > 0)
                    _doodads?.ReportInterior(_loadInteriorRequested, _loadInteriorPlaced,
                        _loadInteriorPlacementMs / 1000.0);
                Console.WriteLine($"[load] doodad placement outdoor " +
                                  $"{_loadOutdoorPlacementMs:F2} ms, " +
                                  $"interior {_loadInteriorPlacementMs:F2} ms");
                Console.WriteLine($"[stream] object residency [{c.col},{c.row}] " +
                                  $"radius {ObjectResidencyRadius:F0} yd");
                _doodads?.DrainNewlyReadyModelPaths(_newDoodadModels);
                _newDoodadModels.Clear();
                if (_controller is not null)
                    _lastDemandCentre = new Vector2(
                        _controller.Position.X, _controller.Position.Y);
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

                // The reused booth/bootstrap renderer may still be receiving its
                // server appearance through the asset/upload workers. Never
                // reveal a stale, missing or half-equipped local player.
                if (_character is { AppearanceReady: false })
                {
                    BumpProgress(0.98f);
                    break;
                }

                if (_finishStage == 0)
                {
                    // Map.dbc, every WDT, AreaTrigger.dbc and the teleport table.
                    EnsureInstanceData();
                    _finishStage = 1;
                    break;
                }

                if (_finishStage == 1)
                {
                    if (!LoadSpawnHasSupport(out string supportFailure))
                    {
                        // A watchdog may relax streaming waits, but it may never
                        // reveal an impossible spawn. In particular, a global-WMO
                        // map paired with coordinates outside that WMO has no floor
                        // and immediately drops the character through the world.
                        if (TryRecoverInvalidLoadSpawn())
                        {
                            BumpProgress(0.98f);
                            break;
                        }

                        if (!_loadSpawnFailureLogged)
                        {
                            _loadSpawnFailureLogged = true;
                            _travelStatus = supportFailure;
                            Console.WriteLine($"[load] BLOCKED - {supportFailure}");
                        }
                        BumpProgress(0.98f);
                        break;
                    }

                    float? ground = _terrain?.SampleHeight(_config.Start.X, _config.Start.Y);
                    _controller?.Teleport(_config.Start.X, _config.Start.Y,
                        _config.Server.Enabled && _worldLoadStarted
                            ? _config.Start.Z
                            : (ground ?? _config.Start.Z));
                    if (_controller is not null) _window.Camera.Target = _controller.Position;

                    // Server worldports commonly land inside the paired return
                    // trigger (BRD is one). Seed the arrival volume before the
                    // curtain clears so UpdatePortals cannot immediately report
                    // that exit and bounce a valid .tele/entrance straight back.
                    _portalLatch = _controller is null
                        ? 0
                        : _areaTriggers?.Containing(
                            _config.Start.Map, _controller.Position)?.Id ?? 0;
                    if (_portalLatch != 0)
                        Console.WriteLine($"[portal] arrival latched trigger {_portalLatch}");

                    _terrain?.VerifyAgainst(_config.Start.X, _config.Start.Y, _config.Start.Z);
                    CompareWmoToCollision();
                    _finishStage = 2;
                    break;
                }

                // Hand the outer ring to the background discovery streamer,
                // nearest first (what the old Load did at the end).
                if (_terrain is not null && _globalWmoPlacement is null)
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
                // Prime one render family per alpha-1 curtain frame. The first
                // complete world pass is then cache-hot and cannot stack WMO,
                // doodad, player, and creature first touches into one reveal
                // hitch. Stages: terrain/sky, WMO, doodads, player, creatures,
                // translucent/debug, then one complete verification pass.
                if (_loadFadeWarmStage < 6)
                {
                    _loadFadeWarmStage++;
                    break;
                }
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
    /// The final reveal invariant. Terrain maps need either an authored height
    /// sample or nearby building collision. Global-WMO maps have no terrain, so
    /// their authoritative spawn must lie inside the installed collision bounds
    /// and have a surface below it.
    /// </summary>
    private bool LoadSpawnHasSupport(out string failure)
    {
        float x = _config.Start.X;
        float y = _config.Start.Y;
        float z = _config.Start.Z;

        if (_globalWmoPlacement is null)
        {
            if (_terrain?.SampleHeight(x, y) is not null)
            {
                failure = "";
                return true;
            }

            if (_collision?.Raycast(new Vector3(x, y, z + 3f), -Vector3.UnitZ, 80f) is not null)
            {
                failure = "";
                return true;
            }

            failure = $"map {_config.Start.Map} has no loaded terrain or collision support at " +
                      $"({x:F1}, {y:F1}, {z:F1}); keeping the loading screen up";
            return false;
        }

        if (_collision is null || _collision.IsEmpty)
        {
            failure = $"global-WMO map {_config.Start.Map} has no installed collision; " +
                      "keeping the loading screen up";
            return false;
        }

        const float BoundsSlop = 3f;
        if (x < _collision.BoundsMin.X - BoundsSlop || x > _collision.BoundsMax.X + BoundsSlop ||
            y < _collision.BoundsMin.Y - BoundsSlop || y > _collision.BoundsMax.Y + BoundsSlop)
        {
            failure = $"server supplied position ({x:F1}, {y:F1}, {z:F1}) outside map " +
                      $"{_config.Start.Map}'s collision bounds " +
                      $"X {_collision.BoundsMin.X:F0}..{_collision.BoundsMax.X:F0}, " +
                      $"Y {_collision.BoundsMin.Y:F0}..{_collision.BoundsMax.Y:F0}; " +
                      "server-side position/map must be repaired";
            return false;
        }

        float probeDepth = MathF.Max(20f, z - _collision.BoundsMin.Z + 5f);
        if (_collision.Raycast(new Vector3(x, y, z + 3f), -Vector3.UnitZ, probeDepth) is null)
        {
            failure = $"global-WMO map {_config.Start.Map} has no floor below server position " +
                      $"({x:F1}, {y:F1}, {z:F1}); keeping the loading screen up";
            return false;
        }

        failure = "";
        return true;
    }

    /// <summary>
    /// Repair a map/position split instead of leaving the character behind an
    /// eternal curtain. First prefer the authored portal destination which is
    /// already within a few yards of the supplied position and ask VMaNGOS to
    /// correct the map. If administrator worldport is unavailable, force a
    /// known-supported point on the declared map and immediately report that
    /// same-map pose so both sides converge again.
    /// </summary>
    private bool TryRecoverInvalidLoadSpawn()
    {
        if (_loadSpawnRecoveryAwaitingWorldport)
        {
            if (Stopwatch.GetElapsedTime(_loadSpawnRecoveryStamp).TotalSeconds < 3.0)
                return true;

            Console.WriteLine("[load-recovery] server worldport did not arrive in 3s; " +
                              "falling back to a supported point on the declared map");
            _loadSpawnRecoveryAwaitingWorldport = false;
            return ForceSupportedPointOnDeclaredMap();
        }

        if (_loadSpawnRecoveryAttempted) return false;
        _loadSpawnRecoveryAttempted = true;

        Vector3 supplied = new(_config.Start.X, _config.Start.Y, _config.Start.Z);
        AreaTriggerTeleport? intended = null;
        float intendedSq = 40f * 40f;
        if (_teleports is not null)
        {
            foreach (AreaTriggerTeleport destination in _teleports.ById.Values)
            {
                if (destination.TargetMap == _config.Start.Map) continue;
                float distanceSq = Vector3.DistanceSquared(supplied, destination.TargetPosition);
                if (distanceSq >= intendedSq) continue;
                intendedSq = distanceSq;
                intended = destination;
            }
        }

        // CMSG_WORLD_TELEPORT is administrator-only on VMaNGOS. We know GM
        // state from the server's own notification and never send this packet
        // for an ordinary account.
        if (intended is not null && _serverGmMode == true &&
            _net?.WorldTeleport((uint)intended.TargetMap,
                intended.TargetPosition, intended.TargetOrientation) == true)
        {
            _loadSpawnRecoveryAwaitingWorldport = true;
            _loadSpawnRecoveryStamp = Stopwatch.GetTimestamp();
            float distance = MathF.Sqrt(intendedSq);
            Console.WriteLine($"[load-recovery] impossible map {_config.Start.Map} position " +
                              $"matches '{intended.Name}' map {intended.TargetMap} destination " +
                              $"within {distance:F1} yd; requested authoritative server worldport");
            _travelStatus = $"repairing server position via {intended.Name}";
            return true;
        }

        return ForceSupportedPointOnDeclaredMap();
    }

    private bool ForceSupportedPointOnDeclaredMap()
    {
        Vector3 supplied = new(_config.Start.X, _config.Start.Y, _config.Start.Z);
        Vector3 safe = default;
        float facing = _config.Start.Orientation;
        string source = "";
        bool found = false;

        // An authored destination INTO the declared map is preferable to an
        // arbitrary mesh edge. Validate it against the collision we just built.
        if (_teleports is not null && _collision is not null)
        {
            float bestSq = float.MaxValue;
            foreach (AreaTriggerTeleport destination in _teleports.ById.Values)
            {
                if (destination.TargetMap != _config.Start.Map) continue;
                Vector3 p = destination.TargetPosition;
                if (p.X < _collision.BoundsMin.X || p.X > _collision.BoundsMax.X ||
                    p.Y < _collision.BoundsMin.Y || p.Y > _collision.BoundsMax.Y) continue;
                if (_collision.Raycast(p + Vector3.UnitZ * 3f,
                        -Vector3.UnitZ, 30f) is null) continue;
                float distanceSq = Vector3.DistanceSquared(supplied, p);
                if (distanceSq >= bestSq) continue;
                bestSq = distanceSq;
                safe = p;
                facing = destination.TargetOrientation;
                source = destination.Name;
                found = true;
            }
        }

        if (!found && _collision?.TryFindNearestWalkablePoint(supplied, out Vector3 floor) == true)
        {
            // Server/player coordinates are around the body origin rather than
            // the literal floor plane; match BRD's measured ~1.5 yd clearance.
            safe = floor + Vector3.UnitZ * 1.5f;
            source = "nearest collision floor";
            found = true;
        }

        if (!found) return false;

        _config.Start.X = safe.X;
        _config.Start.Y = safe.Y;
        _config.Start.Z = safe.Z;
        _config.Start.Orientation = facing;
        _controller?.Teleport(safe.X, safe.Y, safe.Z);
        if (_controller is not null)
        {
            _controller.Yaw = facing;
            _window.Camera.Target = _controller.Position;
        }
        _movementSender.Reset(facing);
        _net?.SendMovement(Op.MSG_MOVE_HEARTBEAT,
            MovementInfo.Create(safe, facing, MovementFlags.None));
        _portalLatch = _areaTriggers?.Containing(_config.Start.Map, safe)?.Id ?? 0;
        _loadSpawnFailureLogged = false;
        Console.WriteLine($"[load-recovery] forced map {_config.Start.Map} to supported {source} " +
                          $"at ({safe.X:F1}, {safe.Y:F1}, {safe.Z:F1}) and reported the pose to the server");
        _travelStatus = $"recovered to {source}";
        return true;
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
