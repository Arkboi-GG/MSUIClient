using MSUIClient;
using MSUIClient.Engine.UI;
using System.Numerics;

internal static class BagStateArtClinicalChecks
{
    public static void Run()
    {
        Check(InventoryUiLaw.KeyringStateTexture(false) ==
                  @"Interface\Buttons\UI-Button-KeyRing" &&
              InventoryUiLaw.KeyringStateTexture(true) ==
                  @"Interface\Buttons\UI-Button-KeyRing-Down" &&
              InventoryUiLaw.KeyringHighlightTexture ==
                  @"Interface\Buttons\UI-Button-KeyRing-Highlight" &&
              InventoryUiLaw.KeyringUvMaximum == new Vector2(0.5625f, 0.609375f),
            "keyring normal/pushed/highlight texture law drift");

        string root = ClientConfig.FindRepoRoot();
        string inventory = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Inventory.cs"));
        Check(inventory.Contains("bool keyringPushed = ImGui.IsItemActive();",
                  StringComparison.Ordinal) &&
              inventory.Contains("InventoryUiLaw.KeyringPushedTexture", StringComparison.Ordinal) &&
              inventory.Contains("InventoryUiLaw.KeyringHighlightTexture", StringComparison.Ordinal) &&
              inventory.Contains("_gameplayArt.AdditiveHandle", StringComparison.Ordinal),
            "keyring pushed/highlight render wiring drift");
        Check(inventory.Contains("uint depress = _gameplayArt.Handle(@\"Interface\\Buttons\\UI-Quickslot-Depress\")",
                  StringComparison.Ordinal) &&
              inventory.Contains("if (ImGui.IsItemActive())", StringComparison.Ordinal) &&
              inventory.Contains("dl.AddImage((nint)depress, min, max)", StringComparison.Ordinal),
            "container item-slot UI-Quickslot-Depress render wiring drift");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
