using MSUIClient.Net;

namespace MSUIClient.Engine.UI;

public enum ChannelAdminCommand
{
    Password,
    SetOwner,
    Owner,
    Moderator,
    Unmoderator,
    Mute,
    Unmute,
    Invite,
    Kick,
    Ban,
    Unban,
    Announcements,
    Moderate,
}

public readonly record struct ChannelAdminRoute(
    ChannelAdminCommand Command,
    string Channel,
    string Value);

/// <summary>Build-5875 channel slots, slash grammar, and GlobalStrings notice composition.</summary>
public static class ChatChannelLaw
{
    public const int MaximumChannels = 10;

    public static int NumberOf(IReadOnlyList<string?> joined, string name)
    {
        for (int i = 0; i < joined.Count; i++)
            if (joined[i]?.Equals(name, StringComparison.OrdinalIgnoreCase) == true) return i + 1;
        return 0;
    }

    public static string? NameOf(IReadOnlyList<string?> joined, int number) =>
        number > 0 && number <= joined.Count ? joined[number - 1] : null;

    public static int ClaimSlot(List<string?> joined, string name)
    {
        int existing = NumberOf(joined, name);
        if (existing != 0) return existing;
        int free = joined.FindIndex(value => value is null);
        if (free >= 0)
        {
            joined[free] = name;
            return free + 1;
        }
        if (joined.Count >= MaximumChannels) return 0;
        joined.Add(name);
        return joined.Count;
    }

    public static int FreeSlot(List<string?> joined, string name)
    {
        int number = NumberOf(joined, name);
        if (number != 0) joined[number - 1] = null;
        return number;
    }

    public static string DisplayName(IReadOnlyList<string?> joined, string channel)
    {
        int number = NumberOf(joined, channel);
        return number == 0 ? channel : $"{number}. {channel}";
    }

    public static bool TryResolveSend(IReadOnlyList<string?> joined, string command,
        string arguments, out string channel, out string message)
    {
        channel = "";
        message = "";
        string token = command.TrimStart('/').ToLowerInvariant();
        if (int.TryParse(token, out int number))
        {
            channel = NameOf(joined, number) ?? "";
            message = arguments;
            return channel.Length > 0;
        }
        if (token is not ("c" or "csay")) return false;
        int split = arguments.IndexOf(' ');
        string selector = split < 0 ? arguments : arguments[..split];
        message = split < 0 ? "" : arguments[(split + 1)..].TrimStart();
        if (int.TryParse(selector, out number)) channel = NameOf(joined, number) ?? "";
        else
        {
            int named = NumberOf(joined, selector);
            channel = NameOf(joined, named) ?? "";
        }
        return channel.Length > 0;
    }

    /// <summary>
    /// Resolves the vanilla channel-administration slash family. A recognized verb returns
    /// true even when its operands are incomplete; the empty channel marks a silent no-op so
    /// a malformed administration command can never fall through as ordinary chat text.
    /// </summary>
    public static bool TryResolveAdmin(IReadOnlyList<string?> joined, string command,
        string arguments, out ChannelAdminRoute route)
    {
        string verb = command.ToLowerInvariant();
        string[] words = arguments.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        string channel = words.Length > 0 ? ResolveSelector(joined, words[0]) : "";
        string value = words.Length > 1 ? words[1].Trim() : "";

        ChannelAdminCommand? kind = verb switch
        {
            "/password" or "/pass" or "/chatpassword" or "/chatpass" or "/cpass" =>
                ChannelAdminCommand.Password,
            "/setowner" => ChannelAdminCommand.SetOwner,
            "/owner" or "/chatowner" => value.Length > 0
                ? ChannelAdminCommand.SetOwner : ChannelAdminCommand.Owner,
            "/moderator" or "/mod" => ChannelAdminCommand.Moderator,
            "/unmoderator" or "/unmod" => ChannelAdminCommand.Unmoderator,
            "/mute" => ChannelAdminCommand.Mute,
            "/unmute" or "/unsquelch" => ChannelAdminCommand.Unmute,
            "/cinvite" => ChannelAdminCommand.Invite,
            "/ckick" => ChannelAdminCommand.Kick,
            "/ban" => ChannelAdminCommand.Ban,
            "/unban" => ChannelAdminCommand.Unban,
            "/announcements" or "/announce" or "/ann" => ChannelAdminCommand.Announcements,
            "/moderate" => ChannelAdminCommand.Moderate,
            _ => null,
        };
        if (kind is null)
        {
            route = default;
            return false;
        }

        bool requiresValue = kind is ChannelAdminCommand.SetOwner or
            ChannelAdminCommand.Moderator or ChannelAdminCommand.Unmoderator or
            ChannelAdminCommand.Mute or ChannelAdminCommand.Unmute or
            ChannelAdminCommand.Invite or ChannelAdminCommand.Kick or
            ChannelAdminCommand.Ban or ChannelAdminCommand.Unban;
        if (channel.Length == 0 || (requiresValue && value.Length == 0)) channel = "";
        route = new(kind.Value, channel, value);
        return true;
    }

    private static string ResolveSelector(IReadOnlyList<string?> joined, string selector)
    {
        if (int.TryParse(selector, out int number)) return NameOf(joined, number) ?? "";
        int named = NumberOf(joined, selector);
        return named == 0 ? selector : NameOf(joined, named) ?? "";
    }

    public static string FormatMember(string displayChannel, string player, bool joined) =>
        $"[{ChatFrameLaw.StripChannelZone(displayChannel)}] [{player}] " +
        (joined ? "joined channel." : "left channel.");

    public static string FormatList(string displayChannel, int members) =>
        $"[{displayChannel}] {members} member(s)";

    public static string? FormatNotice(byte notice, string channel, string a = "", string b = "")
        => notice switch
        {
            ChannelNotice.YouJoined => $"Joined Channel: [{channel}]",
            ChannelNotice.YouLeft => $"Left Channel: [{channel}]",
            ChannelNotice.WrongPassword => $"Wrong password for {channel}.",
            ChannelNotice.NotMember => $"Not on channel {channel}.",
            ChannelNotice.NotModerator => $"Not a moderator of {channel}.",
            ChannelNotice.PasswordChanged => $"[{channel}] Password changed by {a}.",
            ChannelNotice.OwnerChanged => $"[{channel}] Owner changed to {a}.",
            ChannelNotice.PlayerNotFound => $"[{channel}] Player {a} is not on channel.",
            ChannelNotice.NotOwner => $"[{channel}] You are not the channel owner.",
            ChannelNotice.ChannelOwner => $"[{channel}] Channel owner is {a}.",
            ChannelNotice.ModeChange => null,
            ChannelNotice.AnnouncementsOn => $"[{channel}] Channel announcements enabled by {a}.",
            ChannelNotice.AnnouncementsOff => $"[{channel}] Channel announcements disabled by {a}.",
            ChannelNotice.ModerationOn => $"[{channel}] Channel moderation enabled by {a}.",
            ChannelNotice.ModerationOff => $"[{channel}] Channel moderation disabled by {a}.",
            ChannelNotice.Muted => $"[{channel}] You do not have permission to speak.",
            ChannelNotice.PlayerKicked => $"[{channel}] Player {a} kicked by {b}.",
            ChannelNotice.Banned => $"[{channel}] You are banned from that channel.",
            ChannelNotice.PlayerBanned => $"[{channel}] Player {a} banned by {b}.",
            ChannelNotice.PlayerUnbanned => $"[{channel}] Player {a} unbanned by {b}.",
            ChannelNotice.PlayerNotBanned => $"[{channel}] Player {a} is not banned.",
            ChannelNotice.PlayerAlreadyMember => $"[{channel}] Player {a} is already on the channel.",
            ChannelNotice.Invite => $"{a} has invited you to join the channel '{channel}'.",
            ChannelNotice.InviteWrongFaction => $"Target is in the wrong alliance for {channel}.",
            ChannelNotice.WrongFaction => $"Wrong alliance for {channel}.",
            ChannelNotice.InvalidName => "Invalid channel name",
            ChannelNotice.NotModerated => $"{channel} is not moderated",
            ChannelNotice.PlayerInvited => $"[{channel}] You invited {a} to join the channel",
            ChannelNotice.PlayerInviteBanned => $"[{channel}] {a} has been banned.",
            ChannelNotice.Throttled => $"[{channel}] The number of messages that can be sent to " +
                "this channel is limited, please wait to send another message.",
            _ => null,
        };
}
