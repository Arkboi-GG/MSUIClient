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

public enum MoveTransitionKind { GroundState, Clip, Gait }

public readonly record struct MoveVerdict(
    double Time,
    MoveTransitionKind Kind,
    string From,
    string To,
    int FromClipId,
    int ToClipId,
    float ClipTimeAtCut) : IVerdict
{
    public string Channel => "move";

    public string ToLine() => string.Format(CultureInfo.InvariantCulture,
        "time={0:F3} kind={1} from={2} to={3} fromClip={4} toClip={5} clipTimeAtCut={6:F6}",
        Time, Kind, From, To, FromClipId, ToClipId, ClipTimeAtCut);
}

public readonly record struct CombatVerdict(
    double Time,
    string Event,
    string Cause,
    ulong TargetGuid,
    string Detail) : IVerdict
{
    public string Channel => "combat";

    public string ToLine() => string.Format(CultureInfo.InvariantCulture,
        "time={0:F3} event={1} cause={2} target=0x{3:X16} detail={4}",
        Time, Event, Cause, TargetGuid, Detail.Replace(' ', '_'));
}

public readonly record struct InterfaceVerdict(
    double Time,
    string Family,
    string Step,
    string Outcome,
    ulong SourceGuid,
    string Detail) : IVerdict
{
    public string Channel => "interface";

    public string ToLine() => string.Format(CultureInfo.InvariantCulture,
        "time={0:F3} family={1} step={2} outcome={3} source=0x{4:X16} detail={5}",
        Time, Family, Step, Outcome, SourceGuid, Detail.Replace(' ', '_'));
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
    private readonly record struct Stored(long Sequence, IVerdict Verdict);

    private sealed class ChannelRing(int capacity)
    {
        private readonly Stored?[] _items = new Stored?[capacity];
        private int _start;
        private int _count;

        public void Add(Stored item)
        {
            int index = (_start + _count) % _items.Length;
            if (_count == _items.Length)
            {
                _items[_start] = item;
                _start = (_start + 1) % _items.Length;
                return;
            }

            _items[index] = item;
            _count++;
        }

        public Stored[] Snapshot()
        {
            var result = new Stored[_count];
            for (int i = 0; i < _count; i++)
                result[i] = _items[(_start + i) % _items.Length]!.Value;
            return result;
        }
    }

    public static IReadOnlyList<string> Channels { get; } =
        ["portrait", "cast", "action", "anim", "move", "combat", "interface"];

    private readonly Dictionary<string, ChannelRing> _channels =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["portrait"] = new(64),
            ["cast"] = new(128),
            ["action"] = new(512),
            ["anim"] = new(1024),
            ["move"] = new(1024),
        ["combat"] = new(2048),
        ["interface"] = new(2048),
        };
    private long _nextSequence;

    public void Add(IVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);

        if (!_channels.TryGetValue(verdict.Channel, out ChannelRing? channel))
            throw new ArgumentException($"Unknown verdict channel '{verdict.Channel}'", nameof(verdict));
        channel.Add(new Stored(_nextSequence++, verdict));
    }

    public IReadOnlyList<IVerdict> Snapshot(string channel)
    {
        return _channels.TryGetValue(channel, out ChannelRing? ring)
            ? ring.Snapshot().Select(item => item.Verdict).ToArray()
            : Array.Empty<IVerdict>();
    }

    public IReadOnlyList<IVerdict> SnapshotAll()
    {
        return _channels.Values.SelectMany(ring => ring.Snapshot())
            .OrderBy(item => item.Verdict.Time)
            .ThenBy(item => item.Sequence)
            .Select(item => item.Verdict)
            .ToArray();
    }

    public IEnumerable<T> Recent<T>(int max) where T : IVerdict
    {
        return max <= 0
            ? Array.Empty<T>()
            : SnapshotAll().OfType<T>().TakeLast(max).ToArray();
    }
}
