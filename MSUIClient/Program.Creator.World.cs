using System.Numerics;
using ImGuiNET;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// Creator-mode world tools: teleport presets + a click-to-teleport world map,
// and the practice target dummy (a locally synthesized creature - it animates,
// it can be clicked and targeted, it never fights back).
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    // Preset coordinates: capitals + racial starts carry an exact Z (cities sit
    // on WMO floors a terrain sample would miss); outdoor spots ground-snap.
    private static readonly (string Group, string Name, int Map, float X, float Y, float? Z)[]
        CreatorTeleports =
    {
        ("Capitals", "Stormwind", 0, -8842.09f, 626.358f, 94.0867f),
        ("Capitals", "Ironforge", 0, -4981.25f, -881.542f, 501.66f),
        ("Capitals", "Darnassus", 1, 9947.52f, 2482.73f, 1316.21f),
        ("Capitals", "Orgrimmar", 1, 1502.71f, -4415.42f, 22.5573f),
        ("Capitals", "Thunder Bluff", 1, -1277.37f, 124.804f, 131.287f),
        ("Capitals", "Undercity", 0, 1586.48f, 239.562f, -52.149f),
        ("Zones", "Northshire (Elwynn)", 0, -8949.95f, -132.493f, 83.5312f),
        ("Zones", "Goldshire (Elwynn)", 0, -9464f, 62f, null),
        ("Zones", "Crossroads (Barrens)", 1, -443.128f, -2598.87f, null),
        ("Zones", "Ratchet (Barrens)", 1, -956.635f, -3754.71f, null),
        ("Zones", "Sentinel Hill (Westfall)", 0, -10628f, 1037f, null),
        ("Zones", "Lakeshire (Redridge)", 0, -9217f, -2149f, null),
        ("Zones", "Darkshire (Duskwood)", 0, -10515f, -1263f, null),
        ("Zones", "Astranaar (Ashenvale)", 1, 2749f, -404f, null),
        ("Zones", "Auberdine (Darkshore)", 1, 6457f, 484f, null),
        ("Zones", "Gadgetzan (Tanaris)", 1, -7153.9f, -3849.29f, null),
        ("Zones", "Booty Bay (Stranglethorn)", 0, -14297.2f, 530.976f, null),
        ("Zones", "Everlook (Winterspring)", 1, 6721.44f, -4659.09f, null),
        ("Racial starts", "Orc / Troll", 1, -618.518f, -4251.67f, 38.718f),
        ("Racial starts", "Dwarf / Gnome", 0, -6240.32f, 331.033f, 382.758f),
        ("Racial starts", "Night Elf", 1, 10311.3f, 831.463f, 1326.41f),
        ("Racial starts", "Undead", 0, 1676.35f, 1677.45f, 121.67f),
        ("Racial starts", "Tauren", 1, -2917.58f, -257.98f, 52.9968f),
    };

    private int _creatorMapContinent;      // 0 Azeroth, 1 Kalimdor
    private int _creatorMapZoneIndex;      // 0 = whole continent, 1.. = zone list index
    private List<WorldMapAreaInfo>? _creatorMapZones;
    private int _creatorMapZonesFor = -1;

    /// <summary>Teleport with an optional ground-snap: a null Z travels in at 500 and
    /// then snaps to the sampled terrain height once the destination tiles are live.</summary>
    private bool CreatorTeleport(int mapId, float x, float y, float? z, string why)
    {
        EnsureInstanceData();
        bool ok = TravelToMapId(mapId, new Vector3(x, y, z ?? 500f), _controller?.Yaw ?? 0f, why);
        if (ok && z is null && _controller is not null &&
            _terrain?.SampleHeight(x, y) is float ground)
        {
            _controller.Teleport(x, y, ground + 0.5f);
            _window.Camera.Target = _controller.Position;
        }
        if (ok) _creatorPanel = CreatorPanel.None;   // get the menus out of the view
        return ok;
    }

    private float _creatorTeleportPanelW = 400f;   // unscaled; follows content each frame

    private partial void DrawCreatorTeleportPanel()
    {
        if (!BeginCreatorPanel("Teleport", _creatorTeleportPanelW)) return;
        float cs = CreatorUiScale;
        EnsureInstanceData();

        // Both columns size to the widest preset name at the LIVE text scale, and
        // the panel follows, so no dial combination clips a destination. Each
        // group is a vanilla +/- drill-down so the panel stays compact.
        float columnW = CreatorColumnWidth(CreatorTeleports.Select(p => p.Name));
        float rowH = CreatorRowHeight;
        _creatorTeleportPanelW = MathF.Max(400f, (columnW * 2f + 40f * cs) / cs);

        foreach (string group in CreatorTeleports.Select(p => p.Group).Distinct())
        {
            if (!CreatorCategory($"tp-{group}", group, defaultOpen: group == "Capitals")) continue;
            ImGui.Indent(10f * cs);
            int column = 0;
            foreach (var preset in CreatorTeleports.Where(p => p.Group == group))
            {
                if (column++ % 2 == 1) ImGui.SameLine(columnW + 24f * cs);
                if (_skin?.PanelButton(preset.Name, new Vector2(columnW, rowH))
                    ?? ImGui.Button(preset.Name, new Vector2(columnW, rowH)))
                    CreatorTeleport(preset.Map, preset.X, preset.Y, preset.Z, preset.Name);
            }
            ImGui.Unindent(10f * cs);
            ImGui.Spacing();
        }

        if (CreatorCategory("tp-map", "World Map - click anywhere to go there"))
            DrawCreatorMapPicker();

        if (!string.IsNullOrEmpty(_travelStatus))
        {
            ImGui.Spacing();
            ImGui.TextWrapped(_travelStatus);
        }

        EndCreatorPanel();
    }

    /// <summary>Continent/zone tile map with click-to-teleport, driven by WorldMapArea.dbc
    /// bounds (the same inversion the vanilla world map's player marker uses, reversed).</summary>
    private void DrawCreatorMapPicker()
    {
        EnsureWorldMapAreas();
        if (_worldMapAreas is null || _gameplayArt is null)
        {
            ImGui.TextDisabled("WorldMapArea.dbc or map art unavailable.");
            return;
        }

        float cs = CreatorUiScale;
        string[] continents = { "Eastern Kingdoms", "Kalimdor" };
        ImGui.SetNextItemWidth(CreatorComboWidth(continents));
        if (ImGui.Combo("##continent", ref _creatorMapContinent, continents, continents.Length))
            _creatorMapZoneIndex = 0;

        int mapId = _creatorMapContinent == 0 ? 0 : 1;
        if (_creatorMapZonesFor != mapId)
        {
            _creatorMapZonesFor = mapId;
            _creatorMapZones = _worldMapAreas.Areas
                .Where(a => a.MapId == (uint)mapId && a.Directory.Length > 0)
                .OrderBy(a => a.Directory, StringComparer.OrdinalIgnoreCase)
                .ToList();
            _creatorMapZoneIndex = 0;
        }

        var zoneLabels = new string[1 + (_creatorMapZones?.Count ?? 0)];
        zoneLabels[0] = "(whole continent)";
        for (int i = 0; i < (_creatorMapZones?.Count ?? 0); i++)
            zoneLabels[i + 1] = _creatorMapZones![i].Directory;
        ImGui.SameLine();
        ImGui.SetNextItemWidth(CreatorComboWidth(zoneLabels));
        ImGui.Combo("##zone", ref _creatorMapZoneIndex, zoneLabels, zoneLabels.Length);

        WorldMapAreaInfo area;
        if (_creatorMapZoneIndex > 0 && _creatorMapZones is not null)
            area = _creatorMapZones[_creatorMapZoneIndex - 1];
        else if (!_worldMapAreas.TryGetContinent((uint)mapId, out area))
        {
            ImGui.TextDisabled("No continent entry in WorldMapArea.dbc.");
            return;
        }

        // The authored map is 1002x668 inside 12 256px tiles; drawn around half
        // scale, following the UI-scale dial.
        float mapW = 501f * cs, mapH = 334f * cs;
        float tile = 128f * cs;
        Vector2 origin = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();
        dl.PushClipRect(origin, origin + new Vector2(mapW, mapH), true);
        for (int row = 0; row < 3; row++)
        for (int col = 0; col < 4; col++)
        {
            int index = row * 4 + col + 1;
            uint texture = _gameplayArt.Handle(
                $@"Interface\WorldMap\{area.Directory}\{area.Directory}{index}.blp");
            if (texture == 0) continue;
            Vector2 min = origin + new Vector2(col * tile, row * tile);
            dl.AddImage((nint)texture, min, min + new Vector2(tile, tile));
        }
        dl.PopClipRect();

        ImGui.InvisibleButton("##creator-map", new Vector2(mapW, mapH));
        if (ImGui.IsItemHovered())
        {
            Vector2 f = (ImGui.GetMousePos() - origin) / new Vector2(mapW, mapH);
            float worldY = area.Left + f.X * (area.Right - area.Left);
            float worldX = area.Top + f.Y * (area.Bottom - area.Top);
            ImGui.SetTooltip($"({worldX:F0}, {worldY:F0})");
            if (ImGui.IsItemClicked())
                CreatorTeleport((int)area.MapId, worldX, worldY, null,
                    $"map click {area.Directory}");
        }
    }

    // ── Target dummy ─────────────────────────────────────────────────────────

    private const ulong CreatorDummyGuid = 0xF000_0000_0000_0100UL;
    private int _creatorDummyDisplayId = 1141;   // any CreatureDisplayInfo id works here
    private float _creatorDummyScale = 1f;
    private bool _creatorDummySpawned;

    private partial void DrawCreatorTargetPanel()
    {
        if (!BeginCreatorPanel("Target", 320f)) return;

        float cs = CreatorUiScale;
        ImGui.TextWrapped("A practice target: it stands there, animates, and can be " +
                          "clicked and targeted. It never fights back.");
        ImGui.Spacing();
        ImGui.SetNextItemWidth(120f * cs);
        ImGui.InputInt("Display id", ref _creatorDummyDisplayId);
        ImGui.SetNextItemWidth(120f * cs);
        ImGui.SliderFloat("Scale", ref _creatorDummyScale, 0.25f, 4f);

        if (CreatorButton(_creatorDummySpawned ? "Respawn" : "Spawn", 100f * cs))
            SpawnCreatorDummy();
        ImGui.SameLine();
        if (CreatorButton("Despawn", 100f * cs) && _creatorDummySpawned)
        {
            if (_selectionGuid == CreatorDummyGuid) CommitSelection(0, beginAttack: false);
            _entities.RemoveSynthetic(CreatorDummyGuid);
            _creatorDummySpawned = false;
        }

        EndCreatorPanel();
    }

    /// <summary>Place the dummy 8 yards in front of the player, on the ground.</summary>
    private void SpawnCreatorDummy()
    {
        if (_controller is null) return;
        float yaw = _controller.Yaw;
        var forward = new Vector3(MathF.Cos(yaw), MathF.Sin(yaw), 0f);
        Vector3 spot = _controller.Position + forward * 8f;
        if (_terrain?.SampleHeight(spot.X, spot.Y) is float ground) spot.Z = ground;

        _entities.AddSynthetic(new WorldEntity
        {
            Guid = CreatorDummyGuid,
            Type = ObjectTypeId.Unit,
            Fields = ObjectFields.ForSyntheticUnit(_creatorDummyDisplayId, _creatorDummyScale),
            Position = spot,
            Orientation = yaw + MathF.PI,   // face the player
        });
        _creatorDummySpawned = true;
        Console.WriteLine($"[creator] dummy display {_creatorDummyDisplayId} " +
                          $"at ({spot.X:F1}, {spot.Y:F1}, {spot.Z:F1})");
    }

}
