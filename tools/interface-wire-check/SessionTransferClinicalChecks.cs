using MSUIClient;
using MSUIClient.Net;

internal static class SessionTransferClinicalChecks
{
    public static void Run()
    {
        TransferPendingPacket ordinary = SessionTransferPackets.ParsePending(
            Convert.FromHexString("01000000"));
        Check(ordinary == new TransferPendingPacket(1, null, null) &&
              !ordinary.RidingTransport,
            "ordinary TRANSFER_PENDING parse drift");

        TransferPendingPacket riding = SessionTransferPackets.ParsePending(
            Convert.FromHexString("010000003412000000000000"));
        Check(riding == new TransferPendingPacket(1, 0x1234, 0) && riding.RidingTransport,
            "transport TRANSFER_PENDING optional block drift");
        Check(SessionTransferPackets.ParseAborted(new byte[] { 2 }) == 2,
            "TRANSFER_ABORTED reason parse drift");
        var timeWriter = new PacketWriter(8);
        timeWriter.WriteU32(0x1234_5678);
        timeWriter.WriteF32(1f / 60f);
        LoginTimeSpeedPacket time = SessionTransferPackets.ParseTimeSpeed(timeWriter.ToArray());
        Check(time.PackedDateTime == 0x1234_5678 && time.Timescale == 1f / 60f,
            "LOGIN_SETTIMESPEED packet parse drift");
        CheckThrows(() => SessionTransferPackets.ParsePending(new byte[8]),
            "malformed TRANSFER_PENDING tail accepted");
        CheckThrows(() => SessionTransferPackets.ParseAborted(new byte[] { 2, 0 }),
            "malformed TRANSFER_ABORTED tail accepted");
        CheckThrows(() => SessionTransferPackets.ParseTimeSpeed(new byte[4]),
            "truncated LOGIN_SETTIMESPEED accepted");

        string root = ClientConfig.FindRepoRoot();
        string dispatch = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Net.cs"));
        string loading = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Loading.cs"));
        Check(dispatch.Contains("SessionTransferPackets.ParsePending(body)", StringComparison.Ordinal) &&
              dispatch.Contains("if (!transfer.RidingTransport)", StringComparison.Ordinal) &&
              dispatch.Contains("ArmEnterWorldCurtain(_gl", StringComparison.Ordinal) &&
              dispatch.Contains("SessionTransferPackets.ParseAborted(body)", StringComparison.Ordinal) &&
              dispatch.Contains("SessionTransferPackets.ParseTimeSpeed(body)", StringComparison.Ordinal) &&
              dispatch.Contains("_worldClock.SetServerTime", StringComparison.Ordinal) &&
              dispatch.Contains("CancelPendingWorldCurtain()", StringComparison.Ordinal) &&
              loading.Contains("private void ArmEnterWorldCurtain", StringComparison.Ordinal) &&
              loading.Contains("private void CancelPendingWorldCurtain", StringComparison.Ordinal),
            "transfer early-curtain/abort wiring drift");
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
