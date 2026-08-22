using MSUIClient;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using System.Numerics;

internal static class NameplateClinicalChecks
{
    public static void Run()
    {
        Check(NameplateUiLaw.NameLine("Nico", true, 0) == "Nico" &&
              NameplateUiLaw.NameLine("Nico", true, 0x2 | 0x4 | 0x8) ==
                  "<AFK><DND><GM>Nico" &&
              NameplateUiLaw.NameLine("Nico", true, 0x1 | 0x10 | 0x20) == "Nico" &&
              NameplateUiLaw.NameLine("Wolf", false, 0xe) == "Wolf",
            "overhead player flag slot/order law drift");
        Check(NameplateUiLaw.CreatureSubnameLine(true, " Stable Master ") == "<Stable Master>" &&
              NameplateUiLaw.CreatureSubnameLine(true, "  ") is null &&
              NameplateUiLaw.CreatureSubnameLine(false, "Guild") is null,
            "creature subname line law drift");
        Check(NameplateUiLaw.CombatFlashGreen(0) == 128 &&
              NameplateUiLaw.CombatFlashGreen(250) == 64 &&
              NameplateUiLaw.CombatFlashGreen(499) == 0 &&
              NameplateUiLaw.CombatFlashGreen(500) == 0 &&
              NameplateUiLaw.CombatFlashGreen(750) == 64 &&
              NameplateUiLaw.CombatFlashGreen(1000) == 128,
            "combat-flash red/orange triangle drift");
        Check(NameplateUiLaw.SelectionRgb(FactionReaction.Friendly, false, false, true, 0) ==
                  new Vector3(1f, 128f / 255f, 0f) &&
              NameplateUiLaw.SelectionRgb(FactionReaction.Hostile, false, false, false, 0) ==
                  new Vector3(1f, 0f, 0f),
            "combat flash no longer outranks the shared reaction palette");

        string root = ClientConfig.FindRepoRoot();
        string names = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Hud",
            "GameLoop.Nameplates.cs"));
        string targeting = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Combat",
            "GameLoop.Targeting.cs"));
        string settings = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Settings.cs"));
        string model = SourceText.Read(Path.Combine(root, "MSUIClient", "Engine",
            "GameSettings.cs"));
        Check(names.Contains("NameplateUiLaw.NameLine", StringComparison.Ordinal) &&
              names.Contains("creatureInfo?.Subname", StringComparison.Ordinal) &&
              names.Contains("_attackTargetGuid == unit.Guid", StringComparison.Ordinal),
            "overhead identity render wiring drift");
        Check(names.Contains("Settings.Controls.ShowOwnName", StringComparison.Ordinal) &&
              names.Contains("Settings.Controls.ShowPlayerNames", StringComparison.Ordinal) &&
              names.Contains("Settings.Controls.ShowNpcNames", StringComparison.Ordinal) &&
              settings.Contains("BeginBox(\"nameplates\", \"Nameplates\")",
                  StringComparison.Ordinal) &&
              settings.Contains("Check(\"Player Names\"", StringComparison.Ordinal) &&
              settings.Contains("Check(\"NPC Names\"", StringComparison.Ordinal) &&
              settings.Contains("Check(\"Show Own Name\"", StringComparison.Ordinal) &&
              model.Contains("ShowPlayerNames { get; set; } = true", StringComparison.Ordinal) &&
              model.Contains("ShowNpcNames { get; set; } = true", StringComparison.Ordinal) &&
              model.Contains("ShowOwnName { get; set; } = true", StringComparison.Ordinal),
            "nameplate settings rows or live show gates drift");
        Check(targeting.Contains("NameplateUiLaw.SelectionRgb", StringComparison.Ordinal),
            "ground ring no longer shares the overhead colour selector");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
