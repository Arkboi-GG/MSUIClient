namespace MSUIClient.Net;

/// <summary>Account/session tutorial wire bits; UI tutorial IDs are not assumed to be bit indices.</summary>
public sealed class TutorialFlagsState
{
    private readonly uint[] _words = new uint[8];
    public bool Known { get; private set; }
    public bool IsFlagged(uint wireBit) => wireBit < 256 && Known && (_words[wireBit / 32] & (1u << (int)(wireBit % 32))) != 0;

    public void Apply(byte[] body)
    {
        if (body.Length != 32) throw new InvalidDataException("bad SMSG_TUTORIAL_FLAGS body");
        var r = new PacketReader(body);
        for (int i = 0; i < _words.Length; i++) _words[i] = r.ReadU32();
        Known = true;
    }

    public void Mark(uint wireBit)
    {
        if (wireBit >= 256) throw new ArgumentOutOfRangeException(nameof(wireBit));
        if (Known) _words[wireBit / 32] |= 1u << (int)(wireBit % 32);
    }

    public void DisableAll() { if (Known) Array.Fill(_words, uint.MaxValue); }
    public void EnableAll() { if (Known) Array.Clear(_words); }
    public void ResetSession() { Array.Clear(_words); Known = false; }
}
