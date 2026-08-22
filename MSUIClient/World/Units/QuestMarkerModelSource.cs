using System.Numerics;
using MSUIClient.Engine;
using MSUIClient.Formats;

namespace MSUIClient.World.Units;

public readonly record struct QuestMarkerModelRequest(
    ulong Guid,
    string ModelPath,
    bool Raised,
    bool Mounted);

/// <summary>Pure geometry/attachment laws for the Build-5875 TalkToMe marker M2s.</summary>
public static class QuestMarkerModelLaw
{
    public const ushort OverheadAttachment = 18;
    public const ushort MountedOverheadAttachment = 29;
    public const int LowAnimationId = 0;
    public const int RaisedAnimationId = 190;

    public static SpellAttachment.Point? Attachment(M2Model body, bool mounted)
    {
        if (mounted && SpellAttachment.ResolveExact(body, MountedOverheadAttachment) is { } mountedPoint)
            return mountedPoint;
        return SpellAttachment.ResolveExact(body, OverheadAttachment);
    }

    public static int SequenceIndex(M2Model marker, bool raised)
    {
        int sequence = marker.TryFindSequenceIndexByAnimationId(
            raised ? RaisedAnimationId : LowAnimationId);
        if (sequence < 0 && raised)
            sequence = marker.TryFindSequenceIndexByAnimationId(LowAnimationId);
        return sequence;
    }

    /// <summary>
    /// Client 0x607570: one over the posed attachment joint's X-basis length.
    /// Null means transform propagation has not produced a usable basis yet.
    /// </summary>
    public static float? CounterScale(Matrix4x4 attachmentWorld)
    {
        float length = Vector3.TransformNormal(Vector3.UnitX, attachmentWorld).Length();
        if (!float.IsFinite(length) || length <= 0f) return null;
        return length == 1f ? 1f : 1f / length;
    }

    public static Matrix4x4 SeatTransform(Matrix4x4 attachmentWorld, float counterScale)
        => Matrix4x4.CreateScale(counterScale) * attachmentWorld;
}

/// <summary>
/// Loads the five authored TalkToMe M2s and turns current marker requests into
/// ordinary spell-mesh draws. The shared M2 renderer supplies skeletal animation,
/// cylindrical billboard bones, authored materials, depth and world lighting.
/// </summary>
public sealed class QuestMarkerModelSource
{
    private sealed class SeatState
    {
        public required string Path;
        public required M2Model BodyModel;
        public ushort AttachmentId;
        public bool Mounted;
        public bool Raised;
        public bool NoAnchor;
        public bool ScaleLatched;
        public float CounterScale = 1f;
        public double ArmedAt;
    }

    private readonly MpqMount _mpq;
    private readonly Dictionary<string, M2Model?> _models =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ulong, SeatState> _seats = [];

    public QuestMarkerModelSource(MpqMount mpq) => _mpq = mpq;

    public void Clear() => _seats.Clear();

    public IReadOnlyList<SpellMeshDraw> Build(
        double now,
        IEnumerable<QuestMarkerModelRequest> requests,
        Func<ulong, SpellUnitPose> unitPose)
    {
        var draws = new List<SpellMeshDraw>();
        var live = new HashSet<ulong>();

        foreach (QuestMarkerModelRequest request in requests)
        {
            live.Add(request.Guid);
            M2Model? marker = ResolveModel(request.ModelPath);
            if (marker is null || !marker.IsValid) continue;

            SpellUnitPose pose = unitPose(request.Guid);
            if (!pose.Found || pose.Model is not { } body || pose.Skin is null) continue;

            SpellAttachment.Point? point = QuestMarkerModelLaw.Attachment(body, request.Mounted);
            ushort attachmentId = point?.ResolvedId ?? (request.Mounted
                ? QuestMarkerModelLaw.MountedOverheadAttachment
                : QuestMarkerModelLaw.OverheadAttachment);

            if (!_seats.TryGetValue(request.Guid, out SeatState? seat) ||
                !string.Equals(seat.Path, request.ModelPath, StringComparison.OrdinalIgnoreCase) ||
                !ReferenceEquals(seat.BodyModel, body) ||
                seat.AttachmentId != attachmentId ||
                seat.Mounted != request.Mounted)
            {
                seat = new SeatState
                {
                    Path = request.ModelPath,
                    BodyModel = body,
                    AttachmentId = attachmentId,
                    Mounted = request.Mounted,
                    Raised = request.Raised,
                    NoAnchor = point is null,
                    ArmedAt = now,
                };
                _seats[request.Guid] = seat;
            }
            else if (seat.Raised != request.Raised)
            {
                seat.Raised = request.Raised;
                seat.ArmedAt = now;
            }

            // The client creates an unparented marker when attachment 18/29 is absent.
            // It remains invisible until a model swap or mount transition rebuilds its seat.
            if (seat.NoAnchor || point is not { } attach) continue;

            Matrix4x4 attachmentWorld = SpellAttachment.World(body, attach,
                pose.UnitTransform, pose.BoneMatrix);
            if (!seat.ScaleLatched)
            {
                if (QuestMarkerModelLaw.CounterScale(attachmentWorld) is not { } scale) continue;
                seat.CounterScale = scale;
                seat.ScaleLatched = true;
            }

            int sequence = QuestMarkerModelLaw.SequenceIndex(marker, request.Raised);
            float age = (float)Math.Max(0d, now - seat.ArmedAt);
            draws.Add(new SpellMeshDraw(
                unchecked((long)request.Guid ^ long.MinValue),
                request.ModelPath,
                marker,
                QuestMarkerModelLaw.SeatTransform(attachmentWorld, seat.CounterScale),
                age,
                sequence,
                false,
                null,
                Vector3.One,
                1f));
        }

        foreach (ulong stale in _seats.Keys.Where(guid => !live.Contains(guid)).ToArray())
            _seats.Remove(stale);

        return draws;
    }

    private M2Model? ResolveModel(string path)
    {
        path = path.Replace('/', Path.DirectorySeparatorChar);
        if (_models.TryGetValue(path, out M2Model? cached)) return cached;
        byte[]? bytes = _mpq.ReadFile(path);
        M2Model? model = bytes is null ? null : M2Reader.Parse(bytes);
        _models[path] = model;
        return model;
    }
}
