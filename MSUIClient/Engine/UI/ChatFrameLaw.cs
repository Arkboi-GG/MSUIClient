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
    public enum IgnoredSenderAction
    {
        Continue,
        Drop,
        DropAndNotify,
    }

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
        MonsterWhisper = 0x1A,
        BgSystemNeutral = 0x52, BgSystemAlliance = 0x53, BgSystemHorde = 0x54,
        RaidLeader = 0x57, RaidWarning = 0x58,
        RaidBossWhisper = 0x59, RaidBossEmote = 0x5A,
        Battleground = 0x5C, BattlegroundLeader = 0x5D,
        // Client-composed lines - NEVER SMSG_MESSAGECHAT wire in 1.12 (loot/skill/
        // money ride their own packets; channel notices ride SMSG_CHANNEL_NOTIFY).
        // Private values above the wire range so a raw wire byte never casts onto one.
        Loot = 0xF0, Skill = 0xF1, Money = 0xF2, CombatXpGain = 0xF3,
        ChannelJoin = 0xF4, ChannelLeave = 0xF5, ChannelList = 0xF6,
        ChannelNotice = 0xF7, ChannelNoticeUser = 0xF8,
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
        MsgType.RaidLeader or MsgType.RaidWarning
            or MsgType.RaidBossEmote or MsgType.BattlegroundLeader
                                                      => Rgb(255, 219, 183),
        MsgType.RaidBossWhisper                       => Rgb(179, 179, 179),
        MsgType.BgSystemNeutral                       => Rgb(255, 120,  10),
        MsgType.BgSystemAlliance                      => Rgb(  0, 174, 239),
        MsgType.BgSystemHorde                         => Rgb(255,   0,   0),
        _                                             => Rgb(255, 255,   0),
    };

    /// <summary>The only eight wire types passed through the build-5875 NPC-text expander.</summary>
    public static bool MacroExpanded(MsgType type) => type is
        MsgType.MonsterSay or MsgType.MonsterYell or MsgType.MonsterEmote or
        MsgType.MonsterWhisper or MsgType.BgSystemNeutral or MsgType.BgSystemAlliance or
        MsgType.BgSystemHorde or MsgType.RaidBossEmote;

    public static string ChatFlag(byte tag) => tag switch
    {
        1 => "<AFK>",
        2 => "<DND>",
        3 => "<GM>",
        _ => "",
    };

    /// <summary>
    /// Build-5875's receive-side ignore gate. Every line from an ignored GUID is dropped;
    /// only an ordinary WHISPER earns CMSG_CHAT_IGNORED. LANG_ADDON (-1 on the wire) is
    /// filtered before that response gate and therefore never sends the notification.
    /// </summary>
    public static IgnoredSenderAction IgnoredSender(bool ignored, MsgType type, uint language)
    {
        if (!ignored) return IgnoredSenderAction.Continue;
        if (language == uint.MaxValue) return IgnoredSenderAction.Drop;
        return type == MsgType.Whisper
            ? IgnoredSenderAction.DropAndNotify
            : IgnoredSenderAction.Drop;
    }

    /// <summary>FrameXML's CHAT_*_GET composition, with player hyperlinks represented by their
    /// visible bracketed text because MSUI's scrolling-message renderer is not a hyperlink VM.</summary>
    public static string FormatLine(MsgType type, string sender, string channel, string message,
        byte chatTag = 0)
    {
        if (type is MsgType.System or MsgType.TextEmote or MsgType.Skill or MsgType.Loot or
            MsgType.Money or MsgType.CombatXpGain or MsgType.BgSystemNeutral or
            MsgType.BgSystemAlliance or MsgType.BgSystemHorde)
            return message;
        if (type == MsgType.Ignored) return $"{sender} is ignoring you.";

        string flag = ChatFlag(chatTag);
        bool monster = type is MsgType.MonsterSay or MsgType.MonsterYell or MsgType.MonsterEmote or
            MsgType.MonsterWhisper or MsgType.RaidBossWhisper or MsgType.RaidBossEmote;
        string named = sender.Length == 0 ? "" : monster || type == MsgType.Emote
            ? flag + sender
            : $"{flag}|Hplayer:{sender}|h[{sender}]|h";

        string body = type switch
        {
            MsgType.Say or MsgType.MonsterSay => $"{named} says: {message}",
            MsgType.Yell or MsgType.MonsterYell => $"{named} yells: {message}",
            MsgType.Whisper or MsgType.MonsterWhisper or MsgType.RaidBossWhisper =>
                $"{named} whispers: {message}",
            MsgType.WhisperInform => $"To {named}: {message}",
            MsgType.Emote => $"{named} {message}",
            MsgType.MonsterEmote or MsgType.RaidBossEmote => message.Replace("%s", named),
            MsgType.Afk => $"{named} is Away From Keyboard: {message}",
            MsgType.Dnd => $"{named} does not wish to be disturbed: {message}",
            MsgType.Party => $"[Party] {named}: {message}",
            MsgType.Guild => $"[Guild] {named}: {message}",
            MsgType.Officer => $"[Officer] {named}: {message}",
            MsgType.Raid => $"[Raid] {named}: {message}",
            MsgType.RaidLeader => $"[Raid Leader] {named}: {message}",
            MsgType.RaidWarning => $"[Raid Warning] {named}: {message}",
            MsgType.Battleground => $"[Battleground] {named}: {message}",
            MsgType.BattlegroundLeader => $"[Battleground Leader] {named}: {message}",
            MsgType.Channel => $"{named}: {message}",
            _ => message,
        };
        return channel.Length == 0 ? body : $"[{StripChannelZone(channel)}] {body}";
    }

    public static string StripChannelZone(string channel)
    {
        int split = channel.IndexOf(" - ", StringComparison.Ordinal);
        return split < 0 ? channel : channel[..split];
    }

    public static string FormatRandomRoll(string name, uint result, uint minimum, uint maximum) =>
        $"{name} rolls {result} ({minimum}-{maximum})";

    public static string FormatExplorationToast(string areaName) =>
        $"Discovered: {areaName}";

    public static string FormatExplorationLine(string areaName, uint experience) =>
        $"Discovered {areaName}: {experience} experience gained";

    /// <summary>
    /// The two default 1.12 chat windows do not share a feed. General owns the
    /// ordinary system/social/loot/skill groups; Combat Log currently owns the
    /// MONEY and COMBAT_XP_GAIN groups.
    /// </summary>
    public static bool VisibleInTab(MsgType type, int tab) => tab switch
    {
        1 => type is MsgType.Money or MsgType.CombatXpGain,
        _ => type is not MsgType.Money and not MsgType.CombatXpGain,
    };

    /// <summary>GlobalStrings COMBATLOG_XPGAIN_FIRSTPERSON and its rested/unnamed forms.</summary>
    public static string FormatXpGain(string? victim, uint total, uint bonus)
    {
        if (string.IsNullOrEmpty(victim)) return $"You gain {total} experience.";
        return bonus > 0
            ? $"{victim} dies, you gain {total} experience. (+{bonus} exp Rested bonus)"
            : $"{victim} dies, you gain {total} experience.";
    }

    public static (string Total, string Level) FormatPlayedTime(uint total, uint level) =>
        ($"Total time played: {FormatDuration(total)}",
         $"Time played this level: {FormatDuration(level)}");

    private static string FormatDuration(uint seconds)
    {
        uint days = seconds / 86_400;
        uint rem = seconds % 86_400;
        uint hours = rem / 3_600;
        rem %= 3_600;
        return $"{days} days, {hours} hours, {rem / 60} minutes, {rem % 60} seconds";
    }

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
        MsgType.Channel => "Channel: ",
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
