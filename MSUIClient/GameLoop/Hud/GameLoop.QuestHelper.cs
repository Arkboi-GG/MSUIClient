using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

/// <summary>
/// Native, map-only quest guidance. This layer owns neither quest state nor map state: it joins
/// the existing authoritative quest log to a read-only location catalog and offers additive pins
/// to the two existing map renderers. Turning it off makes every method below a no-op.
/// </summary>
public sealed partial class GameLoop
{
    private sealed record QuestHelperPin(uint QuestId, string Title, string Detail,
        QuestHelperPinKind Kind, uint AreaId, float XPercent, float YPercent);

    private sealed class QuestHelperCluster
    {
        public Vector2 Center;
        public int Locations;
        public List<QuestHelperPin> Pins { get; } = [];
        public QuestHelperPinKind Kind => Pins.Any(pin => pin.Kind == QuestHelperPinKind.TurnIn)
            ? QuestHelperPinKind.TurnIn
            : Pins.Any(pin => pin.Kind == QuestHelperPinKind.Available)
                ? QuestHelperPinKind.Available : Pins[0].Kind;
    }

    private QuestHelperDataCatalog? _questHelperData;
    private bool _questHelperDataAttempted;
    private List<QuestHelperPin> _questHelperPins = [];
    private double _questHelperPinsRefreshAt;
    private readonly HashSet<uint> _questHelperRewardedThisSession = [];

    private bool QuestHelperEnabled => Settings.AddOns?.QuestHelper == true;

    private QuestHelperDataCatalog? QuestHelperData()
    {
        if (_questHelperDataAttempted) return _questHelperData;
        _questHelperDataAttempted = true;
        try
        {
            _questHelperData = QuestHelperDataCatalog.LoadEmbedded();
            Console.WriteLine("[quest-helper] native data loaded: " +
                $"{_questHelperData.UnitEntryCount} units, " +
                $"{_questHelperData.ObjectEntryCount} objects, " +
                $"{_questHelperData.ItemSourceCount} item sources, " +
                $"{_questHelperData.TurnInCount} turn-ins");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[quest-helper] disabled: {ex.Message}");
        }
        return _questHelperData;
    }

    private IReadOnlyList<QuestHelperPin> ActiveQuestHelperPins()
    {
        if (!QuestHelperEnabled) return [];
        double now = NowSeconds();
        if (now < _questHelperPinsRefreshAt) return _questHelperPins;
        // Quest fields and bag counts do not need frame-rate polling. Keeping the relatively
        // expensive item-source/spawn join below one pass per second prevents the optional
        // overlay from becoming a new minimap-frame cost in spawn-heavy zones.
        _questHelperPinsRefreshAt = now + .75;
        _questHelperPins = [];

        QuestHelperDataCatalog? data = QuestHelperData();
        if (data is null || _net is not { IsInWorld: true }) return _questHelperPins;

        // One verdict per streamed giver ENTRY, built once per refresh. Doing this at draw time
        // would multiply every continent pin by every streamed entity on every frame.
        var liveAvailableByUnitEntry = new Dictionary<uint, bool>();
        foreach (WorldEntity entity in _entities.Entities.Values)
        {
            if (!entity.IsCreature ||
                !_questStatuses.TryGetValue(entity.Guid, out uint status)) continue;
            bool available = status == GiverStatusWire.DialogAvailable;
            liveAvailableByUnitEntry[entity.Entry] =
                liveAvailableByUnitEntry.GetValueOrDefault(entity.Entry) || available;
        }

        var seen = new HashSet<(uint Quest, QuestHelperPinKind Kind, uint Area,
            int X10, int Y10, string Detail)>();

        void AddSpawns(uint questId, string title, string detail, QuestHelperPinKind kind,
            bool objects, uint entry)
        {
            if (kind == QuestHelperPinKind.Available && !objects &&
                liveAvailableByUnitEntry.TryGetValue(entry, out bool available) && !available)
                return;
            IReadOnlyList<QuestHelperSpawn> spawns = objects
                ? data.ObjectSpawns(entry) : data.UnitSpawns(entry);
            foreach (QuestHelperSpawn spawn in spawns)
            {
                var key = (questId, kind, spawn.AreaId,
                    (int)MathF.Round(spawn.XPercent * 10f),
                    (int)MathF.Round(spawn.YPercent * 10f), detail);
                if (!seen.Add(key)) continue;
                _questHelperPins.Add(new(questId, title, detail, kind, spawn.AreaId,
                    spawn.XPercent, spawn.YPercent));
            }
        }

        void AddSources(uint questId, string title, string detail,
            QuestHelperPinKind kind, QuestHelperSources sources)
        {
            foreach (uint entry in sources.Units)
                AddSpawns(questId, title, detail, kind, objects: false, entry);
            foreach (uint entry in sources.Objects)
                AddSpawns(questId, title, detail, kind, objects: true, entry);
        }

        foreach ((_, uint questId, uint counters, _) in MergedOwnQuestLog())
        {
            if (!_questTemplates.TryGetValue(questId, out QuestTemplate? template))
            {
                RequireQuestTemplate(questId);
                continue;
            }

            string title = template.Title.Length > 0 ? template.Title : $"Quest {questId}";
            if (QuestHelperUiLaw.QuestComplete(counters))
            {
                AddSources(questId, title, "Ready to turn in", QuestHelperPinKind.TurnIn,
                    data.TurnInSources(questId));
                continue;
            }

            for (int index = 0; index < template.Objectives.Count && index < 4; index++)
            {
                QuestLogObjective objective = template.Objectives[index];
                if (objective.CreatureOrGo != 0 && objective.RequiredCount > 0)
                {
                    uint current = QuestHelperUiLaw.ObjectiveProgress(
                        counters, index, objective.RequiredCount);
                    if (current < objective.RequiredCount)
                    {
                        bool objects = QuestHelperUiLaw.ObjectiveIsObject(objective.CreatureOrGo);
                        uint entry = QuestHelperUiLaw.ObjectiveEntry(objective.CreatureOrGo);
                        string label = objective.Text.Length > 0 ? objective.Text
                            : objects ? "Quest object"
                            : _creatureNames.GetValueOrDefault(entry, $"Creature {entry}");
                        AddSpawns(questId, title,
                            $"{label}: {current}/{objective.RequiredCount}",
                            objects ? QuestHelperPinKind.Object : QuestHelperPinKind.Kill,
                            objects, entry);
                    }
                }

                // Creature and item objectives are independent even when they share an index.
                if (objective.ItemId != 0 && objective.ItemCount > 0)
                {
                    uint current = Math.Min(CarriedCount(objective.ItemId), objective.ItemCount);
                    if (current < objective.ItemCount)
                    {
                        string label = QuestObjectiveItemLabel(objective.ItemId);
                        AddSources(questId, title,
                            $"{label}: {current}/{objective.ItemCount}",
                            QuestHelperPinKind.Loot, data.ItemSources(objective.ItemId));
                    }
                }
            }
        }

        var activeQuestIds = MergedOwnQuestLog().Select(row => row.QuestId).ToHashSet();
        var knownRewarded = new HashSet<uint>(_questHelperRewardedThisSession);
        foreach (MemberQuestEntry entry in MemberQuestEntries(LocalPlayerGuid))
            if (entry.Rewarded) knownRewarded.Add(entry.QuestId);

        byte playerLevel = _net.Player?.Level ?? 0;
        byte playerRace = _net.Player?.Race ?? 0;
        byte playerClass = _net.Player?.Class ?? 0;
        foreach (QuestHelperAvailableQuest available in data.AvailableQuests)
        {
            if (activeQuestIds.Contains(available.QuestId) ||
                knownRewarded.Contains(available.QuestId) ||
                !QuestHelperUiLaw.LevelAppropriate(
                    playerLevel, available.MinimumLevel, available.Level) ||
                !QuestHelperUiLaw.MatchesMask(available.RaceMask, playerRace) ||
                !QuestHelperUiLaw.MatchesMask(available.ClassMask, playerClass) ||
                available.PreviousQuests.Any(activeQuestIds.Contains))
                continue;
            AddSources(available.QuestId, available.Title, "Available quest",
                QuestHelperPinKind.Available, available.Sources);
        }

        return _questHelperPins;
    }

    private static List<QuestHelperCluster> ClusterQuestHelperPins(
        IEnumerable<(QuestHelperPin Pin, Vector2 Center)> projected, float cellPixels)
    {
        cellPixels = MathF.Max(1f, cellPixels);
        var cells = new Dictionary<(int X, int Y), QuestHelperCluster>();
        foreach ((QuestHelperPin pin, Vector2 center) in projected)
        {
            var key = ((int)MathF.Floor(center.X / cellPixels),
                (int)MathF.Floor(center.Y / cellPixels));
            if (!cells.TryGetValue(key, out QuestHelperCluster? cluster))
            {
                cluster = new QuestHelperCluster();
                cells.Add(key, cluster);
            }
            cluster.Center += center;
            cluster.Locations++;
            if (!cluster.Pins.Any(existing => existing.QuestId == pin.QuestId &&
                    existing.Kind == pin.Kind && existing.Detail == pin.Detail))
                cluster.Pins.Add(pin);
        }
        foreach (QuestHelperCluster cluster in cells.Values)
            cluster.Center /= Math.Max(1, cluster.Locations);
        return [.. cells.Values];
    }

    private void DrawQuestHelperPin(ImDrawListPtr draw, Vector2 center,
        QuestHelperPinKind kind, float size)
    {
        float half = size * .5f;
        Vector2 min = center - new Vector2(half);
        Vector2 max = center + new Vector2(half);
        uint icon = _gameplayArt?.QuestHelperMarkerHandle(kind) ?? 0;
        if (icon != 0)
            draw.AddImage((nint)icon, min, max, Vector2.Zero, Vector2.One);
        else if (kind is QuestHelperPinKind.Available or QuestHelperPinKind.TurnIn)
        {
            // A failed GL upload still keeps the semantic marker. This is deliberately an
            // outlined glyph rather than falling through to the sack silhouette.
            string glyph = kind == QuestHelperPinKind.Available ? "!" : "?";
            float logicalHeight = size * 1.08f;
            float width = GameTextLaw.MeasureWidth(
                FontFace.FrizQt, glyph, logicalHeight, 1f, outline: 2);
            int em = GameTextLaw.EmPixels(logicalHeight, 1f);
            GameTextLaw.Draw(draw, FontFace.FrizQt, glyph, logicalHeight, 1f,
                new(center.X - width * .5f, center.Y - em * .5f),
                QuestHelperUiLaw.Color(kind), outline: 2);
        }
        else
        {
            // Asset absence should never turn the overlay into a square again. This primitive
            // fallback is a tied sack silhouette and stays transparent around its edges.
            uint color = QuestHelperUiLaw.Color(kind);
            draw.AddCircleFilled(center + new Vector2(0f, size * .12f), size * .34f,
                QuestHelperUiLaw.BorderColor, 12);
            draw.AddTriangleFilled(center + new Vector2(-size * .21f, -size * .08f),
                center + new Vector2(size * .21f, -size * .08f),
                center + new Vector2(0f, -size * .40f), QuestHelperUiLaw.BorderColor);
            draw.AddCircleFilled(center + new Vector2(0f, size * .12f), size * .28f,
                color, 12);
            draw.AddTriangleFilled(center + new Vector2(-size * .16f, -size * .08f),
                center + new Vector2(size * .16f, -size * .08f),
                center + new Vector2(0f, -size * .34f), color);
        }
    }

    private static string QuestHelperTooltip(QuestHelperCluster cluster)
    {
        var lines = new List<string>();
        foreach (IGrouping<uint, QuestHelperPin> quest in cluster.Pins.GroupBy(pin => pin.QuestId))
        {
            QuestHelperPin first = quest.First();
            lines.Add(first.Title);
            lines.AddRange(quest.Select(pin => pin.Detail).Distinct().Take(4));
        }
        if (cluster.Locations > 1) lines.Add($"{cluster.Locations} nearby locations");
        return string.Join('\n', lines.Take(10));
    }

    private static GameTooltipLine[] QuestHelperTooltipLines(QuestHelperCluster cluster)
    {
        var lines = new List<GameTooltipLine>();
        foreach (IGrouping<uint, QuestHelperPin> quest in cluster.Pins.GroupBy(pin => pin.QuestId))
        {
            QuestHelperPin first = quest.First();
            lines.Add(new(first.Title, GameTooltipTextTone.Gold));
            foreach (string detail in quest.Select(pin => pin.Detail).Distinct().Take(4))
                lines.Add(new(detail, GameTooltipTextTone.White, Wrap: true));
        }
        if (cluster.Locations > 1)
            lines.Add(new($"{cluster.Locations} nearby locations", GameTooltipTextTone.Normal));
        return [.. lines.Take(10)];
    }

    private void DrawWorldMapQuestHelperPins(ImDrawListPtr draw, bool haveMapArea,
        WorldMapAreaInfo area, Vector2 mapMin, Vector2 mapSize, float scale)
    {
        if (!QuestHelperEnabled || !haveMapArea) return;
        var projected = new List<(QuestHelperPin, Vector2)>();
        foreach (QuestHelperPin pin in ActiveQuestHelperPins())
        {
            Vector2 fraction;
            if (area.AreaId != 0)
            {
                if (pin.AreaId != area.AreaId) continue;
                fraction = new(pin.XPercent / 100f, pin.YPercent / 100f);
            }
            else
            {
                // A continent map has no AreaId. Lift the pin through its zone's authored
                // WorldMapArea bounds into native world space, then project it into the
                // continent bounds. This keeps distant active objectives discoverable without
                // introducing an arrow or route surface.
                if (_worldMapAreas?.TryGetArea(pin.AreaId,
                        out WorldMapAreaInfo pinArea) != true || pinArea.MapId != area.MapId)
                    continue;
                Vector3 world = QuestHelperUiLaw.WorldPosition(pinArea,
                    new(pin.AreaId, pin.XPercent, pin.YPercent));
                if (!DeathFrameUiLaw.TryWorldMapFraction((int)pinArea.MapId, area.MapId,
                        world, area.Left, area.Right, area.Top, area.Bottom, out fraction))
                    continue;
            }
            projected.Add((pin, WorldMapUiLaw.MapPoint(mapMin, mapSize,
                fraction.X, fraction.Y)));
        }
        List<QuestHelperCluster> clusters = ClusterQuestHelperPins(projected,
            QuestHelperUiLaw.WorldMapClusterPixels * scale);
        for (int index = 0; index < clusters.Count; index++)
        {
            QuestHelperCluster cluster = clusters[index];
            float pinSize = QuestHelperUiLaw.WorldMapMarkerSize(cluster.Kind) * scale;
            DrawQuestHelperPin(draw, cluster.Center, cluster.Kind, pinSize);
            Vector2 half = new(pinSize * .5f + 3f * scale);
            if (!ImGui.IsMouseHoveringRect(cluster.Center - half, cluster.Center + half, false))
                continue;

            GameTooltipLine[] lines = QuestHelperTooltipLines(cluster);
            Vector2 size = new(pinSize + 6f * scale);
            WorldMapUiLaw.TooltipSeat seat = WorldMapUiLaw.CorpseTooltipSeat(
                cluster.Center - size * .5f, size, mapMin, mapSize);
            ulong owner = ((ulong)area.AreaId << 32) | (uint)index;
            OfferOwnerAnchoredSharedGameTooltip(new("world-map-quest-helper", owner),
                lines, seat.Anchor, seat.Pivot);
        }
    }

    private MinimapResourceTooltipCandidate? DrawMinimapQuestHelperPins(
        ImDrawListPtr draw, Vector3 playerPosition, Vector2 mapMin, Vector2 mapMax,
        float scale, float? radiusOverride = null)
    {
        if (!QuestHelperEnabled) return null;
        EnsureWorldMapAreas();
        if (_worldMapAreas is null) return null;
        uint areaId = _areas?.ParentZoneId(_minimapAreaId) ?? 0;
        if (areaId == 0) areaId = _net?.Player?.Zone ?? 0;
        if (!_worldMapAreas.TryGetArea(areaId, out WorldMapAreaInfo area)) return null;

        float radiusYards = radiusOverride ?? MinimapUiLaw.OutdoorRadius(_minimapZoom);
        float side = mapMax.X - mapMin.X;
        float pixelsPerYard = side / (radiusYards * 2f);
        float aperture = side * .5f * MinimapUiLaw.LandmarkEdgeRatio;
        Vector2 center = (mapMin + mapMax) * .5f;
        var projected = new List<(QuestHelperPin, Vector2)>();
        foreach (QuestHelperPin pin in ActiveQuestHelperPins().Where(pin => pin.AreaId == areaId))
        {
            Vector3 world = QuestHelperUiLaw.WorldPosition(area,
                new(pin.AreaId, pin.XPercent, pin.YPercent));
            Vector2 dot = center + new Vector2(
                -(world.Y - playerPosition.Y), -(world.X - playerPosition.X)) * pixelsPerYard;
            if (Vector2.DistanceSquared(dot, center) <= aperture * aperture)
                projected.Add((pin, dot));
        }

        List<QuestHelperCluster> clusters = ClusterQuestHelperPins(projected,
            QuestHelperUiLaw.MinimapClusterPixels * scale);
        MinimapResourceTooltipCandidate? hovered = null;
        draw.PushClipRect(mapMin, mapMax, true);
        for (int index = 0; index < clusters.Count; index++)
        {
            QuestHelperCluster cluster = clusters[index];
            float pinSize = QuestHelperUiLaw.MinimapMarkerSize(cluster.Kind) * scale;
            DrawQuestHelperPin(draw, cluster.Center, cluster.Kind, pinSize);
            Vector2 half = new(pinSize * .5f + 2f * scale);
            if (ImGui.IsMouseHoveringRect(cluster.Center - half, cluster.Center + half, false))
            {
                ulong owner = 0x5148_0000_0000_0000UL |
                    ((ulong)areaId << 20) | (uint)index;
                hovered = new(owner, QuestHelperTooltip(cluster),
                    QuestHelperTooltipLines(cluster));
            }
        }
        draw.PopClipRect();
        return hovered;
    }
}
