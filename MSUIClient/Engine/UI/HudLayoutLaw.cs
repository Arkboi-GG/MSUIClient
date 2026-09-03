using System.Numerics;
using System.Text.Json.Serialization;

namespace MSUIClient.Engine.UI;

/// <summary>Nine-point anchor; the same enum names a screen point and a frame pivot.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HudAnchor
{
    TopLeft, Top, TopRight,
    Left, Center, Right,
    BottomLeft, Bottom, BottomRight,
}

/// <summary>
/// The two layouts every registered HUD frame carries: body play and the Command View
/// vantage. They are separate layouts of the SAME frame set, so the minimap can live top-right
/// in one and bottom-left in the other without an <c>if (_freeView)</c> at the draw site.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HudLayoutContext { Body, Command }

/// <summary>
/// One frame's placement in one context: <c>origin = container * Fraction(Anchor) -
/// size * Fraction(Pivot) + (X, Y)</c>, all in logical pixels. A content-sized frame keeps
/// its alignment across sizes because the pivot is fractional; a layout survives resolution
/// changes because the offset is measured from the nearest screen point, not the corner.
/// </summary>
public sealed record HudPlacement(HudAnchor Anchor, HudAnchor Pivot, float X, float Y)
{
    /// <summary>Anchor and pivot on the same point (the common case).</summary>
    public static HudPlacement At(HudAnchor anchor, float x, float y, HudAnchor? pivot = null)
        => new(anchor, pivot ?? anchor, x, y);
}

/// <summary>A named set of per-frame overrides, one dictionary per context.</summary>
public sealed class HudLayout
{
    public string Name { get; set; } = "";
    public Dictionary<string, HudPlacement> Body { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, HudPlacement> Command { get; set; } = new(StringComparer.Ordinal);

    public Dictionary<string, HudPlacement> For(HudLayoutContext context)
        => context == HudLayoutContext.Command ? Command : Body;

    public HudLayout Clone() => new()
    {
        Name = Name,
        Body = new Dictionary<string, HudPlacement>(Body, StringComparer.Ordinal),
        Command = new Dictionary<string, HudPlacement>(Command, StringComparer.Ordinal),
    };
}

/// <summary>
/// Persisted HUD layout state (settings.json "HudLayout"). <c>Default</c> is implicit and
/// immutable - an empty override set that is never stored in <see cref="Layouts"/>. Authored
/// placements are NOT here: they live at the draw sites, which know their own sizes.
/// </summary>
public sealed class HudLayoutSettings
{
    public string ActiveLayout { get; set; } = HudLayoutLaw.DefaultLayoutName;
    public List<HudLayout> Layouts { get; set; } = new();
    /// <summary>"{guid:X16}" -> layout name. Data model only in phase 1; no UI yet.</summary>
    public Dictionary<string, string> CharacterLayouts { get; set; } = new(StringComparer.Ordinal);
    public int GridSize { get; set; } = 16;
    public bool GridVisible { get; set; } = true;
    public bool SnapToGrid { get; set; } = true;
    public bool SnapToFrames { get; set; } = true;

    // ── legacy (settings Version 11) ─────────────────────────────────────────────────────
    // Read so Migrate11To12 can turn a dragged chat frame into a layout; never written back
    // once zeroed (WhenWritingDefault), so the keys vanish from the file on the next save.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ChatUnlocked { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public float ChatOffsetX { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public float ChatOffsetY { get; set; }

    public HudLayoutSettings Clone() => new()
    {
        ActiveLayout = ActiveLayout,
        Layouts = Layouts.Select(l => l.Clone()).ToList(),
        CharacterLayouts = new Dictionary<string, string>(CharacterLayouts, StringComparer.Ordinal),
        GridSize = GridSize,
        GridVisible = GridVisible,
        SnapToGrid = SnapToGrid,
        SnapToFrames = SnapToFrames,
        ChatUnlocked = ChatUnlocked,
        ChatOffsetX = ChatOffsetX,
        ChatOffsetY = ChatOffsetY,
    };
}

/// <summary>
/// Pure placement law for the HUD layout editor (PLAN_21). Resolves anchor + pivot + offset
/// into a logical origin, clamps it on-screen without ever throwing, re-picks the anchor
/// after a drop so the layout survives resolution changes, snaps to frames / screen / grid,
/// and owns the layout bookkeeping (Default is implicit; editing it forks to Custom).
/// No GameLoop reference (CODE_STRUCTURE_LAW section 4.3).
/// </summary>
public static class HudLayoutLaw
{
    public const string DefaultLayoutName = "Default";
    public const string CustomLayoutName = "Custom";
    /// <summary>Snap capture distance in logical pixels.</summary>
    public const float SnapThreshold = 6f;
    public static readonly int[] GridSizes = [8, 16, 32];
    public const int NudgeSmall = 1;
    public const int NudgeLarge = 10;

    // ── chat frame legacy geometry (restates ChatFrameLaw's two anchor numbers so the
    //    migration has them without the settings layer reaching into a HUD law) ──
    public const float ChatAnchorX = 32f;
    public const float ChatAnchorBottomY = 85f;
    public const float ChatCommandLift = 124f;
    public const string ChatFrameId = "chat";

    public static Vector2 Fraction(HudAnchor anchor) => anchor switch
    {
        HudAnchor.TopLeft => new(0f, 0f),
        HudAnchor.Top => new(.5f, 0f),
        HudAnchor.TopRight => new(1f, 0f),
        HudAnchor.Left => new(0f, .5f),
        HudAnchor.Center => new(.5f, .5f),
        HudAnchor.Right => new(1f, .5f),
        HudAnchor.BottomLeft => new(0f, 1f),
        HudAnchor.Bottom => new(.5f, 1f),
        _ => new(1f, 1f),
    };

    public static string Label(HudAnchor anchor) => anchor switch
    {
        HudAnchor.TopLeft => "Top left",
        HudAnchor.Top => "Top",
        HudAnchor.TopRight => "Top right",
        HudAnchor.Left => "Left",
        HudAnchor.Center => "Center",
        HudAnchor.Right => "Right",
        HudAnchor.BottomLeft => "Bottom left",
        HudAnchor.Bottom => "Bottom",
        _ => "Bottom right",
    };

    /// <summary>
    /// The logical origin of a <paramref name="size"/> box placed by <paramref name="placement"/>
    /// inside a container (the screen, or a parent frame's rect for a child).
    /// </summary>
    public static Vector2 Resolve(HudPlacement placement, Vector2 containerMin, Vector2 containerSize,
        Vector2 size)
        => containerMin + containerSize * Fraction(placement.Anchor) - size * Fraction(placement.Pivot)
           + new Vector2(placement.X, placement.Y);

    /// <summary>
    /// Keep the box on-screen. Guards the min>max case (Math.Clamp THROWS there) that a frame
    /// wider than a 1x1 boot/minimised display produces, and NaN/Infinity from a degenerate
    /// display, so a layout can never take the frame down.
    /// </summary>
    public static Vector2 Clamp(Vector2 origin, Vector2 size, Vector2 display)
    {
        float hx = display.X - size.X, hy = display.Y - size.Y;
        float x = float.IsFinite(origin.X) ? origin.X : 0f;
        float y = float.IsFinite(origin.Y) ? origin.Y : 0f;
        if (!float.IsFinite(hx)) hx = 0f;
        if (!float.IsFinite(hy)) hy = 0f;
        return new Vector2(
            Math.Clamp(x, MathF.Min(0f, hx), MathF.Max(0f, hx)),
            Math.Clamp(y, MathF.Min(0f, hy), MathF.Max(0f, hy)));
    }

    /// <summary>The screen third the box's centre sits in, as an anchor (ElvUI's rule).</summary>
    public static HudAnchor NearestAnchor(Vector2 origin, Vector2 size, Vector2 display)
    {
        Vector2 c = origin + size * .5f;
        int col = display.X <= 0f ? 0 : c.X < display.X / 3f ? 0 : c.X > display.X * 2f / 3f ? 2 : 1;
        int row = display.Y <= 0f ? 0 : c.Y < display.Y / 3f ? 0 : c.Y > display.Y * 2f / 3f ? 2 : 1;
        return (HudAnchor)(row * 3 + col);
    }

    /// <summary>
    /// Express <paramref name="origin"/> relative to <paramref name="anchor"/> (or the nearest
    /// one) without moving the rect: pivot = anchor, offset = origin - the anchor's base point.
    /// </summary>
    public static HudPlacement Reanchor(Vector2 origin, Vector2 size, Vector2 display,
        HudAnchor? anchor = null)
    {
        HudAnchor a = anchor ?? NearestAnchor(origin, size, display);
        Vector2 basePoint = display * Fraction(a) - size * Fraction(a);
        Vector2 offset = origin - basePoint;
        return new HudPlacement(a, a, offset.X, offset.Y);
    }

    public static Vector2 Nudge(Vector2 origin, int dx, int dy, bool large)
        => origin + new Vector2(dx, dy) * (large ? NudgeLarge : NudgeSmall);

    public static int NextGridSize(int current)
    {
        int i = Array.IndexOf(GridSizes, current);
        return GridSizes[(i < 0 ? 0 : i + 1) % GridSizes.Length];
    }

    // ── snapping ─────────────────────────────────────────────────────────────────────────

    public readonly record struct SnapBox(Vector2 Min, Vector2 Size);
    /// <summary>A guide line to draw: vertical at x = At, or horizontal at y = At (logical).</summary>
    public readonly record struct GuideLine(bool Vertical, float At);
    public readonly record struct SnapResult(Vector2 Origin, IReadOnlyList<GuideLine> Guides);

    /// <summary>
    /// Snap a proposed origin. Per axis, in priority order, the first tier with a candidate
    /// within <paramref name="threshold"/> wins: other frames' edges and centres (if
    /// <paramref name="snapToFrames"/>), then screen edges and centres, then grid lines (if
    /// <paramref name="snapToGrid"/>). Beyond the threshold the origin is returned unchanged
    /// with no guides.
    /// </summary>
    public static SnapResult Snap(Vector2 origin, Vector2 size, Vector2 display,
        IReadOnlyList<SnapBox> others, bool snapToFrames, bool snapToGrid, int gridSize,
        float threshold = SnapThreshold)
    {
        var guides = new List<GuideLine>(2);
        float x = SnapAxis(origin.X, size.X, display.X, others, true, snapToFrames, snapToGrid,
            gridSize, threshold, out float? guideX);
        float y = SnapAxis(origin.Y, size.Y, display.Y, others, false, snapToFrames, snapToGrid,
            gridSize, threshold, out float? guideY);
        if (guideX is float gx) guides.Add(new GuideLine(true, gx));
        if (guideY is float gy) guides.Add(new GuideLine(false, gy));
        return new SnapResult(new Vector2(x, y), guides);
    }

    private static float SnapAxis(float origin, float size, float display, IReadOnlyList<SnapBox> others,
        bool horizontal, bool snapToFrames, bool snapToGrid, int gridSize, float threshold,
        out float? guide)
    {
        guide = null;
        float[] edges = [origin, origin + size * .5f, origin + size];

        if (snapToFrames)
        {
            float best = float.MaxValue, bestDelta = 0f, bestAt = 0f;
            foreach (SnapBox o in others)
            {
                float oMin = horizontal ? o.Min.X : o.Min.Y;
                float oSize = horizontal ? o.Size.X : o.Size.Y;
                float[] targets = [oMin, oMin + oSize * .5f, oMin + oSize];
                Consider(edges, targets, threshold, ref best, ref bestDelta, ref bestAt);
            }
            if (best <= threshold) { guide = bestAt; return origin + bestDelta; }
        }
        {
            float best = float.MaxValue, bestDelta = 0f, bestAt = 0f;
            float[] screen = [0f, display * .5f, display];
            Consider(edges, screen, threshold, ref best, ref bestDelta, ref bestAt);
            if (best <= threshold) { guide = bestAt; return origin + bestDelta; }
        }
        if (snapToGrid && gridSize > 0)
        {
            float best = float.MaxValue, bestDelta = 0f, bestAt = 0f;
            foreach (float edge in edges)
            {
                float target = MathF.Round(edge / gridSize) * gridSize;
                float d = MathF.Abs(target - edge);
                if (d < best) { best = d; bestDelta = target - edge; bestAt = target; }
            }
            if (best <= threshold) { guide = bestAt; return origin + bestDelta; }
        }
        return origin;
    }

    private static void Consider(float[] edges, float[] targets, float threshold,
        ref float best, ref float bestDelta, ref float bestAt)
    {
        foreach (float edge in edges)
            foreach (float target in targets)
            {
                float d = MathF.Abs(target - edge);
                if (d <= threshold && d < best) { best = d; bestDelta = target - edge; bestAt = target; }
            }
    }

    // ── layout bookkeeping ───────────────────────────────────────────────────────────────

    public static bool IsDefaultActive(HudLayoutSettings s)
        => string.Equals(s.ActiveLayout, DefaultLayoutName, StringComparison.OrdinalIgnoreCase);

    public static HudLayout? Find(HudLayoutSettings s, string name)
    {
        foreach (HudLayout l in s.Layouts)
            if (string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase)) return l;
        return null;
    }

    /// <summary>The active layout's overrides for a context, or null while Default is active.</summary>
    public static Dictionary<string, HudPlacement>? Overrides(HudLayoutSettings s, HudLayoutContext context)
        => IsDefaultActive(s) ? null : Find(s, s.ActiveLayout)?.For(context);

    public static HudPlacement? Override(HudLayoutSettings s, HudLayoutContext context, string frameId)
        => Overrides(s, context) is { } o && o.TryGetValue(frameId, out HudPlacement? p) ? p : null;

    /// <summary>
    /// The layout edits land in. Default is immutable, so editing it silently forks to
    /// <see cref="CustomLayoutName"/> (created empty if absent) and makes that active. A named
    /// active layout that is missing from the list is recreated empty rather than failing.
    /// </summary>
    public static HudLayout EnsureEditable(HudLayoutSettings s)
    {
        if (IsDefaultActive(s)) s.ActiveLayout = CustomLayoutName;
        HudLayout? layout = Find(s, s.ActiveLayout);
        if (layout is null)
        {
            layout = new HudLayout { Name = s.ActiveLayout };
            s.Layouts.Add(layout);
        }
        return layout;
    }

    /// <summary>Default first, then the user layouts in stored order.</summary>
    public static IReadOnlyList<string> LayoutNames(HudLayoutSettings s)
    {
        var names = new List<string>(s.Layouts.Count + 1) { DefaultLayoutName };
        foreach (HudLayout l in s.Layouts)
            if (l.Name.Length > 0 && !names.Contains(l.Name, StringComparer.OrdinalIgnoreCase))
                names.Add(l.Name);
        return names;
    }

    public static string NextLayoutName(HudLayoutSettings s)
    {
        IReadOnlyList<string> names = LayoutNames(s);
        int i = 0;
        for (int k = 0; k < names.Count; k++)
            if (string.Equals(names[k], s.ActiveLayout, StringComparison.OrdinalIgnoreCase)) { i = k; break; }
        return names[(i + 1) % names.Count];
    }

    /// <summary>
    /// Settings Version 11 -> 12. The one movable frame used to be the chat window, via a
    /// per-frame special case (ChatUnlocked + a saved offset from its authored corner). A
    /// non-zero offset becomes a <c>Custom</c> layout carrying a <c>chat</c> placement in BOTH
    /// contexts - anchored bottom-left like the authored frame, so the rect does not move -
    /// and that layout is made active. The legacy keys are zeroed so they stop being written.
    /// </summary>
    public static void Migrate11To12(HudLayoutSettings s)
    {
        float dx = float.IsFinite(s.ChatOffsetX) ? s.ChatOffsetX : 0f;
        float dy = float.IsFinite(s.ChatOffsetY) ? s.ChatOffsetY : 0f;
        if (dx != 0f || dy != 0f)
        {
            HudLayout? layout = Find(s, CustomLayoutName);
            if (layout is null)
            {
                layout = new HudLayout { Name = CustomLayoutName };
                s.Layouts.Add(layout);
            }
            layout.Body[ChatFrameId] = HudPlacement.At(HudAnchor.BottomLeft,
                ChatAnchorX + dx, -ChatAnchorBottomY + dy);
            layout.Command[ChatFrameId] = HudPlacement.At(HudAnchor.BottomLeft,
                ChatAnchorX + dx, -ChatAnchorBottomY - ChatCommandLift + dy);
            s.ActiveLayout = CustomLayoutName;
        }
        s.ChatUnlocked = false;
        s.ChatOffsetX = 0f;
        s.ChatOffsetY = 0f;
    }
}
