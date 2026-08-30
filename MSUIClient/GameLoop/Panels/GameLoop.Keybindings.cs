using System.Numerics;
using ImGuiNET;
using Silk.NET.Input;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private bool _keybindingsOpen;
    private Dictionary<GameBinding, BindingPair>? _bindingSnapshot;
    private GameBinding? _bindingCapture;
    private int _bindingCaptureSlot = 1;
    private bool _bindingCaptureReleased;
    private int _bindingScroll;
    private readonly byte[] _bindingSearch = new byte[64];
    private readonly HashSet<string> _collapsedBindingCategories =
        new(StringComparer.Ordinal);
    private bool _bindingCategoriesInitialized;
    private bool _characterSpecificBindings;
    private string _bindingFeedback = "";
    private double _bindingFeedbackUntil;

    private void OpenKeybindings()
    {
        EnsureBindingsLoaded();
        _bindingSnapshot = new Dictionary<GameBinding, BindingPair>(_bindings);
        _bindingCapture = null;
        if (!_bindingCategoriesInitialized)
        {
            foreach (string category in BindingRows.Select(row => row.Category).Distinct())
                _collapsedBindingCategories.Add(category);
            _bindingCategoriesInitialized = true;
        }
        _keybindingsOpen = true;
    }

    private void DrawKeybindingsFrame()
    {
        if (!_keybindingsOpen || _gameplayArt is null) return;
        EnsureBindingsLoaded();
        _bindingSnapshot ??= new Dictionary<GameBinding, BindingPair>(_bindings);
        float s = GameplayUiScale();
        // Vanilla anchors this frame TOP to UIParent - centred, 100 down - not against the left
        // edge. The origin is in LOGICAL units, so divide the framebuffer back through the scale
        // before centring, or the frame drifts right as the window grows.
        Vector2 logicalDisplay = s > 0f ? ImGui.GetIO().DisplaySize / s : KeyBindingsUiLaw.FrameSize;
        if (!BeginVanillaWindow("##keybindings",
                KeyBindingsUiLaw.WindowOrigin(logicalDisplay.X, logicalDisplay.Y),
                KeyBindingsUiLaw.FrameSize, out ImDrawListPtr dl,
                out Vector2 origin, out s, movable: true))
        { ImGui.End(); return; }

        foreach (KeyBindingsUiLaw.ArtSlice piece in KeyBindingsUiLaw.Art)
            DrawArt(dl, piece.Path, origin + piece.Offset * s, piece.Size, s);
        GameText.DrawCentered(dl, KeyBindingsUiLaw.TitleFont, "Key Bindings",
            origin + KeyBindingsUiLaw.TitleCenter * s, s);
        GameText.Draw(dl, KeyBindingsUiLaw.ColumnHeaderFont, "Command",
            origin + KeyBindingsUiLaw.CommandTitle * s, s);
        GameText.DrawCentered(dl, KeyBindingsUiLaw.ColumnHeaderFont, "Key 1",
            origin + KeyBindingsUiLaw.KeyOneCenter * s, s);
        GameText.DrawCentered(dl, KeyBindingsUiLaw.ColumnHeaderFont, "Key 2",
            origin + KeyBindingsUiLaw.KeyTwoCenter * s, s);

        KeyBindingsUiLaw.Rect search = KeyBindingsUiLaw.Search;
        if (VanillaInputText(dl, "##binding-search", _bindingSearch,
                origin + search.Min * s, search.Size, s))
            _bindingScroll = 0;
        if (ReadBuffer(_bindingSearch).Length == 0 && !ImGui.IsItemActive())
            GameText.Draw(dl, "GameFontDisableSmall", "Search",
                origin + (search.Min + KeyBindingsUiLaw.SearchPlaceholderOffset) * s, s);

        var visibleRows = new List<(bool Header, string Category, GameBinding Binding, string Label)>();
        string query = ReadBuffer(_bindingSearch).Trim();
        foreach (IGrouping<string, (string Category, GameBinding Binding, string Label, Key Default)>
                 group in BindingRows.GroupBy(row => row.Category))
        {
            var matches = group.Where(row =>
                KeyBindingsUiLaw.MatchesSearch(row.Category, row.Label, query)).ToArray();
            if (matches.Length == 0) continue;
            visibleRows.Add((true, group.Key, default, group.Key));
            if (query.Length == 0 && _collapsedBindingCategories.Contains(group.Key)) continue;
            visibleRows.AddRange(matches.Select(row =>
                (false, row.Category, row.Binding, row.Label)));
        }
        _bindingScroll = KeyBindingsUiLaw.ClampScroll(_bindingScroll, visibleRows.Count);

        // THE WHEEL CATCHER MUST NOT BE A BUTTON. It spans the whole row band and was submitted
        // BEFORE the rows, so on the press frame it took ActiveId first; every item inside it -
        // the category +/- headers and both key buttons on every row - then failed ItemHoverable
        // ("ActiveId != 0 && ActiveId != id") and could never be clicked. The panel drew
        // correctly and did nothing, which is exactly how it was reported. A plain rect test
        // scrolls the same way while claiming no id at all - the pattern the skill list already
        // uses for the same reason (GameLoop.CharacterPage.cs). Reported 2026-08-26.
        Vector2 wheelMin = origin + KeyBindingsUiLaw.Rows.Min * s;
        Vector2 wheelMax = wheelMin + KeyBindingsUiLaw.Rows.Size * s;
        if (_bindingCapture is null && ImGui.IsMouseHoveringRect(wheelMin, wheelMax, false) &&
            ImGui.GetIO().MouseWheel != 0)
            _bindingScroll = KeyBindingsUiLaw.ClampScroll(
                _bindingScroll - Math.Sign(ImGui.GetIO().MouseWheel), visibleRows.Count);

        for (int i = 0; i < KeyBindingsUiLaw.VisibleRows &&
             i + _bindingScroll < visibleRows.Count; i++)
        {
            var row = visibleRows[i + _bindingScroll];
            Vector2 rowMin = origin + KeyBindingsUiLaw.RowMinimum(i) * s;
            if (row.Header)
            {
                ImGui.SetCursorScreenPos(rowMin);
                ImGui.InvisibleButton($"##binding-category-{row.Category}",
                    KeyBindingsUiLaw.RowHitSize * s);
                bool headerHovered = ImGui.IsItemHovered();
                if (query.Length == 0 && ImGui.IsItemClicked(ImGuiMouseButton.Left) &&
                    !_collapsedBindingCategories.Add(row.Category))
                    _collapsedBindingCategories.Remove(row.Category);
                bool collapsed = query.Length == 0 &&
                    _collapsedBindingCategories.Contains(row.Category);
                uint glyph = _gameplayArt.Handle(collapsed
                    ? @"Interface\Buttons\UI-PlusButton-Up"
                    : @"Interface\Buttons\UI-MinusButton-Up");
                if (glyph != 0)
                {
                    KeyBindingsUiLaw.Rect glyphRect = KeyBindingsUiLaw.HeaderGlyph;
                    Vector2 glyphMin = rowMin + glyphRect.Min * s;
                    dl.AddImage((nint)glyph, glyphMin, glyphMin + glyphRect.Size * s);
                }
                GameText.Draw(dl, headerHovered ? KeyBindingsUiLaw.CategoryHighlightFont :
                    KeyBindingsUiLaw.CategoryFont, row.Label,
                    rowMin + KeyBindingsUiLaw.HeaderTextOffset * s, s);
                continue;
            }
            GameText.Draw(dl, KeyBindingsUiLaw.CommandFont, row.Label,
                rowMin + KeyBindingsUiLaw.CommandTextOffset * s, s);
            BindingPair pair = BoundKeys(row.Binding);
            DrawBindingKeyButton(dl, origin, s, rowMin, row.Binding, 1, pair.Primary);
            DrawBindingKeyButton(dl, origin, s, rowMin, row.Binding, 2, pair.Secondary);
        }

        DrawVanillaScrollBar(dl, "##binding-scrollbar",
            origin + KeyBindingsUiLaw.ScrollMinimum * s,
            KeyBindingsUiLaw.ScrollHeight, s, _bindingScroll,
            KeyBindingsUiLaw.MaximumScroll(visibleRows.Count), v => _bindingScroll = v);

        if (_bindingCapture is not null)
        {
            BindingInputKind captureKind = BindingKindOf(_bindingCapture.Value);
            if (!_bindingCaptureReleased) _bindingCaptureReleased = !AnyBindableInputDown();
            else if (FirstBindableChordDown(captureKind) is { } pressed)
            {
                // A row can only hold a chord its command is able to READ - a world-click
                // gesture needs a mouse button, a held modifier needs a bare modifier, and an
                // ordinary command refuses the left and right buttons because those never
                // enter the global latch. Refusing here is what keeps a dead binding - one
                // that displays perfectly and never fires - off the list entirely.
                if (!RtsBindingLaw.Accepts(captureKind, pressed))
                {
                    _bindingFeedback = RtsBindingLaw.RejectionFor(captureKind);
                    _bindingFeedbackUntil = ImGui.GetTime() + 4.0;
                    _bindingCaptureReleased = false;   // wait for release, don't spam the line
                }
                else
                {
                    GameBinding[] previousOwners = _bindings
                        .Where(x => x.Value.Contains(pressed))
                        .Select(x => x.Key).ToArray();
                    string feedback = "Key Bound Successfully";
                    foreach (GameBinding other in previousOwners)
                    {
                        BindingPair without = _bindings[other].Without(pressed);
                        if (other != _bindingCapture.Value &&
                            !without.Primary.IsBound && !without.Secondary.IsBound)
                            feedback = $"{BindingLabel(other)} Function is Now Unbound!";
                        _bindings[other] = _bindings[other].Without(pressed);
                    }
                    _bindings[_bindingCapture.Value] =
                        BoundKeys(_bindingCapture.Value).With(_bindingCaptureSlot, pressed);
                    _bindingFeedback = feedback;
                    _bindingFeedbackUntil = ImGui.GetTime() + 4.0;
                    _bindingCapture = null;
                }
            }
        }

        if (_bindingFeedback.Length > 0 && ImGui.GetTime() < _bindingFeedbackUntil)
            GameText.DrawCentered(dl, "GameFontNormalSmall", _bindingFeedback,
                origin + KeyBindingsUiLaw.FeedbackCenter * s, s, 0xff2020ff);

        bool wasCharacterSpecific = _characterSpecificBindings;
        bool toggledCharacterSpecific = VanillaCheckButton(dl, "##character-bindings",
            origin + KeyBindingsUiLaw.CharacterSpecificMinimum * s,
            "Character Specific Key Bindings", s, ref _characterSpecificBindings);
        if (toggledCharacterSpecific)
        {
            PlayUiSound(CharacterBindingsUiLaw.ToggleSound);
            if (!wasCharacterSpecific)
            {
                EnableCharacterSpecificBindings();
            }
            else
            {
                // UNCHECK is destructive. The source immediately springs the box back on and
                // lets the rule-owned StaticPopup decide whether the set is deleted.
                _characterSpecificBindings = true;
                bool dead = _entities.TryGet(LocalPlayerGuid, out WorldEntity player) && player.IsDead;
                ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Show(
                    _staticPopupSlots, CharacterBindingsUiLaw.Definition, dead));
            }
        }
        if (VanillaButton(dl, "Defaults##bindings", "Reset To Default",
                origin + KeyBindingsUiLaw.Defaults.Min * s,
                KeyBindingsUiLaw.Defaults.Size, s)) ResetBindingsToDefaults();
        bool canUnbind = _bindingCapture is not null;
        if (VanillaButton(dl, "Unbind##bindings", "Unbind",
                origin + KeyBindingsUiLaw.Unbind.Min * s,
                KeyBindingsUiLaw.Unbind.Size, s, canUnbind) &&
            _bindingCapture is { } unbind)
        { _bindings[unbind] = BoundKeys(unbind).With(_bindingCaptureSlot, default); _bindingCapture = null; }
        if (VanillaButton(dl, "Okay##bindings", "Okay",
                origin + KeyBindingsUiLaw.Okay.Min * s,
                KeyBindingsUiLaw.Okay.Size, s))
        { SaveBindings(); _bindingSnapshot = null; _keybindingsOpen = false; }
        if (VanillaButton(dl, "Cancel##bindings", "Cancel",
                origin + KeyBindingsUiLaw.Cancel.Min * s,
                KeyBindingsUiLaw.Cancel.Size, s))
        {
            if (_bindingSnapshot is not null)
            { _bindings.Clear(); foreach (var pair in _bindingSnapshot) _bindings[pair.Key] = pair.Value; }
            _bindingSnapshot = null; _keybindingsOpen = false;
        }
        ImGui.End();
    }

    private void DrawBindingKeyButton(ImDrawListPtr dl, Vector2 origin, float s, Vector2 rowMin,
        GameBinding binding, int slot, BindingChord chord)
    {
        KeyBindingsUiLaw.Rect keyRect = slot == 1
            ? KeyBindingsUiLaw.PrimaryKey : KeyBindingsUiLaw.SecondaryKey;
        Vector2 min = rowMin + keyRect.Min * s;
        bool capturing = _bindingCapture == binding && _bindingCaptureSlot == slot;
        string text = capturing ? "Press Key to Bind" : FriendlyChord(chord);
        if (VanillaButton(dl, $"##bind-{binding}-{slot}", text, min, keyRect.Size, s,
                normalFont: KeyBindingsUiLaw.KeyNormalFont,
                highlightFont: KeyBindingsUiLaw.KeyHighlightFont,
                disabledFont: KeyBindingsUiLaw.KeyDisabledFont))
        {
            _bindingCapture = binding;
            _bindingCaptureSlot = slot;
            _bindingCaptureReleased = !AnyBindableInputDown();
        }
    }

    private bool AnyBindableInputDown() => FirstBindableKeyDown() is not null ||
        _window.MouseMiddleDown || _window.MouseButton4Down || _window.MouseButton5Down ||
        _window.MouseLeftDown || _window.MouseRightDown ||
        AnyModifierDown() || ImGui.GetIO().MouseWheel != 0;

    /// <summary>Bare modifiers count as input for the RELEASE gate only. A modifier row is
    /// armed by clicking its key button, and the player is usually still holding nothing;
    /// without this the ladder they were already holding would be taken instantly.</summary>
    private bool AnyModifierDown() =>
        InputKeyDown(Key.AltLeft) || InputKeyDown(Key.AltRight) ||
        InputKeyDown(Key.ControlLeft) || InputKeyDown(Key.ControlRight) ||
        InputKeyDown(Key.ShiftLeft) || InputKeyDown(Key.ShiftRight);

    private Key? FirstBindableKeyDown()
    {
        foreach (Key key in Enum.GetValues<Key>().Distinct())
            if (key != Key.Unknown && !BindingChordLaw.IsModifier(key) &&
                _window.IsDown(key)) return key;
        return null;
    }

    private BindingChord? FirstBindableChordDown(BindingInputKind kind)
    {
        if (InputKeyDown(Key.SuperLeft) || InputKeyDown(Key.SuperRight)) return null;
        bool alt = InputKeyDown(Key.AltLeft) || InputKeyDown(Key.AltRight);
        bool control = InputKeyDown(Key.ControlLeft) || InputKeyDown(Key.ControlRight);
        bool shift = InputKeyDown(Key.ShiftLeft) || InputKeyDown(Key.ShiftRight);

        // A modifier row takes the ladder ALONE: with a base key or a button also down the
        // player is spelling an ordinary chord, and the ladder is not yet what they meant.
        if (kind == BindingInputKind.Modifier)
            return (alt || control || shift) && FirstBindableKeyDown() is null &&
                   !_window.MouseLeftDown && !_window.MouseRightDown &&
                   !_window.MouseMiddleDown && !_window.MouseButton4Down &&
                   !_window.MouseButton5Down && ImGui.GetIO().MouseWheel == 0
                ? new BindingChord(Key.Unknown, alt, control, shift) : null;

        // The left and right buttons are offered only to gesture rows, and only for a click
        // that lands OUTSIDE every ImGui window. The capture block runs before this frame's
        // Okay/Cancel/Unbind buttons are submitted, so without that fence arming a gesture row
        // and then reaching for Cancel would bind Left Mouse instead of cancelling.
        bool worldClick = kind == BindingInputKind.Pointer && !ImGui.GetIO().WantCaptureMouse;
        BindingPointerKey pointer =
            worldClick && _window.MouseLeftDown ? BindingPointerKey.Button1 :
            worldClick && _window.MouseRightDown ? BindingPointerKey.Button2 :
            _window.MouseMiddleDown ? BindingPointerKey.Button3 :
            _window.MouseButton4Down ? BindingPointerKey.Button4 :
            _window.MouseButton5Down ? BindingPointerKey.Button5 :
            ImGui.GetIO().MouseWheel > 0 ? BindingPointerKey.WheelUp :
            ImGui.GetIO().MouseWheel < 0 ? BindingPointerKey.WheelDown :
            BindingPointerKey.None;
        if (pointer != BindingPointerKey.None)
            return BindingChordLaw.LivePointer(pointer, alt, control, shift);
        return FirstBindableKeyDown() is { } key
            ? BindingChordLaw.Live(key, alt, control, shift) : null;
    }

    private static string BindingLabel(GameBinding binding) =>
        BindingRows.FirstOrDefault(row => row.Binding == binding).Label ?? binding.ToString();

    private static string FriendlyChord(in BindingChord chord) =>
        BindingChordLaw.Display(chord, FriendlyKey);

    private static string FriendlyHotkey(in BindingChord chord) =>
        chord.IsBound ? BindingChordLaw.Display(chord, FriendlyKey) : "";

    private static string FriendlyKey(Key key) => key switch
    {
        Key.Unknown => "",
        Key.Number0 => "0", Key.Number1 => "1", Key.Number2 => "2", Key.Number3 => "3",
        Key.Number4 => "4", Key.Number5 => "5", Key.Number6 => "6", Key.Number7 => "7",
        Key.Number8 => "8", Key.Number9 => "9", Key.Space => "Space Bar", Key.Slash => "/",
        Key.Equal => "=", Key.Minus => "-", _ => key.ToString(),
    };
}
