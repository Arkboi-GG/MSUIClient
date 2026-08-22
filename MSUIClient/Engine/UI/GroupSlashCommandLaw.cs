namespace MSUIClient.Engine.UI;

public enum GroupSlashCommand { Invite, Uninvite, Promote }

/// <summary>Vanilla GlobalStrings aliases for the three party membership slash verbs.</summary>
public static class GroupSlashCommandLaw
{
    public static GroupSlashCommand? Resolve(string command) => command.ToLowerInvariant() switch
    {
        "/invite" or "/inv" or "/i" => GroupSlashCommand.Invite,
        "/uninvite" or "/un" or "/u" or "/kick" => GroupSlashCommand.Uninvite,
        "/promote" or "/pr" => GroupSlashCommand.Promote,
        _ => null,
    };
}
