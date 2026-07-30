using System.Globalization;
using MSUIClient.Net;

namespace MSUIClient.Engine;

public interface IVerdict
{
    double Time { get; }
    string Channel { get; }
    string ToLine();
}

public enum PortraitSubject
{
    Player,
    Target,
    PaperDoll,
    Lab,
}

public enum PortraitCameraSource
{
    Authored,
    Bounds,
    Override,
}

public enum PortraitOutcome
{
    Ready,
    Blank,
    NotDrawn,
    Skipped,
}

public readonly record struct PortraitVerdict(
    double Time,
    PortraitSubject Subject,
    PortraitOutcome Outcome,
    PortraitCameraSource CameraSource,
    bool AuthoredRetriedAsBounds,
    int SubjectPixels,
    int RgbLo,
    int RgbHi,
    int AlphaLo,
    int AlphaHi,
    int Pieces,
    int DisplayId,
    float BindPoseHeight,
    float EyeHeight,
    float Distance,
    float FovyDegrees,
    float NearPlane) : IVerdict
{
    public string Channel => "portrait";

    public string ToLine() => string.Format(CultureInfo.InvariantCulture,
        "time={0:F3} subject={1} outcome={2} camera={3} authoredRetried={4} " +
        "subjectPx={5} rgb={6}..{7} alpha={8}..{9} pieces={10} displayId={11} " +
        "bindPoseHeight={12:F4} eyeHeight={13:F4} distance={14:F4} fovyDeg={15:F4} near={16:F4}",
        Time, Subject, Outcome, CameraSource, AuthoredRetriedAsBounds,
        SubjectPixels, RgbLo, RgbHi, AlphaLo, AlphaHi, Pieces, DisplayId,
        BindPoseHeight, EyeHeight, Distance, FovyDegrees, NearPlane);
}

public readonly record struct CastVerdict(
    double Time,
    uint SpellId,
    CastTargetReason Reason,
    ulong SelectedGuid,
    ulong ResolvedGuid,
    bool Sent) : IVerdict
{
    public string Channel => "cast";

    public string ToLine() => string.Format(CultureInfo.InvariantCulture,
        "time={0:F3} spell={1} reason={2} selected=0x{3:X16} resolved=0x{4:X16} sent={5}",
        Time, SpellId, Reason, SelectedGuid, ResolvedGuid, Sent);
}

public enum AnimChoiceKind { Exact, BakedOnDemand, Fallback, Missing, Substituted }

public readonly record struct AnimChoice(
    double Time,
    string Unit,
    int Track,
    int RequestedId,
    int PlayedId,
    AnimChoiceKind Kind) : IVerdict
{
    public string Channel => "anim";

    public string ToLine() => string.Format(CultureInfo.InvariantCulture,
        "time={0:F3} unit={1} track={2} requested={3} played={4} kind={5}",
        Time, Unit, Track, RequestedId, PlayedId, Kind);
}

public enum ButtonUsability { Usable, NotEnoughPower, Unusable }
public enum ButtonRange { NoCheck, InRange, OutOfRange }

public readonly record struct ActionButtonVerdict(
    double Time,
    int Slot,
    bool IsItem,
    uint ActionId,
    ButtonUsability Usability,
    ButtonRange Range,
    bool Pushed,
    bool Hover,
    bool Checked,
    bool Flashing,
    bool CarriedGrid,
    bool EquippedBorder,
    int PowerCost,
    int CurrentPower,
    int BaseMana,
    int RangeIndex,
    float RangeMin,
    float RangeMax,
    float DistanceToTarget,
    int StackCount) : IVerdict
{
    public string Channel => "action";

    public string ToLine() => string.Format(CultureInfo.InvariantCulture,
        "time={0:F3} slot={1} {2}={3} usable={4} range={5} pushed={6} hover={7} " +
        "checked={8} flashing={9} grid={10} equipped={11} cost={12} power={13} " +
        "baseMana={14} rangeIndex={15} min={16:F2} max={17:F2} dist={18:F2} stack={19}",
        Time, Slot, IsItem ? "item" : "spell", ActionId, Usability, Range, Pushed, Hover,
        Checked, Flashing, CarriedGrid, EquippedBorder, PowerCost, CurrentPower, BaseMana,
        RangeIndex, RangeMin, RangeMax, DistanceToTarget, StackCount);
}

public sealed class VerdictRing
{
    private const int Capacity = 256;
    private readonly IVerdict?[] _items = new IVerdict?[Capacity];
    private int _start;
    private int _count;

    public void Add(IVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);

        int index = (_start + _count) % Capacity;
        if (_count == Capacity)
        {
            _items[_start] = verdict;
            _start = (_start + 1) % Capacity;
            return;
        }

        _items[index] = verdict;
        _count++;
    }

    public IReadOnlyList<IVerdict> Snapshot()
    {
        var result = new IVerdict[_count];
        for (int i = 0; i < _count; i++)
            result[i] = _items[(_start + i) % Capacity]!;
        return result;
    }

    public IEnumerable<T> Recent<T>(int max) where T : IVerdict
    {
        if (max <= 0 || _count == 0) return Array.Empty<T>();

        var newestFirst = new List<T>(Math.Min(max, _count));
        for (int offset = 0; offset < _count && newestFirst.Count < max; offset++)
        {
            int index = (_start + _count - 1 - offset + Capacity) % Capacity;
            if (_items[index] is T verdict) newestFirst.Add(verdict);
        }

        newestFirst.Reverse();
        return newestFirst;
    }
}
