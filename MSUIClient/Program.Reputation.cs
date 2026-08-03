using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private sealed record ReputationState(byte Flags, int Standing);
    private readonly ReputationState[] _reputation = new ReputationState[64];
    private FactionCatalog? _factionCatalog;
    private int _reputationScroll;

    private void InitReputation()
    {
        if (_mpq is null) return;
        try
        {
            byte[]? bytes = _mpq.ReadFile(FactionCatalog.MpqPath);
            _factionCatalog = bytes is null ? null : FactionCatalog.Parse(bytes);
        }
        catch (Exception ex) { Console.WriteLine($"[reputation] Faction.dbc failed: {ex.Message}"); }
    }

    private void ApplyInitialFactions(byte[] body)
    {
        var r = new PacketReader(body);
        uint count = r.ReadU32();
        if (count > 64 || r.Remaining != count * 5)
            throw new InvalidDataException($"invalid initial faction payload count={count} bytes={body.Length}");
        Array.Clear(_reputation);
        for (int i = 0; i < count; i++) _reputation[i] = new(r.ReadU8(), r.ReadI32());
    }

    private void ApplyFactionVisible(byte[] body)
    {
        var r = new PacketReader(body);
        uint index = r.ReadU32();
        if (index < 64) _reputation[index] = _reputation[index] with { Flags = (byte)(_reputation[index].Flags | 1) };
    }

    private void ApplyFactionStanding(byte[] body)
    {
        var r = new PacketReader(body);
        uint count = r.ReadU32();
        if (count > 64 || r.Remaining != count * 8)
            throw new InvalidDataException($"invalid faction standing payload count={count} bytes={body.Length}");
        for (int i = 0; i < count; i++)
        {
            uint index = r.ReadU32(); int standing = r.ReadI32();
            if (index < 64) _reputation[index] = _reputation[index] with { Standing = standing };
        }
    }

    private static (string Name, int Floor, int Ceiling, uint Color) ReputationRank(int standing) => standing switch
    {
        < -6000 => ("Hated", -42000, -6000, 0xff2020cc),
        < -3000 => ("Hostile", -6000, -3000, 0xff2020cc),
        < 0 => ("Unfriendly", -3000, 0, 0xff2060cc),
        < 3000 => ("Neutral", 0, 3000, 0xff20d0dd),
        < 9000 => ("Friendly", 3000, 9000, 0xff20c050),
        < 21000 => ("Honored", 9000, 21000, 0xff20c050),
        < 42000 => ("Revered", 21000, 42000, 0xff20c050),
        _ => ("Exalted", 42000, 43000, 0xff20c050),
    };
}
