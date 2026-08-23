using MSUIClient.Formats;

namespace MSUIClient.Engine.UI;

/// <summary>Data-derived CharacterCreate captions; rendering remains owned by the glue screen.</summary>
public static class CharCreateUiLaw
{
    public const string ClassChoiceSound = "gsCharacterCreationClass";
    public const string LookChoiceSound = "gsCharacterCreationLook";
    public const string CancelSound = "gsCharacterCreationCancel";
    public const string CreateSound = "gsCharacterCreationCreateChar";
    public const string SoundCategory = "ui.glue.char-create";

    public static string[] DialLabels(GlueStrings? strings, string hairToken,
        string facialHairToken) =>
    [
        strings?.Text("CHAR_CUSTOMIZATION1_DESC", "Skin Color") ?? "Skin Color",
        strings?.Text("CHAR_CUSTOMIZATION2_DESC", "Face") ?? "Face",
        strings?.Text($"HAIR_{Normalize(hairToken)}_STYLE", "Hair Style") ?? "Hair Style",
        strings?.Text($"HAIR_{Normalize(hairToken)}_COLOR", "Hair Color") ?? "Hair Color",
        strings?.Text($"FACIAL_HAIR_{Normalize(facialHairToken)}", "Facial Hair") ?? "Facial Hair",
    ];

    private static string Normalize(string token) =>
        string.IsNullOrWhiteSpace(token) ? "NORMAL" : token.Trim().ToUpperInvariant();
}
