namespace MSUIClient.Formats;

public readonly record struct LockSlot(uint KeyType, uint Index, uint Skill);

/// <summary>
/// Build-5875 Lock.dbc. GameObject chest data[0] supplies the row id; a resource
/// tracking mask matches SKILL slots by LockType bit (1 &lt;&lt; (Index - 1)).
/// </summary>
public sealed class LockCatalog
{
    public const string MpqPath = @"DBFilesClient\Lock.dbc";
    public const uint KeySkill = 2;
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
                slots[i] = new(dbc.GetUInt(row, 1 + i), dbc.GetUInt(row, 9 + i), dbc.GetUInt(row, 17 + i));
            result._rows[id] = slots;
        }
        return result;
    }
}
