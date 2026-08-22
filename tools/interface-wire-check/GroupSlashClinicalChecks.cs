using MSUIClient;
using MSUIClient.Engine.UI;

internal static class GroupSlashClinicalChecks
{
    public static void Run()
    {
        Check(GroupSlashCommandLaw.Resolve("/invite") == GroupSlashCommand.Invite &&
              GroupSlashCommandLaw.Resolve("/inv") == GroupSlashCommand.Invite &&
              GroupSlashCommandLaw.Resolve("/i") == GroupSlashCommand.Invite &&
              GroupSlashCommandLaw.Resolve("/uninvite") == GroupSlashCommand.Uninvite &&
              GroupSlashCommandLaw.Resolve("/un") == GroupSlashCommand.Uninvite &&
              GroupSlashCommandLaw.Resolve("/u") == GroupSlashCommand.Uninvite &&
              GroupSlashCommandLaw.Resolve("/kick") == GroupSlashCommand.Uninvite &&
              GroupSlashCommandLaw.Resolve("/promote") == GroupSlashCommand.Promote &&
              GroupSlashCommandLaw.Resolve("/pr") == GroupSlashCommand.Promote &&
              GroupSlashCommandLaw.Resolve("/party") is null,
            "party membership slash alias table drift");

        string root = ClientConfig.FindRepoRoot();
        string chat = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Chat.cs"));
        Check(chat.Contains("ResolveGroupSlashTarget(args)", StringComparison.Ordinal) &&
              chat.Contains("_net?.GroupInvite(name)", StringComparison.Ordinal) &&
              chat.Contains("_net?.GroupUninvite(name)", StringComparison.Ordinal) &&
              chat.Contains("_net?.GroupSetLeader(member.Guid)", StringComparison.Ordinal) &&
              chat.Contains("!target.IsPlayer", StringComparison.Ordinal) &&
              chat.Contains("is not in your party.", StringComparison.Ordinal),
            "group slash name/selected-player dispatch or promote roster resolution is unwired");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
