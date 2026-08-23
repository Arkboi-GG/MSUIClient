using System.Numerics;
using MSUIClient;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

internal static class QuestLogClinicalChecks
{
    public static void Run()
    {
        Check((ushort)Op.CMSG_QUESTLOG_SWAP_QUEST == 0x0193 &&
              WorldSession.BuildQuestLogSwapBody(3, 7).SequenceEqual(new byte[] { 3, 7 }),
            "CMSG_QUESTLOG_SWAP_QUEST opcode or two-u8 body drift");
        Check(QuestFrameUiLaw.WindowOrigin(2f) == new Vector2(0, 208) &&
              QuestFrameUiLaw.WindowSize(2f) == new Vector2(768, 1024) &&
              QuestFrameUiLaw.ClampQuestLogOffset(99, 20) == 14 &&
              QuestFrameUiLaw.ClampQuestLogOffset(-1, 4) == 0 &&
              QuestFrameUiLaw.QuestLogDetailRect == new QuestLogicalRect(19, 175, 300, 261) &&
              QuestFrameUiLaw.ClampQuestLogDetailScroll(999, 500) == 239 &&
              QuestFrameUiLaw.QuestLogCloseRect == new QuestLogicalRect(322, 8, 32, 32) &&
              QuestFrameUiLaw.QuestLogCountPillRect(50) ==
                  new QuestLogicalRect(271, 41, 66, 20) &&
              QuestFrameUiLaw.QuestLogCollapseLeftRect ==
                  new QuestLogicalRect(64, 40, 8, 32) &&
              QuestFrameUiLaw.QuestLogTrackRect == new QuestLogicalRect(129, 44, 20, 20) &&
              QuestFrameUiLaw.QuestLogDetailScrollBarRect ==
                  new QuestLogicalRect(325, 175, 16, 261) &&
              QuestFrameUiLaw.QuestLogDetailThumbY(239, 500) == 404 &&
              QuestFrameUiLaw.QuestLogAbandonRect ==
                  new QuestLogicalRect(17, 437, 125, 21) &&
              QuestFrameUiLaw.QuestLogShareRect == new QuestLogicalRect(141, 437, 123, 21) &&
              QuestFrameUiLaw.QuestLogExitRect == new QuestLogicalRect(264, 437, 77, 21) &&
              QuestFrameUiLaw.QuestLogRowMin(5) == new Vector2(19, 150) &&
              QuestFrameUiLaw.AbandonPopupOrigin(new Vector2(1920, 1080), 2f) ==
                  new Vector2(640, 256) &&
              QuestFrameUiLaw.AbandonPopupAcceptRect == new QuestLogicalRect(26, 36, 128, 20) &&
              QuestFrameUiLaw.AbandonPopupCancelRect == new QuestLogicalRect(167, 36, 128, 20) &&
              QuestFrameUiLaw.QuestWatchTopRight(new Vector2(1920, 1080), 2f) ==
                  new Vector2(1920, 384) &&
              QuestFrameUiLaw.QuestWatchLineTop(0, true, true) == 14 &&
              QuestFrameUiLaw.QuestWatchLineTop(26, true, false) == 31 &&
              QuestFrameUiLaw.QuestWatchLineTop(26, false, false) == 27 &&
              QuestFrameUiLaw.AutoQuestWatchSeconds == 300 &&
              QuestFrameUiLaw.AutoWatchEvictionCandidate(new Dictionary<uint, double>
                  { [7] = 40, [9] = 12, [11] = 80 }) == 9,
            "quest-log modal/list law drift");
        Check(QuestFrameUiLaw.QuestDifficultyColor(20, 25) == new Vector4(1f, .1f, .1f, 1f) &&
              QuestFrameUiLaw.QuestDifficultyColor(20, 23) == new Vector4(1f, .5f, .25f, 1f) &&
              QuestFrameUiLaw.QuestDifficultyColor(20, 20) == new Vector4(1f, 1f, 0f, 1f) &&
              QuestFrameUiLaw.QuestDifficultyColor(20, 17) == new Vector4(.25f, .75f, .25f, 1f) &&
              QuestFrameUiLaw.QuestDifficultyColor(20, 1) == new Vector4(.5f, .5f, .5f, 1f),
            "quest-log difficulty bands drift");
        IReadOnlyList<QuestLogHeaderGroup> groups = QuestFrameUiLaw.GroupQuestLogHeaders(
            ["Westfall", "Quests", "Westfall", "Alchemy"]);
        Check(groups.Count == 3 && groups[0].Header == "Alchemy" &&
              groups[1].Header == "Quests" && groups[2].Header == "Westfall" &&
              groups[2].QuestIndexes.SequenceEqual([0, 2]) &&
              QuestFrameUiLaw.QuestLogFoldIconMin(5) == new Vector2(22, 150) &&
              QuestFrameUiLaw.SecondsToTime(86400) == "24 Hrs " &&
              QuestFrameUiLaw.SecondsToTime(90061) == "1 Day 1 Hr " &&
              QuestFrameUiLaw.SecondsToTime(61) == "1 Min 1 Sec ",
            "quest-log header grouping/fold law drift");

        var writer = new PacketWriter();
        writer.WriteU32(77); writer.WriteU32(2); writer.WriteU32(18);
        writer.WriteI32(-24);
        for (int i = 0; i < 6; i++) writer.WriteU32(0);
        writer.WriteI32(125); writer.WriteU32(0); writer.WriteU32(42);
        writer.WriteU32(0); writer.WriteU32(0);
        writer.WriteU32(6948); writer.WriteU32(1);
        for (int i = 1; i < 4; i++) { writer.WriteU32(0); writer.WriteU32(0); }
        writer.WriteU32(117); writer.WriteU32(5);
        for (int i = 1; i < 6; i++) { writer.WriteU32(0); writer.WriteU32(0); }
        for (int i = 0; i < 4; i++) writer.WriteU32(0);
        writer.WriteCString("A Full Query"); writer.WriteCString("Do the work.");
        writer.WriteCString("Long details."); writer.WriteCString("Done.");
        writer.WriteU32(123); writer.WriteU32(10); writer.WriteU32(0); writer.WriteU32(0);
        writer.WriteU32(0); writer.WriteU32(0); writer.WriteU32(456); writer.WriteU32(4);
        for (int i = 0; i < 8; i++) writer.WriteU32(0);
        writer.WriteCString("Special targets"); writer.WriteCString("");
        writer.WriteCString(""); writer.WriteCString("");
        QuestTemplate query = QuestPackets.ParseQueryResponse(writer.ToArray());
        Check(query.QuestId == 77 && query.Level == 18 && query.ZoneOrSort == -24 &&
              query.Title == "A Full Query" &&
              query.Money == 125 && query.RewardSpell == 42 &&
              query.FixedRewards.SequenceEqual([new QuestRewardItem(6948, 1, 0)]) &&
              query.ChoiceRewards.SequenceEqual([new QuestRewardItem(117, 5, 0)]) &&
              query.ObjectivesText == "Do the work." && query.Details == "Long details." &&
              query.Objectives[0] == new QuestLogObjective(123, 10, 0, 0, "Special targets") &&
              query.Objectives[1] == new QuestLogObjective(0, 0, 456, 4, ""),
            "quest query fixed-count/template/objective decode drift");

        string runtime = SourceText.Read(Path.Combine(ClientConfig.FindRepoRoot(),
            "MSUIClient", "Program.Quest.cs"));
        string session = SourceText.Read(Path.Combine(ClientConfig.FindRepoRoot(),
            "MSUIClient", "Net", "WorldSession.cs"));
        string client = SourceText.Read(Path.Combine(ClientConfig.FindRepoRoot(),
            "MSUIClient", "Net", "NetworkClient.cs"));
        Check(session.Contains("Op.CMSG_QUESTLOG_SWAP_QUEST", StringComparison.Ordinal) &&
              client.Contains("QuestLogSwap(byte firstSlot, byte secondSlot)",
                  StringComparison.Ordinal) &&
              runtime.Contains(
                  "UiPanelFrameOrigin(UiPanelOwnershipRegistry[logMode ? 8 : 7], s)",
                  StringComparison.Ordinal) &&
              runtime.Contains("QuestFrameUiLaw.AbandonPopupOrigin", StringComparison.Ordinal) &&
              runtime.Contains("_questAbandonConfirmation = new(selected, title);",
                  StringComparison.Ordinal) &&
              runtime.Contains("if (AbandonQuest(confirmation.QuestId))",
                  StringComparison.Ordinal) &&
              runtime.Contains("if (ShiftHeld()) HandleQuestLogShiftClick",
                  StringComparison.Ordinal) &&
              runtime.Contains("QuestFrameUiLaw.QuestWatchTopRight", StringComparison.Ordinal) &&
              runtime.Contains("QuestFrameUiLaw.GroupQuestLogHeaders(headers)", StringComparison.Ordinal) &&
              runtime.Contains("QuestFrameUiLaw.QuestLogFoldIconMin(row)", StringComparison.Ordinal) &&
              runtime.Contains("QuestFrameUiLaw.SecondsToTime(secondsLeft.Value)", StringComparison.Ordinal) &&
              runtime.Contains("QuestFrameUiLaw.QuestLogDetailRect", StringComparison.Ordinal) &&
              runtime.Contains("template.ChoiceRewards", StringComparison.Ordinal) &&
              runtime.Contains("template.FixedRewards", StringComparison.Ordinal) &&
              runtime.Contains("template.RewardSpell", StringComparison.Ordinal) &&
              runtime.Contains("AutoWatchQuest(value.QuestId);", StringComparison.Ordinal) &&
              runtime.Contains("ExpireAutomaticQuestWatches();", StringComparison.Ordinal) &&
              runtime.Contains("_questWatches.RemoveAll(id => !now.Contains(id));",
                  StringComparison.Ordinal),
            "quest-log modal/abandon/watch production wiring drift");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
