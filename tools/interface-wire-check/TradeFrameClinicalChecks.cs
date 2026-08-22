using System.Numerics;
using MSUIClient;
using MSUIClient.Engine.UI;

internal static class TradeFrameClinicalChecks
{
    public static void Run()
    {
        Check(TradeFrameUiLaw.FrameOrigin(1.5f) == new Vector2(0, 156) &&
              TradeFrameUiLaw.FrameSize(1.5f) == new Vector2(576, 768) &&
              TradeFrameUiLaw.PlayerPortrait == new TradeFrameUiLaw.LogicalRect(7, 6, 60, 60) &&
              TradeFrameUiLaw.RecipientPortrait ==
                  new TradeFrameUiLaw.LogicalRect(183, 6, 60, 60) &&
              TradeFrameUiLaw.TradeButton == new TradeFrameUiLaw.LogicalRect(186, 435, 85, 22) &&
              TradeFrameUiLaw.CancelButton == new TradeFrameUiLaw.LogicalRect(274, 435, 77, 22),
            "trade shell/portrait/action geometry drift");

        Check(TradeFrameUiLaw.Slot(player: true, 0) ==
                  new TradeFrameUiLaw.LogicalRect(26, 104, 153, 37) &&
              TradeFrameUiLaw.Slot(player: true, 5).Y == 324 &&
              TradeFrameUiLaw.Slot(player: true, 6).Y == 389 &&
              TradeFrameUiLaw.Slot(player: false, 0).X == 195 &&
              TradeFrameUiLaw.EmptySlot(player: true, 0) ==
                  new TradeFrameUiLaw.LogicalRect(13, 91, 64, 64) &&
              TradeFrameUiLaw.NameFrame(player: false, 6).X == 226 &&
              TradeFrameUiLaw.EnchantLabel == "Will Be Enchanted" &&
              TradeFrameUiLaw.NonTradedLabel == "Will Not Be Traded",
            "trade six-row/separated-enchant-slot geometry drift");

        Check(TradeFrameUiLaw.PlayerHighlight ==
                  new TradeFrameUiLaw.LogicalRect(19, 100, 161, 266) &&
              TradeFrameUiLaw.PlayerEnchantHighlight ==
                  new TradeFrameUiLaw.LogicalRect(19, 370, 161, 61) &&
              TradeFrameUiLaw.CancelClick(accepted: true) ==
                  TradeFrameUiLaw.CancelAction.Unaccept &&
              TradeFrameUiLaw.CancelClick(accepted: false) ==
                  TradeFrameUiLaw.CancelAction.Close &&
              TradeFrameUiLaw.StatusCloses(3) && TradeFrameUiLaw.StatusCloses(21) &&
              !TradeFrameUiLaw.StatusCloses(4) && !TradeFrameUiLaw.StatusCloses(7) &&
              !TradeFrameUiLaw.StatusCloses(13),
            "trade accept/status state law drift");

        TradeInvitationUiLaw.ScreenRect invitation = TradeInvitationUiLaw.PopupRect(
            new Vector2(1024, 768), 1);
        TradeInvitationUiLaw.ScreenRect scaledInvitation = TradeInvitationUiLaw.PopupRect(
            new Vector2(2048, 1536), 2);
        Check(invitation.Min == new Vector2(374, 270) &&
              invitation.Size == new Vector2(276, 120) &&
              scaledInvitation == invitation &&
              TradeInvitationUiLaw.Accept == new Vector2(48, 72) &&
              TradeInvitationUiLaw.Decline == new Vector2(148, 72),
            "incoming-trade modal law drift");

        string root = ClientConfig.FindRepoRoot();
        string runtime = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Trade.cs"));
        Check(runtime.Contains("TradeFrameUiLaw.FrameOrigin", StringComparison.Ordinal) &&
              runtime.Contains("TradeInvitationUiLaw.PopupRect", StringComparison.Ordinal) &&
              runtime.Contains("DrawUnitPortraitImage", StringComparison.Ordinal) &&
              runtime.Contains("DrawTradeAcceptHighlight", StringComparison.Ordinal) &&
              runtime.Contains("row.Count.ToString()", StringComparison.Ordinal) &&
              runtime.Contains("OfferPreparedItemTooltip", StringComparison.Ordinal) &&
              runtime.Contains("_net?.UnacceptTrade()", StringComparison.Ordinal) &&
              !runtime.Contains("BeginVanillaWindow(\"##trade\", new Vector2",
                  StringComparison.Ordinal) &&
              !runtime.Contains("BeginVanillaWindow(\"##trade-invite\", origin",
                  StringComparison.Ordinal),
            "trade production renderer bypasses geometry/protocol law");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
