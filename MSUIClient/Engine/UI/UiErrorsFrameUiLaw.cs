using System.Numerics;

namespace MSUIClient.Engine.UI;

public enum UiMessageKind { Info, Error }

/// <summary>ErrorsFrame.xml's MessageFrame geometry, palette, capacity, and fade curve.</summary>
public static class UiErrorsFrameUiLaw
{
    public const float Width = 512f;
    public const float Height = 60f;
    public const float TopOffset = 122f;
    public const float LineHeight = 20f;
    public const int VisibleLines = 3;
    public const double HoldSeconds = 5;
    public const double FadeSeconds = 3;
    public const string Font = "ErrorFont";
    public static readonly Vector4 InfoColor = new(1f, 1f, 0f, 1f);
    public static readonly Vector4 ErrorColor = new(1f, .1f, .1f, 1f);

    public readonly record struct ScreenRect(Vector2 Min, Vector2 Size);

    public static ScreenRect FrameRect(Vector2 displayPixels, float scale)
    {
        Vector2 size = new Vector2(Width, Height) * scale;
        return new(new Vector2((displayPixels.X - size.X) * .5f, TopOffset * scale), size);
    }

    public static Vector2 LineCenter(int row) =>
        new(Width * .5f, (row + .5f) * LineHeight);

    public static Vector4 Color(UiMessageKind kind) =>
        kind == UiMessageKind.Info ? InfoColor : ErrorColor;

    public static float Alpha(double age)
    {
        if (age < 0 || age >= HoldSeconds + FadeSeconds) return 0;
        if (age <= HoldSeconds) return 1;
        return (float)(1 - (age - HoldSeconds) / FadeSeconds);
    }
}

public sealed class UiErrorsFrameState
{
    public readonly record struct Message(string Text, UiMessageKind Kind, double Born, long Id);
    public readonly record struct VisibleMessage(string Text, UiMessageKind Kind, float Alpha);

    private readonly List<Message> _messages = [];
    private long _nextId;

    public void Push(string text, UiMessageKind kind, double now)
    {
        if (string.IsNullOrEmpty(text)) return;
        Purge(now);
        _messages.Add(new(text, kind, now, ++_nextId));
    }

    public IReadOnlyList<VisibleMessage> Visible(double now)
    {
        Purge(now);
        return _messages.OrderByDescending(message => message.Id)
            .Take(UiErrorsFrameUiLaw.VisibleLines)
            .Select(message => new VisibleMessage(message.Text, message.Kind,
                UiErrorsFrameUiLaw.Alpha(now - message.Born)))
            .ToArray();
    }

    public void Clear() => _messages.Clear();

    private void Purge(double now) => _messages.RemoveAll(message =>
        now - message.Born >= UiErrorsFrameUiLaw.HoldSeconds + UiErrorsFrameUiLaw.FadeSeconds);
}
