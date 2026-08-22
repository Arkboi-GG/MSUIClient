using System.Numerics;

namespace MSUIClient.Engine.UI;

/// <summary>Frozen TalentFrame FontString anchors from Blizzard_TalentUI.xml.</summary>
public static class TalentFrameUiLaw
{
    // TalentFrameTitleText TOP(0,-18), expressed as the center of its 12px em box.
    public static readonly Vector2 TitleCenter = new(192f, 24f);

    // PointsMiddle TOP is (207,48); SpentPoints TOP adds (0,-5) in y-up FrameXML.
    public const float SpentPointsCenterX = 207f;
    public const float SpentPointsTop = 53f;

    // TalentPointsText BOTTOMRIGHT -> frame BOTTOMLEFT(252,+87).
    public static readonly Vector2 TalentPointsBottomRight = new(252f, 425f);
    public const float TalentPointsLabelGap = 3f;

    public static string SpentPointsPrefix(string treeName) =>
        $"Points spent in {treeName} Talents: ";
}
