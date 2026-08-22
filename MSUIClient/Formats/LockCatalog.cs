namespace MSUIClient.Formats;

public readonly record struct LockSlot(uint KeyType, uint Index, uint Skill, uint Action)
{
    public bool Available(uint gameObjectState, bool flagLocked)
    {
        if (Action == 4) return gameObjectState == LockCatalog.StateActiveAlternative;
        if (gameObjectState == LockCatalog.StateActiveAlternative) return false;
        if (Action is 0 or 1 or 3 && gameObjectState != LockCatalog.StateReady) return false;
        return Action switch
        {
            0 => !flagLocked,
            1 => flagLocked,
            2 => gameObjectState == LockCatalog.StateActive,
            _ => true,
        };
    }
}

public enum GameObjectLockOutcomeKind { Unlocked, OpenBySpell, OpenByKey, Unmet }
public readonly record struct GameObjectLockOutcome(GameObjectLockOutcomeKind Kind, uint Id)
{
    public bool BlocksUsable(bool flagLocked) =>
        flagLocked && Kind == GameObjectLockOutcomeKind.Unmet;
}

public static class GameObjectLockLaw
{
    public static GameObjectLockOutcome Resolve(IReadOnlyList<LockSlot> slots,
        IEnumerable<uint> knownSpells, Func<uint, SpellInfo?> spellById,
        Func<uint, uint> skillValueForSpell, Func<uint, bool> holdsItem,
        uint gameObjectState, bool flagLocked, uint gameObjectLevel)
    {
        bool real = false;
        foreach (LockSlot slot in slots)
        {
            if (slot.KeyType == LockCatalog.KeySkill)
            {
                real = true;
                if (!slot.Available(gameObjectState, flagLocked)) continue;
                foreach (uint spellId in knownSpells.OrderBy(x => x))
                {
                    SpellInfo? candidate = spellById(spellId);
                    if (candidate is not SpellInfo spell || OpenLockLane(spell, slot.Index) < 0)
                        continue;
                    int lane = OpenLockLane(spell, slot.Index);
                    int provides = OpenLockSkill(spell, lane, skillValueForSpell(spellId));
                    uint required = slot.Skill != 0 ? slot.Skill : gameObjectLevel * 5;
                    if (provides >= required)
                        return new(GameObjectLockOutcomeKind.OpenBySpell, spellId);
                }
            }
            else if (slot.KeyType == LockCatalog.KeyItem)
            {
                real = true;
                if (slot.Available(gameObjectState, flagLocked) && holdsItem(slot.Index))
                    return new(GameObjectLockOutcomeKind.OpenByKey, slot.Index);
            }
        }
        return new(real ? GameObjectLockOutcomeKind.Unmet :
            GameObjectLockOutcomeKind.Unlocked, 0);
    }

    public static int OpenLockLane(in SpellInfo spell, uint lockType)
    {
        if (spell.EffectIds is null || spell.EffectMiscValues is null) return -1;
        int lanes = Math.Min(spell.EffectIds.Length, spell.EffectMiscValues.Length);
        for (int lane = 0; lane < lanes; lane++)
            if (spell.EffectIds[lane] == 33 && spell.EffectMiscValues[lane] == lockType)
                return lane;
        return -1;
    }

    public static int OpenLockSkill(in SpellInfo spell, int lane, uint skillValue)
    {
        if (lane is < 0 or > 2) return 0;
        uint capped = spell.MaxLevel > 0 ? Math.Min(skillValue, spell.MaxLevel * 5) : skillValue;
        float delta = Math.Max(0, (int)(capped / 5) - (int)spell.BaseLevel);
        float value = At(spell.EffectBasePoints, lane) + At(spell.EffectBaseDice, lane) +
            At(spell.EffectDicePerLevel, lane) * delta +
            At(spell.EffectRealPointsPerLevel, lane) * delta;
        float doubled = value >= 0 ? value * 2f - .5f : value * 2f + .5f;
        return ((int)MathF.Round(doubled, MidpointRounding.ToEven)) >> 1;
    }

    private static int At(int[]? values, int index) =>
        values is not null && index < values.Length ? values[index] : 0;
    private static float At(float[]? values, int index) =>
        values is not null && index < values.Length ? values[index] : 0f;
}

/// <summary>
/// Build-5875 Lock.dbc. GameObject chest data[0] supplies the row id; a resource
/// tracking mask matches SKILL slots by LockType bit (1 &lt;&lt; (Index - 1)).
/// </summary>
public sealed class LockCatalog
{
    public const string MpqPath = @"DBFilesClient\Lock.dbc";
    public const uint KeyItem = 1;
    public const uint KeySkill = 2;
    public const uint StateActive = 0;
    public const uint StateReady = 1;
    public const uint StateActiveAlternative = 2;
    private readonly Dictionary<uint, LockSlot[]> _rows = new();

    public int Count => _rows.Count;
    public IReadOnlyList<LockSlot> Slots(uint id) =>
        _rows.TryGetValue(id, out LockSlot[]? slots) ? slots : [];

    public bool MatchesResourceMask(uint lockId, uint mask)
    {
        if (lockId == 0 || mask == 0 || !_rows.TryGetValue(lockId, out LockSlot[]? slots)) return false;
        return slots.Any(s => s.KeyType == KeySkill && s.Index is >= 1 and <= 32 &&
            (mask & (1u << ((int)s.Index - 1))) != 0);
    }

    public uint ResourceLockType(uint lockId)
    {
        if (!_rows.TryGetValue(lockId, out LockSlot[]? slots)) return 0;
        return slots.FirstOrDefault(s => s.KeyType == KeySkill && s.Index is 2 or 3).Index;
    }

    /// <summary>The cursor classifier reads only Lock.dbc's first requirement slot.</summary>
    public uint FirstCursorLockType(uint lockId)
    {
        if (!_rows.TryGetValue(lockId, out LockSlot[]? slots) || slots.Length == 0) return 0;
        LockSlot first = slots[0];
        return first.Index is >= 1 and <= 3 ? first.Index : 0;
    }

    public static LockCatalog? Load(MpqMount mpq)
    {
        byte[]? bytes = mpq.ReadFile(MpqPath);
        DbcFile? dbc = bytes is null ? null : DbcFile.Parse(bytes);
        if (dbc is null || dbc.FieldCount != 33) return null;
        var result = new LockCatalog();
        for (int row = 0; row < dbc.RecordCount; row++)
        {
            uint id = dbc.GetUInt(row, 0);
            var slots = new LockSlot[8];
            for (int i = 0; i < slots.Length; i++)
                slots[i] = new(dbc.GetUInt(row, 1 + i), dbc.GetUInt(row, 9 + i),
                    dbc.GetUInt(row, 17 + i), dbc.GetUInt(row, 25 + i));
            result._rows[id] = slots;
        }
        return result;
    }
}
