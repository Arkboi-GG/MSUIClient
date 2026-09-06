namespace MSUIClient.Net;

public abstract record SpellExecuteResult;
public sealed record ExecuteTarget(ulong Guid) : SpellExecuteResult;
public sealed record ExecuteDrain(ulong Guid, uint Amount, uint Power, float Multiplier) : SpellExecuteResult;
public sealed record ExecuteHeal(ulong Guid, uint Amount, bool Critical) : SpellExecuteResult;
public sealed record ExecuteEnergize(ulong Guid, uint Amount, uint Power) : SpellExecuteResult;
public sealed record ExecuteExtraAttacks(ulong Guid, uint Count) : SpellExecuteResult;
public sealed record ExecuteItem(uint Entry) : SpellExecuteResult;
public sealed record ExecuteInterrupt(ulong Guid, uint SpellId) : SpellExecuteResult;
public sealed record ExecuteDurability(ulong Guid, int ItemEntry, int Unknown) : SpellExecuteResult;
public sealed record SpellExecuteEffect(uint EffectId, SpellExecuteResult[] Results);
public sealed record CombatSpellExecution(ulong Caster, uint SpellId, SpellExecuteEffect[] Effects) : CombatEvent;

/// <summary>Build-5875 Spell::SendLogExecute: packed caster, full per-result GUIDs and effect-specific tails.</summary>
public static class SpellExecutePackets
{
    public static CombatSpellExecution Parse(byte[] body)
    {
        var r = new PacketReader(body);
        ulong caster = r.ReadPackedGuid(); uint spell = r.ReadU32(), count = r.ReadU32();
        if (count > 3 || count > r.Remaining / 8) throw new InvalidDataException("Invalid execute effect count");
        var effects = new SpellExecuteEffect[count];
        for (int i = 0; i < effects.Length; i++)
        {
            uint effect = r.ReadU32(), targets = r.ReadU32();
            int size = ResultSize(effect);
            if (targets > r.Remaining / size) throw new InvalidDataException("Execute result count exceeds payload");
            var results = new SpellExecuteResult[targets];
            for (int j = 0; j < results.Length; j++)
                results[j] = effect switch
                {
                    8 => new ExecuteDrain(r.ReadU64(), r.ReadU32(), r.ReadU32(), r.ReadF32()),
                    10 or 67 => new ExecuteHeal(r.ReadU64(), r.ReadU32(), r.ReadU8() != 0),
                    30 => new ExecuteEnergize(r.ReadU64(), r.ReadU32(), r.ReadU32()),
                    19 => new ExecuteExtraAttacks(r.ReadU64(), r.ReadU32()),
                    24 or 101 => new ExecuteItem(r.ReadU32()),
                    68 => new ExecuteInterrupt(r.ReadU64(), r.ReadU32()),
                    111 => new ExecuteDurability(r.ReadU64(), r.ReadI32(), r.ReadI32()),
                    _ => new ExecuteTarget(r.ReadU64()),
                };
            effects[i] = new(effect, results);
        }
        if (r.Remaining != 0) throw new InvalidDataException("Execute log has trailing bytes");
        return new(caster, spell, effects);
    }

    private static int ResultSize(uint effect) => effect switch
    {
        8 => 20, 10 or 67 => 13, 30 or 111 => 16, 19 or 68 => 12, 24 or 101 => 4,
        1 or 18 or 38 or 63 or 69 or 79 or 91 or 108 or 113 or 114 or 116 or 125 or 126 or
        33 or 59 or 102 or 50 or 28 or 56 or 41 or 42 or 87 or 88 or 89 or 90 or 73 or 74 or
        97 or 76 or 104 or 105 or 106 or 107 or 112 => 8,
        _ => throw new InvalidDataException($"Unsupported execute effect {effect}; result size is unknown"),
    };
}
