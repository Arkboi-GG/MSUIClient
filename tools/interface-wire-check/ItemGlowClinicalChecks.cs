using System.Numerics;
using MSUIClient;
using MSUIClient.Formats;

internal static class ItemGlowClinicalChecks
{
    public static void Run()
    {
        ItemVisualCatalog visuals = ItemVisualCatalog.FromRows(
        [
            (25u, new string?[] { null, null, null, "base.mdx", null }),
            (61u, new string?[] { null, null, null, "enchant.mdx", null }),
        ]);
        EnchantCatalog enchants = EnchantCatalog.FromRows(
        [
            new EnchantInfo(1, "Rockbiter", 0, 61),
            new EnchantInfo(7, "Base-shaped", 0, 25),
            new EnchantInfo(999, "No visual", 0, 0),
        ]);
        Check(ItemGlowLaw.EffectiveVisual(visuals, enchants, 25, [1u]) == 25 &&
              ItemGlowLaw.EffectiveVisual(visuals, enchants, 0, [999u, 1u]) == 61 &&
              ItemGlowLaw.EffectiveVisual(visuals, null, -1, [1u]) == 0,
            "intrinsic-wins/first-enchant/signed item visual fork drift");

        var model = new M2Model();
        model.Attachments.Add(new M2Attachment { Id = 99, Position = new Vector3(1, 2, 3) });
        model.AttachmentLookup.Add(-1);
        model.AttachmentLookup.Add(-1);
        model.AttachmentLookup.Add(0);
        Check(ItemGlowLaw.AttachmentPosition(model, 2) == new Vector3(1, 2, 3) &&
              ItemGlowLaw.AttachmentPosition(model, 0) is null &&
              ItemGlowLaw.AttachmentPosition(model, 4) is null,
            "item glow attachment lookup-only/miss suppression drift");

        string root = ClientConfig.FindRepoRoot();
        string attached = SourceText.Read(Path.Combine(root, "MSUIClient", "World", "Units",
            "AttachedItemRenderer.cs"));
        string effects = SourceText.Read(Path.Combine(root, "MSUIClient", "World", "Units",
            "SpellEffectSource.cs"));
        string program = SourceText.Read(Path.Combine(root, "MSUIClient", "Program.cs"));
        string creature = SourceText.Read(Path.Combine(root, "MSUIClient", "World", "Units",
            "CreatureRenderer.cs"));
        Check(attached.Contains("ItemGlowLaw.EffectiveVisual", StringComparison.Ordinal) &&
              attached.Contains("Matrix4x4.CreateTranslation(local) * itemRoot", StringComparison.Ordinal) &&
              attached.Contains("worldInstance.M41 += camera.Position.X", StringComparison.Ordinal) &&
              effects.Contains("SyncItemGlows", StringComparison.Ordinal) &&
              effects.Contains("item-glow:{asset.Path}#{glow.Id}", StringComparison.Ordinal) &&
              program.Contains("_spellEffects.SyncItemGlows", StringComparison.Ordinal) &&
              creature.Contains("PlayerVisibleItemEnchant", StringComparison.Ordinal),
            "item/enchant glow attachment or shared effect-pipeline wiring drift");

        CheckActualDataIfPresent(root);
    }

    private static void CheckActualDataIfPresent(string root)
    {
        string data = Path.Combine(root, "GameData", "Data");
        if (!Directory.Exists(data)) return;
        ItemVisualCatalog visuals = ItemVisualCatalog.Load(data) ??
            throw new InvalidDataException("ItemVisuals chain unavailable");
        EnchantCatalog enchants = EnchantCatalog.Load(data) ??
            throw new InvalidDataException("SpellItemEnchantment unavailable");
        Check(visuals.Count == 34 &&
              enchants.Rows.Count(row => row.VisualId != 0) == 102 &&
              enchants.Visual(1) == 61 &&
              visuals.Effects(61)?[3]?.Contains("Enchantments", StringComparison.OrdinalIgnoreCase) == true,
            "actual build-5875 item/enchant visual chain drift");
        Check(ItemGlowLaw.EffectiveVisual(visuals, enchants, 0, [999u, 1u]) == 61,
            "first visual-bearing actual enchant selection drift");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
