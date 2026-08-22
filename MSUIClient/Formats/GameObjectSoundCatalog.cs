namespace MSUIClient.Formats;

/// <summary>
/// Build-5875 GameObjectDisplayInfo Sound0..9 slots. Only displays with at
/// least one authored kit are retained.
/// </summary>
public sealed class GameObjectSoundCatalog
{
    private readonly Dictionary<uint, uint[]> _slots = [];

    public int Count => _slots.Count;
    public uint Sound(uint displayId, int slot) =>
        slot is >= 0 and < 10 && _slots.TryGetValue(displayId, out uint[]? row)
            ? row[slot] : 0;

    public static GameObjectSoundCatalog? Load(MpqMount mpq)
    {
        DbcFile? dbc = mpq.ReadFile(GameObjectDisplayTable.MpqPath) is { } bytes
            ? DbcFile.Parse(bytes) : null;
        if (dbc is null || dbc.FieldCount != 12 || dbc.RecordSize != 48) return null;
        var result = new GameObjectSoundCatalog();
        for (int row = 0; row < dbc.RecordCount; row++)
        {
            uint id = dbc.GetUInt(row, 0);
            var slots = new uint[10];
            for (int slot = 0; slot < slots.Length; slot++)
                slots[slot] = dbc.GetUInt(row, 2 + slot);
            if (id != 0 && slots.Any(sound => sound != 0)) result._slots[id] = slots;
        }
        Console.WriteLine($"[dbc] GameObject sounds: {result.Count} display row(s)");
        return result;
    }

    public static GameObjectSoundCatalog FromRows(params (uint DisplayId, uint[] Slots)[] rows)
    {
        var result = new GameObjectSoundCatalog();
        foreach (var row in rows)
        {
            if (row.Slots.Length != 10)
                throw new ArgumentException("GameObject sound rows require exactly 10 slots");
            result._slots[row.DisplayId] = row.Slots.ToArray();
        }
        return result;
    }
}
