namespace MSUIClient.Net;

public readonly record struct HonorStatistics(uint SessionKillsPacked, uint YesterdayKillsPacked,
    uint LastWeekKillsPacked, uint ThisWeekKillsPacked, uint LifetimeHonorableKills,
    uint LifetimeDishonorableKills, uint YesterdayContribution, uint LastWeekContribution,
    uint ThisWeekContribution, uint LastWeekStanding)
{
    public (ushort Honorable, ushort Dishonorable) SessionKills => ((ushort)SessionKillsPacked, (ushort)(SessionKillsPacked >> 16));
    public (ushort Honorable, ushort Dishonorable) YesterdayKills => ((ushort)YesterdayKillsPacked, (ushort)(YesterdayKillsPacked >> 16));
    public (ushort Honorable, ushort Dishonorable) LastWeekKills => ((ushort)LastWeekKillsPacked, (ushort)(LastWeekKillsPacked >> 16));
    public (ushort Honorable, ushort Dishonorable) ThisWeekKills => ((ushort)ThisWeekKillsPacked, (ushort)(ThisWeekKillsPacked >> 16));
    public static HonorStatistics FromFields(ObjectFields f)
    {
        static uint Pack((ushort Honorable, ushort Dishonorable) kills) => kills.Honorable | ((uint)kills.Dishonorable << 16);
        return new(Pack(f.SessionKills), Pack(f.YesterdayKills), Pack(f.LastWeekKills), Pack(f.ThisWeekKills),
            f.LifetimeHonorableKills, f.LifetimeDishonorableKills, f.YesterdayContribution,
            f.LastWeekContribution, f.ThisWeekContribution, f.LastWeekRank);
    }
}

public readonly record struct HonorInspectPacket(ulong Guid, byte HighestRank, HonorStatistics Statistics, byte RankProgress)
{
    public static HonorInspectPacket Parse(byte[] body)
    {
        if (body.Length != 50) throw new InvalidDataException("MSG_INSPECT_HONOR_STATS must contain 50 bytes");
        var r = new PacketReader(body);
        ulong guid = r.ReadU64(); byte highest = r.ReadU8();
        var stats = new HonorStatistics(r.ReadU32(),r.ReadU32(),r.ReadU32(),r.ReadU32(),r.ReadU32(),
            r.ReadU32(),r.ReadU32(),r.ReadU32(),r.ReadU32(),r.ReadU32());
        return new(guid,highest,stats,r.ReadU8());
    }
}
