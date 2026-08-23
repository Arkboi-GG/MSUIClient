using MSUIClient;

internal static class SocialTabBindingClinicalChecks
{
    public static void Run()
    {
        string root = ClientConfig.FindRepoRoot();
        string bindings = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop",
            "Panels", "GameLoop.Bindings.cs"));
        string social = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop",
            "Panels", "GameLoop.Social.cs"));
        string actionBars = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop",
            "Hud", "GameLoop.ActionBars.cs"));

        Check(bindings.Contains(
                  "GameBinding.OpenSocialFriends, \"Toggle Friends Pane\", Key.Unknown",
                  StringComparison.Ordinal) &&
              bindings.Contains(
                  "GameBinding.OpenSocialWho, \"Toggle Who Pane\", Key.Unknown",
                  StringComparison.Ordinal) &&
              bindings.Contains(
                  "GameBinding.OpenSocialGuild, \"Toggle Guild Pane\", Key.Unknown",
                  StringComparison.Ordinal),
            "Benilla's three unbound direct social-pane commands drifted");
        Check(actionBars.Contains("UpdateSocialTabBindings(typing);", StringComparison.Ordinal) &&
              social.Contains("down && !_socialTabBindingWasDown[index] && !typing",
                  StringComparison.Ordinal) &&
              social.Contains("_net is { IsInWorld: true }", StringComparison.Ordinal),
            "direct social-pane commands escaped edge/typing/world dispatch");
        Check(social.Contains("if (CurrentGuildId() == 0) return;", StringComparison.Ordinal) &&
              social.Contains("if (_guildOpen)", StringComparison.Ordinal) &&
              social.Contains("if (_socialOpen && _socialPage == index)",
                  StringComparison.Ordinal) &&
              social.Contains("if (index == 0) _net?.FriendList();", StringComparison.Ordinal) &&
              social.Contains("else SendWhoFilter(ReadBuffer(_whoInput));",
                  StringComparison.Ordinal),
            "direct social-pane same-tab close, guild refusal or page refresh drifted");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
