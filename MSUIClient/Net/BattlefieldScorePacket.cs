namespace MSUIClient.Net;

public sealed record BattlefieldScoreRow(ulong Guid, uint Rank, uint KillingBlows,
    uint HonorableKills, uint Deaths, uint BonusHonor, IReadOnlyList<uint> Objectives);

public sealed record BattlefieldScorePacket(bool Ended, byte? Winner, IReadOnlyList<BattlefieldScoreRow> Rows)
{
    public static BattlefieldScorePacket Parse(byte[] body)
    {
        var r = new PacketReader(body);
        if (r.Remaining < 5) throw new InvalidDataException("Short battlefield scoreboard");
        byte ended = r.ReadU8();
        if (ended > 1 || (ended == 1 && r.Remaining < 5)) throw new InvalidDataException("Invalid battlefield result header");
        byte? winner = ended == 1 ? r.ReadU8() : null;
        if (winner > 2) throw new InvalidDataException("Invalid battlefield winner");
        uint count = r.ReadU32();
        if (count > 80 || count > r.Remaining / 32) throw new InvalidDataException("Invalid battlefield score count");
        var rows = new BattlefieldScoreRow[count];
        var guids = new HashSet<ulong>();
        for (int i = 0; i < rows.Length; i++)
        {
            if (r.Remaining < 32) throw new InvalidDataException("Short battlefield score row");
            ulong guid = r.ReadU64(); uint rank = r.ReadU32(), kb = r.ReadU32(), hk = r.ReadU32(), deaths = r.ReadU32(), honor = r.ReadU32();
            uint extra = r.ReadU32();
            if (extra > 7 || extra > r.Remaining / 4 || guid == 0 || !guids.Add(guid))
                throw new InvalidDataException("Invalid battlefield objective count or identity");
            var objectives = new uint[extra];
            for (int j = 0; j < objectives.Length; j++) objectives[j] = r.ReadU32();
            rows[i] = new(guid, rank, kb, hk, deaths, honor, Array.AsReadOnly(objectives));
        }
        if (r.Remaining != 0) throw new InvalidDataException("Trailing battlefield scores");
        return new(ended == 1, winner, Array.AsReadOnly(rows));
    }
}
