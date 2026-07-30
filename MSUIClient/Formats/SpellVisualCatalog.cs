namespace MSUIClient.Formats;

public readonly record struct SpellVisualStages(uint Precast, uint Cast, uint Impact,
    uint State, uint Channel, uint MissileEffect, uint MissileAttachment);

public readonly record struct SpellVisualKitInfo(ushort? AnimationId,
    IReadOnlyList<(ushort AttachmentId, string ModelPath)> Effects);

/// <summary>Vanilla SpellVisual -> kit -> effect-model and body-animation chain.</summary>
public sealed class SpellVisualCatalog
{
    private static readonly ushort[] KitAttachmentIds =
        [0x14, 0x22, 0x13, 0x15, 0x16, 0x11, 0x17, 0x18, 0x19];
    private readonly Dictionary<uint, SpellVisualStages> _visuals = [];
    private readonly Dictionary<uint, (ushort? Animation, uint[] Effects)> _kits = [];
    private readonly Dictionary<uint, string> _effectPaths = [];

    public bool TryGetStages(uint id, out SpellVisualStages stages) => _visuals.TryGetValue(id, out stages);

    public bool TryGetKit(uint id, out SpellVisualKitInfo kit)
    {
        if (!_kits.TryGetValue(id, out var raw)) { kit = default; return false; }
        var effects = new List<(ushort, string)>();
        for (int i = 0; i < raw.Effects.Length; i++)
            if (raw.Effects[i] is uint effect && effect != 0 && effect != uint.MaxValue &&
                _effectPaths.TryGetValue(effect, out string? path) && path.Length > 0)
                effects.Add((KitAttachmentIds[i], path));
        kit = new SpellVisualKitInfo(raw.Animation, effects);
        return true;
    }

    public string? MissilePath(in SpellVisualStages stages)
        => stages.MissileEffect != 0 && _effectPaths.TryGetValue(stages.MissileEffect, out string? path)
            ? path : null;

    public static SpellVisualCatalog? Load(MpqMount mpq)
    {
        DbcFile? visuals = Parse(mpq, @"DBFilesClient\SpellVisual.dbc");
        DbcFile? kits = Parse(mpq, @"DBFilesClient\SpellVisualKit.dbc");
        DbcFile? names = Parse(mpq, @"DBFilesClient\SpellVisualEffectName.dbc");
        if (visuals is null || kits is null || names is null || visuals.FieldCount < 16 ||
            kits.FieldCount < 35 || names.FieldCount < 5) return null;

        var result = new SpellVisualCatalog();
        for (int row = 0; row < names.RecordCount; row++)
        {
            uint id = names.GetUInt(row, 0);
            string path = names.GetString(row, 2);
            if (id != 0 && path.Length > 0) result._effectPaths[id] = path;
        }
        for (int row = 0; row < kits.RecordCount; row++)
        {
            uint id = kits.GetUInt(row, 0);
            uint anim = kits.GetUInt(row, 2);
            var effects = new uint[9];
            for (int i = 0; i < effects.Length; i++) effects[i] = kits.GetUInt(row, 3 + i);
            result._kits[id] = (anim is 0 or uint.MaxValue ? null : (ushort)anim, effects);
        }
        for (int row = 0; row < visuals.RecordCount; row++)
        {
            uint id = visuals.GetUInt(row, 0);
            result._visuals[id] = new SpellVisualStages(visuals.GetUInt(row, 1), visuals.GetUInt(row, 2),
                visuals.GetUInt(row, 3), visuals.GetUInt(row, 4), visuals.GetUInt(row, 5),
                visuals.GetUInt(row, 7), visuals.GetUInt(row, 9));
        }
        return result;
    }

    private static DbcFile? Parse(MpqMount mpq, string path)
        => mpq.ReadFile(path) is { } bytes ? DbcFile.Parse(bytes) : null;
}
