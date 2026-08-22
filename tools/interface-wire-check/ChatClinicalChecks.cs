using MSUIClient.Engine.UI;
using MSUIClient.Net;

internal static class ChatClinicalChecks
{
    public static void Run()
    {
        Check((byte)ChatFrameLaw.MsgType.BgSystemNeutral == 0x52 &&
              (byte)ChatFrameLaw.MsgType.BgSystemAlliance == 0x53 &&
              (byte)ChatFrameLaw.MsgType.BgSystemHorde == 0x54 &&
              (byte)ChatFrameLaw.MsgType.Battleground == 0x5C &&
              (byte)ChatFrameLaw.MsgType.BattlegroundLeader == 0x5D,
            "build-5875 battleground chat bytes drift");
        Check(ChatFrameLaw.Color(ChatFrameLaw.MsgType.BgSystemNeutral) == Rgb(255, 120, 10) &&
              ChatFrameLaw.Color(ChatFrameLaw.MsgType.BgSystemAlliance) == Rgb(0, 174, 239) &&
              ChatFrameLaw.Color(ChatFrameLaw.MsgType.BgSystemHorde) == Rgb(255, 0, 0) &&
              ChatFrameLaw.Color(ChatFrameLaw.MsgType.RaidBossWhisper) == Rgb(179, 179, 179),
            "battleground/raid-boss chat colors drift");
        Check(ChatFrameLaw.MacroExpanded(ChatFrameLaw.MsgType.MonsterSay) &&
              ChatFrameLaw.MacroExpanded(ChatFrameLaw.MsgType.RaidBossEmote) &&
              ChatFrameLaw.MacroExpanded(ChatFrameLaw.MsgType.BgSystemAlliance) &&
              !ChatFrameLaw.MacroExpanded(ChatFrameLaw.MsgType.RaidBossWhisper) &&
              !ChatFrameLaw.MacroExpanded(ChatFrameLaw.MsgType.Say),
            "eight-type wire macro gate drift");
        Check(ChatFrameLaw.ChatFlag(1) == "<AFK>" && ChatFrameLaw.ChatFlag(2) == "<DND>" &&
              ChatFrameLaw.ChatFlag(3) == "<GM>" && ChatFrameLaw.ChatFlag(99) == "",
            "wire chat-tag mapping drift");

        Check(ChatFrameLaw.FormatLine(ChatFrameLaw.MsgType.Party, "Alice", "", "hello", 1) ==
              "[Party] <AFK>|Hplayer:Alice|h[Alice]|h: hello",
            "party label/player-link flag composition drift");
        Check(ChatFrameLaw.FormatLine(ChatFrameLaw.MsgType.BattlegroundLeader, "Alice", "", "go") ==
              "[Battleground Leader] |Hplayer:Alice|h[Alice]|h: go",
            "battleground leader player-link composition drift");
        Check(ChatFrameLaw.FormatLine(ChatFrameLaw.MsgType.Channel, "Alice",
                  "2. Trade - Stormwind City", "WTS") ==
              "[2. Trade] |Hplayer:Alice|h[Alice]|h: WTS",
            "channel zone-tail stripping drift");
        Check(ChatFrameLaw.FormatLine(ChatFrameLaw.MsgType.MonsterEmote, "Thrall", "",
                  "%s raises his weapon.") == "Thrall raises his weapon.",
            "monster-emote embedded sender substitution drift");
        Check(ChatFrameLaw.FormatLine(ChatFrameLaw.MsgType.BgSystemAlliance, "ignored", "",
                  "The Alliance wins!") == "The Alliance wins!",
            "battleground system text must remain verbatim");

        QuestTextMacroLaw.Subject subject = new("Alice", "Human", "Warrior", 1);
        QuestTextMacroLaw.Expansion expanded = QuestTextMacroLaw.ExpandChecked(
            "$N the $C has taken the flag!", subject);
        Check(expanded.Clean && expanded.Text == "Alice the Warrior has taken the flag!",
            "chat NPC-text expansion drift");
        Check(!QuestTextMacroLaw.ExpandChecked("$N", null).Clean,
            "unresolved chat macro must retain the failure bit");

        ChatMessagePacket monster = ChatPackets.ParseMessage(BuildMonsterSay());
        Check(monster.Type == 0x0B && monster.Language == 7 && monster.SenderGuid == 0x1122 &&
              monster.TargetGuid == 0x3344 && monster.SenderName == "Guard" &&
              monster.Text == "$N, halt!" && monster.ChatTag == 0,
            "MONSTER_SAY inline-name/target-guid wire shape drift");
        ChatMessagePacket bg = ChatPackets.ParseMessage(BuildBgSystem());
        Check(bg.Type == 0x53 && bg.SenderGuid == 0xAABB && bg.TargetGuid == 0 &&
              bg.Text == "$N has taken the flag!" && bg.ChatTag == 3,
            "BG_SYSTEM default sender-guid/macro-subject wire shape drift");

        Check((ushort)Op.CMSG_PLAYED_TIME == 0x01CC && (ushort)Op.SMSG_PLAYED_TIME == 0x01CD &&
              (ushort)Op.MSG_RANDOM_ROLL == 0x01FB &&
              (ushort)Op.CMSG_CHAT_IGNORED == 0x0225 &&
              (ushort)Op.SMSG_CHAT_WRONG_FACTION == 0x0219 &&
              (ushort)Op.SMSG_CHAT_PLAYER_NOT_FOUND == 0x02A9,
            "chat-adjacent opcode values drift");
        const ulong ignoredGuid = 0x123456789ABCDEF0;
        Check(ChatPackets.BuildIgnoredBody(ignoredGuid).SequenceEqual(
                  new byte[] { 0xF0, 0xDE, 0xBC, 0x9A, 0x78, 0x56, 0x34, 0x12 }) &&
              ChatFrameLaw.IgnoredSender(true, ChatFrameLaw.MsgType.Whisper, 7) ==
                  ChatFrameLaw.IgnoredSenderAction.DropAndNotify &&
              ChatFrameLaw.IgnoredSender(true, ChatFrameLaw.MsgType.Say, 7) ==
                  ChatFrameLaw.IgnoredSenderAction.Drop &&
              ChatFrameLaw.IgnoredSender(true, ChatFrameLaw.MsgType.Whisper, uint.MaxValue) ==
                  ChatFrameLaw.IgnoredSenderAction.Drop &&
              ChatFrameLaw.IgnoredSender(false, ChatFrameLaw.MsgType.Whisper, 7) ==
                  ChatFrameLaw.IgnoredSenderAction.Continue,
            "ignored-sender suppression, whisper response, or raw GUID body drift");
        var playedWriter = new PacketWriter();
        playedWriter.WriteU32(90_061); playedWriter.WriteU32(3_661);
        ChatPackets.PlayedTime played = ChatPackets.ParsePlayedTime(playedWriter.ToArray());
        Check(played == new ChatPackets.PlayedTime(90_061, 3_661) &&
              ChatFrameLaw.FormatPlayedTime(played.Total, played.Level) ==
              ("Total time played: 1 days, 1 hours, 1 minutes, 1 seconds",
               "Time played this level: 0 days, 1 hours, 1 minutes, 1 seconds"),
            "played-time body or FrameXML breakdown drift");
        var rollWriter = new PacketWriter();
        rollWriter.WriteU32(1); rollWriter.WriteU32(100); rollWriter.WriteU32(73);
        rollWriter.WriteU64(0x1234);
        ChatPackets.RandomRoll roll = ChatPackets.ParseRandomRoll(rollWriter.ToArray());
        Check(roll == new ChatPackets.RandomRoll(1, 100, 73, 0x1234) &&
              ChatFrameLaw.FormatRandomRoll("Alice", roll.Result, roll.Minimum, roll.Maximum) ==
              "Alice rolls 73 (1-100)", "random-roll body or result composition drift");
        var missingWriter = new PacketWriter(); missingWriter.WriteCString("Nobody");
        Check(ChatPackets.ParsePlayerNotFound(missingWriter.ToArray()) == "Nobody",
            "player-not-found cstring body drift");
    }

    private static byte[] BuildMonsterSay()
    {
        var w = new PacketWriter();
        w.WriteU8(0x0B); w.WriteU32(7); w.WriteU64(0x1122);
        WriteLenString(w, "Guard"); w.WriteU64(0x3344);
        WriteLenString(w, "$N, halt!"); w.WriteU8(0);
        return w.ToArray();
    }

    private static byte[] BuildBgSystem()
    {
        var w = new PacketWriter();
        w.WriteU8(0x53); w.WriteU32(0); w.WriteU64(0xAABB);
        WriteLenString(w, "$N has taken the flag!"); w.WriteU8(3);
        return w.ToArray();
    }

    private static void WriteLenString(PacketWriter w, string value)
    {
        int bytes = System.Text.Encoding.UTF8.GetByteCount(value);
        w.WriteU32((uint)bytes + 1);
        w.WriteCString(value);
    }

    private static uint Rgb(int r, int g, int b) =>
        0xFF000000u | ((uint)b << 16) | ((uint)g << 8) | (uint)r;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
