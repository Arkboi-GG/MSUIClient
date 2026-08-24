using System.Numerics;
using MSUIClient;
using MSUIClient.Engine.UI;

internal static class FriendsFrameClinicalChecks
{
    public static void Run()
    {
        Check(FriendsFrameUiLaw.FrameOrigin(1.5f) == new Vector2(0, 156) &&
              FriendsFrameUiLaw.FrameSize(1.5f) == new Vector2(576, 768) &&
              FriendsFrameUiLaw.TitleCenter == new Vector2(192, 18) &&
              FriendsFrameUiLaw.Close ==
                  new FriendsFrameUiLaw.LogicalRect(322, 8, 32, 32) &&
              FriendsFrameUiLaw.ListWheelRegion ==
                  new FriendsFrameUiLaw.LogicalRect(0, 0, 384, 512) &&
              FriendsFrameUiLaw.OuterTabMinimum(25) == new Vector2(25, 433) &&
              FriendsFrameUiLaw.InsetTabMinimum(150) == new Vector2(150, 39),
            "friends frame positioning law drift");
        Check(FriendsFrameUiLaw.OpenSound == "igMainMenuOpen" &&
              FriendsFrameUiLaw.CloseSound == "igMainMenuClose" &&
              FriendsFrameUiLaw.TabSound == "igCharacterInfoTab" &&
              FriendsFrameUiLaw.RowSound == "igMainMenuOptionCheckBoxOn" &&
              FriendsFrameUiLaw.SoundCategory == "ui.social" &&
              FriendsFrameUiLaw.IgnoreNameLine(null) == "" &&
              FriendsFrameUiLaw.IgnoreNameLine("Thrall") == "Thrall" &&
              FriendsFrameUiLaw.AddFriendTooltip.StartsWith("Adds a player to your friends list.",
                  StringComparison.Ordinal) &&
              FriendsFrameUiLaw.RemoveFriendTooltip ==
                  "Removes the selected player from your friends list." &&
              FriendsFrameUiLaw.SendMessageTooltip ==
                  "Sends a private message to the selected player." &&
              FriendsFrameUiLaw.GroupInviteTooltip ==
                  "Invites the selected player to join a group." &&
              FriendsFrameUiLaw.ResolvedDisplayLabel(null) == "" &&
              FriendsFrameUiLaw.ResolvedDisplayLabel("?") == "" &&
              FriendsFrameUiLaw.ResolvedDisplayLabel("Durotar") == "Durotar",
            "FriendsFrame lifecycle/row sounds or pending-ignore copy drift");
        FriendsFrameUiLaw.ShellArt friendsShell = FriendsFrameUiLaw.ShellFor(0, false);
        FriendsFrameUiLaw.ShellArt ignoreShell = FriendsFrameUiLaw.ShellFor(0, true);
        FriendsFrameUiLaw.ShellArt whoShell = FriendsFrameUiLaw.ShellFor(1, false);
        FriendsFrameUiLaw.ShellArt guildShell = FriendsFrameUiLaw.ShellFor(2, false);
        Check(friendsShell.TopLeft == FriendsFrameUiLaw.GeneralTopLeft &&
              friendsShell.BottomLeft == FriendsFrameUiLaw.FriendsBottomLeft &&
              ignoreShell.TopLeft == FriendsFrameUiLaw.GeneralTopLeft &&
              ignoreShell.BottomLeft == FriendsFrameUiLaw.IgnoreBottomLeft &&
              whoShell.TopLeft == FriendsFrameUiLaw.TrainerTopLeft &&
              whoShell.BottomLeft == FriendsFrameUiLaw.WhoBottomLeft &&
              guildShell.TopLeft == FriendsFrameUiLaw.TrainerTopLeft &&
              guildShell.BottomLeft == FriendsFrameUiLaw.GuildBottomLeft,
            "Friends/Ignore/Who/Guild page-dependent shell-art matrix drift");
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
              FriendsFrameUiLaw.FriendNameOffset == new Vector2(10, 3) &&
              FriendsFrameUiLaw.IgnoreRows ==
                  new FriendsFrameUiLaw.LogicalRect(23, 80, 298, 320) &&
              FriendsFrameUiLaw.IgnoreNameOffset == new Vector2(10, 3) &&
              FriendsFrameUiLaw.IgnorePlayer ==
                  new FriendsFrameUiLaw.LogicalRect(17, 410, 131, 21) &&
              FriendsFrameUiLaw.StopIgnore ==
                  new FriendsFrameUiLaw.LogicalRect(210, 410, 131, 21) &&
              FriendsFrameUiLaw.WhoHeaderName.Y == 70 &&
              FriendsFrameUiLaw.WhoRows ==
                  new FriendsFrameUiLaw.LogicalRect(15, 95, 298, 272) &&
              FriendsFrameUiLaw.WhoTotalsBox ==
                  new FriendsFrameUiLaw.LogicalRect(33, 369, 298, 16) &&
              FriendsFrameUiLaw.WhoSearch ==
                  new FriendsFrameUiLaw.LogicalRect(24, 380, 296, 32) &&
              FriendsFrameUiLaw.WhoSearchTextInset == Vector2.Zero &&
              FriendsFrameUiLaw.RowHighlightPath ==
                  @"Interface\QuestFrame\UI-QuestTitleHighlight",
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
        Check(FriendsFrameUiLaw.DefaultWhoVariable == FriendsWhoVariable.Zone &&
              FriendsFrameUiLaw.ShouldSubmitWhoFilter(true, true, false) &&
              FriendsFrameUiLaw.ShouldSubmitWhoFilter(true, false, true) &&
              !FriendsFrameUiLaw.ShouldSubmitWhoFilter(false, true, false),
            "Who default-column or search-enter submission law drift");
        var contactNames = new Dictionary<ulong, string>
        {
            [1] = "Zeta",
            [2] = "alpha",
            [3] = "",
        };
        IReadOnlyList<ulong> contactOrder = FriendsFrameUiLaw.ContactOrder(
            [3, 1, 4, 2], guid => contactNames.GetValueOrDefault(guid));
        Check(contactOrder.SequenceEqual([2ul, 1ul, 3ul, 4ul]) &&
              FriendsFrameUiLaw.SelectionForGuid(1, contactOrder) == 1 &&
              FriendsFrameUiLaw.SelectionForGuid(99, contactOrder) == 0 &&
              FriendsFrameUiLaw.SelectionForGuid(0, []) == 0,
            "Friends/Ignore resolved-name order or identity selection drift");
        IReadOnlyList<FriendsFrameUiLaw.TextureSlice> headerSlices =
            FriendsFrameUiLaw.WhoColumnHeaderSlices(83);
        DropdownCapsuleUiLaw.Layout whoDropdown = FriendsFrameUiLaw.WhoDropdown(18);
        FriendsFrameUiLaw.ScrollBarLayout friendsScroll = FriendsFrameUiLaw.ScrollBar(
            FriendsFrameUiLaw.FriendsScrollFrame, 2, 4);
        Check(headerSlices.Count == 3 &&
              headerSlices[0].Rect == new FriendsFrameUiLaw.LogicalRect(0, 0, 5, 24) &&
              headerSlices[1].Rect == new FriendsFrameUiLaw.LogicalRect(5, 0, 74, 24) &&
              headerSlices[2].Rect == new FriendsFrameUiLaw.LogicalRect(79, 0, 4, 24) &&
              whoDropdown.Frame ==
                  new DropdownCapsuleUiLaw.LogicalRect(86, 70, 130, 32) &&
              whoDropdown.Button ==
                  new DropdownCapsuleUiLaw.LogicalRect(90, 1, 24, 24) &&
              whoDropdown.LeftJustified &&
              DropdownCapsuleUiLaw.List(whoDropdown, 3) ==
                  new DropdownCapsuleUiLaw.LogicalRect(94, 95, 112, 78) &&
              DropdownCapsuleUiLaw.Row(whoDropdown, 2) ==
                  new DropdownCapsuleUiLaw.LogicalRect(111, 142, 80, 16) &&
              friendsScroll.UpButton ==
                  new FriendsFrameUiLaw.LogicalRect(323, 75, 16, 16) &&
              friendsScroll.DownButton ==
                  new FriendsFrameUiLaw.LogicalRect(323, 363, 16, 16) &&
              friendsScroll.Track ==
                  new FriendsFrameUiLaw.LogicalRect(323, 91, 16, 272) &&
              friendsScroll.Knob ==
                  new FriendsFrameUiLaw.LogicalRect(323, 219, 16, 16),
            "Who header/dropdown or social faux-scroll child geometry drift");
        Check(FriendsFrameUiLaw.WhoHeaderHit(83) ==
                  new FriendsFrameUiLaw.LogicalRect(0, 0, 83, 24) &&
              FriendsFrameUiLaw.WhoHeaderHighlight(83) ==
                  new FriendsFrameUiLaw.LogicalRect(-2, -5, 87, 36) &&
              FriendsFrameUiLaw.SortForVariable(FriendsWhoVariable.Guild) ==
                  FriendsWhoSort.Guild,
            "Who clickable-column highlight/sort law drift");
        FriendsFrameUiLaw.WhoEntry zeta = new("Zeta", "Bravo", 40, 4, 2, 12);
        FriendsFrameUiLaw.WhoEntry alpha = new("Alpha", "Charlie", 60, 1, 3, 10);
        FriendsFrameUiLaw.WhoEntry beta = new("Beta", "Alpha", 20, 8, 1, 11);
        Check(FriendsFrameUiLaw.SortWho([zeta, alpha, beta], FriendsWhoSort.Name)
                  .Select(row => row.Name).SequenceEqual(["Alpha", "Beta", "Zeta"]) &&
              FriendsFrameUiLaw.SortWho([zeta, alpha, beta], FriendsWhoSort.Level)
                  .Select(row => row.Name).SequenceEqual(["Beta", "Zeta", "Alpha"]) &&
              FriendsFrameUiLaw.SortWho([zeta, alpha, beta], FriendsWhoSort.Zone)
                  .Select(row => row.Name).SequenceEqual(["Alpha", "Beta", "Zeta"]) &&
              FriendsFrameUiLaw.SortWho([zeta, alpha, beta], FriendsWhoSort.Guild)
                  .Select(row => row.Name).SequenceEqual(["Beta", "Zeta", "Alpha"]),
            "Who client-side column comparator drift");
        Check(StaticPopupCoordinatorLaw.NarrowEditBorderSlices.Count == 2 &&
              StaticPopupCoordinatorLaw.NarrowEditBorderSlices[0].Rect ==
                  new StaticPopupCoordinatorLaw.Rect(-10, 0, 75, 32) &&
              StaticPopupCoordinatorLaw.NarrowEditBorderSlices[1].Rect ==
                  new StaticPopupCoordinatorLaw.Rect(65, 0, 75, 32) &&
              StaticPopupCoordinatorLaw.EditTextOffset == new Vector2(0, 7),
            "shared StaticPopup edit text/border geometry drift");
        Check(GuildFrameUiLaw.Rows ==
                  new GuildFrameUiLaw.LogicalRect(15, 95, 298, 208) &&
              GuildFrameUiLaw.RowHighlight ==
                  new GuildFrameUiLaw.LogicalRect(5, 2, 298, 16) &&
              GuildFrameUiLaw.RowHighlightPath ==
                  @"Interface\FriendsFrame\UI-FriendsFrame-HighlightBar" &&
              GuildFrameUiLaw.MemberTotalBox ==
                  new GuildFrameUiLaw.LogicalRect(70, 315, 0, 16) &&
              GuildFrameUiLaw.OnlineTotalBox(100) ==
                  new GuildFrameUiLaw.LogicalRect(173, 315, 0, 16) &&
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
        IReadOnlyList<string> motdLines = GuildFrameUiLaw.WrapMotd(
            "One two three four", 9, 20, 10, value => value.Length);
        Check(motdLines.SequenceEqual(["One two", "three"]),
            "GuildFrame fixed-height MOTD font/wrap law drift");
        Check(FriendsFrameUiLaw.CanContact(true, true, true) &&
              !FriendsFrameUiLaw.CanContact(true, false, true) &&
              !FriendsFrameUiLaw.CanContact(false, true, true) &&
              !FriendsFrameUiLaw.CanContact(true, true, false) &&
              FriendsFrameUiLaw.WhoSelectionValid(0, 1) &&
              !FriendsFrameUiLaw.WhoSelectionValid(-1, 1) &&
              !FriendsFrameUiLaw.WhoSelectionValid(1, 1) &&
              FriendsFrameUiLaw.CanOpenFriendMenu(true, true) &&
              !FriendsFrameUiLaw.CanOpenFriendMenu(false, true) &&
              FriendsFrameUiLaw.CanRemove(true),
            "friends selected-online contact gate drift");
        Check(FriendsFrameUiLaw.StatusTag(1) == "" &&
              FriendsFrameUiLaw.StatusTag(2) == "<AFK>" &&
              FriendsFrameUiLaw.StatusTag(4) == "<DND>" &&
              FriendsFrameUiLaw.OfflineNameLine("Thrall") == "Thrall - Offline" &&
              FriendsFrameUiLaw.FriendInfoLine(false, 0, "") == "Unknown" &&
              FriendsFrameUiLaw.FriendInfoLine(true, 60, "Shaman") ==
                  "Level 60 Shaman" &&
              FriendsFrameUiLaw.WhoTotals(1) == "1 Person Found  " &&
              FriendsFrameUiLaw.WhoTotals(50) == "50 People Found  " &&
              FriendsFrameUiLaw.WhoTotals(132) == "132 People Found  (50 displayed)",
            "friends online status/offline two-line display templates drift");

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
              runtime.Contains("FriendsFrameUiLaw.OfflineNameLine", StringComparison.Ordinal) &&
              runtime.Contains("FriendsFrameUiLaw.FriendInfoLine", StringComparison.Ordinal) &&
              runtime.Contains("FriendsFrameUiLaw.StatusTag", StringComparison.Ordinal) &&
              runtime.Contains("FriendsFrameUiLaw.ContactOrder(", StringComparison.Ordinal) &&
              runtime.Contains("FriendsFrameUiLaw.SelectionForGuid", StringComparison.Ordinal) &&
              runtime.Contains("_friendSelected", StringComparison.Ordinal) &&
              runtime.Contains("_ignoreSelected", StringComparison.Ordinal) &&
              runtime.Contains("ReorderSocialContactsAfterNameResolution",
                  StringComparison.Ordinal) &&
              !runtime.Contains("_socialSelected", StringComparison.Ordinal) &&
              runtime.Contains("CloseFriendsFrame()", StringComparison.Ordinal) &&
              runtime.Contains("PlayUiSound(FriendsFrameUiLaw.OpenSound",
                  StringComparison.Ordinal) &&
              runtime.Contains("PlayUiSound(FriendsFrameUiLaw.CloseSound",
                  StringComparison.Ordinal) &&
              runtime.Contains("PlayUiSound(FriendsFrameUiLaw.TabSound",
                  StringComparison.Ordinal) &&
              runtime.Contains("PlayUiSound(FriendsFrameUiLaw.RowSound",
                  StringComparison.Ordinal) &&
              runtime.Contains("FriendsFrameUiLaw.IgnoreNameLine(",
                  StringComparison.Ordinal) &&
              runtime.Contains("FriendsFrameUiLaw.ResolvedDisplayLabel(",
                  StringComparison.Ordinal) &&
              !runtime.Contains("$\"Area {row.Area}\"", StringComparison.Ordinal) &&
              runtime.Contains("FriendsFrameUiLaw.WhoTotals(_whoTotal)", StringComparison.Ordinal) &&
              runtime.Contains("FriendsFrameUiLaw.WhoTotalsBox.Center", StringComparison.Ordinal) &&
              runtime.Contains("FriendsFrameUiLaw.IgnoreNameOffset", StringComparison.Ordinal) &&
              runtime.Contains("highlightPath: FriendsFrameUiLaw.RowHighlightPath",
                  StringComparison.Ordinal) &&
              runtime.Contains("additiveHighlight: true", StringComparison.Ordinal) &&
              runtime.Contains("FriendsFrameUiLaw.WhoSelectionValid(_whoSelected, _who.Count)",
                  StringComparison.Ordinal) &&
              runtime.Contains("FriendsFrameUiLaw.CanOpenFriendMenu", StringComparison.Ordinal) &&
              runtime.Contains("OpenFriendPopup(row.Name, ImGui.GetIO().MousePos)",
                  StringComparison.Ordinal) &&
              runtime.Contains("FriendsFrameUiLaw.WhoHeaderHighlight", StringComparison.Ordinal) &&
              runtime.Contains("SortWhoResults(sort)", StringComparison.Ordinal) &&
              runtime.Contains("FriendsFrameUiLaw.SortForVariable(variable)",
                  StringComparison.Ordinal) &&
              runtime.Contains("FriendsFrameUiLaw.DefaultWhoVariable", StringComparison.Ordinal) &&
              runtime.Contains("FriendsFrameUiLaw.ShouldSubmitWhoFilter", StringComparison.Ordinal) &&
              runtime.Contains("VanillaBareInputText(\"##who-search\"", StringComparison.Ordinal) &&
              runtime.Contains("FriendsFrameUiLaw.WhoSearchTextInset", StringComparison.Ordinal) &&
              !runtime.Contains("VanillaInputText(dl, \"##who-search\"", StringComparison.Ordinal) &&
              runtime.Contains("ImGuiKey.KeypadEnter", StringComparison.Ordinal) &&
              runtime.Contains("FriendsFrameUiLaw.FriendRowStep", StringComparison.Ordinal) &&
              runtime.Contains("FriendsFrameUiLaw.ListWheelRegion", StringComparison.Ordinal) &&
              runtime.Contains("DrawSocialFauxScrollBar", StringComparison.Ordinal) &&
              runtime.Contains("DrawWhoVariableDropdown", StringComparison.Ordinal) &&
              runtime.Contains("FriendsFrameUiLaw.WhoDropdown", StringComparison.Ordinal) &&
              runtime.Contains("VanillaDropdownCapsule", StringComparison.Ordinal) &&
              runtime.Contains("DropdownCapsuleUiLaw.RowCheck", StringComparison.Ordinal) &&
              runtime.Contains("WowSkin.Dialog", StringComparison.Ordinal) &&
              runtime.Contains("FriendsFrameUiLaw.ShellFor", StringComparison.Ordinal) &&
              runtime.Contains("FriendsWhoVariable.Race", StringComparison.Ordinal) &&
              runtime.Contains("FriendsFrameScrollIcon", StringComparison.Ordinal) &&
              !runtime.Contains("DrawRaidPage", StringComparison.Ordinal) &&
              !runtime.Contains("\"Raid\"", StringComparison.Ordinal) &&
              runtime.Contains("OpenChatEditWith($\"/w ", StringComparison.Ordinal) &&
              runtime.Contains("OfferVanillaNewbieTooltip(new(\"social-action\"",
                  StringComparison.Ordinal) &&
              !runtime.Contains("BeginVanillaWindow(\"##social\", new Vector2",
                  StringComparison.Ordinal) &&
              !runtime.Contains("new Vector2", StringComparison.Ordinal) &&
              !runtime.Contains("VanillaInputText(dl, \"##name-popup\"",
                  StringComparison.Ordinal),
            "friends frame or shared name modal bypasses frozen action/layout/input law");

        string executor = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Hud",
            "GameLoop.PartyFrames.cs"));
        string guild = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Guild.cs"));
        string drawOrder = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Combat",
            "GameLoop.CombatFeedback.cs"));
        string network = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Net.cs"));
        Check(executor.Contains("FriendsFrameUiLaw.IsNamePopup(effect.Type)",
                  StringComparison.Ordinal) &&
              executor.Contains("SubmitSocialNamePopup(effect.Type)", StringComparison.Ordinal) &&
              executor.Contains("StaticPopupCoordinatorLaw.EditBoxEscape", StringComparison.Ordinal) &&
              drawOrder.Contains("DrawSocialNamePopup();", StringComparison.Ordinal) &&
              network.Contains("ReorderSocialContactsAfterNameResolution();",
                  StringComparison.Ordinal),
            "social name popup is not wired through shared coordinator/render order");
        Check(guild.Contains("GuildFrameUiLaw.Row(seat)", StringComparison.Ordinal) &&
              guild.Contains("FriendsFrameUiLaw.ShellFor(page: 2", StringComparison.Ordinal) &&
              guild.Contains("highlightPath: GuildFrameUiLaw.RowHighlightPath",
                  StringComparison.Ordinal) &&
              guild.Contains("highlightOffset: GuildFrameUiLaw.RowHighlight.Min",
                  StringComparison.Ordinal) &&
              guild.Contains("GuildFrameUiLaw.OnlineTotalBox(memberTotalWidth)",
                  StringComparison.Ordinal) &&
              guild.Contains("GuildFrameUiLaw.WrapMotd", StringComparison.Ordinal) &&
              guild.Contains("GameText.Draw(dl, \"GameFontHighlightSmall\", motdLines[line]",
                  StringComparison.Ordinal) &&
              guild.Contains("GuildFrameUiLaw.OfflineFilter", StringComparison.Ordinal) &&
              guild.Contains("GuildFrameUiLaw.ViewToggle", StringComparison.Ordinal) &&
              guild.Contains("FriendsFrameUiLaw.OuterTabs", StringComparison.Ordinal) &&
              guild.Contains("DrawSocialFauxScrollBar", StringComparison.Ordinal) &&
              guild.Contains("FriendsFrameUiLaw.ListWheelRegion", StringComparison.Ordinal) &&
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
