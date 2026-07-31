using System.Globalization;
using System.Text;
using MSUIClient.Engine;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private readonly object _socketTraceLock = new();
    private StreamWriter? _socketTraceWriter;
    private string _socketTracePath = "";
    private int _socketTraceRow;

    private void StartSocketTrace(string name)
    {
        StopSocketTrace();
        string safe = string.Concat((string.IsNullOrWhiteSpace(name) ? "manual" : name.Trim())
            .Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-'));
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string directory = Path.Combine(_config.RepoRoot, "dumps");
        Directory.CreateDirectory(directory);
        lock (_socketTraceLock)
        {
            _socketTracePath = Path.Combine(directory, $"sockettrace-{safe}-{stamp}.csv");
            _socketTraceWriter = new StreamWriter(_socketTracePath, false, new UTF8Encoding(false));
            _socketTraceWriter.WriteLine("row,time,opcode,name,bytes,sha256,flushed,hex");
            _socketTraceRow = 0;
        }
    }

    private void StopSocketTrace()
    {
        lock (_socketTraceLock)
        {
            if (_socketTraceWriter is null) return;
            _socketTraceWriter.Flush();
            _socketTraceWriter.Dispose();
            _socketTraceWriter = null;
            Console.WriteLine($"[socket-trace] wrote {_socketTraceRow} rows to {_socketTracePath}");
        }
    }

    private void ObserveSocketWrite(ushort opcode, ReadOnlySpan<byte> packet,
        ReadOnlySpan<byte> sha256)
    {
        string hash = Convert.ToHexString(sha256).ToLowerInvariant();
        string hex = Convert.ToHexString(packet);
        lock (_socketTraceLock)
        {
            if (_socketTraceWriter is null) return;
            _socketTraceWriter.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{++_socketTraceRow},{NowSeconds():R},0x{opcode:X4},{WireRing.NameFor(opcode)}," +
                $"{packet.Length},{hash},true,{hex}"));
            _socketTraceWriter.Flush();
        }
        EmitCombat("SocketFlush", "post-encryption-socket-write", 0,
            $"opcode=0x{opcode:X4};bytes={packet.Length};sha256={hash};flushed=true;hex={hex}",
            (Op)opcode);
    }
}
