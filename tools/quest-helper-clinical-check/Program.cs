using MSUIClient;
using MSUIClient.Engine;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using System.Numerics;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidDataException(message);
}

Check(new GameSettings().AddOns.QuestHelper,
    "Quest Helper must ship enabled behind its independently persisted switch");
Check(GameMenuUiLaw.FrameHeight == 268f &&
      Enumerable.Range(0, 9).Select(GameMenuUiLaw.ButtonTop).SequenceEqual(
          new[] { 26.5f, 48.5f, 70.5f, 92.5f, 114.5f, 136.5f, 158.5f, 180.5f, 217.5f }),
    "native AddOns row overlaps or changed the authored Game Menu spacing");
Check(QuestHelperUiLaw.QuestComplete(1u << 24) &&
      !QuestHelperUiLaw.QuestComplete(0) &&
      QuestHelperUiLaw.ObjectiveProgress(5u << 6, 1, 8) == 5 &&
      QuestHelperUiLaw.ObjectiveEntry(unchecked((uint)-142702)) == 142702 &&
      QuestHelperUiLaw.ObjectiveIsObject(unchecked((uint)-142702)),
    "objective state/entry decoding drift");
Check(QuestHelperUiLaw.LevelAppropriate(10, 8, 10) &&
      !QuestHelperUiLaw.LevelAppropriate(7, 8, 10) &&
      !QuestHelperUiLaw.LevelAppropriate(20, 1, 10) &&
      QuestHelperUiLaw.MatchesMask(178, 2) &&
      !QuestHelperUiLaw.MatchesMask(178, 1),
    "available-quest level/race eligibility drift");

var area = new WorldMapAreaInfo(1, 0, 12, "Elwynn", -100, 100, -200, 200);
Vector3 world = QuestHelperUiLaw.WorldPosition(area, new(12, 25, 75));
Check(world == new Vector3(100, -50, 0), "zone-percent to native world-space mapping drift");

QuestHelperDataCatalog data = QuestHelperDataCatalog.LoadEmbedded();
Check(data.UnitEntryCount > 4_000 && data.ObjectEntryCount > 600 &&
      data.ItemSourceCount > 1_400 && data.TurnInCount > 4_000 &&
      data.AvailableQuestCount > 4_000,
    "embedded Vanilla catalog is unexpectedly incomplete");
Check(data.UnitSpawns(6).Any(spawn => spawn.AreaId == 12) &&
      data.ItemSources(750).Units.Length > 0 &&
      data.TurnInSources(7).Units.Contains(197u),
    "known Kobold/Wolf Meat/quest-7 catalog joins failed");
QuestHelperAvailableQuest quest7 = data.AvailableQuests.Single(row => row.QuestId == 7);
Check(quest7.Sources.Units.Contains(197u) &&
      quest7.Title.Equals("Kobold Camp Cleanup", StringComparison.Ordinal),
    "known available quest start/title join failed");

string root = ClientConfig.FindRepoRoot();
(string markerProof, string markerMetrics) = QuestHelperMarkerVisualCheck.Run(root, Check);
string settingsPath = Path.Combine(Path.GetTempPath(),
    $"msui-quest-helper-settings-{Guid.NewGuid():N}.json");
try
{
    SettingsStore store = SettingsStore.Load(root, settingsPath);
    store.Settings.AddOns.QuestHelper = false;
    store.Save();
    Check(!SettingsStore.Load(root, settingsPath).Settings.AddOns.QuestHelper,
        "Quest Helper enablement did not survive settings save/load");
}
finally
{
    if (File.Exists(settingsPath)) File.Delete(settingsPath);
}

string settings = File.ReadAllText(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
    "GameLoop.Settings.cs"));
string minimap = File.ReadAllText(Path.Combine(root, "MSUIClient", "GameLoop", "Hud",
    "GameLoop.Minimap.cs"));
string worldMap = File.ReadAllText(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
    "GameLoop.WorldMap.cs"));
string helper = File.ReadAllText(Path.Combine(root, "MSUIClient", "GameLoop", "Hud",
    "GameLoop.QuestHelper.cs"));
Check(settings.Contains("GameMenuButtonAddOns", StringComparison.Ordinal) &&
      settings.Contains("Enable Quest Helper", StringComparison.Ordinal) &&
      minimap.Contains("DrawMinimapQuestHelperPins", StringComparison.Ordinal) &&
      worldMap.Contains("DrawWorldMapQuestHelperPins", StringComparison.Ordinal) &&
      helper.Contains("QuestHelperMarkerHandle", StringComparison.Ordinal) &&
      helper.Contains("WorldMapMarkerSize", StringComparison.Ordinal) &&
      helper.Contains("MinimapMarkerSize", StringComparison.Ordinal) &&
      helper.Contains("data.AvailableQuests", StringComparison.Ordinal) &&
      helper.Contains("QuestHelperTooltipLines", StringComparison.Ordinal) &&
      !helper.Contains("if (cluster)", StringComparison.Ordinal) &&
      helper.Contains("if (!QuestHelperEnabled)", StringComparison.Ordinal) &&
      !helper.Contains("DrawMinimapQuestHelperArrow", StringComparison.Ordinal) &&
      !helper.Contains("DrawWorldMapQuestHelperArrow", StringComparison.Ordinal),
    "native AddOns toggle, isolated overlay wiring, or no-navigation contract drift");
string combatHud = File.ReadAllText(Path.Combine(root, "MSUIClient", "GameLoop", "Combat",
    "GameLoop.CombatFeedback.cs"));
int mapBranch = combatHud.IndexOf("if (_worldMapOpen)", StringComparison.Ordinal);
int mapResolve = combatHud.IndexOf("ResolveAndDrawSharedGameTooltip();", mapBranch,
    StringComparison.Ordinal);
Check(mapBranch >= 0 && mapResolve > mapBranch &&
      mapResolve < combatHud.IndexOf("return;", mapBranch, StringComparison.Ordinal),
    "fullscreen world-map tooltips are no longer resolved before its early return");
string tooltipRenderer = File.ReadAllText(Path.Combine(root, "MSUIClient", "GameLoop", "Hud",
    "GameLoop.GameTooltip.Renderer.cs"));
Check(tooltipRenderer.Contains("_worldMapOpen", StringComparison.Ordinal) &&
      tooltipRenderer.Contains("ImGui.GetForegroundDrawList()", StringComparison.Ordinal),
    "world-map GameTooltip art can fall behind the fullscreen foreground map again");

Console.WriteLine($"quest-helper marker proof: {markerProof}");
Console.WriteLine($"quest-helper marker metrics: {markerMetrics}");
Console.WriteLine("quest-helper-clinical-check: PASS");
