using System.Numerics;

namespace MSUIClient.Engine.UI;

public static class TaxiFrameUiLaw
{
    public const float Width = 384f;
    public const float Height = 512f;
    public const float Top = 104f;
    public static readonly Vector2 PortraitOffset = new(8, 9);
    public const float PortraitSize = 58f;
    public static readonly Vector2 MapOffset = new(21, 75);
    public static readonly Vector2 MapSize = new(316, 352);
    public static readonly Vector2 CloseButton = new(323, 8);
    public const float NodeSize = 16f;
    public const float NodeHighlightSize = 32f;
    public const float RouteWidth = 32f;
    public const float RouteLineFactor = 32f / 30f;
    public const string CurrentIcon = @"Interface\TaxiFrame\UI-Taxi-Icon-Green.blp";
    public const string ReachableIcon = @"Interface\TaxiFrame\UI-Taxi-Icon-White.blp";
    public const string HighlightIcon = @"Interface\TaxiFrame\UI-Taxi-Icon-Highlight.blp";
    public const string RouteTexture = @"Interface\TaxiFrame\UI-Taxi-Line.blp";
    public const string OpenSound = "igMainMenuOpen";
    public const string CloseSound = "igMainMenuClose";
    public const string DiscoveredSound = "TaxiNodeDiscovered";
    public const string SoundCategory = "ui.taxi";
    public const string DiscoveredText = "New flight path discovered!";
    public const string NoConnectedFlightPaths =
        "You don’t know any flight locations connected to this one.";

    public readonly record struct LogicalRect(float X, float Y, float Width, float Height)
    {
        public Vector2 Min => new(X, Y);
        public Vector2 Size => new(Width, Height);
        public Vector2 ScaledMin(Vector2 origin, float scale) => origin + Min * scale;
        public Vector2 ScaledSize(float scale) => Size * scale;
    }

    public readonly record struct TooltipSeat(Vector2 Anchor, Vector2 Pivot);

    public static readonly LogicalRect Frame = new(0, 0, Width, Height);
    public static readonly LogicalRect Portrait = new(
        PortraitOffset.X, PortraitOffset.Y, PortraitSize, PortraitSize);
    public static readonly LogicalRect Map = new(MapOffset.X, MapOffset.Y, MapSize.X, MapSize.Y);
    public static readonly LogicalRect Close = new(CloseButton.X, CloseButton.Y, 32, 32);
    public static readonly LogicalRect[] ShellPieces =
    [
        new(0, 0, 256, 256),
        new(256, 0, 128, 256),
        new(0, 256, 256, 256),
        new(256, 256, 128, 256)
    ];
    public static readonly Vector2 RouteUvA = new(0, 0);
    public static readonly Vector2 RouteUvB = new(0, 1);
    public static readonly Vector2 RouteUvC = new(1, 1);
    public static readonly Vector2 RouteUvD = new(1, 0);

    public static Vector2 FrameOrigin(float scale) => new(0, Top * scale);
    public static Vector2 FrameSize(float scale) => new(Width * scale, Height * scale);

    public static Vector2 TitleCenter(Vector2 origin, float scale, float titleEm) =>
        new(origin.X + Width * .5f * scale, origin.Y + 17f * scale + titleEm * .5f);

    public static Vector2 NodeHalf(float size, float scale) => Vector2.One * (size * scale * .5f);

    // TaxiButtonTemplate: GameTooltip:SetOwner(button, "ANCHOR_RIGHT"). The shared classic
    // renderer consumes a bottom-left tooltip pivot seated at the button's screen top-right.
    public static TooltipSeat NodeTooltipSeat(Vector2 center, float scale)
    {
        Vector2 half = NodeHalf(NodeSize, scale);
        return new(new Vector2(center.X + half.X, center.Y - half.Y), Vector2.UnitY);
    }

    public static Vector2 SegmentSource(Vector4 normalized) => new(normalized.X, normalized.Y);
    public static Vector2 SegmentDestination(Vector4 normalized) => new(normalized.Z, normalized.W);

    public static string? ActivateErrorText(uint code) => code switch
    {
        0 => null,
        1 => "UNSPECIFIED TAXI SERVER ERROR",
        2 => "There is no direct path to that destination!",
        3 => "You don't have enough money!",
        4 => "You are too far away from the taxi stand!",
        5 => "There is no taxi vendor nearby!",
        6 => "You haven't reached that taxi node on foot yet!",
        7 => "You are busy and can't use the taxi service now.",
        8 => "You are already mounted! Dismount first.",
        9 => "You can't take a taxi while shapeshifted!",
        10 => "You are moving.",
        11 => "You are already there!",
        12 => "You need to be standing to go anywhere.",
        _ => $"Taxi activation failed ({code}).",
    };

    public static Vector2 NodeCenter(Vector2 normalized, Vector2 mapMinimum, float scale) =>
        mapMinimum + new Vector2(normalized.X * MapSize.X,
            (1f - normalized.Y) * MapSize.Y) * scale;

    public readonly record struct RouteQuad(Vector2 A, Vector2 B, Vector2 C, Vector2 D);

    public static RouteQuad RouteLine(Vector2 source, Vector2 destination, float scale)
    {
        Vector2 delta = destination - source;
        float length = delta.Length();
        if (length <= float.Epsilon) return new(source, source, source, source);
        Vector2 direction = delta / length;
        Vector2 normal = new(-direction.Y, direction.X);
        float halfWidth = RouteWidth * RouteLineFactor * scale * .5f;
        Vector2 offset = normal * halfWidth;
        return new(source - offset, source + offset,
            destination + offset, destination - offset);
    }
}
