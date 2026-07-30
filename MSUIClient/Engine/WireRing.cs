using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using MSUIClient.Net;

namespace MSUIClient.Engine;

public readonly record struct WirePacket(
    double Time, bool Outgoing, ushort Opcode, string OpcodeName, int Size);

public readonly record struct WirePacketDetail(WirePacket Packet, byte[] Prefix);

public sealed class WireRing
{
    private const int Capacity = 512;
    private const int DisplayPrefixSize = 16;
    private static readonly IReadOnlyDictionary<ushort, string> OpcodeNames =
        Enum.GetValues<Op>()
            .GroupBy(op => (ushort)op)
            .ToDictionary(group => group.Key, group => group.First().ToString());

    private readonly object _gate = new();
    private readonly WirePacketDetail[] _items = new WirePacketDetail[Capacity];
    private int _start;
    private int _count;

    public static string NameFor(ushort opcode) => OpcodeNames.TryGetValue(opcode, out string? name)
        ? name
        : $"0x{opcode:X4}";

    public void Add(WirePacket packet) => Add(packet, ReadOnlySpan<byte>.Empty);

    public void Add(WirePacket packet, ReadOnlySpan<byte> payload)
    {
        byte[] prefix = payload[..Math.Min(payload.Length, DisplayPrefixSize)].ToArray();
        lock (_gate)
        {
            int index = (_start + _count) % Capacity;
            if (_count == Capacity)
            {
                _items[_start] = new WirePacketDetail(packet, prefix);
                _start = (_start + 1) % Capacity;
                return;
            }

            _items[index] = new WirePacketDetail(packet, prefix);
            _count++;
        }
    }

    public IReadOnlyList<WirePacket> Snapshot()
    {
        lock (_gate)
        {
            var result = new WirePacket[_count];
            for (int i = 0; i < _count; i++)
                result[i] = _items[(_start + i) % Capacity].Packet;
            return result;
        }
    }

    public IReadOnlyList<WirePacketDetail> SnapshotDetailed()
    {
        lock (_gate)
        {
            var result = new WirePacketDetail[_count];
            for (int i = 0; i < _count; i++)
            {
                WirePacketDetail item = _items[(_start + i) % Capacity];
                result[i] = new WirePacketDetail(item.Packet, (byte[])item.Prefix.Clone());
            }
            return result;
        }
    }
}

public sealed class WireLogRecorder : IDisposable
{
    private const int StoredPayloadLimit = 256;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(5);
    private readonly object _gate = new();
    private readonly ConcurrentQueue<(WirePacket Packet, byte[] Stored)> _pending = new();
    private FileStream? _binaryStream;
    private BinaryWriter? _binary;
    private StreamWriter? _text;
    private long _lastFlushTimestamp;

    public bool IsRecording
    {
        get { lock (_gate) return _binary is not null; }
    }

    public string Start(string repoRoot)
    {
        lock (_gate)
        {
            StopLocked();
            Directory.CreateDirectory(Path.Combine(repoRoot, "dumps"));
            DateTime timestamp = DateTime.Now;
            string relative;
            string binaryPath;
            do
            {
                relative = Path.Combine("dumps", $"wire-{timestamp:yyyyMMdd-HHmmss}.wlog");
                binaryPath = Path.Combine(repoRoot, relative);
                timestamp = timestamp.AddSeconds(1);
            } while (File.Exists(binaryPath) || File.Exists(Path.ChangeExtension(binaryPath, ".txt")));

            _binaryStream = new FileStream(binaryPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
            _binary = new BinaryWriter(_binaryStream, Encoding.UTF8, leaveOpen: true);
            _text = new StreamWriter(Path.ChangeExtension(binaryPath, ".txt"), append: false,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 64 * 1024);
            _lastFlushTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            return relative.Replace('\\', '/');
        }
    }

    public void Enqueue(WirePacket packet, ReadOnlySpan<byte> payload)
    {
        lock (_gate)
        {
            if (_binary is null) return;
            int storedSize = ShouldStorePayload(packet.Opcode)
                ? Math.Min(payload.Length, StoredPayloadLimit)
                : 0;
            _pending.Enqueue((packet, payload[..storedSize].ToArray()));
        }
    }

    public void Pump()
    {
        lock (_gate)
        {
            if (_binary is null) return;
            DrainLocked();
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (System.Diagnostics.Stopwatch.GetElapsedTime(_lastFlushTimestamp, now) >= FlushInterval)
            {
                FlushLocked();
                _lastFlushTimestamp = now;
            }
        }
    }

    public void Stop()
    {
        lock (_gate) StopLocked();
    }

    public static bool ShouldStorePayload(ushort opcode) => (Op)opcode is not (
        Op.SMSG_AUTH_CHALLENGE or Op.CMSG_AUTH_SESSION or Op.SMSG_AUTH_RESPONSE or
        Op.SMSG_WARDEN_DATA);

    public static string FormatText(WirePacket packet, ReadOnlySpan<byte> prefix)
    {
        string hex = prefix.Length == 0
            ? (ShouldStorePayload(packet.Opcode) ? "" : "[payload omitted]")
            : Convert.ToHexString(prefix).Chunk(2).Select(chars => new string(chars)).Aggregate(
                (left, right) => left + " " + right) + (packet.Size > prefix.Length ? " …" : "");
        return string.Format(CultureInfo.InvariantCulture,
            "t={0:F3}s {1}(0x{2:X4}) {3}B{4}", packet.Time, packet.OpcodeName,
            packet.Opcode, packet.Size, hex.Length == 0 ? "" : "  " + hex);
    }

    private void DrainLocked()
    {
        while (_pending.TryDequeue(out var item))
        {
            WirePacket packet = item.Packet;
            byte[] stored = item.Stored;
            _binary!.Write((byte)(packet.Outgoing ? 1 : 0));
            _binary.Write(packet.Time);
            _binary.Write(packet.Opcode);
            _binary.Write(checked((uint)packet.Size));
            _binary.Write(checked((ushort)stored.Length));
            _binary.Write(stored);
            _text!.WriteLine(FormatText(packet, stored.AsSpan(0, Math.Min(stored.Length, 16))));
        }
    }

    private void FlushLocked()
    {
        _binary?.Flush();
        _binaryStream?.Flush(flushToDisk: false);
        _text?.Flush();
    }

    private void StopLocked()
    {
        if (_binary is null) return;
        DrainLocked();
        FlushLocked();
        _text?.Dispose();
        _binary?.Dispose();
        _binaryStream?.Dispose();
        _text = null;
        _binary = null;
        _binaryStream = null;
        while (_pending.TryDequeue(out _)) { }
    }

    public void Dispose() => Stop();
}
