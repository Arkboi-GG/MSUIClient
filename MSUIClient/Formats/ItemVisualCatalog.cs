using System.Numerics;

namespace MSUIClient.Formats;

/// <summary>
/// Build-5875 ItemVisuals + ItemVisualEffects join. Each of the five slots maps
/// one-for-one to attachment id 0..4 on the held item's own M2.
/// </summary>
public sealed class ItemVisualCatalog
{
    public const string VisualsPath = @"DBFilesClient\ItemVisuals.dbc";
    public const string EffectsPath = @"DBFilesClient\ItemVisualEffects.dbc";
    public const int SlotCount = 5;

    private readonly Dictionary<uint, string?[]> _rows = [];

    public int Count => _rows.Count;
    public string?[]? Effects(int visualId) => visualId > 0 &&
        _rows.TryGetValue((uint)visualId, out string?[]? row) ? row : null;

    internal void Set(uint id, string?[] effects) => _rows[id] = effects;

    public static ItemVisualCatalog FromRows(
        IEnumerable<(uint Id, IReadOnlyList<string?> Effects)> rows)
    {
        var result = new ItemVisualCatalog();
        foreach ((uint id, IReadOnlyList<string?> effects) in rows)
        {
            var slots = new string?[SlotCount];
            for (int i = 0; i < Math.Min(SlotCount, effects.Count); i++) slots[i] = effects[i];
            result._rows[id] = slots;
        }
        return result;
    }

    public static ItemVisualCatalog? Load(string clientDataPath)
    {
        byte[]? effectBytes = AdtTerrainReader.ReadFileFromMpqs(clientDataPath, EffectsPath);
        byte[]? visualBytes = AdtTerrainReader.ReadFileFromMpqs(clientDataPath, VisualsPath);
        DbcFile? effectsDbc = effectBytes is null ? null : DbcFile.Parse(effectBytes);
        DbcFile? visualsDbc = visualBytes is null ? null : DbcFile.Parse(visualBytes);
        if (effectsDbc is null || effectsDbc.FieldCount != 2 ||
            visualsDbc is null || visualsDbc.FieldCount != 6)
            return null;

        var effectPaths = new Dictionary<uint, string>();
        for (int row = 0; row < effectsDbc.RecordCount; row++)
        {
            uint id = effectsDbc.GetUInt(row, 0);
            string path = effectsDbc.GetString(row, 1);
            if (id != 0 && path.Length > 0) effectPaths[id] = path;
        }

        var result = new ItemVisualCatalog();
        for (int row = 0; row < visualsDbc.RecordCount; row++)
        {
            uint id = visualsDbc.GetUInt(row, 0);
            if (id == 0) continue;
            var slots = new string?[SlotCount];
            for (int slot = 0; slot < SlotCount; slot++)
            {
                int raw = unchecked((int)visualsDbc.GetUInt(row, 1 + slot));
                if (raw > 0 && effectPaths.TryGetValue((uint)raw, out string? path))
                    slots[slot] = path;
            }
            result._rows[id] = slots;
        }
        return result;
    }
}

public static class ItemGlowLaw
{
    /// <summary>Intrinsic display visual wins; otherwise the first visual-bearing enchant wins.</summary>
    public static int EffectiveVisual(ItemVisualCatalog? visuals, EnchantCatalog? enchants,
        int intrinsic, IEnumerable<uint> itemEnchants)
    {
        if (visuals?.Effects(intrinsic) is not null) return intrinsic;
        if (enchants is null) return 0;
        foreach (uint enchant in itemEnchants)
            if (enchants.Visual(enchant) is int visual && visual != 0)
                return visual;
        return 0;
    }

    /// <summary>
    /// Item glow slots use AttachmentLookup only. A missing lookup entry suppresses
    /// that slot instead of falling back to the item origin or scanning duplicates.
    /// </summary>
    public static Vector3? AttachmentPosition(M2Model item, int slot)
    {
        if (slot < 0 || slot >= ItemVisualCatalog.SlotCount ||
            item.AttachmentLookup.Count <= slot) return null;
        short index = item.AttachmentLookup[slot];
        return index >= 0 && index < item.Attachments.Count
            ? item.Attachments[index].Position
            : null;
    }
}
