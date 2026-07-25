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

        sw.Stop();
        _instancesLoadMs = sw.Elapsed.TotalMilliseconds;
        Console.WriteLine($"[instances] {_maps.Count} map(s), {withWdt} with a WDT " +
                          $"({globalWmo} global-WMO, {terrain} terrain) in {_instancesLoadMs:F0} ms");
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
                ImGui.TextWrapped(wdt.GlobalWmoPath);
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
}
