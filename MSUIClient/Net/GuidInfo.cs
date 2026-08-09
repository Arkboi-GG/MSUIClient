namespace MSUIClient.Net;

// 1.12.1 GUID high-part decode, ported from benilla-protocol/src/guid.rs
// (verified there against vmangos ObjectGuid.h). The high 16 bits tag the
// object family; for entry-carrying families the creature/GO template entry
// rides bits 24-47.
public static class GuidInfo
{
    public const ushort HighPlayer = 0x0000;
    public const ushort HighItem = 0x4000;
    public const ushort HighGameObject = 0xF110;
    public const ushort HighTransport = 0xF120;
    public const ushort HighUnit = 0xF130;
    public const ushort HighPet = 0xF140;
    public const ushort HighMoTransport = 0x1FC0;

    public static ushort High(ulong guid) => (ushort)((guid >> 48) & 0xFFFF);

    public static bool IsPlayer(ulong guid) => guid != 0 && High(guid) == HighPlayer;
    public static bool IsItem(ulong guid) => High(guid) == HighItem;
    public static bool IsCreatureOrPet(ulong guid) => High(guid) is HighUnit or HighPet;
    public static bool IsPet(ulong guid) => High(guid) == HighPet;
    public static bool IsGameObject(ulong guid) => High(guid) == HighGameObject;

    /// <summary>The creature/GO template entry, for the families that carry one; null otherwise.</summary>
    public static uint? Entry(ulong guid) => High(guid) switch
    {
        HighMoTransport => (uint)(guid & 0xFFFFFFFF),
        HighUnit or HighGameObject or HighTransport => (uint)((guid >> 24) & 0xFFFFFF),
        _ => null,
    };
}
