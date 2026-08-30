using System.Numerics;

namespace MSUIClient.Engine.UI;

public readonly record struct WorldMapDropdownGeometry(
    Vector2 FrameMin, Vector2 FrameSize, Vector2 ListMin, Vector2 ListSize)
{
    public bool Contains(Vector2 point, bool listOpen) =>
        Contains(FrameMin, FrameSize, point) ||
        listOpen && Contains(ListMin, ListSize, point);

    private static bool Contains(Vector2 min, Vector2 size, Vector2 point) =>
        point.X >= min.X && point.Y >= min.Y &&
        point.X <= min.X + size.X && point.Y <= min.Y + size.Y;
}

/// <summary>Current Benilla WorldMapFrame/UIDropDownMenu fixed geometry.</summary>
public static class WorldMapUiLaw
{
    public readonly record struct LogicalRect(float X, float Y, float Width, float Height)
    {
        public Vector2 Min => new(X, Y);
        public Vector2 Size => new(Width, Height);
        public Vector2 ScaledMin(Vector2 origin, float scale) => origin + Min * scale;
        public Vector2 ScaledSize(float scale) => Size * scale;
    }

    public readonly record struct TextureSlice(LogicalRect Rect, Vector2 UvMin, Vector2 UvMax);
    public readonly record struct ScreenRect(Vector2 Min, Vector2 Size);
    public readonly record struct TooltipSeat(Vector2 Anchor, Vector2 Pivot);

    public readonly record struct FrameLayout(float Scale, Vector2 LogicalOrigin,
        Vector2 LogicalSize);

    public const float FrameWidth = 1024f;
    public const float FrameHeight = 768f;
    public const float DropWidth = 130f;
    public const float CapsuleWidth = 180f;
    public const float CapsuleHeight = 32f;
    public const float RowHeight = 16f;
    public const float BorderHeight = 15f;
    public const int MaximumRows = 32;
    public const string TitleFont = "GameFontNormal";
    public const string HoverLabelFont = "WorldMapTextFont";
    public const float TileSize = 256f;
    public const float PoiIconSize = 16f;
    public static readonly LogicalRect FrameRect = new(0, 0, FrameWidth, FrameHeight);
    public static readonly LogicalRect MapRect = new(11, 69, 1002, 668);
    // World Map FontString CENTER -> frame CENTER(0,+372), screen y = 384-372.
    public static readonly Vector2 TitleCenter = new(512, 12);
    public static readonly LogicalRect ViewAction = new(680, 34, 110, 22);
    public static readonly LogicalRect Close = new(982, 4, 32, 32);
    public static readonly DropdownCapsuleUiLaw.Layout Capsule =
        DropdownCapsuleUiLaw.At(0, 0, DropWidth);
    public static readonly Vector2 CapsuleLabel = new(20, -10);
    public static readonly LogicalRect DropdownRow = new(0, 0, DropWidth, RowHeight);
    public static readonly Vector4 CorpseTooltipColor = new(1f, .125f, .125f, 1f);

    /// <summary>Preserves the fullscreen 1024x768 scale-to-fit and letterbox centering.</summary>
    public static FrameLayout Frame(Vector2 display)
    {
        float width = float.IsFinite(display.X) ? MathF.Max(0f, display.X) : 0f;
        float height = float.IsFinite(display.Y) ? MathF.Max(0f, display.Y) : 0f;
        float scale = MathF.Max(.01f, MathF.Min(width / FrameWidth, height / FrameHeight));
        Vector2 size = new(FrameWidth, FrameHeight);
        Vector2 origin = (new Vector2(width, height) / scale - size) * .5f;
        return new(scale, origin, size);
    }

    public static WorldMapDropdownGeometry Continent(Vector2 origin, float scale, int rows)
        => Geometry(origin + new Vector2(342f, 35f) * scale, scale, rows);

    public static WorldMapDropdownGeometry Zone(Vector2 origin, float scale, int rows)
        => Geometry(origin + new Vector2(489f, 35f) * scale, scale, rows);

    public static WorldMapDropdownGeometry Geometry(Vector2 frameMin, float scale, int rows)
    {
        int count = Math.Clamp(rows, 0, MaximumRows);
        Vector2 frameSize = new(CapsuleWidth, CapsuleHeight);
        Vector2 listMin = frameMin + new Vector2(8f, CapsuleHeight - 7f) * scale;
        Vector2 listSize = new(DropWidth + 32f, count * RowHeight + BorderHeight * 2f);
        return new(frameMin, frameSize * scale, listMin, listSize * scale);
    }

    public static Vector2 RowMin(in WorldMapDropdownGeometry geometry, int index, float scale) =>
        geometry.ListMin + new Vector2(17f, BorderHeight + Math.Clamp(index, 0,
            MaximumRows - 1) * RowHeight) * scale;

    public static LogicalRect ShellTile(int row, int column) =>
        new(Math.Clamp(column, 0, 3) * TileSize, Math.Clamp(row, 0, 2) * TileSize,
            TileSize, TileSize);

    public static LogicalRect DetailTile(int row, int column) =>
        new(Math.Clamp(column, 0, 3) * TileSize, Math.Clamp(row, 0, 2) * TileSize,
            TileSize, TileSize);

    public static Vector2 At(Vector2 origin, Vector2 logicalPoint, float scale) =>
        origin + logicalPoint * scale;

    public static Vector2 MapPoint(Vector2 mapMin, Vector2 mapSize, float x, float y) =>
        new(mapMin.X + x * mapSize.X, mapMin.Y + y * mapSize.Y);

    public static TooltipSeat CorpseTooltipSeat(Vector2 markerMin, Vector2 markerSize,
        Vector2 mapMin, Vector2 mapSize)
    {
        bool markerOnRight = markerMin.X + markerSize.X * .5f >
            mapMin.X + mapSize.X * .5f;
        return markerOnRight
            ? new(markerMin, Vector2.One)
            : new(markerMin + Vector2.UnitX * markerSize.X, Vector2.UnitY);
    }

    public static Vector2 HoverLabel(Vector2 mapMin, Vector2 mapSize, float scale) =>
        // Area frame TOP(-10), FontString TOP(+20 screen), then half of the
        // SetFont(33) byte-law-clamped 32px em to reach GameText's center point.
        new(mapMin.X + mapSize.X * .5f + 20f * scale,
            mapMin.Y + (10f + 20f + 16f) * scale);

    public static ScreenRect PixelRect(Vector2 origin, float x, float y,
        float width, float height, float scale) => new(
            new(origin.X + x * scale, origin.Y + y * scale),
            new(width * scale, height * scale));

    public static Vector2 UvMaximum(float u, float v) => new(u, v);
}
