using System.Numerics;

namespace MSUIClient.Engine.UI;

public enum OptionsSearchPage { Video, Interface, Sound }

public readonly record struct OptionsSearchEntry(OptionsSearchPage Page, string Label);

public readonly record struct OptionsSearchGroup(
    OptionsSearchPage Page, int BestScore, IReadOnlyList<OptionsSearchEntry> Entries);

/// <summary>
/// Current Benilla's SettingsPanel search scoring and MSUI's rule-owned search seats. The
/// results navigate into MSUI's preserved three-page options presentation; they do not alter its
/// independent scaling, remembered sizes, or live-apply semantics.
/// </summary>
public static class OptionsSearchUiLaw
{
    public const float BoxWidth = 350f;
    public const float BoxHeight = 22f;
    public const float BoxTop = 30f;
    public const float MinimumBoxWidth = 120f;
    public const float SideMargin = 18f;
    public const float ClearSize = 17f;
    public const float ClearRight = 3f;
    public const float TextLeft = 5f;
    public const float TextRight = 20f;
    public const float BelowBoxGap = 8f;
    public const float GroupHeight = 45f;
    public const float ResultHeight = 26f;
    public const float ResultGap = 9f;
    public const float GroupTextLeft = 16f;
    public const float ResultTextLeft = 28f;
    public const string Placeholder = "Search";
    public const string ResultsTitle = "Search Results";
    public const string NoResults = "No results found.";

    public readonly record struct Rect(float X, float Y, float Width, float Height)
    {
        public Vector2 Min => new(X, Y);
        public Vector2 Size => new(Width, Height);
    }

    private static readonly string[] VideoLabels =
    [
        "Quality", "Display", "Painterly mode", "View distance", "Environment detail",
        "Ground clutter", "Water", "Lighting and sky",
        "Alpha lift", "Apex ahead", "Fade out", "Full-strength speed", "V length",
        "V width", "Wake strength", "Water depth cutoff", "Wavefronts", "World lock",
        "Alpha cutoff", "Ambient amount", "Ambient strength", "Animation FPS",
        "Anisotropic filtering", "Authored water colours (Light.dbc)  [KNOWN BAD]",
        "Baked interior light - buildings (MOCV)", "Baked interior light - props (MODD)",
        "Baked terrain shadows", "Band dither", "Base brightness", "Brightness",
        "Building alpha cutoff", "Building detail", "Building distance", "Calm completes",
        "Calm starts", "Canvas grain", "Clutter density", "Clutter distance",
        "Colour bands", "Colour richness", "Deep darkening", "Depth rate", "Detail",
        "Distance calm", "Doodad alpha cut", "Doodad distance", "Draw buildings",
        "Draw distance fog", "Draw doodads", "Draw the sky gradient", "Draw WMO liquid",
        "Fade end", "Fade follows distance", "Fade start", "Fade start (fraction)",
        "Far plane", "Field of view", "Flat cull bounds", "Fog fully opaque", "Fog starts",
        "Force two-sided", "Frame blend", "Frustum culling", "Fullscreen",
        "Game hours per minute", "GPU instancing", "Highlight gain", "Hour",
        "Impostor max verts", "Ink lines", "Ink threshold", "Inside margin",
        "Instance cap", "Interface scale", "Interior brightness",
        "Interior cull (from outside)", "Interior doorway glow", "Light and shade",
        "Link the two interior brightnesses", "Match camera far plane to fog", "Max per cell",
        "Multisample count", "Multisampling", "Native-resolution world canvas", "Near plane",
        "No-doodad mask", "Object detail", "Occlusion cull exterior (BVH)",
        "Occlusion min distance", "Opacity (deep)", "Painterly HUD", "Painterly world",
        "Per-cell layer map", "Prop interior brightness", "Render water",
        "Rescatter after moving", "Scale", "Scale jitter", "Shell near-guard",
        "Shoreline alpha", "Shoreline width", "Show grass and ground effects", "Silhouettes",
        "Skip cells under water", "Skip terrain holes", "Sky band 1", "Sky band 2",
        "Sky horizon band", "Sky sheen (grazing)", "Stop submitting past fog",
        "Stream only nearby doodads", "Sun amount", "Sun strength", "Sun/shade colour",
        "Swap distance-only city shells", "Texture brightness", "Texture contrast",
        "Texture scale (tiling)", "Textured frame (Blizzard UI art)", "Time-of-day lighting",
        "Unit contact shadows", "Value flattening", "View distance", "VSync", "Walking wake",
        "Water detail", "Wave amplitude", "Wave speed", "Wind speed", "Wind strength",
        "Window height", "Window width", "World canvas height",
    ];

    private static readonly string[] InterfaceLabels =
    [
        "Mouse", "Nameplates", "Chat Bubbles", "CRPG / RTS", "Camera", "Current keys",
        "Camera collision", "Camera Following Style", "Collision clearance", "Cut buildings away in the free view",
        "Eye height", "Free-view camera collides with the world", "Invert vertical look",
        "Lock ActionBars", "Max camera distance", "Mouse sensitivity", "NPC Names", "Party chat bubbles",
        "Player Names", "Raw cursor", "Restore speed", "RTS commands on party portraits",
        "Show Cloak", "Show Helm", "Show Own Name", "Speech bubbles", "Sticky Targeting", "Turn speed",
    ];

    private static readonly string[] SoundLabels =
    [
        "Sound", "Volume", "Ambience Volume", "Enable All Sound", "Enable Ambience",
        "Enable Music", "Master Volume", "Music Volume", "Sound Effects Volume",
    ];

    public static IReadOnlyList<OptionsSearchEntry> Catalog { get; } =
        Entries(OptionsSearchPage.Video, VideoLabels)
            .Concat(Entries(OptionsSearchPage.Interface, InterfaceLabels))
            .Concat(Entries(OptionsSearchPage.Sound, SoundLabels))
            .ToArray();

    public static Rect Box(float logicalFrameWidth)
    {
        float available = MathF.Max(MinimumBoxWidth, logicalFrameWidth - SideMargin * 2f);
        float width = MathF.Min(BoxWidth, available);
        return new((logicalFrameWidth - width) * .5f, BoxTop, width, BoxHeight);
    }

    public static Rect ClearButton(Rect box) =>
        new(box.X + box.Width - ClearRight - ClearSize,
            box.Y + (box.Height - ClearSize) * .5f, ClearSize, ClearSize);

    public static float ContentTop => BoxTop + BoxHeight + BelowBoxGap;

    public static string PageLabel(OptionsSearchPage page) => page switch
    {
        OptionsSearchPage.Video => "Video Options",
        OptionsSearchPage.Interface => "Interface Options",
        OptionsSearchPage.Sound => "Sound Options",
        _ => "Options",
    };

    public static int? Score(string tag, string query)
    {
        string upper = query.Trim().ToUpperInvariant();
        if (upper.Length == 0) return null;
        var words = new List<string> { upper };
        words.AddRange(upper.Split(new[] { ',', ' ' },
            StringSplitOptions.RemoveEmptyEntries));
        string candidate = tag.ToUpperInvariant();
        foreach (string word in words)
        {
            int first = candidate.IndexOf(word, StringComparison.Ordinal);
            if (first >= 0) return word.Length - 1;
        }
        return null;
    }

    public static OptionsSearchGroup[] Find(string query)
    {
        var matches = Catalog.Select((entry, index) =>
                (Entry: entry, Index: index, Score: Score(entry.Label, query)))
            .Where(match => match.Score is not null)
            .OrderByDescending(match => match.Score!.Value)
            .ThenBy(match => match.Index)
            .ToArray();
        var order = new List<OptionsSearchPage>();
        var groups = new Dictionary<OptionsSearchPage, List<OptionsSearchEntry>>();
        var best = new Dictionary<OptionsSearchPage, int>();
        foreach (var match in matches)
        {
            if (!groups.TryGetValue(match.Entry.Page, out List<OptionsSearchEntry>? entries))
            {
                entries = [];
                groups.Add(match.Entry.Page, entries);
                best.Add(match.Entry.Page, match.Score!.Value);
                order.Add(match.Entry.Page);
            }
            entries.Add(match.Entry);
        }
        return order.Select(page => new OptionsSearchGroup(page, best[page], groups[page]))
            .ToArray();
    }

    private static IEnumerable<OptionsSearchEntry> Entries(
        OptionsSearchPage page, IEnumerable<string> labels) =>
        labels.Select(label => new OptionsSearchEntry(page, label));
}
