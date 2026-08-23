using System.Numerics;
using MSUIClient;
using MSUIClient.Engine.UI;

internal static class ZoneTextClinicalChecks
{
    public static void Run()
    {
        var old = new ZoneTextIdentity(12, "Elwynn Forest", "Goldshire", false);
        var outdoor = new ZoneTextIdentity(12, "Elwynn Forest", "Crystal Lake", false);
        var indoor = new ZoneTextIdentity(12, "Lion's Pride Inn", "", true);
        var newArea = new ZoneTextIdentity(1519, "Stormwind City", "Trade District", false);
        Check(ZoneTextUiLaw.Elect(null, old) == ZoneChangeKind.NewArea &&
              ZoneTextUiLaw.Elect(old, outdoor) == ZoneChangeKind.Changed &&
              ZoneTextUiLaw.Elect(old, indoor) == ZoneChangeKind.Indoors &&
              ZoneTextUiLaw.Elect(old, newArea) == ZoneChangeKind.NewArea &&
              ZoneTextUiLaw.Elect(old, old) == ZoneChangeKind.None,
            "zone-text event election drift");
        Check(!ZoneTextUiLaw.ShowZone(old, outdoor, ZoneChangeKind.Changed) &&
              ZoneTextUiLaw.ShowZone(old, indoor, ZoneChangeKind.Indoors) &&
              ZoneTextUiLaw.ShowSubZone(old, outdoor, ZoneChangeKind.Changed) &&
              !ZoneTextUiLaw.ShowSubZone(old, old, ZoneChangeKind.None),
            "zone/subzone cached-text gate drift");
        Check(ZoneTextUiLaw.Alpha(0) == 0 && ZoneTextUiLaw.Alpha(.25) == .5f &&
              ZoneTextUiLaw.Alpha(.5) == 1 && ZoneTextUiLaw.Alpha(1.5) == 1 &&
              ZoneTextUiLaw.Alpha(2.5) == .5f && ZoneTextUiLaw.Alpha(3.5) == 0 &&
              ZoneTextUiLaw.FrameCenter(new Vector2(1920, 1080), 1f) ==
                  new Vector2(960, 504) &&
              ZoneTextUiLaw.ZoneExtraCenter(new Vector2(960, 504), 24, 12) ==
                  new Vector2(960, 522) &&
              ZoneTextUiLaw.SubZoneCenter(new Vector2(960, 504), 24, 12, true) ==
                  new Vector2(960, 534) &&
              ZoneTextUiLaw.SubZoneExtraCenter(new Vector2(960, 534), 12) ==
                  new Vector2(960, 546),
            "zone-text fade/seat law drift");
        MinimapZonePvpInfo arena = MinimapUiLaw.ZonePvp(0x80, 0, 2, 4);
        Check(ZoneTextUiLaw.SubZoneTint(arena) == ZoneTextUiLaw.ArenaTint &&
              ZoneTextUiLaw.HasTerritorySeat(arena) &&
              ZoneTextUiLaw.ZoneTint(new(MinimapZonePvpType.Unknown, "", false)) ==
                  ZoneTextUiLaw.UnheldTint,
            "zone-text PvP tint/seat law drift");

        string root = ClientConfig.FindRepoRoot();
        string runtime = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Hud",
            "GameLoop.ZoneText.cs"));
        string areaWire = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Hud",
            "GameLoop.Minimap.cs"));
        string composition = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Combat",
            "GameLoop.CombatFeedback.cs"));
        Check(runtime.Contains("ZoneTextFont", StringComparison.Ordinal) &&
              runtime.Contains("SubZoneTextFont", StringComparison.Ordinal) &&
              runtime.Contains("ZoneTextUiLaw.SubZoneCenter", StringComparison.Ordinal) &&
              !runtime.Contains("new Vector2", StringComparison.Ordinal) &&
              areaWire.Contains("UpdateZoneTextIdentity", StringComparison.Ordinal) &&
              areaWire.Contains("DefaultRow", StringComparison.Ordinal) &&
              areaWire.Contains("GroupRow", StringComparison.Ordinal) &&
              composition.Contains("DrawZoneTextSplash();", StringComparison.Ordinal),
            "zone-text production wiring drift");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
