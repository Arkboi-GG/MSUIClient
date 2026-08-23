using System.Numerics;
using MSUIClient;
using MSUIClient.Engine;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;

internal static class ChatBubbleClinicalChecks
{
    public static void Run()
    {
        Check(ChatBubbleUiLaw.Enabled(ChatFrameLaw.MsgType.Say, true, false) &&
              ChatBubbleUiLaw.Enabled(ChatFrameLaw.MsgType.MonsterYell, true, false) &&
              ChatBubbleUiLaw.Enabled(ChatFrameLaw.MsgType.Party, false, true) &&
              !ChatBubbleUiLaw.Enabled(ChatFrameLaw.MsgType.Party, true, false) &&
              !ChatBubbleUiLaw.Enabled(ChatFrameLaw.MsgType.TextEmote, true, true) &&
              !ChatBubbleUiLaw.Enabled(ChatFrameLaw.MsgType.Whisper, true, true),
            "bubble chat-kind/CVar gate drift");

        Check(ChatBubbleUiLaw.Sanitize(
                "A || |cffaabbccred|r |Hitem:1|h[Thing]|h |x") ==
              "A | red [Thing] |x" &&
              ChatBubbleUiLaw.Sanitize("tail|") == "tail|",
            "bubble plain-text escape stripping drift");
        Check(ChatBubbleUiLaw.WordCount(" one\t two  three ") == 3 &&
              ChatBubbleUiLaw.WordCount(" \t ") == 0 &&
              ChatBubbleUiLaw.DurationSeconds(1, self: false) == 2.75 &&
              ChatBubbleUiLaw.DurationSeconds(3, self: false) == 4.25 &&
              ChatBubbleUiLaw.DurationSeconds(1, self: true) == 1.5 &&
              ChatBubbleUiLaw.DurationSeconds(3, self: true) == 2.5,
            "bubble word-count or self/other lifetime drift");

        Check(Close(ChatBubbleUiLaw.StepAlpha(0f, 0.1, 2.75, 0.125f, true), 0.5f) &&
              Close(ChatBubbleUiLaw.StepAlpha(0.5f, 0.2, 2.75, 0.125f, false), 0f) &&
              Close(ChatBubbleUiLaw.StepAlpha(0.5f, 3.0, 2.75, 0.125f, true), 0f) &&
              ChatBubbleUiLaw.Recyclable(0f, 3.0, 2.75) &&
              !ChatBubbleUiLaw.Recyclable(0f, 2.9, 2.75),
            "bubble 250ms recoverable/permanent fade drift");
        Check(Close(ChatBubbleUiLaw.PlateBasis(1024, 768), 1280f) &&
              Close(ChatBubbleUiLaw.BorderPixels(1024, 768), 16f),
            "bubble diagonal basis or 16/1024 border law drift");

        ChatBubbleUiLaw.Bounds frame = ChatBubbleUiLaw.Frame(
            new Vector2(500.2f, 400.2f), 200, 100, 2);
        ChatBubbleUiLaw.ImageRect inner = ChatBubbleUiLaw.Inner(frame, 16);
        ChatBubbleUiLaw.ImageRect tail = ChatBubbleUiLaw.Tail(frame, 16);
        IReadOnlyList<ChatBubbleUiLaw.ImageQuad> backdrop =
            ChatBubbleUiLaw.Backdrop(frame, 16);
        Check(frame == new ChatBubbleUiLaw.Bounds(400, 300, 600, 400) &&
              inner == new ChatBubbleUiLaw.ImageRect(
                  new Vector2(416, 316), new Vector2(584, 384)) &&
              tail == new ChatBubbleUiLaw.ImageRect(
                  new Vector2(484, 396), new Vector2(500, 412)) &&
              ChatBubbleUiLaw.LinePosition(frame, 80, 330) == new Vector2(460, 330) &&
              backdrop.Count == 8 &&
              backdrop[0].TopLeft == new Vector2(400, 316) &&
              backdrop[7].BottomRight == new Vector2(600, 400),
            "bubble frame/inner/tail/line/backdrop geometry drift");

        var stand = new M2Sequence
        {
            AnimationId = 0,
            BoundsMinimum = new(-1f, -1f, -0.2f),
            BoundsMaximum = new(1f, 1f, 2.0128f),
        };
        Check(Close(stand.BoundsZExtent, 2.2128f),
            "Stand CAaBox Z extent no longer comes from the static sequence header");

        string root = ClientConfig.FindRepoRoot();
        string bubbles = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Hud",
            "GameLoop.ChatBubbles.cs"));
        string chat = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Chat.cs"));
        string names = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Hud",
            "GameLoop.Nameplates.cs"));
        string art = SourceText.Read(Path.Combine(root, "MSUIClient", "Engine", "UI",
            "GameplayArt.cs"));
        string settings = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Settings.cs"));
        Check(chat.Contains("TrySpawnChatBubble(packet.SenderGuid, type, message)",
                  StringComparison.Ordinal) &&
              bubbles.Contains("WouldHaveActiveNameplate", StringComparison.Ordinal) &&
              bubbles.Contains("LatchedChatBubbleLift", StringComparison.Ordinal) &&
              bubbles.Contains("CameraDistanceSq", StringComparison.Ordinal) &&
              bubbles.Contains("AddImageQuad", StringComparison.Ordinal) &&
              bubbles.Contains("ChatBubbleUiLaw.Frame", StringComparison.Ordinal) &&
              bubbles.Contains("ChatBubbleUiLaw.Backdrop", StringComparison.Ordinal) &&
              !bubbles.Contains("new Vector2", StringComparison.Ordinal) &&
              names.Contains("HasLiveChatBubble(unit.Guid)", StringComparison.Ordinal) &&
              art.Contains("RepeatHandle", StringComparison.Ordinal) &&
              settings.Contains("BeginBox(\"chat-bubbles\", \"Chat Bubbles\")",
                  StringComparison.Ordinal),
            "bubble wire/spawn/name-exclusion/backdrop wiring drift");
    }

    private static bool Close(float actual, float expected) => MathF.Abs(actual - expected) < 0.0002f;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
