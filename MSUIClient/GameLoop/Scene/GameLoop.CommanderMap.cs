using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
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
    private uint _commanderMapZone;                  // 0 = both-continent overview, else AreaId of the drilled-in zone
    private readonly Dictionary<uint, ZoneIntel> _zoneIntel = [];
    private readonly List<CommanderUnit> _commanderUnits = [];
    private double _zoneIntelAt;                     // NowSeconds() of the last SMSG; 0 = never
    private double _zoneIntelSentAt;                 // request throttle; 0 forces an immediate send
    private (float X, float Y, float Alt)? _commanderFlySettle;  // ground snap pending terrain streaming
    private double _commanderFlySettleAt;
    private WorldMapOverlayCatalog? _commanderMapOverlays;
    private WorldMapHighlightCatalog? _commanderMapHighlights;
    private WorldMapZoneHitCatalog? _commanderMapHits;
    private bool _commanderMapOverlaysLoaded;
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
            if (zoneRowBytes < 8) throw new InvalidDataException($"zone row stride {zoneRowBytes} is smaller than 8");
            var nextIntel = new Dictionary<uint, ZoneIntel>(zoneCount);
            var nextControl = new Dictionary<uint, byte>(zoneCount);
            for (int i = 0; i < zoneCount; i++)
            {
                uint zone = r.ReadU32();
                ushort bots = r.ReadU16();
                ushort players = r.ReadU16();
                byte controller = 0;
                if (zoneRowBytes >= 9) controller = r.ReadU8();   // R3 territory; 0x80 = contested
                if (zoneRowBytes > 9) r.Skip(zoneRowBytes - 9);   // a future server grew the row
                nextIntel[zone] = new ZoneIntel(bots, players);
                nextControl[zone] = controller;
            }
            byte unitCount = r.ReadU8();
            byte unitRowBytes = r.ReadU8();
            if (unitRowBytes < 29) throw new InvalidDataException($"unit row stride {unitRowBytes} is smaller than 29");
            var nextUnits = new List<CommanderUnit>(unitCount);
            for (int i = 0; i < unitCount; i++)
            {
                ulong guid = r.ReadU64();
                uint mapId = r.ReadU32();
                uint zoneId = r.ReadU32();
                Vector3 pos = r.ReadVector3();
                byte flags = r.ReadU8();
                if (unitRowBytes > 29) r.Skip(unitRowBytes - 29);
                nextUnits.Add(new CommanderUnit(guid, mapId, zoneId, pos, flags));
            }

            // Publish one complete census. A malformed future packet must not mix
            // partial new zones with units or territory state from the previous one.
            _zoneIntel.Clear();
            foreach ((uint zone, ZoneIntel intel) in nextIntel) _zoneIntel[zone] = intel;
            _zoneControl.Clear();
            foreach ((uint zone, byte controller) in nextControl) _zoneControl[zone] = controller;
            _commanderUnits.Clear();
            _commanderUnits.AddRange(nextUnits);
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
            string honor = CommanderMapUiLaw.ShowHonor(_rtsMode, _rtsModules)
                ? $"  (Honor {poolAfter:n0})" : string.Empty;
            CommanderShowNotice($"{what}: {verdict}{honor}");
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

    private readonly record struct CommanderZoneRect(
        WorldMapAreaInfo Zone, Vector2 Min, Vector2 Max);

    private readonly record struct CommanderOverviewMap(
        uint MapId, string Name, WorldMapAreaInfo Continent,
        Vector2 CardMin, Vector2 CardMax, Vector2 MapMin, Vector2 MapSize,
        Vector4 Crop, IReadOnlyList<CommanderZoneRect> Zones);

    private void DrawCommanderMapFrame()
    {
        if (!_commanderMapOpen || _gameplayArt is null || _net is null) return;
        EnsureWorldMapAreas();
        EnsureCommanderMapOverlays();
        EnsureAreaTableForMinimap();
        if (_worldMapAreas is null) { _commanderMapOpen = false; return; }

        Vector2 disp = ImGui.GetIO().DisplaySize;
        float s = MathF.Min(GameplayUiScale(),
            MathF.Min(disp.X / 1024f, disp.Y / 720f));
        s = Math.Clamp(s, 0.65f, 2.5f);
        float margin = Math.Clamp(MathF.Min(disp.X, disp.Y) * 0.0125f, 12f * s, 24f * s);
        float gap = Math.Clamp(disp.X * 0.008f, 12f * s, 22f * s);
        float headerH = Math.Clamp(disp.Y * 0.055f, 54f * s, 70f * s);
        float footerH = 34f * s;
        double now = NowSeconds();
        bool stale = _zoneIntelAt != 0 && now - _zoneIntelAt > CommanderIntelRefreshSeconds * 3;

        WorldMapAreaInfo drilledZone = default;
        bool drilled = _commanderMapZone != 0 &&
                       _worldMapAreas.TryGetArea(_commanderMapZone, out drilledZone);
        if (!drilled) _commanderMapZone = 0;

        PushCreatorStyle();
        ImGui.SetNextWindowPos(Vector2.Zero);
        ImGui.SetNextWindowSize(disp);
        ImGui.Begin("###commander-map",
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground);

        ImDrawListPtr dl = ImGui.GetForegroundDrawList();
        dl.AddRectFilled(Vector2.Zero, disp, 0xFF111416);

        Vector2 headerMin = new(margin, margin);
        Vector2 headerMax = new(disp.X - margin, margin + headerH);
        dl.AddRectFilled(headerMin, headerMax, 0xFA181410, 6f * s);
        dl.AddRect(headerMin, headerMax, 0x50FFFFFF, 6f * s);
        dl.AddText(ImGui.GetFont(), 20f * s, headerMin + new Vector2(14f, 7f) * s,
            VanillaGold, "COMMANDER");
        if (_rtsMode == 1)
        {
            string campaign = CommanderMapUiLaw.CampaignStatus(
                _rtsMode, _rtsModules, _rtsFactions[OwnTeamIndex].HonorPool);
            dl.AddText(ImGui.GetFont(), 11f * s, headerMin + new Vector2(14f, 33f) * s,
                0xFF60C0E0, campaign);
        }

        string intelText = _zoneIntelAt == 0 ? "Waiting for population data" :
            stale ? $"Data stale - {now - _zoneIntelAt:0}s" : $"Updated {now - _zoneIntelAt:0}s ago";
        uint intelColor = _zoneIntelAt == 0 ? 0xFF808080 : stale ? 0xFF3E8CE6 : 0xFF9CC49C;
        float intelSize = 12f * s;
        Vector2 close = new(headerMax.X - 34f * s, headerMin.Y + (headerH - 28f * s) * 0.5f);
        float intelWidth = ImGui.CalcTextSize(intelText).X * (intelSize / MathF.Max(1f, ImGui.GetFontSize()));
        string centreTitle = drilled
            ? $"WORLD OVERVIEW  >  {(drilledZone.MapId == 1 ? "KALIMDOR" : "EASTERN KINGDOMS")}  >  {CommanderZoneName(drilledZone)}"
            : "WORLD OVERVIEW";
        float titleSize = (drilled ? 13f : 15f) * s;
        float titleLeft = headerMin.X + 210f * s;
        float titleRight = close.X - intelWidth - 22f * s;
        string fittedTitle = CommanderFitText(centreTitle,
            MathF.Max(80f * s, titleRight - titleLeft), titleSize);
        DrawCenteredText(dl, new Vector2((titleLeft + titleRight) * 0.5f,
                headerMin.Y + headerH * 0.5f),
            fittedTitle, titleSize, 0xFFF2E8D4);
        dl.AddText(ImGui.GetFont(), intelSize,
            new Vector2(close.X - intelWidth - 14f * s, headerMin.Y + (headerH - intelSize) * 0.5f),
            intelColor, intelText);
        DrawImageButton(dl, "##commander-close", close, new Vector2(28f) * s,
            @"Interface\Buttons\UI-Panel-MinimizeButton-Up", @"Interface\Buttons\UI-Panel-MinimizeButton-Down",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");
        if (ImGui.IsItemClicked()) _commanderMapOpen = false;

        Vector2 bodyMin = new(margin, headerMax.Y + gap);
        Vector2 bodyMax = new(disp.X - margin, disp.Y - margin - footerH);
        uint playerMap = _net.Player?.Map ?? 0;
        if (!drilled)
            DrawCommanderWorldOverview(dl, bodyMin, bodyMax, gap, s, playerMap, stale);
        else
            DrawCommanderDrill(dl, drilledZone, bodyMin, bodyMax, gap, s, playerMap);

        bool noticeActive = _commanderNotice is not null && now - _commanderNoticeAt < 4.0;
        string hint = noticeActive ? _commanderNotice! : drilled
            ? "Click ground to fly - click a unit to enter the fight - right-click returns - M / Esc closes"
            : "Hover a zone for intel - click it to inspect - both continents are fully revealed - M / Esc closes";
        Vector2 legendPoint = new(margin + 5f * s,
            disp.Y - margin - footerH * 0.45f);
        DrawCommanderDiamond(dl, legendPoint, 4.5f * s, VanillaGold);
        dl.AddText(ImGui.GetFont(), 11f * s,
            legendPoint + new Vector2(10f, -5.5f) * s,
            0xFF96918Bu, "Camera  -  B Bots  -  P Players");
        DrawCenteredText(dl, new Vector2(disp.X * 0.5f, disp.Y - margin - footerH * 0.45f),
            hint, 12f * s, noticeActive ? 0xFF3EA6E6u : 0xFFB6B0A8u);

        if (drilled && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            _commanderMapZone = 0;

        ImGui.End();
        PopCreatorStyle();
    }

    private void EnsureCommanderMapOverlays()
    {
        if (_commanderMapOverlaysLoaded) return;
        _commanderMapOverlaysLoaded = true;
        try
        {
            if (_mpq is not null)
            {
                _commanderMapOverlays = WorldMapOverlayCatalog.Load(_mpq);
                if (_worldMapAreas is not null)
                {
                    _commanderMapHighlights = WorldMapHighlightCatalog.Build(_worldMapAreas);
                    _commanderMapHits = WorldMapZoneHitCatalog.Load(_mpq, _worldMapAreas);
                }
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"[commander] WorldMapOverlay load failed: {e.Message}");
        }
    }

    private string CommanderZoneName(WorldMapAreaInfo zone) =>
        _areas?.ZoneName(zone.AreaId) is { Length: > 0 } name ? name : zone.Directory;

    private void DrawCommanderWorldOverview(ImDrawListPtr dl, Vector2 bodyMin, Vector2 bodyMax,
        float gap, float s, uint playerMap, bool stale)
    {
        if (_worldMapAreas is null) return;

        int ekRows = CommanderPresenceRows(0).Count;
        int kalRows = CommanderPresenceRows(1).Count;
        int visibleRows = Math.Min(3, Math.Max(ekRows, kalRows));
        float presenceH = Math.Clamp((58f + Math.Max(1, visibleRows) * 21f) * s,
            84f * s, 132f * s);
        Vector2 stageMax = new(bodyMax.X, MathF.Max(bodyMin.Y + 220f * s,
            bodyMax.Y - presenceH - gap));
        CommanderMapUiLaw.DualViewportLayout layout = CommanderMapUiLaw.LayoutDualViewports(
            bodyMin, stageMax - bodyMin, gap, 420f);

        CommanderOverviewMap? ek = BuildCommanderOverviewMap(0, "EASTERN KINGDOMS",
            layout.EasternKingdoms, 30f * s);
        CommanderOverviewMap? kal = BuildCommanderOverviewMap(1, "KALIMDOR",
            layout.Kalimdor, 30f * s);
        if (ek is null || kal is null) return;

        Vector2 presenceMin = new(bodyMin.X, stageMax.Y + gap);
        Vector2 presenceMax = new(bodyMax.X, bodyMax.Y);
        uint rowHover = DrawCommanderWorldPresence(dl, ek.Value, kal.Value,
            presenceMin, presenceMax, gap, s, stale);
        uint ekHighlight = 0;
        uint kalHighlight = 0;
        if (rowHover != 0 &&
            _worldMapAreas.TryGetArea(rowHover, out WorldMapAreaInfo rowArea))
        {
            if (rowArea.MapId == 1) kalHighlight = rowHover;
            else if (rowArea.MapId == 0) ekHighlight = rowHover;
        }
        DrawCommanderOverviewMap(dl, ek.Value, playerMap, s, stale, ekHighlight);
        DrawCommanderOverviewMap(dl, kal.Value, playerMap, s, stale, kalHighlight);
    }

    private CommanderOverviewMap? BuildCommanderOverviewMap(uint mapId, string name,
        CommanderMapUiLaw.ScreenRect cell, float cardHeaderH)
    {
        if (_worldMapAreas is null || !_worldMapAreas.TryGetContinent(mapId, out WorldMapAreaInfo continent))
            return null;

        var zones = new List<CommanderZoneRect>();
        Vector2 unionMin = Vector2.One;
        Vector2 unionMax = Vector2.Zero;
        foreach (WorldMapAreaInfo zone in _worldMapAreas.Areas)
        {
            if (zone.MapId != mapId || zone.AreaId == 0 || zone.Directory.Length == 0) continue;
            Vector2 first = new(continent.X(zone.Left), continent.Y(zone.Top));
            Vector2 second = new(continent.X(zone.Right), continent.Y(zone.Bottom));
            Vector2 min = Vector2.Min(first, second);
            Vector2 max = Vector2.Max(first, second);
            if (max.X <= min.X || max.Y <= min.Y) continue;
            zones.Add(new CommanderZoneRect(zone, min, max));
            unionMin = Vector2.Min(unionMin, min);
            unionMax = Vector2.Max(unionMax, max);
        }
        if (zones.Count == 0) return null;

        CommanderMapUiLaw.ScreenRect mapCell = new(
            cell.Min + new Vector2(0f, cardHeaderH),
            new Vector2(cell.Size.X, MathF.Max(1f, cell.Size.Y - cardHeaderH)));
        var rawCrop = new CommanderMapUiLaw.NormalizedRect(unionMin, unionMax);
        CommanderMapUiLaw.NormalizedRect padded = CommanderMapUiLaw.PadCrop(rawCrop, new Vector2(0.045f));
        CommanderMapUiLaw.NormalizedRect crop = CommanderGrowCropToAspect(padded, mapCell.Aspect);
        CommanderMapUiLaw.ScreenRect viewport = CommanderMapUiLaw.FitViewportToCrop(mapCell, crop);
        return new CommanderOverviewMap(mapId, name, continent,
            cell.Min, cell.Max, viewport.Min, viewport.Size,
            new Vector4(crop.Min.X, crop.Min.Y, crop.Max.X, crop.Max.Y), zones);
    }

    private static CommanderMapUiLaw.NormalizedRect CommanderGrowCropToAspect(
        CommanderMapUiLaw.NormalizedRect crop, float screenAspect)
    {
        Vector2 min = crop.Min;
        Vector2 max = crop.Max;
        Vector2 size = crop.Size;
        float desiredNormalizedAspect = screenAspect * CommanderMapUiLaw.AuthoredHeight /
                                        CommanderMapUiLaw.AuthoredWidth;
        if (size.X / size.Y < desiredNormalizedAspect)
        {
            float width = MathF.Min(1f, size.Y * desiredNormalizedAspect);
            float centre = (min.X + max.X) * 0.5f;
            min.X = Math.Clamp(centre - width * 0.5f, 0f, 1f - width);
            max.X = min.X + width;
        }
        else
        {
            float height = MathF.Min(1f, size.X / desiredNormalizedAspect);
            float centre = (min.Y + max.Y) * 0.5f;
            min.Y = Math.Clamp(centre - height * 0.5f, 0f, 1f - height);
            max.Y = min.Y + height;
        }
        return new CommanderMapUiLaw.NormalizedRect(min, max);
    }

    private static CommanderMapUiLaw.ScreenRect CommanderViewport(in CommanderOverviewMap map) =>
        new(map.MapMin, map.MapSize);

    private static CommanderMapUiLaw.NormalizedRect CommanderCrop(in CommanderOverviewMap map) =>
        new(new Vector2(map.Crop.X, map.Crop.Y), new Vector2(map.Crop.Z, map.Crop.W));

    private void DrawCommanderOverviewMap(ImDrawListPtr dl, in CommanderOverviewMap map,
        uint playerMap, float s, bool stale, uint forcedHighlightArea)
    {
        CommanderMapUiLaw.ScreenRect viewport = CommanderViewport(map);
        CommanderMapUiLaw.NormalizedRect crop = CommanderCrop(map);
        dl.AddRectFilled(map.CardMin, map.CardMax, 0xF01B1815, 5f * s);
        DrawCommanderMapTilesCropped(dl, map.Continent.Directory, viewport, crop, 0xFFFFFFFF);

        ImGui.SetCursorScreenPos(viewport.Min);
        ImGui.InvisibleButton($"##commander-world-{map.MapId}", viewport.Size);
        CommanderZoneRect? hovered = ImGui.IsItemHovered()
            ? ResolveCommanderZone(map, ImGui.GetMousePos()) : null;
        CommanderZoneRect? highlighted = hovered;
        if (highlighted is null && forcedHighlightArea != 0)
        {
            CommanderZoneRect rowZone = map.Zones.FirstOrDefault(
                z => z.Zone.AreaId == forcedHighlightArea);
            if (rowZone.Zone.AreaId != 0) highlighted = rowZone;
        }
        if (highlighted is CommanderZoneRect highlight)
            DrawCommanderZoneHighlight(dl, map, highlight, s, drawLabel: true);
        if (hovered is CommanderZoneRect hover)
        {
            if (ImGui.IsItemClicked()) _commanderMapZone = hover.Zone.AreaId;
        }

        Vector2? cameraPoint = null;
        if (CommanderMapUiLaw.ShowCameraMarker(map.MapId, playerMap) && _controller is not null)
        {
            Vector2 normalized = new(map.Continent.X(_controller.Position.Y),
                map.Continent.Y(_controller.Position.X));
            if (CommanderMapUiLaw.Contains(crop, normalized))
                cameraPoint = CommanderMapUiLaw.ProjectCrop(viewport, crop, normalized);
        }

        var occupied = new List<(Vector2 Min, Vector2 Max)>();
        foreach (CommanderZoneRect zone in map.Zones)
        {
            if (!_zoneIntel.TryGetValue(zone.Zone.AreaId, out ZoneIntel intel) ||
                intel.Bots + intel.Players == 0) continue;
            Vector2 centre = CommanderMapUiLaw.ProjectCrop(viewport, crop,
                (zone.Min + zone.Max) * 0.5f);
            if (!CommanderMapUiLaw.Contains(viewport, centre)) continue;
            occupied.Add(DrawCommanderPill(dl, centre, intel, s, stale, cameraPoint,
                viewport.Min, viewport.Max, occupied));
        }
        if (cameraPoint is Vector2 camera)
            DrawCommanderDiamond(dl, camera, 7f * s, VanillaGold);

        dl.AddRectFilled(map.CardMin, new Vector2(map.CardMax.X, map.MapMin.Y), 0xE0181410);
        dl.AddText(ImGui.GetFont(), 14f * s, map.CardMin + new Vector2(12f, 7f) * s,
            VanillaGold, map.Name);
        string summary = highlighted is CommanderZoneRect hz
            ? CommanderZoneSummary(hz.Zone)
            : CommanderContinentSummary(map.MapId);
        DrawCommanderRightAlignedText(dl, map.CardMax.X - 12f * s,
            map.CardMin.Y + 8f * s, summary, 12f * s,
            highlighted is null ? 0xFF9A948Du : 0xFFF0E6D2u);
        dl.AddRect(map.CardMin, map.CardMax, 0x60FFFFFF, 5f * s, ImDrawFlags.None, 1f * s);
    }

    private CommanderZoneRect? ResolveCommanderZone(in CommanderOverviewMap map, Vector2 screen)
    {
        CommanderMapUiLaw.ScreenRect viewport = CommanderViewport(map);
        CommanderMapUiLaw.NormalizedRect crop = CommanderCrop(map);
        if (!CommanderMapUiLaw.Contains(viewport, screen)) return null;
        Vector2 normalized = CommanderMapUiLaw.UnprojectCrop(viewport, crop, screen);
        if (_commanderMapHits?.TryResolveArea(map.MapId, map.Continent,
                normalized, out uint areaId) != true)
            return null;
        foreach (CommanderZoneRect zone in map.Zones)
            if (zone.Zone.AreaId == areaId) return zone;
        return null;
    }

    private void DrawCommanderZoneHighlight(ImDrawListPtr dl, in CommanderOverviewMap map,
        in CommanderZoneRect zone, float s, bool drawLabel)
    {
        if (_commanderMapHighlights?.TryGetArea(
                zone.Zone.AreaId, out WorldMapHighlightInfo highlight) != true)
            return;
        CommanderMapUiLaw.ScreenRect viewport = CommanderViewport(map);
        CommanderMapUiLaw.NormalizedRect crop = CommanderCrop(map);
        Vector2 min = CommanderMapUiLaw.ProjectCrop(viewport, crop,
            new Vector2(highlight.Bounds.Left, highlight.Bounds.Top));
        Vector2 max = CommanderMapUiLaw.ProjectCrop(viewport, crop,
            new Vector2(highlight.Bounds.Right, highlight.Bounds.Bottom));
        // World-map highlights are colored ADD-authored art. Preserve their authored hue;
        // AdditiveHandle makes the black field transparent for ImGui without whitening it.
        uint texture = _gameplayArt!.AdditiveHandle(highlight.TexturePath);
        dl.PushClipRect(viewport.Min, viewport.Max, true);
        if (texture != 0)
            dl.AddImage((nint)texture, Vector2.Min(min, max), Vector2.Max(min, max),
                Vector2.Zero, new Vector2(highlight.UMax, highlight.VMax),
                0xFFFFFFFF);
        if (drawLabel)
            DrawCommanderOutlinedCenteredText(dl, (min + max) * 0.5f,
                CommanderZoneName(zone.Zone), 14f * s, VanillaGold);
        dl.PopClipRect();
    }

    private string CommanderZoneSummary(WorldMapAreaInfo zone)
    {
        _zoneIntel.TryGetValue(zone.AreaId, out ZoneIntel intel);
        return $"{CommanderZoneName(zone)}  -  B{intel.Bots}  -  P{intel.Players}";
    }

    private string CommanderContinentSummary(uint mapId)
    {
        int zones = 0, bots = 0, players = 0;
        foreach ((uint areaId, ZoneIntel intel) in _zoneIntel)
        {
            if (_worldMapAreas?.TryGetArea(areaId, out WorldMapAreaInfo area) != true ||
                area.MapId != mapId || intel.Bots + intel.Players == 0) continue;
            zones++;
            bots += intel.Bots;
            players += intel.Players;
        }
        return zones == 0 ? "No players or bots reported" :
            $"{zones} populated zones  -  B{bots}  -  P{players}";
    }

    private List<(WorldMapAreaInfo Area, ZoneIntel Intel, string Name)> CommanderPresenceRows(uint mapId)
    {
        var rows = new List<(WorldMapAreaInfo, ZoneIntel, string)>();
        foreach ((uint areaId, ZoneIntel intel) in _zoneIntel)
        {
            if (_worldMapAreas?.TryGetArea(areaId, out WorldMapAreaInfo area) != true ||
                !CommanderMapUiLaw.ShowWorldPresence(area.MapId, intel.Bots, intel.Players) ||
                area.MapId != mapId) continue;
            rows.Add((area, intel, CommanderZoneName(area)));
        }
        rows.Sort((a, b) => (b.Item2.Bots + b.Item2.Players).CompareTo(
            a.Item2.Bots + a.Item2.Players));
        return rows;
    }

    private uint DrawCommanderWorldPresence(ImDrawListPtr dl, in CommanderOverviewMap ek,
        in CommanderOverviewMap kal, Vector2 min, Vector2 max, float gap, float s, bool stale)
    {
        dl.AddRectFilled(min, max, 0xF01B1815, 5f * s);
        dl.AddRect(min, max, 0x50FFFFFF, 5f * s);
        float half = (max.X - min.X - gap) * 0.5f;
        uint hover = 0;
        hover = DrawCommanderPresenceColumn(dl, 0, ek.Name,
            new Vector2(min.X, min.Y), new Vector2(min.X + half, max.Y), s, stale);
        uint kalHover = DrawCommanderPresenceColumn(dl, 1, kal.Name,
            new Vector2(min.X + half + gap, min.Y), max, s, stale);
        return kalHover != 0 ? kalHover : hover;
    }

    private uint DrawCommanderPresenceColumn(ImDrawListPtr dl, uint mapId, string continentName,
        Vector2 min, Vector2 max, float s, bool stale)
    {
        List<(WorldMapAreaInfo Area, ZoneIntel Intel, string Name)> rows = CommanderPresenceRows(mapId);
        int visible = Math.Min(3, rows.Count);
        float x = min.X + 12f * s;
        float y = min.Y + 9f * s;
        dl.AddText(ImGui.GetFont(), 12f * s, new Vector2(x, y), VanillaGold,
            continentName + " PRESENCE");
        float botRight = max.X - 62f * s;
        float playerRight = max.X - 12f * s;
        DrawCommanderRightAlignedText(dl, botRight, y, "BOTS", 10f * s, 0xFF8A8A8A);
        DrawCommanderRightAlignedText(dl, playerRight, y, "PLAYERS", 10f * s, 0xFF8A8A8A);
        y += 22f * s;
        uint hovered = 0;
        uint color = stale ? 0xFF8A8A8Au : 0xFFE6E6E6u;
        for (int i = 0; i < visible; i++)
        {
            var row = rows[i];
            ImGui.SetCursorScreenPos(new Vector2(x - 4f * s, y - 2f * s));
            ImGui.InvisibleButton($"##cmd-world-presence-{mapId}-{row.Area.AreaId}",
                new Vector2(max.X - x - 8f * s, 20f * s));
            bool rowHover = ImGui.IsItemHovered();
            if (rowHover)
            {
                hovered = row.Area.AreaId;
                dl.AddRectFilled(new Vector2(x - 4f * s, y - 2f * s),
                    new Vector2(max.X - 8f * s, y + 17f * s), 0x20FFFFFF, 2f * s);
            }
            dl.AddText(ImGui.GetFont(), 12f * s, new Vector2(x, y),
                rowHover ? 0xFFFFFFFFu : color,
                CommanderFitText(row.Name, botRight - x - 48f * s, 12f * s));
            DrawCommanderRightAlignedText(dl, botRight, y, row.Intel.Bots.ToString(), 12f * s, color);
            DrawCommanderRightAlignedText(dl, playerRight, y, row.Intel.Players.ToString(), 12f * s, color);
            if (ImGui.IsItemClicked()) _commanderMapZone = row.Area.AreaId;
            y += 20f * s;
        }
        if (rows.Count == 0)
            dl.AddText(ImGui.GetFont(), 11f * s, new Vector2(x, y), 0xFF8A8A8A,
                _zoneIntelAt == 0 ? "Waiting for population data." : "No players or bots reported.");
        else if (rows.Count > visible)
            dl.AddText(ImGui.GetFont(), 10f * s, new Vector2(x, y), 0xFF8A8A8A,
                $"+{rows.Count - visible} more populated zones");
        return hovered;
    }

    private void DrawCommanderDrill(ImDrawListPtr dl, WorldMapAreaInfo zone,
        Vector2 bodyMin, Vector2 bodyMax, float gap, float s, uint playerMap)
    {
        float backH = 34f * s;
        if (CommanderTab(dl, "##cmd-world-overview", "< World overview", bodyMin,
                new Vector2(150f, 27f), s, false))
            _commanderMapZone = 0;

        Vector2 contentMin = new(bodyMin.X, bodyMin.Y + backH);
        Vector2 contentSize = bodyMax - contentMin;
        float railW = Math.Clamp(contentSize.X * 0.22f, 280f * s, 380f * s);
        float availableW = MathF.Max(200f, contentSize.X - railW - gap);
        float mapW = MathF.Min(availableW,
            contentSize.Y * CommanderMapUiLaw.AuthoredWidth / CommanderMapUiLaw.AuthoredHeight);
        float mapH = mapW * CommanderMapUiLaw.AuthoredHeight / CommanderMapUiLaw.AuthoredWidth;
        Vector2 mapMin = contentMin + new Vector2((availableW - mapW) * 0.5f,
            (contentSize.Y - mapH) * 0.5f);
        DrawCommanderZoneView(dl, zone, playerMap, mapMin, mapW, mapH, s);

        Vector2 panelMin = new(contentMin.X + availableW + gap, contentMin.Y);
        Vector2 panelMax = new(bodyMax.X, bodyMax.Y);
        DrawCommanderIntelPanel(dl, (int)zone.MapId, true, zone, panelMin, panelMax, s, stale: false);
    }

    private static void DrawCommanderOutlinedCenteredText(ImDrawListPtr dl, Vector2 centre,
        string text, float size, uint color)
    {
        Vector2 measured = ImGui.CalcTextSize(text) * (size / MathF.Max(1f, ImGui.GetFontSize()));
        Vector2 at = centre - measured * 0.5f;
        for (int y = -1; y <= 1; y++)
        for (int x = -1; x <= 1; x++)
            if (x != 0 || y != 0)
                dl.AddText(ImGui.GetFont(), size, at + new Vector2(x, y) * 1.5f,
                    0xE0000000, text);
        dl.AddText(ImGui.GetFont(), size, at, color, text);
    }

    private void DrawCommanderZoneView(ImDrawListPtr dl, WorldMapAreaInfo zone, uint playerMap,
        Vector2 mapMin, float mapW, float mapH, float s)
    {
        DrawCommanderMapTiles(dl, zone.Directory, mapMin, mapW, mapH);
        DrawCommanderNoFogOverlays(dl, zone, mapMin, mapW, mapH);
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
        foreach (CommanderUnit unit in _commanderUnits)
        {
            if (!CommanderUnitInZone(unit, zone, out Vector2 f)) continue;
            Vector2 p = CommanderMapUiLaw.Project(mapMin, mapSize, f);
            bool hover = mapHovered && (mouse - p).Length() < 12f * s;
            if (hover) hoveredUnit = unit;
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
                    CommanderShowNotice("Camera travel is limited to your current continent.");
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
            Vector2 f = CommanderMapUiLaw.Unproject(mapMin, mapSize, mouse);
            float worldY = zone.Left + f.X * (zone.Right - zone.Left);
            float worldX = zone.Top + f.Y * (zone.Bottom - zone.Top);
            if (zone.MapId != playerMap)
                CommanderShowNotice("Camera travel is limited to your current continent.");
            else
                CommanderFlyTo(worldX, worldY, CommanderFlyAltitude);   // map stays open: navigation, not commitment
        }

        // Camera diamond inside the zone map too.
        if (zone.MapId == playerMap && _controller is not null)
        {
            Vector2 f = new(zone.X(_controller.Position.Y), zone.Y(_controller.Position.X));
            if (f.X is > 0f and < 1f && f.Y is > 0f and < 1f)
                DrawCommanderDiamond(dl, CommanderMapUiLaw.Project(mapMin, mapSize, f), 7f * s, VanillaGold);
        }
    }

    private static bool CommanderUnitInZone(CommanderUnit unit, WorldMapAreaInfo zone, out Vector2 fraction)
    {
        fraction = new Vector2(zone.X(unit.Pos.Y), zone.Y(unit.Pos.X));
        if (unit.MapId != zone.MapId) return false;
        bool inside = fraction.X is > 0.001f and < 0.999f &&
                      fraction.Y is > 0.001f and < 0.999f;
        return unit.ZoneId == zone.AreaId || inside;
    }

    private void DrawCommanderIntelPanel(ImDrawListPtr dl, int mapId, bool drilled, WorldMapAreaInfo drilledZone,
        Vector2 panelMin, Vector2 panelMax, float s, bool stale)
    {
        float line = 20f * s;
        const int maxVisibleRows = 16;
        float x = panelMin.X + 12f * s;
        float y = panelMin.Y + 10f * s;

        if (!drilled)
        {
            var rows = new List<(uint ZoneId, ZoneIntel Intel, WorldMapAreaInfo Area, string Name)>();
            foreach ((uint zoneId, ZoneIntel intel) in _zoneIntel)
            {
                if (_worldMapAreas?.TryGetArea(zoneId, out WorldMapAreaInfo area) != true ||
                    !CommanderMapUiLaw.ShowPresence((uint)mapId, area.MapId, intel.Bots, intel.Players)) continue;
                string name = _areas?.ZoneName(zoneId) is { Length: > 0 } zoneName ? zoneName : area.Directory;
                rows.Add((zoneId, intel, area, name));
            }
            rows.Sort((a, b) => (b.Intel.Bots + b.Intel.Players).CompareTo(a.Intel.Bots + a.Intel.Players));

            int visible = Math.Min(maxVisibleRows, rows.Count);
            bool overflow = rows.Count > visible;
            float desiredHeight = (64f + Math.Max(1, visible) * 20f + (overflow ? 20f : 0f)) * s;
            Vector2 actualMax = new(panelMax.X, MathF.Min(panelMax.Y, panelMin.Y + MathF.Max(112f * s, desiredHeight)));
            dl.AddRectFilled(panelMin, actualMax, 0xF0201C18, 4f * s);
            dl.AddRect(panelMin, actualMax, 0x40FFFFFF, 4f * s);

            dl.AddText(ImGui.GetFont(), 14f * s, new Vector2(x, y), VanillaGold, "ZONE PRESENCE");
            y += line * 1.25f;
            float botRight = actualMax.X - 58f * s;
            float playerRight = actualMax.X - 12f * s;
            dl.AddText(ImGui.GetFont(), 10f * s, new Vector2(x, y), 0xFF8A8A8A, "ZONE");
            DrawCommanderRightAlignedText(dl, botRight, y, "BOTS", 10f * s, 0xFF8A8A8A);
            DrawCommanderRightAlignedText(dl, playerRight, y, "PLAYERS", 10f * s, 0xFF8A8A8A);
            y += line * 0.8f;

            uint dim = stale ? 0xFF8A8A8Au : 0xFFE6E6E6u;
            for (int i = 0; i < visible; i++)
            {
                var row = rows[i];
                uint zoneId = row.ZoneId;
                ZoneIntel intel = row.Intel;
                string name = row.Name;
                ImGui.SetCursorScreenPos(new Vector2(x - 4f * s, y - 2f * s));
                ImGui.InvisibleButton($"##cmd-active-{zoneId}", new Vector2(actualMax.X - x - 8f * s, line));
                bool rowHover = ImGui.IsItemHovered();
                if (rowHover)
                    dl.AddRectFilled(new Vector2(x - 4f * s, y - 2f * s),
                        new Vector2(actualMax.X - 8f * s, y - 2f * s + line), 0x18FFFFFF, 2f * s);
                string fitted = CommanderFitText(name, botRight - x - 50f * s, 13f * s);
                dl.AddText(ImGui.GetFont(), 13f * s, new Vector2(x, y), rowHover ? 0xFFFFFFFFu : dim, fitted);
                DrawCommanderRightAlignedText(dl, botRight, y, intel.Bots.ToString(), 13f * s, dim);
                DrawCommanderRightAlignedText(dl, playerRight, y, intel.Players.ToString(), 13f * s, dim);
                if (ImGui.IsItemClicked())
                    _commanderMapZone = zoneId;
                y += line;
            }
            if (rows.Count == 0)
            {
                string empty = _zoneIntelAt == 0 ? "Waiting for population data." :
                    "No players or bots reported here.";
                dl.AddText(ImGui.GetFont(), 12f * s, new Vector2(x, y), 0xFF8A8A8A, empty);
                y += line;
            }
            if (overflow)
                dl.AddText(ImGui.GetFont(), 11f * s, new Vector2(x, y), 0xFF8A8A8A,
                    $"+{rows.Count - visible} more populated zones");
        }
        else
        {
            var units = new List<CommanderUnit>();
            foreach (CommanderUnit unit in _commanderUnits)
                if (CommanderUnitInZone(unit, drilledZone, out _)) units.Add(unit);
            int visible = Math.Min(maxVisibleRows, units.Count);
            bool overflow = units.Count > visible;
            float desiredHeight = (58f + Math.Max(1, visible) * 20f + (overflow ? 20f : 0f)) * s;
            Vector2 actualMax = new(panelMax.X, MathF.Min(panelMax.Y, panelMin.Y + MathF.Max(112f * s, desiredHeight)));
            dl.AddRectFilled(panelMin, actualMax, 0xF0201C18, 4f * s);
            dl.AddRect(panelMin, actualMax, 0x40FFFFFF, 4f * s);

            dl.AddText(ImGui.GetFont(), 14f * s, new Vector2(x, y), VanillaGold, "YOUR GROUP HERE");
            y += line * 1.15f;
            string zoneName = _areas?.ZoneName(drilledZone.AreaId) is { Length: > 0 } resolvedZoneName
                ? resolvedZoneName : drilledZone.Directory;
            dl.AddText(ImGui.GetFont(), 10f * s, new Vector2(x, y), 0xFF8A8A8A,
                CommanderFitText(zoneName.ToUpperInvariant(), actualMax.X - x - 12f * s, 10f * s));
            y += line * 0.9f;

            for (int i = 0; i < visible; i++)
            {
                CommanderUnit unit = units[i];
                string name = CommanderUnitName(unit.Guid);
                uint color = unit.Alive ? CommanderClassColor(BotClassName(unit.Guid, name)) : 0xFF707070;
                ImGui.SetCursorScreenPos(new Vector2(x - 4f * s, y - 2f * s));
                ImGui.InvisibleButton($"##cmd-unit-{unit.Guid}", new Vector2(actualMax.X - x - 8f * s, line));
                bool rowHover = ImGui.IsItemHovered();
                if (rowHover)
                    dl.AddRectFilled(new Vector2(x - 4f * s, y - 2f * s),
                        new Vector2(actualMax.X - 8f * s, y - 2f * s + line), 0x18FFFFFF, 2f * s);
                dl.AddCircleFilled(new Vector2(x + 5f * s, y + 7f * s), 4f * s, color);
                dl.AddText(ImGui.GetFont(), 13f * s, new Vector2(x + 14f * s, y),
                    unit.Alive ? (rowHover ? 0xFFFFFFFFu : 0xFFE6E6E6u) : 0xFF8A8A8Au,
                    CommanderFitText(name + (unit.Alive ? "" : " (dead)"), actualMax.X - x - 30f * s, 13f * s));
                if (ImGui.IsItemClicked())
                {
                    if (unit.MapId != (_net?.Player?.Map ?? 0))
                        CommanderShowNotice("Camera travel is limited to your current continent.");
                    else
                    {
                        CommanderFlyTo(unit.Pos.X, unit.Pos.Y, CommanderUnitAltitude);
                        _commanderMapOpen = false;
                    }
                }
                y += line;
            }
            if (units.Count == 0)
                dl.AddText(ImGui.GetFont(), 13f * s, new Vector2(x, y), 0xFF8A8A8A, "None of your units are here.");
            else if (overflow)
                dl.AddText(ImGui.GetFont(), 11f * s, new Vector2(x, y), 0xFF8A8A8A,
                    $"+{units.Count - visible} more units");
        }
    }

    private static void DrawCommanderRightAlignedText(ImDrawListPtr dl, float right, float y,
        string text, float size, uint color)
    {
        float width = ImGui.CalcTextSize(text).X * (size / MathF.Max(1f, ImGui.GetFontSize()));
        dl.AddText(ImGui.GetFont(), size, new Vector2(right - width, y), color, text);
    }

    private static string CommanderFitText(string text, float maxWidth, float size)
    {
        if (maxWidth <= 0f) return string.Empty;
        float scale = size / MathF.Max(1f, ImGui.GetFontSize());
        if (ImGui.CalcTextSize(text).X * scale <= maxWidth) return text;
        const string ellipsis = "...";
        for (int length = text.Length - 1; length > 0; length--)
        {
            string candidate = text[..length] + ellipsis;
            if (ImGui.CalcTextSize(candidate).X * scale <= maxWidth) return candidate;
        }
        return ellipsis;
    }

    // ── small drawing helpers ────────────────────────────────────────────────

    private void DrawCommanderMapTiles(ImDrawListPtr dl, string directory, Vector2 mapMin, float mapW, float mapH)
    {
        // The twelve square textures form a padded 1024x768 atlas. Only its
        // upper-left 1002x668 is authored map content: preserve square pixels and
        // clip the 22px right / 100px bottom padding exactly like the vanilla map.
        const uint tint = 0xFFFFFFFF;
        dl.PushClipRect(mapMin, mapMin + new Vector2(mapW, mapH), true);
        dl.AddRectFilled(mapMin, mapMin + new Vector2(mapW, mapH), 0xFF0E0C0A);
        for (int row = 0; row < CommanderMapUiLaw.TileRows; row++)
        for (int col = 0; col < CommanderMapUiLaw.TileColumns; col++)
        {
            int index = row * CommanderMapUiLaw.TileColumns + col + 1;
            uint texture = _gameplayArt!.Handle($@"Interface\WorldMap\{directory}\{directory}{index}.blp");
            if (texture == 0) continue;
            (Vector2 min, Vector2 max) = CommanderMapUiLaw.TileBounds(mapMin, mapW, row, col);
            dl.AddImage((nint)texture, min, max, Vector2.Zero, Vector2.One, tint);
        }
        dl.PopClipRect();
        dl.AddRect(mapMin, mapMin + new Vector2(mapW, mapH), 0x40FFFFFF);
    }

    private void DrawCommanderMapTilesCropped(ImDrawListPtr dl, string directory,
        CommanderMapUiLaw.ScreenRect viewport, CommanderMapUiLaw.NormalizedRect crop, uint tint)
    {
        float scaleX = viewport.Size.X / (crop.Size.X * CommanderMapUiLaw.AuthoredWidth);
        float scaleY = viewport.Size.Y / (crop.Size.Y * CommanderMapUiLaw.AuthoredHeight);
        float scale = MathF.Min(scaleX, scaleY);
        float authoredW = CommanderMapUiLaw.AuthoredWidth * scale;
        float authoredH = CommanderMapUiLaw.AuthoredHeight * scale;
        Vector2 authoredMin = viewport.Min - new Vector2(
            crop.Min.X * authoredW, crop.Min.Y * authoredH);

        dl.PushClipRect(viewport.Min, viewport.Max, true);
        dl.AddRectFilled(viewport.Min, viewport.Max, 0xFF0E0C0A);
        for (int row = 0; row < CommanderMapUiLaw.TileRows; row++)
        for (int col = 0; col < CommanderMapUiLaw.TileColumns; col++)
        {
            int index = row * CommanderMapUiLaw.TileColumns + col + 1;
            uint texture = _gameplayArt!.Handle(
                $@"Interface\WorldMap\{directory}\{directory}{index}.blp");
            if (texture == 0) continue;
            (Vector2 min, Vector2 max) = CommanderMapUiLaw.TileBounds(
                authoredMin, authoredW, row, col);
            dl.AddImage((nint)texture, min, max, Vector2.Zero, Vector2.One, tint);
        }
        dl.PopClipRect();
        dl.AddRect(viewport.Min, viewport.Max, 0x60FFFFFF);
    }

    private void DrawCommanderNoFogOverlays(ImDrawListPtr dl, WorldMapAreaInfo zone,
        Vector2 mapMin, float mapW, float mapH)
    {
        if (_commanderMapOverlays is null) return;
        float scale = mapW / CommanderMapUiLaw.AuthoredWidth;
        dl.PushClipRect(mapMin, mapMin + new Vector2(mapW, mapH), true);
        foreach (WorldMapOverlayChunk chunk in
                 _commanderMapOverlays.BuildFullRevealChunks(zone.Id, zone.Directory))
        {
            uint texture = _gameplayArt!.Handle(chunk.TexturePath);
            if (texture == 0) continue;
            Vector2 min = mapMin + new Vector2(chunk.OffsetX, chunk.OffsetY) * scale;
            Vector2 max = min + new Vector2(chunk.PixelWidth, chunk.PixelHeight) * scale;
            dl.AddImage((nint)texture, min, max, Vector2.Zero,
                new Vector2(chunk.UMax, chunk.VMax), 0xFFFFFFFF);
        }
        dl.PopClipRect();
    }

    private static (Vector2 Min, Vector2 Max) DrawCommanderPill(ImDrawListPtr dl,
        Vector2 centre, ZoneIntel intel, float s, bool stale, Vector2? avoid,
        Vector2 frameMin, Vector2 frameMax, IReadOnlyList<(Vector2 Min, Vector2 Max)> occupied)
    {
        string text = $"B{intel.Bots}  ·  P{intel.Players}";
        float size = 12f * s;
        Vector2 measured = ImGui.CalcTextSize(text) * (size / MathF.Max(1f, ImGui.GetFontSize()));
        Vector2 pad = new(6f * s, 3f * s);
        Vector2 source = centre;
        Vector2 min = default, max = default;
        float step = measured.Y + pad.Y * 2f + 5f * s;
        for (int attempt = 0; attempt < 7; attempt++)
        {
            int level = (attempt + 1) / 2;
            int direction = attempt == 0 ? 0 : attempt % 2 == 1 ? -1 : 1;
            centre = source + new Vector2(0, direction * level * step);
            min = centre - measured * 0.5f - pad;
            max = centre + measured * 0.5f + pad;

            Vector2 correction = Vector2.Zero;
            float inset = 4f * s;
            if (min.X < frameMin.X + inset) correction.X += frameMin.X + inset - min.X;
            if (max.X > frameMax.X - inset) correction.X -= max.X - (frameMax.X - inset);
            if (min.Y < frameMin.Y + inset) correction.Y += frameMin.Y + inset - min.Y;
            if (max.Y > frameMax.Y - inset) correction.Y -= max.Y - (frameMax.Y - inset);
            centre += correction;
            min += correction;
            max += correction;

            bool blocked = avoid is Vector2 point && CommanderRectsOverlap(min, max,
                point - new Vector2(8f * s), point + new Vector2(8f * s), 0f);
            if (!blocked)
                foreach ((Vector2 usedMin, Vector2 usedMax) in occupied)
                    if (CommanderRectsOverlap(min, max, usedMin, usedMax, 3f * s))
                    { blocked = true; break; }
            if (!blocked) break;
        }
        if (Vector2.DistanceSquared(source, centre) > 1f)
            dl.AddLine(source, centre, 0x70D2B870, 1f * s);
        dl.AddRectFilled(min, max, stale ? 0x90242424u : 0xC8242C30u, (max.Y - min.Y) * 0.5f);
        dl.AddText(ImGui.GetFont(), size, centre - measured * 0.5f,
            stale ? 0xFF9A9A9Au : 0xFFF0E6D2u, text);
        return (min, max);
    }

    private static bool CommanderRectsOverlap(Vector2 aMin, Vector2 aMax,
        Vector2 bMin, Vector2 bMax, float margin) =>
        aMin.X < bMax.X + margin && aMax.X > bMin.X - margin &&
        aMin.Y < bMax.Y + margin && aMax.Y > bMin.Y - margin;

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
        uint fill = selected ? 0xE0303A40u : hover ? 0xD0303A40u : 0xB0282C30u;
        dl.AddRectFilled(min, min + size, fill, 4f * s);
        dl.AddRect(min, min + size, selected ? 0xFF60C0E0u : 0x50FFFFFFu, 4f * s);
        DrawCenteredText(dl, min + size * 0.5f, label, 13f * s,
            selected ? 0xFFFFFFFFu : hover ? 0xFFF0ECE4u : 0xFFD0CAC2u);
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
