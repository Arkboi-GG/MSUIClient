namespace MSUIClient.Engine.UI;

/// <summary>Separates text/modal input ownership from ordinary focused UI controls.</summary>
public static class GameplayInputLaw
{
    public static bool BlocksMovement(bool wantsKeyboard, bool wantsTextInput,
        bool settingsModalOpen, bool bindingCaptureActive)
    {
        // ImGui sets wantsKeyboard for focused buttons as well as editors.  Buttons must not
        // interrupt locomotion; text entry and explicit modal capture still must.
        _ = wantsKeyboard;
        return wantsTextInput || settingsModalOpen || bindingCaptureActive;
    }
}
