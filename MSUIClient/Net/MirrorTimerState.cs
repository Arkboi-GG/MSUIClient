namespace MSUIClient.Net;

public enum MirrorTimerKind : uint
{
    Fatigue = 0,
    Breath = 1,
    FeignDeath = 2,
}

public readonly record struct MirrorTimerStart(
    uint RawKind, uint RemainingMs, uint DurationMs, int Scale, bool Paused, uint SpellId)
{
    public MirrorTimerKind? Kind => RawKind <= 2 ? (MirrorTimerKind)RawKind : null;
}

/// <summary>Strict build-5875 mirror-timer packet bodies.</summary>
public static class MirrorTimerPackets
{
    public static MirrorTimerStart ParseStart(byte[] body)
    {
        if (body.Length != 21)
            throw new InvalidDataException(
                $"SMSG_START_MIRROR_TIMER expected 21 bytes, got {body.Length}");
        var reader = new PacketReader(body);
        MirrorTimerStart packet = new(reader.ReadU32(), reader.ReadU32(), reader.ReadU32(),
            reader.ReadI32(), reader.ReadU8() != 0, reader.ReadU32());
        RequireConsumed(reader, nameof(ParseStart));
        return packet;
    }

    public static (uint RawKind, bool Paused) ParsePause(byte[] body)
    {
        if (body.Length != 5)
            throw new InvalidDataException(
                $"SMSG_PAUSE_MIRROR_TIMER expected 5 bytes, got {body.Length}");
        var reader = new PacketReader(body);
        var packet = (reader.ReadU32(), reader.ReadU8() != 0);
        RequireConsumed(reader, nameof(ParsePause));
        return packet;
    }

    public static uint ParseStop(byte[] body)
    {
        if (body.Length != 4)
            throw new InvalidDataException(
                $"SMSG_STOP_MIRROR_TIMER expected 4 bytes, got {body.Length}");
        return new PacketReader(body).ReadU32();
    }

    private static void RequireConsumed(PacketReader reader, string packet)
    {
        if (reader.Remaining != 0)
            throw new InvalidDataException($"{packet} has {reader.Remaining} trailing bytes");
    }
}

/// <summary>The reference's fixed three-bar pool and server-rate integration.</summary>
public sealed class MirrorTimerState
{
    public const int FrameCount = 3;

    public sealed class ActiveTimer
    {
        public required MirrorTimerKind Kind { get; init; }
        public double ValueSeconds { get; set; }
        public double DurationSeconds { get; set; }
        public int Scale { get; set; }
        public bool Paused { get; set; }
        public uint SpellId { get; set; }
        public double UpdatedAt { get; set; }
    }

    private readonly ActiveTimer?[] _frames = new ActiveTimer?[FrameCount];
    public IReadOnlyList<ActiveTimer?> Frames => _frames;

    public ActiveTimer? Start(in MirrorTimerStart packet, double now)
    {
        if (packet.Kind is not { } kind) return null;
        int slot = Array.FindIndex(_frames, timer => timer?.Kind == kind);
        if (slot < 0) slot = Array.FindIndex(_frames, timer => timer is null);
        if (slot < 0) return null;
        var timer = new ActiveTimer
        {
            Kind = kind,
            ValueSeconds = packet.RemainingMs / 1000.0,
            DurationSeconds = packet.DurationMs / 1000.0,
            Scale = packet.Scale,
            Paused = packet.Paused,
            SpellId = packet.SpellId,
            UpdatedAt = now,
        };
        _frames[slot] = timer;
        return timer;
    }

    public bool Pause(uint rawKind, bool paused, double now)
    {
        if (rawKind > 2) return false;
        ActiveTimer? timer = _frames.FirstOrDefault(x => x?.Kind == (MirrorTimerKind)rawKind);
        if (timer is null) return false;
        timer.ValueSeconds = ValueAt(timer, now);
        timer.UpdatedAt = now;
        timer.Paused = paused;
        return true;
    }

    public bool Stop(uint rawKind)
    {
        if (rawKind > 2) return false;
        int slot = Array.FindIndex(_frames, timer => timer?.Kind == (MirrorTimerKind)rawKind);
        if (slot < 0) return false;
        _frames[slot] = null;
        return true;
    }

    public static double ValueAt(ActiveTimer timer, double now)
    {
        double value = timer.ValueSeconds +
            (timer.Paused ? 0 : timer.Scale * Math.Max(0, now - timer.UpdatedAt));
        return Math.Clamp(value, 0, Math.Max(0, timer.DurationSeconds));
    }

    public static float FractionAt(ActiveTimer timer, double now) =>
        timer.DurationSeconds <= 0 ? 0 :
        (float)Math.Clamp(ValueAt(timer, now) / timer.DurationSeconds, 0, 1);

    public void Clear() => Array.Clear(_frames);
}
