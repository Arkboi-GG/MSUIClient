using System.Numerics;

namespace MSUIClient.Engine.UI;

/// <summary>
/// Rule-owned geometry for MSUI's desktop GM-ticket window. Benilla currently ships no
/// HelpFrame, so these seats preserve MSUI's established presentation while keeping every
/// window and page position out of the ImGui host.
/// </summary>
public static class HelpFrameUiLaw
{
    public readonly record struct LogicalRect(float X, float Y, float Width, float Height)
    {
        public Vector2 Min => new(X, Y);
        public Vector2 Size => new(Width, Height);
    }

    public readonly record struct ArtSeat(string Path, LogicalRect Rect);

    public const float Width = 640f;
    public const float Height = 512f;

    public static LogicalRect Frame(Vector2 logicalDisplay) =>
        new((logicalDisplay.X - Width) * .5f, (logicalDisplay.Y - Height) * .5f,
            Width, Height);

    public static readonly ArtSeat[] Art =
    [
        new(@"Interface\HelpFrame\HelpFrame-TopLeft", new(0, 0, 256, 256)),
        new(@"Interface\HelpFrame\HelpFrame-Top", new(256, 0, 256, 256)),
        new(@"Interface\HelpFrame\HelpFrame-TopRight", new(512, 0, 128, 256)),
        new(@"Interface\HelpFrame\HelpFrame-BotLeft", new(0, 256, 256, 256)),
        new(@"Interface\HelpFrame\HelpFrame-Bottom", new(256, 256, 256, 256)),
        new(@"Interface\HelpFrame\HelpFrame-BotRight", new(512, 256, 128, 256)),
    ];

    public static readonly LogicalRect Header = new(140, -12, 336, 64);
    public static readonly Vector2 TitleCenter = new(308, 18);
    public static readonly LogicalRect Close = new(566, 3, 32, 32);

    public static readonly Vector2 HomeHeading = new(42, 58);
    public static readonly LogicalRect HomeIntroduction = new(42, 92, 550, 0);
    public static Vector2 HomeIssueHeading(int index) => new(54, 145 + index * 72);
    public static LogicalRect HomeIssueDescription(int index) =>
        new(70, 166 + index * 72, 500, 0);
    public static readonly LogicalRect HomeOpenTicket = new(213, 405, 214, 24);
    public static readonly LogicalRect HomeCancel = new(270, 447, 100, 22);

    public static readonly Vector2 CategoryHeadingCenter = new(320, 62);
    public static LogicalRect CategoryButton(int index) =>
        new(86 + Math.Max(0, index) % 2 * 250,
            105 + Math.Max(0, index) / 2 * 74, 218, 52);
    public static readonly LogicalRect CategoryBack = new(213, 447, 100, 22);
    public static readonly LogicalRect CategoryCancel = new(327, 447, 100, 22);

    public static readonly Vector2 TicketCategory = new(44, 66);
    public static readonly LogicalRect TicketInstructions = new(44, 91, 548, 0);
    public static readonly LogicalRect TicketInput = new(44, 125, 548, 265);
    public static readonly Vector2 TicketStatus = new(44, 405);
    public static readonly LogicalRect TicketBack = new(251, 441, 100, 22);
    public static readonly LogicalRect TicketSubmit = new(365, 441, 100, 22);
    public static readonly LogicalRect TicketDelete = new(475, 441, 110, 22);
}
