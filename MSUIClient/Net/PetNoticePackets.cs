namespace MSUIClient.Net;

public readonly record struct PetNotice(string Key, string Fallback, bool TameFailure = false);

public static class PetNoticePackets
{
    // These layouts are the actual core producers: notably SendPetNameInvalid
    // sends neither its error argument nor the attempted name.
    public static PetNotice? Parse(Op opcode, byte[] body)
    {
        if (opcode == Op.SMSG_PET_TAME_FAILURE)
        {
            if (body.Length != 1) throw new InvalidDataException("bad SMSG_PET_TAME_FAILURE body");
            return body[0] switch
            {
                0 => null,
                1 => new("PETTAME_INVALIDCREATURE", "Creature not found", true),
                2 => new("PETTAME_TOOMANY", "You have too many pets already", true),
                3 => new("PETTAME_CREATUREALREADYOWNED", "Creature is already controlled", true),
                4 => new("PETTAME_NOTTAMEABLE", "Creature not tameable", true),
                5 => new("PETTAME_ANOTHERSUMMONACTIVE", "You have an active summon already", true),
                6 => new("PETTAME_UNITSCANTTAME", "You cannot tame creatures", true),
                7 => new("PETTAME_NOPETAVAILABLE", "You do not have a pet to summon", true),
                8 => new("PETTAME_INTERNALERROR", "Internal pet error", true),
                9 => new("PETTAME_TOOHIGHLEVEL", "Creature is too high level for you to tame", true),
                10 => new("PETTAME_DEAD", "Your pet is dead", true),
                11 => new("PETTAME_NOTDEAD", "Your pet is not dead", true),
                _ => new("PETTAME_UNKNOWNERROR", "Unknown taming error", true),
            };
        }
        if (body.Length != 0) throw new InvalidDataException($"bad {opcode} body");
        return opcode switch
        {
            Op.SMSG_PET_NAME_INVALID => new("ERR_INVALID_PETNAME", "Error, invalid name entered."),
            Op.SMSG_PET_BROKEN => new("ERR_PET_BROKEN", "Your pet has run away"),
            _ => throw new InvalidDataException("unsupported pet notice opcode"),
        };
    }
}
