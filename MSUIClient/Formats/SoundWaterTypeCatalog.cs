namespace MSUIClient.Formats;

/// <summary>
/// Build-5875 SoundWaterType.dbc: the authored liquid class/speed nibble to
/// SoundEntries kit map used by the above-water liquid ambience driver.
/// </summary>
public sealed class SoundWaterTypeCatalog
{
    public const string MpqPath = @"DBFilesClient\SoundWaterType.dbc";
    private readonly Dictionary<(uint Class, uint Speed), uint> _kits = [];

    public int Count => _kits.Count;

    /// <summary>
    /// Resolve one MCLQ/MLIQ low nibble. Its low two bits are the liquid class;
    /// bits 2-3 are the authored fluid speed. 0x0f is the dry sentinel.
    /// </summary>
    public bool TryGetKit(byte nibble, out uint kit)
    {
        uint value = (uint)(nibble & 0x0f);
        if (value == 0x0f)
        {
            kit = 0;
            return false;
        }
        return _kits.TryGetValue((value & 3u, value & 0x0cu), out kit);
    }

    public static SoundWaterTypeCatalog? Load(MpqMount mpq)
    {
        DbcFile? dbc = mpq.ReadFile(MpqPath) is { } bytes ? DbcFile.Parse(bytes) : null;
        if (dbc is null || dbc.FieldCount < 4) return null;

        var result = new SoundWaterTypeCatalog();
        for (int row = 0; row < dbc.RecordCount; row++)
        {
            uint liquidClass = dbc.GetUInt(row, 1);
            uint fluidSpeed = dbc.GetUInt(row, 2);
            uint kit = dbc.GetUInt(row, 3);
            if (kit != 0) result._kits[(liquidClass, fluidSpeed)] = kit;
        }
        return result;
    }
}
