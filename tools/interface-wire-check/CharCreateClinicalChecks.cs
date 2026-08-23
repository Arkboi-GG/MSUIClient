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
        string[] labels = CharCreateUiLaw.DialLabels(strings, "horns", "markings");
        string[] fallback = CharCreateUiLaw.DialLabels(null, "", "");
        Check(labels.SequenceEqual(["Skin Tone", "Visage", "Horn Style", "Horn Color", "Markings"]) &&
              fallback.SequenceEqual(["Skin Color", "Face", "Hair Style", "Hair Color", "Facial Hair"]),
            "character-create customization-token label law drift");
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
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
