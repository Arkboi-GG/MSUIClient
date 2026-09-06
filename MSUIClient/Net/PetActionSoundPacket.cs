using MSUIClient.Formats;

namespace MSUIClient.Net;

public readonly record struct PetActionSoundPacket(ulong PetGuid, uint Talk)
{
    public static PetActionSoundPacket Parse(byte[] body)
    {
        if (body.Length != 12) throw new InvalidDataException("bad SMSG_PET_ACTION_SOUND body");
        var r = new PacketReader(body);
        return new(r.ReadU64(), r.ReadU32());
    }

    // Core PetTalk: SPECIAL_SPELL=0, ATTACK=1. These are selectors, not kit IDs.
    public uint SoundKit(CreatureVoice voice) => Talk switch
    {
        0 => voice.PetOrderSound,
        1 => voice.PetAttackSound,
        _ => 0,
    };
}
