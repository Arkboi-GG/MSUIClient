using MSUIClient.Engine.UI;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private void ApplyMountResult(byte[] body, bool mount)
    {
        string opcode = mount ? "SMSG_MOUNTRESULT" : "SMSG_DISMOUNTRESULT";
        uint code = MountResultPackets.ParseCode(body, opcode);
        string? key = MountResultUiLaw.GlobalStringKey(mount, code);
        EmitInterface("mount", mount ? "mount-result" : "dismount-result",
            key is null ? "SILENT" : "REFUSED", ControlledGuid, $"code={code};key={key ?? "none"}");
        if (key is not null)
            ShowUiError(InventoryGlobalString(key,
                mount ? "You can't mount right now." : "You can't dismount right now."));
    }
}
