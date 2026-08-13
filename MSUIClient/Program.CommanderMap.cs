using System.Numerics;
using ImGuiNET;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

// ── RTS commander map (M while the free view is up) ─────────────────────────
// A purpose-built strategic surface, deliberately NOT the vanilla parchment
// world map: continent view with hoverable zones showing the live bots/players
// census (SMSG_SUI_ZONE_INTEL), drill-in zone view showing the owner's own
// units, and click-to-fly for both units and open ground. Owner direction
// 2026-08-12 (CRPG_RTS_WIP.md "Future design" Part 1).
public sealed partial class GameLoop
{
    private readonly record struct ZoneIntel(ushort Bots, ushort Players);
    private readonly record struct CommanderUnit(ulong Guid, uint MapId, uint ZoneId, Vector3 Pos, byte Flags)
    {
        public bool Alive => (Flags & 1) != 0;
        public bool IsBot => (Flags & 2) != 0;
    }

    // ── tier-2 RTS worldstate (SMSG_SUI_RTS_STATE; all zeros in vanilla) ─────
    private readonly record struct RtsFaction(long HonorPool, int Ore, int Skins, int Herbs,
        ushort ControlledZones, ushort HeroesFielded, ushort HeroSlotCap);
    private readonly record struct RtsHero(ulong Guid, byte Team, byte HeroLevel, bool Dead);
    private readonly record struct RtsDungeon(uint MapId, byte Controller, byte LiveRunFlags);

    private byte _rtsMode;                           // 0 vanilla, 1 RTS match
    private byte _rtsModules;                        // bit0 honor, 1 heroes, 2 territory, 3 dungeons
    private readonly RtsFaction[] _rtsFactions = new RtsFaction[2];
    private readonly List<RtsHero> _rtsHeroes = [];
    private readonly List<RtsDungeon> _rtsDungeons = [];
    private readonly Dictionary<uint, byte> _zoneControl = [];   // zoneId -> controller (0x80 = contested)

    /// <summary>0 = alliance, 1 = horde (the session character's side).</summary>
    private int OwnTeamIndex => _net?.Player?.Race is 2 or 5 or 6 or 8 ? 1 : 0;

    private bool _commanderMapOpen;
    private uint _commanderMapZone;                  // 0 = continent view, else AreaId of the drilled-in zone
    private int _commanderMapContinent = -1;         // 0 EK / 1 Kalimdor; -1 = derive from the character's map on open
    private readonly Dictionary<uint, ZoneIntel> _zoneIntel = [];
    private readonly List<CommanderUnit> _commanderUnits = [];
    private double _zoneIntelAt;                     // NowSeconds() of the last SMSG; 0 = never
    private double _zoneIntelSentAt;                 // request throttle; 0 forces an immediate send
    private (float X, float Y, float Alt)? _commanderFlySettle;  // ground snap pending terrain streaming
    private double _commanderFlySettleAt;
    private List<(WorldMapAreaInfo Zone, Vector2 Min, Vector2 Max)>? _commanderZoneRects;
    private int _commanderZoneRectsFor = -1;
    private string? _commanderNotice;                // one-line transient message in the footer
    private double _commanderNoticeAt;

    private const double CommanderIntelRefreshSeconds = 5.0;
    private const float CommanderFlyAltitude = 60f;  // ground click: commander vantage
    private const float CommanderUnitAltitude = 25f; // unit click: close enough to read the fight

    /// <summary>Toggle from the M binding (UpdateWorldMapInput routes here while the free view is up).</summary>
    private void ToggleCommanderMap()
    {
        _commanderMapOpen = !_commanderMapOpen;
        if (!_commanderMapOpen) return;
        _zoneIntelSentAt = 0;                        // force the census request out this frame
        _commanderMapZone = 0;
        if (_commanderMapContinent < 0)
            _commanderMapContinent = _net?.Player?.Map == 1 ? 1 : 0;
    }

    /// <summary>
    /// Per-frame commander-map work, called unconditionally from the frame loop:
    /// the census request cadence (map open only) and the fly-to ground-snap
    /// latch, which must keep running after the map closes on a unit click.
    /// </summary>
    private void UpdateCommanderMap()
    {
        if (!_freeView)
        {
            _commanderFlySettle = null;              // never teleport a controller that drives a real body
            return;
        }

        if (_commanderFlySettle is (float sx, float sy, float alt))
        {
            if (_terrain?.SampleHeight(sx, sy) is float ground && _controller is not null)
            {
                _controller.Teleport(sx, sy, ground + alt);
                _window.Camera.Target = _controller.Position;
                _freecamCamSentAt = 0;               // the eye must follow the altitude snap immediately
                _commanderFlySettle = null;
            }
            else if (NowSeconds() - _commanderFlySettleAt > 5.0)
                _commanderFlySettle = null;          // hole in the terrain data; 500 is above everything anyway
        }

        if (_commanderMapOpen && _net is { IsInWorld: true })
        {
            double now = NowSeconds();
            if (now - _zoneIntelSentAt > CommanderIntelRefreshSeconds && _net.SuiZoneIntel())
            {
                _zoneIntelSentAt = now;
                _net.SuiRtsState();   // same cadence: mode, honor pools, heroes, objectives
            }
        }
    }

    /// <summary>SMSG_SUI_ZONE_INTEL: stride-versioned census + own-unit blocks (see SUI_WIRE_PROTOCOL.md).</summary>
    private void ApplySuiZoneIntel(byte[] body)
    {
        try
        {
            var r = new PacketReader(body);
            ushort zoneCount = r.ReadU16();
            byte zoneRowBytes = r.ReadU8();
            _zoneIntel.Clear();
            for (int i = 0; i < zoneCount; i++)
            {
                uint zone = r.ReadU32();
                ushort bots = r.ReadU16();
                ushort players = r.ReadU16();
                byte controller = 0;
                if (zoneRowBytes >= 9) controller = r.ReadU8();   // R3 territory; 0x80 = contested
                if (zoneRowBytes > 9) r.Skip(zoneRowBytes - 9);   // a future server grew the row
                _zoneIntel[zone] = new ZoneIntel(bots, players);
                _zoneControl[zone] = controller;
            }
            byte unitCount = r.ReadU8();
            byte unitRowBytes = r.ReadU8();
            _commanderUnits.Clear();
            for (int i = 0; i < unitCount; i++)
            {
                ulong guid = r.ReadU64();
                uint mapId = r.ReadU32();
                uint zoneId = r.ReadU32();
                Vector3 pos = r.ReadVector3();
                byte flags = r.ReadU8();
                if (unitRowBytes > 29) r.Skip(unitRowBytes - 29);
                _commanderUnits.Add(new CommanderUnit(guid, mapId, zoneId, pos, flags));
            }
            _zoneIntelAt = NowSeconds();
        }
        catch (Exception e)
        {
            // A malformed packet must be loud: silence here is indistinguishable
            // from "the world is empty" (the bot-bags lesson, round 13).
            Console.WriteLine($"[commander] SMSG_SUI_ZONE_INTEL parse failed: {e.Message}");
        }
    }

    /// <summary>SMSG_SUI_RTS_STATE: mode + module flags + stride-versioned faction/hero/dungeon blocks.</summary>
    private void ApplySuiRtsState(byte[] body)
    {
        try
        {
            var r = new PacketReader(body);
            _rtsMode = r.ReadU8();
            _rtsModules = r.ReadU8();
            byte factionStride = r.ReadU8();
            for (int t = 0; t < 2; t++)
            {
                long pool = (long)r.ReadU64();
                int ore = r.ReadI32();
                int skins = r.ReadI32();
                int herbs = r.ReadI32();
                ushort zones = r.ReadU16();
                ushort fielded = r.ReadU16();
                ushort cap = r.ReadU16();
                if (factionStride > 26) r.Skip(factionStride - 26);
                _rtsFactions[t] = new RtsFaction(pool, ore, skins, herbs, zones, fielded, cap);
            }
            byte heroCount = r.ReadU8();
            byte heroStride = r.ReadU8();
            _rtsHeroes.Clear();
            for (int i = 0; i < heroCount; i++)
            {
                ulong guid = r.ReadU64();
                byte team = r.ReadU8();
                byte level = r.ReadU8();
                byte dead = r.ReadU8();
                r.Skip(1);                                        // pad
                if (heroStride > 12) r.Skip(heroStride - 12);
                _rtsHeroes.Add(new RtsHero(guid, team, level, dead != 0));
            }
            byte dungeonCount = r.ReadU8();
            byte dungeonStride = r.ReadU8();
            _rtsDungeons.Clear();
            for (int i = 0; i < dungeonCount; i++)
            {
                uint mapId = r.ReadU32();
                byte controller = r.ReadU8();
                byte runFlags = r.ReadU8();
                r.Skip(1);                                        // pad
                if (dungeonStride > 7) r.Skip(dungeonStride - 7);
                _rtsDungeons.Add(new RtsDungeon(mapId, controller, runFlags));
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"[commander] SMSG_SUI_RTS_STATE parse failed: {e.Message}");
        }
    }

    /// <summary>SMSG_SUI_RTS_ACTION_RESULT: surface the verdict in the commander notice line.</summary>
    private void ApplySuiRtsActionResult(byte[] body)
    {
        try
        {
            var r = new PacketReader(body);
            byte action = r.ReadU8();
            byte result = r.ReadU8();
            r.ReadU64();                                          // subject guid
            long poolAfter = (long)r.ReadU64();
            string what = action switch { 1 => "Declare hero", 2 => "Upgrade hero", 3 => "Revive hero", _ => $"Action {action}" };
            string verdict = result switch
            {
                0 => "done",
                1 => "not enough Honor",
                2 => "no free hero slot",
                3 => "invalid target",
                _ => "not available",
            };
            CommanderShowNotice($"{what}: {verdict}  (Honor {poolAfter:n0})");
        }
        catch (Exception e)
        {
            Console.WriteLine($"[commander] SMSG_SUI_RTS_ACTION_RESULT parse failed: {e.Message}");
        }
    }

    /// <summary>
    /// Fly the free-view rig to a world point. If the destination terrain is not
    /// resident yet, travel in at 500 (the CreatorTeleport convention) and let the
    /// settle latch snap down once tiles stream. Zeroing the cam-heartbeat stamp
    /// makes the server's streaming eye follow on the next frame — no new wire.
    /// </summary>
    private void CommanderFlyTo(float worldX, float worldY, float altitude)
    {
        if (_controller is null || !_freeView) return;
        if (_terrain?.SampleHeight(worldX, worldY) is float ground)
        {
            _controller.Teleport(worldX, worldY, ground + altitude);
            _commanderFlySettle = null;
        }
        else
        {
            _controller.Teleport(worldX, worldY, 500f);
            _commanderFlySettle = (worldX, worldY, altitude);
            _commanderFlySettleAt = NowSeconds();
        }
        _window.Camera.Target = _controller.Position;
        _freecamCamSentAt = 0;
    }

    private void CommanderShowNotice(string text)
    {
        _commanderNotice = text;
        _commanderNoticeAt = NowSeconds();
    }

    // ── drawing ──────────────────────────────────────────────────────────────

    private void DrawCommanderMapFrame()
    {
        if (!_commanderMapOpen || _gameplayArt is null || _net is null) return;
        EnsureWorldMapAreas();
        EnsureAreaTableForMinimap();
        if (_worldMapAreas is null) { _commanderMapOpen = false; return; }

        Vector2 disp = ImGui.GetIO().DisplaySize;
        float s = MathF.Min(GameplayUiScale(), MathF.Min(disp.X / 1024f, disp.Y / 768f));
        Vector2 canvas = new Vector2(1024, 768) * s;
        Vector2 origin = (disp - canvas) * 0.5f;

        PushCreatorStyle();
        ImGui.SetNextWindowPos(Vector2.Zero);
        ImGui.SetNextWindowSize(disp);
        ImGui.Begin("###commander-map",
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground);

        // COMMAND_SURFACE strata: like the vanilla map, everything paints on the
        // foreground list so the scrim and map cover every ordinary window.
        ImDrawListPtr dl = ImGui.GetForegroundDrawList();
        dl.AddRectFilled(Vector2.Zero, disp, 0xF41A1614);          // near-opaque cool-dark scrim

        // Header ----------------------------------------------------------------
        double now = NowSeconds();
        dl.AddText(ImGui.GetFont(), 20f * s, origin + new Vector2(24, 12) * s, VanillaGold, "COMMANDER");
        if (_rtsMode == 1)
        {
            long ownPool = _rtsFactions[OwnTeamIndex].HonorPool;
            dl.AddText(ImGui.GetFont(), 12f * s, origin + new Vector2(24, 34) * s, 0xFF60C0E0,
                $"RTS MATCH  ·  Honor {ownPool:n0}");
        }
        WorldMapAreaInfo drilledZone = default;
        bool drilled = _commanderMapZone != 0 &&
                       _worldMapAreas.TryGetArea(_commanderMapZone, out drilledZone);
        if (!drilled) _commanderMapZone = 0;

        if (CommanderTab(dl, "##cmd-tab-ek", "Eastern Kingdoms", origin + new Vector2(360, 10) * s,
                new Vector2(150, 26), s, _commanderMapContinent == 0))
        { _commanderMapContinent = 0; _commanderMapZone = 0; }
        if (CommanderTab(dl, "##cmd-tab-kal", "Kalimdor", origin + new Vector2(520, 10) * s,
                new Vector2(120, 26), s, _commanderMapContinent == 1))
        { _commanderMapContinent = 1; _commanderMapZone = 0; }

        bool stale = _zoneIntelAt != 0 && now - _zoneIntelAt > CommanderIntelRefreshSeconds * 3;
        string intelText = _zoneIntelAt == 0 ? "awaiting intel..." : $"intel {now - _zoneIntelAt:0}s";
        uint intelColor = _zoneIntelAt == 0 ? 0xFF808080 : stale ? 0xFF3E8CE6 : 0xFF9CC49C;
        dl.AddText(ImGui.GetFont(), 13f * s, origin + new Vector2(860, 18) * s, intelColor, intelText);

        Vector2 close = origin + new Vector2(982, 4) * s;
        DrawImageButton(dl, "##commander-close", close, new Vector2(32) * s,
            @"Interface\Buttons\UI-Panel-MinimizeButton-Up", @"Interface\Buttons\UI-Panel-MinimizeButton-Down",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");
        if (ImGui.IsItemClicked()) _commanderMapOpen = false;

        // Layout: map body left, intel panel right --------------------------------
        int mapId = _commanderMapContinent == 1 ? 1 : 0;
        Vector2 panelMin = origin + new Vector2(770, 56) * s;
        Vector2 panelMax = origin + new Vector2(1008, 724) * s;
        float mapW = 730f * s;
        float mapH = mapW * (668f / 1002f);
        Vector2 mapMin = origin + new Vector2(20, 56) * s + new Vector2(0, (668f * s - mapH) * 0.5f);
        // (668 logical body height; the authored aspect never fills it exactly)
        mapMin.Y = MathF.Max(mapMin.Y, origin.Y + 56f * s);

        uint playerMap = _net.Player?.Map ?? 0;
        if (!drilled)
            DrawCommanderContinentView(dl, mapId, playerMap, mapMin, mapW, mapH, s, stale);
        else
            DrawCommanderZoneView(dl, drilledZone, playerMap, mapMin, mapW, mapH, s);

        DrawCommanderIntelPanel(dl, mapId, drilled, drilledZone, panelMin, panelMax, s, stale);

        // Footer ------------------------------------------------------------------
        string hint = drilled
            ? "Click a unit to take the camera there  ·  click ground to fly there  ·  right-click for the continent  ·  M / Esc closes"
            : "Hover a zone for intel  ·  click a zone to inspect your forces there  ·  M / Esc closes";
        DrawCenteredText(dl, origin + new Vector2(512, 744) * s, hint, 13f * s, 0xFF9A948C);
        if (_commanderNotice is not null && now - _commanderNoticeAt < 4.0)
            DrawCenteredText(dl, origin + new Vector2(512, 726) * s, _commanderNotice, 14f * s, 0xFF3EA6E6);

        // Right-click backs out of the drill-in (free-view camera look is gated off
        // while the map is up, so the button is unambiguous here).
        if (drilled && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            _commanderMapZone = 0;

        ImGui.End();
        PopCreatorStyle();
    }

    private void DrawCommanderContinentView(ImDrawListPtr dl, int mapId, uint playerMap,
        Vector2 mapMin, float mapW, float mapH, float s, bool stale)
    {
        if (_worldMapAreas is null || !_worldMapAreas.TryGetContinent((uint)mapId, out WorldMapAreaInfo cont))
            return;

        DrawCommanderMapTiles(dl, cont.Directory, mapMin, mapW, mapH);

        // Zone rects in continent-fraction space, cached per continent. The
        // normalizers take X(worldY)/Y(worldX) — the deliberate axis swap every
        // map projection in this codebase uses.
        if (_commanderZoneRectsFor != mapId || _commanderZoneRects is null)
        {
            _commanderZoneRectsFor = mapId;
            _commanderZoneRects = [];
            foreach (WorldMapAreaInfo zone in _worldMapAreas.Areas)
            {
                if (zone.MapId != (uint)mapId || zone.Directory.Length == 0 || zone.AreaId == 0) continue;
                Vector2 a = new(cont.X(zone.Left), cont.Y(zone.Top));
                Vector2 b = new(cont.X(zone.Right), cont.Y(zone.Bottom));
                _commanderZoneRects.Add((zone, Vector2.Min(a, b), Vector2.Max(a, b)));
            }
        }

        Vector2 mapSize = new(mapW, mapH);
        foreach ((WorldMapAreaInfo _, Vector2 min, Vector2 max) in _commanderZoneRects)
            dl.AddRect(mapMin + min * mapSize, mapMin + max * mapSize, 0x24FFFFFF, 0, 0, 1f);

        // Hover hit-test: SMALLEST containing rect wins — WorldMapArea rects
        // overlap (Stormwind sits inside Elwynn's), and the city must win on it.
        ImGui.SetCursorScreenPos(mapMin);
        ImGui.InvisibleButton("##commander-continent", mapSize);
        (WorldMapAreaInfo Zone, Vector2 Min, Vector2 Max)? hovered = null;
        if (ImGui.IsItemHovered())
        {
            Vector2 f = (ImGui.GetMousePos() - mapMin) / mapSize;
            float bestArea = float.MaxValue;
            foreach (var r in _commanderZoneRects)
            {
                if (f.X < r.Min.X || f.X > r.Max.X || f.Y < r.Min.Y || f.Y > r.Max.Y) continue;
                float area = (r.Max.X - r.Min.X) * (r.Max.Y - r.Min.Y);
                if (area < bestArea) { bestArea = area; hovered = r; }
            }
        }

        // Census pills at rect centres (dimmed while the feed is stale).
        foreach ((WorldMapAreaInfo zone, Vector2 min, Vector2 max) in _commanderZoneRects)
        {
            if (!_zoneIntel.TryGetValue(zone.AreaId, out ZoneIntel intel) ||
                intel.Bots + intel.Players == 0) continue;
            Vector2 centre = mapMin + (min + max) * 0.5f * mapSize;
            DrawCommanderPill(dl, centre, intel, s, stale);
        }

        // Camera position diamond (only meaningful on the character's continent).
        if ((uint)mapId == playerMap && _controller is not null)
        {
            Vector2 cam = mapMin + new Vector2(cont.X(_controller.Position.Y), cont.Y(_controller.Position.X)) * mapSize;
            DrawCommanderDiamond(dl, cam, 7f * s, VanillaGold);
        }

        if (hovered is (WorldMapAreaInfo hz, Vector2 hmin, Vector2 hmax))
        {
            dl.AddRectFilled(mapMin + hmin * mapSize, mapMin + hmax * mapSize, 0x22FFFFFF);
            dl.AddRect(mapMin + hmin * mapSize, mapMin + hmax * mapSize, 0xFF60C0E0, 0, 0, 2f * s);
            _zoneIntel.TryGetValue(hz.AreaId, out ZoneIntel intel);
            DrawCommanderFlyout(dl, ImGui.GetMousePos() + new Vector2(18f, 10f) * s, s,
                _areas?.ZoneName(hz.AreaId) is { Length: > 0 } n ? n : hz.Directory,
                $"Bots {intel.Bots}", $"Players {intel.Players}");
            if (ImGui.IsItemClicked()) _commanderMapZone = hz.AreaId;
        }
    }

    private void DrawCommanderZoneView(ImDrawListPtr dl, WorldMapAreaInfo zone, uint playerMap,
        Vector2 mapMin, float mapW, float mapH, float s)
    {
        DrawCommanderMapTiles(dl, zone.Directory, mapMin, mapW, mapH);
        Vector2 mapSize = new(mapW, mapH);

        string zoneName = _areas?.ZoneName(zone.AreaId) is { Length: > 0 } n ? n : zone.Directory;
        dl.AddText(ImGui.GetFont(), 16f * s, mapMin + new Vector2(8f, 6f) * s, 0xFFFFFFFF, zoneName);

        // Own units in this zone. Zone id is authoritative; the rect test rescues
        // units the server stamped with a subzone id the map doesn't know.
        ImGui.SetCursorScreenPos(mapMin);
        ImGui.InvisibleButton("##commander-zone", mapSize);
        bool mapHovered = ImGui.IsItemHovered();
        Vector2 mouse = ImGui.GetMousePos();

        CommanderUnit? hoveredUnit = null;
        Vector2 hoveredPos = default;
        foreach (CommanderUnit unit in _commanderUnits)
        {
            if (unit.MapId != zone.MapId) continue;
            Vector2 f = new(zone.X(unit.Pos.Y), zone.Y(unit.Pos.X));
            bool inside = f.X is > 0.001f and < 0.999f && f.Y is > 0.001f and < 0.999f;
            if (unit.ZoneId != zone.AreaId && !inside) continue;
            Vector2 p = mapMin + f * mapSize;
            bool hover = mapHovered && (mouse - p).Length() < 12f * s;
            if (hover) { hoveredUnit = unit; hoveredPos = p; }
            DrawCommanderUnitMarker(dl, unit, p, s, hover);
        }

        if (hoveredUnit is CommanderUnit hu)
        {
            string name = CommanderUnitName(hu.Guid);
            DrawCommanderFlyout(dl, mouse + new Vector2(18f, 10f) * s, s,
                name + (hu.Alive ? "" : "  (dead)"),
                hu.IsBot ? "Bot — click to take the camera there" : "Player — click to take the camera there");
            if (ImGui.IsItemClicked())
            {
                if (hu.MapId != playerMap)
                    CommanderShowNotice("Cross-continent view arrives in phase 1.5 — the streaming eye cannot leave your character's continent.");
                else
                {
                    CommanderFlyTo(hu.Pos.X, hu.Pos.Y, CommanderUnitAltitude);
                    _commanderMapOpen = false;      // unit click = the deliberate "enter the world" action
                }
            }
        }
        else if (mapHovered && ImGui.IsItemClicked())
        {
            // Ground click: inverse transform through the ZONE rect (fraction.X is
            // worldY, fraction.Y is worldX — the same swap as every projection).
            Vector2 f = (mouse - mapMin) / mapSize;
            float worldY = zone.Left + f.X * (zone.Right - zone.Left);
            float worldX = zone.Top + f.Y * (zone.Bottom - zone.Top);
            if (zone.MapId != playerMap)
                CommanderShowNotice("Cross-continent view arrives in phase 1.5 — the streaming eye cannot leave your character's continent.");
            else
                CommanderFlyTo(worldX, worldY, CommanderFlyAltitude);   // map stays open: navigation, not commitment
        }

        // Camera diamond inside the zone map too.
        if (zone.MapId == playerMap && _controller is not null)
        {
            Vector2 f = new(zone.X(_controller.Position.Y), zone.Y(_controller.Position.X));
            if (f.X is > 0f and < 1f && f.Y is > 0f and < 1f)
                DrawCommanderDiamond(dl, mapMin + f * mapSize, 7f * s, VanillaGold);
        }
    }

    private void DrawCommanderIntelPanel(ImDrawListPtr dl, int mapId, bool drilled, WorldMapAreaInfo drilledZone,
        Vector2 panelMin, Vector2 panelMax, float s, bool stale)
    {
        dl.AddRectFilled(panelMin, panelMax, 0xC0201C18, 4f * s);
        dl.AddRect(panelMin, panelMax, 0x40FFFFFF, 4f * s);
        float x = panelMin.X + 12f * s;
        float y = panelMin.Y + 10f * s;
        float line = 20f * s;

        if (!drilled)
        {
            dl.AddText(ImGui.GetFont(), 14f * s, new Vector2(x, y), VanillaGold, "ACTIVE ZONES");
            y += line * 1.3f;
            uint dim = stale ? 0xFF8A8A8Au : 0xFFE6E6E6u;
            int shown = 0;
            (uint Zone, ZoneIntel Intel)[] ranked = _zoneIntel
                .OrderByDescending(kv => kv.Value.Bots + kv.Value.Players)
                .Select(kv => (kv.Key, kv.Value)).ToArray();
            ushort otherBots = 0, otherPlayers = 0;
            foreach ((uint zoneId, ZoneIntel intel) in ranked)
            {
                if (_worldMapAreas?.TryGetArea(zoneId, out WorldMapAreaInfo za) != true)
                {
                    otherBots += intel.Bots; otherPlayers += intel.Players;
                    continue;
                }
                if (shown >= 24) { otherBots += intel.Bots; otherPlayers += intel.Players; continue; }
                shown++;
                string name = _areas?.ZoneName(zoneId) is { Length: > 0 } zn ? zn : za.Directory;
                ImGui.SetCursorScreenPos(new Vector2(x - 4f * s, y - 2f * s));
                ImGui.InvisibleButton($"##cmd-active-{zoneId}", new Vector2(panelMax.X - x - 8f * s, line));
                bool rowHover = ImGui.IsItemHovered();
                if (rowHover)
                    dl.AddRectFilled(new Vector2(x - 4f * s, y - 2f * s),
                        new Vector2(panelMax.X - 8f * s, y - 2f * s + line), 0x18FFFFFF, 2f * s);
                dl.AddText(ImGui.GetFont(), 13f * s, new Vector2(x, y), rowHover ? 0xFFFFFFFFu : dim, name);
                string counts = $"{intel.Bots} / {intel.Players}";
                float cw = ImGui.CalcTextSize(counts).X * (13f * s / MathF.Max(1f, ImGui.GetFontSize()));
                dl.AddText(ImGui.GetFont(), 13f * s, new Vector2(panelMax.X - 12f * s - cw, y), dim, counts);
                if (ImGui.IsItemClicked())
                {
                    _commanderMapContinent = za.MapId == 1 ? 1 : 0;
                    _commanderMapZone = zoneId;
                }
                y += line;
            }
            if (otherBots + otherPlayers > 0)
            {
                y += line * 0.3f;
                dl.AddText(ImGui.GetFont(), 12f * s, new Vector2(x, y), 0xFF8A8A8A,
                    $"Elsewhere (dungeons etc.): {otherBots} / {otherPlayers}");
                y += line;
            }
            if (_zoneIntel.Count == 0)
                dl.AddText(ImGui.GetFont(), 13f * s, new Vector2(x, y), 0xFF8A8A8A,
                    _zoneIntelAt == 0 ? "No census yet." : "The world is empty.");
            y += line * 0.5f;
            dl.AddText(ImGui.GetFont(), 12f * s, new Vector2(x, y), 0xFF8A8A8A, "bots / players");
        }
        else
        {
            dl.AddText(ImGui.GetFont(), 14f * s, new Vector2(x, y), VanillaGold, "YOUR FORCES");
            y += line * 1.3f;
            int listed = 0;
            foreach (CommanderUnit unit in _commanderUnits)
            {
                if (unit.MapId != drilledZone.MapId || unit.ZoneId != drilledZone.AreaId)
                {
                    Vector2 f = new(drilledZone.X(unit.Pos.Y), drilledZone.Y(unit.Pos.X));
                    if (unit.MapId != drilledZone.MapId ||
                        f.X is <= 0.001f or >= 0.999f || f.Y is <= 0.001f or >= 0.999f) continue;
                }
                listed++;
                string name = CommanderUnitName(unit.Guid);
                uint color = unit.Alive ? CommanderClassColor(BotClassName(unit.Guid, name)) : 0xFF707070;
                ImGui.SetCursorScreenPos(new Vector2(x - 4f * s, y - 2f * s));
                ImGui.InvisibleButton($"##cmd-unit-{unit.Guid}", new Vector2(panelMax.X - x - 8f * s, line));
                bool rowHover = ImGui.IsItemHovered();
                if (rowHover)
                    dl.AddRectFilled(new Vector2(x - 4f * s, y - 2f * s),
                        new Vector2(panelMax.X - 8f * s, y - 2f * s + line), 0x18FFFFFF, 2f * s);
                dl.AddCircleFilled(new Vector2(x + 5f * s, y + 7f * s), 4f * s, color);
                dl.AddText(ImGui.GetFont(), 13f * s, new Vector2(x + 14f * s, y),
                    unit.Alive ? (rowHover ? 0xFFFFFFFFu : 0xFFE6E6E6u) : 0xFF8A8A8Au,
                    name + (unit.Alive ? "" : " (dead)"));
                if (ImGui.IsItemClicked())
                {
                    if (unit.MapId != (_net?.Player?.Map ?? 0))
                        CommanderShowNotice("Cross-continent view arrives in phase 1.5 — the streaming eye cannot leave your character's continent.");
                    else
                    {
                        CommanderFlyTo(unit.Pos.X, unit.Pos.Y, CommanderUnitAltitude);
                        _commanderMapOpen = false;
                    }
                }
                y += line;
            }
            if (listed == 0)
                dl.AddText(ImGui.GetFont(), 13f * s, new Vector2(x, y), 0xFF8A8A8A, "None of your units are here.");
        }
    }

    // ── small drawing helpers ────────────────────────────────────────────────

    private void DrawCommanderMapTiles(ImDrawListPtr dl, string directory, Vector2 mapMin, float mapW, float mapH)
    {
        // The authored 1002x668 map in 12 256px tiles, multiplied toward a cool
        // grey-blue so it reads as a strategic backdrop, not the vanilla parchment.
        const uint tint = 0xFFB4A296;
        float tileW = mapW / 4f, tileH = mapH / 3f;
        dl.PushClipRect(mapMin, mapMin + new Vector2(mapW, mapH), true);
        dl.AddRectFilled(mapMin, mapMin + new Vector2(mapW, mapH), 0xFF0E0C0A);
        for (int row = 0; row < 3; row++)
        for (int col = 0; col < 4; col++)
        {
            int index = row * 4 + col + 1;
            uint texture = _gameplayArt!.Handle($@"Interface\WorldMap\{directory}\{directory}{index}.blp");
            if (texture == 0) continue;
            Vector2 min = mapMin + new Vector2(col * tileW, row * tileH);
            dl.AddImage((nint)texture, min, min + new Vector2(tileW, tileH), Vector2.Zero, Vector2.One, tint);
        }
        dl.PopClipRect();
        dl.AddRect(mapMin, mapMin + new Vector2(mapW, mapH), 0x40FFFFFF);
    }

    private static void DrawCommanderPill(ImDrawListPtr dl, Vector2 centre, ZoneIntel intel, float s, bool stale)
    {
        string text = $"{intel.Bots} / {intel.Players}";
        float size = 12f * s;
        Vector2 measured = ImGui.CalcTextSize(text) * (size / MathF.Max(1f, ImGui.GetFontSize()));
        Vector2 pad = new(6f * s, 3f * s);
        Vector2 min = centre - measured * 0.5f - pad;
        Vector2 max = centre + measured * 0.5f + pad;
        dl.AddRectFilled(min, max, stale ? 0x90242424u : 0xC8242C30u, (max.Y - min.Y) * 0.5f);
        dl.AddText(ImGui.GetFont(), size, centre - measured * 0.5f,
            stale ? 0xFF9A9A9Au : 0xFFF0E6D2u, text);
    }

    private static void DrawCommanderDiamond(ImDrawListPtr dl, Vector2 centre, float r, uint color)
    {
        dl.AddQuadFilled(centre + new Vector2(0, -r), centre + new Vector2(r, 0),
            centre + new Vector2(0, r), centre + new Vector2(-r, 0), color);
        dl.AddQuad(centre + new Vector2(0, -r), centre + new Vector2(r, 0),
            centre + new Vector2(0, r), centre + new Vector2(-r, 0), 0xFF202020, 1.5f);
    }

    private void DrawCommanderUnitMarker(ImDrawListPtr dl, CommanderUnit unit, Vector2 p, float s, bool hover)
    {
        string name = CommanderUnitName(unit.Guid);
        float r = (hover ? 9f : 7f) * s;
        uint color = unit.Alive ? CommanderClassColor(BotClassName(unit.Guid, name)) : 0xFF606060;
        dl.AddCircleFilled(p, r, color);
        dl.AddCircle(p, r, 0xFF101010, 0, 2f * s);
        if (unit.Guid == _net?.PlayerGuid)
            DrawCommanderDiamond(dl, p + new Vector2(0, -r - 6f * s), 4f * s, VanillaGold);
        // Name label with a hard shadow — the map art underneath is busy.
        float size = 12f * s;
        Vector2 measured = ImGui.CalcTextSize(name) * (size / MathF.Max(1f, ImGui.GetFontSize()));
        Vector2 at = p + new Vector2(-measured.X * 0.5f, r + 3f * s);
        dl.AddText(ImGui.GetFont(), size, at + new Vector2(1f, 1f) * s, 0xE0000000, name);
        dl.AddText(ImGui.GetFont(), size, at, unit.Alive ? 0xFFF0F0F0u : 0xFFA0A0A0u, name);
    }

    private static void DrawCommanderFlyout(ImDrawListPtr dl, Vector2 at, float s, params string[] lines)
    {
        float title = 14f * s, body = 13f * s, gap = 4f * s;
        float w = 0, h = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            float size = i == 0 ? title : body;
            Vector2 m = ImGui.CalcTextSize(lines[i]) * (size / MathF.Max(1f, ImGui.GetFontSize()));
            w = MathF.Max(w, m.X);
            h += m.Y + (i > 0 ? gap : 0);
        }
        Vector2 pad = new(10f * s, 8f * s);
        Vector2 min = at, max = at + new Vector2(w, h) + pad * 2;
        Vector2 disp = ImGui.GetIO().DisplaySize;
        Vector2 shift = new(MathF.Min(0, disp.X - 8f - max.X), MathF.Min(0, disp.Y - 8f - max.Y));
        min += shift; max += shift;
        dl.AddRectFilled(min, max, 0xF0181410, 4f * s);
        dl.AddRect(min, max, 0xFF60C0E0, 4f * s);
        float y = min.Y + pad.Y;
        for (int i = 0; i < lines.Length; i++)
        {
            float size = i == 0 ? title : body;
            dl.AddText(ImGui.GetFont(), size, new Vector2(min.X + pad.X, y),
                i == 0 ? VanillaGold : 0xFFE6E6E6u, lines[i]);
            y += ImGui.CalcTextSize(lines[i]).Y * (size / MathF.Max(1f, ImGui.GetFontSize())) + gap;
        }
    }

    private bool CommanderTab(ImDrawListPtr dl, string id, string label, Vector2 min, Vector2 logicalSize,
        float s, bool selected)
    {
        Vector2 size = logicalSize * s;
        ImGui.SetCursorScreenPos(min);
        bool clicked = ImGui.InvisibleButton(id, size);
        bool hover = ImGui.IsItemHovered();
        uint fill = selected ? 0xC8303A40u : hover ? 0x60303A40u : 0x30282C30u;
        dl.AddRectFilled(min, min + size, fill, 4f * s);
        if (selected) dl.AddRect(min, min + size, 0xFF60C0E0, 4f * s);
        DrawCenteredText(dl, min + size * 0.5f, label, 13f * s, selected ? 0xFFFFFFFFu : 0xFFC0BCB4u);
        return clicked;
    }

    private string CommanderUnitName(ulong guid)
    {
        if (guid == _net?.PlayerGuid) return _net.PlayerName is { Length: > 0 } own ? own : "You";
        foreach (PartyMember m in _partyMembers)
            if (m.Guid == guid) return m.Name;
        return "Unit";
    }

    private static uint CommanderClassColor(string className) => className switch
    {
        // The vanilla class palette, ABGR.
        "Warrior" => 0xFF6E9CC7, "Paladin" => 0xFFBA8CF5, "Hunter" => 0xFF73D4AB,
        "Rogue" => 0xFF69F5FF, "Priest" => 0xFFFFFFFF, "Shaman" => 0xFFDE7000,
        "Mage" => 0xFFF0CC69, "Warlock" => 0xFFC98294, "Druid" => 0xFF0A7DFF,
        _ => 0xFFD0D0D0,
    };
}
