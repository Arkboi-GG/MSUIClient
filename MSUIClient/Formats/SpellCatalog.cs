namespace MSUIClient.Formats;

/// <summary>The 1.12 Spell.dbc display/cast subset used by the spellbook and action bar.</summary>
public readonly record struct SpellInfo(
    uint Id, string Name, string Rank, string IconPath,
    uint Attributes, uint AttributesEx2, uint AttributesEx3,
    uint InterruptFlags, uint ChannelInterruptFlags, uint Targets, uint ImplicitTarget,
    uint RecoveryMs, uint CategoryRecoveryMs,
    uint PowerType, uint ManaCost, uint ManaCostPercent,
    uint StartRecoveryCategory, uint StartRecoveryMs, uint VisualId, float Speed, string Description,
    uint RangeIndex, uint School = 0, uint CastingTimeIndex = 0, int CastTimeMs = 0,
    uint DurationIndex = 0, int DurationMs = 0)
{
    public bool Passive => (Attributes & 0x40) != 0;
    public bool Ranged => (Attributes & 0x2) != 0 || AutoRepeat;
    public bool AutoRepeat => (AttributesEx2 & 0x20) != 0;
    public bool OnNextSwing => (Attributes & 0x404) != 0;
    public bool MovementInterrupts => (InterruptFlags & 0x08) != 0;
    public bool MovementInterruptsChannel => (ChannelInterruptFlags & 0x08) != 0;
    public string CastClassification => ChannelInterruptFlags != 0 ? "CHANNEL" :
        CastTimeMs > 0 ? "CAST_TIME" : "INSTANT";
}

/// <summary>One SpellRange.dbc row: min/max in yards; flags bit0 marks the melee row.</summary>
public readonly record struct SpellRangeRow(float Min, float Max, bool Melee);

public sealed class SpellCatalog
{
    public const string SpellPath = @"DBFilesClient\Spell.dbc";
    public const string IconPath = @"DBFilesClient\SpellIcon.dbc";
    public const string RangePath = @"DBFilesClient\SpellRange.dbc";
    private readonly Dictionary<uint, SpellInfo> _spells = new();
    private readonly Dictionary<uint, SpellRangeRow> _ranges = new();
    private readonly Dictionary<uint, int> _castTimes = new();
    private readonly Dictionary<uint, int> _durations = new();

    public int Count => _spells.Count;
    public bool TryGet(uint id, out SpellInfo spell) => _spells.TryGetValue(id, out spell);
    public bool TryGetRange(uint rangeIndex, out SpellRangeRow range) => _ranges.TryGetValue(rangeIndex, out range);

    public static SpellCatalog? Load(MpqMount mpq)
    {
        byte[]? spellBytes = mpq.ReadFile(SpellPath);
        byte[]? iconBytes = mpq.ReadFile(IconPath);
        DbcFile? spells = spellBytes is null ? null : DbcFile.Parse(spellBytes);
        DbcFile? icons = iconBytes is null ? null : DbcFile.Parse(iconBytes);
        if (spells is null || icons is null || spells.FieldCount < 173 || icons.FieldCount < 2)
            return null;

        var iconMap = new Dictionary<uint, string>();
        for (int row = 0; row < icons.RecordCount; row++)
        {
            uint id = icons.GetUInt(row, 0);
            string path = icons.GetString(row, 1);
            if (id != 0 && path.Length > 0)
                iconMap[id] = path.EndsWith(".blp", StringComparison.OrdinalIgnoreCase) ? path : path + ".blp";
        }

        var result = new SpellCatalog();

        DbcFile? castTimes = Parse(mpq, @"DBFilesClient\SpellCastTimes.dbc");
        if (castTimes is not null && castTimes.FieldCount >= 4)
            for (int row = 0; row < castTimes.RecordCount; row++)
                result._castTimes[castTimes.GetUInt(row, 0)] = Math.Max(0, castTimes.GetInt(row, 1));
        DbcFile? durations = Parse(mpq, @"DBFilesClient\SpellDuration.dbc");
        if (durations is not null && durations.FieldCount >= 4)
            for (int row = 0; row < durations.RecordCount; row++)
                result._durations[durations.GetUInt(row, 0)] = durations.GetInt(row, 1);

        // SpellRange.dbc: [0]=id, [1]=min f32, [2]=max f32, [3]=flags (bit0 = melee row).
        byte[]? rangeBytes = mpq.ReadFile(RangePath);
        DbcFile? ranges = rangeBytes is null ? null : DbcFile.Parse(rangeBytes);
        if (ranges is not null && ranges.FieldCount >= 4)
        {
            for (int row = 0; row < ranges.RecordCount; row++)
            {
                uint id = ranges.GetUInt(row, 0);
                result._ranges[id] = new SpellRangeRow(ranges.GetFloat(row, 1),
                    ranges.GetFloat(row, 2), (ranges.GetUInt(row, 3) & 0x1) != 0);
            }
        }

        for (int row = 0; row < spells.RecordCount; row++)
        {
            uint id = spells.GetUInt(row, 0);
            if (id == 0) continue;
            iconMap.TryGetValue(spells.GetUInt(row, 117), out string? icon);
            uint castTimeIndex = spells.GetUInt(row, 18), durationIndex = spells.GetUInt(row, 30);
            result._spells[id] = new SpellInfo(
                id, spells.GetString(row, 120), spells.GetString(row, 129), icon ?? "",
                spells.GetUInt(row, 6), spells.GetUInt(row, 8), spells.GetUInt(row, 9),
                spells.GetUInt(row, 21), spells.GetUInt(row, 23),
                spells.GetUInt(row, 13), spells.GetUInt(row, 82),
                spells.GetUInt(row, 19), spells.GetUInt(row, 20),
                spells.GetUInt(row, 31), spells.GetUInt(row, 32), spells.GetUInt(row, 156),
                spells.GetUInt(row, 157), spells.GetUInt(row, 158), spells.GetUInt(row, 115),
                spells.GetFloat(row, 37), spells.GetString(row, 138),
                spells.GetUInt(row, 36), spells.GetUInt(row, 1), castTimeIndex,
                result._castTimes.GetValueOrDefault(castTimeIndex), durationIndex,
                result._durations.GetValueOrDefault(durationIndex));
        }
        return result;
    }

    private static DbcFile? Parse(MpqMount mpq, string path)
        => mpq.ReadFile(path) is { } bytes ? DbcFile.Parse(bytes) : null;
}
