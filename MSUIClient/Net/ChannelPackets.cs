namespace MSUIClient.Net;

public static class ChannelNotice
{
    public const byte Joined = 0x00, Left = 0x01, YouJoined = 0x02, YouLeft = 0x03,
        WrongPassword = 0x04, NotMember = 0x05, NotModerator = 0x06,
        PasswordChanged = 0x07, OwnerChanged = 0x08, PlayerNotFound = 0x09,
        NotOwner = 0x0A, ChannelOwner = 0x0B, ModeChange = 0x0C,
        AnnouncementsOn = 0x0D, AnnouncementsOff = 0x0E, ModerationOn = 0x0F,
        ModerationOff = 0x10, Muted = 0x11, PlayerKicked = 0x12, Banned = 0x13,
        PlayerBanned = 0x14, PlayerUnbanned = 0x15, PlayerNotBanned = 0x16,
        PlayerAlreadyMember = 0x17, Invite = 0x18, InviteWrongFaction = 0x19,
        WrongFaction = 0x1A, InvalidName = 0x1B, NotModerated = 0x1C,
        PlayerInvited = 0x1D, PlayerInviteBanned = 0x1E, Throttled = 0x1F;
}

public sealed record ChannelNoticePacket(
    byte Notice,
    string Channel,
    ulong FirstGuid = 0,
    ulong SecondGuid = 0,
    string Name = "",
    uint Flags = 0,
    byte OldFlags = 0,
    byte NewFlags = 0);

public sealed record ChannelListPacket(
    string Channel,
    byte Flags,
    IReadOnlyList<(ulong Guid, byte Flags)> Members);

/// <summary>Exact build-5875 channel packet bodies.</summary>
public static class ChannelPackets
{
    public static byte[] BuildJoin(string name, string password)
    {
        var w = new PacketWriter();
        w.WriteCString(name);
        w.WriteCString(password);
        return w.ToArray();
    }

    public static byte[] BuildName(string name)
    {
        var w = new PacketWriter();
        w.WriteCString(name);
        return w.ToArray();
    }

    public static ChannelNoticePacket ParseNotice(byte[] body)
    {
        var r = new PacketReader(body);
        byte notice = r.ReadU8();
        string channel = r.ReadCString();
        ChannelNoticePacket packet = notice switch
        {
            ChannelNotice.Joined or ChannelNotice.Left =>
                new(notice, channel, FirstGuid: r.ReadU64()),
            ChannelNotice.YouJoined => ReadYouJoined(r, notice, channel),
            ChannelNotice.PasswordChanged or ChannelNotice.OwnerChanged or
            ChannelNotice.AnnouncementsOn or ChannelNotice.AnnouncementsOff or
            ChannelNotice.ModerationOn or ChannelNotice.ModerationOff or
            ChannelNotice.PlayerAlreadyMember or ChannelNotice.Invite =>
                new(notice, channel, FirstGuid: r.ReadU64()),
            ChannelNotice.PlayerNotFound or ChannelNotice.ChannelOwner or
            ChannelNotice.PlayerNotBanned or ChannelNotice.PlayerInvited or
            ChannelNotice.PlayerInviteBanned =>
                new(notice, channel, Name: r.ReadCString()),
            ChannelNotice.ModeChange => new(notice, channel, FirstGuid: r.ReadU64(),
                OldFlags: r.ReadU8(), NewFlags: r.ReadU8()),
            ChannelNotice.PlayerKicked or ChannelNotice.PlayerBanned or
            ChannelNotice.PlayerUnbanned =>
                new(notice, channel, FirstGuid: r.ReadU64(), SecondGuid: r.ReadU64()),
            >= ChannelNotice.YouLeft and <= ChannelNotice.Throttled => new(notice, channel),
            _ => throw new InvalidDataException(
                $"SMSG_CHANNEL_NOTIFY: unknown notice type 0x{notice:X2}"),
        };
        if (r.Remaining != 0)
            throw new InvalidDataException($"SMSG_CHANNEL_NOTIFY: {r.Remaining} trailing byte(s)");
        return packet;
    }

    private static ChannelNoticePacket ReadYouJoined(PacketReader r, byte notice, string channel)
    {
        uint flags = r.ReadU32();
        _ = r.ReadU32(); // reserved, always zero
        return new(notice, channel, Flags: flags);
    }

    public static ChannelListPacket ParseList(byte[] body)
    {
        var r = new PacketReader(body);
        string channel = r.ReadCString();
        byte flags = r.ReadU8();
        uint count = r.ReadU32();
        if (count > (uint)(r.Remaining / 9))
            throw new InvalidDataException("SMSG_CHANNEL_LIST member count exceeds body");
        var members = new List<(ulong Guid, byte Flags)>((int)Math.Min(count, 256));
        for (uint i = 0; i < count; i++) members.Add((r.ReadU64(), r.ReadU8()));
        if (r.Remaining != 0)
            throw new InvalidDataException($"SMSG_CHANNEL_LIST: {r.Remaining} trailing byte(s)");
        return new(channel, flags, members);
    }
}
