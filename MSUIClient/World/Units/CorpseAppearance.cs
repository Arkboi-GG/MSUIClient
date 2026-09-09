using System.Numerics;
using System.Text;
using MSUIClient.Net;
using MSUIClient.Formats;

namespace MSUIClient.World.Units;

/// <summary>Build-5875 corpse descriptors are not unit/player descriptors. Equipment contains
/// display IDs, not item entries. This view never changes the gameplay entity or sends queries.</summary>
public readonly struct CorpseAppearance(ObjectFields fields)
{
    private uint Value(ushort index) => fields.GetU32(index) ?? 0;

    public ulong Owner => fields.GetGuid(6) ?? 0;
    public uint DisplayId => Value(12);
    public uint Flags => Value(35);
    public bool IsBones => (Flags & 1) != 0;
    public byte Race => (byte)(Value(32) >> 8);
    public byte Gender => (byte)(Value(32) >> 16);
    public Vector3 Position => new(fields.GetF32(9) ?? 0, fields.GetF32(10) ?? 0, fields.GetF32(11) ?? 0);
    public float Facing => fields.GetF32(8) ?? 0;
    public CharacterEquipment.PlayerAppearance Look => new(
        (byte)(Value(32) >> 24), (byte)Value(33),
        (byte)(Value(33) >> 8), (byte)(Value(33) >> 16),
        (byte)(Value(33) >> 24));

    public bool CanRenderBody => !IsBones && DisplayId != 0 && Gender <= 1 &&
        Race is 1 or 2 or 3 or 4 or 5 or 6 or 7 or 8 &&
        float.IsFinite(Position.X) && float.IsFinite(Position.Y) &&
        float.IsFinite(Position.Z) && float.IsFinite(Facing);

    public (uint Display, byte InventoryType) Equipment(int slot)
    {
        if ((uint)slot >= 19) throw new ArgumentOutOfRangeException(nameof(slot));
        uint packed = Value((ushort)(13 + slot));
        return (packed & 0x00FFFFFF, (byte)(packed >> 24));
    }

    public bool SlotShown(int slot) => slot switch
    {
        0 => (Flags & 8) == 0,
        14 => (Flags & 16) == 0,
        // The corpse does not supply item class, sheath or enchantments. Held-item
        // attachment behavior needs its own empirical check; do not invent metadata.
        15 or 16 or 17 => false,
        _ => true,
    };

    public CharacterEquipment BuildEquipment()
    {
        var kit = new CharacterEquipment { PlayerLook = Look };
        for (int slot = 0; slot < 19; slot++)
        {
            var item = Equipment(slot);
            if (SlotShown(slot) && item.Display != 0)
                kit.Add($"corpse-slot{slot}", item.Display, item.InventoryType, slot);
        }
        return kit;
    }

    public void UpdateRenderView(WorldEntity view)
    {
        // This private view is never inserted into EntityStore or a gameplay admission list.
        view.Type = ObjectTypeId.Player;
        view.Position = Position; view.Orientation = Facing;
        view.Fields.SetU32(ObjectFields.UNIT_DISPLAYID, DisplayId);
        view.Fields.SetU32(ObjectFields.UNIT_BYTES_0, (uint)(Race | (Gender << 16)));
        view.Fields.SetU32(ObjectFields.UNIT_MAXHEALTH, 1);
        view.Fields.SetU32(ObjectFields.UNIT_HEALTH, 0);
        view.Fields.SetU32(ObjectFields.OBJECT_SCALE_X, fields.GetU32(ObjectFields.OBJECT_SCALE_X) ?? BitConverter.SingleToUInt32Bits(1));
        view.Fields.SetUnitStandState((byte)UnitStandState.Dead);
    }

    public CreatureModelInfo ModelInfo(CreatureModelInfo model)
    {
        int[] slots = [0, 2, 3, 4, 5, 6, 7, 8, 9, 18, 14];
        var equipment = new uint[slots.Length];
        for (int i = 0; i < slots.Length; i++)
            if (SlotShown(slots[i])) equipment[i] = Equipment(slots[i]).Display;
        var look = Look;
        return model with
        {
            HasExtended = true, ExtRace = Race, ExtSex = Gender,
            ExtSkin = look.Skin, ExtFace = look.Face, ExtHairStyle = look.HairStyle,
            ExtHairColor = look.HairColor, ExtFacialHair = look.FacialHair,
            ExtEquipment = equipment, BakeName = "", IsPlayerAppearance = true,
        };
    }

    public string Signature()
    {
        var look = Look;
        var key = new StringBuilder("corpse/");
        key.Append(Race).Append('/').Append(Gender).Append('/').Append(DisplayId)
            .Append('/').Append(look.Skin).Append('/').Append(look.Face)
            .Append('/').Append(look.HairStyle).Append('/').Append(look.HairColor)
            .Append('/').Append(look.FacialHair).Append('|');
        for (int slot = 0; slot < 19; slot++)
        {
            var item = Equipment(slot);
            if (SlotShown(slot)) key.Append(item.Display).Append(',').Append(item.InventoryType);
            else key.Append("hidden");
            key.Append(':');
        }
        return key.ToString();
    }
}
