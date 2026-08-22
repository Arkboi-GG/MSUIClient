using System.Net;
using System.Numerics;
using System.Text;

namespace MSUIClient.Engine.UI;

/// <summary>Current ItemTextFrame geometry, material palette, paging, and readable routing law.</summary>
public static class ItemTextFrameUiLaw
{
    public readonly record struct LogicalRect(float X, float Y, float Width, float Height)
    {
        public Vector2 Min => new(X, Y);
        public Vector2 Size => new(Width, Height);
    }

    public const float Width = 384;
    public const float Height = 512;
    public const float Top = 104;
    public const string DefaultMaterial = "Parchment";
    public const string OpenSound = "igMainMenuOpen";
    public const string CloseSound = "igMainMenuClose";
    public const string PageSound = "igMainMenuOptionCheckBoxOn";
    public const string TopLeftArt = @"Interface\ItemTextFrame\UI-ItemText-TopLeft";
    public const string TopRightArt = @"Interface\Spellbook\UI-SpellbookPanel-TopRight";
    public const string BottomLeftArt = @"Interface\ItemTextFrame\UI-ItemText-BotLeft";
    public const string BottomRightArt = @"Interface\Spellbook\UI-SpellbookPanel-BotRight";
    public const string BookIcon = @"Interface\Spellbook\Spellbook-Icon";

    public static readonly LogicalRect Icon = new(10, 8, 58, 58);
    public static readonly LogicalRect Title = new(86, 19, 224, 14);
    public static readonly LogicalRect Scroll = new(38, 76, 280, 355);
    public static readonly LogicalRect Body = new(38, 91, 270, 304);
    public static readonly LogicalRect ScrollBar = new(318, 71, 28, 365);
    public static readonly LogicalRect Prev = new(74, 40, 32, 32);
    public static readonly LogicalRect Next = new(313, 40, 32, 32);
    public static readonly LogicalRect Close = new(323, 10, 32, 32);
    public static readonly Vector2 PrevLabel = new(106, 50);
    public static readonly Vector2 NextLabelRight = new(313, 50);
    public static readonly Vector2 PageCenter = new(202, 50);
    public static readonly LogicalRect MaterialTopLeft = new(21, 75, 256, 256);
    public static readonly LogicalRect MaterialTopRight = new(277, 75, 64, 256);
    public static readonly LogicalRect MaterialBottomLeft = new(21, 331, 256, 128);
    public static readonly LogicalRect MaterialBottomRight = new(277, 331, 64, 128);

    public static Vector2 FrameOrigin(float scale) => new(0, Top * scale);
    public static Vector2 FrameSize(float scale) => new(Width * scale, Height * scale);

    public static bool HasPaging(int page, bool hasNext) => page > 1 || hasNext;
    public static bool CanPrevious(int page) => page > 1;
    public static bool CanNext(bool hasNext) => hasNext;

    public static int TurnPage(int pageIndex, int pageCount, int delta) =>
        Math.Clamp(pageIndex + Math.Sign(delta), 0, Math.Max(0, pageCount - 1));

    public static float MaximumScroll(float contentHeight) =>
        Math.Max(0, contentHeight - Body.Height);
    public static float ClampScroll(float value, float contentHeight) =>
        Math.Clamp(value, 0, MaximumScroll(contentHeight));

    public static string MaterialArt(string material, string corner) =>
        $@"Interface\ItemTextFrame\ItemText-{Material(material)}-{corner}";

    public static string Material(string? material) => string.IsNullOrWhiteSpace(material)
        ? DefaultMaterial : material.Trim();

    public static Vector4 TextColor(string? material) => Material(material) switch
    {
        "Stone" => new(1, 1, 1, 1),
        "Marble" => new(0, 0, 0, 1),
        "Silver" => new(.12f, .12f, .12f, 1),
        _ => new(.18f, .12f, .06f, 1),
    };

    public static Vector4 TitleColor(string? material) => Material(material) switch
    {
        "Stone" or "Marble" or "Silver" or "Bronze" => new(.93f, .82f, 0, 1),
        _ => new(0, 0, 0, 1),
    };

    public static string ComposeBody(string text, string? creator) =>
        string.IsNullOrWhiteSpace(creator)
            ? $"\n{text}\n"
            : $"\n{text}\n\nFrom,\n{creator.Trim()}\n\n";

    /// <summary>SimpleHTML-shaped source to visible text blocks for the ImGui text adapter.</summary>
    public static string VisibleText(string source)
    {
        if (string.IsNullOrEmpty(source)) return "";
        string normalized = source
            .Replace("<BR/>", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("<BR>", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("</P>", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("</H1>", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("</H2>", "\n", StringComparison.OrdinalIgnoreCase);
        var output = new StringBuilder(normalized.Length);
        bool tag = false;
        foreach (char c in normalized)
        {
            if (c == '<') { tag = true; continue; }
            if (tag)
            {
                if (c == '>') tag = false;
                continue;
            }
            output.Append(c);
        }
        return WebUtility.HtmlDecode(output.ToString()).Replace("\r", "").Trim('\n');
    }
}
