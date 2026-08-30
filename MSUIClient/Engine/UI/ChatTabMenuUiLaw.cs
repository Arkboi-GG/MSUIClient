using System.Numerics;

namespace MSUIClient.Engine.UI;

/// <summary>Which flyout is showing at a given nesting depth. None means that depth is closed.</summary>
public enum ChatTabMenuLevel
{
    None, Root, FontSize, Channels, ChannelGuild, ChannelWhisper, ChannelParty,
    SystemMessages, OtherMessages, OtherCreature, OtherLoot,
}

public enum ChatTabMenuRowKind { Header, Submenu, FontSize, MsgType }

/// <summary>
/// One menu row. Header rows are non-interactive section labels (gold, no hover).
/// Submenu rows open a nested ChatTabMenuLevel on hover. FontSize rows carry a point
/// size and render a checkmark when selected. MsgType rows carry a checkbox (visible
/// in this tab) and a color swatch (opens an inline ColorEdit3 popup on click).
/// </summary>
public readonly record struct ChatTabMenuRow(
    string Label,
    ChatTabMenuRowKind Kind = ChatTabMenuRowKind.Submenu,
    ChatTabMenuLevel Nested = ChatTabMenuLevel.None,
    ChatFrameLaw.MsgType? Type = null,
    int FontPt = 0);

/// <summary>
/// Layout/content for the chat tab right-click settings menu. Rows are wider and the
/// tree is deeper than ChatMenuUiLaw's emote flyout (up to 3 levels: e.g. Channels ->
/// Party -> Raid Warning), so this is its own law rather than an extension of that one.
/// </summary>
public static class ChatTabMenuUiLaw
{
    public const float RowHeight = 16f;
    public const float BorderWidth = 10f;
    public const float BorderHeight = 8f;
    public const float MinContentWidth = 130f;
    public const float SwatchSize = 12f;
    public const float SwatchMargin = 6f;
    public const float CheckWidth = 14f;
    public const float ArrowWidth = 12f;
    public const float NestedX = 4f;
    public const float TimeoutSeconds = 3f;
    public const float ViewportMargin = 4f;

    public static readonly ChatTabMenuRow[] Root =
    [
        new("Display", ChatTabMenuRowKind.Header),
        new("Font Size", Nested: ChatTabMenuLevel.FontSize),
        new("Filters", ChatTabMenuRowKind.Header),
        new("Channels", Nested: ChatTabMenuLevel.Channels),
        new("System Messages", Nested: ChatTabMenuLevel.SystemMessages),
        new("Other Messages", Nested: ChatTabMenuLevel.OtherMessages),
    ];

    public static readonly ChatTabMenuRow[] FontSizes =
    [
        new("12 pt", ChatTabMenuRowKind.FontSize, FontPt: 12),
        new("14 pt", ChatTabMenuRowKind.FontSize, FontPt: 14),
        new("16 pt", ChatTabMenuRowKind.FontSize, FontPt: 16),
        new("18 pt", ChatTabMenuRowKind.FontSize, FontPt: 18),
    ];

    private static ChatTabMenuRow MsgRow(string label, ChatFrameLaw.MsgType type,
        ChatTabMenuLevel nested = ChatTabMenuLevel.None) =>
        new(label, ChatTabMenuRowKind.MsgType, nested, type);

    public static readonly ChatTabMenuRow[] Channels =
    [
        MsgRow("Say", ChatFrameLaw.MsgType.Say),
        MsgRow("Yell", ChatFrameLaw.MsgType.Yell),
        MsgRow("Guild", ChatFrameLaw.MsgType.Guild, ChatTabMenuLevel.ChannelGuild),
        MsgRow("Whisper", ChatFrameLaw.MsgType.Whisper, ChatTabMenuLevel.ChannelWhisper),
        MsgRow("Party", ChatFrameLaw.MsgType.Party, ChatTabMenuLevel.ChannelParty),
        MsgRow("General", ChatFrameLaw.MsgType.Channel),
    ];

    public static readonly ChatTabMenuRow[] ChannelGuild =
    [
        MsgRow("Guild", ChatFrameLaw.MsgType.Guild),
        MsgRow("Officer", ChatFrameLaw.MsgType.Officer),
    ];

    public static readonly ChatTabMenuRow[] ChannelWhisper =
    [
        MsgRow("Incoming Whisper", ChatFrameLaw.MsgType.Whisper),
        MsgRow("Whisper", ChatFrameLaw.MsgType.WhisperInform),
    ];

    public static readonly ChatTabMenuRow[] ChannelParty =
    [
        MsgRow("Party", ChatFrameLaw.MsgType.Party),
        MsgRow("Raid", ChatFrameLaw.MsgType.Raid),
        MsgRow("Raid Leader", ChatFrameLaw.MsgType.RaidLeader),
        MsgRow("Raid Warning", ChatFrameLaw.MsgType.RaidWarning),
        MsgRow("Battleground", ChatFrameLaw.MsgType.Battleground),
        MsgRow("Battleground Leader", ChatFrameLaw.MsgType.BattlegroundLeader),
    ];

    public static readonly ChatTabMenuRow[] SystemMessages =
    [
        MsgRow("System", ChatFrameLaw.MsgType.System),
        MsgRow("AFK", ChatFrameLaw.MsgType.Afk),
        MsgRow("DND", ChatFrameLaw.MsgType.Dnd),
        MsgRow("Ignored", ChatFrameLaw.MsgType.Ignored),
        MsgRow("Channel List", ChatFrameLaw.MsgType.ChannelList),
        MsgRow("Neutral zone message", ChatFrameLaw.MsgType.BgSystemNeutral),
        MsgRow("Alliance zone message", ChatFrameLaw.MsgType.BgSystemAlliance),
        MsgRow("Horde zone message", ChatFrameLaw.MsgType.BgSystemHorde),
    ];

    public static readonly ChatTabMenuRow[] OtherMessages =
    [
        new("Creature", ChatTabMenuRowKind.Submenu, ChatTabMenuLevel.OtherCreature),
        MsgRow("Skill", ChatFrameLaw.MsgType.Skill),
        new("Loot", ChatTabMenuRowKind.Submenu, ChatTabMenuLevel.OtherLoot),
    ];

    public static readonly ChatTabMenuRow[] OtherCreature =
    [
        MsgRow("Creature Say", ChatFrameLaw.MsgType.MonsterSay),
        MsgRow("Creature Yell", ChatFrameLaw.MsgType.MonsterYell),
        MsgRow("Creature Emote", ChatFrameLaw.MsgType.MonsterEmote),
        MsgRow("Creature Whisper", ChatFrameLaw.MsgType.MonsterWhisper),
        MsgRow("Raid Boss Emote", ChatFrameLaw.MsgType.RaidBossEmote),
    ];

    public static readonly ChatTabMenuRow[] OtherLoot =
    [
        MsgRow("Item Loot", ChatFrameLaw.MsgType.Loot),
        MsgRow("Money Loot", ChatFrameLaw.MsgType.Money),
    ];

    public static IReadOnlyList<ChatTabMenuRow> Rows(ChatTabMenuLevel level) => level switch
    {
        ChatTabMenuLevel.Root => Root,
        ChatTabMenuLevel.FontSize => FontSizes,
        ChatTabMenuLevel.Channels => Channels,
        ChatTabMenuLevel.ChannelGuild => ChannelGuild,
        ChatTabMenuLevel.ChannelWhisper => ChannelWhisper,
        ChatTabMenuLevel.ChannelParty => ChannelParty,
        ChatTabMenuLevel.SystemMessages => SystemMessages,
        ChatTabMenuLevel.OtherMessages => OtherMessages,
        ChatTabMenuLevel.OtherCreature => OtherCreature,
        ChatTabMenuLevel.OtherLoot => OtherLoot,
        _ => [],
    };

    /// <summary>Content width in logical px, wide enough for the longest label in this row set.</summary>
    public static float ContentWidth(IReadOnlyList<ChatTabMenuRow> rows,
        Func<string, float> measure)
    {
        float widest = MinContentWidth;
        foreach (ChatTabMenuRow row in rows)
        {
            float w = measure(row.Label) + CheckWidth + SwatchMargin + SwatchSize + ArrowWidth;
            if (w > widest) widest = w;
        }
        return widest;
    }

    public static float CardHeight(int rows) => rows * RowHeight + BorderHeight * 2f;
    public static Vector2 CardSize(int rows, float contentWidth) =>
        new(contentWidth + BorderWidth * 2f, CardHeight(rows));

    public static Vector2 RowOrigin(int index, float contentWidth) =>
        new(BorderWidth, BorderHeight + index * RowHeight);
    public static Vector2 RowSize(float contentWidth) => new(contentWidth, RowHeight);
    public static Vector2 TextOrigin(int index, float contentWidth) =>
        RowOrigin(index, contentWidth) + new Vector2(CheckWidth, 2f);
    public static Vector2 SwatchOrigin(int index, float contentWidth) =>
        RowOrigin(index, contentWidth) + new Vector2(contentWidth - ArrowWidth - SwatchMargin - SwatchSize, 2f);
    public static Vector2 CheckOrigin(int index, float contentWidth) =>
        RowOrigin(index, contentWidth) + new Vector2(0f, 2f);

    public static Vector2 RootOrigin(Vector2 tabTopLeft, int rows, float contentWidth, Vector2 displaySize) =>
        Clamp(new Vector2(tabTopLeft.X, tabTopLeft.Y - CardHeight(rows)),
            CardSize(rows, contentWidth), displaySize);

    /// <summary>Nested flyout's TOPLEFT anchors to the parent row's TOPRIGHT, nudged in by NestedX.</summary>
    public static Vector2 SubmenuOrigin(Vector2 parentOrigin, int parentRow, float parentContentWidth,
        int childRows, float childContentWidth, Vector2 displaySize)
    {
        Vector2 desired = parentOrigin + new Vector2(
            BorderWidth + parentContentWidth + NestedX,
            RowOrigin(parentRow, parentContentWidth).Y - BorderHeight);
        return Clamp(desired, CardSize(childRows, childContentWidth), displaySize);
    }

    public static int HitRow(Vector2 point, Vector2 origin, int rows, float contentWidth)
    {
        Vector2 local = point - origin;
        if (local.X < 0f || local.X >= contentWidth + BorderWidth * 2f ||
            local.Y < BorderHeight || local.Y >= BorderHeight + rows * RowHeight)
            return -1;
        return Math.Clamp((int)((local.Y - BorderHeight) / RowHeight), 0, rows - 1);
    }

    public static bool Contains(Vector2 point, Vector2 origin, int rows, float contentWidth)
    {
        Vector2 size = CardSize(rows, contentWidth);
        return point.X >= origin.X && point.X < origin.X + size.X &&
               point.Y >= origin.Y && point.Y < origin.Y + size.Y;
    }

    private static Vector2 Clamp(Vector2 desired, Vector2 size, Vector2 display)
    {
        float maxX = MathF.Max(ViewportMargin, display.X - size.X - ViewportMargin);
        float maxY = MathF.Max(ViewportMargin, display.Y - size.Y - ViewportMargin);
        return new(Math.Clamp(desired.X, ViewportMargin, maxX),
            Math.Clamp(desired.Y, ViewportMargin, maxY));
    }
}
