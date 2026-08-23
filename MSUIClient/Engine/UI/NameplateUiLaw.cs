using System.Numerics;
using MSUIClient.Formats;

namespace MSUIClient.Engine.UI;

/// <summary>Build-5875 overhead identity stack and shared selection-circle colour law.</summary>
public static class NameplateUiLaw
{
    public readonly record struct Bounds(float Left, float Top, float Right, float Bottom)
    {
        public float Width => Right - Left;
        public float Height => Bottom - Top;
        public bool Contains(Vector2 point) =>
            point.X >= Left && point.X <= Right && point.Y >= Top && point.Y <= Bottom;
        public bool Overlaps(Bounds other) =>
            Right > other.Left && Left < other.Right && Bottom > other.Top && Top < other.Bottom;
        public Bounds Offset(float x, float y) =>
            new(Left + x, Top + y, Right + x, Bottom + y);
    }

    public readonly record struct ImageRect(Vector2 Min, Vector2 Max);
    public readonly record struct PlateLayout(float Basis, float Width, float Height,
        float NameSize, float LevelSize, Vector2 SortPoint);

    public const float RangeYards = 20f;

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

    public static PlateLayout Layout(Vector2 display)
    {
        float diagonal = MathF.Sqrt(display.X * display.X + display.Y * display.Y);
        float basis = diagonal <= 1280f ? diagonal : 1280f + (diagonal - 1280f) * .5f;
        return new(basis, MathF.Round(.1f * basis), MathF.Round(.025f * basis),
            MathF.Min(32f, MathF.Round(.01f * basis)),
            MathF.Min(32f, MathF.Round(.0086f * basis)),
            new Vector2(.4f * diagonal, display.Y - .3f * diagonal));
    }

    public static Bounds DesiredPlate(Vector2 screen, PlateLayout layout) =>
        new(screen.X - layout.Width * .5f, screen.Y,
            screen.X + layout.Width * .5f, screen.Y + layout.Height);

    private static float Gx(float value, float basis) => MathF.Round(value * basis);

    public static ImageRect HealthFill(Bounds plate, float basis, float health)
    {
        float left = plate.Left + Gx(.0031f, basis);
        float bottom = plate.Bottom - Gx(.003125f, basis);
        float width = Gx(.0804f, basis) * Math.Clamp(health, 0f, 1f);
        float height = Gx(.007025f, basis);
        return new(new Vector2(left, bottom - height), new Vector2(left + width, bottom));
    }

    public static Vector2 HealthUvMax(float health) => new(Math.Clamp(health, 0f, 1f), 1f);

    public static ImageRect Frame(Bounds plate) =>
        new(new Vector2(plate.Left, plate.Top), new Vector2(plate.Right, plate.Bottom));

    public static Vector2 NameAnchor(Bounds plate, int lineIndex, float fontSize) =>
        new((plate.Left + plate.Right) * .5f,
            LineBottom((plate.Top + plate.Bottom) * .5f, lineIndex, fontSize));

    public static Vector2 LevelAnchor(Bounds plate, float basis) =>
        new(plate.Right - Gx(.0092f, basis), plate.Bottom - Gx(.0071f, basis));

    public static ImageRect Skull(Vector2 levelAnchor, float basis)
    {
        float half = Gx(.01f, basis) * .5f;
        return new(levelAnchor - new Vector2(half), levelAnchor + new Vector2(half));
    }

    public static Vector2 TextPosition(Vector2 anchor, Vector2 extent, bool bottomSeated) =>
        new(anchor.X - extent.X * .5f,
            bottomSeated ? anchor.Y - extent.Y : anchor.Y - extent.Y * .5f);

    public static Vector2 TextShadow(float fontSize) =>
        new(MathF.Max(1f, MathF.Round(fontSize * .1f)));

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
