namespace MSUIClient.Net;

/// <summary>Read-only server cache snapshots. No local setting/macro application or upload.</summary>
public sealed class AccountDataState
{
    private readonly object _sync = new();
    private readonly byte[]?[] _digests = new byte[AccountDataPackets.Count][];
    private readonly byte[]?[] _data = new byte[AccountDataPackets.Count][];
    private readonly bool[] _pending = new bool[AccountDataPackets.Count];
    private ulong _character;

    public void Reset(ulong character = 0)
    {
        lock (_sync)
        {
            _character = character;
            Array.Clear(_digests); Array.Clear(_data); Array.Clear(_pending);
        }
    }

    public uint[] ApplyDigests(byte[] body)
    {
        byte[][] next = AccountDataPackets.ParseDigests(body); // validate before replacing any slot
        lock (_sync)
        {
            if (_character == 0) return [];
            List<uint> requests = [];
            for (uint i = 0; i < AccountDataPackets.Count; i++)
            {
                bool same = _digests[i]?.AsSpan().SequenceEqual(next[i]) == true;
                _digests[i] = next[i];
                if (next[i].All(value => value == 0))
                { _data[i] = []; _pending[i] = false; }
                else if (!same || (_data[i] is null && !_pending[i]))
                { _data[i] = null; _pending[i] = true; requests.Add(i); }
            }
            return requests.ToArray();
        }
    }

    public bool ApplyUpdate(byte[] body)
    {
        AccountDataPayload value = AccountDataPackets.ParseUpdate(body);
        lock (_sync)
        {
            uint i = value.Type;
            if (_character == 0 || !_pending[i] || _digests[i] is not { } expected) return false;
            if (!AccountDataPackets.Digest(value.Data).AsSpan().SequenceEqual(expected)) return false;
            _pending[i] = false;
            _data[i] = value.Data;
            return true;
        }
    }

    public bool TryGet(uint type, out ulong character, out byte[] data)
    {
        lock (_sync)
        {
            character = AccountDataPackets.IsGlobal(type) ? 0 : _character;
            data = [];
            if (type >= AccountDataPackets.Count || _character == 0 || _data[type] is not { } bytes) return false;
            data = bytes.ToArray(); // callers cannot mutate retained data
            return true;
        }
    }
}
