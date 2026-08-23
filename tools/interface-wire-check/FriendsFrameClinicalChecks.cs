using System.Numerics;
using MSUIClient;
using MSUIClient.Engine.UI;

internal static class FriendsFrameClinicalChecks
{
    public static void Run()
    {
        Check(FriendsFrameUiLaw.FrameOrigin(1.5f) == new Vector2(0, 156) &&
              FriendsFrameUiLaw.FrameSize(1.5f) == new Vector2(576, 768),
            "friends frame positioning law drift");
        StaticPopupCoordinatorLaw.NarrowEditBoxLayout popup =
            StaticPopupCoordinatorLaw.NarrowEditLayout(12);
        Check(popup.Width == 320 && popup.Height == 112 &&
              popup.Text == new StaticPopupCoordinatorLaw.Rect(15, 16, 290, 12) &&
              popup.EditBox == new StaticPopupCoordinatorLaw.Rect(95, 35, 130, 32) &&
              popup.Button1 == new StaticPopupCoordinatorLaw.Rect(26, 75, 128, 20) &&
              popup.Button2 == new StaticPopupCoordinatorLaw.Rect(167, 75, 128, 20),
            "shared narrow StaticPopup child-anchor law drift");
        Check(FriendsFrameUiLaw.AddFriendPopupDefinition.HasEditBox &&
              FriendsFrameUiLaw.AddFriendPopupDefinition.HasEditBoxEnter &&
              FriendsFrameUiLaw.AddFriendPopupDefinition.MaxLetters == 12 &&
              FriendsFrameUiLaw.AddFriendPopupDefinition.HideOnEscape &&
              FriendsFrameUiLaw.AddFriendPopupText == "Enter name of friend to add:" &&
              FriendsFrameUiLaw.AddIgnorePopupText == "Enter name of player to ignore:",
            "social StaticPopup definition/text drift");
        StaticPopupCoordinatorLaw.Plan shown = StaticPopupCoordinatorLaw.Show(
            StaticPopupCoordinatorLaw.Slots.Empty,
            FriendsFrameUiLaw.AddFriendPopupDefinition,
            playerDeadOrGhost: false);
        Check(shown.Outcome == StaticPopupCoordinatorLaw.Outcome.Shown &&
              shown.Effects.Any(effect => effect.Kind ==
                  StaticPopupCoordinatorLaw.EffectKind.ClearEditBox) &&
              shown.Effects.Any(effect => effect.Kind ==
                  StaticPopupCoordinatorLaw.EffectKind.ShowEditBox),
            "social StaticPopup show does not prepare the frozen edit field");
        StaticPopupCoordinatorLaw.Plan enter = StaticPopupCoordinatorLaw.EditBoxEnter(
            shown.Slots, 1);
        Check(enter.Outcome == StaticPopupCoordinatorLaw.Outcome.EditSubmitted &&
              enter.Slots == shown.Slots && enter.Effects.Count == 1 &&
              enter.Effects[0].Kind == StaticPopupCoordinatorLaw.EffectKind.EditBoxEnter,
            "StaticPopup edit Enter callback/hide ownership drift");
        StaticPopupCoordinatorLaw.Plan escape = StaticPopupCoordinatorLaw.EditBoxEscape(
            shown.Slots, 1);
        Check(escape.Outcome == StaticPopupCoordinatorLaw.Outcome.Hidden &&
              escape.Slots.First is null &&
              escape.Effects[0].Kind ==
                  StaticPopupCoordinatorLaw.EffectKind.ClearEditBoxFocus,
            "StaticPopup edit Escape focus/hide ordering drift");
        Check(FriendsFrameUiLaw.AddFriend ==
                  new FriendsFrameUiLaw.LogicalRect(17, 384, 131, 21) &&
              FriendsFrameUiLaw.SendMessage ==
                  new FriendsFrameUiLaw.LogicalRect(214, 384, 131, 21) &&
              FriendsFrameUiLaw.RemoveFriend.Y == 410 &&
              FriendsFrameUiLaw.GroupInvite.Y == 410,
            "friends action-button geometry drift");
        Check(FriendsFrameUiLaw.FriendsRows ==
                  new FriendsFrameUiLaw.LogicalRect(23, 76, 298, 304) &&
              FriendsFrameUiLaw.FriendRowStep == 34 &&
              FriendsFrameUiLaw.IgnoreRows ==
                  new FriendsFrameUiLaw.LogicalRect(23, 80, 298, 320) &&
              FriendsFrameUiLaw.IgnorePlayer ==
                  new FriendsFrameUiLaw.LogicalRect(17, 410, 131, 21) &&
              FriendsFrameUiLaw.StopIgnore ==
                  new FriendsFrameUiLaw.LogicalRect(210, 410, 131, 21) &&
              FriendsFrameUiLaw.WhoHeaderName.Y == 70 &&
              FriendsFrameUiLaw.WhoRows ==
                  new FriendsFrameUiLaw.LogicalRect(15, 95, 298, 272),
            "Friends/Ignore/Who FrameXML row or action geometry drift");
        Check(FriendsFrameUiLaw.ScrollFurniture(FriendsFrameUiLaw.FriendsScrollFrame) ==
                  new FriendsFrameUiLaw.LogicalRect(323, 75, 16, 304) &&
              FriendsFrameUiLaw.MaximumOffset(14, 10) == 4 &&
              FriendsFrameUiLaw.ClampOffset(9, 14, 10) == 4 &&
              FriendsFrameUiLaw.WheelOffset(2, 14, 10, 1) == 1 &&
              FriendsFrameUiLaw.WheelOffset(2, 14, 10, -1) == 3 &&
              FriendsFrameUiLaw.OuterTabs.SequenceEqual(["Friends", "Who", "Guild"]),
            "FriendsFrame faux-scroll or current three-tab law drift");
        Check(FriendsFrameUiLaw.WhoVariableHeader(17) ==
                  new FriendsFrameUiLaw.LogicalRect(101, 70, 120, 24) &&
              FriendsFrameUiLaw.WhoLevelHeader(17).X == 219 &&
              FriendsFrameUiLaw.WhoClassHeader(17).X == 249 &&
              FriendsFrameUiLaw.WhoDropdownWidth(17) == 95 &&
              FriendsFrameUiLaw.WhoVariableHeader(18).Width == 105 &&
              FriendsFrameUiLaw.WhoLevelHeader(18).X == 204 &&
              FriendsFrameUiLaw.WhoDropdownWidth(18) == 80 &&
              FriendsFrameUiLaw.WhoVariableLabels.SequenceEqual(["Zone", "Guild", "Race"]),
            "Who variable-column crowded/wide header and dropdown law drift");
        Check(GuildFrameUiLaw.Rows ==
                  new GuildFrameUiLaw.LogicalRect(15, 95, 298, 208) &&
              GuildFrameUiLaw.Row(12) ==
                  new GuildFrameUiLaw.LogicalRect(15, 287, 298, 16) &&
              GuildFrameUiLaw.ScrollFurniture() ==
                  new GuildFrameUiLaw.LogicalRect(323, 98, 16, 237) &&
              GuildFrameUiLaw.PlayerHeaders(13)[1].Width == 120 &&
              GuildFrameUiLaw.PlayerHeaders(14)[1].Width == 105 &&
              GuildFrameUiLaw.StatusHeaders(13)[2].Width == 90 &&
              GuildFrameUiLaw.StatusHeaders(14)[2].Width == 75 &&
              GuildFrameUiLaw.ViewToggle(13).X == 307 &&
              GuildFrameUiLaw.ViewToggle(14).X == 284 &&
              GuildFrameUiLaw.LastOnline(.02f) == "< an hour" &&
              GuildFrameUiLaw.LastOnline(.1f) == "2 hours" &&
              GuildFrameUiLaw.LastOnline(45f) == "1 month" &&
              GuildFrameUiLaw.LastOnline(800f) == "2 years",
            "GuildFrame row/header/filter/toggle/last-online law drift");
        Check(FriendsFrameUiLaw.CanContact(true, true, true) &&
              !FriendsFrameUiLaw.CanContact(true, false, true) &&
              !FriendsFrameUiLaw.CanContact(false, true, true) &&
              !FriendsFrameUiLaw.CanContact(true, true, false) &&
              FriendsFrameUiLaw.CanRemove(true),
            "friends selected-online contact gate drift");

        string root = ClientConfig.FindRepoRoot();
        string runtime = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Social.cs"));
        Check(runtime.Contains(
                  "UiPanelFrameLogicalOrigin(UiPanelOwnershipRegistry[14])",
                  StringComparison.Ordinal) &&
              runtime.Contains("FriendsFrameUiLaw.NamePopup(_staticPopupSlots)",
                  StringComparison.Ordinal) &&
              runtime.Contains("StaticPopupCoordinatorLaw.NarrowEditLayout", StringComparison.Ordinal) &&
              runtime.Contains("StaticPopupCoordinatorLaw.EditBoxEnter", StringComparison.Ordinal) &&
              runtime.Contains("ImGuiInputTextFlags.EnterReturnsTrue", StringComparison.Ordinal) &&
              runtime.Contains(@"Interface\ChatFrame\UI-ChatInputBorder-Left",
                  StringComparison.Ordinal) &&
              runtime.Contains(@"Interface\ChatFrame\UI-ChatInputBorder-Right",
                  StringComparison.Ordinal) &&
              runtime.Contains("FriendsFrameUiLaw.CanContact", StringComparison.Ordinal) &&
              runtime.Contains("FriendsFrameUiLaw.FriendRowStep", StringComparison.Ordinal) &&
              runtime.Contains("DrawSocialFauxScrollBar", StringComparison.Ordinal) &&
              runtime.Contains("DrawWhoVariableDropdown", StringComparison.Ordinal) &&
              runtime.Contains("FriendsWhoVariable.Race", StringComparison.Ordinal) &&
              runtime.Contains("FriendsFrameScrollIcon", StringComparison.Ordinal) &&
              !runtime.Contains("DrawRaidPage", StringComparison.Ordinal) &&
              !runtime.Contains("\"Raid\"", StringComparison.Ordinal) &&
              runtime.Contains("OpenChatEditWith($\"/w ", StringComparison.Ordinal) &&
              !runtime.Contains("BeginVanillaWindow(\"##social\", new Vector2",
                  StringComparison.Ordinal) &&
              !runtime.Contains("VanillaInputText(dl, \"##name-popup\"",
                  StringComparison.Ordinal),
            "friends frame or shared name modal bypasses frozen action/layout/input law");

        string executor = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Hud",
            "GameLoop.PartyFrames.cs"));
        string guild = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Guild.cs"));
        string drawOrder = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Combat",
            "GameLoop.CombatFeedback.cs"));
        Check(executor.Contains("FriendsFrameUiLaw.IsNamePopup(effect.Type)",
                  StringComparison.Ordinal) &&
              executor.Contains("SubmitSocialNamePopup(effect.Type)", StringComparison.Ordinal) &&
              executor.Contains("StaticPopupCoordinatorLaw.EditBoxEscape", StringComparison.Ordinal) &&
              drawOrder.Contains("DrawSocialNamePopup();", StringComparison.Ordinal),
            "social name popup is not wired through shared coordinator/render order");
        Check(guild.Contains("GuildFrameUiLaw.Row(seat)", StringComparison.Ordinal) &&
              guild.Contains("GuildFrameUiLaw.OfflineFilter", StringComparison.Ordinal) &&
              guild.Contains("GuildFrameUiLaw.ViewToggle", StringComparison.Ordinal) &&
              guild.Contains("FriendsFrameUiLaw.OuterTabs", StringComparison.Ordinal) &&
              guild.Contains("DrawSocialFauxScrollBar", StringComparison.Ordinal) &&
              guild.Contains("UiPanelFrameOrigin(UiPanelOwnershipRegistry[14], s)",
                  StringComparison.Ordinal) &&
              !guild.Contains("new Vector2(337,18)", StringComparison.Ordinal) &&
              !guild.Contains("VanillaInputText(dl,\"##guild-motd\"", StringComparison.Ordinal) &&
              !guild.Contains("\"Raid\"", StringComparison.Ordinal),
            "GuildFrame bypasses its law, retains the ad-hoc MOTD editor, or restores Raid tab");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
