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
        if (!BeginVanillaWindow("##keybindings", KeyBindingsUiLaw.WindowMinimum,
                KeyBindingsUiLaw.FrameSize, out ImDrawListPtr dl,
                out Vector2 origin, out s)) return;

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
        Vector2 wheelMin = origin + KeyBindingsUiLaw.Rows.Min * s;
        ImGui.SetCursorScreenPos(wheelMin);
        ImGui.InvisibleButton("##binding-wheel", KeyBindingsUiLaw.Rows.Size * s);
        if (_bindingCapture is null && ImGui.IsItemHovered() && ImGui.GetIO().MouseWheel != 0)
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
            if (!_bindingCaptureReleased) _bindingCaptureReleased = !AnyBindableInputDown();
            else if (FirstBindableChordDown() is { } pressed)
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
                _bindings[_bindingCapture.Value] = BoundKeys(_bindingCapture.Value).With(_bindingCaptureSlot, pressed);
                _bindingFeedback = feedback;
                _bindingFeedbackUntil = ImGui.GetTime() + 4.0;
                _bindingCapture = null;
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
        ImGui.GetIO().MouseWheel != 0;

    private Key? FirstBindableKeyDown()
    {
        foreach (Key key in Enum.GetValues<Key>().Distinct())
            if (key != Key.Unknown && !BindingChordLaw.IsModifier(key) &&
                _window.IsDown(key)) return key;
        return null;
    }

    private BindingChord? FirstBindableChordDown()
    {
        if (InputKeyDown(Key.SuperLeft) || InputKeyDown(Key.SuperRight)) return null;
        bool alt = InputKeyDown(Key.AltLeft) || InputKeyDown(Key.AltRight);
        bool control = InputKeyDown(Key.ControlLeft) || InputKeyDown(Key.ControlRight);
        bool shift = InputKeyDown(Key.ShiftLeft) || InputKeyDown(Key.ShiftRight);
        BindingPointerKey pointer = _window.MouseMiddleDown ? BindingPointerKey.Button3 :
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
