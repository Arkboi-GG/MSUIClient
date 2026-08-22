using System.Text;
using MSUIClient;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

internal static class ChatChannelClinicalChecks
{
    public static void Run()
    {
        Check((ushort)Op.CMSG_JOIN_CHANNEL == 0x0097 &&
              (ushort)Op.CMSG_LEAVE_CHANNEL == 0x0098 &&
              (ushort)Op.SMSG_CHANNEL_NOTIFY == 0x0099 &&
              (ushort)Op.CMSG_CHANNEL_LIST == 0x009A &&
              (ushort)Op.SMSG_CHANNEL_LIST == 0x009B,
            "build-5875 channel opcode block drift");
        Check(ChannelPackets.BuildJoin("Secret", "hunter2")
                  .SequenceEqual(Encoding.UTF8.GetBytes("Secret\0hunter2\0")) &&
              ChannelPackets.BuildName("General")
                  .SequenceEqual(Encoding.UTF8.GetBytes("General\0")),
            "channel CMSG cstring bodies drift");

        var joined = new PacketWriter();
        joined.WriteU8(ChannelNotice.YouJoined); joined.WriteCString("General - Elwynn Forest");
        joined.WriteU32(0x18); joined.WriteU32(0);
        ChannelNoticePacket join = ChannelPackets.ParseNotice(joined.ToArray());
        Check(join.Notice == ChannelNotice.YouJoined &&
              join.Channel == "General - Elwynn Forest" && join.Flags == 0x18,
            "YOU_JOINED notice shape drift");
        var kicked = new PacketWriter();
        kicked.WriteU8(ChannelNotice.PlayerKicked); kicked.WriteCString("World");
        kicked.WriteU64(0x11); kicked.WriteU64(0x22);
        ChannelNoticePacket kick = ChannelPackets.ParseNotice(kicked.ToArray());
        Check(kick.FirstGuid == 0x11 && kick.SecondGuid == 0x22,
            "PLAYER_KICKED two-guid notice shape drift");
        var listBody = new PacketWriter();
        listBody.WriteCString("World"); listBody.WriteU8(1); listBody.WriteU32(2);
        listBody.WriteU64(0x11); listBody.WriteU8(2);
        listBody.WriteU64(0x22); listBody.WriteU8(4);
        ChannelListPacket list = ChannelPackets.ParseList(listBody.ToArray());
        Check(list.Channel == "World" && list.Flags == 1 && list.Members.Count == 2 &&
              list.Members[1] == (0x22ul, (byte)4), "SMSG_CHANNEL_LIST shape drift");

        var channels = new List<string?>();
        Check(ChatChannelLaw.ClaimSlot(channels, "General - Elwynn Forest") == 1 &&
              ChatChannelLaw.ClaimSlot(channels, "Trade - Stormwind City") == 2 &&
              ChatChannelLaw.ClaimSlot(channels, "LocalDefense - Elwynn Forest") == 3 &&
              ChatChannelLaw.FreeSlot(channels, "Trade - Stormwind City") == 2 &&
              ChatChannelLaw.ClaimSlot(channels, "World") == 2 &&
              ChatChannelLaw.NameOf(channels, 3) == "LocalDefense - Elwynn Forest" &&
              ChatChannelLaw.DisplayName(channels, "World") == "2. World",
            "stable channel slot/hole allocator drift");
        Check(ChatChannelLaw.TryResolveSend(channels, "/2", "hello", out string channel,
                  out string message) && channel == "World" && message == "hello" &&
              ChatChannelLaw.TryResolveSend(channels, "/c", "3 defend", out channel,
                  out message) && channel == "LocalDefense - Elwynn Forest" && message == "defend",
            "numbered /c channel send grammar drift");

        Check(ChatChannelLaw.FormatMember("3. LocalDefense - Elwynn Forest", "Alice", true) ==
                  "[3. LocalDefense] [Alice] joined channel." &&
              ChatChannelLaw.FormatNotice(ChannelNotice.YouJoined,
                  "1. General - Elwynn Forest") ==
                  "Joined Channel: [1. General - Elwynn Forest]" &&
              ChatChannelLaw.FormatNotice(ChannelNotice.ModeChange, "World") is null &&
              ChatChannelLaw.FormatNotice(ChannelNotice.PlayerKicked, "World", "Alice", "Bob") ==
                  "[World] Player Alice kicked by Bob.",
            "channel member/notice GlobalStrings composition drift");

        string root = ClientConfig.FindRepoRoot();
        string chat = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Chat.cs"));
        string net = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Net.cs"));
        Check(chat.Contains("_net?.JoinChannel", StringComparison.Ordinal) &&
              chat.Contains("_net?.LeaveChannel", StringComparison.Ordinal) &&
              chat.Contains("_net?.ChannelList", StringComparison.Ordinal) &&
              chat.Contains("ChatChannelLaw.TryResolveSend", StringComparison.Ordinal) &&
              net.Contains("case Op.SMSG_CHANNEL_NOTIFY", StringComparison.Ordinal) &&
              net.Contains("case Op.SMSG_CHANNEL_LIST", StringComparison.Ordinal),
            "channel slash/send or receive dispatch wiring is absent");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
