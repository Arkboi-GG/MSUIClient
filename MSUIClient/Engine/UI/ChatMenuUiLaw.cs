using System.Numerics;

namespace MSUIClient.Engine.UI;

public enum ChatMenuLevel { None, Root, Emote, VoiceEmote }

public readonly record struct ChatMenuRow(
    string Label,
    string Shortcut = "",
    string InputPrefix = "",
    string Command = "",
    ChatMenuLevel Nested = ChatMenuLevel.None);

/// <summary>
/// Benilla's 1.12 UIMenu chat table and layout. The renderer gets every seat,
/// anchor and hit row from this law; no ImGui window position or auto-layout is
/// allowed to decide where a chat popup lands.
/// </summary>
public static class ChatMenuUiLaw
{
    public const float ButtonHeight = 16f;
    public const float ButtonWidth = 104f;
    public const float BorderHeight = 12f;
    public const float BorderWidth = 12f;
    public const float CardWidth = ButtonWidth + BorderWidth * 2f;
    public const float ButtonSize = 32f;
    public const float NestedX = 10f;
    public const float NestedY = -12f;
    public const float TimeoutSeconds = 2f;
    public const float ViewportMargin = 4f;
    public const string OpenSound = "igChatEmoteButton";
    public const string RowSound = "UChatScrollButton";
    public const string SoundCategory = "ui.chat-menu";

    private static readonly ChatMenuRow[] Root =
    [
        new("Say", "/s", "/s "),
        new("Party Chat", "/p", "/p "),
        new("Guild Chat", "/g", "/g "),
        new("Yell", "/y", "/y "),
        new("Whisper", "/w", "/w "),
        new("Emote", "/e", "/e ", Nested: ChatMenuLevel.Emote),
        new("Reply", "/r", "/r "),
        new("Voice Emote", Nested: ChatMenuLevel.VoiceEmote),
    ];

    private static readonly string[] EmoteCommands =
    [
        "/applaud", "/beg", "/bow", "/chicken", "/cry", "/dance", "/eat", "/flex",
        "/kiss", "/kneel", "/laugh", "/point", "/roar", "/rude", "/salute", "/shy",
        "/sit", "/sleep", "/stand", "/talk", "/wave",
    ];

    private static readonly string[] VoiceCommands =
    [
        "/attackmytarget", "/bye", "/charge", "/cheer", "/congratulate", "/flee",
        "/flirt", "/follow", "/healme", "/hello", "/helpme", "/incoming", "/joke",
        "/no", "/nod", "/oom", "/openfire", "/rasp", "/thank", "/train", "/wait",
        "/welcome",
    ];

    private static readonly ChatMenuRow[] Emotes =
        EmoteCommands.Select(command => new ChatMenuRow(command, Command: command)).ToArray();
    private static readonly ChatMenuRow[] VoiceEmotes =
        VoiceCommands.Select(command => new ChatMenuRow(command, Command: command)).ToArray();

    public static IReadOnlyList<ChatMenuRow> Rows(ChatMenuLevel level) => level switch
    {
        ChatMenuLevel.Root => Root,
        ChatMenuLevel.Emote => Emotes,
        ChatMenuLevel.VoiceEmote => VoiceEmotes,
        _ => Array.Empty<ChatMenuRow>(),
    };

    public static float CardHeight(int rows) =>
        Math.Max(0, rows) * ButtonHeight + BorderHeight * 2f;

    public static Vector2 RowOrigin(int index) =>
        new(BorderWidth, BorderHeight + Math.Max(0, index) * ButtonHeight);

    public static Vector2 RowSize => new(ButtonWidth, ButtonHeight);
    public static Vector2 TextOrigin(int index) => RowOrigin(index) + new Vector2(0f, 3f);

    /// <summary>ChatMenu BOTTOMLEFT to the bubble button TOPRIGHT.</summary>
    public static Vector2 RootOrigin(Vector2 buttonMin, int rows, Vector2 displaySize) =>
        Clamp(new Vector2(buttonMin.X + ButtonSize,
            buttonMin.Y - CardHeight(rows)), new(CardWidth, CardHeight(rows)), displaySize);

    /// <summary>Nested BOTTOMLEFT to parent row BOTTOMRIGHT at (10,-12).</summary>
    public static Vector2 SubmenuOrigin(Vector2 parentOrigin, int parentRow,
        int childRows, Vector2 displaySize)
    {
        float childHeight = CardHeight(childRows);
        Vector2 desired = new(
            parentOrigin.X + BorderWidth + ButtonWidth + NestedX,
            parentOrigin.Y + BorderHeight + (parentRow + 1) * ButtonHeight - NestedY - childHeight);
        return Clamp(desired, new(CardWidth, childHeight), displaySize);
    }

    public static int HitRow(Vector2 point, Vector2 origin, int rows)
    {
        Vector2 local = point - origin;
        if (local.X < BorderWidth || local.X >= BorderWidth + ButtonWidth ||
            local.Y < BorderHeight || local.Y >= BorderHeight + rows * ButtonHeight)
            return -1;
        return Math.Clamp((int)((local.Y - BorderHeight) / ButtonHeight), 0, rows - 1);
    }

    public static bool Contains(Vector2 point, Vector2 origin, int rows) =>
        point.X >= origin.X && point.X < origin.X + CardWidth &&
        point.Y >= origin.Y && point.Y < origin.Y + CardHeight(rows);

    private static Vector2 Clamp(Vector2 desired, Vector2 size, Vector2 display)
    {
        float maxX = MathF.Max(ViewportMargin, display.X - size.X - ViewportMargin);
        float maxY = MathF.Max(ViewportMargin, display.Y - size.Y - ViewportMargin);
        return new(Math.Clamp(desired.X, ViewportMargin, maxX),
            Math.Clamp(desired.Y, ViewportMargin, maxY));
    }
}
