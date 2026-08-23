using MSUIClient;
using MSUIClient.Engine.UI;

internal static class LootFrameClinicalChecks
{
    public static void Run()
    {
        LootFrameUiLaw.OpenPresentation empty = LootFrameUiLaw.OnShow(1, 0, 0);
        LootFrameUiLaw.OpenPresentation emptyFishing = LootFrameUiLaw.OnShow(3, 0, 0);
        LootFrameUiLaw.OpenPresentation fishing = LootFrameUiLaw.OnShow(3, 1, 0);
        LootFrameUiLaw.OpenPresentation corpse = LootFrameUiLaw.OnShow(1, 1, 0);
        LootFrameUiLaw.OpenPresentation coins = LootFrameUiLaw.OnShow(3, 0, 1);
        Check(empty.SoundCue == LootFrameUiLaw.EmptyOpenSound &&
              empty.OverlayPath == LootFrameUiLaw.CorpseOverlay &&
              emptyFishing == empty &&
              fishing.SoundCue == LootFrameUiLaw.FishingOpenSound &&
              fishing.OverlayPath == LootFrameUiLaw.FishingOverlay &&
              corpse.SoundCue is null && corpse.OverlayPath == LootFrameUiLaw.CorpseOverlay &&
              coins.SoundCue == LootFrameUiLaw.FishingOpenSound,
            "LootFrame OnShow empty/fishing precedence drift");

        string root = ClientConfig.FindRepoRoot();
        string runtime = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Loot.cs"));
        Check(runtime.Contains("PlayUiSound(cue, LootFrameUiLaw.SoundCategory)",
                  StringComparison.Ordinal) &&
              runtime.Contains("UiPanelFrameOrigin(UiPanelOwnershipRegistry[9], s)",
                  StringComparison.Ordinal) &&
              runtime.Contains("new Vector2(16f, 12f) * s", StringComparison.Ordinal) &&
              runtime.Contains(".OverlayPath", StringComparison.Ordinal) &&
              !runtime.Contains("DrawArt(dl, @\"Interface\\TargetingFrame\\TargetDead\"",
                  StringComparison.Ordinal),
            "LootFrame open presentation bypasses its law");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
