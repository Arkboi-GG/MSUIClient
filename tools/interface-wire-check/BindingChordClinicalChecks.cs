using MSUIClient;
using MSUIClient.Engine.UI;
using Silk.NET.Input;

internal static class BindingChordClinicalChecks
{
    public static void Run()
    {
        CheckCodecAndFallback();
        CheckUnboundIsNotTheZeroKey();
        CheckCrpgRtsChordExtensions();
        CheckZeroKeyPoisonRepair();
        CheckRuntimeSourceFence();
    }

    private static void CheckCodecAndFallback()
    {
        var full = new BindingChord(Key.F1, Alt: true, Control: true, Shift: true);
        Check(BindingChordLaw.Canonical(full) == "ALT-CTRL-SHIFT-F1" &&
              BindingChordLaw.TryParse("ALT-CTRL-SHIFT-F1", out BindingChord parsed) &&
              parsed == full,
            "binding canonical prefix order or round trip drifted");
        Check(BindingChordLaw.TryParse("CTRL--", out BindingChord minus) &&
              minus == new BindingChord(Key.Minus, Control: true) &&
              BindingChordLaw.Canonical(minus) == "CTRL--",
            "Ctrl-minus punctuation chord drifted");
        Check(BindingChordLaw.TryParse("Number1", out BindingChord legacy) &&
              legacy == new BindingChord(Key.Number1) &&
              BindingChordLaw.Canonical(legacy) == "1",
            "legacy enum-name binding migration drifted");
        Check(BindingChordLaw.Fallback(full) ==
                  new BindingChord(Key.F1, Control: true, Shift: true) &&
              BindingChordLaw.Fallback(new BindingChord(Key.W, Control: true, Shift: true)) ==
                  new BindingChord(Key.W, Shift: true) &&
              BindingChordLaw.Fallback(new BindingChord(Key.W, Shift: true)) ==
                  new BindingChord(Key.W) &&
              BindingChordLaw.Fallback(new BindingChord(Key.W)) is null,
            "binding one-step leftmost-modifier fallback drifted");
        Check(BindingChordLaw.IsModifier(Key.AltLeft) &&
              BindingChordLaw.IsModifier(Key.ControlRight) &&
              BindingChordLaw.IsModifier(Key.ShiftLeft) &&
              BindingChordLaw.IsModifier(Key.SuperRight) &&
              !BindingChordLaw.IsModifier(Key.Z) &&
              !BindingChordLaw.TryParse("SUPER-Z", out _),
            "modifier-key/Super exclusion drifted");
        Check(BindingChordLaw.Display(new BindingChord(Key.Z, Alt: true),
                  key => key.ToString()) == "ALT-Z",
            "binding display chord drifted");
        Check(BindingChordLaw.Live(Key.KeypadEnter, false, false, false) ==
                  new BindingChord(Key.Enter) &&
              BindingCommandLaw.ForwardAxis(false, false, false, true) == 1 &&
              BindingCommandLaw.ForwardAxis(true, false, false, true) == 2 &&
              BindingCommandLaw.ForwardAxis(false, true, false, true) == 0 &&
              BindingCommandLaw.AutorunCancelled(true, false, false, false) &&
              !BindingCommandLaw.AutorunCancelled(false, false, false, false),
            "binding keypad-enter normalization or autorun axis/cancel law drifted");
        BindingChord mouse = BindingChordLaw.LivePointer(BindingPointerKey.Button4,
            alt: false, control: true, shift: true);
        Check(BindingChordLaw.Canonical(mouse) == "CTRL-SHIFT-BUTTON4" &&
              BindingChordLaw.TryParse("CTRL-SHIFT-BUTTON4", out BindingChord parsedMouse) &&
              parsedMouse == mouse &&
              BindingChordLaw.Display(mouse, key => key.ToString()) ==
                  "CTRL-SHIFT-Mouse Button 4" &&
              BindingChordLaw.TryParse("MOUSEWHEELUP", out BindingChord wheel) &&
              wheel.Pointer == BindingPointerKey.WheelUp && wheel.Key == Key.Unknown,
            "mouse button/wheel canonical codec or display drifted");
        Check(KeyBindingsUiLaw.FrameSize == new System.Numerics.Vector2(640, 512) &&
              // Re-pinned 2026-08-26 to the MEASURED layout. These previously enshrined the
              // broken values - x=0 (flush left, not TOP-anchored/centred), a search box at
              // y=8 sitting on the frame's own top border (solid to y=52), and a 17-row band
              // that left no interior room for MSUI's extra chrome. A guard that asserts the
              // defect is worse than no guard: it is why none of this was caught.
              KeyBindingsUiLaw.WindowOrigin(1600f) ==
                  new System.Numerics.Vector2(480, 100) &&
              KeyBindingsUiLaw.Search == new KeyBindingsUiLaw.Rect(26, 58, 180, 22) &&
              KeyBindingsUiLaw.SearchPlaceholderOffset ==
                  new System.Numerics.Vector2(7, 5) &&
              KeyBindingsUiLaw.Rows == new KeyBindingsUiLaw.Rect(27, 104, 535, 345) &&
              KeyBindingsUiLaw.VisibleRows == 15 && KeyBindingsUiLaw.RowPitch == 23 &&
              KeyBindingsUiLaw.TitleFont == "GameFontNormal" &&
              KeyBindingsUiLaw.CategoryFont == "GameFontNormal" &&
              KeyBindingsUiLaw.CommandFont == "GameFontNormalSmall" &&
              KeyBindingsUiLaw.KeyNormalFont == "GameFontHighlightSmall" &&
              KeyBindingsUiLaw.HeaderGlyph ==
                  new KeyBindingsUiLaw.Rect(2, 3.5f, 16, 16) &&
              KeyBindingsUiLaw.HeaderTextOffset == new System.Numerics.Vector2(24, 5.5f) &&
              KeyBindingsUiLaw.RowMinimum(3) ==
                  new System.Numerics.Vector2(27, 104 + 3 * 23) &&
              KeyBindingsUiLaw.RowHitSize == new System.Numerics.Vector2(535, 23) &&
              KeyBindingsUiLaw.PrimaryKey == new KeyBindingsUiLaw.Rect(175, 1, 180, 22) &&
              KeyBindingsUiLaw.SecondaryKey == new KeyBindingsUiLaw.Rect(355, 1, 180, 22) &&
              // 20 rows less the 15 that fit. These still asserted 3 - the 17-row band's
              // answer - after VisibleRows was re-pinned to 15, so this check threw on a clean
              // tree and nobody saw it: it only runs behind --binding-chord-only.
              KeyBindingsUiLaw.MaximumScroll(20) == 5 &&
              KeyBindingsUiLaw.ClampScroll(9, 20) == 5 &&
              KeyBindingsUiLaw.MatchesSearch("Action Bar", "Action Button 1", "button") &&
              !KeyBindingsUiLaw.MatchesSearch("Movement", "Jump", "spell"),
            "keybindings shell/search/collapse geometry law drifted");
        // The scroll bar sits in the art's carved slot exactly where KeyBindingFrame.xml puts
        // it: the 560x390 faux scroll frame at (2,-53) hangs the bar at TOPRIGHT +6, so the up
        // button is flush at y 53 and the down button ends at 443 (2026-09-03).
        Check(KeyBindingsUiLaw.ScrollMinimum == new System.Numerics.Vector2(568, 53) &&
              KeyBindingsUiLaw.ScrollHeight == 390f,
            "keybindings scroll bar left its FrameXML slot");
        // Widening: clamped to the screen and a ceiling, every extra pixel to the command column.
        Check(KeyBindingsUiLaw.ClampExtraWidth(200f, 1600f) == 200f &&
              KeyBindingsUiLaw.ClampExtraWidth(-5f, 1600f) == 0f &&
              KeyBindingsUiLaw.ClampExtraWidth(float.NaN, 1600f) == 0f &&
              KeyBindingsUiLaw.ClampExtraWidth(900f, 1600f) == 640f &&
              KeyBindingsUiLaw.ClampExtraWidth(300f, 700f) == 60f &&
              KeyBindingsUiLaw.FrameSizeWith(120f) == new System.Numerics.Vector2(760, 512) &&
              KeyBindingsUiLaw.WindowOrigin(1600f, float.PositiveInfinity, 120f) ==
                  new System.Numerics.Vector2(420, 100) &&
              KeyBindingsUiLaw.CommandColumnWidth(120f) == 291f &&
              KeyBindingsUiLaw.StretchTop(120f) == new KeyBindingsUiLaw.Rect(512, 0, 120, 256) &&
              KeyBindingsUiLaw.ResizeGrip(0f).X + KeyBindingsUiLaw.ResizeGrip(0f).Width ==
                  KeyBindingsUiLaw.VisibleRightEdge &&
              KeyBindingsUiLaw.ArtIsRightAnchored(KeyBindingsUiLaw.Art[2]) &&
              !KeyBindingsUiLaw.ArtIsRightAnchored(KeyBindingsUiLaw.Art[1]),
            "keybindings widening law drifted");
    }

    /// <summary>
    /// UNBOUND MUST NOT BE THE ZERO KEY. Silk.NET puts Key.Unknown at -1 and names nothing at 0,
    /// so default(BindingChord) - what BindingPair.Without, the Unbind button and every seeded
    /// second slot produce - carries a key the enum cannot name. While IsBound only asked
    /// "Key != Key.Unknown" that chord read as BOUND: it drew as "0", saved as "0", and came back
    /// from disk as the real number-zero key, collecting 119 of 125 commands onto one press.
    /// Reported 2026-08-26. These assert the behaviour, not the defect.
    /// </summary>
    private static void CheckUnboundIsNotTheZeroKey()
    {
        BindingChord unbound = default;
        Check(!unbound.IsBound && !new BindingChord(Key.Unknown).IsBound,
            "default/Key.Unknown chord reads as bound again - unbound is a phantom key");
        // A phantom key CARRYING MODIFIERS is a different thing and is deliberately bound: it
        // is MSUI's held-modifier command (RTS Controls / Cast Card Ability on Primary), whose
        // base input is whatever the ability itself is bound to. What the repair above actually
        // protects - that unbound canonicalises to "" and never to the zero key - is unchanged,
        // and is asserted below and again in the modifier round trip.
        Check(new BindingChord(Key.Unknown, Alt: true, Control: true, Shift: true).IsBound,
            "a bare modifier ladder stopped being bindable - the self-cast modifier is dead");
        Check(BindingChordLaw.Canonical(unbound).Length == 0 &&
              BindingChordLaw.Canonical(new BindingChord(Key.Unknown)).Length == 0,
            "an unbound slot canonicalises to a key token again - saving poisons the file");
        Check(BindingChordLaw.Display(unbound, key => key.ToString()) == "Not Bound",
            "an unbound slot no longer displays Not Bound");
        Check(BindingChordLaw.TryParse(BindingChordLaw.Canonical(unbound),
                  out BindingChord reloaded) && !reloaded.IsBound,
            "an unbound slot does not survive a save/load round trip");
        Check(reloadedIsNotZero(), "unbound and a real press of the 0 key are the same chord");

        // ...and the number-zero key itself still works, which is the whole reason the two were
        // confusable: Action Button 10 ships on it.
        BindingChord zero = BindingChordLaw.Live(Key.Number0, false, false, false);
        Check(zero.IsBound && BindingChordLaw.Canonical(zero) == "0" &&
              BindingChordLaw.TryParse("0", out BindingChord parsedZero) && parsedZero == zero,
            "the number-zero key stopped round-tripping");

        static bool reloadedIsNotZero()
        {
            BindingChordLaw.TryParse(BindingChordLaw.Canonical(default), out BindingChord slot);
            return slot != BindingChordLaw.Live(Key.Number0, false, false, false);
        }
    }

    /// <summary>
    /// MSUI's two chord extensions: the vanilla BUTTON1/BUTTON2 tokens, which carry the free
    /// view's world-click gestures, and the bare modifier ladder behind the self-cast command.
    /// Both have to survive a save/load round trip, and neither may be confusable with unbound.
    /// </summary>
    private static void CheckCrpgRtsChordExtensions()
    {
        BindingChord gesture = BindingChordLaw.LivePointer(BindingPointerKey.Button1,
            true, false, false);
        Check(BindingChordLaw.Canonical(gesture) == "ALT-BUTTON1" &&
              BindingChordLaw.TryParse("ALT-BUTTON1", out BindingChord parsedGesture) &&
              parsedGesture == gesture &&
              BindingChordLaw.Display(gesture, key => key.ToString()) == "ALT-Left Mouse",
            "Alt+Left Mouse stopped round-tripping - the take-control gesture cannot be saved");
        Check(BindingChordLaw.TryParse("SHIFT-BUTTON2", out BindingChord queue) &&
              queue.Pointer == BindingPointerKey.Button2 && queue.Shift && queue.Key == Key.Unknown,
            "Shift+Right Mouse stopped round-tripping - chain waypoints cannot be saved");
        Check(RtsBindingLaw.IsWorldClickButton(BindingPointerKey.Button1) &&
              RtsBindingLaw.IsWorldClickButton(BindingPointerKey.Button2) &&
              !RtsBindingLaw.IsWorldClickButton(BindingPointerKey.Button3),
            "the world-click button set drifted");

        BindingChord modifier = new(Key.Unknown, Alt: true);
        Check(modifier.IsBound && BindingChordLaw.IsModifierOnly(modifier) &&
              BindingChordLaw.Canonical(modifier) == "ALT" &&
              BindingChordLaw.TryParse("ALT", out BindingChord parsedModifier) &&
              parsedModifier == modifier,
            "the bare Alt modifier stopped round-tripping");
        Check(BindingChordLaw.TryParse("CTRL-SHIFT", out BindingChord ladder) &&
              BindingChordLaw.IsModifierOnly(ladder) && ladder.Control && ladder.Shift &&
              !ladder.Alt && BindingChordLaw.Canonical(ladder) == "CTRL-SHIFT",
            "a two-modifier ladder stopped round-tripping");
        Check(!BindingChordLaw.IsModifierOnly(default) &&
              !BindingChordLaw.IsModifierOnly(BindingChordLaw.Live(Key.Z, true, false, false)),
            "unbound or an ordinary Alt+key chord is being read as a bare modifier");

        // Capture may only seat a chord the command can READ, or the row is decorative.
        Check(RtsBindingLaw.Accepts(BindingInputKind.Pointer, gesture) &&
              !RtsBindingLaw.Accepts(BindingInputKind.Pointer, modifier) &&
              RtsBindingLaw.Accepts(BindingInputKind.Modifier, modifier) &&
              !RtsBindingLaw.Accepts(BindingInputKind.Modifier, gesture) &&
              !RtsBindingLaw.Accepts(BindingInputKind.Any, gesture) &&
              !RtsBindingLaw.Accepts(BindingInputKind.Any, modifier) &&
              RtsBindingLaw.Accepts(BindingInputKind.Any,
                  BindingChordLaw.Live(Key.Z, false, true, false)),
            "a Key Bindings row can seat a chord its command cannot read");

        // Gesture matching is EXACT: a stray extra modifier must not fall back onto a
        // different order. Alt+Shift+Left is neither take-control nor add-to-selection.
        Check(RtsBindingLaw.ClaimsPointer(gesture, BindingPointerKey.Button1,
                  true, false, false) &&
              !RtsBindingLaw.ClaimsPointer(gesture, BindingPointerKey.Button1,
                  true, false, true) &&
              !RtsBindingLaw.ClaimsPointer(gesture, BindingPointerKey.Button2,
                  true, false, false),
            "world-click gesture matching stopped being exact");

        // Held modifiers are NOT exclusive: the binding underneath may carry its own.
        Check(RtsBindingLaw.ModifierHeld(modifier, true, true, false) &&
              !RtsBindingLaw.ModifierHeld(modifier, false, true, true) &&
              !RtsBindingLaw.ModifierHeld(gesture, true, false, false),
            "held-modifier matching drifted");
    }

    /// <summary>
    /// The one-time repair for files ALREADY written with "0" for unbound. The marker is a
    /// command with "0" in both slots, which the editor cannot produce.
    /// </summary>
    private static void CheckZeroKeyPoisonRepair()
    {
        string[][] poisoned = [["B", "0"], ["0", "0"], ["W", "UP"]];
        string[][] clean = [["B", ""], ["0", ""], ["W", "UP"]];
        Check(BindingChordLaw.HasZeroKeyPoison(poisoned) &&
              !BindingChordLaw.HasZeroKeyPoison(clean),
            "zero-key poison detection drifted");
        Check(BindingChordLaw.IsZeroKeyPoison(["B", "0"], 1) &&
              BindingChordLaw.IsZeroKeyPoison(["0", "0"], 0) &&
              BindingChordLaw.IsZeroKeyPoison(["0", "0"], 1) &&
              !BindingChordLaw.IsZeroKeyPoison(["B", "0"], 0) &&
              !BindingChordLaw.IsZeroKeyPoison(["0", "NUMPAD0"], 0) &&
              !BindingChordLaw.IsZeroKeyPoison(["W", "UP"], 1) &&
              !BindingChordLaw.IsZeroKeyPoison(["B"], 1),
            "zero-key poison slot selection drifted - a real 0 binding is being dropped, " +
            "or a poisoned slot is being kept");
    }
    private static void CheckRuntimeSourceFence()
    {
        string root = ClientConfig.FindRepoRoot();
        string bindings = File.ReadAllText(Path.Combine(root, "MSUIClient", "GameLoop",
            "Panels", "GameLoop.Bindings.cs"));
        string page = File.ReadAllText(Path.Combine(root, "MSUIClient", "GameLoop",
            "Panels", "GameLoop.Keybindings.cs"));
        string window = File.ReadAllText(Path.Combine(root, "MSUIClient", "Engine",
            "ClientWindow.cs"));
        string sheath = File.ReadAllText(Path.Combine(root, "MSUIClient", "GameLoop",
            "Combat", "GameLoop.Sheath.cs"));
        string chat = File.ReadAllText(Path.Combine(root, "MSUIClient", "GameLoop",
            "Panels", "GameLoop.Chat.cs"));
        string update = File.ReadAllText(Path.Combine(root, "MSUIClient", "Program.cs"));

        Check(bindings.Contains("record struct BindingPair(BindingChord Primary",
                  StringComparison.Ordinal) &&
              bindings.Contains("BindingChordLaw.Canonical(x.Value.Primary)",
                  StringComparison.Ordinal) &&
              bindings.Contains("BindingChordLaw.TryParse", StringComparison.Ordinal) &&
              bindings.Contains("BindingChordLaw.HasZeroKeyPoison(saved.Values)",
                  StringComparison.Ordinal) &&
              bindings.Contains("BindingChordLaw.IsZeroKeyPoison(keys, slot)",
                  StringComparison.Ordinal) &&
              bindings.Contains("Alt: row.Binding == GameBinding.ToggleUi",
                  StringComparison.Ordinal) &&
              bindings.Contains("GameBinding[] exact = _bindings",
                  StringComparison.Ordinal) &&
              bindings.Contains("_bindingPointerLatches", StringComparison.Ordinal) &&
              bindings.Contains("_window.BindingWheelDelta", StringComparison.Ordinal) &&
              bindings.Contains("BindingChordLaw.LivePointer", StringComparison.Ordinal) &&
              bindings.Contains("BindingChordLaw.Fallback(live)",
                  StringComparison.Ordinal) &&
              bindings.Contains("if (wasDown || typing || super) continue;",
                  StringComparison.Ordinal) &&
              bindings.Contains("if (worldEntry) continue;",
                  StringComparison.Ordinal) &&
              bindings.Contains("if (!typing && !super && !worldEntry)",
                  StringComparison.Ordinal) &&
              bindings.Contains("if (typing) _bindingLatches.Clear();",
                  StringComparison.Ordinal) &&
              bindings.Contains("GameBinding.ToggleAutorun, \"Auto Run\", Key.NumLock",
                  StringComparison.Ordinal) &&
              bindings.Contains("BindingPointerKey.Button4", StringComparison.Ordinal) &&
              bindings.Contains("GameBinding.OpenChatSlash, \"Open Chat Slash\", Key.Slash",
                  StringComparison.Ordinal) &&
              bindings.Contains("GameBinding.CameraZoomIn, \"Zoom In\", Key.Unknown",
                  StringComparison.Ordinal) &&
              bindings.Contains("GameBinding.ToggleEnemyNameplates, \"Show Enemy Name Plates\", Key.V",
                  StringComparison.Ordinal) &&
              bindings.Contains("GameBinding.AttackTarget, \"Attack Target\", Key.T",
                  StringComparison.Ordinal) &&
              bindings.Contains("GameBinding.FollowTarget, \"Follow Target\", Key.Unknown",
                  StringComparison.Ordinal) &&
              bindings.Contains("GameBinding.SitOrStand, \"Sit/Stand\", Key.X",
                  StringComparison.Ordinal) &&
              bindings.Contains("GameBinding.MinimapZoomIn, \"Minimap Zoom In\", Key.KeypadAdd",
                  StringComparison.Ordinal) &&
              bindings.Contains("GameBinding.MinimapZoomOut, \"Minimap Zoom Out\", Key.KeypadSubtract",
                  StringComparison.Ordinal) &&
              bindings.Contains("GameBinding.ChatPageUp, \"Chat Page Up\", Key.PageUp",
                  StringComparison.Ordinal) &&
              bindings.Contains("new BindingChord(Key.PageDown, Shift: true)",
                  StringComparison.Ordinal) &&
              bindings.Contains("ChatFrameLaw.PageUpOffset", StringComparison.Ordinal) &&
              bindings.Contains("new BindingChord(Key.V, Shift: true)",
                  StringComparison.Ordinal) &&
              bindings.Contains("new BindingChord(Key.V, Control: true)",
                  StringComparison.Ordinal) &&
              bindings.Contains("UpdateAutorunBinding(bool typing)", StringComparison.Ordinal) &&
              bindings.Contains("UpdateChatBindings(bool typing)", StringComparison.Ordinal) &&
              bindings.Contains("UpdateCameraZoomBindings(bool typing)",
                  StringComparison.Ordinal) &&
              bindings.Contains("StopAttack(\"attack-target-toggle\")",
                  StringComparison.Ordinal) &&
              bindings.Contains("CommitSelection(nearest.Guid, beginAttack: true)",
                  StringComparison.Ordinal),
            "binding storage, world-entry input boundary, or exact-first/fallback runtime dispatch escaped the chord law");
        // The capture now takes the row's input KIND: an ordinary command refuses the world
        // click buttons, a gesture row requires one, and a modifier row takes a bare ladder.
        Check(page.Contains("FirstBindableChordDown(BindingInputKind kind)", StringComparison.Ordinal) &&
              page.Contains("RtsBindingLaw.Accepts(captureKind, pressed)", StringComparison.Ordinal) &&
              page.Contains("BindingPointerKey.Button1", StringComparison.Ordinal) &&
              page.Contains("BindingPointerKey.Button2", StringComparison.Ordinal) &&
              page.Contains("!BindingChordLaw.IsModifier(key)", StringComparison.Ordinal) &&
              page.Contains("InputKeyDown(Key.SuperLeft)", StringComparison.Ordinal) &&
              page.Contains("BindingPointerKey.Button3", StringComparison.Ordinal) &&
              page.Contains("BindingPointerKey.WheelUp", StringComparison.Ordinal) &&
              page.Contains("AnyBindableInputDown()", StringComparison.Ordinal) &&
              page.Contains("KeyBindingsUiLaw.FrameSize", StringComparison.Ordinal) &&
              page.Contains("GameText.DrawCentered(dl, KeyBindingsUiLaw.TitleFont",
                  StringComparison.Ordinal) &&
              page.Contains("normalFont: KeyBindingsUiLaw.KeyNormalFont",
                  StringComparison.Ordinal) &&
              !page.Contains("dl.AddText", StringComparison.Ordinal) &&
              page.Contains("_collapsedBindingCategories", StringComparison.Ordinal) &&
              page.Contains("KeyBindingsUiLaw.MatchesSearch", StringComparison.Ordinal) &&
              !page.Contains("new Vector2", StringComparison.Ordinal) &&
              page.Contains("Function is Now Unbound!", StringComparison.Ordinal) &&
              page.Contains("FriendlyChord(chord)", StringComparison.Ordinal),
            "keybinding capture/display/conflict feedback escaped the chord law");
        Check(window.Contains("public bool MouseButton4Down", StringComparison.Ordinal) &&
              window.Contains("public bool MouseButton5Down", StringComparison.Ordinal) &&
              window.Contains("public float BindingWheelDelta", StringComparison.Ordinal) &&
              window.Contains("_mouse.IsButtonPressed(MouseButton.Button4)",
                  StringComparison.Ordinal) &&
              window.Contains("_mouse.IsButtonPressed(MouseButton.Button5)",
                  StringComparison.Ordinal) &&
              window.Contains("BindingWheelDelta += wheel.Y;", StringComparison.Ordinal) &&
              window.Contains("BindingWheelDelta = 0f;", StringComparison.Ordinal) &&
              !window.Contains("else Camera.Zoom(_pendingZoom);", StringComparison.Ordinal) &&
              update.Contains("BindingCommandLaw.ForwardAxis", StringComparison.Ordinal) &&
              !update.Contains("_window.Axis(Key.Up, Key.Down)", StringComparison.Ordinal) &&
              !chat.Contains("ImGui.IsKeyPressed(ImGuiKey.Enter", StringComparison.Ordinal),
            "mouse button/wheel host sampling escaped the binding input seam");
        Check(sheath.Contains("BindingBaseDown(GameBinding.Sheath)",
                  StringComparison.Ordinal) &&
              sheath.Contains("bool acceptedDown = BindingDown(GameBinding.Sheath);",
                  StringComparison.Ordinal) &&
              sheath.Contains("_sheathKeyWasDown = physicalDown;", StringComparison.Ordinal),
            "sheath no longer tracks the base edge separately from exact chord dispatch");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
