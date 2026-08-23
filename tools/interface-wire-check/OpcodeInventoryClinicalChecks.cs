using MSUIClient.Net;

internal static class OpcodeInventoryClinicalChecks
{
    public static void Run()
    {
        Check((ushort)Op.MSG_MOVE_WORLDPORT_ACK == 0x00DC &&
              (ushort)Op.CMSG_MOVE_WORLDPORT_ACK == 0x00DC &&
              (ushort)Op.SMSG_CLIENT_CONTROL_UPDATE == 0x0159 &&
              (ushort)Op.CMSG_FAR_SIGHT == 0x027A &&
              (ushort)Op.CMSG_TOGGLE_HELM == 0x02B9 &&
              (ushort)Op.CMSG_TOGGLE_CLOAK == 0x02BA &&
              (ushort)Op.CMSG_SET_ACTIONBAR_TOGGLES == 0x02BF &&
              (ushort)Op.CMSG_MOVE_NOT_ACTIVE_MOVER == 0x02D1,
            "current Benilla opcode inventory tail drift");

        // 2026-08-23 recount: Benilla has 424 names; MSUI contains all of
        // them plus its implemented auction/ticket/SuperUI-specific entries.
        Check(Enum.GetNames<Op>().Length >= 486,
            "opcode table lost names from the current 424-of-424 inventory");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
