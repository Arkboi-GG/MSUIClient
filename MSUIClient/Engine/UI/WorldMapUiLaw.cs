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
    public const string CapsuleTexture =
        @"Interface\Glues\CharacterCreate\CharacterCreate-LabelFrame";

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
}
