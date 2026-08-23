using System.Numerics;

namespace MSUIClient.Engine.UI;

/// <summary>
/// Frozen build-5875 pet-spellbook rules. The book consumes the packed list that follows the ten
/// action-bar words in SMSG_PET_SPELLS; it is deliberately not inferred from the bar itself.
/// </summary>
public static class PetSpellBookUiLaw
{
    public readonly record struct LogicalRect(float X, float Y, float Width, float Height);

    public const int SpellsPerPage = 12;
    public const uint SpellActionKind = 0x0100_0000u;
    public const string PetTitle = "Pet";
    public const string DemonTitle = "Demon";
    public const string OpenSound = "igAbilityOpen";
    public const string CloseSound = "igAbilityClose";

    public static readonly LogicalRect PlayerTab = new(15, 419, 128, 64);
    public static readonly LogicalRect PetTab = new(123, 419, 128, 64);
    public static readonly Vector2 TabTextCenterOffset = new(64, 29);
    public static readonly Vector2 TabHitMin = new(15, 13);
    public static readonly Vector2 TabHitMax = new(114, 49);

    /// <summary>The pet add-gate is only SPELL_ATTR_DO_NOT_DISPLAY (0x80).</summary>
    public static bool Eligible(uint attributes) => (attributes & 0x80u) == 0;

    public static uint SpellId(uint packed) => packed & 0x0000_FFFFu;
    public static uint CastWord(uint spellId) => SpellActionKind | (spellId & 0xFFFFu);
    public static bool AutocastAllowed(uint packed) =>
        (packed & PetActionBarUiLaw.AutocastAllowed) != 0;
    public static bool AutocastEnabled(uint packed) =>
        (packed & PetActionBarUiLaw.AutocastEnabled) != 0;

    public static string Title(byte playerClass) => playerClass == 9 ? DemonTitle : PetTitle;

    /// <summary>
    /// Flip the raw book word and mirror bit 30 onto every matching bar word. Matching excludes
    /// both autocast bits, exactly like the client. Returns false if the book word is absent or
    /// does not advertise autocast support.
    /// </summary>
    public static bool TryToggleAutocast(List<uint> book, uint[] bar, uint spellId,
        out bool enabled)
    {
        enabled = false;
        int index = book.FindIndex(word => SpellId(word) == spellId && AutocastAllowed(word));
        if (index < 0) return false;
        uint toggled = book[index] ^ PetActionBarUiLaw.AutocastEnabled;
        book[index] = toggled;
        enabled = AutocastEnabled(toggled);
        uint action = toggled & 0x3FFF_FFFFu;
        for (int i = 0; i < bar.Length; i++)
            if ((bar[i] & 0x3FFF_FFFFu) == action)
                bar[i] = enabled
                    ? bar[i] | PetActionBarUiLaw.AutocastEnabled
                    : bar[i] & ~PetActionBarUiLaw.AutocastEnabled;
        return true;
    }
}
