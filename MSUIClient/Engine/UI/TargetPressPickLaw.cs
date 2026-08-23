using System.Numerics;

namespace MSUIClient.Engine.UI;

/// <summary>
/// The world subject frozen on a primary-button down edge. Hover can disappear during
/// camera look and units can move before release; neither is allowed to change the gesture.
/// </summary>
public readonly record struct TargetPressPick(
    bool Armed, ulong UnitGuid, ulong GameObjectGuid, Vector3? GroundPoint);

public static class TargetPressPickLaw
{
    public static TargetPressPick Update(
        TargetPressPick current, bool down, bool wasDown, bool otherDown,
        ulong unitGuid, ulong gameObjectGuid, Vector3? groundPoint)
    {
        if (!down || wasDown) return current;
        return otherDown ? default : new(true, unitGuid, gameObjectGuid, groundPoint);
    }

    /// <summary>A two-primary-button chord is camera control and owns no world subject.</summary>
    public static (TargetPressPick Left, TargetPressPick Right) CancelChord(
        bool leftDown, bool rightDown, TargetPressPick left, TargetPressPick right) =>
        leftDown && rightDown ? (default, default) : (left, right);
}
