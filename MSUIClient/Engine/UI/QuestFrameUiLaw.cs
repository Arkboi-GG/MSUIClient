using System.Numerics;

namespace MSUIClient.Engine.UI;

public enum QuestNpcPanel { None, Greeting, Detail, Progress, Reward }

/// <summary>Authored QuestFrame.xml geometry and bounded panel behavior.</summary>
public static class QuestFrameUiLaw
{
    public const float Width = 384f;
    public const float Height = 512f;
    public const float ScrollX = 23f;
    public const float ScrollY = 81f;
    public const float ScrollWidth = 300f;
    public const float ScrollHeight = 334f;
    public const float ScrollStep = 20f;
    public const float ScrollTrackHeight = 302f;
    public const float ScrollThumbHeight = 16f;
    public const float ContentWidth = 300f;
    public const int MaxTitles = 10;
    public const int MaxItems = 6;
    public const float ItemWidth = 147f;
    public const float ItemHeight = 41f;
    public const float ItemIcon = 39f;

    public static Vector2 CloseMin => new(326, 15); // center at TOPRIGHT(-42,-31)
    public static Vector2 ItemGridOffset(int index) =>
        new((index & 1) == 0 ? 0 : ItemWidth + 1f, (index / 2) * (ItemHeight + 2f));
    public static float ClampScroll(float value, float contentHeight) =>
        Math.Clamp(value, 0, Math.Max(0, contentHeight - ScrollHeight));
    public static float ScrollThumbY(float value, float contentHeight)
    {
        float range = Math.Max(0, contentHeight - ScrollHeight);
        return range <= 0 ? 0 : value / range * (ScrollTrackHeight - ScrollThumbHeight);
    }
    public static bool RewardCompleteEnabled(int choiceCount, int selectedChoice) =>
        choiceCount == 0 || selectedChoice >= 0 && selectedChoice < choiceCount;
}
