using System.Numerics;

namespace MSUIClient.Engine.UI;

/// <summary>Build-5875 StackSplitFrame geometry and spinner state transitions.</summary>
public static class StackSplitUiLaw
{
    public static readonly Vector2 FrameSize = new(172f, 96f);
    public static readonly Vector2 PlateUvMax = new(.671875f, .75f);
    public static readonly Vector2 ArrowSize = new(16f, 16f);
    public static readonly Vector2 LeftArrow = new(11f, 22f);
    public static readonly Vector2 RightArrow = new(150f, 22f);
    public static readonly Vector2 CountRightEdge = new(122f, 24f);
    public static readonly Vector2 ButtonSize = new(64f, 24f);
    public static readonly Vector2 OkayButton = new(19f, 52f);
    public static readonly Vector2 CancelButton = new(91f, 52f);

    public const string PlatePath = @"Interface\MoneyFrame\UI-MoneyFrame";
    public const string LeftArrowStem = @"Interface\MoneyFrame\Arrow-Left";
    public const string RightArrowStem = @"Interface\MoneyFrame\Arrow-Right";

    /// <summary>BOTTOMRIGHT of the frame is attached to TOPRIGHT of its owner item button.</summary>
    public static Vector2 Origin(Vector2 ownerTopRight, float scale) =>
        ownerTopRight - FrameSize * scale;

    public static int Clamp(int count, int maximum) => Math.Clamp(count, 1, Math.Max(1, maximum));

    public static (int Count, bool Typed) AppendDigit(
        int count, bool typed, int digit, int maximum)
    {
        if (digit is < 0 or > 9) return (Clamp(count, maximum), typed);
        long candidate = typed ? (long)count * 10 + digit : digit;
        if (candidate < 1) return (Clamp(count, maximum), typed);
        return ((int)Math.Clamp(candidate, 1, Math.Max(1, maximum)), true);
    }

    public static (int Count, bool Typed) Backspace(int count, int maximum)
    {
        int shortened = count / 10;
        return shortened < 1 ? (1, false) : (Clamp(shortened, maximum), true);
    }
}
