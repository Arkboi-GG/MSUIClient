using MSUIClient;
using MSUIClient.Net;

internal static class AiReactionClinicalChecks
{
    public static void Run()
    {
        var writer = new PacketWriter();
        writer.WriteU64(0x1122_3344_5566_7788);
        writer.WriteU32(AiReactionPackets.Hostile);
        Check(AiReactionPackets.Parse(writer.ToArray()) ==
                  new AiReactionPacket(0x1122_3344_5566_7788, 2) &&
              AiReactionPackets.Audible(0) && AiReactionPackets.Audible(2) &&
              !AiReactionPackets.Audible(1),
            "SMSG_AI_REACTION body or audible reaction law drift");
        CheckThrows(() => AiReactionPackets.Parse(new byte[11]));
        CheckThrows(() => AiReactionPackets.Parse(new byte[13]));

        string root = ClientConfig.FindRepoRoot();
        string dispatch = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Net.cs"));
        string flow = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Dev",
            "GameLoop.DevWindow.Overlays.cs"));
        string voices = SourceText.Read(Path.Combine(root, "MSUIClient", "Formats",
            "CreatureVoiceCatalog.cs"));
        Check(dispatch.Contains("case Op.SMSG_AI_REACTION:", StringComparison.Ordinal) &&
              dispatch.Contains("CreatureVoiceCatalog.Load(_mpq)", StringComparison.Ordinal) &&
              flow.Contains("AiReactionPackets.Parse(body)", StringComparison.Ordinal) &&
              flow.Contains("_audioMixer.IsLive(active)", StringComparison.Ordinal) &&
              flow.Contains("voice.AggroSound : voice.AlertSound", StringComparison.Ordinal) &&
              flow.Contains("unit.MountDisplayId", StringComparison.Ordinal) &&
              flow.Contains("category: \"creature\"", StringComparison.Ordinal) &&
              voices.Contains("displayDbc.GetUInt(row, 2)", StringComparison.Ordinal) &&
              voices.Contains("modelDbc.GetUInt(row, 13)", StringComparison.Ordinal),
            "AI-reaction voice catalog, hostile latch, or audio route is unwired");
    }

    private static void CheckThrows(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidDataException("malformed AI-reaction body was accepted");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
