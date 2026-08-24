using System.Numerics;

namespace MSUIClient.Engine.UI;

/// <summary>Build-5875 UIDropDownMenuTemplate capsule geometry and art identity.</summary>
public static class DropdownCapsuleUiLaw
{
    public readonly record struct LogicalRect(float X, float Y, float Width, float Height)
    {
        public Vector2 Min => new(X, Y);
        public Vector2 Size => new(Width, Height);
    }

    public readonly record struct TextureSlice(LogicalRect Rect, Vector2 UvMin, Vector2 UvMax);

    public readonly record struct Layout(LogicalRect Frame, TextureSlice[] Art,
        LogicalRect TextBox, Vector2 SelectionRight, LogicalRect Button,
        bool LeftJustified);

    public const float FrameHeight = 32f;
    public const float EndCapWidth = 25f;
    public const float ArtHeight = 64f;
    public const float TextHeight = 10f;
    public const float RowHeight = 16f;
    public const float BorderHeight = 15f;
    public const int MaximumRows = 32;
    public const string Texture =
        @"Interface\Glues\CharacterCreate\CharacterCreate-LabelFrame";
    public const string SelectionFont = "GameFontHighlightSmall";
    public const string ButtonUp = @"Interface\ChatFrame\UI-ChatIcon-ScrollDown-Up";
    public const string ButtonDown = @"Interface\ChatFrame\UI-ChatIcon-ScrollDown-Down";
    public const string ButtonDisabled =
        @"Interface\ChatFrame\UI-ChatIcon-ScrollDown-Disabled";
    public const string ButtonHighlight = @"Interface\Buttons\UI-Common-MouseHilight";
    public const string RowHighlight = @"Interface\QuestFrame\UI-QuestTitleHighlight";
    public const string RowCheck = @"Interface\Buttons\UI-CheckBox-Check";
    public const string ToggleSound = "igMainMenuOptionCheckBoxOn";
    public const string RowSound = "UChatScrollButton";
    public static readonly LogicalRect Check = new(0, -4, 24, 24);
    public static readonly Vector2 RowTextOffset = new(27, 2);

    public static Layout TopRight(float parentWidth, float rightInset, float top,
        float middleWidth, float buttonWidth = 24f, bool leftJustified = false) =>
        At(parentWidth - rightInset - FrameWidth(middleWidth), top, middleWidth,
            buttonWidth, leftJustified);

    public static Layout TopCenter(float parentX, float parentWidth, float top,
        float middleWidth, float buttonWidth = 24f, bool leftJustified = false) =>
        At(parentX + (parentWidth - FrameWidth(middleWidth)) * .5f, top, middleWidth,
            buttonWidth, leftJustified);

    // The left capsule's RIGHT is anchored rightOffset pixels to the right capsule's LEFT.
    public static Layout LeftOf(in Layout right, float rightOffset, float middleWidth,
        float buttonWidth = 24f, bool leftJustified = false) =>
        At(right.Frame.X + rightOffset - FrameWidth(middleWidth), right.Frame.Y,
            middleWidth, buttonWidth, leftJustified);

    public static Layout At(float x, float y, float middleWidth,
        float buttonWidth = 24f, bool leftJustified = false)
    {
        float middle = Math.Max(0, middleWidth);
        float frameWidth = FrameWidth(middle);
        float textWidth = Math.Max(0, middle - EndCapWidth);
        float button = Math.Max(0, buttonWidth);
        return new(
            new(x, y, frameWidth, FrameHeight),
            [
                new(new(0, -17, EndCapWidth, ArtHeight), Vector2.Zero,
                    new(.1953125f, 1)),
                new(new(EndCapWidth, -17, middle, ArtHeight),
                    new(.1953125f, 0), new(.8046875f, 1)),
                new(new(EndCapWidth + middle, -17, EndCapWidth, ArtHeight),
                    new(.8046875f, 0), Vector2.One),
            ],
            new(32, 6, textWidth, TextHeight),
            new(middle + 7, 11),
            new(frameWidth - 16 - button, 1, button, 24),
            leftJustified);
    }

    public static LogicalRect List(in Layout layout, int rowCount)
    {
        float middle = Math.Max(0, layout.Frame.Width - EndCapWidth * 2);
        int rows = Math.Clamp(rowCount, 0, MaximumRows);
        return new(layout.Frame.X + 8, layout.Frame.Y + FrameHeight - 7,
            middle + 32, rows * RowHeight + BorderHeight * 2);
    }

    public static LogicalRect Row(in Layout layout, int index)
    {
        LogicalRect list = List(layout, 0);
        float middle = Math.Max(0, layout.Frame.Width - EndCapWidth * 2);
        int row = Math.Clamp(index, 0, MaximumRows - 1);
        return new(list.X + 17, list.Y + BorderHeight + row * RowHeight,
            middle, RowHeight);
    }

    public static float FrameWidth(float middleWidth) =>
        Math.Max(0, middleWidth) + EndCapWidth * 2;
}
