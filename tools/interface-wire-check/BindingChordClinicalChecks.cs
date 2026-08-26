using MSUIClient;
using MSUIClient.Engine.UI;
using Silk.NET.Input;

internal static class BindingChordClinicalChecks
{
    public static void Run()
    {
        CheckCodecAndFallback();
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
              KeyBindingsUiLaw.MaximumScroll(20) == 3 &&
              KeyBindingsUiLaw.ClampScroll(9, 20) == 3 &&
              KeyBindingsUiLaw.MatchesSearch("Action Bar", "Action Button 1", "button") &&
              !KeyBindingsUiLaw.MatchesSearch("Movement", "Jump", "spell"),
            "keybindings shell/search/collapse geometry law drifted");
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
            "binding storage or exact-first/fallback runtime dispatch escaped the chord law");
        Check(page.Contains("FirstBindableChordDown()", StringComparison.Ordinal) &&
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
