using MSUIClient.Net;

namespace MSUIClient.Engine.UI;

public static class RealmSelectUiLaw
{
    public const int PageSize = 10;
    public static int LastPage(int count) => Math.Max(0, (count - 1) / PageSize);
    public static string TypeName(uint type) => type switch
    {
        0 or 4 => "Normal", 1 => "PvP", 6 => "RP", 8 => "RP-PvP", _ => "Other"
    };
    public static string Status(RealmInfo realm) => !realm.CanSelect ? "Offline" :
        (realm.Flags & 0x80) != 0 ? "Full" : (realm.Flags & 0x40) != 0 ? "Recommended" :
        (realm.Flags & 0x20) != 0 ? "New Players" : "Online";
}
