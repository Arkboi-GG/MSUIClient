namespace MSUIClient.Engine.UI;

/// <summary>Build-5875 (dis)mount result code to GlobalStrings display law.</summary>
public static class MountResultUiLaw
{
    public static string? GlobalStringKey(bool mount, uint code) => mount
        ? code switch
        {
            0 => "ERR_MOUNT_INVALIDMOUNTEE",
            1 => "ERR_MOUNT_TOOFARAWAY",
            2 => "ERR_MOUNT_ALREADYMOUNTED",
            3 => "ERR_MOUNT_NOTMOUNTABLE",
            4 => "ERR_MOUNT_NOTYOURPET",
            5 => "ERR_MOUNT_OTHER",
            6 => "ERR_MOUNT_LOOTING",
            7 => "ERR_MOUNT_RACECANTMOUNT",
            8 => "ERR_MOUNT_SHAPESHIFTED",
            9 => "ERR_MOUNT_FORCEDDISMOUNT",
            _ => null,
        }
        : code switch
        {
            0 => "ERR_DISMOUNT_NOPET",
            1 => "ERR_DISMOUNT_NOTMOUNTED",
            2 => "ERR_DISMOUNT_NOTYOURPET",
            _ => null,
        };
}
