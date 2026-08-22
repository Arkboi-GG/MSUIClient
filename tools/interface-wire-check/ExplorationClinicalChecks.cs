using MSUIClient;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

internal static class ExplorationClinicalChecks
{
    public static void Run()
    {
        ExplorationExperiencePacket packet = ExplorationPackets.Parse(
            Convert.FromHexString("2800000055000000"));
        Check(packet == new ExplorationExperiencePacket(40, 85) &&
              (ushort)Op.SMSG_EXPLORATION_EXPERIENCE == 0x01F8,
            "exploration packet/opcode drift");
        Check(ChatFrameLaw.FormatExplorationToast("Westfall") == "Discovered: Westfall" &&
              ChatFrameLaw.FormatExplorationLine("Westfall", 85) ==
                  "Discovered Westfall: 85 experience gained",
            "exploration feedback copy drift");
        CheckThrows(() => ExplorationPackets.Parse(new byte[7]),
            "truncated exploration packet accepted");
        CheckThrows(() => ExplorationPackets.Parse(new byte[9]),
            "exploration trailing byte accepted");

        string root = ClientConfig.FindRepoRoot();
        string dispatch = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Net.cs"));
        string runtime = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Chat.cs"));
        string catalog = SourceText.Read(Path.Combine(root, "MSUIClient", "Formats",
            "ExplorationSoundCatalog.cs"));
        Check(dispatch.Contains("case Op.SMSG_EXPLORATION_EXPERIENCE", StringComparison.Ordinal) &&
              runtime.Contains("ExplorationPackets.Parse(body)", StringComparison.Ordinal) &&
              runtime.Contains("ShowUiInfo(ChatFrameLaw.FormatExplorationToast(area))",
                  StringComparison.Ordinal) &&
              runtime.Contains("if (packet.Experience > 0)", StringComparison.Ordinal) &&
              runtime.Contains("PlaySpellSound(ControlledGuid, kit, trackHold: false)",
                  StringComparison.Ordinal) &&
              catalog.Contains("uint kit = dbc.GetUInt(row, 3)", StringComparison.Ordinal),
            "exploration feedback/jingle runtime wiring drift");
    }

    private static void CheckThrows(Action action, string message)
    {
        try { action(); }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException) { return; }
        throw new InvalidDataException(message);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
