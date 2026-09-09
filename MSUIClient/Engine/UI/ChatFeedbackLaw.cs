namespace MSUIClient.Engine.UI;

public static class ChatFeedbackLaw
{
    // Mounted ChatFrame.lua: CHAT_TELL_ALERT_TIME, extended by every whisper.
    public const double WhisperQuietSeconds = 300;
    public static bool WhisperAlertDue(double now, double quietUntil) => now > quietUntil;

    public static string ScrollCue(string direction) => direction switch
    {
        "ScrollUp" => "igChatScrollUp",
        "ScrollDown" => "igChatScrollDown",
        "ScrollEnd" => "igChatBottom",
        _ => "",
    };
}
