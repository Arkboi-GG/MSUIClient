namespace MSUIClient.Engine.UI;

/// <summary>Build-5875 TOGGLEUI's exact-modifier and edge-trigger law.</summary>
public static class UiHideLaw
{
    public static bool ToggleFired(bool chordDown, bool wasDown, bool typing) =>
        chordDown && !wasDown && !typing;

    public static bool ToggleFired(bool boundKeyDown, bool altDown, bool controlDown,
        bool shiftDown, bool superDown, bool wasDown, bool typing) =>
        boundKeyDown && altDown && !controlDown && !shiftDown && !superDown &&
        !wasDown && !typing;
}
