using MSUIClient;
using MSUIClient.Net;

internal static class AutoRepeatClinicalChecks
{
    public static void Run()
    {
        Check((ushort)Op.CMSG_CANCEL_AUTO_REPEAT_SPELL == 0x026D &&
              (ushort)Op.SMSG_CANCEL_AUTO_REPEAT == 0x029C,
            "auto-repeat opcode values drift");

        string root = ClientConfig.FindRepoRoot();
        string session = SourceText.Read(Path.Combine(root, "MSUIClient", "Net", "WorldSession.cs"));
        string action = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Hud",
            "GameLoop.ActionBars.cs"));
        string cast = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Combat",
            "GameLoop.Casting.cs"));
        string dispatch = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Net.cs"));
        Check(session.Contains("Op.CMSG_CANCEL_AUTO_REPEAT_SPELL, ReadOnlySpan<byte>.Empty",
                  StringComparison.Ordinal) &&
              action.Contains("spell.AutoRepeat && _autoRepeatSpell == spellId", StringComparison.Ordinal) &&
              action.Contains("_net.CancelAutoRepeat()", StringComparison.Ordinal) &&
              cast.Contains("if (_autoRepeatSpell != 0)", StringComparison.Ordinal) &&
              cast.Contains("ApplyAutoRepeatCancelled", StringComparison.Ordinal) &&
              dispatch.Contains("SMSG_CANCEL_AUTO_REPEAT expected empty body", StringComparison.Ordinal) &&
              dispatch.Contains("SpellAutoRepeatCancelledEvent", StringComparison.Ordinal),
            "auto-repeat re-press/Escape sender or strict server-cancel consumer is unwired");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
