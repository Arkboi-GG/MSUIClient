namespace MSUIClient.Net;

/// <summary>
/// Build-5875 GameObject state/one-shot animation law. GAMEOBJECT_STATE owns
/// the held Opened/Closed/Destroyed pose and the one-window motion used to get
/// there; the custom and despawn opcodes share the separate transient channel.
/// </summary>
public static class GameObjectAnimationLaw
{
    public const uint StateActive = 0;
    public const uint StateReady = 1;
    public const uint StateAlternative = 2;

    public const int CloseAnimationId = 146;
    public const int ClosedAnimationId = 147;
    public const int OpenAnimationId = 148;
    public const int OpenedAnimationId = 149;
    public const int DestroyAnimationId = 150;
    public const int DestroyedAnimationId = 151;
    public const int RebuildAnimationId = 152;
    public const int FirstCustomAnimationId = 153;
    public const int CustomAnimationCount = 4;
    public const int DespawnAnimationId = 157;

    public enum StatePlayKind { Rest, Motion }

    public readonly record struct StatePlay(int AnimationId, StatePlayKind Kind);

    public readonly record struct OwnedAnimation(int AnimationId, bool Frozen);

    /// <summary>The byte-verified family-A handler census in the 1.12 client.</summary>
    public static bool Animates(uint typeId) => typeId is
        0 or 1 or 2 or 3 or 6 or 8 or 9 or 10 or 12 or 16 or 17 or 18 or 19 or
        23 or 24 or 26 or 27 or 28 or 29 or 30;

    /// <summary>Only doors and buttons lose their collider while open.</summary>
    public static bool CollisionFollowsState(uint typeId) => typeId is 0 or 1;

    /// <summary>An omitted create-field is its wire zero: ACTIVE/open/passable.</summary>
    public static bool ColliderIsSolid(uint? wireState) =>
        (wireState ?? StateActive) == StateReady;

    public static int? RestAnimationId(uint state) => state switch
    {
        StateActive => OpenedAnimationId,
        StateReady => ClosedAnimationId,
        StateAlternative => DestroyedAnimationId,
        _ => null,
    };

    public static int? MotionAnimationId(uint previous, uint current) =>
        (previous, current) switch
        {
            (StateReady, StateActive) => OpenAnimationId,
            (StateActive, StateReady) => CloseAnimationId,
            (StateAlternative, StateReady) => RebuildAnimationId,
            (StateReady, StateAlternative) => DestroyAnimationId,
            _ => null,
        };

    /// <summary>First sight snaps to a rest pose; a genuine edge swings once.</summary>
    public static StatePlay? ResolveStatePlay(uint? previous, uint current)
    {
        if (previous is uint prior && MotionAnimationId(prior, current) is int motion)
            return new(motion, StatePlayKind.Motion);
        return RestAnimationId(current) is int rest
            ? new(rest, StatePlayKind.Rest)
            : null;
    }

    /// <summary>
    /// The reference door-family missing-sequence remap. The frozen legs use
    /// frame zero of Open/Close as the absent Closed/Opened rest pose.
    /// </summary>
    public static OwnedAnimation RemapMissing(int requested, Func<int, bool> owns)
    {
        if (owns(requested)) return new(requested, false);
        return requested switch
        {
            CloseAnimationId when owns(OpenAnimationId) => new(CloseAnimationId, false),
            CloseAnimationId => new(ClosedAnimationId, false),
            ClosedAnimationId when owns(CloseAnimationId) => new(ClosedAnimationId, false),
            ClosedAnimationId when owns(OpenAnimationId) => new(OpenAnimationId, true),
            ClosedAnimationId => new(0, false),
            OpenAnimationId when owns(CloseAnimationId) => new(OpenAnimationId, false),
            OpenAnimationId when owns(DestroyAnimationId) => new(DestroyAnimationId, false),
            OpenAnimationId => new(OpenedAnimationId, false),
            OpenedAnimationId when owns(OpenAnimationId) => new(OpenedAnimationId, false),
            OpenedAnimationId when owns(CloseAnimationId) => new(CloseAnimationId, true),
            OpenedAnimationId => new(DestroyedAnimationId, false),
            _ => new(requested, false),
        };
    }

    public static int? CustomAnimationId(uint wireAnimation) =>
        wireAnimation < CustomAnimationCount
            ? FirstCustomAnimationId + (int)wireAnimation
            : null;

    public static bool ShouldRetainDestroy(bool despawnAnnounced,
        bool placementPresent, bool clipOwned) =>
        despawnAnnounced && placementPresent && clipOwned;

    public static double RetainedUntil(double startedAt, float durationSeconds) =>
        startedAt + Math.Max(0, durationSeconds);

    public static bool RetentionFinished(double now, double retainedUntil) =>
        now >= retainedUntil;
}
