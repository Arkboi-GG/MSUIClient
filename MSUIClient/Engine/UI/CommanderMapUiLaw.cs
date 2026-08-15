using System.Numerics;

namespace MSUIClient.Engine.UI;

/// <summary>
/// Pure geometry, interaction, and presentation laws shared by the Commander
/// renderer and its clinical check. Vanilla world-map art is authored in the
/// upper-left 1002x668 pixels of a padded 4x3 grid of 256px textures.
/// </summary>
public static class CommanderMapUiLaw
{
    public const float AuthoredWidth = 1002f;
    public const float AuthoredHeight = 668f;
    public const float TileSize = 256f;
    public const int TileColumns = 4;
    public const int TileRows = 3;
    public const float OverlayChunkSize = 256f;

    public const byte HonorModule = 1 << 0;
    public const byte HeroesModule = 1 << 1;
    public const byte FactionControlModule = 1 << 4;
    public const byte MaximumHeroLevel = 5;

    public enum HeroAction : byte
    {
        None = 0,
        Declare = 1,
        Upgrade = 2,
        Revive = 3,
    }

    /// <summary>A screen-space rectangle with a positive size.</summary>
    public readonly record struct ScreenRect(Vector2 Min, Vector2 Size)
    {
        public Vector2 Max => Min + Size;
        public Vector2 Centre => Min + Size * 0.5f;
        public float Aspect => Size.X / Size.Y;
    }

    /// <summary>A rectangle in the uncropped 1002x668 map's 0..1 space.</summary>
    public readonly record struct NormalizedRect(Vector2 Min, Vector2 Max)
    {
        public Vector2 Size => Max - Min;
        public Vector2 Centre => (Min + Max) * 0.5f;
        public float MapPixelAspect => Size.X * AuthoredWidth / (Size.Y * AuthoredHeight);
    }

    /// <summary>
    /// The two overview cells. Kalimdor is left/top and Eastern Kingdoms is
    /// right/bottom. Both cells are present in every responsive mode.
    /// </summary>
    public readonly record struct DualViewportLayout(
        ScreenRect EasternKingdoms, ScreenRect Kalimdor, bool Stacked);

    /// <summary>
    /// One texture in a WorldMapOverlay row. PixelMin/PixelSize are destination
    /// coordinates on the authored 1002x668 map. TexturePixelSize describes the
    /// power-of-two BLP backing; UvMax crops its unused right/bottom padding.
    /// </summary>
    public readonly record struct OverlayChunk(
        int TextureIndex,
        Vector2 PixelMin,
        Vector2 PixelSize,
        Vector2 TexturePixelSize,
        Vector2 UvMax)
    {
        public NormalizedRect MapBounds => new(
            new Vector2(PixelMin.X / AuthoredWidth, PixelMin.Y / AuthoredHeight),
            new Vector2((PixelMin.X + PixelSize.X) / AuthoredWidth,
                (PixelMin.Y + PixelSize.Y) / AuthoredHeight));
    }

    public readonly record struct ZoneHighlightPlacement(
        string TexturePath, ScreenRect Destination, Vector2 UvMax);

    public static Vector2 FrameSize(float targetWidth)
    {
        if (!float.IsFinite(targetWidth) || targetWidth <= 0f)
            throw new ArgumentOutOfRangeException(nameof(targetWidth));
        return new Vector2(targetWidth, targetWidth * AuthoredHeight / AuthoredWidth);
    }

    public static (Vector2 Min, Vector2 Max) TileBounds(
        Vector2 frameMin, float targetWidth, int row, int column)
    {
        if ((uint)row >= TileRows) throw new ArgumentOutOfRangeException(nameof(row));
        if ((uint)column >= TileColumns) throw new ArgumentOutOfRangeException(nameof(column));
        float scale = targetWidth / AuthoredWidth;
        float tile = TileSize * scale;
        Vector2 min = frameMin + new Vector2(column * tile, row * tile);
        return (min, min + new Vector2(tile));
    }

    public static Vector2 Project(Vector2 frameMin, Vector2 frameSize, Vector2 normalized) =>
        frameMin + normalized * frameSize;

    public static Vector2 Unproject(Vector2 frameMin, Vector2 frameSize, Vector2 screen) =>
        (screen - frameMin) / frameSize;

    public static bool Contains(Vector2 frameMin, Vector2 frameSize, Vector2 screen) =>
        screen.X >= frameMin.X && screen.X <= frameMin.X + frameSize.X &&
        screen.Y >= frameMin.Y && screen.Y <= frameMin.Y + frameSize.Y;

    public static bool Contains(in ScreenRect rect, Vector2 screen) =>
        Contains(rect.Min, rect.Size, screen);

    public static bool Contains(in NormalizedRect rect, Vector2 normalized) =>
        normalized.X >= rect.Min.X && normalized.X <= rect.Max.X &&
        normalized.Y >= rect.Min.Y && normalized.Y <= rect.Max.Y;

    /// <summary>
    /// Split an available overview area into two non-overlapping cells. Wide
    /// surfaces place both continents side by side; narrow surfaces stack them.
    /// The minimum applies to each raw side-by-side cell, before crop aspect-fit.
    /// </summary>
    public static DualViewportLayout LayoutDualViewports(
        Vector2 min, Vector2 size, float gap, float minimumSideBySideWidth)
    {
        ValidatePositive(size, nameof(size));
        if (!float.IsFinite(gap) || gap < 0f || gap >= MathF.Max(size.X, size.Y))
            throw new ArgumentOutOfRangeException(nameof(gap));
        if (!float.IsFinite(minimumSideBySideWidth) || minimumSideBySideWidth <= 0f)
            throw new ArgumentOutOfRangeException(nameof(minimumSideBySideWidth));

        float sideWidth = (size.X - gap) * 0.5f;
        bool stacked = sideWidth < minimumSideBySideWidth;
        if (!stacked)
        {
            Vector2 cell = new(sideWidth, size.Y);
            return new DualViewportLayout(
                new ScreenRect(min + new Vector2(sideWidth + gap, 0f), cell),
                new ScreenRect(min, cell),
                false);
        }

        float stackedHeight = (size.Y - gap) * 0.5f;
        if (stackedHeight <= 0f)
            throw new ArgumentOutOfRangeException(nameof(size), "The available height cannot hold two viewports.");
        Vector2 stackedCell = new(size.X, stackedHeight);
        return new DualViewportLayout(
            new ScreenRect(min + new Vector2(0f, stackedHeight + gap), stackedCell),
            new ScreenRect(min, stackedCell),
            true);
    }

    /// <summary>
    /// Aspect-fit one land crop into its layout cell. The crop's authored pixel
    /// aspect, rather than its normalized X:Y ratio, is the visual aspect.
    /// </summary>
    public static ScreenRect FitViewportToCrop(in ScreenRect cell, in NormalizedRect crop)
    {
        ValidateScreenRect(cell, nameof(cell));
        ValidateNormalizedRect(crop, nameof(crop));
        float targetAspect = crop.MapPixelAspect;
        Vector2 size;
        if (cell.Aspect > targetAspect)
            size = new Vector2(cell.Size.Y * targetAspect, cell.Size.Y);
        else
            size = new Vector2(cell.Size.X, cell.Size.X / targetAspect);
        return new ScreenRect(cell.Centre - size * 0.5f, size);
    }

    /// <summary>Pad a land-union crop without ever leaving the authored map.</summary>
    public static NormalizedRect PadCrop(in NormalizedRect crop, Vector2 padding)
    {
        ValidateNormalizedRect(crop, nameof(crop));
        if (!float.IsFinite(padding.X) || !float.IsFinite(padding.Y) ||
            padding.X < 0f || padding.Y < 0f)
            throw new ArgumentOutOfRangeException(nameof(padding));
        return new NormalizedRect(
            Vector2.Max(Vector2.Zero, crop.Min - padding),
            Vector2.Min(Vector2.One, crop.Max + padding));
    }

    /// <summary>Project a full-map normalized point through a land crop.</summary>
    public static Vector2 ProjectCrop(
        in ScreenRect viewport, in NormalizedRect crop, Vector2 mapNormalized)
    {
        ValidateScreenRect(viewport, nameof(viewport));
        ValidateNormalizedRect(crop, nameof(crop));
        Vector2 local = (mapNormalized - crop.Min) / crop.Size;
        return Project(viewport.Min, viewport.Size, local);
    }

    /// <summary>Inverse of <see cref="ProjectCrop"/>.</summary>
    public static Vector2 UnprojectCrop(
        in ScreenRect viewport, in NormalizedRect crop, Vector2 screen)
    {
        ValidateScreenRect(viewport, nameof(viewport));
        ValidateNormalizedRect(crop, nameof(crop));
        return crop.Min + Unproject(viewport.Min, viewport.Size, screen) * crop.Size;
    }

    /// <summary>
    /// Expand one WorldMapOverlay DBC row into its 1.12 texture chunks. Commander
    /// no-fog draws every row returned by the DBC, regardless of the character's
    /// exploration flags. Chunks are numbered row-major from 1, exactly as the
    /// stock FrameXML texture-name suffixes are.
    /// </summary>
    public static OverlayChunk[] NoFogOverlayChunks(
        int offsetX, int offsetY, int width, int height)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));

        int columns = (width + (int)OverlayChunkSize - 1) / (int)OverlayChunkSize;
        int rows = (height + (int)OverlayChunkSize - 1) / (int)OverlayChunkSize;
        var result = new OverlayChunk[checked(columns * rows)];
        int index = 0;
        for (int row = 0; row < rows; row++)
        for (int column = 0; column < columns; column++)
        {
            int contentWidth = Math.Min((int)OverlayChunkSize,
                width - column * (int)OverlayChunkSize);
            int contentHeight = Math.Min((int)OverlayChunkSize,
                height - row * (int)OverlayChunkSize);
            int textureWidth = OverlayTextureExtent(contentWidth);
            int textureHeight = OverlayTextureExtent(contentHeight);
            result[index] = new OverlayChunk(
                index + 1,
                new Vector2(offsetX + column * OverlayChunkSize,
                    offsetY + row * OverlayChunkSize),
                new Vector2(contentWidth, contentHeight),
                new Vector2(textureWidth, textureHeight),
                new Vector2((float)contentWidth / textureWidth,
                    (float)contentHeight / textureHeight));
            index++;
        }
        return result;
    }

    /// <summary>The stock texture path used by UpdateMapHighlight in 1.12.</summary>
    public static string ZoneHighlightPath(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("A map directory is required.", nameof(directory));
        return $@"Interface\WorldMap\{directory}\{directory}Highlight.blp";
    }

    /// <summary>
    /// Place the authored stock hover texture over a WorldMapArea rectangle.
    /// Its BLP is power-of-two padded; only the top-left destination-sized
    /// portion is sampled, matching UpdateMapHighlight's texture coordinates.
    /// </summary>
    public static ZoneHighlightPlacement PlaceZoneHighlight(
        string directory,
        in NormalizedRect zoneBounds,
        in ScreenRect viewport,
        in NormalizedRect crop)
    {
        ValidateNormalizedRect(zoneBounds, nameof(zoneBounds));
        Vector2 min = ProjectCrop(viewport, crop, zoneBounds.Min);
        Vector2 max = ProjectCrop(viewport, crop, zoneBounds.Max);
        int logicalHeight = Math.Max(1, (int)(128f / zoneBounds.MapPixelAspect));
        int fileHeight = 1;
        while (fileHeight < logicalHeight) fileHeight <<= 1;
        Vector2 uvMax = new(1f, MathF.Min(1f, logicalHeight / (float)fileHeight));
        return new ZoneHighlightPlacement(
            ZoneHighlightPath(directory),
            new ScreenRect(Vector2.Min(min, max), Vector2.Abs(max - min)),
            uvMax);
    }

    /// <summary>Project an exploration-overlay chunk through the active crop.</summary>
    public static ScreenRect ProjectOverlayChunk(
        in OverlayChunk chunk, in ScreenRect viewport, in NormalizedRect crop)
    {
        Vector2 min = ProjectCrop(viewport, crop, chunk.MapBounds.Min);
        Vector2 max = ProjectCrop(viewport, crop, chunk.MapBounds.Max);
        return new ScreenRect(Vector2.Min(min, max), Vector2.Abs(max - min));
    }

    public static string OverlayChunkPath(string textureBase, int textureIndex)
    {
        if (string.IsNullOrWhiteSpace(textureBase))
            throw new ArgumentException("An overlay texture base is required.", nameof(textureBase));
        if (textureIndex <= 0) throw new ArgumentOutOfRangeException(nameof(textureIndex));
        return textureBase + textureIndex;
    }

    public static bool CanTravelCamera(uint playerMap, uint destinationMap) =>
        playerMap == destinationMap;

    public static bool ShowCameraMarker(uint viewportMap, uint playerMap) =>
        viewportMap == playerMap;

    public static bool IsOverviewContinent(uint mapId) => mapId is 0 or 1;

    public static bool ShowWorldPresence(uint areaMap, ushort bots, ushort players) =>
        IsOverviewContinent(areaMap) && bots + players > 0;

    public static bool ShowHonor(byte mode, byte modules) =>
        mode == 1 && (modules & HonorModule) != 0;

    public static bool ShowHeroes(byte mode, byte modules) =>
        mode == 1 && (modules & HeroesModule) != 0;

    public static bool ShowFactionControl(byte mode, byte modules) =>
        mode == 1 && (modules & FactionControlModule) != 0;

    /// <summary>
    /// Client-side affordance only; the server remains authoritative for faction,
    /// slot, Honor, world-presence, and cost checks. Hero level zero means the
    /// candidate has not been declared.
    /// </summary>
    public static HeroAction HeroActionFor(
        byte mode, byte modules, bool ownFaction, bool eligibleBot,
        byte heroLevel, bool dead)
    {
        if (!ShowHeroes(mode, modules) || !ownFaction || !eligibleBot)
            return HeroAction.None;
        if (heroLevel == 0) return HeroAction.Declare;
        if (dead) return HeroAction.Revive;
        return heroLevel < MaximumHeroLevel ? HeroAction.Upgrade : HeroAction.None;
    }

    public static string CampaignStatus(byte mode, byte modules, long honor) =>
        mode != 1 ? string.Empty : ShowHonor(mode, modules)
            ? $"RTS CAMPAIGN  \u00B7  Honor {honor:n0}"
            : "RTS CAMPAIGN";

    /// <summary>Legacy selected-continent filter retained for the single-map renderer.</summary>
    public static bool ShowPresence(uint selectedMap, uint areaMap, ushort bots, ushort players) =>
        selectedMap == areaMap && bots + players > 0;

    private static int OverlayTextureExtent(int contentExtent)
    {
        int result = 16; // FrameXML's minimum backing dimension.
        while (result < contentExtent) result *= 2;
        return result;
    }

    private static void ValidatePositive(Vector2 value, string name)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) ||
            value.X <= 0f || value.Y <= 0f)
            throw new ArgumentOutOfRangeException(name);
    }

    private static void ValidateScreenRect(in ScreenRect rect, string name) =>
        ValidatePositive(rect.Size, name);

    private static void ValidateNormalizedRect(in NormalizedRect rect, string name)
    {
        Vector2 size = rect.Size;
        if (!float.IsFinite(rect.Min.X) || !float.IsFinite(rect.Min.Y) ||
            !float.IsFinite(rect.Max.X) || !float.IsFinite(rect.Max.Y) ||
            rect.Min.X < 0f || rect.Min.Y < 0f || rect.Max.X > 1f || rect.Max.Y > 1f ||
            size.X <= 0f || size.Y <= 0f)
            throw new ArgumentOutOfRangeException(name);
    }
}
