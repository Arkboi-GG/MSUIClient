namespace MSUIClient.Engine.UI;

public enum BottomMultiActionBar { Left, Right }

public static class MultiActionBarUiLaw
{
    public const int ButtonsPerBar = 12;
    public const int BottomLeftBase = 60;
    public const int BottomRightBase = 48;
    public const float FrameWidth = 500;
    public const float FrameHeight = 38;
    public const float ButtonSize = 36;
    public const float ButtonStep = 42;
    public const float BottomLeftRise = 17;
    public const float BottomBarGap = 10;

    public static int Base(BottomMultiActionBar bar) => bar == BottomMultiActionBar.Left
        ? BottomLeftBase : BottomRightBase;

    public static int WireSlot(BottomMultiActionBar bar, int buttonIndex) =>
        Base(bar) + Math.Clamp(buttonIndex, 0, ButtonsPerBar - 1);

    public static bool ShowEmptyWell(bool cursorPayloadHeld) => cursorPayloadHeld;

    public static bool UseOnKeyRelease(bool wasDown, bool isDown, bool typing, bool inWorld) =>
        wasDown && !isDown && !typing && inWorld;
}
