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
        Check(FriendsFrameUiLaw.CanContact(true, true, true) &&
              !FriendsFrameUiLaw.CanContact(true, false, true) &&
              !FriendsFrameUiLaw.CanContact(false, true, true) &&
              !FriendsFrameUiLaw.CanContact(true, true, false) &&
              FriendsFrameUiLaw.CanRemove(true),
            "friends selected-online contact gate drift");

        string root = ClientConfig.FindRepoRoot();
        string runtime = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Social.cs"));
        Check(runtime.Contains("FriendsFrameUiLaw.FrameOrigin", StringComparison.Ordinal) &&
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
              runtime.Contains("OpenChatEditWith($\"/w ", StringComparison.Ordinal) &&
              !runtime.Contains("BeginVanillaWindow(\"##social\", new Vector2",
                  StringComparison.Ordinal) &&
              !runtime.Contains("VanillaInputText(dl, \"##name-popup\"",
                  StringComparison.Ordinal),
            "friends frame or shared name modal bypasses frozen action/layout/input law");

        string executor = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Hud",
            "GameLoop.PartyFrames.cs"));
        string drawOrder = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Combat",
            "GameLoop.CombatFeedback.cs"));
        Check(executor.Contains("FriendsFrameUiLaw.IsNamePopup(effect.Type)",
                  StringComparison.Ordinal) &&
              executor.Contains("SubmitSocialNamePopup(effect.Type)", StringComparison.Ordinal) &&
              executor.Contains("StaticPopupCoordinatorLaw.EditBoxEscape", StringComparison.Ordinal) &&
              drawOrder.Contains("DrawSocialNamePopup();", StringComparison.Ordinal),
            "social name popup is not wired through shared coordinator/render order");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
