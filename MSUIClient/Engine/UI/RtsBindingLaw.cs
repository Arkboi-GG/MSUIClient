using Silk.NET.Input;

namespace MSUIClient.Engine.UI;

/// <summary>What a Key Bindings row is allowed to be bound to.</summary>
public enum BindingInputKind
{
    /// <summary>The ordinary command: any key or Button3/4/5/wheel chord. Every shipped
    /// vanilla row is this, and it deliberately REFUSES the left and right mouse buttons -
    /// those never enter the global latch, so binding one here would be a dead key.</summary>
    Any,

    /// <summary>A world-click gesture. Only a pointer chord can express it, because the
    /// command is "this button, on this thing, under these modifiers" - there is no
    /// keyboard equivalent for "the unit I clicked".</summary>
    Pointer,

    /// <summary>A held modifier that changes what some OTHER binding's press means. Only a
    /// bare modifier ladder can express it; there is no base input of its own.</summary>
    Modifier,
}

/// <summary>
/// Pure grammar for MSUI's CRPG/RTS bindings: which chords a row may hold, and whether a
/// captured world click or the live modifier state satisfies one.
///
/// The left and right mouse buttons resolve HERE and nowhere else. They are deliberately
/// absent from the global latch scan in GameLoop.Bindings.cs, because those buttons are also
/// camera look and ImGui's own click source: routing them through the shared resolver - which
/// carries a leftmost-modifier fallback - would let a stray binding fire on every click in the
/// world and in every panel. A gesture is matched against the modifier state CAPTURED WITH THE
/// CLICK instead of the live keyboard, which is what the queued-click drain needs anyway (a
/// release is delivered a frame or more after the press that classified it).
/// </summary>
public static class RtsBindingLaw
{
    public static BindingPointerKey PointerFor(MouseButton button) => button switch
    {
        MouseButton.Left => BindingPointerKey.Button1,
        MouseButton.Right => BindingPointerKey.Button2,
        MouseButton.Middle => BindingPointerKey.Button3,
        _ => BindingPointerKey.None,
    };

    /// <summary>The world-click buttons - the two this law exists to keep out of the latch.</summary>
    public static bool IsWorldClickButton(BindingPointerKey pointer) =>
        pointer is BindingPointerKey.Button1 or BindingPointerKey.Button2;

    /// <summary>
    /// Does this chord name exactly this click? EXACT, with no leftmost-modifier fallback:
    /// the gesture set is dense (plain, Shift and Alt all mean different things on the same
    /// button), so a fallback would let Alt+Shift+click silently become a plain Shift+click
    /// order. An unrecognised combination doing nothing is the safer read for a command that
    /// moves a squad.
    /// </summary>
    public static bool ClaimsPointer(in BindingChord chord, BindingPointerKey pointer,
        bool alt, bool control, bool shift) =>
        chord.IsBound && pointer != BindingPointerKey.None && chord.Pointer == pointer &&
        chord.Alt == alt && chord.Control == control && chord.Shift == shift;

    /// <summary>
    /// Is the bound modifier ladder currently held? NOT exclusive: every modifier the chord
    /// names must be down, and any extra one is ignored. That is what the hard-coded
    /// <c>AltHeld()</c> it replaces did, and the base binding underneath may carry modifiers
    /// of its own.
    /// </summary>
    public static bool ModifierHeld(in BindingChord chord, bool alt, bool control, bool shift) =>
        BindingChordLaw.IsModifierOnly(chord) &&
        (!chord.Alt || alt) && (!chord.Control || control) && (!chord.Shift || shift);

    /// <summary>May a row of this kind hold this chord? The Key Bindings capture asks before
    /// it commits, so a player can never seat a chord the command cannot read.</summary>
    public static bool Accepts(BindingInputKind kind, in BindingChord chord) => kind switch
    {
        BindingInputKind.Pointer => chord.Pointer != BindingPointerKey.None,
        BindingInputKind.Modifier => BindingChordLaw.IsModifierOnly(chord),
        _ => !BindingChordLaw.IsModifierOnly(chord) && !IsWorldClickButton(chord.Pointer),
    };

    /// <summary>The one-line refusal shown in the frame's feedback line.</summary>
    public static string RejectionFor(BindingInputKind kind) => kind switch
    {
        BindingInputKind.Pointer => "This Command Needs a Mouse Button",
        BindingInputKind.Modifier => "This Command Needs a Modifier Key",
        _ => "Left and Right Mouse Are Reserved",
    };
}
