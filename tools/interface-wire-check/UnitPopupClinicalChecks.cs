using System.Numerics;
using MSUIClient;
using MSUIClient.Engine.UI;

internal static class UnitPopupClinicalChecks
{
    public static void Run()
    {
        UnitPopupRow[] partyLeaderSelfRows = UnitPopupUiLaw.VisibleRows(UnitPopupWhich.Self,
            inParty: true, isLeader: true, isRaid: false,
            canCooperate: true, unitInParty: true);
        UnitPopupRow[] raidLeaderSelfRows = UnitPopupUiLaw.VisibleRows(UnitPopupWhich.Self,
            inParty: true, isLeader: true, isRaid: true,
            canCooperate: true, unitInParty: true);
        UnitPopupRow[] partyMemberSelfRows = UnitPopupUiLaw.VisibleRows(UnitPopupWhich.Self,
            inParty: true, isLeader: false, isRaid: false,
            canCooperate: true, unitInParty: true);
        Check(partyLeaderSelfRows.SequenceEqual(new[]
              { UnitPopupRow.ConvertToRaid, UnitPopupRow.Leave, UnitPopupRow.Cancel }) &&
              raidLeaderSelfRows.SequenceEqual(new[]
                  { UnitPopupRow.Leave, UnitPopupRow.Cancel }) &&
              partyMemberSelfRows.SequenceEqual(new[]
                  { UnitPopupRow.Leave, UnitPopupRow.Cancel }) &&
              UnitPopupUiLaw.RowText(UnitPopupRow.ConvertToRaid) == "Convert to Raid" &&
              UnitPopupUiLaw.RowEnabled(UnitPopupRow.ConvertToRaid,
                  true, true, false, true, 0f) &&
              !UnitPopupUiLaw.RowEnabled(UnitPopupRow.ConvertToRaid,
                  true, true, true, true, 0f) &&
              !UnitPopupUiLaw.RowEnabled(UnitPopupRow.ConvertToRaid,
                  true, false, false, true, 0f),
            "UnitPopup Convert-to-Raid SELF-row leader/party/raid gating drift");

        Check(UnitPopupUiLaw.CardWidth(20f) == UnitPopupUiLaw.MinCardWidth &&
              UnitPopupUiLaw.CardWidth(135f) == 155f &&
              UnitPopupUiLaw.CardWidth(1000f) == UnitPopupUiLaw.MaxCardWidth &&
              UnitPopupUiLaw.CardWidth(float.NaN) == UnitPopupUiLaw.MinCardWidth &&
              UnitPopupUiLaw.CardHeight(3) == 80f &&
              UnitPopupUiLaw.RowOrigin(0) == new Vector2(5f, 25f) &&
              UnitPopupUiLaw.RowTextOrigin(0) == new Vector2(10f, 28f) &&
              UnitPopupUiLaw.RowSize(120f) == new Vector2(110f, 16f),
            "UnitPopup compact adaptive MENU-mode geometry drift");

        Check(UnitPopupUiLaw.ClampOrigin(new Vector2(790f, 590f),
                  new Vector2(120f, 80f), new Vector2(800f, 600f)) ==
              new Vector2(676f, 516f) &&
              UnitPopupUiLaw.ClampOrigin(new Vector2(-20f, -10f),
                  new Vector2(120f, 80f), new Vector2(800f, 600f)) ==
              new Vector2(4f, 4f) &&
              UnitPopupUiLaw.ClampOrigin(new Vector2(200f, 150f),
                  new Vector2(120f, 80f), new Vector2(800f, 600f)) ==
              new Vector2(200f, 150f),
            "UnitPopup viewport-edge clamping drift");

        string runtime = SourceText.Read(Path.Combine(ClientConfig.FindRepoRoot(),
            "MSUIClient", "Program.UnitPopup.cs"));
        Check(runtime.Contains("case UnitPopupRow.ConvertToRaid:\n" +
                  "                _net?.GroupRaidConvert();", StringComparison.Ordinal),
            "UnitPopup Convert-to-Raid row is not wired to CMSG_GROUP_RAID_CONVERT");
        Check(runtime.Contains(
                  "_skin.DrawBackdrop(dl, origin, origin + physicalSize, WowSkin.Tooltip);",
                  StringComparison.Ordinal) &&
              !runtime.Contains("dl.AddRectFilled(origin, origin + size * s, 0xee080808",
                  StringComparison.Ordinal) &&
              !runtime.Contains("VanillaButton(dl, $\"##unit-popup", StringComparison.Ordinal),
            "UnitPopup regressed from the vanilla MENU-mode backdrop/text rows to the black card");
        Check(runtime.Contains(
                  "_unitPopupAutoCloseAt = now + UnitPopupUiLaw.AutoCloseSeconds;",
                  StringComparison.Ordinal) &&
              runtime.Contains("bool clickedOutside =", StringComparison.Ordinal),
            "UnitPopup hover-away timeout or click-away dismissal wiring drift");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
