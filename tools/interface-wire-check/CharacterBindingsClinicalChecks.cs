using MSUIClient;
using MSUIClient.Engine.UI;

internal static class CharacterBindingsClinicalChecks
{
    public static void Run()
    {
        CheckLaw();
        CheckRuntimeSourceFence();
    }

    private static void CheckLaw()
    {
        Check(CharacterBindingsUiLaw.CharacterFileName(0x1234UL) ==
                  "keybindings.character-0000000000001234.json",
            "character binding filename identity drifted");
        Check(CharacterBindingsUiLaw.ConfirmText ==
                  "Really switch to general key bindings?  All key bindings specific to this character will be permanantly deleted." &&
              CharacterBindingsUiLaw.Definition.Type ==
                  "CONFIRM_DELETING_CHARACTER_SPECIFIC_BINDINGS" &&
              CharacterBindingsUiLaw.Definition.HasAccept &&
              !CharacterBindingsUiLaw.Definition.HasCancel &&
              !CharacterBindingsUiLaw.Definition.HideOnEscape &&
              !CharacterBindingsUiLaw.Definition.WhileDead,
            "character binding StaticPopup entry drifted from current Benilla");
        Check(CharacterBindingsUiLaw.Width == 320 &&
              CharacterBindingsUiLaw.TextWidth == 290 &&
              CharacterBindingsUiLaw.ButtonOneX == 26 &&
              CharacterBindingsUiLaw.ButtonTwoX == 167,
            "character binding confirmation escaped shared StaticPopup geometry");

        StaticPopupCoordinatorLaw.Plan shown = StaticPopupCoordinatorLaw.Show(
            StaticPopupCoordinatorLaw.Slots.Empty,
            CharacterBindingsUiLaw.Definition,
            playerDeadOrGhost: false);
        Check(shown.Outcome == StaticPopupCoordinatorLaw.Outcome.Shown &&
              shown.Slot == 1 &&
              CharacterBindingsUiLaw.Visible(shown.Slots)?.Slot == 1,
            "character binding confirmation did not enter a shared popup slot");
        StaticPopupCoordinatorLaw.Plan accepted = StaticPopupCoordinatorLaw.Click(
            shown.Slots, 1, buttonIndex: 1);
        Check(accepted.Outcome == StaticPopupCoordinatorLaw.Outcome.Accepted &&
              accepted.Effects.Count > 0 &&
              accepted.Effects[0].Kind == StaticPopupCoordinatorLaw.EffectKind.Accept &&
              !StaticPopupCoordinatorLaw.AnyVisible(accepted.Slots),
            "character binding confirmation acceptance lifecycle drifted");
    }

    private static void CheckRuntimeSourceFence()
    {
        string root = ClientConfig.FindRepoRoot();
        string bindings = File.ReadAllText(Path.Combine(root, "MSUIClient", "GameLoop",
            "Panels", "GameLoop.Bindings.cs"));
        string page = File.ReadAllText(Path.Combine(root, "MSUIClient", "GameLoop",
            "Panels", "GameLoop.Keybindings.cs"));
        string popup = File.ReadAllText(Path.Combine(root, "MSUIClient", "GameLoop",
            "Panels", "GameLoop.CharacterBindingsPopup.cs"));
        string executor = File.ReadAllText(Path.Combine(root, "MSUIClient", "GameLoop",
            "Hud", "GameLoop.PartyFrames.cs"));

        int loadAccount = bindings.IndexOf("LoadBindingsFromPath(AccountBindingsPath());",
            StringComparison.Ordinal);
        int disableCharacter = bindings.IndexOf("_characterSpecificBindings = false;",
            loadAccount, StringComparison.Ordinal);
        int deleteCharacter = bindings.IndexOf("File.Delete(characterPath)",
            disableCharacter, StringComparison.Ordinal);
        int saveAccount = bindings.IndexOf("SaveBindingsToPath(AccountBindingsPath());",
            deleteCharacter, StringComparison.Ordinal);
        Check(bindings.Contains("File.Exists(characterPath)", StringComparison.Ordinal) &&
              bindings.Contains("CharacterBindingsUiLaw.CharacterFileName(playerGuid)",
                  StringComparison.Ordinal) &&
              bindings.Contains("SaveBindingsToPath(CharacterBindingsPath(_bindingsCharacterGuid))",
                  StringComparison.Ordinal) &&
              loadAccount >= 0 && disableCharacter > loadAccount &&
              deleteCharacter > disableCharacter && saveAccount > deleteCharacter,
            "character binding account-copy/create or load-before-delete ordering drifted");
        Check(page.Contains("_characterSpecificBindings = true;", StringComparison.Ordinal) &&
              page.Contains("CharacterBindingsUiLaw.ToggleSound", StringComparison.Ordinal) &&
              page.Contains("StaticPopupCoordinatorLaw.Show(", StringComparison.Ordinal) &&
              page.Contains("CharacterBindingsUiLaw.Definition", StringComparison.Ordinal),
            "character binding checkbox no longer springs back through shared StaticPopup");
        Check(popup.Contains("CharacterBindingsUiLaw.Visible(_staticPopupSlots)",
                  StringComparison.Ordinal) &&
              popup.Contains("StaticPopupOrigin(visible.Slot", StringComparison.Ordinal) &&
              popup.Contains("CharacterBindingsUiLaw.ButtonOneX", StringComparison.Ordinal) &&
              popup.Contains("CharacterBindingsUiLaw.ButtonTwoX", StringComparison.Ordinal) &&
              executor.Contains("AcceptDeleteCharacterSpecificBindings();",
                  StringComparison.Ordinal),
            "character binding confirmation renderer or accept callback escaped the rule system");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
