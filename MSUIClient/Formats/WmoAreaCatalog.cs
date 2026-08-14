namespace MSUIClient.Formats;

/// <summary>
/// One row of vanilla <c>WMOAreaTable.dbc</c>. The first three lookup columns
/// live on separate source records: MOHD.wmoID on the root, MODF.nameSet on
/// the placement, and MOGP.uniqueID on the group.
/// </summary>
public sealed record WmoAreaRow(
    uint Id,
    uint WmoId,
    uint NameSetId,
    uint WmoGroupId,
    uint SoundProvider,
    uint SoundProviderUnderwater,
    uint AmbienceId,
    uint ZoneMusicId,
    uint IntroSoundId,
    uint AreaTableId,
    string Name);

/// <summary>
/// Typed reader for build-5875 <c>DBFilesClient\WMOAreaTable.dbc</c>.
///
/// Rows are keyed by the exact (WMOID, NameSetID, WMOGroupID) tuple. A
/// WMOGroupID of <see cref="uint.MaxValue"/> is the whole-WMO default. The
/// resolved view overlays non-zero group fields over that default, matching
/// the client data's use of sparse group rows.
/// </summary>
public sealed class WmoAreaCatalog
{
    public const string MpqPath = @"DBFilesClient\WMOAreaTable.dbc";
    public const int VanillaFieldCount = 20;
    public const int VanillaRecordSize = VanillaFieldCount * sizeof(uint);

    private readonly Dictionary<(uint Wmo, uint NameSet, uint Group), WmoAreaRow> _groups = [];
    private readonly Dictionary<(uint Wmo, uint NameSet), WmoAreaRow> _defaults = [];

    /// <summary>Distinct exact keys; vanilla contains ten duplicate editor rows.</summary>
    public int Count => _groups.Count + _defaults.Count;

    public static WmoAreaCatalog? Parse(byte[] data)
    {
        DbcFile? dbc = DbcFile.Parse(data);
        // Build 5875 is exactly 20 fields / 80 bytes. Keeping this strict is
        // important: the WMO/NameSet/Group join columns are all plain integers,
        // so accepting a different client schema could appear to work while
        // silently assigning the wrong interior area.
        if (dbc is null ||
            dbc.FieldCount != VanillaFieldCount ||
            dbc.RecordSize != VanillaRecordSize)
            return null;

        var catalog = new WmoAreaCatalog();
        for (int row = 0; row < dbc.RecordCount; row++)
        {
            uint wmoId = dbc.GetUInt(row, 1);
            uint nameSetId = dbc.GetUInt(row, 2);
            uint groupId = dbc.GetUInt(row, 3);
            var value = new WmoAreaRow(
                dbc.GetUInt(row, 0),
                wmoId,
                nameSetId,
                groupId,
                dbc.GetUInt(row, 4),
                dbc.GetUInt(row, 5),
                dbc.GetUInt(row, 6),
                dbc.GetUInt(row, 7),
                dbc.GetUInt(row, 8),
                dbc.GetUInt(row, 10),
                dbc.GetString(row, 11));

            if (groupId == uint.MaxValue)
                catalog._defaults[(wmoId, nameSetId)] = value;
            else
                catalog._groups[(wmoId, nameSetId, groupId)] = value;
        }

        Console.WriteLine($"[dbc] WMOAreaTable: {catalog.Count} distinct row(s)");
        return catalog;
    }

    /// <summary>The exact group row. No name-set retry and no default fallback.</summary>
    public WmoAreaRow? GroupRow(uint wmoId, uint nameSetId, uint groupId)
        => _groups.GetValueOrDefault((wmoId, nameSetId, groupId));

    /// <summary>The exact whole-WMO row. No name-set retry.</summary>
    public WmoAreaRow? DefaultRow(uint wmoId, uint nameSetId)
        => _defaults.GetValueOrDefault((wmoId, nameSetId));

    /// <summary>
    /// Resolve the runtime identity. The selected name set is tried first,
    /// then zero when it has no rows; sparse group fields inherit the whole-WMO
    /// default. This is the audio/area overlay law used by the archived client
    /// reference, while <see cref="GroupRow"/> and <see cref="DefaultRow"/>
    /// remain available for exact zone-text queries.
    /// </summary>
    public WmoAreaRow? Resolve(uint wmoId, uint nameSetId, uint groupId)
    {
        WmoAreaRow? group = null;
        WmoAreaRow? fallback = null;
        // This lookup runs on the minimap hot path. Two scalar probes preserve
        // the name-set fallback law without allocating a candidates array each
        // frame.
        group = GroupRow(wmoId, nameSetId, groupId);
        fallback = DefaultRow(wmoId, nameSetId);
        if (group is null && fallback is null && nameSetId != 0)
        {
            group = GroupRow(wmoId, 0, groupId);
            fallback = DefaultRow(wmoId, 0);
        }
        if (group is null && fallback is null) return null;
        if (group is null) return fallback;
        if (fallback is null) return group;

        static uint NonZero(uint value, uint inherited) => value != 0 ? value : inherited;
        return group with
        {
            SoundProvider = NonZero(group.SoundProvider, fallback.SoundProvider),
            SoundProviderUnderwater = NonZero(
                group.SoundProviderUnderwater, fallback.SoundProviderUnderwater),
            AmbienceId = NonZero(group.AmbienceId, fallback.AmbienceId),
            ZoneMusicId = NonZero(group.ZoneMusicId, fallback.ZoneMusicId),
            IntroSoundId = NonZero(group.IntroSoundId, fallback.IntroSoundId),
            AreaTableId = NonZero(group.AreaTableId, fallback.AreaTableId),
            Name = group.Name.Length > 0 ? group.Name : fallback.Name,
        };
    }
}
