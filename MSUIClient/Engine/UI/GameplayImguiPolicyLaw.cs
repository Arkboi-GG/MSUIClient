using System.Text.RegularExpressions;

namespace MSUIClient.Engine.UI;

/// <summary>
/// Policy law: gameplay windows and modals are BANNED from using ImGui's default
/// widgets. A frame the player sees in the world must read as a WoW frame, not a
/// debug window — so its content is painted with the game's own art and text
/// (<see cref="GameText"/> on a draw list, <c>DrawVanillaPanelChrome</c>, the
/// <c>VanillaButton</c>/<c>VanillaTab</c> family, <c>DrawFourPieceShell</c>), never
/// with <c>ImGui.Button</c>, <c>ImGui.Text*</c>, <c>ImGui.BeginTable</c>,
/// <c>ImGui.Separator</c>, <c>ImGui.SetTooltip</c>, and the rest of the debug kit.
///
/// This is NOT a ban on ImGui itself: ImGui.NET is the immediate-mode BACKEND for
/// the whole client, so the window host and input primitives — <c>ImGui.Begin</c>,
/// <c>GetWindowDrawList</c>, <c>InvisibleButton</c>, <c>SetNextWindowSize</c>,
/// <c>IsItemHovered</c> — are how even a pixel-faithful vanilla frame is hosted and
/// stays allowed (see <see cref="AllowedHostPrimitives"/>). The ban is on the
/// VISIBLE WIDGETS that make a frame look like a tool.
///
/// DEV SURFACES ARE EXCLUDED. Creator Mode, the dev tools, the encounter lab, and
/// other developer-only windows may use ImGui widgets freely — they are never shown
/// to a player. <see cref="IsDevExcludedPath"/> draws that line.
///
/// Enforcement is a ratchet, not a big-bang migration: legacy gameplay panels still
/// carry many ImGui-widget calls, so the clinical check holds an explicit ENROLLED
/// allowlist of panels that meet the standard and asserts they stay clean. New
/// gameplay panels are authored clean and added to that list; legacy panels are
/// migrated onto it over time. <see cref="Scan"/> is the shared detector.
/// </summary>
public static class GameplayImguiPolicyLaw
{
    /// <summary>
    /// ImGui members that render a visible widget and therefore may not appear in a
    /// gameplay window/modal. Not exhaustive of ImGui, but covers the debug kit a
    /// gameplay frame would reach for; extend as new ones are spotted in review.
    /// </summary>
    public static readonly IReadOnlySet<string> BannedWidgets = new HashSet<string>(StringComparer.Ordinal)
    {
        // Buttons / toggles (InvisibleButton is deliberately NOT here — it is the
        // input primitive VanillaButton is built on).
        "Button", "SmallButton", "ArrowButton", "Checkbox", "CheckboxFlags",
        "RadioButton", "Bullet",
        // Text
        "Text", "TextUnformatted", "TextColored", "TextDisabled", "TextWrapped",
        "LabelText", "BulletText",
        // Inputs / sliders / drags
        "InputText", "InputTextMultiline", "InputTextWithHint", "InputInt", "InputFloat",
        "InputDouble", "SliderFloat", "SliderInt", "SliderAngle", "VSliderFloat",
        "VSliderInt", "DragFloat", "DragInt", "DragFloatRange2",
        // Combos / lists / selectables
        "BeginCombo", "Combo", "Selectable", "BeginListBox", "ListBox",
        // Tables / columns
        "BeginTable", "EndTable", "TableNextRow", "TableNextColumn", "TableSetupColumn",
        "TableHeadersRow", "Columns",
        // Trees / headers / menus / tabs
        "TreeNode", "TreeNodeEx", "CollapsingHeader", "BeginMenu", "MenuItem",
        "BeginMenuBar", "BeginTabBar", "BeginTabItem",
        // Misc visible chrome
        "Separator", "SeparatorText", "ProgressBar", "ColorEdit3", "ColorEdit4",
        "ColorButton", "PlotLines", "PlotHistogram", "Image", "ImageButton",
        // Tooltips: gameplay frames use the shared GAME tooltip, never ImGui's.
        "SetTooltip", "BeginTooltip", "SetItemTooltip",
    };

    /// <summary>
    /// The window-host / input / layout primitives that are ALLOWED in a gameplay
    /// frame — documentation for reviewers. These do not render a themed widget; they
    /// host the window, read input, or place a cursor. The scanner ignores everything
    /// not in <see cref="BannedWidgets"/>, so this set is descriptive, not enforced.
    /// </summary>
    public static readonly IReadOnlySet<string> AllowedHostPrimitives = new HashSet<string>(StringComparer.Ordinal)
    {
        "Begin", "End", "BeginChild", "EndChild",
        "GetWindowDrawList", "GetWindowPos", "GetWindowSize", "GetContentRegionAvail",
        "GetCursorScreenPos", "SetCursorScreenPos", "GetCursorPos", "SetCursorPos",
        "InvisibleButton", "IsItemHovered", "IsItemActive", "IsItemClicked",
        "IsMouseClicked", "IsMouseDown", "IsWindowHovered", "IsWindowFocused",
        "SetNextWindowPos", "SetNextWindowSize", "SetNextWindowSizeConstraints",
        "SetNextWindowBgAlpha", "SetNextWindowFocus", "PushID", "PopID",
        "PushClipRect", "PopClipRect", "Dummy", "SameLine", "NewLine", "Spacing",
        "ColorConvertFloat4ToU32", "ColorConvertU32ToFloat4", "GetIO", "GetStyle",
    };

    /// <summary>One banned call site.</summary>
    /// <param name="Member">The ImGui member (e.g. "Button").</param>
    /// <param name="Line">1-based line number in the scanned source.</param>
    public readonly record struct Usage(string Member, int Line);

    // ImGui.<Member> — the member name is captured so it can be checked for membership.
    private static readonly Regex CallSite =
        new(@"\bImGui\.([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);

    /// <summary>
    /// A dev-only surface, exempt from the ban. Creator Mode and the dev tools live
    /// under their own folders and are never shown to a player; the check matches on
    /// path segment so it is agnostic to OS slash direction.
    /// </summary>
    public static bool IsDevExcludedPath(string path)
    {
        string p = path.Replace('\\', '/');
        return p.Contains("/Dev/", StringComparison.OrdinalIgnoreCase)
            || p.Contains("/CreatorMode/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Every banned ImGui-widget call in a source file, in order. A line commented
    /// out still matches — that is deliberate: a commented-out widget is a migration
    /// left half-done, and the point is to keep enrolled files clean of the pattern.
    /// </summary>
    public static IReadOnlyList<Usage> Scan(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var found = new List<Usage>();
        int line = 1;
        int start = 0;
        for (int i = 0; i < source.Length; i++)
        {
            if (source[i] != '\n') continue;
            ScanSpan(source, start, i, line, found);
            line++;
            start = i + 1;
        }
        ScanSpan(source, start, source.Length, line, found);
        return found;
    }

    private static void ScanSpan(string source, int start, int end, int line, List<Usage> found)
    {
        foreach (Match m in CallSite.Matches(source[start..end]))
        {
            string member = m.Groups[1].Value;
            if (BannedWidgets.Contains(member))
                found.Add(new Usage(member, line));
        }
    }

    /// <summary>A one-line human summary of what a scan found, for a check message.</summary>
    public static string Describe(string file, IReadOnlyList<Usage> usages) =>
        usages.Count == 0
            ? $"{file}: clean"
            : $"{file}: banned ImGui widgets — " +
              string.Join(", ", usages.Select(u => $"ImGui.{u.Member}@{u.Line}"));
}
