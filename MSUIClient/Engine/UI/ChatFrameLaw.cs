namespace MSUIClient.Engine.UI;

/// <summary>
/// The 1.12 chat-frame rule set, transcribed from FrameXML so the renderer never
/// hand-picks a colour, size, alpha or timing. Message-type colours are the
/// shipped ChatTypeInfo/chat-cache COLORS table (byte-verified against benilla's
/// event.rs and wow-re chat-color-table.md); geometry and fade constants come
/// from ChatFrame.xml + FloatingChatFrame.xml/.lua.
///
/// Nothing here touches GL or ImGui - same rule as the other *Law files. The
/// renderer (Program.Chat.cs) reads these and draws through GameText/GameplayArt.
/// </summary>
public static class ChatFrameLaw
{
    // ── message types (wire CHAT_MSG_* order; the enum value IS the 1.12 type
    //    byte so an SMSG_MESSAGECHAT parse can cast straight to it) ────────────
    public enum MsgType : byte
    {
        // Wire CHAT_MSG_* bytes, VERIFIED against vmangos build 5875
        // (benilla-protocol/src/messages/chat.rs). The enum value IS the type byte,
        // so an SMSG_MESSAGECHAT parse casts straight to it.
        Say = 0x00, Party = 0x01, Raid = 0x02, Guild = 0x03, Officer = 0x04,
        Yell = 0x05, Whisper = 0x06, WhisperInform = 0x07, Emote = 0x08,
        TextEmote = 0x09, System = 0x0A, MonsterSay = 0x0B, MonsterYell = 0x0C,
        MonsterEmote = 0x0D, Channel = 0x0E, Afk = 0x14, Dnd = 0x15, Ignored = 0x16,
        MonsterWhisper = 0x1A, RaidLeader = 0x57, RaidWarning = 0x58,
        RaidBossWhisper = 0x59, RaidBossEmote = 0x5A,
        // Client-composed lines - NEVER SMSG_MESSAGECHAT wire in 1.12 (loot/skill/
        // money ride their own packets; channel notices ride SMSG_CHANNEL_NOTIFY).
        // Private values above the wire range so a raw wire byte never casts onto one.
        Loot = 0xF0, Skill = 0xF1, Money = 0xF2, CombatXpGain = 0xF3,
        ChannelJoin = 0xF4, ChannelLeave = 0xF5, ChannelList = 0xF6,
        ChannelNotice = 0xF7, ChannelNoticeUser = 0xF8,
        Battleground = 0xF9, BattlegroundLeader = 0xFA,
    }

    /// <summary>
    /// The default colour for a message type as ImGui ABGR (0xAABBGGRR). Values
    /// are the shipped 1.12 defaults (RGB 0-255); anything unlisted falls back to
    /// SYSTEM yellow, exactly as an unregistered ChatTypeInfo entry does.
    /// </summary>
    public static uint Color(MsgType type) => type switch
    {
        MsgType.Say                                   => Rgb(255, 255, 255),
        MsgType.Party                                 => Rgb(170, 170, 255),
        MsgType.Raid or MsgType.Battleground          => Rgb(255, 127,   0),
        MsgType.Guild                                 => Rgb( 64, 255,  64),
        MsgType.Officer                               => Rgb( 64, 192,  64),
        MsgType.Yell                                  => Rgb(255,  64,  64),
        MsgType.Whisper or MsgType.WhisperInform
            or MsgType.Afk or MsgType.Dnd             => Rgb(255, 128, 255),
        MsgType.Emote or MsgType.TextEmote            => Rgb(255, 128,  64),
        MsgType.System                                => Rgb(255, 255,   0),
        MsgType.MonsterSay                            => Rgb(255, 255, 159),
        MsgType.MonsterYell                           => Rgb(255,  64,  64),
        MsgType.MonsterEmote                          => Rgb(255, 128,  64),
        MsgType.MonsterWhisper                        => Rgb(179, 179, 179),
        MsgType.Channel                               => Rgb(255, 192, 192),
        MsgType.ChannelJoin or MsgType.ChannelLeave
            or MsgType.ChannelList                    => Rgb(192, 128, 128),
        MsgType.ChannelNotice
            or MsgType.ChannelNoticeUser              => Rgb(192, 192, 192),
        MsgType.Ignored                               => Rgb(255,   0,   0),
        MsgType.Skill                                 => Rgb( 85,  85, 255),
        MsgType.Loot                                  => Rgb(  0, 170,   0),
        MsgType.Money                                 => Rgb(255, 255,   0),
        MsgType.CombatXpGain                          => Rgb(111, 111, 255),
        MsgType.RaidLeader or MsgType.RaidWarning or MsgType.RaidBossWhisper
            or MsgType.RaidBossEmote or MsgType.BattlegroundLeader
                                                      => Rgb(255, 219, 183),
        _                                             => Rgb(255, 255,   0),
    };

    /// <summary>The edit-box header string for a send type ("Say: ", "Guild: "...).</summary>
    public static string Header(MsgType type) => type switch
    {
        MsgType.Say => "Say: ",
        MsgType.Yell => "Yell: ",
        MsgType.Party => "Party: ",
        MsgType.Raid => "Raid: ",
        MsgType.RaidWarning => "Raid Warning: ",
        MsgType.Guild => "Guild: ",
        MsgType.Officer => "Officer: ",
        MsgType.Battleground => "Battleground: ",
        _ => "Say: ",
    };

    // ── geometry (1.12 logical pixels, laid out on the 1024x768 canvas) ───────
    public const float FrameWidth = 430f, FrameHeight = 120f;
    public const float AnchorX = 32f, AnchorBottomY = 85f;   // BOTTOMLEFT (32, 85)
    public const int MaxLines = 128;
    public const float LinePitch = 14f;                       // ChatFontNormal em

    // Background stretches past the frame rect: TOPLEFT (-2,+3), BOTTOMRIGHT (+2,-6).
    public const float BgLeft = -2f, BgTop = -3f, BgRight = 2f, BgBottom = 6f;
    public const float BorderCorner = 16f;                    // 16x16 corner slices

    // Tab: 70x32 default, resized to (labelWidth + 37); 3-slice caps are 16 wide.
    // Label and hover-highlight both sit BELOW the tab centre (FrameXML y=-5 / -7,
    // and WoW's -y is screen-down): the tab art's body is in its lower portion.
    public const float TabHeight = 32f, TabCap = 16f, TabLabelInset = 20f;
    public const float TabLabelDrop = 5f, TabHighlightDrop = 7f;
    public const float TabGap = 2f;                           // tab BOTTOMLEFT x offset over background TOPLEFT

    // Edit box: below the frame, x -5/+5, y -2, 32 tall; opaque border; header +13.
    public const float EditHeight = 32f, EditOutset = 5f, EditDrop = 2f;
    public const float EditHeaderInset = 13f, EditLeftCap = 256f, EditRightCap = 16f;

    // ── fade / reveal (FloatingChatFrame.lua) ─────────────────────────────────
    public const float FadeTime = 0.15f;              // CHAT_FRAME_FADE_TIME
    public const float RevealDelay = 0.2f;            // CHAT_TAB_SHOW_DELAY (stationary cursor)
    public const float ChromeAlpha = 0.25f;           // DEFAULT_CHATFRAME_ALPHA (bg + borders)
    public const float TabSelectedAlpha = 1.0f;
    public const float TabUnselectedAlpha = 0.5f;
    // MouseIsOver(chatFrame, 45, -10, -5, 5): up 45 (tab zone), down 10, sides 5.
    public const float HoverUp = 45f, HoverDown = 10f, HoverSide = 5f;
    public const float FlashPeriod = 0.5f, FlashDuration = 60f;   // whisper tab flash

    // ── texture paths ─────────────────────────────────────────────────────────
    public const string Background = @"Interface\ChatFrame\ChatFrameBackground";
    public const string Border = @"Interface\ChatFrame\ChatFrameBorder";
    public const string Tab = @"Interface\ChatFrame\ChatFrameTab";
    public const string TabHighlight = @"Interface\PaperDollInfoFrame\UI-Character-Tab-Highlight";
    public const string EditLeft = @"Interface\ChatFrame\UI-ChatInputBorder-Left";
    public const string EditRight = @"Interface\ChatFrame\UI-ChatInputBorder-Right";
    public const string MenuButton = @"Interface\ChatFrame\UI-ChatIcon-Chat-Up";   // the speech-bubble menu
    public const string ChatFont = "ChatFontNormal";        // ARIALN 14 white, (1,-1) black shadow
    public const string TabFont = "GameFontNormalSmall";     // FRIZQT 10 gold

    /// <summary>One border slice's UV rect (ChatFrameBorder is a 4x8 atlas).</summary>
    public readonly record struct Slice(float U0, float V0, float U1, float V1,
        float OffX, float OffY, float W, float H);

    /// <summary>
    /// The 8 border slices with their ChatFrame.xml TexCoords, offset from the
    /// background rect, and logical size. Corners are 16x16; edges stretch.
    /// </summary>
    public static (float U0, float V0, float U1, float V1) BorderUv(int slice) => slice switch
    {
        0 => (0f,    0f,        0.25f, 0.125f),      // TopLeft
        1 => (0.75f, 0f,        1.0f,  0.125f),      // TopRight
        2 => (0f,    0.7265625f,0.25f, 0.8515625f),  // BottomLeft
        3 => (0.75f, 0.7265625f,1.0f,  0.8515625f),  // BottomRight
        4 => (0.25f, 0f,        0.75f, 0.125f),      // Top
        5 => (0.25f, 0.7265625f,0.75f, 0.8515625f),  // Bottom
        6 => (0f,    0.125f,    0.25f, 0.7265625f),  // Left
        _ => (0.75f, 0.125f,    1.0f,  0.7265625f),  // Right
    };

    private static uint Rgb(int r, int g, int b) =>
        0xFF000000u | ((uint)b << 16) | ((uint)g << 8) | (uint)r;
}
