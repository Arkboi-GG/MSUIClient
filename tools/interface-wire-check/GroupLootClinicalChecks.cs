using System.Numerics;
using MSUIClient;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

internal static class GroupLootClinicalChecks
{
    public static void Run()
    {
        Check((ushort)Op.SMSG_LOOT_ALL_PASSED == 0x029E &&
              (ushort)Op.SMSG_LOOT_ROLL_WON == 0x029F &&
              (ushort)Op.CMSG_LOOT_ROLL == 0x02A0 &&
              (ushort)Op.SMSG_LOOT_START_ROLL == 0x02A1 &&
              (ushort)Op.SMSG_LOOT_ROLL == 0x02A2,
            "group-loot opcode family drift");

        Check(Convert.ToHexString(LootPackets.BuildRollBody(
                  0x123456789ABCDEF0, 2, GroupLootVote.Greed)) ==
              "F0DEBC9A785634120200000002",
            "CMSG_LOOT_ROLL body drift");

        var startBody = new PacketWriter();
        startBody.WriteU64(0xAA); startBody.WriteU32(1); startBody.WriteU32(17182);
        startBody.WriteU32(0); startBody.WriteU32(7); startBody.WriteU32(60000);
        LootStartRoll start = LootPackets.ParseStartRoll(startBody.ToArray());
        Check(start == new LootStartRoll(0xAA, 1, 17182, 7, 60000),
            "SMSG_LOOT_START_ROLL field order drift");
        CheckThrows(() => LootPackets.ParseStartRoll(startBody.ToArray().Concat(
                new byte[] { 0xCC }).ToArray()),
            "SMSG_LOOT_START_ROLL accepted trailing bytes");

        LootRollAnnouncement needVote = ParseRoll(0, 0);
        LootRollAnnouncement passVote = ParseRoll(128, 128);
        LootRollAnnouncement greedVote = ParseRoll(128, 2);
        LootRollAnnouncement dice = ParseRoll(57, 2);
        Check(needVote.Vote == GroupLootVote.Need && passVote.Vote == GroupLootVote.Pass &&
              greedVote.Vote == GroupLootVote.Greed && dice.IsDice && dice.Vote is null,
            "SMSG_LOOT_ROLL vote/dice overload drift");

        var wonBody = new PacketWriter();
        wonBody.WriteU64(0xAA); wonBody.WriteU32(1); wonBody.WriteU32(17182);
        wonBody.WriteU32(0); wonBody.WriteU32(7); wonBody.WriteU64(0xBB);
        wonBody.WriteU8(84); wonBody.WriteU8(1);
        Check(LootPackets.ParseRollWon(wonBody.ToArray()) ==
              new LootRollWon(0xAA, 1, 17182, 7, 0xBB, 84, 1),
            "SMSG_LOOT_ROLL_WON winner placement drift");

        var passedBody = new PacketWriter();
        passedBody.WriteU64(0xAA); passedBody.WriteU32(1); passedBody.WriteU32(17182);
        passedBody.WriteU32(7); passedBody.WriteU32(0);
        Check(LootPackets.ParseAllPassed(passedBody.ToArray()) ==
              new LootAllPassed(0xAA, 1, 17182, 7),
            "SMSG_LOOT_ALL_PASSED swapped property/suffix tail drift");

        var state = new GroupLootRollState();
        for (uint i = 0; i < GroupLootRollState.FrameCount; i++)
            Check(state.Start(new LootStartRoll(0x100 + i, i, 1000 + i, 0, 60000), 10) is not null,
                $"frame {i + 1} was not claimed");
        Check(state.Start(new LootStartRoll(0x999, 9, 9999, 0, 60000), 10) is null,
            "fifth simultaneous roll was not silently unframed");
        Check(state.Start(new LootStartRoll(0x100, 0, 1000, 0, 60000), 10) is null,
            "duplicate wire roll identity was accepted");
        LootRollKey firstKey = new(0x100, 0);
        Check(state.Close(firstKey) && state.Find(firstKey) is null,
            "client-predicted roll close drift");
        GroupLootRollState.ActiveRoll reused = state.Start(
            new LootStartRoll(0x777, 7, 7777, 0, 60000), 10) ??
            throw new InvalidDataException("first hidden frame was not reused");
        Check(state.Frames[0]?.Id == reused.Id && state.RemainingMilliseconds(reused, 80) == 0,
            "first-hidden reuse or saturated timer drift");

        var managed = new UiParentManagedState(true, true, false, false, true, false, false);
        GroupLootFrameUiLaw.ScreenRect first = GroupLootFrameUiLaw.FrameRect(
            new Vector2(1920, 1080), 1, 0, managed);
        GroupLootFrameUiLaw.ScreenRect second = GroupLootFrameUiLaw.FrameRect(
            new Vector2(1920, 1080), 1, 1, managed);
        Check(first.Min == new Vector2(838.5f, 852) && first.Size == new Vector2(243, 84) &&
              second.Min == new Vector2(838.5f, 753) &&
              GroupLootFrameUiLaw.IconMin == new Vector2(18, 18) &&
              GroupLootFrameUiLaw.NeedMin == new Vector2(174, 14) &&
              GroupLootFrameUiLaw.GreedMin == new Vector2(172, 44) &&
              GroupLootFrameUiLaw.TimerMin == new Vector2(16, 57),
            "GroupLootFrame managed stack/child geometry drift");
        GroupLootFrameUiLaw.ScreenRect popup = GroupLootFrameUiLaw.ConfirmRect(
            new Vector2(1920, 1080), 1, 14);
        Check(popup.Min.X == 800 && popup.Min.Y == 128 && popup.Size.X == 320 &&
              GroupLootFrameUiLaw.ConfirmText == "Looting this item will bind it to you." &&
              GroupLootFrameUiLaw.ConfirmButtonMin(1, 14).X == 26 &&
              GroupLootFrameUiLaw.ConfirmButtonMin(2, 14).X == 167,
            "CONFIRM_LOOT_ROLL popup law drift");

        string root = ClientConfig.FindRepoRoot();
        string runtime = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.GroupLoot.cs"));
        string dispatch = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Net.cs"));
        string draw = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Combat",
            "GameLoop.CombatFeedback.cs"));
        Check(runtime.Contains("GroupLootFrameUiLaw.FrameRect", StringComparison.Ordinal) &&
              runtime.Contains("UiParentManagedState", StringComparison.Ordinal) &&
              runtime.Contains("WowSkin.DialogGold", StringComparison.Ordinal) &&
              runtime.Contains("VoteOnGroupLoot", StringComparison.Ordinal) &&
              runtime.Contains("GroupLootFrameUiLaw.ConfirmRect", StringComparison.Ordinal) &&
              dispatch.Contains("case Op.SMSG_LOOT_START_ROLL", StringComparison.Ordinal) &&
              dispatch.Contains("case Op.SMSG_LOOT_ALL_PASSED", StringComparison.Ordinal) &&
              draw.Contains("DrawGroupLootFrames();", StringComparison.Ordinal) &&
              draw.Contains("DrawGroupLootConfirmation();", StringComparison.Ordinal) &&
              !runtime.Contains("SetNextWindowPos(new", StringComparison.Ordinal),
            "group-loot production wiring bypasses packet/frame/modal laws");
    }

    private static LootRollAnnouncement ParseRoll(byte number, byte type)
    {
        var body = new PacketWriter();
        body.WriteU64(0xAA); body.WriteU32(1); body.WriteU64(0xBB); body.WriteU32(17182);
        body.WriteU32(0); body.WriteU32(7); body.WriteU8(number); body.WriteU8(type);
        return LootPackets.ParseRoll(body.ToArray());
    }

    private static void CheckThrows(Action action, string message)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidDataException(message);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
