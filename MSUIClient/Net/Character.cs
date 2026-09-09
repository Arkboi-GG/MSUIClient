using System.Numerics;

namespace MSUIClient.Net;

// A SMSG_CHAR_ENUM roster entry, ported from benilla-protocol/messages/roster.rs
// (vmangos Player::BuildEnumData field order). Position is raw WoW space.

public readonly record struct CharEnumEquip(uint DisplayId, byte InventoryType);

public sealed class Character
{
    public ulong Guid;
    public string Name = "";
    public byte Race;      // ChrRaces.dbc id (1 Human .. 8 Troll)
    public byte Class;     // ChrClasses.dbc id (1 Warrior .. 11 Druid)
    public byte Gender;    // 0 male, 1 female
    public byte Skin, Face, HairStyle, HairColor, FacialHair;
    public byte Level;
    public uint Zone;      // AreaTable.dbc id
    public uint Map;
    public Vector3 Position;
    public uint Flags;     // CHARACTER_FLAG_* (ghost, hide-helm/cloak, rename)
    public uint PetDisplayId, PetLevel, PetFamily; // saved current pet supplied by CHAR_ENUM
    public CharEnumEquip[] Equipment = new CharEnumEquip[19];

    public const uint FlagGhost = 0x2000;
    public const uint FlagRename = 0x4000;
    public bool RequiresRename => (Flags & FlagRename) != 0;

    public bool IsGhost => (Flags & FlagGhost) != 0;

    /// <summary>Faction tongue for chat (Alliance races → Common, Horde → Orcish). vmangos drops chat in an unknown language.</summary>
    public uint FactionLanguage => Race is 2 or 5 or 6 or 8 ? 0x1u /*Orcish*/ : 0x7u /*Common*/;

    public static Character Read(PacketReader r)
    {
        var c = new Character
        {
            Guid = r.ReadU64(),
            Name = r.ReadCString(),
            Race = r.ReadU8(),
            Class = r.ReadU8(),
            Gender = r.ReadU8(),
            Skin = r.ReadU8(),
            Face = r.ReadU8(),
            HairStyle = r.ReadU8(),
            HairColor = r.ReadU8(),
            FacialHair = r.ReadU8(),
            Level = r.ReadU8(),
            Zone = r.ReadU32(),
            Map = r.ReadU32(),
            Position = r.ReadVector3(),
        };
        r.ReadU32();            // guild id (discard)
        c.Flags = r.ReadU32();
        r.ReadU8();             // first-login (discard)
        c.PetDisplayId = r.ReadU32();
        c.PetLevel = r.ReadU32();
        c.PetFamily = r.ReadU32();
        for (int i = 0; i < 19; i++)
            c.Equipment[i] = new CharEnumEquip(r.ReadU32(), r.ReadU8());
        r.ReadU32();            // first bag display id (discard)
        r.ReadU8();             // first bag inventory type (discard)
        return c;
    }
}
