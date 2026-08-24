using System.Numerics;

namespace MSUIClient.Engine.UI;

/// <summary>Current Benilla PetPaperDollFrame geometry and descriptor projection.</summary>
public static class PetPaperDollUiLaw
{
    public readonly record struct LogicalRect(float X, float Y, float Width, float Height)
    {
        public Vector2 Min => new(X, Y);
        public Vector2 Size => new(Width, Height);
        public Vector2 Center => new(X + Width * .5f, Y + Height * .5f);
    }

    public readonly record struct ArtSeat(string Path, LogicalRect Rect);
    public readonly record struct TooltipSeat(Vector2 Anchor, Vector2 Pivot);

    public static readonly LogicalRect Model = new(25, 78, 318, 224);
    public static readonly LogicalRect RotateLeft = new(23, 76, 35, 35);
    public static readonly LogicalRect RotateRight = new(58, 76, 35, 35);
    public static readonly LogicalRect Diet = new(30, 111, 24, 23);
    public static readonly LogicalRect Experience = new(23, 396, 319, 11);
    public static readonly LogicalRect Attributes = new(67, 300, 230, 78);
    public static readonly LogicalRect Resistance = new(315, 77, 32, 160);
    public static readonly LogicalRect Close = new(265, 411, 80, 22);
    public static readonly LogicalRect PetName = new(143.5f, 16, 109, 16);
    public const string PetNameFont = "GameFontHighlight";
    public const string PetLevelFont = "GameFontNormalSmall";
    public const string PetLoyaltyFont = "GameFontNormalSmall";
    public const string TrainingValueFont = "GameFontHighlightSmall";
    public const string TrainingLabelFont = "GameFontNormalSmall";
    public const string StatLabelFont = "GameFontNormalSmall";
    public const string StatValueFont = "GameFontHighlightSmall";
    public const string ResistanceFont = "GameFontHighlightSmall";
    public static readonly Vector2 PetLevelTopCenter = new(198, 38);
    public const float PetLoyaltyGap = 1;
    public static readonly Vector2 TrainingValueBottomRight = new(249, 426);
    public static readonly Vector2 ModelUvMin = new(0, 1);
    public static readonly Vector2 ModelUvMax = new(1, 0);
    public static readonly Vector2 StatBackgroundUvMax = new(.89f, .61f);
    public static readonly Vector2 DietUvMax = new(.1875f, .359375f);
    public static readonly Vector2 ExperienceDwarfUvMin = new(.203125f, .2890625f);
    public static readonly Vector2 ExperienceDwarfUvMax = new(.8046875f, .33984375f);
    public static readonly Vector4 ExperienceColor = new(.58f, 0, .55f, 1);
    public const string StatBackgroundPath =
        @"Interface\PaperDollInfoFrame\UI-Character-StatBackground";
    public const string ResistanceIconsPath =
        @"Interface\PaperDollInfoFrame\UI-Character-ResistanceIcons";
    public const string ExperienceDwarfPath = @"Interface\MainMenuBar\UI-MainMenuBar-Dwarf";
    public const string DietPath = @"Interface\PetPaperDollFrame\UI-PetHappiness";
    public static readonly ArtSeat[] BackgroundArt =
    [
        new(@"Interface\PaperDollInfoFrame\UI-Character-General-TopLeft",
            new(2, 1, 256, 256)),
        new(@"Interface\PaperDollInfoFrame\UI-Character-General-TopRight",
            new(258, 1, 128, 256)),
        new(@"Interface\PetPaperDollFrame\UI-PetPaperDollFrame-BotLeft",
            new(2, 257, 256, 256)),
        new(@"Interface\PetPaperDollFrame\UI-PetPaperDollFrame-BotRight",
            new(258, 257, 128, 256)),
    ];
    public static readonly int[] ResistanceSchoolIds = [6, 2, 3, 4, 5];
    public static readonly (float Top, float Bottom)[] ResistanceUvs =
    [
        (.2265625f, .33984375f), (0, .11328125f), (.11328125f, .2265625f),
        (.33984375f, .453125f), (.453125f, .56640625f),
    ];
    public static readonly string[] StatNames =
        ["Strength", "Agility", "Stamina", "Intellect", "Spirit"];
    public static readonly string[] ResistanceNames =
        ["Arcane Resistance", "Fire Resistance", "Nature Resistance", "Frost Resistance", "Shadow Resistance"];

    public const int StatRows = 5;
    public const float StatRowWidth = 104;
    public const float StatRowHeight = 13;
    public const float StatRowPitch = 14;
    public const float LeftStatX = 73;
    public const float RightStatX = 189;
    public const float StatTop = 305;
    public const float LiveAnimationMaxStep = .05f;

    public static LogicalRect StatRow(bool right, int row) => new(
        right ? RightStatX : LeftStatX,
        StatTop + Math.Clamp(row, 0, StatRows - 1) * StatRowPitch,
        StatRowWidth, StatRowHeight);

    public static LogicalRect ResistanceRow(int row) =>
        new(315, 77 + Math.Clamp(row, 0, 4) * 29, 32, 29);

    // Pet diet, stat, combat-stat, and resistance owners all use ANCHOR_RIGHT.
    public static TooltipSeat RightTooltipSeat(Vector2 ownerMin, Vector2 ownerSize) =>
        new(ownerMin + Vector2.UnitX * ownerSize.X, Vector2.UnitY);

    public static Vector2 ResistanceUvMin(int row) =>
        new(0, ResistanceUvs[Math.Clamp(row, 0, ResistanceUvs.Length - 1)].Top);

    public static Vector2 ResistanceUvMax(int row) =>
        new(1, ResistanceUvs[Math.Clamp(row, 0, ResistanceUvs.Length - 1)].Bottom);

    public static LogicalRect AttributePlate(bool right) =>
        new(Attributes.X + (right ? 115 : 0), Attributes.Y, 114, 78);

    public static LogicalRect ExperienceDwarfPiece(bool right) =>
        right ? new(160, 0, 159, 13) : new(0, 0, 160, 13);

    public static Vector4 PanelClip(Vector2 origin, float scale) => new(
        origin.X, origin.Y, origin.X + 384 * scale, origin.Y + 512 * scale);

    public static Vector2 PetNameCenter(Vector2 origin, float scale) =>
        origin + PetName.Center * scale;

    public static Vector2 PetLevelCenter(Vector2 origin, float scale) =>
        origin + PetLevelTopCenter * scale +
            new Vector2(0, GameText.EmPixels(PetLevelFont, scale) * .5f);

    public static Vector2 PetLoyaltyCenter(Vector2 origin, float scale) =>
        origin + PetLevelTopCenter * scale + new Vector2(0,
            GameText.EmPixels(PetLevelFont, scale) + PetLoyaltyGap * scale +
            GameText.EmPixels(PetLoyaltyFont, scale) * .5f);

    public static Vector2 StatLabelMin(Vector2 rowMin, float scale) => new(
        rowMin.X,
        GameText.BoxCenteredTop(StatLabelFont, rowMin.Y, StatRowHeight, scale));

    public static Vector2 StatValueRightTop(Vector2 rowMin, float scale) => new(
        rowMin.X + StatRowWidth * scale,
        GameText.BoxCenteredTop(StatValueFont, rowMin.Y, StatRowHeight, scale));

    public static Vector2 ResistanceTextCenter(Vector2 rowMin, float scale) =>
        rowMin + new Vector2(16, 21) * scale;

    public static Vector2 TrainingValueTopRight(Vector2 origin, float scale) =>
        origin + TrainingValueBottomRight * scale -
            new Vector2(0, GameText.EmPixels(TrainingValueFont, scale));

    public static Vector2 TrainingLabelRightTop(Vector2 origin, float scale,
        float valueWidth) =>
        new(TrainingValueTopRight(origin, scale).X - valueWidth - 5 * scale,
            origin.Y + TrainingValueBottomRight.Y * scale -
                (GameText.EmPixels(TrainingValueFont, scale) +
                 GameText.EmPixels(TrainingLabelFont, scale)) * .5f);

    public static float ExperienceFraction(uint current, uint next) =>
        next == 0 ? 0 : Math.Clamp((float)current / next, 0, 1);

    public static (ushort Total, ushort Spent) TrainingPoints(uint packed) =>
        ((ushort)(packed >> 16), (ushort)packed);

    /// <summary>Advance the live pet model pane without catching up time spent closed.</summary>
    public static float LiveAnimationStep(double now, double previous) =>
        previous > 0 && double.IsFinite(now) && double.IsFinite(previous)
            ? (float)Math.Clamp(now - previous, 0, LiveAnimationMaxStep)
            : 0f;

    /// <summary>
    /// UnitAttackBothHands and UnitDefense take the CGUnit (non-player) branch for
    /// pets: both return level * 5 with a zero modifier. Creatures have no player
    /// skill block to stream, so this is the authored client calculation.
    /// </summary>
    public static uint CreatureSkill(uint level) => level * 5;

    /// <summary>
    /// The reference only assigns PetLevelText after UnitCreatureFamily succeeds. A pet whose
    /// family has not resolved yet therefore has a blank level line, not a bare "Level N".
    /// </summary>
    public static string LevelText(uint level, string? family) =>
        string.IsNullOrWhiteSpace(family) ? "" : $"Level {level} {family.Trim()}";

    public static string LoyaltyName(byte level) => level switch
    {
        1 => "Rebellious",
        2 => "Unruly",
        3 => "Submissive",
        4 => "Dependable",
        5 => "Faithful",
        6 => "Best Friend",
        _ => "",
    };

    /// <summary>
    /// Unlike the player page, the pet resistance hover has no quality subtext and does not put
    /// the effective total before the optional base/buff split.
    /// </summary>
    public static string ResistanceTooltip(string name, int effective, int positive, int negative)
    {
        if (positive == 0 && negative == 0) return name;
        int baseValue = effective - positive - negative;
        string positivePart = positive > 0 ? $" +{positive}" : "";
        string negativePart = negative < 0 ? $" {negative}" : "";
        return $"{name} ( {baseValue}{positivePart}{negativePart} )";
    }
}
