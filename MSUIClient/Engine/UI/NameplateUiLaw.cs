using System.Numerics;
using MSUIClient.Formats;

namespace MSUIClient.Engine.UI;

/// <summary>Build-5875 overhead identity stack and shared selection-circle colour law.</summary>
public static class NameplateUiLaw
{
    private static readonly (uint Bit, string Text)[] PlayerPrefixes =
    [
        (0x2u, "<AFK>"),
        (0x4u, "<DND>"),
        (0x8u, "<GM>"),
    ];

    public static string NameLine(string name, bool isPlayer, uint playerFlags)
    {
        if (!isPlayer) return name;
        string prefix = string.Concat(PlayerPrefixes
            .Where(entry => (playerFlags & entry.Bit) != 0)
            .Select(entry => entry.Text));
        return prefix.Length == 0 ? name : prefix + name;
    }

    public static string? CreatureSubnameLine(bool isCreature, string? subname)
    {
        if (!isCreature || string.IsNullOrWhiteSpace(subname)) return null;
        return $"<{subname.Trim()}>";
    }

    /// <summary>
    /// Current Benilla's V-plate mode split: neutral joins hostile on the enemy switch, while
    /// friendly units use the independent friendly switch.
    /// </summary>
    public static bool ModeAllows(FactionReaction reaction, bool enemies, bool friends) =>
        reaction == FactionReaction.Friendly ? friends : enemies;

    /// <summary>ALLNAMEPLATES turns both on unless both were already on, then turns both off.</summary>
    public static (bool Enemies, bool Friends) ToggleAll(bool enemies, bool friends)
    {
        bool both = enemies && friends;
        return (!both, !both);
    }

    /// <summary>The reference line stack uses a 1:1 line-pitch-to-font-size ratio.</summary>
    public static float LineBottom(float firstLineBottom, int lineIndex, float fontSize) =>
        firstLineBottom + Math.Max(0, lineIndex) * fontSize;

    /// <summary>
    /// Current Benilla billboard pitch: fixed at close range, then grows proportionally so the
    /// overhead identity remains legible without imposing an artificial world-distance cap.
    /// </summary>
    public static float WorldNamePitch(float distance) =>
        distance > 4f ? distance / 4f * 1.5f * 0.2f : 0.2f;

    /// <summary>
    /// GetSelectionCircleColor's palette. The combat pulse is its first-priority branch and is
    /// shared by the ground selection ring and overhead name.
    /// </summary>
    public static Vector3 SelectionRgb(FactionReaction reaction, bool isPlayer, bool isDead,
        bool combatFlash, uint uptimeMs)
    {
        if (combatFlash)
            return new Vector3(1f, CombatFlashGreen(uptimeMs) / 255f, 0f);
        if (isPlayer)
            return reaction == FactionReaction.Hostile
                ? new Vector3(1f, 0f, 0f)
                : new Vector3(96f / 255f, 96f / 255f, 1f);
        if (isDead) return new Vector3(127f / 255f);
        return reaction switch
        {
            FactionReaction.Hostile => new Vector3(1f, 0f, 0f),
            FactionReaction.Friendly => new Vector3(0f, 1f, 0f),
            _ => new Vector3(1f, 1f, 0f),
        };
    }

    /// <summary>One-second red-to-orange triangle: G=128→0→128, truncating each byte.</summary>
    public static byte CombatFlashGreen(uint uptimeMs)
    {
        uint phase = uptimeMs % 1000u;
        uint distance = phase <= 500u ? 500u - phase : phase - 500u;
        return (byte)(128u * distance / 500u);
    }
}
