using System.Numerics;
using MSUIClient;
using MSUIClient.Engine.UI;

internal static class ChatMenuClinicalChecks
{
    public static void Run()
    {
        IReadOnlyList<ChatMenuRow> root = ChatMenuUiLaw.Rows(ChatMenuLevel.Root);
        Check(root.Count == 8 &&
              root[0] == new ChatMenuRow("Say", "/s", "/s ") &&
              root[5].Label == "Emote" && root[5].InputPrefix == "/e " &&
              root[5].Nested == ChatMenuLevel.Emote &&
              root[6].Label == "Reply" && root[6].InputPrefix == "/r " &&
              root[7].Label == "Voice Emote" &&
              root[7].Nested == ChatMenuLevel.VoiceEmote,
            "ChatMenu root row table drift");
        Check(ChatMenuUiLaw.Rows(ChatMenuLevel.Emote).Count == 21 &&
              ChatMenuUiLaw.Rows(ChatMenuLevel.Emote)[20].Command == "/wave" &&
              ChatMenuUiLaw.Rows(ChatMenuLevel.VoiceEmote).Count == 22 &&
              ChatMenuUiLaw.Rows(ChatMenuLevel.VoiceEmote)[0].Command == "/attackmytarget" &&
              ChatMenuUiLaw.Rows(ChatMenuLevel.VoiceEmote)[21].Command == "/welcome",
            "ChatMenu nested emote tables drift");

        Vector2 rootOrigin = ChatMenuUiLaw.RootOrigin(new Vector2(0, 547), root.Count,
            new Vector2(1024, 768));
        Check(rootOrigin == new Vector2(32, 395) &&
              ChatMenuUiLaw.CardHeight(root.Count) == 152 &&
              ChatMenuUiLaw.HitRow(new Vector2(45, 408), rootOrigin, root.Count) == 0 &&
              ChatMenuUiLaw.HitRow(new Vector2(45, 519), rootOrigin, root.Count) == 7,
            "ChatMenu root BOTTOMLEFT-to-TOPRIGHT layout/hit law drift");
        Vector2 emoteOrigin = ChatMenuUiLaw.SubmenuOrigin(rootOrigin, 5, 21,
            new Vector2(1024, 768));
        Check(emoteOrigin == new Vector2(158, 155),
            "ChatMenu nested BOTTOMLEFT-to-row-BOTTOMRIGHT layout drift");

        string chat = SourceText.Read(Path.Combine(ClientConfig.FindRepoRoot(), "MSUIClient",
            "GameLoop", "Panels", "GameLoop.Chat.cs"));
        Check(chat.Contains("ChatMenuUiLaw.RootOrigin(buttonMin", StringComparison.Ordinal) &&
              chat.Contains("ImGui.GetForegroundDrawList()", StringComparison.Ordinal) &&
              chat.Contains("_skin!.DrawBackdrop", StringComparison.Ordinal) &&
              chat.Contains("You have nobody to reply to yet.", StringComparison.Ordinal) &&
              chat.Contains("case \"/e\" or \"/em\" or \"/emote\"", StringComparison.Ordinal),
            "law-positioned ChatMenu drawing, reply, or freeform emote wiring is absent");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
