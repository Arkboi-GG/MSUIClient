using MSUIClient;
using MSUIClient.Engine.UI;
using MSUIClient.Net;
using System.Numerics;

internal static class ChatClinicalChecks
{
    public static void Run()
    {
        Check(ChatFrameLaw.FrameOrigin(new Vector2(1024, 768)) == new Vector2(32, 563) &&
              ChatFrameLaw.FrameRect == new ChatFrameLaw.LogicalRect(0, 0, 430, 120) &&
              ChatFrameLaw.BackgroundRect ==
                  new ChatFrameLaw.LogicalRect(-2, -3, 434, 129) &&
              ChatFrameLaw.EditBoxRect ==
                  new ChatFrameLaw.LogicalRect(-5, 122, 440, 32),
            "ChatFrame bottom anchor, background, or edit-box geometry drift");
        Check(ChatFrameLaw.ClampFrameOrigin(new Vector2(-100, -100), new Vector2(1024, 768)) ==
                  new Vector2(32, 45) &&
              ChatFrameLaw.ClampFrameOrigin(new Vector2(2000, 2000), new Vector2(1024, 768)) ==
                  new Vector2(592, 611),
            "ChatFrame movable origin no longer stays reachable");
        Check(ChatFrameLaw.MenuButtonRect ==
                  new ChatFrameLaw.LogicalRect(-32, -2, 32, 32) &&
              ChatFrameLaw.ScrollUpButtonRect ==
                  new ChatFrameLaw.LogicalRect(-32, 30, 32, 32) &&
              ChatFrameLaw.ScrollDownButtonRect ==
                  new ChatFrameLaw.LogicalRect(-32, 62, 32, 32) &&
              ChatFrameLaw.ScrollEndButtonRect ==
                  new ChatFrameLaw.LogicalRect(-32, 92, 32, 32),
            "ChatFrame menu/up/down/end anchor-chain geometry drift");
        Check(ChatFrameLaw.BorderRect(0) ==
                  new ChatFrameLaw.LogicalRect(-4, -5, 16, 16) &&
              ChatFrameLaw.BorderRect(3) ==
                  new ChatFrameLaw.LogicalRect(418, 113, 16, 16) &&
              ChatFrameLaw.BorderRect(4) ==
                  new ChatFrameLaw.LogicalRect(12, -5, 406, 16) &&
              ChatFrameLaw.BorderRect(6) ==
                  new ChatFrameLaw.LogicalRect(-4, 11, 16, 102),
            "ChatFrame eight-slice border geometry drift");
        ChatFrameLaw.ScreenRect hover = ChatFrameLaw.HoverScreenRect(new Vector2(32, 563), 2);
        ChatFrameLaw.ScreenLine underline = ChatFrameLaw.LinkUnderline(
            new Vector2(10, 20), 30, 14);
        Check(ChatFrameLaw.FrameScaledSize(2) == new Vector2(860, 240) &&
              hover.Min == new Vector2(54, 1036) && hover.Max == new Vector2(934, 1386) &&
              ChatFrameLaw.MessagePosition(new Vector2(32, 563), 2, 14, 1) ==
                  new Vector2(36, 641) &&
              ChatFrameLaw.LinkHitSize(30, 14) == new Vector2(30, 14) &&
              underline.Start == new Vector2(10, 33) && underline.End == new Vector2(40, 33) &&
              ChatFrameLaw.ControlButtonScaledSize(1.5f) == new Vector2(48),
            "ChatFrame frame/hover/message/link/control geometry drift");

        ChatFrameLaw.TabLayout tab = ChatFrameLaw.TabGeometry(
            new Vector2(32, 563), 0, 20, 2, 10);
        Check(tab.Left == new ChatFrameLaw.LogicalRect(32, 528, 16, 32) &&
              tab.Middle == new ChatFrameLaw.LogicalRect(48, 528, 20, 32) &&
              tab.Right == new ChatFrameLaw.LogicalRect(68, 528, 16, 32) &&
              tab.Hit == new ChatFrameLaw.LogicalRect(32, 528, 52, 32) &&
              tab.Highlight == new ChatFrameLaw.LogicalRect(32, 535, 52, 32) &&
              tab.LabelPosition == new Vector2(102, 1093) && tab.Width == 52 &&
              ChatFrameLaw.TabMiddleUvMin == new Vector2(.25f, 0) &&
              ChatFrameLaw.TabMiddleUvMax == new Vector2(.75f, 1),
            "ChatFrame tab slice/hit/highlight/label geometry drift");

        ChatFrameLaw.EditLayout edit = ChatFrameLaw.EditGeometry(
            new Vector2(32, 563), 2, 14, 40, 16);
        Check(edit.Left == new ChatFrameLaw.LogicalRect(27, 685, 256, 32) &&
              edit.Middle == new ChatFrameLaw.LogicalRect(283, 685, 168, 32) &&
              edit.Right == new ChatFrameLaw.LogicalRect(451, 685, 16, 32) &&
              edit.HeaderPosition == new Vector2(80, 1395) &&
              edit.FramePadding == new Vector2(0, 24) &&
              edit.InputPosition == new Vector2(124, 1370) &&
              edit.InputSize == new Vector2(778, 64),
            "ChatFrame edit-box slice/header/input geometry drift");

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
        Check(ChatFrameLaw.PageUpOffset(3, 8) == 11 &&
              ChatFrameLaw.PageDownOffset(11, 8) == 3 &&
              ChatFrameLaw.PageDownOffset(3, 8) == 0,
            "chat page-up/page-down offset law drift");
        string root = ClientConfig.FindRepoRoot();
        string chatSource = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Chat.cs"));
        Check(chatSource.Contains("ChatFrameLaw.FrameOrigin(logicalDisplay)",
                  StringComparison.Ordinal) &&
              chatSource.Contains("ChatFrameLaw.MenuButtonRect.Min", StringComparison.Ordinal) &&
              chatSource.Contains("ChatFrameLaw.ScrollEndButtonRect.Min",
                  StringComparison.Ordinal) &&
              chatSource.Contains("ChatFrameLaw.BorderRect(slice)", StringComparison.Ordinal) &&
              chatSource.Contains("ChatFrameLaw.TabGeometry", StringComparison.Ordinal) &&
              chatSource.Contains("ChatFrameLaw.EditGeometry", StringComparison.Ordinal) &&
              chatSource.Contains("DrawChatMover(ref root", StringComparison.Ordinal) &&
              chatSource.Contains("Settings.HudLayout.ChatOffset", StringComparison.Ordinal) &&
              chatSource.Contains("ChatMenuUiLaw.CardScaledSize", StringComparison.Ordinal) &&
              !chatSource.Contains("new Vector2(", StringComparison.Ordinal) &&
              !chatSource.Contains("logicalDisplay.Y - (95f", StringComparison.Ordinal) &&
              !chatSource.Contains("new Vector2(-32, 84)", StringComparison.Ordinal),
            "ChatFrame renderer bypassed rule-owned origin/control/border geometry");
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
