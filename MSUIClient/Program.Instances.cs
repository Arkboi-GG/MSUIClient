using System.Diagnostics;
using System.Numerics;
using ImGuiNET;
using MSUIClient.Formats;

namespace MSUIClient;

// ============================================================================
// The instances instrument (PLAN_13_INSTANCES.md section 6, stage 1).
//
// Same rule as Program.DevTools.cs, Program.Hitch.cs, Program.LightProbe.cs and
// Program.Portals.cs: developer TOOLING. It reads Map.dbc and every map's WDT
// and prints what it found. Nothing here loads a map, moves the player or
// touches a renderer — stage 1 is deliberately read-only so it cannot regress
// the working outdoor client (PLAN_13 H5).
//
// WHAT THIS PANEL IS FOR
//   PLAN_13 section 1 makes a set of claims about the archives that were read
//   with tools/mpqpeek — a Python port that the handbook says loses to the C#
//   whenever they disagree. This panel is the C#. If it prints 44 maps and the
//   tile ranges in section 1's table, the readers agree with the tool and stage
//   2 can start. If it does not, the WDT parse is wrong and section 1 is the
//   reference.
// ============================================================================
public sealed partial class GameLoop
{
    private MapTable? _maps;
    private Dictionary<int, WdtFile?>? _mapWdts;
    private bool _instancesLoadAttempted;
    private double _instancesLoadMs;
    private string _instanceFilter = "";
    private bool _instancesInstanceOnly;

    /// <summary>
    /// Read Map.dbc and every map's WDT, once. 44 WDTs is a few megabytes of
    /// MAIN tables and takes a moment, so it happens on first open of the panel
    /// rather than at startup — nothing in the client needs it yet.
    /// </summary>
    private void EnsureInstanceData()
    {
        if (_instancesLoadAttempted) return;
        _instancesLoadAttempted = true;

        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var bytes = AdtTerrainReader.ReadFileFromMpqs(_config.ClientDataPath, MapTable.MpqPath);
            if (bytes is null)
            {
                Console.WriteLine($"[instances] {MapTable.MpqPath} not found in the archives");
                return;
            }

            _maps = MapTable.Parse(bytes);
            if (_maps is null) return;

            _mapWdts = new Dictionary<int, WdtFile?>(_maps.Count);
            int withWdt = 0, globalWmo = 0, terrain = 0;
            foreach (var m in _maps.All)
            {
                var wdt = WdtFile.Read(_config.ClientDataPath, m.Directory);
                _mapWdts[m.Id] = wdt;
                if (wdt is null) continue;
                withWdt++;
                if (wdt.UsesGlobalWmo) globalWmo++; else terrain++;
            }

            Console.WriteLine($"[instances] {_maps.Count} map(s), {withWdt} with a WDT " +
                              $"({globalWmo} global-WMO, {terrain} terrain)");
        }
        finally
        {
            // In the finally so the readout is honest on the failure paths too -
            // "read in 0 ms" next to "Map.dbc did not load" reads like a second
            // bug when it is only an unstopped stopwatch.
            sw.Stop();
            _instancesLoadMs = sw.Elapsed.TotalMilliseconds;
        }
    }

    private void DrawInstancesPanel()
    {
        if (!ImGui.CollapsingHeader("Instances (PLAN_13)")) return;

        EnsureInstanceData();

        if (_maps is null || _mapWdts is null)
        {
            ImGui.TextDisabled("Map.dbc did not load - see the console");
            return;
        }

        int globalWmo = 0, terrain = 0, noWdt = 0;
        foreach (var m in _maps.All)
        {
            var w = _mapWdts.GetValueOrDefault(m.Id);
            if (w is null) noWdt++;
            else if (w.UsesGlobalWmo) globalWmo++;
            else terrain++;
        }

        // PLAN_13 section 7 step 1: this line is the test. 44 maps, and the
        // two kinds split the way section 1's first table says.
        ImGui.Text($"{_maps.Count} map(s)   {globalWmo} global-WMO   {terrain} terrain" +
                   (noWdt > 0 ? $"   {noWdt} with NO WDT" : ""));
        ImGui.TextDisabled($"read in {_instancesLoadMs:F0} ms   current map: {_config.Start.MapName}");

        ImGui.SetNextItemWidth(160f);
        ImGui.InputText("filter##inst", ref _instanceFilter, 64u);
        ImGui.SameLine();
        ImGui.Checkbox("instances only", ref _instancesInstanceOnly);
        ImGui.SameLine();
        if (ImGui.Button("Dump to console")) DumpInstanceTable();

        if (_travelReturn is { } back)
        {
            if (ImGui.Button($"Return to {back.DisplayName}##travelback")) TravelBack();
            ImGui.SameLine();
            ImGui.TextDisabled($"({back.Position.X:F0}, {back.Position.Y:F0}, {back.Position.Z:F0})");
        }

        if (_travelStatus.Length > 0) ImGui.TextUnformatted(_travelStatus);

        ImGui.Separator();

        foreach (var m in _maps.All)
        {
            if (_instancesInstanceOnly && !m.IsInstance) continue;
            if (_instanceFilter.Length > 0 &&
                m.Directory.IndexOf(_instanceFilter, StringComparison.OrdinalIgnoreCase) < 0 &&
                m.Name.IndexOf(_instanceFilter, StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            var wdt = _mapWdts.GetValueOrDefault(m.Id);
            bool here = string.Equals(m.Directory, _config.Start.MapName,
                                      StringComparison.OrdinalIgnoreCase);

            string kind = wdt is null ? "no WDT" : wdt.UsesGlobalWmo ? "global WMO" : "terrain";
            string head = $"{m.Id,4}  {m.Name}   [{kind}]{(here ? "   <- you are here" : "")}";

            if (!ImGui.TreeNodeEx($"{head}##map{m.Id}", ImGuiTreeNodeFlags.SpanAvailWidth)) continue;

            ImGui.TextDisabled($"dir {m.Directory}   {m.InstanceType}");

            if (wdt is null)
            {
                ImGui.TextColored(new Vector4(1f, 0.5f, 0.4f, 1f),
                    $@"World\Maps\{m.Directory}\{m.Directory}.wdt not in the archives");
                ImGui.TreePop();
                continue;
            }

            ImGui.TextDisabled($"MPHD flags 0x{wdt.Flags:X4}   MVER {wdt.Version}");

            // Travel. H1: the button does not care whether this is a dungeon,
            // only whether the map has terrain to stand on - which is why
            // Azeroth and Deadmines are the same row with different numbers.
            bool canTravel = !wdt.UsesGlobalWmo && wdt.TileCount > 0 && !here;
            if (canTravel)
            {
                if (ImGui.Button($"Travel here##go{m.Id}")) TravelTo(m, wdt);
                ImGui.SameLine();
                ImGui.TextDisabled("arrival point, not the real entrance (H3)");
            }
            else if (here)
            {
                ImGui.TextDisabled("you are already on this map");
            }
            else if (wdt.UsesGlobalWmo)
            {
                ImGui.TextDisabled("global WMO - stage 3, not travellable yet");
            }
            else
            {
                ImGui.TextDisabled("no terrain tiles - nothing to stand on");
            }

            if (wdt.TileCount > 0)
            {
                var (cc, cr) = wdt.CentreTile;
                var (wx, wy) = TileOrigin(cc, cr);
                ImGui.Text($"{wdt.TileCount} tile(s)   col {wdt.MinCol}..{wdt.MaxCol}   " +
                           $"row {wdt.MinRow}..{wdt.MaxRow}");
                ImGui.Text($"centre tile [{cc},{cr}]   world approx ({wx:F0}, {wy:F0})");

                // The centre of the occupied block is not always an occupied
                // tile - `development` spreads 18 tiles across col 0..63 and its
                // centre [31,1] has no ADT. Stage 2's spawn uses SpawnTile for
                // exactly that reason, so the panel shows when the two differ.
                var (sc, sr) = wdt.SpawnTile;
                if (sc != cc || sr != cr)
                {
                    var (sx, sy) = TileOrigin(sc, sr);
                    ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f),
                        $"centre tile is EMPTY - spawn tile [{sc},{sr}] ({sx:F0}, {sy:F0})");
                }
            }
            else
            {
                ImGui.TextDisabled("0 tiles - no terrain on this map at all");
            }

            if (wdt.GlobalWmoPath is not null)
            {
                // TextUnformatted, not TextWrapped: ImGui.NET forwards the
                // string to native igText* as the FORMAT argument, so a '%' in
                // archive-derived data renders garbage. No 1.12 path contains
                // one, but the data decides that, not us.
                ImGui.TextUnformatted(wdt.GlobalWmoPath);
                if (wdt.GlobalWmo is { } g)
                {
                    ImGui.Text($"pos ({g.PosX:F1}, {g.PosY:F1}, {g.PosZ:F1})   " +
                               $"rot ({g.RotX:F1}, {g.RotY:F1}, {g.RotZ:F1})");
                    ImGui.Text($"lo ({g.BbMinX:F1}, {g.BbMinY:F1}, {g.BbMinZ:F1})   " +
                               $"hi ({g.BbMaxX:F1}, {g.BbMaxY:F1}, {g.BbMaxZ:F1})");
                }
                else
                {
                    ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f),
                        "MWMO names a WMO but there is no MODF placement");
                }
            }

            ImGui.TreePop();
        }
    }

    /// <summary>
    /// World position of a tile's origin corner, matching AdtTerrainReader:
    /// originX = (32 - row) * GRID_SIZE, originY = (32 - col) * GRID_SIZE.
    /// Row drives X and col drives Y - the transposition that makes WoW's grid
    /// confusing every single time.
    /// </summary>
    private static (float X, float Y) TileOrigin(int col, int row)
        => ((32 - row) * AdtTerrainReader.GRID_SIZE, (32 - col) * AdtTerrainReader.GRID_SIZE);

    /// <summary>
    /// The whole table in one console block, so it can be pasted next to
    /// PLAN_13 section 1 and diffed by eye. That comparison is the entire point
    /// of stage 1.
    /// </summary>
    private void DumpInstanceTable()
    {
        if (_maps is null || _mapWdts is null) return;

        Console.WriteLine("[instances] id  ty directory                  kind        tiles col     row     centre");
        foreach (var m in _maps.All)
        {
            var w = _mapWdts.GetValueOrDefault(m.Id);
            if (w is null)
            {
                Console.WriteLine($"[instances] {m.Id,4} {(int)m.InstanceType}  {m.Directory,-26} NO WDT");
                continue;
            }

            string kind = w.UsesGlobalWmo ? "globalWMO" : "terrain  ";
            string range = w.TileCount > 0
                ? $"{w.MinCol}..{w.MaxCol,-4} {w.MinRow}..{w.MaxRow,-4} [{w.CentreTile.Col},{w.CentreTile.Row}]"
                : "";
            Console.WriteLine($"[instances] {m.Id,4} {(int)m.InstanceType}  {m.Directory,-26} {kind} " +
                              $"{w.TileCount,5} {range} {w.GlobalWmoPath ?? ""}");
        }
    }
    // ========================================================================
    // STAGE 2 — travel (PLAN_13_INSTANCES.md H2, H3, H4).
    //
    // Swap the world's CONTENT. Do not re-run Load(): it builds the MPQ mount,
    // the worker pools, the GL renderers, the shaders and the character, almost
    // none of which is per-map, and re-running it would leak GL objects and
    // restart the workers.
    //
    // Five things are per-map, and PLAN_13 H2 named three of them as missing.
    // They are not any more:
    //
    //   AdtCache's map directory   -> AdtCache.SetMap        (new)
    //   the resident terrain tiles -> TerrainRenderer.UnloadAll (new)
    //   the built liquid meshes    -> LiquidRenderer.UnloadAll  (new)
    //   WMO and doodad placements  -> ResetForMapChange / ResetPlacements
    //   the collision world        -> LoadCollision, which already rebuilds it
    //
    // The refill is deliberately the SAME sequence UpdateWorldResidency runs on
    // a tile crossing, in the same order, because a second refill path is a
    // second thing to keep correct. H1: there is no `if (isDungeon)` anywhere
    // below - Deadmines and Azeroth differ only in how many tiles they have.
    // ========================================================================

    /// <summary>Where a travel came from, so the trip back lands where you left.</summary>
    private sealed class TravelReturn
    {
        public int MapId;
        public string MapName = "";
        public string DisplayName = "";
        public Vector3 Position;
        public float Facing;
    }

    private TravelReturn? _travelReturn;
    private bool _travelInProgress;
    private string _travelStatus = "";

    /// <summary>
    /// Decide where a map should drop you, and say honestly whether it can.
    ///
    /// H3: these are ARRIVAL POINTS, not entrances. There is no offline source
    /// for "where does this dungeon's door put you" - AreaTrigger.dbc carries
    /// the trigger volumes and the destinations live in the server's
    /// areatrigger_teleport table, which we do not have. When P2 lands the
    /// server sends a real position and this becomes a debug affordance, so do
    /// not hand-author twenty coordinates it will supersede.
    /// </summary>
    private bool TryPlanArrival(MapRow map, WdtFile wdt, out Vector2 where, out string why)
    {
        where = default;

        if (wdt.UsesGlobalWmo)
        {
            why = "global-WMO map - that is stage 3, and it needs WMO collision " +
                  "resident before the player is placed or you fall through the floor";
            return false;
        }

        if (wdt.TileCount == 0)
        {
            why = "terrain map with zero tiles - there is nothing to stand on";
            return false;
        }

        // SpawnTile, not CentreTile. `development` occupies 18 tiles scattered
        // across col 0..63 and the centre of that block is [31,1], which has no
        // ADT at all. Any map whose tiles are not a solid rectangle can do this.
        var (col, row) = wdt.SpawnTile;
        where = TerrainRenderer.TileCenter(col, row);
        why = "";
        return true;
    }

    /// <summary>
    /// Travel to a map. Returns false and changes NOTHING if it refuses.
    ///
    /// PLAN_13 §9: a refused transition is a bad afternoon, a half-cleared world
    /// is a bug hunt. So every check that can be made BEFORE the teardown is
    /// made before it, and the teardown itself cannot fail.
    /// </summary>
    private bool TravelTo(MapRow map, WdtFile wdt, Vector3? exactPosition = null, float? facing = null)
    {
        if (_travelInProgress) return false;

        if (_terrain is null || _adts is null || _controller is null)
        {
            _travelStatus = "world is not loaded";
            Console.WriteLine("[travel] refused - the world is not loaded");
            return false;
        }

        Vector2 arrive;
        if (exactPosition is Vector3 exact)
        {
            arrive = new Vector2(exact.X, exact.Y);
        }
        else if (!TryPlanArrival(map, wdt, out arrive, out string why))
        {
            _travelStatus = $"refused: {why}";
            Console.WriteLine($"[travel] refused {map.Name} - {why}");
            return false;
        }

        // Remember where we were BEFORE anything is torn down. A return trip
        // that has to guess is not a return trip.
        var from = new TravelReturn
        {
            MapId = _config.Start.Map,
            MapName = _config.Start.MapName,
            DisplayName = _maps?.ByDirectory(_config.Start.MapName)?.Name ?? _config.Start.MapName,
            Position = _controller.Position,
            Facing = _controller.Yaw,
        };

        _travelInProgress = true;
        var timer = Stopwatch.StartNew();
        try
        {
            Console.WriteLine($"[travel] {from.DisplayName} -> {map.Name} ({map.Directory}) " +
                              $"arriving at ({arrive.X:F0}, {arrive.Y:F0})");

            // A map change produces a legitimately enormous frame. That is not
            // the bug the hitch recorder hunts (PLAN_07).
            _hitch.SuppressFor(5.0);

            // ---- teardown, in dependency order ----------------------------
            // Collision first: it holds references into WMO and doodad
            // placements, and it is what the controller is standing on.
            TearDownWorldContent();

            // The cache LAST of the content clears and BEFORE any reload, so
            // nothing can re-read a stale tile between the two.
            _adts.SetMap(map.Directory);
            _residentCentre = null;

            // The current map is the config's Start block. Mutating it in
            // memory is how every other consumer - exterior lighting, vantages,
            // scene dumps, hitch records, the vmap collision loader - follows
            // along without a second notion of "which map are we on".
            _config.Start.Map = map.Id;
            _config.Start.MapName = map.Directory;

            // ---- refill: the same sequence a tile crossing runs ------------
            var centre = TerrainRenderer.TileAt(arrive.X, arrive.Y);
            _terrain.SetResidency(centre.col, centre.row, _config.Start.TileRadius, _adts);

            if (_terrain.TileCount == 0)
            {
                // Nothing loaded. Do not leave the player in an empty grid and
                // do not recurse into TravelTo - unwind by hand, here, once.
                Console.WriteLine($"[travel] FAILED - {map.Directory} produced 0 terrain tiles " +
                                  $"around [{centre.col},{centre.row}]; returning to {from.DisplayName}");
                _travelStatus = $"FAILED: {map.Name} produced no terrain";
                RestoreAfterFailedTravel(from);
                return false;
            }

            _wmo?.LoadForTiles(_terrain.LoadedTiles, _adts);
            _liquid?.LoadForTiles(_terrain.LoadedTiles, _adts);

            var preloadRing = TerrainRenderer.TileRing(centre.col, centre.row, WmoPreloadRadius);
            _wmo?.QueuePreloadForTiles(preloadRing, _adts);

            PopulateDoodads(centre, reportDiagnostics: true);
            _adts.Retain(preloadRing);
            _residentCentre = centre;

            // H4 — COLLISION BEFORE THE PLAYER IS PLACED, not after. This is
            // the ordering the whole plan says is most likely to be got wrong,
            // and the symptom of getting it wrong is falling through the floor.
            // LoadCollision is used rather than BeginCollisionBuild because the
            // latter builds on a worker and returns immediately, which is
            // exactly the race this line exists to avoid.
            LoadCollision();

            // ---- arrival ---------------------------------------------------
            float? ground = _terrain.SampleHeight(arrive.X, arrive.Y);
            float z = exactPosition?.Z ?? ground ?? 0f;
            if (exactPosition is null && ground is null)
            {
                // No MCVT under the arrival point - a hole, or the tile centre
                // landed on a gap. Fall from above rather than spawn at Z=0,
                // which on most maps is underground.
                z = 500f;
                Console.WriteLine($"[travel] no terrain height at ({arrive.X:F0}, {arrive.Y:F0}) - " +
                                  "arriving from above and falling in");
            }

            _controller.Teleport(arrive.X, arrive.Y, z);
            _controller.Yaw = facing ?? _controller.Yaw;
            _window.Camera.Target = _controller.Position;
            _window.Camera.Yaw = _controller.Yaw;

            _foliage?.ForceRescatter();

            _travelReturn = exactPosition is null ? from : null;
            _travelStatus =
                $"{map.Name}: {_terrain.TileCount} tile(s), {_wmo?.InstanceCount ?? 0} WMO, " +
                $"{_doodads?.InstanceCount ?? 0} doodad(s) in {timer.Elapsed.TotalSeconds:F2}s";

            Console.WriteLine($"[travel] arrived on {map.Name} at " +
                              $"({arrive.X:F0}, {arrive.Y:F0}, {z:F1}) tile [{centre.col},{centre.row}] - " +
                              $"{_terrain.TileCount} terrain, {_wmo?.InstanceCount ?? 0} WMO, " +
                              $"{_doodads?.InstanceCount ?? 0} doodad placement(s), " +
                              $"{timer.Elapsed.TotalSeconds:F2}s");
            return true;
        }
        catch (Exception ex)
        {
            // Logging alone would leave the world unrecoverably half-swapped:
            // the teardown has run, the config names the new map, and the player
            // is still standing at the OLD map's coordinates with no collision.
            // On the next frame UpdateWorldResidency would re-home the new map
            // around those coordinates and drop them through it. If this is the
            // first travel of the session there is no Return button either, so
            // the only other exit is a restart.
            Console.WriteLine($"[travel] FAILED - {ex.Message}");
            _travelStatus = $"FAILED: {ex.Message}";
            try { RestoreAfterFailedTravel(from); }
            catch (Exception restore)
            {
                Console.WriteLine($"[travel] restore ALSO failed - {restore.Message}. " +
                                  "The world is now inconsistent; restart the client.");
            }
            return false;
        }
        finally
        {
            _travelInProgress = false;
        }
    }

    /// <summary>
    /// Drop every piece of per-map content, including the two kinds nobody had
    /// noticed were per-map until this was reviewed.
    ///
    /// THE ASYNCHRONOUS COLLISION BUILD IS THE DANGEROUS ONE. BeginCollisionBuild
    /// snapshots placements onto a worker and stamps them with
    /// `_collisionGeneration`; AcceptReadyCollision installs the result if the
    /// stamp still matches. Nulling `_collision` does NOT stop a build that is
    /// already in flight, so a crossing that started a second before the travel
    /// would land the OLD map's BVH on the new map one frame after arrival - and
    /// you would be colliding with Elwynn inside the Deadmines, silently. Bumping
    /// the generation is what makes that result get dropped instead. It is H4's
    /// failure arriving by a door H4 did not look at.
    ///
    /// THE BACKGROUND DISCOVERY QUEUE is milder but the same shape: it holds the
    /// starting map's outer-ring tile coordinates and would feed them to the new
    /// map's preloader for the couple of seconds it takes to drain.
    /// </summary>
    private void TearDownWorldContent()
    {
        _collision = null;
        if (_controller is not null) _controller.Collision = null;
        _collisionDebug?.Clear();

        // Orphan any in-flight collision build so its result is discarded.
        _collisionBuildTask = null;
        _collisionGeneration++;
        _doodadCollisionDirty = false;
        _doodadCollisionPending = 0;

        // Tile coordinates of a map we are leaving.
        _backgroundDiscovery.Clear();
        _backgroundAdtLoad = null;

        _wmo?.ResetForMapChange();
        _doodads?.ResetPlacements();
        _liquid?.UnloadAll();
        _terrain?.UnloadAll();
    }

    /// <summary>
    /// Put the world back after a travel that tore down and then found nothing.
    /// Inline rather than a recursive TravelTo call, because the failure mode
    /// being handled is "the reload produced nothing" and recursing into the
    /// same code to handle it is how one bad map becomes an infinite loop.
    /// </summary>
    private void RestoreAfterFailedTravel(TravelReturn from)
    {
        if (_terrain is null || _adts is null || _controller is null) return;

        _config.Start.Map = from.MapId;
        _config.Start.MapName = from.MapName;

        TearDownWorldContent();
        _adts.SetMap(from.MapName);

        var centre = TerrainRenderer.TileAt(from.Position.X, from.Position.Y);
        _terrain.SetResidency(centre.col, centre.row, _config.Start.TileRadius, _adts);
        _wmo?.LoadForTiles(_terrain.LoadedTiles, _adts);
        _liquid?.LoadForTiles(_terrain.LoadedTiles, _adts);
        PopulateDoodads(centre, reportDiagnostics: false);
        _residentCentre = centre;
        LoadCollision();

        _controller.Teleport(from.Position.X, from.Position.Y, from.Position.Z);
        _controller.Yaw = from.Facing;
        _window.Camera.Target = _controller.Position;
        _foliage?.ForceRescatter();

        Console.WriteLine($"[travel] restored {from.DisplayName} at " +
                          $"({from.Position.X:F0}, {from.Position.Y:F0}, {from.Position.Z:F0})");
    }

    /// <summary>The trip back, to the exact spot you left from.</summary>
    private void TravelBack()
    {
        var back = _travelReturn;
        if (back is null || _maps is null || _mapWdts is null) return;

        var row = _maps.ByDirectory(back.MapName);
        var wdt = row is null ? null : _mapWdts.GetValueOrDefault(row.Id);
        if (row is null || wdt is null)
        {
            Console.WriteLine($"[travel] cannot return - no Map.dbc row or WDT for '{back.MapName}'");
            return;
        }

        // No _travelReturn = null here. TravelTo already clears it on the path
        // that succeeds (an explicit position means "this IS the return trip").
        // Clearing it here as well would only ever fire when the trip was
        // REFUSED or failed and restored - taking away the button that is the
        // way back, while the player is still in the dungeon.
        TravelTo(row, wdt, back.Position, back.Facing);
    }

}
