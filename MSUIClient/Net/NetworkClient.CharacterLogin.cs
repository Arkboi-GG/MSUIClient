namespace MSUIClient.Net;

public sealed partial class NetworkClient
{
    private int _characterLoginFailure = -1;

    public bool TryTakeCharacterLoginFailure(out byte reason)
    {
        int result = Interlocked.Exchange(ref _characterLoginFailure, -1);
        reason = result < 0 ? (byte)0 : (byte)result;
        return result >= 0;
    }

    private bool RecordCharacterLoginFailure(byte[] body)
    {
        if (body.Length != 1) throw new InvalidDataException("invalid character login failure payload");
        if (State != NetState.EnteringWorld) return false;
        // This is a failed character attempt, not a failed authenticated account socket.
        // The worker returns to CharEnum and its ordinary manual-pick park on this connection.
        while (_inbound.TryDequeue(out _)) { }
        Interlocked.Exchange(ref _characterLoginFailure, body[0]);
        Console.WriteLine($"[net] character login rejected: 0x{body[0]:X2}");
        return true;
    }
}
