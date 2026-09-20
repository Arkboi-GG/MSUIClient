using System.Numerics;

namespace MSUIClient.Engine.UI;

public static class PetTrainingUiLaw
{
    public static uint TaughtAbility(uint[]? effects, uint[]? triggers)
    {
        if (effects is null || triggers is null) return 0;
        for (int lane = 0; lane < Math.Min(3, Math.Min(effects.Length, triggers.Length)); lane++)
            if (effects[lane] is 36 or 57 && triggers[lane] != 0) return triggers[lane];
        return 0;
    }

    public static Vector2 RowCostPosition(Vector2 row, float logicalWidth, float textY,
        float scale, float measuredWidth) => new(row.X + (logicalWidth - 15) * scale - measuredWidth, textY);
    public static Vector2 CostPosition(Vector2 requirements, float linePitch) =>
        requirements + new Vector2(0, linePitch);
    public static Vector2 PointsPosition(Vector2 origin, float scale, float valueWidth, float linePitch) =>
        origin + new Vector2(170, 426) * scale - new Vector2(valueWidth, linePitch);
    public static Vector2 PointsLabelPosition(Vector2 value, float labelWidth, float scale) =>
        value - new Vector2(labelWidth + 5 * scale, 0);
    public static bool CanTrain(bool ownLivingPet, uint level, uint requiredLevel,
        int availablePoints, int cost, bool alreadyKnown) =>
        ownLivingPet && !alreadyKnown && level >= requiredLevel && cost >= 0 &&
        (cost == 0 || availablePoints >= cost);
}
