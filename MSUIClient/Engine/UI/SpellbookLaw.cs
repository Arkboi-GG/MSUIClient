using MSUIClient.Formats;
using System.Numerics;

namespace MSUIClient.Engine.UI;

/// <summary>Pure 1.12 spellbook classification/sorting rules used by the live book and validators.</summary>
public static class SpellbookLaw
{
    public const string GeneralName = "General";
    public const string GeneralIcon = @"Interface\Icons\Ability_Kick";
    // SpellBookFrame.xml declares eight right-rail skill-line tab seats.
    public const int MaxClassTabs = 8;

    // Frozen SpellBookFrame sound kits: open/close on show/hide, page-turn on both page clicks.
    // "igAbiliityPageTurn" is the real registered kit name (SoundEntries id 836), not a typo.
    public const string OpenSound = "igSpellBookOpen";
    public const string CloseSound = "igSpellBookClose";
    public const string PageTurnSound = "igAbiliityPageTurn";

    // Build-5875 Fonts.xml + SpellButtonTemplate, read directly from patch.MPQ. Keep these as
    // SOURCE semantics. The renderer-specific font-height conversion is deliberately not here.
    public const float NameFontHeight = 12f;
    public const float RankFontHeight = 10f;
    public const float ButtonSize = 37f;
    public const float NameWidth = 103f;
    public const int NameMaxLines = 3;
    public const float NameAnchorX = 4f;
    public const float NameAnchorYWithRank = 4f;
    public const float NameAnchorYWithoutRank = 2f;
    public const float RankWidth = 79f;
    public const float RankBoxHeight = 18f;
    public const float RankAnchorY = 4f;
    // CooldownFrameTemplate: 36x36, centered on the 37x37 spell button at CENTER(0,-1).
    // FrameXML y is up while screen y is down, hence the +1.5 screen-space top offset.
    public const float CooldownSize = 36f;
    public static readonly Vector2 CooldownOffset = new(0.5f, 1.5f);
    public const string CheckedTexture = @"Interface\Buttons\CheckButtonHilight";
    public const uint ModShapeshiftAura = 36;
    public const uint PassiveNameColor = 0xff00a3c4; // RGB(.77,.64,0) => (196,163,0).
    public const uint RankColor = 0xff003359; // RGB(0.35, 0.20, 0), ImGui ABGR packing.

    public static bool Eligible(in SpellInfo spell) => spell.InSpellbook;

    public static Vector2 CooldownMin(Vector2 buttonMin, float scale) =>
        buttonMin + CooldownOffset * scale;

    public static Vector2 CooldownMax(Vector2 buttonMin, float scale) =>
        CooldownMin(buttonMin, scale) + new Vector2(CooldownSize * scale);

    /// <summary>The form id authored by the first SPELL_AURA_MOD_SHAPESHIFT effect lane.</summary>
    public static uint? ShapeshiftForm(in SpellInfo spell)
    {
        if (spell.AuraIds is null || spell.EffectMiscValues is null) return null;
        int count = Math.Min(spell.AuraIds.Length, spell.EffectMiscValues.Length);
        for (int i = 0; i < count; i++)
            if (spell.AuraIds[i] == ModShapeshiftAura && spell.EffectMiscValues[i] > 0)
                return (uint)spell.EffectMiscValues[i];
        return null;
    }

    /// <summary>
    /// Frozen IsCurrentCast semantics: a matching active form or this spell's open profession
    /// window. An ordinary in-flight cast is deliberately not an input to this predicate.
    /// </summary>
    public static bool Checked(in SpellInfo spell, byte activeForm, bool professionWindowOpen) =>
        professionWindowOpen ||
        (activeForm != 0 && ShapeshiftForm(spell) == activeForm);

    /// <summary>MacroFrame_AddMacroLine's exact spellbook payload (the caller appends it raw).</summary>
    public static string? MacroCastLine(in SpellInfo spell)
    {
        if (spell.Passive) return null;
        return string.IsNullOrWhiteSpace(spell.Rank)
            ? $"/cast {spell.Name}"
            : $"/cast {spell.Name}({spell.Rank})";
    }

    public static int LeadingRankNumber(string? rank)
    {
        if (string.IsNullOrEmpty(rank)) return 0;
        int i = 0;
        while (i < rank.Length && !char.IsDigit(rank[i])) i++;
        int value = 0;
        while (i < rank.Length && char.IsDigit(rank[i]))
        {
            value = checked(value * 10 + rank[i] - '0');
            i++;
        }
        return value;
    }
}
