using MSUIClient.Formats;
using System.Numerics;

namespace MSUIClient.Engine.UI;

/// <summary>Build-5875 WorldStateFrame dimensions and Core 1.12 score columns.</summary>
public static class BattlefieldScoreUiLaw
{
    public const int VisibleRows = 22;
    public const float Height = 512;
    public static int ObjectiveCount(uint map) => map switch { 30 => 7, 489 or 529 => 2, _ => 0 };
    public static float Width(int objectives, int rows) => 530 + 77 * Math.Clamp(objectives, 0, 7) + (rows > VisibleRows ? 37 : 0);
    // Native center panels preserve their own anchors (SetCenterFrame skipSetPoint=1).
    public static Vector2 Origin(Vector2 display, float width, float scale) =>
        new((display.X - width * scale) * .5f + 55 * scale, (display.Y - Height * scale) * .5f);
    public static string WinnerText(byte? winner) => winner switch { 0 => "Horde Wins!", 1 => "Alliance Wins!", 2 => "Battle concluded", _ => "Battleground Scores" };
    public static string[] ObjectiveNames(uint map) => map switch
    {
        30 => ["Graveyards Assaulted", "Graveyards Defended", "Towers Assaulted", "Towers Defended", "Mines Taken", "Lieutenants Killed", "NPCs Summoned"],
        489 => ["Flag Captures", "Flag Returns"],
        529 => ["Bases Assaulted", "Bases Defended"],
        _ => [],
    };
    // Preserve authored record order: AV IDs are deliberately not numerically sorted.
    public static IReadOnlyList<WorldStateUiRow> ObjectiveColumns(WorldStateUiCatalog? catalog, uint map) =>
        catalog?.Rows.Where(row => row.Type == 2 && row.Map == map && row.Area == 0 &&
            row.Faction is 0 or -1 && row.StateVariable == 0).Take(7).ToArray() ?? [];

    public readonly record struct ObjectiveCell(string Text, string Icon);
    public static ObjectiveCell ObjectiveValue(WorldStateUiRow? column, uint value, RaceTeam? rowTeam,
        string countTemplate = "x %d")
    {
        string number = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (column is null || column.Icon.Length == 0) return new(number, "");
        if (value == 0) return new("", "");
        // A name query can still be outstanding. Keep the number visible without inventing a faction.
        if (rowTeam is not (RaceTeam.Horde or RaceTeam.Alliance)) return new(number, "");
        return new(countTemplate.Replace("%d", number, StringComparison.Ordinal),
            column.Icon + (rowTeam == RaceTeam.Horde ? "0" : "1"));
    }
    public static int ClampScroll(int scroll, int rows) => Math.Clamp(scroll, 0, Math.Max(0, rows - VisibleRows));
}
