using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private sealed class LiveChatBubble
    {
        public required string Text;
        public required ChatFrameLaw.MsgType Type;
        public double Born;
        public double Duration;
        public double LastTick;
        public float Lift;
        public float Alpha;
    }

    private readonly Dictionary<ulong, LiveChatBubble> _chatBubbles = [];

    private readonly record struct PendingChatBubble(
        ulong Guid, LiveChatBubble Bubble, Vector2 Seat, float CameraDistanceSq);

    /// <summary>
    /// The SMSG_MESSAGECHAT display-path arm. All gates happen at the same point
    /// as chat routing so deferred monster macros use their expanded display text
    /// and a hidden UI does not postpone the bubble's lifetime.
    /// </summary>
    private void TrySpawnChatBubble(ulong senderGuid, ChatFrameLaw.MsgType type, string rawText)
    {
        if (senderGuid == 0 || !ChatBubbleUiLaw.Enabled(type,
                Settings.Controls.ChatBubbles, Settings.Controls.PartyChatBubbles)) return;

        string text = ChatBubbleUiLaw.Sanitize(rawText);
        int words = ChatBubbleUiLaw.WordCount(text);
        if (words == 0 || !_entities.TryGet(senderGuid, out WorldEntity speaker) ||
            !speaker.IsUnit || !_entities.TryGet(LocalPlayerGuid, out WorldEntity self)) return;

        Vector3 selfPosition = UnitWorldPosition(self);
        if (Vector3.DistanceSquared(UnitWorldPosition(speaker), selfPosition) >
            ChatBubbleUiLaw.RangeSquared) return;

        // Replace is allowed while an old bubble owns the name-exclusion handle.
        // Otherwise a live V-plate refuses creation, matching +0xe64's two-way gate.
        Vector2 display = ImGui.GetIO().DisplaySize;
        if (!_chatBubbles.ContainsKey(senderGuid) &&
            WouldHaveActiveNameplate(speaker, display)) return;

        double now = NowSeconds();
        _chatBubbles[senderGuid] = new LiveChatBubble
        {
            Text = text,
            Type = type,
            Born = now,
            Duration = ChatBubbleUiLaw.DurationSeconds(words, senderGuid == LocalPlayerGuid),
            LastTick = now,
            Lift = LatchedChatBubbleLift(speaker),
            Alpha = 0f,
        };
    }

    private float LatchedChatBubbleLift(WorldEntity speaker)
    {
        if (speaker.Guid == ControlledGuid && _character is not null)
            return _character.StandBoxHeight();
        return _creatures?.TryGetStandBoxHeight(speaker, out float height) == true
            ? height : 0f;
    }

    private bool HasLiveChatBubble(ulong guid) => _chatBubbles.ContainsKey(guid);

    private void ClearChatBubbles() => _chatBubbles.Clear();

    private void DrawChatBubbles()
    {
        if (_chatBubbles.Count == 0) return;

        double now = NowSeconds();
        Vector2 display = ImGui.GetIO().DisplaySize;
        WorldEntity? self = _entities.TryGet(LocalPlayerGuid, out WorldEntity local) ? local : null;
        Vector3 selfPosition = self is null ? default : UnitWorldPosition(self);
        List<ulong> remove = [];
        List<PendingChatBubble> pending = [];

        foreach ((ulong guid, LiveChatBubble bubble) in _chatBubbles)
        {
            if (!_entities.TryGet(guid, out WorldEntity speaker) || !speaker.IsUnit)
            {
                remove.Add(guid);
                continue;
            }

            float delta = (float)Math.Clamp(now - bubble.LastTick, 0.0, 5.0);
            bubble.LastTick = now;
            double age = Math.Max(0.0, now - bubble.Born);
            Vector3 speakerPosition = UnitWorldPosition(speaker);
            bool inRange = self is not null &&
                Vector3.DistanceSquared(speakerPosition, selfPosition) <=
                    ChatBubbleUiLaw.RangeSquared;
            bubble.Alpha = ChatBubbleUiLaw.StepAlpha(
                bubble.Alpha, age, bubble.Duration, delta, inRange);
            if (ChatBubbleUiLaw.Recyclable(bubble.Alpha, age, bubble.Duration))
            {
                remove.Add(guid);
                continue;
            }

            // The exclusion handle stays live even while a recoverably out-of-range
            // bubble is fully transparent. Only recycling releases the nameplate.
            if (bubble.Alpha <= 0f) continue;
            Vector3 anchor = speakerPosition +
                new Vector3(0f, 0f, bubble.Lift + ChatBubbleUiLaw.WorldLift);
            if (!_window.Camera.TryWorldToScreen(anchor, display, out Vector2 seat)) continue;
            pending.Add(new PendingChatBubble(guid, bubble, seat,
                Vector3.DistanceSquared(_window.Camera.Position, speakerPosition)));
        }

        foreach (ulong guid in remove) _chatBubbles.Remove(guid);
        if (_gameplayArt is null || pending.Count == 0) return;

        // Reference frame levels are restamped farthest-to-nearest every frame;
        // the nearest card is therefore emitted last and stacks as one whole card.
        pending.Sort(static (a, b) =>
        {
            int distance = b.CameraDistanceSq.CompareTo(a.CameraDistanceSq);
            return distance != 0 ? distance : a.Guid.CompareTo(b.Guid);
        });

        ImDrawListPtr draw = ImGui.GetBackgroundDrawList();
        foreach (PendingChatBubble item in pending)
            DrawChatBubble(draw, item.Bubble, item.Seat, display);
    }

    private void DrawChatBubble(ImDrawListPtr draw, LiveChatBubble bubble,
        Vector2 seat, Vector2 display)
    {
        float basis = ChatBubbleUiLaw.PlateBasis(display.X, display.Y);
        float border = ChatBubbleUiLaw.BorderPixels(display.X, display.Y);
        float margin = ChatBubbleUiLaw.MarginGx * basis;
        float fontSize = MathF.Max(1f, ChatBubbleUiLaw.TextHeightGx * basis);
        float wrapCap = ChatBubbleUiLaw.WrapWidthGx * basis;
        float fontScale = fontSize / MathF.Max(1f, ImGui.GetFontSize());
        float Measure(string value) => ImGui.CalcTextSize(value).X * fontScale;

        float unwrapped = Measure(bubble.Text);
        float textWidth = unwrapped > wrapCap
            ? wrapCap : MathF.Max(unwrapped, 2f * border);
        List<string> lines = WrapBubbleText(bubble.Text, textWidth, Measure);
        float lineHeight = MathF.Max(fontSize, ImGui.CalcTextSize("Ag").Y * fontScale);
        float textHeight = MathF.Max(lineHeight, lines.Count * lineHeight);
        float width = MathF.Ceiling(textWidth) + 2f * margin;
        float height = MathF.Ceiling(textHeight) + 2f * margin;

        float deviceScale = MathF.Max(1f, ImGui.GetIO().DisplayFramebufferScale.X);
        float Snap(float value) => MathF.Round(value * deviceScale) / deviceScale;
        float left = Snap(seat.X - width * 0.5f);
        float bottom = Snap(seat.Y);
        var frame = new ScreenRect(left, bottom - height, left + width, bottom);
        uint white = WithAlpha(0xffffffffu, bubble.Alpha);

        uint background = _gameplayArt!.Handle(ChatBubbleUiLaw.BackgroundTexture);
        if (background != 0)
            draw.AddImage((nint)background,
                new Vector2(frame.Left + border, frame.Top + border),
                new Vector2(frame.Right - border, frame.Bottom - border),
                Vector2.Zero, Vector2.One, white);

        uint edge = _gameplayArt.RepeatHandle(ChatBubbleUiLaw.BackdropTexture);
        if (edge != 0) DrawChatBubbleBackdrop(draw, edge, frame, border, white);

        uint tail = _gameplayArt.Handle(ChatBubbleUiLaw.TailTexture);
        if (tail != 0)
        {
            float center = (frame.Left + frame.Right) * 0.5f;
            float top = frame.Bottom - border * 0.25f;
            draw.AddImage((nint)tail, new Vector2(center - border, top),
                new Vector2(center, top + border), Vector2.Zero, Vector2.One, white);
        }

        ImFontPtr font = ImGui.GetFont();
        uint color = WithAlpha(ChatFrameLaw.Color(bubble.Type), bubble.Alpha);
        float y = frame.Top + margin;
        foreach (string line in lines)
        {
            float lineWidth = Measure(line);
            draw.AddText(font, fontSize,
                new Vector2((frame.Left + frame.Right - lineWidth) * 0.5f, y), color, line);
            y += lineHeight;
        }
    }

    private static List<string> WrapBubbleText(string text, float maxWidth,
        Func<string, float> measure)
    {
        string[] words = text.Replace('\t', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        List<string> lines = [];
        string line = "";
        foreach (string rawWord in words)
        {
            string word = rawWord;
            string candidate = line.Length == 0 ? word : line + " " + word;
            if (measure(candidate) <= maxWidth)
            {
                line = candidate;
                continue;
            }
            if (line.Length > 0)
            {
                lines.Add(line);
                line = "";
            }

            // Font layout hard-wraps an overlong token rather than widening the card.
            while (word.Length > 0 && measure(word) > maxWidth)
            {
                int count = 1;
                while (count < word.Length && measure(word[..(count + 1)]) <= maxWidth) count++;
                lines.Add(word[..count]);
                word = word[count..];
            }
            line = word;
        }
        if (line.Length > 0) lines.Add(line);
        if (lines.Count == 0) lines.Add("");
        return lines;
    }

    private static void DrawChatBubbleBackdrop(ImDrawListPtr draw, uint texture,
        ScreenRect frame, float edge, uint color)
    {
        if (edge <= 0f) return;
        float widthRun = MathF.Max(0f, frame.Width / edge - 2f);
        float heightRun = MathF.Max(0f, frame.Height / edge - 2f);
        const float slice = 0.125f;
        const float halfU = 0.5f / 256f;
        const float halfV = 0.5f / 32f;

        float InsetRunStart(float run) => run <= 1f ? MathF.Min(halfV, run * 0.5f) : 0f;
        float InsetRunEnd(float run) => run <= 1f ? MathF.Max(run * 0.5f, run - halfV) : run;
        float h0 = InsetRunStart(heightRun), h1 = InsetRunEnd(heightRun);
        float w0 = InsetRunStart(widthRun), w1 = InsetRunEnd(widthRun);

        void Quad(float left, float top, float right, float bottom,
            Vector2 uvTl, Vector2 uvTr, Vector2 uvBr, Vector2 uvBl) =>
            draw.AddImageQuad((nint)texture,
                new Vector2(left, top), new Vector2(right, top),
                new Vector2(right, bottom), new Vector2(left, bottom),
                uvTl, uvTr, uvBr, uvBl, color);

        // Tiling runs keep their unbounded axis exact. Corners inset both axes
        // onto texel centres so bilinear filtering cannot bleed atlas neighbours.
        Quad(frame.Left, frame.Top + edge, frame.Left + edge, frame.Bottom - edge,
            new(halfU, h0), new(slice - halfU, h0),
            new(slice - halfU, h1), new(halfU, h1));
        Quad(frame.Right - edge, frame.Top + edge, frame.Right, frame.Bottom - edge,
            new(slice + halfU, h0), new(2f * slice - halfU, h0),
            new(2f * slice - halfU, h1), new(slice + halfU, h1));
        Quad(frame.Left + edge, frame.Top, frame.Right - edge, frame.Top + edge,
            new(2f * slice + halfU, w1), new(2f * slice + halfU, w0),
            new(3f * slice - halfU, w0), new(3f * slice - halfU, w1));
        Quad(frame.Left + edge, frame.Bottom - edge, frame.Right - edge, frame.Bottom,
            new(3f * slice + halfU, w1), new(3f * slice + halfU, w0),
            new(4f * slice - halfU, w0), new(4f * slice - halfU, w1));

        void Corner(float left, float top, int cell)
        {
            float u0 = cell * slice + halfU;
            float u1 = (cell + 1) * slice - halfU;
            Quad(left, top, left + edge, top + edge,
                new(u0, halfV), new(u1, halfV),
                new(u1, 1f - halfV), new(u0, 1f - halfV));
        }
        Corner(frame.Left, frame.Top, 4);
        Corner(frame.Right - edge, frame.Top, 5);
        Corner(frame.Left, frame.Bottom - edge, 6);
        Corner(frame.Right - edge, frame.Bottom - edge, 7);
    }
}
