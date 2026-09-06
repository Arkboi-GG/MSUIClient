using MSUIClient.Formats;

namespace MSUIClient.Net;

/// <summary>Complete forced-reaction snapshots keyed by the player whose session supplied them.</summary>
public sealed class ForcedReactionStore
{
    private readonly Dictionary<ulong, Dictionary<uint, uint>> _byOwner = [];

    public void Apply(ulong owner, byte[] body)
    {
        var reader = new PacketReader(body);
        uint count = reader.ReadU32();
        if (owner == 0 || count > (uint)reader.Remaining / 8 || reader.Remaining != count * 8)
            throw new InvalidDataException("invalid forced-reaction snapshot");
        var snapshot = new Dictionary<uint, uint>();
        for (uint i = 0; i < count; ++i)
        {
            uint faction = reader.ReadU32(), rank = reader.ReadU32();
            if (faction == 0 || rank > 7 || !snapshot.TryAdd(faction, rank))
                throw new InvalidDataException("invalid forced-reaction faction/rank");
        }
        // Values are aggregate replacement state, including the empty snapshot on aura removal.
        _byOwner[owner] = snapshot;
    }

    public bool TryGet(ulong owner, uint faction, out FactionReaction reaction)
    {
        reaction = default;
        if (!_byOwner.TryGetValue(owner, out var rows) || !rows.TryGetValue(faction, out uint rank)) return false;
        reaction = rank <= 1 ? FactionReaction.Hostile : rank >= 4 ? FactionReaction.Friendly : FactionReaction.Neutral;
        return true;
    }

    public void Clear() => _byOwner.Clear();
}
