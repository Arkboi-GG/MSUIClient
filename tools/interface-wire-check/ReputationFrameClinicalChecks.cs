using System.Numerics;
using MSUIClient;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

internal static class ReputationFrameClinicalChecks
{
    public static void Run()
    {
        Check(ReputationFrameUiLaw.DetailOffset == new Vector2(351, -28) &&
              ReputationFrameUiLaw.DetailSize == new Vector2(212, 203) &&
              ReputationFrameUiLaw.Close ==
                  new ReputationFrameUiLaw.LogicalRect(177, 3, 32, 32) &&
              ReputationFrameUiLaw.Name ==
                  new ReputationFrameUiLaw.LogicalRect(20, 21, 170, 12) &&
              ReputationFrameUiLaw.Description ==
                  new ReputationFrameUiLaw.LogicalRect(20, 35, 170, 92) &&
              ReputationFrameUiLaw.Corner ==
                  new ReputationFrameUiLaw.LogicalRect(174, 7, 32, 32) &&
              ReputationFrameUiLaw.AtWarCheck ==
                  new ReputationFrameUiLaw.LogicalRect(14, 143, 26, 26) &&
              ReputationFrameUiLaw.MainScreenCheck ==
                  new ReputationFrameUiLaw.LogicalRect(14, 166, 26, 26),
            "ReputationDetailFrame authored geometry drift");

        ReputationFrameUiLaw.ScreenRect frame =
            ReputationFrameUiLaw.DetailScreenRect(new Vector2(10, 20), 2);
        ReputationFrameUiLaw.CheckGeometry sword = ReputationFrameUiLaw.Check(
            frame.Min, ReputationFrameUiLaw.AtWarCheck, 2, true);
        Check(frame == new ReputationFrameUiLaw.ScreenRect(
                  new Vector2(712, -36), new Vector2(424, 406)) &&
              sword.Hit == new ReputationFrameUiLaw.ScreenRect(
                  new Vector2(740, 250), new Vector2(52, 52)) &&
              sword.MarkMin == new Vector2(746, 240) &&
              sword.MarkSize == new Vector2(64) &&
              sword.LabelPosition == new Vector2(788, 264),
            "ReputationDetailFrame screen/checkbox projection drift");

        byte flags = ReputationFrameUiLaw.Visible | ReputationFrameUiLaw.AtWar;
        Check(ReputationFrameUiLaw.IsVisible(flags) &&
              ReputationFrameUiLaw.IsAtWar(flags) &&
              !ReputationFrameUiLaw.IsInactive(flags) &&
              ReputationFrameUiLaw.IsHeader(ReputationFrameUiLaw.Header) &&
              ReputationFrameUiLaw.CanToggleAtWar(flags, -3000) &&
              !ReputationFrameUiLaw.CanToggleAtWar(flags, -3001) &&
              !ReputationFrameUiLaw.CanToggleAtWar(
                  (byte)(flags | ReputationFrameUiLaw.PeaceForced), 42000) &&
              ReputationFrameUiLaw.IsInactive(
                  ReputationFrameUiLaw.WithInactive(flags, true)) &&
              !ReputationFrameUiLaw.IsAtWar(
                  ReputationFrameUiLaw.WithAtWar(flags, false)),
            "reputation flag/display law drift");

        Check(ReputationFrameUiLaw.SlotAndFlagBody(0x12345678, true)
                  .SequenceEqual(new byte[] { 0x78, 0x56, 0x34, 0x12, 1 }) &&
              ReputationFrameUiLaw.WatchedBody(-1)
                  .SequenceEqual(new byte[] { 0xff, 0xff, 0xff, 0xff }) &&
              (ushort)Op.CMSG_SET_FACTION_ATWAR == 0x0125 &&
              (ushort)Op.CMSG_SET_FACTION_INACTIVE == 0x0317 &&
              (ushort)Op.CMSG_SET_WATCHED_FACTION == 0x0318 &&
              ObjectFields.PLAYER_FIELD_WATCHED_FACTION_INDEX == 1261,
            "reputation opcode/body/watched-field law drift");

        string root = ClientConfig.FindRepoRoot();
        string runtime = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.CharacterPage.cs"));
        string state = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Reputation.cs"));
        string session = SourceText.Read(Path.Combine(root, "MSUIClient", "Net",
            "WorldSession.cs"));
        string faction = SourceText.Read(Path.Combine(root, "MSUIClient", "Formats",
            "Faction.cs"));
        Check(runtime.Contains("ReputationFrameUiLaw.DetailScreenRect", StringComparison.Ordinal) &&
              runtime.Contains("ImGui.Begin(\"##reputation-detail\"", StringComparison.Ordinal) &&
              runtime.Contains("ReputationFrameUiLaw.AtWarCheck", StringComparison.Ordinal) &&
              runtime.Contains("ReputationFrameUiLaw.InactiveCheck", StringComparison.Ordinal) &&
              runtime.Contains("ReputationFrameUiLaw.MainScreenCheck", StringComparison.Ordinal) &&
              runtime.Contains("SelectReputationDetail(row.Slot)", StringComparison.Ordinal) &&
              runtime.Contains("ReputationFrameUiLaw.IsHeader", StringComparison.Ordinal) &&
              runtime.Contains("ReputationFrameUiLaw.InactiveHeaderKey", StringComparison.Ordinal) &&
              state.Contains("_net.SetFactionAtWar", StringComparison.Ordinal) &&
              state.Contains("_net.SetFactionInactive", StringComparison.Ordinal) &&
              state.Contains("_net.SetWatchedFaction", StringComparison.Ordinal) &&
              session.Contains("ReputationFrameUiLaw.SlotAndFlagBody", StringComparison.Ordinal) &&
              session.Contains("ReputationFrameUiLaw.WatchedBody", StringComparison.Ordinal) &&
              faction.Contains("dbc.GetString(row, 28)", StringComparison.Ordinal),
            "Reputation frame bypasses rule-owned dialog/tree/wire/description law");

        int detailStart = runtime.IndexOf("private void DrawReputationDetail", StringComparison.Ordinal);
        int detailEnd = runtime.IndexOf("private void DrawHonorPage", detailStart,
            StringComparison.Ordinal);
        string detailRuntime = runtime[detailStart..detailEnd];
        Check(detailStart >= 0 && detailEnd > detailStart &&
              detailRuntime.Contains("ReputationFrameUiLaw.DetailScreenRect", StringComparison.Ordinal) &&
              detailRuntime.Contains("ReputationFrameUiLaw.Check", StringComparison.Ordinal) &&
              !detailRuntime.Contains("new Vector2", StringComparison.Ordinal),
            "ReputationDetailFrame renderer owns modal geometry");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
