using System.Numerics;
using MSUIClient;
using MSUIClient.Engine.UI;

internal static class UiErrorsFrameClinicalChecks
{
    public static void Run()
    {
        Check(UiErrorsFrameUiLaw.Width == 512 && UiErrorsFrameUiLaw.Height == 60 &&
              UiErrorsFrameUiLaw.TopOffset == 122 && UiErrorsFrameUiLaw.VisibleLines == 3 &&
              UiErrorsFrameUiLaw.HoldSeconds == 5 && UiErrorsFrameUiLaw.FadeSeconds == 3 &&
              UiErrorsFrameUiLaw.Font == "ErrorFont" &&
              UiErrorsFrameUiLaw.InfoColor == new Vector4(1, 1, 0, 1) &&
              UiErrorsFrameUiLaw.ErrorColor == new Vector4(1, .1f, .1f, 1),
            "UIErrorsFrame authored MessageFrame contract drift");
        UiErrorsFrameUiLaw.ScreenRect frame = UiErrorsFrameUiLaw.FrameRect(
            new Vector2(1920, 1080), 1);
        Check(frame.Min == new Vector2(704, 122) && frame.Size == new Vector2(512, 60) &&
              UiErrorsFrameUiLaw.LineCenter(0) == new Vector2(256, 10) &&
              UiErrorsFrameUiLaw.LineCenter(2) == new Vector2(256, 50) &&
              UiErrorsFrameUiLaw.Alpha(0) == 1 && UiErrorsFrameUiLaw.Alpha(5) == 1 &&
              MathF.Abs(UiErrorsFrameUiLaw.Alpha(6.5) - .5f) < .0001f &&
              UiErrorsFrameUiLaw.Alpha(8) == 0,
            "UIErrorsFrame seat/line/five-hold-three-fade law drift");

        var state = new UiErrorsFrameState();
        state.Push("old", UiMessageKind.Error, 0);
        state.Push("middle", UiMessageKind.Info, 1);
        state.Push("new", UiMessageKind.Error, 2);
        state.Push("newest", UiMessageKind.Info, 3);
        IReadOnlyList<UiErrorsFrameState.VisibleMessage> visible = state.Visible(4);
        Check(visible.Count == 3 && visible[0].Text == "newest" &&
              visible[1].Text == "new" && visible[2].Text == "middle" &&
              visible[0].Kind == UiMessageKind.Info,
            "UIErrorsFrame newest-on-top three-line cap drift");
        Check(state.Visible(9).Count == 2 && state.Visible(11).Count == 0,
            "UIErrorsFrame independent expiry/purge drift");

        string root = ClientConfig.FindRepoRoot();
        string renderer = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Dev",
            "GameLoop.UiErrorsParity.cs"));
        string route = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Loot.cs"));
        string network = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Net.cs"));
        string spell = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Dev",
            "GameLoop.DevTools.SpellErrors.cs"));
        string quest = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Quest.cs"));
        Check(renderer.Contains("UiErrorsFrameUiLaw.FrameRect", StringComparison.Ordinal) &&
              renderer.Contains("ImGui.GetForegroundDrawList()", StringComparison.Ordinal) &&
              renderer.Contains("UiErrorsFrameUiLaw.LineCenter", StringComparison.Ordinal) &&
              renderer.Contains("UiErrorsFrameUiLaw.Font", StringComparison.Ordinal) &&
              !renderer.Contains("new Vector2(512", StringComparison.Ordinal) &&
              route.Contains("_uiErrors.Push(text, UiMessageKind.Error", StringComparison.Ordinal) &&
              route.Contains("_uiErrors.Push(text, UiMessageKind.Info", StringComparison.Ordinal) &&
              route.Contains("LootPackets.ParseFishingVerdict(body, escaped)", StringComparison.Ordinal) &&
              route.Contains("ShowUiInfo(text)", StringComparison.Ordinal) &&
              network.Contains("case Op.SMSG_FISH_NOT_HOOKED", StringComparison.Ordinal) &&
              network.Contains("case Op.SMSG_FISH_ESCAPED", StringComparison.Ordinal) &&
              spell.Contains("ShowUiError(text)", StringComparison.Ordinal) &&
              !spell.Contains("PushCenterText(text", StringComparison.Ordinal) &&
              quest.Contains("ShowUiInfo(QuestKillProgressText(value))", StringComparison.Ordinal) &&
              quest.Contains("ShowUiInfo($\"{label}: {current}/{objective.ItemCount}\")",
                  StringComparison.Ordinal) &&
              quest.Contains("\"Objective Complete.\"", StringComparison.Ordinal),
            "live UI error/info producers bypass the MessageFrame state/law");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
