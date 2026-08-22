using Silk.NET.Input;

namespace MSUIClient.Engine.UI;

/// <summary>One canonical 1.12 keyboard chord. Super/Cmd is deliberately not representable.</summary>
public readonly record struct BindingChord(Key Key, bool Alt = false, bool Control = false,
    bool Shift = false)
{
    public bool IsBound => Key != Key.Unknown;
}

public static class BindingChordLaw
{
    public static bool IsModifier(Key key) => key is Key.AltLeft or Key.AltRight or
        Key.ControlLeft or Key.ControlRight or Key.ShiftLeft or Key.ShiftRight or
        Key.SuperLeft or Key.SuperRight;

    public static BindingChord Live(Key key, bool alt, bool control, bool shift) =>
        new(key, alt, control, shift);

    /// <summary>The one reference retry: drop the emitted chord's leftmost modifier.</summary>
    public static BindingChord? Fallback(in BindingChord chord)
    {
        if (chord.Alt) return chord with { Alt = false };
        if (chord.Control) return chord with { Control = false };
        if (chord.Shift) return chord with { Shift = false };
        return null;
    }

    public static string Canonical(in BindingChord chord)
    {
        if (!chord.IsBound) return "";
        string prefix = chord.Alt ? "ALT-" : "";
        if (chord.Control) prefix += "CTRL-";
        if (chord.Shift) prefix += "SHIFT-";
        return prefix + KeyToken(chord.Key);
    }

    public static bool TryParse(string? text, out BindingChord chord)
    {
        chord = default;
        if (string.IsNullOrWhiteSpace(text) ||
            text.Equals(nameof(Key.Unknown), StringComparison.OrdinalIgnoreCase))
            return true;
        string rest = text.Trim();
        bool alt = false, control = false, shift = false;
        bool consumed;
        do
        {
            consumed = false;
            if (rest.StartsWith("ALT-", StringComparison.OrdinalIgnoreCase))
            {
                alt = true;
                rest = rest[4..];
                consumed = true;
            }
            else if (rest.StartsWith("CTRL-", StringComparison.OrdinalIgnoreCase))
            {
                control = true;
                rest = rest[5..];
                consumed = true;
            }
            else if (rest.StartsWith("SHIFT-", StringComparison.OrdinalIgnoreCase))
            {
                shift = true;
                rest = rest[6..];
                consumed = true;
            }
        } while (consumed);

        if (!TryTokenKey(rest, out Key key) &&
            !Enum.TryParse(rest, ignoreCase: true, out key)) return false;
        if (key == Key.Unknown || IsModifier(key)) return false;
        chord = new(key, alt, control, shift);
        return true;
    }

    public static string Display(in BindingChord chord, Func<Key, string> baseLabel)
    {
        ArgumentNullException.ThrowIfNull(baseLabel);
        if (!chord.IsBound) return "Not Bound";
        string prefix = chord.Alt ? "ALT-" : "";
        if (chord.Control) prefix += "CTRL-";
        if (chord.Shift) prefix += "SHIFT-";
        return prefix + baseLabel(chord.Key);
    }

    private static string KeyToken(Key key)
    {
        if (key is >= Key.A and <= Key.Z) return key.ToString().ToUpperInvariant();
        return key switch
        {
            Key.Number0 => "0", Key.Number1 => "1", Key.Number2 => "2",
            Key.Number3 => "3", Key.Number4 => "4", Key.Number5 => "5",
            Key.Number6 => "6", Key.Number7 => "7", Key.Number8 => "8",
            Key.Number9 => "9", Key.Space => "SPACE", Key.Tab => "TAB",
            Key.Enter or Key.KeypadEnter => "ENTER", Key.Escape => "ESCAPE",
            Key.Backspace => "BACKSPACE", Key.Insert => "INSERT", Key.Delete => "DELETE",
            Key.Home => "HOME", Key.End => "END", Key.PageUp => "PAGEUP",
            Key.PageDown => "PAGEDOWN", Key.Up => "UP", Key.Down => "DOWN",
            Key.Left => "LEFT", Key.Right => "RIGHT", Key.Keypad0 => "NUMPAD0",
            Key.Keypad1 => "NUMPAD1", Key.Keypad2 => "NUMPAD2", Key.Keypad3 => "NUMPAD3",
            Key.Keypad4 => "NUMPAD4", Key.Keypad5 => "NUMPAD5", Key.Keypad6 => "NUMPAD6",
            Key.Keypad7 => "NUMPAD7", Key.Keypad8 => "NUMPAD8", Key.Keypad9 => "NUMPAD9",
            Key.KeypadAdd => "NUMPADPLUS", Key.KeypadSubtract => "NUMPADMINUS",
            Key.KeypadDivide => "NUMPADDIVIDE", Key.KeypadMultiply => "NUMPADMULTIPLY",
            Key.KeypadDecimal => "NUMPADDECIMAL", Key.NumLock => "NUMLOCK",
            Key.PrintScreen => "PRINTSCREEN", Key.CapsLock => "CAPSLOCK",
            Key.Minus => "-", Key.Equal => "=", Key.LeftBracket => "[",
            Key.RightBracket => "]", Key.BackSlash => "\\", Key.Semicolon => ";",
            Key.Apostrophe => "'", Key.Comma => ",", Key.Period => ".",
            Key.Slash => "/", Key.GraveAccent => "`", _ => key.ToString(),
        };
    }

    private static bool TryTokenKey(string token, out Key key)
    {
        key = token.ToUpperInvariant() switch
        {
            "0" => Key.Number0, "1" => Key.Number1, "2" => Key.Number2,
            "3" => Key.Number3, "4" => Key.Number4, "5" => Key.Number5,
            "6" => Key.Number6, "7" => Key.Number7, "8" => Key.Number8,
            "9" => Key.Number9, "SPACE" => Key.Space, "TAB" => Key.Tab,
            "ENTER" => Key.Enter, "ESCAPE" => Key.Escape, "BACKSPACE" => Key.Backspace,
            "INSERT" => Key.Insert, "DELETE" => Key.Delete, "HOME" => Key.Home,
            "END" => Key.End, "PAGEUP" => Key.PageUp, "PAGEDOWN" => Key.PageDown,
            "UP" => Key.Up, "DOWN" => Key.Down, "LEFT" => Key.Left, "RIGHT" => Key.Right,
            "NUMPAD0" => Key.Keypad0, "NUMPAD1" => Key.Keypad1,
            "NUMPAD2" => Key.Keypad2, "NUMPAD3" => Key.Keypad3,
            "NUMPAD4" => Key.Keypad4, "NUMPAD5" => Key.Keypad5,
            "NUMPAD6" => Key.Keypad6, "NUMPAD7" => Key.Keypad7,
            "NUMPAD8" => Key.Keypad8, "NUMPAD9" => Key.Keypad9,
            "NUMPADPLUS" => Key.KeypadAdd, "NUMPADMINUS" => Key.KeypadSubtract,
            "NUMPADDIVIDE" => Key.KeypadDivide, "NUMPADMULTIPLY" => Key.KeypadMultiply,
            "NUMPADDECIMAL" => Key.KeypadDecimal, "NUMLOCK" => Key.NumLock,
            "PRINTSCREEN" => Key.PrintScreen, "CAPSLOCK" => Key.CapsLock,
            "-" => Key.Minus, "=" => Key.Equal, "[" => Key.LeftBracket,
            "]" => Key.RightBracket, "\\" => Key.BackSlash, ";" => Key.Semicolon,
            "'" => Key.Apostrophe, "," => Key.Comma, "." => Key.Period,
            "/" => Key.Slash, "`" => Key.GraveAccent, _ => Key.Unknown,
        };
        if (key != Key.Unknown) return true;
        if (token.Length == 1 && token[0] is >= 'A' and <= 'Z')
            return Enum.TryParse(token, ignoreCase: true, out key);
        return false;
    }
}
