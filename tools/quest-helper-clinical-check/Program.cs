using MSUIClient;
using MSUIClient.Engine;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;
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

Vector3 world = QuestHelperUiLaw.WorldPosition(new QuestHelperSpawn(0, 100, -50));
Check(world == new Vector3(100, -50, 0), "native quest spawn coordinate mapping drift");

var liveExports = new Dictionary<string, string>
{
    ["quest_template"] = "entry,patch,QuestLevel,MinLevel,RequiredRaces,RequiredClasses,Title,PrevQuestId,ReqItemId1\n7,10,10,1,0,0,Kobold Camp Cleanup,0,750\n",
    ["creature_questrelation"] = "id,quest,patch_min,patch_max\n197,7,0,10\n",
    ["creature_involvedrelation"] = "id,quest,patch_min,patch_max\n197,7,0,10\n",
    ["creature"] = "guid,id,id2,id3,id4,id5,map,position_x,position_y,patch_min,patch_max\n1,197,0,0,0,0,0,100,-50,0,10\n",
    ["creature_template"] = "entry,patch,loot_id\n197,10,197\n",
    ["creature_loot_template"] = "entry,item,mincountOrRef,patch_min,patch_max\n197,750,1,0,10\n",
};
QuestHelperDataCatalog data = QuestHelperDataClient.ParseExports(liveExports);
Check(data.UnitSpawns(197).Single() == new QuestHelperSpawn(0, 100, -50) &&
      data.ItemSources(750).Units.Contains(197u) &&
      data.TurnInSources(7).Units.Contains(197u),
    "live Quest Helper realm-table joins failed");
QuestHelperAvailableQuest quest7 = data.AvailableQuests.Single(row => row.QuestId == 7);
Check(quest7.Sources.Units.Contains(197u) &&
      quest7.Title.Equals("Kobold Camp Cleanup", StringComparison.Ordinal),
    "live available quest start/title join failed");

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
