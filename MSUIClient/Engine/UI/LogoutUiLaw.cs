namespace MSUIClient.Engine.UI;

/// <summary>
/// Build-5875 logout decision table. The server owns whether logout is refused,
/// instant, or a 20-second wait; the client only remembers whether the identical
/// request began as Logout or Exit Game so it can choose the matching narration.
/// </summary>
public enum LogoutResponseAction
{
    Refused,
    AwaitCompletion,
    ShowCampCountdown,
    ShowQuitCountdown,
}

public readonly record struct LogoutResponse(uint Reason, bool Instant)
{
    public static LogoutResponse Parse(ReadOnlySpan<byte> body)
    {
        if (body.Length != 5)
            throw new InvalidDataException($"SMSG_LOGOUT_RESPONSE must be 5 bytes, got {body.Length}");
        uint reason = (uint)(body[0] | body[1] << 8 | body[2] << 16 | body[3] << 24);
        return new LogoutResponse(reason, body[4] != 0);
    }
}

public static class LogoutUiLaw
{
    public const float CountdownSeconds = 20f;
    public const string RefusedText = "You can't logout now.";

    public static LogoutResponseAction Decide(LogoutResponse response, bool quitting)
    {
        if (response.Reason != 0) return LogoutResponseAction.Refused;
        if (response.Instant) return LogoutResponseAction.AwaitCompletion;
        return quitting
            ? LogoutResponseAction.ShowQuitCountdown
            : LogoutResponseAction.ShowCampCountdown;
    }

    public static string CountdownText(bool quitting, float secondsRemaining)
    {
        int seconds = Math.Max(0, (int)MathF.Ceiling(secondsRemaining));
        string unit = seconds == 1 ? "second" : "seconds";
        return quitting
            ? $"{seconds} {unit} until exit"
            : $"{seconds} {unit} until logout";
    }
}
