using System.Text;
using MSUIClient;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;

internal static class CharCreateClinicalChecks
{
    public static void Run()
    {
        GlueStrings strings = GlueStrings.Parse(Encoding.UTF8.GetBytes(
            "CHAR_CUSTOMIZATION1_DESC = \"Skin Tone\";\n" +
            "CHAR_CUSTOMIZATION2_DESC = \"Visage\";\n" +
            "HAIR_HORNS_STYLE = \"Horn Style\";\n" +
            "HAIR_HORNS_COLOR = \"Horn Color\";\n" +
            "FACIAL_HAIR_MARKINGS = \"Markings\";\n"));
        GlueStrings escapes = GlueStrings.Parse(Encoding.UTF8.GetBytes(
            "PARAGRAPH = \"First\\nSecond\\tTabbed\";\n" +
            "QUOTED = \"a \\\"b\\\" c\";\n" +
            "SLASH = \"a\\\\b\";\n" +
            "CODE = getglobal(\"nope\");\n" +
            "BROKEN = \"no close\n"));
        string[] labels = CharCreateUiLaw.DialLabels(strings, "horns", "markings");
        string[] fallback = CharCreateUiLaw.DialLabels(null, "", "");
        Check(labels.SequenceEqual(["Skin Tone", "Visage", "Horn Style", "Horn Color", "Markings"]) &&
              fallback.SequenceEqual(["Skin Color", "Face", "Hair Style", "Hair Color", "Facial Hair"]),
            "character-create customization-token label law drift");
        CharCreateUiLaw.ScreenRect host = CharCreateUiLaw.Host(new System.Numerics.Vector2(1920f, 1080f));
        CharCreateUiLaw.ActionButtons actions = CharCreateUiLaw.Actions(
            new System.Numerics.Vector2(1024f, 768f), 1f, 1f);
        CharCreateUiLaw.ScreenRect tuning = CharCreateUiLaw.TuningWindow;
        Check(host.Min == System.Numerics.Vector2.Zero &&
              host.Size == new System.Numerics.Vector2(1920f, 1080f) &&
              actions.Accept.Min == new System.Numerics.Vector2(814f, 678f) &&
              actions.Accept.Size == new System.Numerics.Vector2(160f, 35f) &&
              actions.Back.Min == new System.Numerics.Vector2(854f, 718f) &&
              actions.Back.Size == new System.Numerics.Vector2(120f, 30f) &&
              tuning.Min == new System.Numerics.Vector2(48f, 48f) &&
              tuning.Size == new System.Numerics.Vector2(400f, 0f),
            "character-create window/action geometry law drift");
        Check(escapes.Get("PARAGRAPH") == "First\nSecond\tTabbed" &&
              escapes.Get("QUOTED") == "a \"b\" c" &&
              escapes.Get("SLASH") == "a\\b" &&
              escapes.Get("CODE") is null && escapes.Get("BROKEN") is null,
            "current GlueStrings assignment/escape parsing drift");
        Check(CharCreateUiLaw.ClassChoiceSound == "gsCharacterCreationClass" &&
              CharCreateUiLaw.LookChoiceSound == "gsCharacterCreationLook" &&
              CharCreateUiLaw.CancelSound == "gsCharacterCreationCancel" &&
              CharCreateUiLaw.CreateSound == "gsCharacterCreationCreateChar",
            "character-create glue sound cue law drift");

        string root = ClientConfig.FindRepoRoot();
        string catalog = SourceText.Read(Path.Combine(root, "MSUIClient", "Formats",
            "CharCreateCatalog.cs"));
        string runtime = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.CharCreate.cs"));
        string law = SourceText.Read(Path.Combine(root, "MSUIClient", "Engine", "UI",
            "CharCreateUiLaw.cs"));
        Check(catalog.Contains("ChrRacesPath", StringComparison.Ordinal) &&
              catalog.Contains("U32(d!, rec, 26)", StringComparison.Ordinal) &&
              catalog.Contains("U32(d!, rec, 27)", StringComparison.Ordinal) &&
              catalog.Contains("U32(d!, rec, 28)", StringComparison.Ordinal) &&
              catalog.Contains("WdbcString", StringComparison.Ordinal) &&
              runtime.Contains("_ccCatalog?.HairCustomization(_cc.Race)",
                  StringComparison.Ordinal) &&
              runtime.Contains("_ccCatalog?.FacialHairCustomization(_cc.Race, _cc.Sex)",
                  StringComparison.Ordinal) &&
              runtime.Contains("CharCreateUiLaw.DialLabels", StringComparison.Ordinal) &&
              runtime.Contains("PlayCharCreateSound(CharCreateUiLaw.ClassChoiceSound)",
                  StringComparison.Ordinal) &&
              runtime.Contains("PlayCharCreateSound(CharCreateUiLaw.LookChoiceSound)",
                  StringComparison.Ordinal) &&
              runtime.Contains("PlayCharCreateSound(CharCreateUiLaw.CancelSound)",
                  StringComparison.Ordinal) &&
              runtime.Contains("PlayCharCreateSound(CharCreateUiLaw.CreateSound)",
                  StringComparison.Ordinal),
            "ChrRaces captions or glue-screen sound gestures are unwired");
        Check(law.Contains("internal static class CreateTune", StringComparison.Ordinal) &&
              law.Contains("TowerX = 28f", StringComparison.Ordinal) &&
              law.Contains("PanelFactionH = 160f", StringComparison.Ordinal) &&
              law.Contains("DialArrowW = 30.9f", StringComparison.Ordinal) &&
              runtime.Contains("CharCreateUiLaw.Host(disp)", StringComparison.Ordinal) &&
              runtime.Contains("CharCreateUiLaw.Actions(disp, s, GlueTune.ButtonHeightMul)",
                  StringComparison.Ordinal) &&
              runtime.Contains("CharCreateUiLaw.TuningWindow", StringComparison.Ordinal) &&
              !runtime.Contains("SetNextWindowPos(Vector2.Zero", StringComparison.Ordinal) &&
              !runtime.Contains("new Vector2(400f, 0f)", StringComparison.Ordinal) &&
              !runtime.Contains("internal static class CreateTune", StringComparison.Ordinal),
            "character-create geometry must be owned by CharCreateUiLaw, not the renderer");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
