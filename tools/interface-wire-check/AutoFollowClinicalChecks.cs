using System.Numerics;
using MSUIClient;
using MSUIClient.Engine;
using MSUIClient.Engine.UI;

internal static class AutoFollowClinicalChecks
{
    public static void Run()
    {
        Check(AutoFollowUiLaw.StartRefusal(false, false, true, true, true) ==
                  AutoFollowRefusal.InvalidTarget &&
              AutoFollowUiLaw.StartRefusal(true, true, true, true, true) ==
                  AutoFollowRefusal.PlayerDead &&
              AutoFollowUiLaw.StartRefusal(true, true, false, true, true) ==
                  AutoFollowRefusal.Stunned &&
              AutoFollowUiLaw.StartRefusal(true, true, false, false, true) ==
                  AutoFollowRefusal.Busy &&
              AutoFollowUiLaw.StartRefusal(true, true, false, false, false) ==
                  AutoFollowRefusal.None,
            "auto-follow start-gate order drift");

        Check(Near(AutoFollowUiLaw.ArriveDistance(7f), 3f) &&
              Near(AutoFollowUiLaw.ResumeDistance(7f), 4.5f) &&
              Near(AutoFollowUiLaw.ArriveDistance(14f), 6f) &&
              Near(AutoFollowUiLaw.ResumeDistance(14f), 9f) &&
              Near(AutoFollowUiLaw.ResumeDistance(3.5f), 4.5f) &&
              !AutoFollowUiLaw.ShouldMove(true, 3f, 7f) &&
              AutoFollowUiLaw.ShouldMove(true, 3.001f, 7f) &&
              !AutoFollowUiLaw.ShouldMove(false, 4.499f, 7f) &&
              AutoFollowUiLaw.ShouldMove(false, 4.5f, 7f),
            "auto-follow arrive/resume hysteresis drift");

        AutoFollowMotion quarterTurn = AutoFollowUiLaw.Tick(
            new Vector3(0, 10, 0), 0f, false, 7f, .25f);
        AutoFollowMotion arrived = AutoFollowUiLaw.Tick(
            new Vector3(3, 0, 0), 0f, true, 7f, 1f);
        AutoFollowMotion overhead = AutoFollowUiLaw.Tick(
            new Vector3(.001f, 0, 10), 0f, false, 7f, 1f);
        AutoFollowMotion noBearing = AutoFollowUiLaw.Tick(
            Vector3.Zero, 1f, true, 7f, 1f);
        Check(Near(quarterTurn.Yaw, MathF.PI * .25f) && quarterTurn.Forward &&
              quarterTurn.MovingLatch && !quarterTurn.EndsFollow &&
              !arrived.Forward && !arrived.MovingLatch &&
              overhead.EndsFollow &&
              !noBearing.Forward && noBearing.MovingLatch && !noBearing.EndsFollow,
            "auto-follow bounded steering or degenerate-bearing law drift");

        Check(AutoFollowUiLaw.BeginText("Thrall") == "Following Thrall." &&
              AutoFollowUiLaw.EndText("Thrall") == "You stop following Thrall." &&
              AutoFollowUiLaw.StatusAlpha(active: true, 99) == 1f &&
              Near(AutoFollowUiLaw.StatusAlpha(active: false, 1), .75f) &&
              AutoFollowUiLaw.StatusAlpha(active: false, 4) == 0f &&
              AutoFollowUiLaw.StatusCenter(new Vector2(1920, 1080)) ==
                  new Vector2(960, 540) &&
              AutoFollowUiLaw.StatusFontObject == "GameFontNormalHuge",
            "AutoFollowStatus copy, fade, font or full-screen center drift");

        string root = ClientConfig.FindRepoRoot();
        string runtime = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Follow.cs"));
        string program = SourceText.Read(Path.Combine(root, "MSUIClient", "Program.cs"));
        string combat = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Combat",
            "GameLoop.CombatFeedback.cs"));
        string popup = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Hud",
            "GameLoop.UnitPopup.cs"));
        string chat = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Chat.cs"));
        int followInput = program.IndexOf("ApplyAutoFollowInput(ref forward, dt, typing, mouseSteering);",
            StringComparison.Ordinal);
        int scriptedInput = program.IndexOf("OverrideMovementInput(ref forward", StringComparison.Ordinal);
        int zoneStatus = combat.IndexOf("DrawZoneTextSplash();", StringComparison.Ordinal);
        int followStatus = combat.IndexOf("DrawAutoFollowStatus();", StringComparison.Ordinal);
        Check(followInput >= 0 && scriptedInput > followInput &&
              runtime.Contains("AutoFollowUiLaw.Tick(", StringComparison.Ordinal) &&
              runtime.Contains("forward = Math.Clamp(forward + 1f", StringComparison.Ordinal) &&
              runtime.Contains("TryResolveAutoFollowByName", StringComparison.Ordinal) &&
              runtime.Contains("AutoFollowCommonPrefix", StringComparison.Ordinal) &&
              runtime.Contains("movementStarted || bothMouseEngaged || mouseSteering || lostMover",
                  StringComparison.Ordinal) &&
              runtime.Contains("followee.IsDead", StringComparison.Ordinal) &&
              !runtime.Contains("ImGui.Begin(", StringComparison.Ordinal) &&
              !runtime.Contains("CMSG_", StringComparison.Ordinal),
            "auto-follow must synthesize ordinary input without a window or dedicated wire path");

        Check(popup.Contains("case UnitPopupRow.Follow:", StringComparison.Ordinal) &&
              popup.Contains("StartAutoFollow(guid, name);", StringComparison.Ordinal) &&
              chat.Contains("case \"/follow\" or \"/fol\" or \"/f\":", StringComparison.Ordinal) &&
              chat.Contains("StartAutoFollow(_selectionGuid, AutoFollowTargetName",
                  StringComparison.Ordinal) &&
              zoneStatus >= 0 && followStatus > zoneStatus &&
              runtime.Contains("ImGui.GetBackgroundDrawList()", StringComparison.Ordinal) &&
              runtime.Contains("GameText.DrawCenteredWithAlpha", StringComparison.Ordinal),
            "UnitPopup Follow action or BACKGROUND AutoFollowStatus composition drift");
    }

    private static bool Near(float actual, float expected) => MathF.Abs(actual - expected) < 1e-4f;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
