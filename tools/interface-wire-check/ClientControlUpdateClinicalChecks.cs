using MSUIClient;
using MSUIClient.Engine;
using MSUIClient.Net;

internal static class ClientControlUpdateClinicalChecks
{
    public static void Run()
    {
        ClientControlUpdatePacket grant = ClientControlUpdatePackets.Parse(
            Convert.FromHexString("DBB4A21A0C30F101"));
        ClientControlUpdatePacket revoke = ClientControlUpdatePackets.Parse(
            Convert.FromHexString("014500"));
        ClientControlUpdatePacket zero = ClientControlUpdatePackets.Parse(
            Convert.FromHexString("0001"));
        Check(grant == new ClientControlUpdatePacket(0xF130000C1A00A2B4, true) &&
              revoke == new ClientControlUpdatePacket(0x45, false) &&
              zero == new ClientControlUpdatePacket(0, true),
            "SMSG_CLIENT_CONTROL_UPDATE packed-guid/allow byte parser drift");

        ulong self = 0x45;
        Check(ClientControlUpdateLaw.Classify(self, false, self) ==
                  ClientControlUpdateLaw.Verdict.SelfRevoked &&
              ClientControlUpdateLaw.Classify(self, true, self) ==
                  ClientControlUpdateLaw.Verdict.SelfRestored &&
              ClientControlUpdateLaw.Classify(0x99, true, self) ==
                  ClientControlUpdateLaw.Verdict.ForeignGranted &&
              ClientControlUpdateLaw.Classify(0x99, false, self) ==
                  ClientControlUpdateLaw.Verdict.ForeignReleased,
            "client-control two-dimensional verdict drift");

        Check(!ClientControlUpdateLaw.SuiOwnsRouting(
                  freeView: false, ordinaryOwnCharacterState: true) &&
              ClientControlUpdateLaw.SuiOwnsRouting(
                  freeView: true, ordinaryOwnCharacterState: true) &&
              ClientControlUpdateLaw.SuiOwnsRouting(
                  freeView: false, ordinaryOwnCharacterState: false) &&
              ClientControlUpdateLaw.LocksCurrentMover(
                  selfControlLost: true, freeView: false,
                  ordinaryOwnCharacterState: true, controllingSessionCharacter: true) &&
              !ClientControlUpdateLaw.LocksCurrentMover(
                  selfControlLost: true, freeView: true,
                  ordinaryOwnCharacterState: false, controllingSessionCharacter: true) &&
              !ClientControlUpdateLaw.LocksCurrentMover(
                  selfControlLost: true, freeView: false,
                  ordinaryOwnCharacterState: false, controllingSessionCharacter: false) &&
              !ClientControlUpdateLaw.LocksCurrentMover(
                  selfControlLost: false, freeView: false,
                  ordinaryOwnCharacterState: true, controllingSessionCharacter: true),
            "stock body lock escaped its ordinary-own-character ownership boundary");

        string root = ClientConfig.FindRepoRoot();
        string host = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.ClientControlUpdate.cs"));
        string net = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Net.cs"));
        string program = SourceText.Read(Path.Combine(root, "MSUIClient", "Program.cs"));
        Check(host.Contains("_movementSender.ParkForRoot(net, _controller)",
                  StringComparison.Ordinal) &&
              host.Contains("net.SetActiveMover(update.Mover)", StringComparison.Ordinal) &&
              host.Contains("_controlState == ControlState.OwnChar", StringComparison.Ordinal) &&
              host.Contains("if (SuiOwnsClientControl)", StringComparison.Ordinal) &&
              host.Contains("if (!VanillaSelfControlLocksMover) return;",
                  StringComparison.Ordinal) &&
              host.Contains("verdict != ClientControlUpdateLaw.Verdict.SelfRestored",
                  StringComparison.Ordinal) &&
              !host.Contains("_controlState = ControlState.Possessing",
                  StringComparison.Ordinal) &&
              net.Contains("case Op.SMSG_CLIENT_CONTROL_UPDATE:", StringComparison.Ordinal) &&
              program.Contains("ApplyVanillaControlLockout(ref forward, ref strafe, ref turn",
                  StringComparison.Ordinal) &&
              program.Contains("bool vanillaControlLocked = VanillaSelfControlLocksMover",
                  StringComparison.Ordinal) &&
              program.Contains("vanillaControlLocked ? _controller.Yaw",
                  StringComparison.Ordinal) &&
              !program.Contains("_vanillaSelfControlLost", StringComparison.Ordinal),
            "client-control own-body lock/restore or protected SUI boundary drift");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
