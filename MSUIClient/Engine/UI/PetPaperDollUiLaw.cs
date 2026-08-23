using System.Numerics;

namespace MSUIClient.Engine.UI;

/// <summary>Current Benilla PetPaperDollFrame geometry and descriptor projection.</summary>
public static class PetPaperDollUiLaw
{
    public readonly record struct LogicalRect(float X, float Y, float Width, float Height)
    {
        public Vector2 Min => new(X, Y);
        public Vector2 Size => new(Width, Height);
    }

    public static readonly LogicalRect Model = new(25, 78, 318, 224);
    public static readonly LogicalRect RotateLeft = new(23, 76, 35, 35);
    public static readonly LogicalRect RotateRight = new(58, 76, 35, 35);
    public static readonly LogicalRect Diet = new(30, 111, 24, 23);
    public static readonly LogicalRect Experience = new(23, 396, 319, 11);
    public static readonly LogicalRect Attributes = new(67, 300, 230, 78);
    public static readonly LogicalRect Resistance = new(315, 77, 32, 160);
    public static readonly LogicalRect Close = new(265, 411, 80, 22);
    public static readonly LogicalRect PetName = new(143.5f, 16, 109, 16);
    public static readonly LogicalRect PetLevel = new(120, 38, 158, 12);
    public static readonly LogicalRect PetLoyalty = new(120, 51, 158, 12);
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

    public static LogicalRect StatRow(bool right, int row) => new(
        right ? RightStatX : LeftStatX,
        StatTop + Math.Clamp(row, 0, StatRows - 1) * StatRowPitch,
        StatRowWidth, StatRowHeight);

    public static LogicalRect ResistanceRow(int row) =>
        new(315, 77 + Math.Clamp(row, 0, 4) * 29, 32, 29);

    public static float ExperienceFraction(uint current, uint next) =>
        next == 0 ? 0 : Math.Clamp((float)current / next, 0, 1);

    public static (ushort Total, ushort Spent) TrainingPoints(uint packed) =>
        ((ushort)(packed >> 16), (ushort)packed);

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
