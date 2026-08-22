using MSUIClient;
using MSUIClient.Engine.UI;

internal static class HardwareCursorClinicalChecks
{
    public static void Run()
    {
        HardwareCursorImage image = HardwareCursorLaw.FromBgra(
            [0x10, 0x20, 0x30, 0x40, 0xA0, 0xB0, 0xC0, 0xD0], 2, 1);
        Check(image.Width == 2 && image.Height == 1 && image.Rgba.SequenceEqual(
                  new byte[] { 0x30, 0x20, 0x10, 0x40, 0xC0, 0xB0, 0xA0, 0xD0 }),
            "hardware cursor BGRA-to-RGBA conversion drift");
        CheckThrows(() => HardwareCursorLaw.FromBgra(new byte[7], 2, 1));

        string root = ClientConfig.FindRepoRoot();
        string window = SourceText.Read(Path.Combine(root, "MSUIClient", "Engine",
            "ClientWindow.cs"));
        string inventory = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop",
            "Panels", "GameLoop.Inventory.cs"));
        string hud = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Combat",
            "GameLoop.CombatFeedback.cs"));
        Check(window.Contains("CursorType.Custom", StringComparison.Ordinal) &&
              window.Contains("cursor.HotspotX = 0", StringComparison.Ordinal) &&
              window.Contains("cursor.HotspotY = 0", StringComparison.Ordinal) &&
              inventory.Contains("_window.UseHardwareCursor(stem, resolved)",
                  StringComparison.Ordinal) &&
              inventory.Contains("if (TryUseHardwareCursor(stem)) return;",
                  StringComparison.Ordinal) &&
              hud.Contains("TryUseHardwareCursor(WorldCursorKind.Point.ToString())",
                  StringComparison.Ordinal),
            "hardware cursor authority or foreground fallback is unwired");
    }

    private static void CheckThrows(Action action)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        throw new InvalidDataException("malformed hardware cursor pixels were accepted");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
