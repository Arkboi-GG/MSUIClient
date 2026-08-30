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
        Check(NameplateUiLaw.ModeAllows(FactionReaction.Hostile, true, false) &&
              NameplateUiLaw.ModeAllows(FactionReaction.Neutral, true, false) &&
              !NameplateUiLaw.ModeAllows(FactionReaction.Friendly, true, false) &&
              NameplateUiLaw.ModeAllows(FactionReaction.Friendly, false, true) &&
              NameplateUiLaw.ToggleAll(false, true) == (true, true) &&
              NameplateUiLaw.ToggleAll(true, true) == (false, false),
            "enemy/friendly/all V-plate mode law drift");
        Check(NameplateUiLaw.WorldNamePitch(0f) == 0.2f &&
              NameplateUiLaw.WorldNamePitch(4f) == 0.2f &&
              MathF.Abs(NameplateUiLaw.WorldNamePitch(8f) - 0.6f) < .0001f,
            "depth-tested world-name distance/pitch law drift");
        NameplateUiLaw.PlateLayout layout = NameplateUiLaw.Layout(new Vector2(1024, 768));
        NameplateUiLaw.Bounds plate = NameplateUiLaw.DesiredPlate(new Vector2(500, 200), layout);
        NameplateUiLaw.ImageRect health = NameplateUiLaw.HealthFill(plate, layout.Basis, .5f);
        Check(layout == new NameplateUiLaw.PlateLayout(1280, 128, 32, 13, 11,
                  new Vector2(512, 384)) &&
              plate == new NameplateUiLaw.Bounds(436, 200, 564, 232) &&
              health == new NameplateUiLaw.ImageRect(
                  new Vector2(440, 219), new Vector2(491.5f, 228)) &&
              NameplateUiLaw.NameAnchor(plate, 0, 1, 13) == new Vector2(500, 216) &&
              NameplateUiLaw.NameAnchor(plate, 0, 2, 13) == new Vector2(500, 203) &&
              NameplateUiLaw.NameAnchor(plate, 1, 2, 13) == new Vector2(500, 216) &&
              NameplateUiLaw.LevelAnchor(plate, 1280) == new Vector2(552, 223) &&
              NameplateUiLaw.Skull(new Vector2(552, 223), 1280) ==
                  new NameplateUiLaw.ImageRect(
                      new Vector2(545.5f, 216.5f), new Vector2(558.5f, 229.5f)) &&
              NameplateUiLaw.TextPosition(new Vector2(500, 216), new Vector2(100, 20), true) ==
                  new Vector2(450, 196) &&
              NameplateUiLaw.TextShadow(13) == new Vector2(1),
            "V-plate authored screen geometry drift");

        string root = ClientConfig.FindRepoRoot();
        string names = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Hud",
            "GameLoop.Nameplates.cs"));
        string targeting = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Combat",
            "GameLoop.Targeting.cs"));
        string settings = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Settings.cs"));
        string model = SourceText.Read(Path.Combine(root, "MSUIClient", "Engine",
            "GameSettings.cs"));
        string renderer = SourceText.Read(Path.Combine(root, "MSUIClient", "World", "Units",
            "WorldNameRenderer.cs"));
        string program = SourceText.Read(Path.Combine(root, "MSUIClient", "Program.cs"));
        Check(names.Contains("NameplateUiLaw.NameLine", StringComparison.Ordinal) &&
              names.Contains("creatureInfo?.Subname", StringComparison.Ordinal) &&
              names.Contains("_attackTargetGuid == unit.Guid", StringComparison.Ordinal),
            "overhead identity render wiring drift");
        Check(names.Contains("BindingDown(GameBinding.ToggleEnemyNameplates)",
                  StringComparison.Ordinal) &&
              names.Contains("BindingDown(GameBinding.ToggleFriendlyNameplates)",
                  StringComparison.Ordinal) &&
              names.Contains("BindingDown(GameBinding.ToggleAllNameplates)",
                  StringComparison.Ordinal) &&
              names.Contains("NameplateUiLaw.ModeAllows", StringComparison.Ordinal) &&
              names.Contains("NameplateUiLaw.Layout", StringComparison.Ordinal) &&
              names.Contains("NameplateUiLaw.HealthFill", StringComparison.Ordinal) &&
              names.Contains("NameplateUiLaw.TextPosition", StringComparison.Ordinal) &&
              !names.Contains("new Vector2", StringComparison.Ordinal) &&
              !names.Contains("control && InputKeyDown", StringComparison.Ordinal),
            "V-plate modes escaped the binding table or reaction gate");
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
        Check(targeting.Contains("SelectionRingLaw.TargetRgb", StringComparison.Ordinal),
            "ground ring no longer uses its dedicated friendly-blue/neutral-yellow palette");
        Check(names.Contains("WouldHaveActiveNameplate(unit, display)", StringComparison.Ordinal) &&
              names.Contains("_worldNames.Render(_window.Camera", StringComparison.Ordinal) &&
              names.Contains("NameplateUiLaw.WorldNamePitch(distance)", StringComparison.Ordinal) &&
              renderer.Contains("_gl.Enable(EnableCap.DepthTest)", StringComparison.Ordinal) &&
              renderer.Contains("_gl.DepthMask(false)", StringComparison.Ordinal) &&
              renderer.Contains("camera.RelativeViewProjection", StringComparison.Ordinal) &&
              program.IndexOf("RenderWorldUnitNames();", StringComparison.Ordinal) >
              program.IndexOf("_liquidRenderMilliseconds", StringComparison.Ordinal),
            "overhead names escaped the late depth-tested world geometry pass");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
