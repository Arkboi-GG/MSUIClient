using MSUIClient;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

internal static class StandStateClinicalChecks
{
    public static void Run()
    {
        Check((ushort)Op.CMSG_STANDSTATECHANGE == 0x0101 &&
              WorldSession.BuildStandStateChangeBody(0x1122_3344u).SequenceEqual(
                  new byte[] { 0x44, 0x33, 0x22, 0x11 }),
            "CMSG_STANDSTATECHANGE opcode or exact u32 body drift");

        Check(StandStateUiLaw.ResolveCommand("/stand") == 0 &&
              StandStateUiLaw.ResolveCommand("/sit") == 1 &&
              StandStateUiLaw.ResolveCommand("/sleep") == 3 &&
              StandStateUiLaw.ResolveCommand("/lay") == 3 &&
              StandStateUiLaw.ResolveCommand("/kneel") == 8 &&
              StandStateUiLaw.ResolveCommand("/dance") is null &&
              StandStateUiLaw.IsClientState(0) && StandStateUiLaw.IsClientState(1) &&
              StandStateUiLaw.IsClientState(3) && StandStateUiLaw.IsClientState(8) &&
              !StandStateUiLaw.IsClientState(2),
            "posture alias/state law drift");

        var fields = new ObjectFields();
        fields.SetU32(ObjectFields.UNIT_FIELD_BYTES_1, 0xAABB_CC00u);
        fields.SetUnitStandState(StandStateUiLaw.Kneel);
        Check(fields.UnitStandState == 8 &&
              fields.GetU32(ObjectFields.UNIT_FIELD_BYTES_1) == 0xAABB_CC08u &&
              StandStateUiLaw.LoopAnimation(1) == 97 &&
              StandStateUiLaw.LoopAnimation(3) == 100 &&
              StandStateUiLaw.LoopAnimation(8) == 115,
            "local stand-state commit clobbered sibling descriptor bytes or pose mapping drifted");

        string root = ClientConfig.FindRepoRoot();
        string chat = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Chat.cs"));
        string local = SourceText.Read(Path.Combine(root, "MSUIClient", "Program.cs"));
        string remote = SourceText.Read(Path.Combine(root, "MSUIClient", "World", "Units",
            "PlayerRenderer.cs"));
        Check(chat.Contains("StandStateUiLaw.ResolveCommand(command)", StringComparison.Ordinal) &&
              chat.Contains("TrySetLocalStandState(standState)", StringComparison.Ordinal) &&
              chat.Contains("_net?.StandStateChange(standState)", StringComparison.Ordinal) &&
              chat.Contains("self.Fields.SetUnitStandState(standState)", StringComparison.Ordinal) &&
              local.Contains("StandState = _entities.TryGet(ControlledGuid", StringComparison.Ordinal) &&
              remote.Contains("LoopAnimation(e.Fields.UnitStandState)", StringComparison.Ordinal),
            "posture slash/local-commit/render path is unwired");
        string bindings = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Bindings.cs"));
        Check(bindings.Contains("GameBinding.SitOrStand, \"Sit/Stand\", Key.X",
                  StringComparison.Ordinal) &&
              bindings.Contains("UpdateStandStateBinding(bool typing)",
                  StringComparison.Ordinal) &&
              bindings.Contains("self.Fields.UnitStandState == StandStateUiLaw.Stand",
                  StringComparison.Ordinal) &&
              local.Contains("UpdateStandStateBinding(typing);", StringComparison.Ordinal),
            "SITORSTAND binding no longer uses the shared posture send/local-commit seam");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
