using System.Numerics;
using ImGuiNET;
using MSUIClient.Formats;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// Teleport-panel destinations that come out of the client's OWN data instead of
// a hand-typed list, so "all of them" means all of them and no coordinate in
// here was ever guessed:
//
//   Hubs      TaxiNodes.dbc - every flight master in the game, which is the
//             closest thing 1.12 has to a canonical list of the towns that
//             matter, at the exact position the flight map itself draws.
//   Dungeons  Map.dbc instanceType 1 and 2, entered at the destination named by
//   Raids     VMaNGOS's own areatrigger_teleport row - the same coordinates the
//             server would teleport you to for walking through the door.
//
// ENTERING IS TWO ROUTES, AND WHICH ONE APPLIES IS A PROPERTY OF THE MAP RATHER
// THAN OF THE DUNGEON. A map with ADT tiles refills its resident ring in place
// (TravelTo) while the world keeps running. A global-WMO map has no tile grid to
// refill, which is why TravelTo refuses one, so it comes up through the ordinary
// loading curtain instead - the route SMSG_NEW_WORLD takes and the same one the
// client boots into an instance with.
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    private readonly byte[] _creatorHubSearchBuf = new byte[64];
    private readonly byte[] _creatorInstanceSearchBuf = new byte[64];

    /// <summary>Every teleport row that LANDS on a map, keyed by that map. Built
    /// once: the table is a few hundred rows and this is read every frame.</summary>
    private Dictionary<int, List<AreaTriggerTeleport>>? _creatorEntrancesInto;

    private void RegisterCreatorDestinationSections()
    {
        CreatorSection("Teleport", "tp-hubs", "Hubs (every flight point)", false, DrawCreatorHubs);
        CreatorSection("Teleport", "tp-dungeons", "Dungeons", false,
            () => DrawCreatorInstanceList(MapInstanceType.Dungeon, "dungeon"));
        CreatorSection("Teleport", "tp-raids", "Raids", false,
            () => DrawCreatorInstanceList(MapInstanceType.Raid, "raid"));
    }

    private List<AreaTriggerTeleport> CreatorEntrancesInto(int mapId)
    {
        // Built only once the table is actually there, so an early call cannot
        // freeze an empty index in place for the rest of the session.
        if (_creatorEntrancesInto is null && _teleports is not null)
        {
            _creatorEntrancesInto = new Dictionary<int, List<AreaTriggerTeleport>>();
            foreach (AreaTriggerTeleport tel in _teleports.ById.Values)
            {
                if (!_creatorEntrancesInto.TryGetValue(tel.TargetMap, out var list))
                    _creatorEntrancesInto[tel.TargetMap] = list = [];
                list.Add(tel);
            }
            foreach (var list in _creatorEntrancesInto.Values)
                list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        }
        return _creatorEntrancesInto?.GetValueOrDefault(mapId) ?? [];
    }

    // ── hubs ─────────────────────────────────────────────────────────────────

    private void DrawCreatorHubs()
    {
        EnsureInstanceData();
        EnsureTaxiNodes();
        NotePanelField("buttons");
        float cs = CreatorUiScale;

        if (_taxiNodes is null)
        {
            ImGui.TextDisabled(@"DBFilesClient\TaxiNodes.dbc unavailable - see the console.");
            return;
        }

        ImGui.SetNextItemWidth(220f * cs);
        ImGui.InputText("##hub-search", _creatorHubSearchBuf, (uint)_creatorHubSearchBuf.Length);
        ImGui.SameLine();
        ImGui.TextDisabled("filter");
        string query = BufToString(_creatorHubSearchBuf);

        // The |15500| bound is the taxi panel's own: a handful of rows carry
        // placeholder coordinates that belong to no map.
        var byMap = _taxiNodes.Nodes
            .Where(n => n.Name.Length > 0 &&
                        MathF.Abs(n.Position.X) < 15500f && MathF.Abs(n.Position.Y) < 15500f)
            .Where(n => query.Length == 0 ||
                        n.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .GroupBy(n => n.MapId)
            .OrderBy(g => g.Key)
            .ToList();

        int shown = 0;
        foreach (var group in byMap)
        {
            var nodes = group
                .OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (nodes.Count == 0) continue;
            shown += nodes.Count;

            string mapName = _maps?.Get((int)group.Key)?.Name ?? $"map {group.Key}";
            if (!CreatorCategory($"tp-hub-map-{group.Key}", $"{mapName}  ({nodes.Count})",
                    group.Key is 0 or 1)) continue;

            float columnW = CreatorColumnWidth(nodes.Select(n => n.Name)) * CreatorButtonMul;
            float rowH = CreatorButtonHeight;
            int columns = Math.Max(1, (int)(ImGui.GetContentRegionAvail().X / (columnW + 24f * cs)));
            int column = 0;
            foreach (TaxiNodeInfo node in nodes)
            {
                if (column % columns != 0) ImGui.SameLine(column % columns * (columnW + 24f * cs));
                column++;
                // Travels with the node's own Z rather than ground-snapping: a
                // flight master often stands on a tower, a zeppelin platform or a
                // city roof that a terrain sample would drop straight through.
                if (CreatorAnchoredButton($"{node.Name}##hub{node.Id}", new Vector2(columnW, rowH)))
                    CreatorTeleport((int)node.MapId, node.Position.X, node.Position.Y,
                        node.Position.Z, node.Name);
            }
            ImGui.Spacing();
        }

        if (shown == 0)
            ImGui.TextDisabled(query.Length > 0
                ? "No flight point matches that filter."
                : "TaxiNodes.dbc carried no usable rows.");
    }

    // ── dungeons and raids ───────────────────────────────────────────────────

    private void DrawCreatorInstanceList(MapInstanceType kind, string noun)
    {
        EnsureInstanceData();
        NotePanelField("buttons");
        float cs = CreatorUiScale;

        if (_maps is null || _mapWdts is null)
        {
            ImGui.TextDisabled("Map.dbc did not load - see the console.");
            return;
        }

        ImGui.SetNextItemWidth(220f * cs);
        ImGui.InputText("##instance-search", _creatorInstanceSearchBuf,
            (uint)_creatorInstanceSearchBuf.Length);
        ImGui.SameLine();
        ImGui.TextDisabled("filter");
        string query = BufToString(_creatorInstanceSearchBuf);

        var rows = _maps.All
            .Where(m => m.InstanceType == kind)
            .Where(m => query.Length == 0 ||
                        m.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        m.Directory.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (rows.Count == 0)
        {
            ImGui.TextDisabled(query.Length > 0
                ? $"No {noun} matches that filter."
                : $"Map.dbc lists no {noun}.");
            return;
        }

        ImGui.TextDisabled($"{rows.Count} {noun}(s) - Enter drops you at the door's own " +
                           "destination, Entrance puts you outside it in the world.");

        foreach (MapRow map in rows)
        {
            ImGui.PushID(map.Id);
            WdtFile? wdt = _mapWdts.GetValueOrDefault(map.Id);
            var doors = CreatorEntrancesInto(map.Id);
            AreaTriggerTeleport? door = doors.Count > 0 ? doors[0] : null;
            var outside = EntrancesTo(map.Id);
            bool here = map.Id == _config.Start.Map;
            bool busy = _worldLoading || _travelInProgress;

            bool canEnter = wdt is not null && door is not null && !here && !busy;
            if (!canEnter) ImGui.BeginDisabled();
            if (CreatorRowButton("Enter", $"in{map.Id}") &&
                wdt is not null && door is not null)
                CreatorEnterInstance(map, wdt, door.TargetPosition, door.TargetOrientation,
                    $"{map.Name} ({door.Name})");
            if (!canEnter) ImGui.EndDisabled();

            bool canStand = outside.Count > 0 && !busy;
            ImGui.SameLine();
            if (!canStand) ImGui.BeginDisabled();
            if (CreatorRowButton("Entrance", $"out{map.Id}") && outside.Count > 0)
            {
                var (tel, volume, stand, facing) = outside[0];
                GoToEntrance(volume, stand, facing, tel.Name);
                _creatorPanel = CreatorPanel.None;
            }
            if (!canStand) ImGui.EndDisabled();

            string level = door is { RequiredLevel: > 0 } lvl ? $"   lvl {lvl.RequiredLevel}+" : "";
            string note =
                here ? "   <- you are here"
                : wdt is null ? $@"   no World\Maps\{map.Directory} WDT in the archives"
                : door is null ? "   no entrance row in areatrigger_teleport.tsv"
                : wdt.UsesGlobalWmo ? "   global WMO (loads behind the curtain)"
                : "";
            ImGui.SameLine();
            ImGui.TextUnformatted($"{map.Name}{level}{note}");
            ImGui.PopID();
        }
    }

    /// <summary>A red panel button sized to its caption, with an id suffix so the
    /// same caption can repeat down a list without colliding.</summary>
    private bool CreatorRowButton(string caption, string id)
    {
        float mul = CreatorButtonMul;
        var size = new Vector2(
            ImGui.CalcTextSize(caption).X + 28f * CreatorUiScale * mul, CreatorButtonHeight);
        return CreatorAnchoredButton($"{caption}##{id}", size);
    }

    // ── the two entry routes ─────────────────────────────────────────────────

    /// <summary>Enter an instance at an authored destination, by whichever route
    /// that map's WDT allows.</summary>
    private bool CreatorEnterInstance(MapRow map, WdtFile wdt, Vector3 position, float facing,
        string why)
        => !wdt.UsesGlobalWmo && wdt.TileCount > 0
            ? CreatorTeleport(map.Id, position.X, position.Y, position.Z, why)
            : CreatorLoadIntoMap(map, position, facing, why);

    /// <summary>
    /// Bring a map up behind the loading curtain - the route SMSG_NEW_WORLD takes
    /// - for destinations the in-place refill cannot serve.
    ///
    /// TravelTo swaps the resident TILE RING while the world keeps running, so a
    /// global-WMO instance (the Stockade, the Deeprun Tram, Blackrock Depths) has
    /// nothing for it to refill and it refuses by design. Those maps load
    /// perfectly well; they just have to load the way the client boots into one.
    /// </summary>
    private bool CreatorLoadIntoMap(MapRow map, Vector3 position, float facing, string why)
    {
        if (_gl is null || _adts is null || _controller is null || _terrain is null) return false;
        if (_worldLoading || _travelInProgress)
        {
            _travelStatus = "a world load is already running";
            return false;
        }

        // Recorded BEFORE the teardown, exactly like TravelTo: a Return button
        // that has to guess where it came from is not a return button.
        var from = new TravelReturn
        {
            MapId = _config.Start.Map,
            MapName = _config.Start.MapName,
            DisplayName = _maps?.ByDirectory(_config.Start.MapName)?.Name ?? _config.Start.MapName,
            Position = _controller.Position,
            Facing = _controller.Yaw,
        };

        Console.WriteLine($"[travel] '{why}' loads {map.Name} ({map.Directory}) behind the " +
                          $"curtain at ({position.X:F0}, {position.Y:F0}, {position.Z:F0})");

        // A map change is a legitimately enormous frame, not the hitch PLAN_07 hunts.
        _hitch.SuppressFor(5.0);
        TearDownWorldContent();
        _adts.SetMap(map.Directory);
        _residentCentre = null;

        // The Start block IS "which map are we on" for every other consumer, and
        // the load's own arrival step places the player from these coordinates.
        _config.Start.Map = map.Id;
        _config.Start.MapName = map.Directory;
        _config.Start.X = position.X;
        _config.Start.Y = position.Y;
        _config.Start.Z = position.Z;
        _config.Start.Orientation = facing;

        _controller.Teleport(position.X, position.Y, position.Z);
        _controller.Yaw = facing;
        _window.Camera.Target = _controller.Position;
        _window.Camera.Yaw = facing;
        // The old map may have collapsed the boom at these unrelated coordinates;
        // never carry a camera-collision result through an opaque transition.
        _window.Camera.EffectiveDistance = _window.Camera.Distance;

        _travelReturn = from;
        _travelStatus = $"loading {map.Name}...";
        _creatorPanel = CreatorPanel.None;   // get the menus out of the view
        ArmEnterWorldCurtain(_gl, map.Id);
        BeginWorldLoad(_gl);
        return true;
    }
}
