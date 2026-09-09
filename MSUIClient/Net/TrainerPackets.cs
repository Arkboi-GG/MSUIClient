namespace MSUIClient.Net;

public readonly record struct TrainerSpell(
    uint ServiceSpellId, byte State, uint Cost, bool PrimaryProfessionLearnable,
    bool PrimaryProfessionDialog, byte RequiredLevel, uint RequiredSkill,
    uint RequiredSkillValue, uint RequiredSpell1, uint RequiredSpell2, uint RequiredSpell3);

public sealed record TrainerList(ulong TrainerGuid, uint TrainerType,
    IReadOnlyList<TrainerSpell> Spells, string Greeting);

public readonly record struct TrainerResult(ulong TrainerGuid, uint ServiceSpellId, uint Error);

public static class TrainerPackets
{
    public static TrainerList ParseList(byte[] body)
    {
        var r = new PacketReader(body);
        ulong guid = r.ReadU64();
        uint type = r.ReadU32();
        uint count = r.ReadU32();
        if (count > 2048 || r.Remaining < checked((int)count * 38 + 1))
            throw new InvalidDataException($"trainer spell count {count} exceeds packet bounds");
        var spells = new List<TrainerSpell>((int)count);
        for (uint i = 0; i < count; i++)
            spells.Add(new TrainerSpell(r.ReadU32(), r.ReadU8(), r.ReadU32(),
                r.ReadU32() != 0, r.ReadU32() != 0, r.ReadU8(), r.ReadU32(), r.ReadU32(),
                r.ReadU32(), r.ReadU32(), r.ReadU32()));
        string greeting = r.ReadCString();
        if (r.Remaining != 0) throw new InvalidDataException($"trainer list has {r.Remaining} trailing bytes");
        return new TrainerList(guid, type, spells, greeting);
    }

    public static TrainerResult ParseSuccess(byte[] body)
    {
        var r = new PacketReader(body); return new TrainerResult(r.ReadU64(), r.ReadU32(), 0);
    }

    public static TrainerResult ParseFailure(byte[] body)
    {
        var r = new PacketReader(body); return new TrainerResult(r.ReadU64(), r.ReadU32(), r.ReadU32());
    }
}
