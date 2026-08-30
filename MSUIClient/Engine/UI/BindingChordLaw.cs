using Silk.NET.Input;

namespace MSUIClient.Engine.UI;

public enum BindingPointerKey
{
    None,
    Button3,
    Button4,
    Button5,
    WheelUp,
    WheelDown,
    // The left and right buttons are LAST because they are not ordinary command inputs.
    // Vanilla names them BUTTON1/BUTTON2 and MSUI's RTS/CRPG gestures are authored on them,
    // but they are deliberately kept OUT of the global latch scan in GameLoop.Bindings.cs -
    // they are camera look and ImGui's own click source. They resolve only against a captured
    // WorldMouseClick, through RtsBindingLaw. See BindingClaimsClick.
    Button1,
    Button2,
}

/// <summary>One canonical 1.12 keyboard or mouse chord. Super/Cmd is not representable.</summary>
public readonly record struct BindingChord(Key Key, bool Alt = false, bool Control = false,
    bool Shift = false, BindingPointerKey Pointer = BindingPointerKey.None)
{
    /// <summary>
    /// Bound means NAMES A REAL INPUT - not merely "is not Key.Unknown".
    ///
    /// Silk.NET's Key.Unknown is -1 and the enum names nothing at 0, so default(Key) is a third
    /// thing: neither Unknown nor a key. The old test was `Key != Key.Unknown`, which called
    /// that third thing BOUND - and default(BindingChord) is exactly what every unbind path
    /// produces: BindingPair.Without after a key is taken by another command, the Unbind
    /// button's With(slot, default), and the second slot of every row ResetBindingsToDefaults
    /// seeds. So "unbound" was a chord on a phantom key, which:
    ///   - drew as "0" instead of "Not Bound" (FriendlyKey falls through to key.ToString(), and
    ///     ((Key)0).ToString() is "0" because the enum has no name for it);
    ///   - canonicalised to "0" and was SAVED that way, and "0" is the token for the number-zero
    ///     key - so one save/load round trip bound 119 of the reporter's 125 commands to 0;
    ///   - made "&lt;command&gt; Function is Now Unbound!" unreachable, because the cleared pair
    ///     still answered IsBound, so nobody was told their key had been taken.
    /// Reported 2026-08-26: rebinding the inventory key showed the displaced command sitting on
    /// "0", and the client had to be restarted.
    /// </summary>
    /// <remarks>
    /// A chord carrying ONLY modifiers is also bound: MSUI's RTS/CRPG grammar has genuine
    /// held-modifier commands ("which modifier casts a card ability on the primary"), whose
    /// base input is whatever key the ability itself is bound to. default(BindingChord) is
    /// still unbound - every modifier flag defaults to false - so the repair above is intact.
    /// </remarks>
    public bool IsBound => Pointer != BindingPointerKey.None ||
        (Key != Key.Unknown && Key != default) || Alt || Control || Shift;
}

public static class BindingChordLaw
{
    public static bool IsModifier(Key key) => key is Key.AltLeft or Key.AltRight or
        Key.ControlLeft or Key.ControlRight or Key.ShiftLeft or Key.ShiftRight or
        Key.SuperLeft or Key.SuperRight;

    public static BindingChord Live(Key key, bool alt, bool control, bool shift) =>
        new(key == Key.KeypadEnter ? Key.Enter : key, alt, control, shift);

    public static BindingChord LivePointer(BindingPointerKey pointer,
        bool alt, bool control, bool shift) =>
        new(Key.Unknown, alt, control, shift, pointer);

    /// <summary>The one reference retry: drop the emitted chord's leftmost modifier.</summary>
    public static BindingChord? Fallback(in BindingChord chord)
    {
        if (chord.Alt) return chord with { Alt = false };
        if (chord.Control) return chord with { Control = false };
        if (chord.Shift) return chord with { Shift = false };
        return null;
    }

    /// <summary>The token a number-zero binding writes - and the token the pre-fix writer also
    /// emitted for UNBOUND. See <see cref="HasZeroKeyPoison"/>.</summary>
    private const string ZeroKeyToken = "0";

    /// <summary>
    /// Was this file written while default(BindingChord) still read as bound?
    ///
    /// Such a writer canonicalised every unbound slot as "0" - the number-zero key's own token -
    /// so the next load bound the whole command table to 0 at once. The tell is a command with
    /// "0" in BOTH slots, which the editor cannot produce: binding a chord first strips it from
    /// every previous owner, the same command's other slot included, so a command's two slots are
    /// never the same chord. Anything the fixed writer emits carries "" for unbound instead and
    /// is left alone. Reported 2026-08-26.
    /// </summary>
    public static bool HasZeroKeyPoison(IEnumerable<string[]> savedSlots)
    {
        ArgumentNullException.ThrowIfNull(savedSlots);
        return savedSlots.Any(keys => keys.Length > 1 &&
            keys[0] == ZeroKeyToken && keys[1] == ZeroKeyToken);
    }

    /// <summary>
    /// In a poisoned file, is THIS slot the writer's "unbound" rather than a real zero binding?
    ///
    /// A secondary "0" always is - nothing seeds a secondary zero. A primary "0" only is when the
    /// secondary is "0" too, which is the impossible pair above; a lone primary "0" beside a real
    /// secondary is a binding the player chose, and is kept. Poisoned slots fall back to the
    /// DEFAULT rather than to unbound, so Action Button 10 keeps the 0 key it ships with.
    /// </summary>
    public static bool IsZeroKeyPoison(string[] savedKeys, int slotIndex)
    {
        ArgumentNullException.ThrowIfNull(savedKeys);
        if (slotIndex < 0 || slotIndex >= savedKeys.Length ||
            savedKeys[slotIndex] != ZeroKeyToken) return false;
        return slotIndex > 0 || (savedKeys.Length > 1 && savedKeys[1] == ZeroKeyToken);
    }
    /// <summary>A chord with modifiers and no base input - the held-modifier commands.</summary>
    public static bool IsModifierOnly(in BindingChord chord) =>
        chord.Pointer == BindingPointerKey.None &&
        (chord.Key == Key.Unknown || chord.Key == default) &&
        (chord.Alt || chord.Control || chord.Shift);

    /// <summary>ALT / CTRL-SHIFT / ... - the prefix ladder standing alone, so it round-trips
    /// through the very same prefix loop <see cref="TryParse"/> already runs.</summary>
    private static string ModifierToken(in BindingChord chord)
    {
        var parts = new List<string>(3);
        if (chord.Alt) parts.Add("ALT");
        if (chord.Control) parts.Add("CTRL");
        if (chord.Shift) parts.Add("SHIFT");
        return string.Join('-', parts);
    }

    public static string Canonical(in BindingChord chord)
    {
        if (!chord.IsBound) return "";
        if (IsModifierOnly(chord)) return ModifierToken(chord);
        string prefix = chord.Alt ? "ALT-" : "";
        if (chord.Control) prefix += "CTRL-";
        if (chord.Shift) prefix += "SHIFT-";
        return prefix + (chord.Pointer == BindingPointerKey.None
            ? KeyToken(chord.Key) : PointerToken(chord.Pointer));
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

        if (TryTokenPointer(rest, out BindingPointerKey pointer))
        {
            chord = LivePointer(pointer, alt, control, shift);
            return true;
        }
        // A bare modifier ladder: the loop above ate "ALT-" and left "CTRL", or ate everything
        // and left "". Either way this is a held-modifier command, not a malformed key.
        if (TryTokenModifier(rest, ref alt, ref control, ref shift))
        {
            chord = new(Key.Unknown, alt, control, shift);
            return chord.IsBound;
        }
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
        if (IsModifierOnly(chord)) return ModifierToken(chord);
        string prefix = chord.Alt ? "ALT-" : "";
        if (chord.Control) prefix += "CTRL-";
        if (chord.Shift) prefix += "SHIFT-";
        return prefix + (chord.Pointer == BindingPointerKey.None
            ? baseLabel(chord.Key) : PointerLabel(chord.Pointer));
    }

    public static string PointerToken(BindingPointerKey pointer) => pointer switch
    {
        BindingPointerKey.Button1 => "BUTTON1",
        BindingPointerKey.Button2 => "BUTTON2",
        BindingPointerKey.Button3 => "BUTTON3",
        BindingPointerKey.Button4 => "BUTTON4",
        BindingPointerKey.Button5 => "BUTTON5",
        BindingPointerKey.WheelUp => "MOUSEWHEELUP",
        BindingPointerKey.WheelDown => "MOUSEWHEELDOWN",
        _ => "",
    };

    public static string PointerLabel(BindingPointerKey pointer) => pointer switch
    {
        BindingPointerKey.Button1 => "Left Mouse",
        BindingPointerKey.Button2 => "Right Mouse",
        BindingPointerKey.Button3 => "Middle Mouse",
        BindingPointerKey.Button4 => "Mouse Button 4",
        BindingPointerKey.Button5 => "Mouse Button 5",
        BindingPointerKey.WheelUp => "Mouse Wheel Up",
        BindingPointerKey.WheelDown => "Mouse Wheel Down",
        _ => "",
    };

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

    private static bool TryTokenModifier(string token, ref bool alt, ref bool control,
        ref bool shift)
    {
        switch (token.ToUpperInvariant())
        {
            case "": return alt || control || shift;
            case "ALT": alt = true; return true;
            case "CTRL": control = true; return true;
            case "SHIFT": shift = true; return true;
            default: return false;
        }
    }

    private static bool TryTokenPointer(string token, out BindingPointerKey pointer)
    {
        pointer = token.ToUpperInvariant() switch
        {
            "BUTTON1" => BindingPointerKey.Button1,
            "BUTTON2" => BindingPointerKey.Button2,
            "BUTTON3" => BindingPointerKey.Button3,
            "BUTTON4" => BindingPointerKey.Button4,
            "BUTTON5" => BindingPointerKey.Button5,
            "MOUSEWHEELUP" => BindingPointerKey.WheelUp,
            "MOUSEWHEELDOWN" => BindingPointerKey.WheelDown,
            _ => BindingPointerKey.None,
        };
        return pointer != BindingPointerKey.None;
    }
}

/// <summary>Pure host-command laws shared by the keybinding dispatcher and movement path.</summary>
public static class BindingCommandLaw
{
    public static int ForwardAxis(bool forward, bool backward, bool bothButtons, bool autorun) =>
        (forward ? 1 : 0) + (bothButtons ? 1 : 0) + (autorun ? 1 : 0) -
        (backward ? 1 : 0);

    public static bool AutorunCancelled(bool forwardStarted, bool backwardStarted,
        bool bothButtonsEngaged, bool lostMover) =>
        forwardStarted || backwardStarted || bothButtonsEngaged || lostMover;

    public static float StepMasterVolume(float current, int direction) =>
        Math.Clamp(current + Math.Sign(direction) * .1f, 0f, 1f);
}
