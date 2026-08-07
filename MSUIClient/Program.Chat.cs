using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    // ── functional-input state (stage 3) ──────────────────────────────────────
    private string _chatInput = "";
    private bool _chatEditJustOpened, _chatEditActivated;
    // Sender GUIDs already NAME_QUERY'd, so an unresolved sender is asked once.
    private readonly HashSet<ulong> _chatNameQueried = [];

    // One buffered line: the cleaned text plus its 1.12 message type (the type
    // picks the colour through ChatFrameLaw). Ring-buffered at MaxLines.
    private readonly List<(string Text, ChatFrameLaw.MsgType Type)> _chat = [];
    private int _chatScroll;

    // Hover reveal (FloatingChatFrame.lua): the chrome + tabs sit at alpha 0 and
    // fade in only when the cursor rests on the frame. _chatReveal is 0..1.
    private float _chatReveal, _chatRevealTarget, _chatHoverTime;
    private bool _chatEditOpen;                       // edit box shown (Enter) - wired in a later stage
    private ChatFrameLaw.MsgType _chatSendType = ChatFrameLaw.MsgType.Say;
    private int _chatSelectedTab;                     // 0 = General, 1 = Combat Log

    // The docked tabs. General + Combat Log always exist in 1.12; Guild/Raid tabs
    // appear only in a guild/raid (that membership state isn't tracked yet, so
    // they're omitted rather than shown empty).
    private static readonly string[] ChatTabs = ["General", "Combat Log"];

    private void AddChatMessage(string text) => AddChatMessage(text, ChatFrameLaw.MsgType.System);

    private void AddChatMessage(string text, ChatFrameLaw.MsgType type)
    {
        string cleaned = string.Join(' ', text.Replace('|', ' ').Split(' ',
            StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (cleaned.Length == 0) return;
        if (_chat.Count == ChatFrameLaw.MaxLines) _chat.RemoveAt(0);
        _chat.Add((cleaned, type));
        _chatScroll = 0;
    }

    // ── stage 2: live incoming (SMSG_MESSAGECHAT / SMSG_NOTIFICATION) ──────────

    /// <summary>
    /// Parse a 1.12 SMSG_MESSAGECHAT body and post it typed+coloured. Wire body:
    /// u8 type, u32 language, then a per-type sender block (MONSTER_* carry the
    /// name inline; CHANNEL carries the channel name then the sender GUID; all
    /// others just the sender GUID), then u32 msgLen, the message cstring, u8 tag.
    /// The dispatcher wraps this in try/catch, so a malformed body logs, not crashes.
    /// </summary>
    private void HandleMessageChat(byte[] body)
    {
        var r = new PacketReader(body);
        byte typeByte = r.ReadU8();
        r.ReadU32();                                   // language
        var type = (ChatFrameLaw.MsgType)typeByte;

        string sender = "", channel = "";
        switch (typeByte)
        {
            case 0x0D or 0x1A or 0x59 or 0x5A:         // MONSTER_EMOTE, MONSTER_WHISPER, RAID_BOSS_*
                sender = ReadLenString(r);             // name is inline (length-prefixed)
                r.ReadU64();                           // target guid
                break;
            case 0x00 or 0x01 or 0x05:                 // SAY, PARTY, YELL: sender guid appears TWICE
                sender = ResolveChatName(r.ReadU64());
                r.ReadU64();                           // sender guid again (vanilla quirk)
                break;
            case 0x0B or 0x0C:                         // MONSTER_SAY, MONSTER_YELL
                r.ReadU64();                           // sender guid (name is inline)
                sender = ReadLenString(r);
                r.ReadU64();                           // target guid
                break;
            case 0x0E:                                 // CHANNEL
                channel = r.ReadCString();
                r.ReadU32();                           // player rank
                sender = ResolveChatName(r.ReadU64());
                break;
            default:                                   // RAID/GUILD/OFFICER/WHISPER/SYSTEM/AFK/DND/…
                sender = ResolveChatName(r.ReadU64());
                break;
        }
        string msg = ReadLenString(r);
        // chatTag (u8) follows; unused.

        if (type == ChatFrameLaw.MsgType.System) UpdateGmModeFrom(msg);
        AddChatMessage(FormatChatLine(type, sender, channel, msg), type);
    }

    // u32 length (includes the NUL) + the string; 0 length = empty, no bytes follow.
    private static string ReadLenString(PacketReader r)
    {
        uint len = r.ReadU32();
        return len == 0 ? "" : r.ReadCString();
    }

    /// <summary>SMSG_NOTIFICATION is a lone cstring - post it as a system line.</summary>
    private void HandleNotification(byte[] body) =>
        AddChatMessage(new PacketReader(body).ReadCString(), ChatFrameLaw.MsgType.System);

    /// <summary>A sender GUID to a name via the shared cache; query once if unknown.</summary>
    private string ResolveChatName(ulong guid)
    {
        if (guid == 0) return "";
        if (_playerNames.TryGetValue(guid, out string? n) && n.Length > 0) return n;
        if (_chatNameQueried.Add(guid)) _net?.NameQuery(guid);
        return $"Player-{guid & 0xffff:X4}";           // placeholder until the response lands
    }

    // The GM-mode flag the combat dev-tools read used to be scraped from server
    // chat by the old crude decoder; keep that side effect on system lines.
    private void UpdateGmModeFrom(string text)
    {
        if (text.Contains("GM mode is ON", StringComparison.OrdinalIgnoreCase)) _serverGmMode = true;
        else if (text.Contains("GM mode is OFF", StringComparison.OrdinalIgnoreCase)) _serverGmMode = false;
    }

    /// <summary>1.12 CHAT_*_GET display formats: "Name says: msg", "[Name]: msg",
    /// "Name whispers: msg", "[Channel] Name: msg", plain system text, etc.</summary>
    private static string FormatChatLine(ChatFrameLaw.MsgType type, string sender, string channel, string msg)
        => type switch
        {
            ChatFrameLaw.MsgType.Say or ChatFrameLaw.MsgType.MonsterSay => $"{sender} says: {msg}",
            ChatFrameLaw.MsgType.Yell or ChatFrameLaw.MsgType.MonsterYell => $"{sender} yells: {msg}",
            ChatFrameLaw.MsgType.Whisper or ChatFrameLaw.MsgType.MonsterWhisper
                or ChatFrameLaw.MsgType.RaidBossWhisper => $"{sender} whispers: {msg}",
            ChatFrameLaw.MsgType.WhisperInform => $"To {sender}: {msg}",
            ChatFrameLaw.MsgType.Emote or ChatFrameLaw.MsgType.TextEmote
                or ChatFrameLaw.MsgType.MonsterEmote
                or ChatFrameLaw.MsgType.RaidBossEmote => $"{sender} {msg}",
            ChatFrameLaw.MsgType.Guild or ChatFrameLaw.MsgType.Officer or ChatFrameLaw.MsgType.Party
                or ChatFrameLaw.MsgType.Raid or ChatFrameLaw.MsgType.RaidLeader
                or ChatFrameLaw.MsgType.RaidWarning => $"[{sender}]: {msg}",
            ChatFrameLaw.MsgType.Channel => $"[{channel}] {sender}: {msg}",
            _ => msg,                                   // System, Loot, Skill, channel notices…
        };

    private void DrawChatFrame()
    {
        if (_gameplayArt is null) return;
        float s = GameplayUiScale();
        Vector2 logicalDisplay = ImGui.GetIO().DisplaySize / s;
        Vector2 root = new(ChatFrameLaw.AnchorX,
            logicalDisplay.Y - (95f + ChatFrameLaw.FrameHeight));
        Vector2 rootPx = root * s;
        ImDrawListPtr dl = ImGui.GetBackgroundDrawList();

        UpdateChatReveal(root, s);

        if (_uiParityArmed && _uiParityPanel == "chat-frame")
        {
            BeginUiParityFrame(rootPx, s);
            CollectUiParity("ChatFrame1", "ScrollingMessageFrame", rootPx,
                new Vector2(ChatFrameLaw.FrameWidth, ChatFrameLaw.FrameHeight) * s,
                parent: "", point: "BOTTOMLEFT", offsetX: "32", offsetY: "95", strata: "BACKGROUND");
            CollectUiParity("ChatFrame1/FontString", "FontString", rootPx,
                new Vector2(ChatFrameLaw.FrameWidth, ChatFrameLaw.FrameHeight) * s,
                parent: "ChatFrame1", font: "ChatFontNormal", fontPath: @"Fonts\ARIALN.TTF",
                fontSize: "14", color: "#FFFFFFFF", strata: "BACKGROUND");
            CollectUiParity("ChatFrame1Background", "Texture",
                (root + new Vector2(ChatFrameLaw.BgLeft, ChatFrameLaw.BgTop)) * s,
                new Vector2(ChatFrameLaw.FrameWidth, ChatFrameLaw.FrameHeight) * s,
                parent: "ChatFrame1", point: "TOPLEFT", offsetX: "-2", offsetY: "3",
                texture: ChatFrameLaw.Background, layer: "BACKGROUND", strata: "BACKGROUND");
        }

        DrawChatChrome(dl, root, s);
        DrawChatMessages(dl, root, s);
        // The left button column is ALWAYS visible (unlike the hover-fade chrome):
        // the speech-bubble menu on top, then scroll up / down / to-bottom, stacked
        // 30px (BottomButton BOTTOMLEFT (-32,-4), -2 overlaps).
        DrawChatMenuButton(dl, root + new Vector2(-32, -6));
        DrawChatScrollButton(dl, root + new Vector2(-32, 24), "ScrollUp", () => _chatScroll++);
        DrawChatScrollButton(dl, root + new Vector2(-32, 54), "ScrollDown",
            () => _chatScroll = Math.Max(0, _chatScroll - 1));
        DrawChatScrollButton(dl, root + new Vector2(-32, 84), "ScrollEnd", () => _chatScroll = 0);
        DrawChatTabs(dl, root, s);

        // Enter opens the input, unless another text field already owns the keyboard.
        if (!_chatEditOpen && !ImGui.GetIO().WantTextInput &&
            (ImGui.IsKeyPressed(ImGuiKey.Enter, false) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter, false)))
            OpenChatEdit();
        if (_chatEditOpen) DrawChatEditBox(dl, root, s);

        if (_uiParityArmed && _uiParityPanel == "chat-frame") MarkUiParityFrameComplete();
    }

    /// <summary>
    /// FCF reveal: a cursor that rests STILL on the frame's hover rect (expanded
    /// 45 up into the tabs, 10 down, 5 sides) for RevealDelay arms the reveal;
    /// leaving disarms it. The reveal eases toward its target over FadeTime, and
    /// every chrome alpha is (its target × reveal).
    /// </summary>
    private void UpdateChatReveal(Vector2 root, float s)
    {
        float dt = ImGui.GetIO().DeltaTime;
        Vector2 min = (root + new Vector2(-ChatFrameLaw.HoverSide, -ChatFrameLaw.HoverUp)) * s;
        Vector2 max = (root + new Vector2(ChatFrameLaw.FrameWidth + ChatFrameLaw.HoverSide,
            ChatFrameLaw.FrameHeight + ChatFrameLaw.HoverDown)) * s;
        bool over = ImGui.IsMouseHoveringRect(min, max, false);
        bool moved = ImGui.GetIO().MouseDelta.LengthSquared() > 0.01f;

        if (over)
        {
            _chatHoverTime = moved ? 0f : _chatHoverTime + dt;
            if (_chatHoverTime > ChatFrameLaw.RevealDelay) _chatRevealTarget = 1f;
        }
        else
        {
            _chatRevealTarget = 0f;
            _chatHoverTime = 0f;
        }
        if (_chatEditOpen) _chatRevealTarget = 1f;   // typing keeps it revealed

        float span = dt / ChatFrameLaw.FadeTime;
        if (_chatReveal < _chatRevealTarget) _chatReveal = MathF.Min(_chatRevealTarget, _chatReveal + span);
        else if (_chatReveal > _chatRevealTarget) _chatReveal = MathF.Max(_chatRevealTarget, _chatReveal - span);
    }

    /// <summary>Background + 8-slice border, both white art vertex-tinted BLACK
    /// (FCF_SetWindowColor ships 0,0,0), at ChromeAlpha × reveal.</summary>
    private void DrawChatChrome(ImDrawListPtr dl, Vector2 root, float s)
    {
        if (_chatReveal <= 0.001f) return;
        uint tint = BlackAlpha(ChatFrameLaw.ChromeAlpha * _chatReveal);

        // Background stretches -2/+2 in x, +3/-6 in y past the frame rect.
        Vector2 bgMin = root + new Vector2(ChatFrameLaw.BgLeft, ChatFrameLaw.BgTop);
        Vector2 bgSize = new(ChatFrameLaw.FrameWidth - ChatFrameLaw.BgLeft + ChatFrameLaw.BgRight,
            ChatFrameLaw.FrameHeight - ChatFrameLaw.BgTop - ChatFrameLaw.BgBottom + 3f);
        DrawChatTexture(dl, bgMin, bgSize, ChatFrameLaw.Background, Vector2.Zero, Vector2.One, tint);

        // 8 border slices anchored to the background rect, corners 16x16.
        float c = ChatFrameLaw.BorderCorner;
        float w = bgSize.X, h = bgSize.Y;
        DrawBorderSlice(dl, bgMin + new Vector2(-2, 2), new(c, c), 0, tint);            // TopLeft
        DrawBorderSlice(dl, bgMin + new Vector2(w + 2 - c, 2), new(c, c), 1, tint);      // TopRight
        DrawBorderSlice(dl, bgMin + new Vector2(-2, h - 2 - c), new(c, c), 2, tint);     // BottomLeft
        DrawBorderSlice(dl, bgMin + new Vector2(w + 2 - c, h - 2 - c), new(c, c), 3, tint); // BottomRight
        DrawBorderSlice(dl, bgMin + new Vector2(-2 + c, 2), new(w + 4 - 2 * c, c), 4, tint);      // Top
        DrawBorderSlice(dl, bgMin + new Vector2(-2 + c, h - 2 - c), new(w + 4 - 2 * c, c), 5, tint); // Bottom
        DrawBorderSlice(dl, bgMin + new Vector2(-2, 2 + c), new(c, h - 2 * c), 6, tint);            // Left
        DrawBorderSlice(dl, bgMin + new Vector2(w + 2 - c, 2 + c), new(c, h - 2 * c), 7, tint);     // Right
    }

    private void DrawBorderSlice(ImDrawListPtr dl, Vector2 min, Vector2 size, int slice, uint tint)
    {
        var (u0, v0, u1, v1) = ChatFrameLaw.BorderUv(slice);
        DrawChatTexture(dl, min, size, ChatFrameLaw.Border, new(u0, v0), new(u1, v1), tint);
    }

    private void DrawChatMessages(ImDrawListPtr dl, Vector2 root, float s)
    {
        float wrapPx = (ChatFrameLaw.FrameWidth - 8f) * s;
        float pitch = GameText.LinePitch(ChatFrameLaw.ChatFont, s);
        if (pitch <= 0f) return;

        var lines = new List<(string Text, uint Color)>();
        foreach (var (text, type) in _chat)
        {
            uint color = ChatFrameLaw.Color(type);
            foreach (string wl in WrapChatLine(text, s, wrapPx))
                lines.Add((wl, color));
        }

        int maxVisible = Math.Max(1, (int)(ChatFrameLaw.FrameHeight * s / pitch));
        _chatScroll = Math.Clamp(_chatScroll, 0, Math.Max(0, lines.Count - maxVisible));
        int bottom = lines.Count - _chatScroll;
        int top = Math.Max(0, bottom - maxVisible);

        float leftPx = (root.X + 4f) * s;
        float bottomLineTop = (root.Y + ChatFrameLaw.FrameHeight) * s - pitch;
        for (int i = bottom - 1, row = 0; i >= top; i--, row++)
            GameText.Draw(dl, ChatFrameLaw.ChatFont, lines[i].Text,
                new Vector2(leftPx, bottomLineTop - row * pitch), s, lines[i].Color);
    }

    /// <summary>Word-wrap to the message area width (the client's own GetStringWidth wrap).</summary>
    private static List<string> WrapChatLine(string text, float s, float maxWidthPx)
    {
        var lines = new List<string>();
        string current = "";
        foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = current.Length == 0 ? word : current + " " + word;
            if (current.Length > 0 &&
                GameText.MeasureWidth(ChatFrameLaw.ChatFont, candidate, s) > maxWidthPx)
            { lines.Add(current); current = word; }
            else current = candidate;
        }
        if (current.Length > 0) lines.Add(current);
        if (lines.Count == 0) lines.Add("");
        return lines;
    }

    private void DrawChatScrollButton(ImDrawListPtr dl, Vector2 logicalMin, string direction, Action click)
    {
        float s = GameplayUiScale();
        string texture = $@"Interface\ChatFrame\UI-ChatIcon-{direction}-Up";
        Vector2 min = logicalMin * s, size = new Vector2(32) * s;
        uint handle = _gameplayArt?.Handle(texture) ?? 0;
        // Always visible (the reference keeps the scroll column up regardless of hover).
        if (handle != 0) dl.AddImage((nint)handle, min, min + size);
        if (ImGui.IsMouseHoveringRect(min, min + size, false) &&
            ImGui.IsMouseClicked(ImGuiMouseButton.Left)) click();

        if (!_uiParityArmed || _uiParityPanel != "chat-frame") return;
        string name = direction switch { "ScrollUp" => "ChatFrame1UpButton",
            "ScrollDown" => "ChatFrame1DownButton", _ => "ChatFrame1BottomButton" };
        string point = direction == "ScrollUp" ? "BOTTOM" : direction == "ScrollDown" ? "BOTTOM" : "BOTTOMLEFT";
        string relative = direction == "ScrollUp" ? "ChatFrame1DownButton"
            : direction == "ScrollDown" ? "ChatFrame1BottomButton" : "";
        string relativePoint = direction is "ScrollUp" or "ScrollDown" ? "TOP" : "";
        string ox = direction == "ScrollEnd" ? "-32" : direction == "ScrollDown" ? "0" : "";
        string oy = direction == "ScrollEnd" ? "-4" : direction == "ScrollDown" ? "-2" : "";
        CollectUiParity(name, "Button", min, size, parent: "ChatFrame1", point: point,
            relativeTo: relative, relativePoint: relativePoint, offsetX: ox, offsetY: oy,
            texture: texture, strata: "BACKGROUND");
        CollectUiParity(name + "/NormalTexture", "NormalTexture", min, size, parent: name,
            texture: texture, strata: "BACKGROUND");
    }

    /// <summary>
    /// The docked tab row (General, Combat Log, …): each a 3-slice ChatFrameTab
    /// auto-sized to label+37, label in GameFontNormalSmall (FRIZQT 10 gold),
    /// UI-Character-Tab-Highlight (ADD) on mouseover. The tabs fade with the frame
    /// - selected at 1.0 × reveal, unselected at 0.5 × reveal. Label and highlight
    /// both sit below the tab centre (FrameXML y=-5 / -7, screen-down).
    /// </summary>
    private void DrawChatTabs(ImDrawListPtr dl, Vector2 root, float s)
    {
        if (_chatReveal <= 0.001f) return;
        int em = GameText.EmPixels(ChatFrameLaw.TabFont, s);
        float x = 0f;   // logical x from root

        for (int t = 0; t < ChatTabs.Length; t++)
        {
            string label = ChatTabs[t];
            bool selected = t == _chatSelectedTab;
            float midW = GameText.MeasureWidth(ChatFrameLaw.TabFont, label, s) / s + 5f;
            float tabW = ChatFrameLaw.TabCap * 2 + midW;
            Vector2 min = root + new Vector2(x, -35);
            float alpha = (selected ? ChatFrameLaw.TabSelectedAlpha : ChatFrameLaw.TabUnselectedAlpha) * _chatReveal;
            uint tint = WhiteAlpha(alpha);

            DrawChatTexture(dl, min, new(ChatFrameLaw.TabCap, ChatFrameLaw.TabHeight),
                ChatFrameLaw.Tab, new(0, 0), new(.25f, 1), tint);
            DrawChatTexture(dl, min + new Vector2(ChatFrameLaw.TabCap, 0), new(midW, ChatFrameLaw.TabHeight),
                ChatFrameLaw.Tab, new(.25f, 0), new(.75f, 1), tint);
            DrawChatTexture(dl, min + new Vector2(ChatFrameLaw.TabCap + midW, 0),
                new(ChatFrameLaw.TabCap, ChatFrameLaw.TabHeight),
                ChatFrameLaw.Tab, new(.75f, 0), Vector2.One, tint);

            if (ImGui.IsMouseHoveringRect(min * s, (min + new Vector2(tabW, ChatFrameLaw.TabHeight)) * s, false))
            {
                uint hl = _gameplayArt?.AdditiveHandle(ChatFrameLaw.TabHighlight) ?? 0;
                if (hl != 0)
                    dl.AddImage((nint)hl,
                        (min + new Vector2(0, ChatFrameLaw.TabHighlightDrop)) * s,
                        (min + new Vector2(tabW, ChatFrameLaw.TabHeight + ChatFrameLaw.TabHighlightDrop)) * s,
                        Vector2.Zero, Vector2.One, WhiteAlpha(_chatReveal));
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left)) _chatSelectedTab = t;
            }

            uint labelColor = WithAlpha(FontObjectLaw.Get(ChatFrameLaw.TabFont).Color, alpha);
            GameText.Draw(dl, ChatFrameLaw.TabFont, label,
                new Vector2((min.X + ChatFrameLaw.TabCap + 3f) * s,
                    min.Y * s + (ChatFrameLaw.TabHeight * s - em) * 0.5f + ChatFrameLaw.TabLabelDrop * s),
                s, labelColor);

            x += tabW;
        }
    }

    /// <summary>The speech-bubble chat menu button (UI-ChatIcon-Chat-Up), always
    /// visible. Opening the emote/channel menu on click is wired in a later stage.</summary>
    private void DrawChatMenuButton(ImDrawListPtr dl, Vector2 logicalMin)
    {
        float s = GameplayUiScale();
        Vector2 min = logicalMin * s, size = new Vector2(32) * s;
        uint handle = _gameplayArt?.Handle(ChatFrameLaw.MenuButton) ?? 0;
        if (handle != 0) dl.AddImage((nint)handle, min, min + size);
    }

    /// <summary>
    /// The chat input: opaque 3-piece UI-ChatInputBorder, header ("Say:"/"Guild:"…)
    /// in ChatFontNormal coloured by the current send type. Shown only while typing.
    /// </summary>
    private void DrawChatEditBox(ImDrawListPtr dl, Vector2 root, float s)
    {
        Vector2 min = root + new Vector2(-ChatFrameLaw.EditOutset,
            ChatFrameLaw.FrameHeight + ChatFrameLaw.EditDrop);
        float w = ChatFrameLaw.FrameWidth + 2f * ChatFrameLaw.EditOutset;
        DrawChatTexture(dl, min, new(ChatFrameLaw.EditLeftCap, ChatFrameLaw.EditHeight),
            ChatFrameLaw.EditLeft, Vector2.Zero, Vector2.One, 0xffffffffu);
        DrawChatTexture(dl, min + new Vector2(ChatFrameLaw.EditLeftCap, 0),
            new(w - ChatFrameLaw.EditLeftCap - ChatFrameLaw.EditRightCap, ChatFrameLaw.EditHeight),
            ChatFrameLaw.EditRight, Vector2.Zero, new(.9375f, 1), 0xffffffffu);
        DrawChatTexture(dl, min + new Vector2(w - ChatFrameLaw.EditRightCap, 0),
            new(ChatFrameLaw.EditRightCap, ChatFrameLaw.EditHeight),
            ChatFrameLaw.EditRight, new(.9375f, 0), Vector2.One, 0xffffffffu);

        // The header reflects the send type peeked from a leading /slash, and tints
        // both it and the typed text (Say white, Guild green, Whisper pink…).
        _chatSendType = PeekChatType(_chatInput);
        string header = ChatFrameLaw.Header(_chatSendType);
        uint color = ChatFrameLaw.Color(_chatSendType);
        int em = GameText.EmPixels(ChatFrameLaw.ChatFont, s);
        float textTop = min.Y * s + (ChatFrameLaw.EditHeight * s - em) * 0.5f;
        GameText.Draw(dl, ChatFrameLaw.ChatFont, header,
            new Vector2((min.X + ChatFrameLaw.EditHeaderInset) * s, textTop), s, color);

        // The editable field is a transparent ImGui InputText overlaid after the
        // header. Its focus sets WantCaptureKeyboard, which the movement gate at
        // Program.cs already honours - so WASD won't walk while typing.
        float headerW = GameText.MeasureWidth(ChatFrameLaw.ChatFont, header, s);
        float inX = (min.X + ChatFrameLaw.EditHeaderInset) * s + headerW + 2f * s;
        float inRight = (min.X + w - ChatFrameLaw.EditRightCap) * s;
        float inW = MathF.Max(16f, inRight - inX);
        float pad = MathF.Max(0f, (ChatFrameLaw.EditHeight * s - ImGui.GetFontSize()) * 0.5f);

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(0f, pad));
        ImGui.PushStyleColor(ImGuiCol.FrameBg, 0u);
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.SetNextWindowPos(new Vector2(inX, min.Y * s));
        ImGui.SetNextWindowSize(new Vector2(inW, ChatFrameLaw.EditHeight * s));
        if (ImGui.Begin("##chat-input-window", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground |
                ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoNav))
        {
            if (_chatEditJustOpened) { ImGui.SetKeyboardFocusHere(); _chatEditJustOpened = false; }
            ImGui.SetNextItemWidth(inW);
            bool submit = ImGui.InputText("##chat-edit", ref _chatInput, 255,
                ImGuiInputTextFlags.EnterReturnsTrue);
            bool active = ImGui.IsItemActive();
            if (active) _chatEditActivated = true;
            if (submit) SubmitChat();
            else if (_chatEditActivated && !active) CloseChatEdit();   // escape / click-away
        }
        ImGui.End();
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(2);
    }

    private void OpenChatEdit()
    {
        _chatEditOpen = true;
        _chatEditJustOpened = true;
        _chatEditActivated = false;
        _chatInput = "";
        _chatSendType = ChatFrameLaw.MsgType.Say;
    }

    private void CloseChatEdit()
    {
        _chatEditOpen = false;
        _chatEditActivated = false;
        _chatInput = "";
    }

    private void SubmitChat()
    {
        string raw = _chatInput.Trim();
        if (raw.Length > 0)
        {
            (ChatFrameLaw.MsgType type, string? target, string message) = ParseChatCommand(raw);
            // The server echoes our own line back as SMSG_MESSAGECHAT, so there is
            // no local echo here - it appears when the round-trip lands.
            if (message.Length > 0) _net?.SendChat((uint)type, target, message);
        }
        CloseChatEdit();
    }

    /// <summary>
    /// Split a typed line into (type, target, message). A leading /slash picks the
    /// channel (/s /y /g /o /p /raid, and /w Name for a whisper); anything else is
    /// Say. Unknown slashes fall through to Say verbatim.
    /// </summary>
    private static (ChatFrameLaw.MsgType, string?, string) ParseChatCommand(string raw)
    {
        if (!raw.StartsWith('/')) return (ChatFrameLaw.MsgType.Say, null, raw);
        int sp = raw.IndexOf(' ');
        string cmd = (sp < 0 ? raw : raw[..sp]).ToLowerInvariant();
        string rest = sp < 0 ? "" : raw[(sp + 1)..].TrimStart();
        switch (cmd)
        {
            case "/s" or "/say": return (ChatFrameLaw.MsgType.Say, null, rest);
            case "/y" or "/yell": return (ChatFrameLaw.MsgType.Yell, null, rest);
            case "/g" or "/guild": return (ChatFrameLaw.MsgType.Guild, null, rest);
            case "/o" or "/officer": return (ChatFrameLaw.MsgType.Officer, null, rest);
            case "/p" or "/party": return (ChatFrameLaw.MsgType.Party, null, rest);
            case "/raid" or "/ra": return (ChatFrameLaw.MsgType.Raid, null, rest);
            case "/w" or "/whisper" or "/tell" or "/t":
            {
                int sp2 = rest.IndexOf(' ');
                string target = sp2 < 0 ? rest : rest[..sp2];
                string msg = sp2 < 0 ? "" : rest[(sp2 + 1)..].TrimStart();
                return (ChatFrameLaw.MsgType.Whisper, target.Length > 0 ? target : null, msg);
            }
            default: return (ChatFrameLaw.MsgType.Say, null, raw);
        }
    }

    /// <summary>The send type implied by a leading /slash, for the header display.</summary>
    private static ChatFrameLaw.MsgType PeekChatType(string input)
    {
        string raw = input.TrimStart();
        if (!raw.StartsWith('/')) return ChatFrameLaw.MsgType.Say;
        int sp = raw.IndexOf(' ');
        string cmd = (sp < 0 ? raw : raw[..sp]).ToLowerInvariant();
        return cmd switch
        {
            "/y" or "/yell" => ChatFrameLaw.MsgType.Yell,
            "/g" or "/guild" => ChatFrameLaw.MsgType.Guild,
            "/o" or "/officer" => ChatFrameLaw.MsgType.Officer,
            "/p" or "/party" => ChatFrameLaw.MsgType.Party,
            "/raid" or "/ra" => ChatFrameLaw.MsgType.Raid,
            "/w" or "/whisper" or "/tell" or "/t" => ChatFrameLaw.MsgType.Whisper,
            _ => ChatFrameLaw.MsgType.Say,
        };
    }

    private void DrawChatTexture(ImDrawListPtr dl, Vector2 logicalMin, Vector2 logicalSize,
        string texture, Vector2 uv0, Vector2 uv1, uint tint)
    {
        uint handle = _gameplayArt?.Handle(texture) ?? 0;
        if (handle == 0) return;
        float s = GameplayUiScale();
        Vector2 min = logicalMin * s;
        dl.AddImage((nint)handle, min, min + logicalSize * s, uv0, uv1, tint);
    }

    // ABGR white/black at a given 0..1 alpha (WithAlpha lives in Program.Nameplates.cs).
    private static uint WhiteAlpha(float a) => WithAlpha(0xFFFFFFFFu, a);
    private static uint BlackAlpha(float a) => WithAlpha(0xFF000000u, a);
}
