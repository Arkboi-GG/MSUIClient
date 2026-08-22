namespace MSUIClient.Engine.UI;

/// <summary>Exact GlobalStrings templates selected by SMSG_FRIEND_STATUS result.</summary>
public static class FriendStatusUiLaw
{
    public static string? Template(byte result) => result switch
    {
        0x00 => "Friend lookup database error.",
        0x01 => "You don't have room for any more friends.",
        0x02 => "|Hplayer:%s|h[%s]|h has come online.",
        0x03 => "%s has gone offline.",
        0x04 => "Player not found.",
        0x05 => "%s removed from friends list.",
        0x06 or 0x07 => "%s added to friends.",
        0x08 => "%s is already your friend.",
        0x09 => "You can't put yourself on your friend list.",
        0x0a => "Friends must be part of your alliance.",
        0x0b => "You can't ignore any more players.",
        0x0c => "You can't ignore yourself.",
        0x0d => "Player not found.",
        0x0e => "%s is already being ignored.",
        0x0f => "%s is now being ignored.",
        0x10 => "%s is no longer being ignored.",
        0x11 => "That name is ambiguous, type more of the player's server name",
        0x1a => "Unknown friend response from server.",
        _ => null,
    };

    public static bool NeedsName(string template) =>
        template.Contains("%s", StringComparison.Ordinal);

    public static string Compose(string template, string name) =>
        template.Replace("%s", name, StringComparison.Ordinal);
}
