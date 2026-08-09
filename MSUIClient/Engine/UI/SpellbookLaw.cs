using MSUIClient.Formats;

namespace MSUIClient.Engine.UI;

/// <summary>Pure 1.12 spellbook classification/sorting rules used by the live book and validators.</summary>
public static class SpellbookLaw
{
    public const string GeneralName = "General";
    public const string GeneralIcon = @"Interface\Icons\Ability_Kick";
    public const int MaxClassTabs = 4;

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
    public const uint PassiveNameColor = 0xff00a3c4; // RGB(.77,.64,0) => (196,163,0).
    public const uint RankColor = 0xff003359; // RGB(0.35, 0.20, 0), ImGui ABGR packing.

    public static bool Eligible(in SpellInfo spell) => spell.InSpellbook;

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
