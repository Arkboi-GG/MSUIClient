namespace MSUIClient.Net;

/// <summary>
/// One row of the stablemaster list (MSG_LIST_STABLED_PETS response). Slot 1 is the
/// active pet; slots 2+ are stabled. Loyalty is the vanilla pet loyalty level.
/// </summary>
/// <param name="PetNumber">The pet's unique number (its id for stable/unstable/swap).</param>
/// <param name="Entry">Creature-template entry (species).</param>
/// <param name="Level">Pet level.</param>
/// <param name="Name">The pet's given name.</param>
/// <param name="Loyalty">Loyalty level (1-6).</param>
/// <param name="Slot">1 = active pet, 2+ = stable slot.</param>
public readonly record struct StabledPet(
    uint PetNumber, uint Entry, uint Level, string Name, uint Loyalty, byte Slot)
{
    /// <summary>The currently-summoned/active pet occupies client slot 1.</summary>
    public bool IsActive => Slot == 1;
}

/// <summary>
/// A stablemaster's view for this character: the active pet, the stabled pets, and
/// how many stable slots are purchased. Carries the stablemaster GUID so every
/// follow-up action (stable/unstable/swap/buy) can address the same NPC.
/// </summary>
public sealed record StableList(ulong NpcGuid, byte StableSlots, StabledPet[] Pets)
{
    public StabledPet? Active
    {
        get
        {
            foreach (StabledPet p in Pets) if (p.IsActive) return p;
            return null;
        }
    }

    public IEnumerable<StabledPet> Stabled => Pets.Where(p => !p.IsActive);
}

/// <summary>
/// Pet stables wire (spec P3). Parses the stablemaster list (MSG_LIST_STABLED_PETS
/// response) and builds the request/action bodies. Every action addresses the
/// stablemaster by GUID; unstable/swap also carry the target pet number.
///
/// The list row carries a NUL-terminated pet name, so the packet is variable length —
/// parsed defensively (any underrun or trailing byte makes it not this packet)
/// rather than by a fixed stride.
/// </summary>
public static class StableWire
{
    // SMSG_STABLE_RESULT codes (NPCHandler.cpp).
    public const byte ResultErrMoney = 0x01;
    public const byte ResultErrStable = 0x06;   // generic failure
    public const byte ResultStabled = 0x08;
    public const byte ResultUnstabled = 0x09;   // unstable / swap success
    public const byte ResultBoughtSlot = 0x0A;

    /// <summary>u64 guid + u8 count + u8 stableSlots.</summary>
    private const int ListHeaderBytes = 10;

    public static bool IsSuccess(byte result) =>
        result is ResultStabled or ResultUnstabled or ResultBoughtSlot;

    /// <summary>Player-facing text for a stable result code.</summary>
    public static string DescribeResult(byte result) => result switch
    {
        ResultStabled => "Pet stabled.",
        ResultUnstabled => "Pet moved.",
        ResultBoughtSlot => "Stable slot purchased.",
        ResultErrMoney => "You don't have enough money.",
        _ => "The stablemaster can't do that right now.",
    };

    /// <summary>MSG_LIST_STABLED_PETS / CMSG_STABLE_PET / CMSG_BUY_STABLE_SLOT body: just the NPC guid.</summary>
    public static byte[] BuildNpcGuidBody(ulong npcGuid)
    {
        var w = new PacketWriter(8);
        w.WriteU64(npcGuid);
        return w.ToArray();
    }

    /// <summary>CMSG_UNSTABLE_PET / CMSG_STABLE_SWAP_PET body: NPC guid + pet number.</summary>
    public static byte[] BuildPetActionBody(ulong npcGuid, uint petNumber)
    {
        var w = new PacketWriter(12);
        w.WriteU64(npcGuid);
        w.WriteU32(petNumber);
        return w.ToArray();
    }

    /// <summary>
    /// MSG_LIST_STABLED_PETS response: u64 npcGuid, u8 count, u8 stableSlots, then
    /// count × { u32 petNumber, u32 entry, u32 level, cstring name, u32 loyalty, u8 slot }.
    /// </summary>
    public static bool TryParseStableList(byte[] body, out StableList list)
    {
        list = new StableList(0, 0, []);
        if (body.Length < ListHeaderBytes) return false;
        try
        {
            var r = new PacketReader(body);
            ulong npcGuid = r.ReadU64();
            int count = r.ReadU8();
            byte stableSlots = r.ReadU8();

            var pets = new StabledPet[count];
            for (int i = 0; i < count; i++)
            {
                uint petNumber = r.ReadU32();
                uint entry = r.ReadU32();
                uint level = r.ReadU32();
                string name = r.ReadCString();
                uint loyalty = r.ReadU32();
                byte slot = r.ReadU8();
                pets[i] = new StabledPet(petNumber, entry, level, name, loyalty, slot);
            }
            if (r.HasMore) return false;   // trailing bytes → not this packet
            if (npcGuid == 0) return false;

            list = new StableList(npcGuid, stableSlots, pets);
            return true;
        }
        catch (EndOfStreamException)
        {
            return false;
        }
    }
}
