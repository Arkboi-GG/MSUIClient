using MSUIClient;
using MSUIClient.Net;

internal static class EmoteAnimationClinicalChecks
{
    public static void Run()
    {
        var writer = new PacketWriter();
        writer.WriteU32(3);
        writer.WriteU64(0x1122_3344_5566_7788UL);
        Check(EmotePackets.Parse(writer.ToArray()) ==
                  new EmotePacket(3, 0x1122_3344_5566_7788UL),
            "SMSG_EMOTE u32 + raw-u64 body drift");
        CheckThrows(() => EmotePackets.Parse(new byte[11]));
        CheckThrows(() => EmotePackets.Parse(new byte[13]));

        string root = ClientConfig.FindRepoRoot();
        string catalog = SourceText.Read(Path.Combine(root, "MSUIClient", "Formats",
            "EmoteCatalog.cs"));
        string dispatch = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Net.cs"));
        string apply = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Combat",
            "GameLoop.Emotes.cs"));
        Check(catalog.Contains("dbc.GetUInt(row, 2)", StringComparison.Ordinal) &&
              catalog.Contains("dbc.GetUInt(row, 6)", StringComparison.Ordinal) &&
              dispatch.Contains("case Op.SMSG_EMOTE:", StringComparison.Ordinal) &&
              apply.Contains("unit.Fields.UnitStandState == StandStateUiLaw.Sleep", StringComparison.Ordinal) &&
              apply.Contains("MovementFlags.Swimming", StringComparison.Ordinal) &&
              apply.Contains("_character?.TriggerOneShot", StringComparison.Ordinal) &&
              apply.Contains("_creatures?.TriggerOneShot", StringComparison.Ordinal) &&
              apply.Contains("emote.EventSoundId", StringComparison.Ordinal),
            "Emotes.dbc animation/sound resolution, gate, or renderer route is unwired");
    }

    private static void CheckThrows(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidDataException("malformed SMSG_EMOTE body was accepted");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
