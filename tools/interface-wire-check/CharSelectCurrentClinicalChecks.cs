using System.Numerics;
using MSUIClient;
using MSUIClient.Engine.UI;

internal static class CharSelectCurrentClinicalChecks
{
    public static void Run()
    {
        string root = ClientConfig.FindRepoRoot();
        string runtime = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Net.cs"));
        string skin = SourceText.Read(Path.Combine(root, "MSUIClient", "Engine", "UI",
            "WowSkin.cs"));
        Check(runtime.Contains("GlueButton(\"Enter World\"", StringComparison.Ordinal) &&
              runtime.Contains("GlueButton(\"Delete Character\"", StringComparison.Ordinal) &&
              runtime.Contains("GlueButton(\"Back\"", StringComparison.Ordinal) &&
              !runtime.Contains("GlueButton(\"AddOns\"", StringComparison.Ordinal) &&
              !runtime.Contains("AddOns bottom-left", StringComparison.Ordinal),
            "character-select actions or no-consumer AddOns-button policy drift");

        CharSelectUiLaw.ScreenRect host = CharSelectUiLaw.Host(new Vector2(1920f, 1080f));
        CharSelectUiLaw.ScreenRect tuning = CharSelectUiLaw.TuningWindow;
        Check(host.Min == Vector2.Zero && host.Size == new Vector2(1920f, 1080f) &&
              tuning.Min == new Vector2(48f, 48f) && tuning.Size == new Vector2(360f, 0f),
            "character-select host/tuning window geometry law drift");

        foreach (float scale in new[] { .5f, 1f, 1369f / 768f, 2f })
        foreach (int count in Enumerable.Range(1, 10))
        {
            float top = 89f * scale, createTop = (657f - 48.6f - 12f) * scale;
            float pitch = CharSelectUiLaw.RosterPitch(count, createTop - top - 12f * scale, scale);
            float rowScale = pitch / 60f;
            float lastTop = top + (count - 1) * pitch;
            Check(lastTop + 54f * rowScale < createTop &&
                  lastTop + (76.9f - 9.3f) * rowScale < createTop,
                $"roster {count} hit box or highlight overlaps Create at scale {scale}");
            Check(50f * rowScale < pitch, "roster location text spills into next row");
        }

        CharSelectUiLaw.DeleteDialogLayout dialog =
            CharSelectUiLaw.DeleteDialog(new Vector2(1024f, 768f), 1f);
        Check(dialog.Frame.Min == new Vector2(256f, 256f) &&
              dialog.Frame.Size == new Vector2(512f, 256f) &&
              dialog.Alert.Min == new Vector2(268f, 342f) &&
              dialog.Edit.Min == new Vector2(447f, 358f) &&
              dialog.Edit.Size == new Vector2(130f, 32f) &&
              dialog.Okay.Min == new Vector2(306f, 456f) &&
              dialog.Cancel.Min == new Vector2(519f, 456f),
            "character-select delete dialog authored geometry drift");
        Check(runtime.Contains("CharSelectUiLaw.DeleteDialog(disp, s)", StringComparison.Ordinal) &&
              runtime.Contains("CharSelectUiLaw.Host(disp)", StringComparison.Ordinal) &&
              runtime.Contains("CharSelectUiLaw.TuningWindow", StringComparison.Ordinal) &&
              !runtime.Contains("new Vector2(360f, 0f)", StringComparison.Ordinal) &&
              runtime.Contains("CharSelectUiLaw.AcceptSound", StringComparison.Ordinal) &&
              runtime.Contains("CharSelectUiLaw.CancelSound", StringComparison.Ordinal) &&
              runtime.Contains("\"dialog.alert\"", StringComparison.Ordinal) &&
              runtime.Contains("\"chat.input.left\"", StringComparison.Ordinal) &&
              runtime.Contains("\"chat.input.right\"", StringComparison.Ordinal) &&
              skin.Contains("UI-ChatInputBorder-Left.blp", StringComparison.Ordinal) &&
              skin.Contains("UI-ChatInputBorder-Right.blp", StringComparison.Ordinal),
            "character-select delete dialog law, art, or sound wiring drift");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
