using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private void RestorePermanentCastFailureCooldown(ulong caster, uint spellId,
        SpellCastFailureContext? context)
    {
        if (context?.PermanentCooldown != true) return;
        SpellInfo info = default;
        _spellCatalog?.TryGet(spellId, out info);
        ActionsFor(caster).StartCooldown(spellId, info.Category, info.RecoveryMs,
            info.CategoryRecoveryMs, MovementInfo.ClientUptimeMs() / 1000.0,
            onHold: true, categoryWildcard: info.CategoryWildcard);
    }

    private string ContextualSpellFailureText(byte reason, string power, SpellCastFailureContext? context)
    {
        string fallback = SpellCastResultNames.Text(reason, power);
        if (context is null || reason == 0x17) return fallback;
        string? requirement = null;
        if (context.RequiredArea is { } area)
            requirement = _areas?.AreaName(area);
        else if (context.RequiredFocus is { } focus)
        {
            EnsureSpellFocusCatalog();
            requirement = _spellFoci?.KnownName(focus);
        }
        else if (context.ItemClass is >= 0)
        {
            if (_mpq is not null)
            {
                _itemClasses ??= ItemClassCatalog.Load(_mpq);
                _itemSubClasses ??= ItemSubClassCatalog.Load(_mpq);
            }
            uint itemClass = (uint)context.ItemClass.Value;
            var names = new List<string>();
            if (context.SubclassMask != 0)
                for (int bit = 0; bit < 32; bit++)
                    if ((context.SubclassMask & (1u << bit)) != 0 &&
                        _itemSubClasses?.Name(itemClass, (uint)bit) is { Length: > 0 } name)
                        names.Add(name);
            requirement = names.Count > 0 ? string.Join(", ", names) : _itemClasses?.Name(itemClass);
            if (context.InventoryMask != 0 && !string.IsNullOrWhiteSpace(requirement))
            {
                var slots = new List<string>();
                for (uint bit = 0; bit < 32; bit++)
                    if ((context.InventoryMask & (1u << (int)bit)) != 0 &&
                        InventoryUiLaw.InventoryTypeName(bit) is { Length: > 0 } slot)
                        slots.Add(slot);
                if (slots.Count > 0) requirement += $" ({string.Join(", ", slots.Distinct())})";
            }
        }
        if (string.IsNullOrWhiteSpace(requirement)) return fallback;
        string template = reason switch
        {
            0x5D => "You need to be in %s", 0x5E => "Requires %s",
            0x1A => "Must have a %s equipped in the main hand",
            0x1B => "Must have a %s equipped in the offhand",
            _ => "Must have a %s equipped",
        };
        return InventoryGlobalString("SPELL_FAILED_" + SpellCastResultNames.Name(reason), template)
            .Replace("%s", requirement);
    }
}
