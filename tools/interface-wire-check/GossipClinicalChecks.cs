using MSUIClient;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

internal static class GossipClinicalChecks
{
    public static void Run()
    {
        var weighted = new[]
        {
            new NpcTextBlock(0.25f, "Male A", "Female A"),
            new NpcTextBlock(0.75f, "Male B", "Female B"),
        };
        Check(GossipUiLaw.SelectGreeting(weighted, 0, 1.9f) == "Male A" &&
              GossipUiLaw.SelectGreeting(weighted, 0, 1.75f) == "Male A" &&
              GossipUiLaw.SelectGreeting(weighted, 0, 1.1f) == "Male B" &&
              GossipUiLaw.SelectGreeting(weighted, 1, 1.1f) == "Female B" &&
              GossipUiLaw.SelectGreeting(weighted, 2, 1.9f) == "Male A",
            "gossip weighted/gender greeting law drift");
        Check(GossipUiLaw.SelectGreeting(
                  new[] { new NpcTextBlock(0, "Zero first", ""),
                          new NpcTextBlock(0, "Zero second", "Fallback forbidden") },
                  0, 1.5f) == "Zero first" &&
              GossipUiLaw.SelectGreeting(
                  new[] { new NpcTextBlock(1, "Wrong column", "") }, 1, 1.5f) is null,
            "gossip zero-weight/no-cross-gender-fallback drift");
        float roll = GossipUiLaw.GreetingRoll(new Random(7));
        Check(roll >= 1f && roll < 2f, "gossip PRNG roll shape drift");

        var writer = new PacketWriter(512);
        writer.WriteU32(77);
        for (int i = 0; i < 8; i++)
        {
            writer.WriteF32(i == 3 ? 0.75f : 0f);
            writer.WriteCString($"M{i}");
            writer.WriteCString($"F{i}");
            for (int tail = 0; tail < 7; tail++) writer.WriteU32((uint)(i * 10 + tail));
        }
        NpcText parsed = GossipPackets.ParseText(writer.ToArray());
        Check(parsed.TextId == 77 && parsed.Blocks.Count == 8 &&
              parsed.Blocks[3] == new NpcTextBlock(0.75f, "M3", "F3") &&
              parsed.Blocks[7].MaleText == "M7",
            "gossip eight-block packet retention drift");

        Check(GossipUiLaw.OptionIcon(0).EndsWith("GossipGossipIcon") &&
              GossipUiLaw.OptionIcon(1).EndsWith("VendorGossipIcon") &&
              GossipUiLaw.OptionIcon(4).EndsWith("HealerGossipIcon") &&
              GossipUiLaw.OptionIcon(5).EndsWith("BinderGossipIcon") &&
              GossipUiLaw.OptionIcon(9).EndsWith("BattleMasterGossipIcon") &&
              GossipUiLaw.OptionIcon(10).EndsWith("GossipGossipIcon") &&
              GossipUiLaw.OptionIcon(255).EndsWith("GossipGossipIcon"),
            "gossip option icon table drift");
        Check(GossipUiLaw.QuestIcon(3).EndsWith("ActiveQuestIcon") &&
              GossipUiLaw.QuestIcon(4).EndsWith("ActiveQuestIcon") &&
              GossipUiLaw.QuestIcon(0).EndsWith("AvailableQuestIcon") &&
              GossipUiLaw.QuestIcon(5).EndsWith("AvailableQuestIcon"),
            "gossip quest icon law drift");
        Check(GossipUiLaw.Scroll == new GossipLogicalRect(23, 81, 300, 334) &&
              GossipUiLaw.ScrollUp == new GossipLogicalRect(329, 81, 16, 16) &&
              GossipUiLaw.ScrollTrack == new GossipLogicalRect(329, 97, 16, 302) &&
              GossipUiLaw.ScrollDown == new GossipLogicalRect(329, 399, 16, 16) &&
              GossipUiLaw.Goodbye == new GossipLogicalRect(267, 417, 78, 22) &&
              GossipUiLaw.Close == new GossipLogicalRect(326, 15, 32, 32) &&
              GossipUiLaw.RowTop(20) == 131 &&
              GossipUiLaw.RowHeight(10) == 18 &&
              GossipUiLaw.RowHeight(25) == 27 &&
              GossipUiLaw.ContentHeight(100, Enumerable.Repeat(30f, 10).ToArray()) == 450 &&
              GossipUiLaw.MaximumScroll(450) == 116 &&
              GossipUiLaw.WheelScroll(60, 450, 1) == 40 &&
              GossipUiLaw.ThumbY(116, 450) == 383,
            "gossip scroll/window geometry law drift");

        string root = ClientConfig.FindRepoRoot();
        string runtime = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Gossip.cs"));
        Check(runtime.Contains("GossipUiLaw.OptionIcon(option.Icon)", StringComparison.Ordinal) &&
              runtime.Contains("UiPanelFrameOrigin(UiPanelOwnershipRegistry[0], s)",
                  StringComparison.Ordinal) &&
              runtime.Contains("GossipUiLaw.QuestIcon(quest.Icon)", StringComparison.Ordinal) &&
              runtime.Contains("_npcTextRecords.TryGetValue", StringComparison.Ordinal) &&
              runtime.Contains("GossipUiLaw.SelectGreeting", StringComparison.Ordinal) &&
              runtime.Contains("source.Fields.Bytes0.Gender", StringComparison.Ordinal) &&
              runtime.Contains("ImGui.PushClipRect(scrollMin, scrollMax, true)",
                  StringComparison.Ordinal) &&
              runtime.Contains("DrawGossipScrollBar(dl, p, s, contentHeight)",
                  StringComparison.Ordinal) &&
              runtime.Contains("!option.Coded", StringComparison.Ordinal) &&
              runtime.IndexOf("rows.Add((true", StringComparison.Ordinal) <
                  runtime.IndexOf("rows.Add((false", StringComparison.Ordinal),
            "gossip icon/greeting production wiring drift");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
