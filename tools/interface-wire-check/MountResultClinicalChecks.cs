using MSUIClient;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

internal static class MountResultClinicalChecks
{
    public static void Run()
    {
        Check(MountResultPackets.ParseCode([10, 0, 0, 0], "SMSG_MOUNTRESULT") == 10,
            "mount result u32 parser drift");
        CheckThrows(() => MountResultPackets.ParseCode([3], "SMSG_DISMOUNTRESULT"));
        CheckThrows(() => MountResultPackets.ParseCode([3, 0, 0, 0, 0], "SMSG_DISMOUNTRESULT"));

        Check(MountResultUiLaw.GlobalStringKey(true, 10) is null &&
              MountResultUiLaw.GlobalStringKey(false, 3) is null &&
              MountResultUiLaw.GlobalStringKey(true, 2) == "ERR_MOUNT_ALREADYMOUNTED" &&
              MountResultUiLaw.GlobalStringKey(true, 8) == "ERR_MOUNT_SHAPESHIFTED" &&
              MountResultUiLaw.GlobalStringKey(false, 1) == "ERR_DISMOUNT_NOTMOUNTED" &&
              MountResultUiLaw.GlobalStringKey(true, 11) is null &&
              MountResultUiLaw.GlobalStringKey(false, 4) is null,
            "mount/dismount GlobalStrings result table drift");

        string root = ClientConfig.FindRepoRoot();
        string dispatch = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Net.cs"));
        string apply = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Combat",
            "GameLoop.MountResults.cs"));
        Check(dispatch.Contains("case Op.SMSG_MOUNTRESULT:", StringComparison.Ordinal) &&
              dispatch.Contains("case Op.SMSG_DISMOUNTRESULT:", StringComparison.Ordinal) &&
              apply.Contains("MountResultUiLaw.GlobalStringKey", StringComparison.Ordinal) &&
              apply.Contains("InventoryGlobalString(key", StringComparison.Ordinal) &&
              apply.Contains("ShowUiError", StringComparison.Ordinal),
            "mount result live dispatch or UIErrorsFrame delivery is unwired");
    }

    private static void CheckThrows(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidDataException("malformed mount-result packet was accepted");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
