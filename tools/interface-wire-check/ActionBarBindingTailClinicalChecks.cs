using MSUIClient;
using MSUIClient.Engine;
using MSUIClient.Engine.UI;

internal static class ActionBarBindingTailClinicalChecks
{
    public static void Run()
    {
        Check(ActionBarLockLaw.Toggle(false) && !ActionBarLockLaw.Toggle(true) &&
              ActionBarLockLaw.DragGestureAllowed(false) &&
              !ActionBarLockLaw.DragGestureAllowed(true) &&
              ActionBarLockLaw.ReceiveDragAllowed(false) &&
              !ActionBarLockLaw.ReceiveDragAllowed(true) &&
              ActionBarLockLaw.ShiftClickPickupAllowed(true),
            "LOCK_ACTIONBAR's narrow drag/drop contract drifted");
        Check(!new GameSettings.ControlSettings().LockActionBars,
            "Lock ActionBars must default off");

        MultiActionKeyTransition press = MultiActionBarUiLaw.AdvanceKey(
            armed: false, wasDown: false, isDown: true, typing: false, inWorld: true);
        MultiActionKeyTransition release = MultiActionBarUiLaw.AdvanceKey(
            press.Armed, wasDown: true, isDown: false, typing: false, inWorld: true);
        Check(press.Armed && !press.Fire && !release.Armed && release.Fire,
            "secondary/pet action bindings must arm on down and fire on up");

        string root = ClientConfig.FindRepoRoot();
        string bindings = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop",
            "Panels", "GameLoop.Bindings.cs"));
        string bars = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Hud",
            "GameLoop.ActionBars.cs"));
        string stance = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Hud",
            "GameLoop.StanceBar.cs"));
        string pet = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Pet.cs"));
        string settings = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop",
            "Panels", "GameLoop.Settings.cs"));
        string search = SourceText.Read(Path.Combine(root, "MSUIClient", "Engine", "UI",
            "OptionsSearchUiLaw.cs"));

        Check(bindings.Contains("GameBinding.ShapeshiftButton10", StringComparison.Ordinal) &&
              bindings.Contains("GameBinding.BonusActionButton10", StringComparison.Ordinal) &&
              bindings.Contains("GameBinding.ToggleActionBarLock", StringComparison.Ordinal) &&
              bindings.Contains("Secondary Action Button 10", StringComparison.Ordinal) &&
              bindings.Contains("Control: row.Binding is >= GameBinding.ShapeshiftButton1",
                  StringComparison.Ordinal),
            "action-bar binding registry/default chords drifted");
        Check(bars.Contains("UpdateActionBarTailBindings(typing)", StringComparison.Ordinal) &&
              bars.Contains("ActivateStanceSpell", StringComparison.Ordinal) &&
              bars.Contains("UsePetAction(i, _petGuid, pet)", StringComparison.Ordinal) &&
              bars.Contains("ActionBarLockLaw.Toggle", StringComparison.Ordinal) &&
              bars.Contains("ActionBarLockLaw.DragGestureAllowed", StringComparison.Ordinal) &&
              bars.Contains("ActionBarLockLaw.ReceiveDragAllowed", StringComparison.Ordinal),
            "action-bar tail dispatcher or lock gate drifted");
        Check(stance.Contains("BindingDown(ShapeshiftBinding(i))", StringComparison.Ordinal) &&
              pet.Contains("BindingDown(BonusActionBinding(i))", StringComparison.Ordinal) &&
              pet.Contains("ActionBarLockLaw.DragGestureAllowed", StringComparison.Ordinal) &&
              pet.Contains("ActionBarLockLaw.ReceiveDragAllowed", StringComparison.Ordinal),
            "stance/pet pushed state or lock gate drifted");
        Check(settings.Contains("Check(\"Lock ActionBars\"", StringComparison.Ordinal) &&
              search.Contains("\"Lock ActionBars\"", StringComparison.Ordinal),
            "Lock ActionBars Interface option/search wiring drifted");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
