using System.Numerics;

namespace MSUIClient.Engine.UI;

public enum ItemRefClickAction
{
    None,
    InsertPlayerName,
    OpenFriendMenu,
    Whisper,
    DressUp,
    InsertItemLink,
    OpenItemTooltip,
}

/// <summary>Current ItemRefTooltip bottom-center anchor and dynamic tooltip furniture.</summary>
public static class ItemRefTooltipUiLaw
{
    public readonly record struct LogicalRect(float X, float Y, float Width, float Height)
    {
        public Vector2 Min => new(X, Y);
        public Vector2 Size => new(Width, Height);
    }

    public const float MinimumWidth = 128;
    public const float MinimumHeight = 64;
    public const float BottomOffset = 80;
    public const float Padding = 16;
    public const float LinePitch = 14;
    public static readonly LogicalRect Close = new(0, 0, 32, 32);

    public static Vector2 Size(float contentWidth, int lines) => new(
        MathF.Max(MinimumWidth, contentWidth + Padding * 2 + 16),
        MathF.Max(MinimumHeight, lines * LinePitch + Padding * 2));

    public static Vector2 Origin(Vector2 logicalDisplay, Vector2 size) =>
        new((logicalDisplay.X - size.X) * .5f,
            logicalDisplay.Y - BottomOffset - size.Y);

    public static Vector2 CloseOrigin(Vector2 size) => new(size.X - 31, 0);

    public static Vector2 LinePosition(Vector2 origin, float logicalY, float scale) =>
        new(origin.X + Padding * scale, origin.Y + logicalY * scale);

    public static ItemRefClickAction PlayerAction(bool shift, bool chatOpen, bool rightButton)
    {
        if (shift)
            return chatOpen ? ItemRefClickAction.InsertPlayerName : ItemRefClickAction.None;
        return rightButton ? ItemRefClickAction.OpenFriendMenu : ItemRefClickAction.Whisper;
    }

    public static ItemRefClickAction ItemAction(bool control, bool shift, bool chatOpen,
        bool linkAvailable)
    {
        if (!linkAvailable) return ItemRefClickAction.None;
        if (control) return ItemRefClickAction.DressUp;
        if (shift)
            return chatOpen ? ItemRefClickAction.InsertItemLink : ItemRefClickAction.None;
        return ItemRefClickAction.OpenItemTooltip;
    }
}
