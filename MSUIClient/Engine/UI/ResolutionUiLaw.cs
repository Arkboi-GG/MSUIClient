namespace MSUIClient.Engine.UI;

/// <summary>One selectable window size, plus what the player needs to recognise it.</summary>
public readonly record struct ResolutionOption(int Width, int Height, bool IsNative);

/// <summary>
/// Turning a monitor's reported video modes into the list the Video Options page offers.
///
/// The page used to carry two continuous drag sliders, one for width and one for height. That was
/// not merely inconvenient: the skinned slider is a hand-drawn InvisibleButton with a direct
/// mouse-X-to-value map and no ctrl+click-to-type, no keyboard stepping and no snapping, so in the
/// normal client there was no way to land on an exact number at all. Reported by a tester,
/// 2026-08-26.
///
/// Pure by construction - it never sees a Silk type, only plain (width, height) pairs - so the
/// windowing layer stays replaceable and this stays testable.
/// </summary>
public static class ResolutionUiLaw
{
    // Matches Program.ApplyStartupSettings' own clamp. Anything the monitor reports outside this
    // could not survive a restart anyway, so it is not worth offering.
    public const int MinimumWidth = 640;
    public const int MinimumHeight = 480;
    public const int MaximumWidth = 7680;
    public const int MaximumHeight = 4320;

    /// <summary>
    /// Offered when the windowing layer cannot enumerate modes. Deliberately conservative and
    /// 16:9-heavy, since that is what the clipped-bar report came from; the native mode is added
    /// separately by <see cref="Build"/>, so an unusual panel still gets its own entry.
    /// </summary>
    public static readonly (int Width, int Height)[] Fallback =
    [
        (1280, 720), (1280, 800), (1366, 768), (1440, 900), (1600, 900), (1600, 1200),
        (1680, 1050), (1920, 1080), (1920, 1200), (2560, 1080), (2560, 1440), (3440, 1440),
        (3840, 2160),
    ];

    public static bool InRange(int width, int height) =>
        width >= MinimumWidth && width <= MaximumWidth &&
        height >= MinimumHeight && height <= MaximumHeight;

    /// <summary>
    /// Deduplicate by (width, height) - a monitor reports one mode per refresh rate and the client
    /// does not choose a refresh rate - drop anything larger than the panel can actually show,
    /// then sort by area so the list reads small to large.
    ///
    /// <paramref name="current"/> is always present in the result even when the monitor does not
    /// report it. A value hand-edited into settings.json, or one carried over from a different
    /// display, must stay selectable; silently dropping it would make the combo snap the player to
    /// a size they never chose the first time they opened the page.
    /// </summary>
    public static IReadOnlyList<ResolutionOption> Build(
        IEnumerable<(int Width, int Height)> modes,
        (int Width, int Height) native,
        (int Width, int Height) current)
    {
        bool hasNative = InRange(native.Width, native.Height);
        var seen = new HashSet<(int, int)>();
        var result = new List<ResolutionOption>();

        void Add(int width, int height)
        {
            if (!InRange(width, height)) return;
            // Never offer a size larger than the panel: the window would be created off-screen
            // and the player could not reach the menu to undo it.
            if (hasNative && (width > native.Width || height > native.Height)) return;
            if (!seen.Add((width, height))) return;
            result.Add(new ResolutionOption(width, height,
                hasNative && width == native.Width && height == native.Height));
        }

        foreach ((int width, int height) in modes ?? []) Add(width, height);
        if (hasNative) Add(native.Width, native.Height);

        // The saved value bypasses the native ceiling on purpose - see the summary.
        if (InRange(current.Width, current.Height) && seen.Add((current.Width, current.Height)))
            result.Add(new ResolutionOption(current.Width, current.Height,
                hasNative && current.Width == native.Width && current.Height == native.Height));

        result.Sort((a, b) =>
        {
            int byArea = ((long)a.Width * a.Height).CompareTo((long)b.Width * b.Height);
            return byArea != 0 ? byArea : a.Width.CompareTo(b.Width);
        });
        return result;
    }

    /// <summary>Index of the saved size, or -1 when the list does not contain it.</summary>
    public static int IndexOf(IReadOnlyList<ResolutionOption> options, (int Width, int Height) current)
    {
        for (int i = 0; i < (options?.Count ?? 0); i++)
            if (options![i].Width == current.Width && options[i].Height == current.Height) return i;
        return -1;
    }

    /// <summary>
    /// "1920 x 1080  (16:9)  native". The aspect is spelled out because it is the number that
    /// actually decides whether the HUD fits, and a player comparing two entries has no other way
    /// to see it.
    /// </summary>
    public static string Label(in ResolutionOption option)
    {
        string aspect = AspectLabel(option.Width, option.Height);
        string native = option.IsNative ? "  native" : "";
        return $"{option.Width} x {option.Height}  ({aspect}){native}";
    }

    /// <summary>Reduce to lowest terms, then name the handful of ratios people recognise.</summary>
    public static string AspectLabel(int width, int height)
    {
        if (width <= 0 || height <= 0) return "?";
        int divisor = Gcd(width, height);
        int w = width / divisor, h = height / divisor;

        // 1280x800 reduces to 8:5 and 3440x1440 to 43:18; both read as noise next to the name
        // everyone actually uses for them.
        return (w, h) switch
        {
            (8, 5) => "16:10",
            (43, 18) => "21:9",
            (64, 27) => "21:9",
            (12, 5) => "24:10",
            (32, 9) => "32:9",
            _ when w > 40 || h > 40 => $"{(double)width / height:0.##}:1",
            _ => $"{w}:{h}",
        };
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0) (a, b) = (b, a % b);
        return a == 0 ? 1 : a;
    }
}
