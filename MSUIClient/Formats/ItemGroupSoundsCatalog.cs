namespace MSUIClient.Formats;

public enum ItemSoundGesture { Pickup = 0, PutDown = 1, Use = 2 }

/// <summary>
/// Build-5875 ItemGroupSounds.dbc: id + four SoundEntries kit ids. Items join
/// through ItemDisplayInfo field 11; zero means the authored gesture is silent.
/// </summary>
public sealed class ItemGroupSoundsCatalog
{
    public const string MpqPath = @"DBFilesClient\ItemGroupSounds.dbc";
    private readonly Dictionary<uint, uint[]> _groups = [];
    public int Count => _groups.Count;

    public uint? Kit(uint group, ItemSoundGesture gesture) =>
        _groups.TryGetValue(group, out uint[]? kits) && kits[(int)gesture] != 0
            ? kits[(int)gesture] : null;

    /// <summary>Clinical fixture; production normally loads the DBC from the MPQ chain.</summary>
    public static ItemGroupSoundsCatalog FromRows(
        params (uint Id, uint Pickup, uint PutDown, uint Use, uint Unused)[] rows)
    {
        var result = new ItemGroupSoundsCatalog();
        foreach (var row in rows)
            result._groups[row.Id] = [row.Pickup, row.PutDown, row.Use, row.Unused];
        return result;
    }

    public static ItemGroupSoundsCatalog? Load(MpqMount mpq)
    {
        byte[]? bytes = mpq.ReadFile(MpqPath);
        if (bytes is null) return null;
        DbcFile? dbc = DbcFile.Parse(bytes);
        if (dbc is null || dbc.FieldCount != 5 || dbc.RecordSize != 20)
            return null;
        var result = new ItemGroupSoundsCatalog();
        for (int row = 0; row < dbc.RecordCount; row++)
        {
            uint id = dbc.GetUInt(row, 0);
            if (id == 0) continue;
            result._groups[id] =
                [dbc.GetUInt(row, 1), dbc.GetUInt(row, 2), dbc.GetUInt(row, 3), dbc.GetUInt(row, 4)];
        }
        Console.WriteLine($"[dbc] ItemGroupSounds: {result.Count} group(s)");
        return result;
    }
}
