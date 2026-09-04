using MSUIClient;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

internal static class TradeProtocolClinicalChecks
{
    public static void Run()
    {
        Check((ushort)Op.CMSG_BUSY_TRADE == 0x0118 &&
              (ushort)Op.CMSG_IGNORE_TRADE == 0x0119 &&
              WorldSession.BuildTradeDeclineBody().Length == 0,
            "trade decline opcodes or empty body drift");
        TradePackets.Status begin = TradePackets.ParseStatus(
            Convert.FromHexString("010000008877665544332211"));
        Check(begin.Code == 1 && begin.Partner == 0x1122334455667788,
            "BEGIN_TRADE raw-guid tail drift");
        TradePackets.Status close = TradePackets.ParseStatus(
            Convert.FromHexString("0C000000443322117FDDCCBBAA"));
        Check(close.Code == 12 && close.Result == 0x11223344 &&
              close.ItemLimitCategory == 0xAABBCCDD,
            "CLOSE_WINDOW result/u8/category tail drift");
        TradePackets.Status conjured = TradePackets.ParseStatus(
            Convert.FromHexString("1600000006"));
        Check(conjured.Code == 22 && conjured.Slot == 6 &&
              !TradeFrameUiLaw.StatusCloses(conjured.Code),
            "ONLY_CONJURED slot tail or non-closing behavior drift");
        uint[] refusalCodes = [0, 5, 6, 10, 11, 14, 15, 16, 17, 18, 19, 20, 21, 22];
        Check(refusalCodes.All(code => TradeFrameUiLaw.ErrorForStatus(code) is not null) &&
              TradeFrameUiLaw.ErrorForStatus(1) is null &&
              TradeFrameUiLaw.ErrorForStatus(13) is null,
            "trade refusal status/error vocabulary drift");
        TradeFrameUiLaw.StatusError ignored =
            TradeFrameUiLaw.ErrorForStatus(14)!.Value;
        Check(ignored.GlobalStringKey == "ERR_IGNORING_YOU_S" &&
              TradeFrameUiLaw.FormatStatusError(ignored, "Nico",
                  (_, fallback) => fallback) == "Nico is ignoring you.",
            "trade refusal MPQ string formatting drift");

        var extendedBody = new PacketWriter(444);
        extendedBody.WriteU8(1);
        extendedBody.WriteU32(7);
        extendedBody.WriteU32(7);
        extendedBody.WriteU32(12345);
        extendedBody.WriteU32(7418);
        for (byte i = 0; i < 7; i++)
        {
            extendedBody.WriteU8(i);
            extendedBody.WriteU32(i == 2 ? 6948u : 0u);
            extendedBody.WriteU32(i == 2 ? 2425u : 0u);
            extendedBody.WriteU32(i == 2 ? 3u : 0u);
            extendedBody.WriteU32(0);
            extendedBody.WriteU64(0);
            extendedBody.WriteU32(0);
            extendedBody.WriteU64(0);
            extendedBody.WriteI32(i == 2 ? -1 : 0);
            extendedBody.WriteU32(0);
            extendedBody.WriteU32(0);
            extendedBody.WriteU32(0);
            extendedBody.WriteU32(i == 2 ? 40u : 0u);
            extendedBody.WriteU32(i == 2 ? 31u : 0u);
        }
        TradePackets.Extended extended = TradePackets.ParseExtended(extendedBody.ToArray());
        Check(extended.TheirWindow && extended.Gold == 12345 &&
              extended.EnchantSpellId == 7418 && extended.Slots[2]?.Entry == 6948 &&
              extended.Slots[2]?.Charges == -1 && extended.Slots[2]?.Durability == 31,
            "fixed seven-slot extended trade snapshot drift");
        Check(TradeFrameUiLaw.ItemSlotText("Chest", false, null) ==
                  new TradeFrameUiLaw.SlotText("Chest", null) &&
              TradeFrameUiLaw.ItemSlotText("Sword", true, null) ==
                  new TradeFrameUiLaw.SlotText(TradeFrameUiLaw.NonTradedLabel, 0xffffffff) &&
              TradeFrameUiLaw.ItemSlotText("Sword", true, "Crusader") ==
                  new TradeFrameUiLaw.SlotText("Crusader",
                      TradeFrameUiLaw.ProposedEnchantColor),
            "slot-seven proposed-enchant label/color drift");
        CheckThrows(() => TradePackets.ParseStatus(new byte[3]));
        CheckThrows(() => TradePackets.ParseStatus(new byte[5]));
        CheckThrows(() => TradePackets.ParseExtended(new byte[443]));

        string root = ClientConfig.FindRepoRoot();
        string trade = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Trade.cs"));
        // The status-1 arm was extracted into AnswerTradeRequest, which now also carries the
        // busy reply (dead / not authorable / auction open -> CMSG_BUSY_TRADE). Auto-answer
        // with no client prompt is unchanged; assert the seam in its new shape.
        Check(trade.Contains("AnswerTradeRequest(wire.Partner);", StringComparison.Ordinal) &&
              trade.Contains("_tradePartnerGuid = initiator;", StringComparison.Ordinal) &&
              trade.Contains("_net?.BeginTrade();", StringComparison.Ordinal) &&
              !trade.Contains("_tradeInviteGuid", StringComparison.Ordinal) &&
              !trade.Contains("DrawTradeInvitation", StringComparison.Ordinal),
            "BEGIN_TRADE must auto-answer without a custom client prompt");
        Check(trade.Contains("TradePackets.ParseStatus(body)", StringComparison.Ordinal) &&
              trade.Contains("TradePackets.ParseExtended(body)", StringComparison.Ordinal),
            "production trade decoder is not using the strict packet law");
        Check(trade.Contains("ShowUiError(TradeFrameUiLaw.FormatStatusError", StringComparison.Ordinal),
            "trade refusal feedback is not routed into UIErrorsFrame");
        Check(trade.Contains("wire.EnchantSpellId", StringComparison.Ordinal) &&
              trade.Contains("proposed.Name", StringComparison.Ordinal),
            "extended enchant spell is not retained and resolved for slot seven");
    }

    private static void CheckThrows(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidDataException("malformed trade packet was accepted");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
