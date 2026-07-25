using MSUIClient.Formats.Mpq;

namespace MSUIClient.Engine.UI;

/// <summary>
/// WoW's own UI typeface, pulled out of the client's archives.
///
/// WHY THIS EXISTS
///   ImGui ships a small bitmap font (ProggyClean). It is legible, it is free,
///   and it is instantly recognisable as "not a game" - it was the single
///   loudest thing wrong with the first two attempts at this menu, louder than
///   the frame art, because every label on screen is in it.
///
///   `Fonts\FRIZQT__.TTF` is the real 1.12 UI face and it is sitting in
///   fonts.MPQ, 62 KB, alongside MORPHEUS (quest headers), SKURRI (damage) and
///   ARIALN. Reading it costs one archive open at startup.
///
/// WHY IT GOES TO A TEMP FILE
///   Silk's ImGuiController takes a font PATH, not bytes, and it builds its atlas
///   during construction - before GameLoop.Load runs and before MpqMount exists.
///   So the font is extracted in Program.Main, straight from the archive, written
///   once to the temp directory, and the path handed to the window.
///
/// EVERY STEP IS OPTIONAL
///   No archive, no file, no write permission - all of it returns null and the
///   client falls back to ImGui's own font with the old global scale. A missing
///   typeface is a cosmetic outcome, not a startup failure.
/// </summary>
public static class UiFont
{
    /// <summary>The 1.12 UI face. GameFontNormal and every panel label use it.</summary>
    public const string FrizQt = @"Fonts\FRIZQT__.TTF";

    /// <summary>Quest titles and the big headers. Not used yet; here so the path is recorded.</summary>
    public const string Morpheus = @"Fonts\MORPHEUS.TTF";

    /// <summary>
    /// Archives to try, in the order the client's own load order tries them:
    /// patches beat base, higher-numbered patches beat lower. A retexture patch
    /// is allowed to replace the font, and this respects that.
    /// </summary>
    private static readonly string[] Archives =
    [
        "patch-4.MPQ", "patch-3.MPQ", "patch-2.MPQ", "patch.MPQ", "fonts.MPQ",
    ];

    /// <summary>
    /// Extract a font to a temp file and return its path, or null if anything at
    /// all went wrong. Never throws.
    /// </summary>
    public static string? Extract(string clientDataPath, string internalPath = FrizQt)
    {
        string leaf = internalPath.Replace('\\', '_');
        string target = Path.Combine(Path.GetTempPath(), "msui-" + leaf);

        try
        {
            foreach (string name in Archives)
            {
                string archivePath = Path.Combine(clientDataPath, name);
                if (!File.Exists(archivePath)) continue;

                MpqArchive? archive = null;
                try
                {
                    archive = MpqArchive.Open(archivePath);
                    var bytes = archive?.ReadFile(internalPath);
                    if (bytes is null || bytes.Length == 0) continue;

                    File.WriteAllBytes(target, bytes);
                    Console.WriteLine($"[ui-font] {internalPath} from {name}, {bytes.Length:N0} bytes");
                    return target;
                }
                finally
                {
                    archive?.Dispose();
                }
            }

            Console.WriteLine($"[ui-font] {internalPath} not found in any archive - using ImGui's own font");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ui-font] could not extract {internalPath} - {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Pixel height to rasterise at.
    ///
    /// GameFontNormal is FRIZQT at 12 points inside a 21-pixel button, so the
    /// face is a little over half the button height. Keeping that ratio is what
    /// makes a 144x21 button look like a WoW button rather than a WoW button with
    /// someone else's text crammed into it.
    /// </summary>
    public static int SizeFor(float uiScale)
        => (int)MathF.Round(Math.Clamp(12f * uiScale, 10f, 64f));
}
