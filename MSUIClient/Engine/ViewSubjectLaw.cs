using System.Numerics;

namespace MSUIClient.Engine;

/// <summary>Pure build-5875 camera-subject rules for PLAYER_FARSIGHT / Bind Sight.</summary>
public static class ViewSubjectLaw
{
    public const ushort PlayerFarsightField = 712;
    public const float Attachment17Lift = 0.0972f;
    public const float PivotFloor = 5f / 6f;
    public const float PivotFallback = 1.8f;

    public static float PivotHeight(float? attachment17Z, float minHeight, float maxHeight,
        float scale)
    {
        float local = attachment17Z is float z
            ? z + Attachment17Lift
            : 0.9f * MathF.Max(0f, maxHeight - minHeight);
        return MathF.Max(PivotFloor, local * MathF.Max(0.01f, scale));
    }

    public static Vector3 EyeTarget(Vector3 feet, float pivotHeight) =>
        feet + new Vector3(0f, 0f, pivotHeight);

    public static byte[] VoteBody(bool engage) => [engage ? (byte)1 : (byte)0];
}
