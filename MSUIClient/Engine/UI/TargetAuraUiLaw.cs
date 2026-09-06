using System.Numerics;
using MSUIClient.Formats;

namespace MSUIClient.Engine.UI;

public static class TargetAuraUiLaw
{
    public const int HelpfulLimit = 5, HarmfulLimit = 16;
    public static float IconSize(int debuffCount) => debuffCount >= 6 ? 17f : 21f;

    // Shipped TargetFrame.lua with no target-of-target child: the first wrap is at six,
    // and XML keeps debuffs 7..16 on the second row. Friendly targets put buffs first.
    public static Vector2 IconMin(bool harmful, int index, int debuffCount, bool friendly)
    {
        float size = IconSize(debuffCount);
        float y = friendly ? harmful ? 68 + size + 2 : 68 : harmful ? 68 : 68 + 2 * (size + 2);
        int row = harmful && index >= 6 ? 1 : 0;
        int col = harmful && index >= 6 ? index - 6 : index;
        return new(5 + col * (size + 3), y + row * (size + 2));
    }

    public static SpellTooltipView Tooltip(uint spellId, SpellInfo? spell, SpellCatalog catalog,
        uint auraLevel, bool harmful)
    {
        string description = spell is { } info
            ? SpellTooltipLaw.Substitute(info.AuraDescription, info, catalog, auraLevel) : "";
        string type = harmful ? spell?.DispelType switch
        {
            1 => "Magic", 2 => "Curse", 3 => "Disease", 4 => "Poison", _ => "",
        } : "";
        // Aura text describes the applied effect; cast cost/range/cooldown are not aura properties.
        return new(spell?.Name ?? $"Spell {spellId}", type, null, null, null, null, description);
    }
}
