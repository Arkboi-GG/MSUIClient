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
    private bool _characterSpecificBindings;
    private string _bindingFeedback = "";
    private double _bindingFeedbackUntil;

    private void OpenKeybindings()
    {
        EnsureBindingsLoaded();
        _bindingSnapshot = new Dictionary<GameBinding, BindingPair>(_bindings);
        _bindingCapture = null;
        _keybindingsOpen = true;
    }

    private void DrawKeybindingsFrame()
    {
        if (!_keybindingsOpen || _gameplayArt is null) return;
        EnsureBindingsLoaded();
        _bindingSnapshot ??= new Dictionary<GameBinding, BindingPair>(_bindings);
        float s = GameplayUiScale();
        if (!BeginVanillaWindow("##keybindings", new Vector2(0, 104), new Vector2(640, 512), out ImDrawListPtr dl,
                out Vector2 origin, out s)) return;

        (string Path, Vector2 Offset, Vector2 Size)[] art =
        [
            (@"Interface\KeyBindingFrame\UI-KeyBindingFrame-TopLeft", new(0,0), new(256,256)),
            (@"Interface\KeyBindingFrame\UI-KeyBindingFrame-Top", new(256,0), new(256,256)),
            (@"Interface\KeyBindingFrame\UI-KeyBindingFrame-TopRight", new(512,0), new(128,256)),
            (@"Interface\KeyBindingFrame\UI-KeyBindingFrame-BotLeft", new(0,256), new(256,256)),
            (@"Interface\KeyBindingFrame\UI-KeyBindingFrame-Bot", new(256,256), new(256,256)),
            (@"Interface\KeyBindingFrame\UI-KeyBindingFrame-BotRight", new(512,256), new(128,256)),
        ];
        foreach (var piece in art) DrawArt(dl, piece.Path, origin + piece.Offset * s, piece.Size, s);
        DrawCenteredText(dl, origin + new Vector2(290, 24) * s, "Key Bindings", 14 * s, VanillaGold);
        dl.AddText(ImGui.GetFont(), 11f * s, origin + new Vector2(26, 35) * s, VanillaGold, "Command");
        DrawCenteredText(dl, origin + new Vector2(290, 41) * s, "Key 1", 11f * s, VanillaGold);
        DrawCenteredText(dl, origin + new Vector2(470, 41) * s, "Key 2", 11f * s, VanillaGold);

        var visibleRows = new List<(bool Header, string Category, GameBinding Binding, string Label)>();
        string lastCategory = "";
        foreach (var row in BindingRows)
        {
            if (row.Category != lastCategory)
            {
                lastCategory = row.Category;
                visibleRows.Add((true, row.Category, default, row.Category));
            }
            visibleRows.Add((false, row.Category, row.Binding, row.Label));
        }
        _bindingScroll = Math.Clamp(_bindingScroll, 0, Math.Max(0, visibleRows.Count - 17));
        Vector2 wheelMin = origin + new Vector2(27, 53) * s;
        ImGui.SetCursorScreenPos(wheelMin);
        ImGui.InvisibleButton("##binding-wheel", new Vector2(535, 390) * s);
        if (ImGui.IsItemHovered() && ImGui.GetIO().MouseWheel != 0)
            _bindingScroll = Math.Clamp(_bindingScroll - Math.Sign(ImGui.GetIO().MouseWheel), 0,
                Math.Max(0, visibleRows.Count - 17));

        for (int i = 0; i < 17 && i + _bindingScroll < visibleRows.Count; i++)
        {
            var row = visibleRows[i + _bindingScroll];
            Vector2 rowMin = origin + new Vector2(27, 53 + i * 23) * s;
            if (row.Header)
            {
                dl.AddText(ImGui.GetFont(), 12f * s, rowMin + new Vector2(0, 5) * s,
                    0xffffffff, row.Label);
                continue;
            }
            dl.AddText(ImGui.GetFont(), 10f * s, rowMin + new Vector2(0, 6) * s,
                VanillaGold, row.Label);
            BindingPair pair = BoundKeys(row.Binding);
            DrawBindingKeyButton(dl, origin, s, rowMin, row.Binding, 1, pair.Primary);
            DrawBindingKeyButton(dl, origin, s, rowMin, row.Binding, 2, pair.Secondary);
        }

        DrawVanillaScrollBar(dl, "##binding-scrollbar", origin + new Vector2(584, 52) * s,
            390, s, _bindingScroll, Math.Max(0, visibleRows.Count - 17), v => _bindingScroll = v);

        if (_bindingCapture is not null)
        {
            if (!_bindingCaptureReleased) _bindingCaptureReleased = !AnyBindableKeyDown();
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
                origin + new Vector2(320, 455) * s, s, 0xff2020ff);

        bool wasCharacterSpecific = _characterSpecificBindings;
        bool toggledCharacterSpecific = VanillaCheckButton(dl, "##character-bindings",
            origin + new Vector2(395, 10) * s,
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
        if (VanillaButton(dl, "Defaults##bindings", "Reset To Default", origin + new Vector2(10, 469) * s,
                new Vector2(130, 22), s)) ResetBindingsToDefaults();
        bool canUnbind = _bindingCapture is not null;
        if (VanillaButton(dl, "Unbind##bindings", "Unbind", origin + new Vector2(230, 469) * s,
                new Vector2(130, 22), s, canUnbind) && _bindingCapture is { } unbind)
        { _bindings[unbind] = BoundKeys(unbind).With(_bindingCaptureSlot, default); _bindingCapture = null; }
        if (VanillaButton(dl, "Okay##bindings", "Okay", origin + new Vector2(360, 469) * s,
                new Vector2(130, 22), s))
        { SaveBindings(); _bindingSnapshot = null; _keybindingsOpen = false; }
        if (VanillaButton(dl, "Cancel##bindings", "Cancel", origin + new Vector2(490, 469) * s,
                new Vector2(130, 22), s))
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
        Vector2 min = rowMin + new Vector2(slot == 1 ? 175 : 355, 1) * s;
        bool capturing = _bindingCapture == binding && _bindingCaptureSlot == slot;
        string text = capturing ? "Press Key to Bind" : FriendlyChord(chord);
        if (VanillaButton(dl, $"##bind-{binding}-{slot}", text, min, new Vector2(180, 22), s))
        {
            _bindingCapture = binding;
            _bindingCaptureSlot = slot;
            _bindingCaptureReleased = !AnyBindableKeyDown();
        }
    }

    private bool AnyBindableKeyDown() => FirstBindableKeyDown() is not null;

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
        if (FirstBindableKeyDown() is not { } key) return null;
        return BindingChordLaw.Live(key,
            InputKeyDown(Key.AltLeft) || InputKeyDown(Key.AltRight),
            InputKeyDown(Key.ControlLeft) || InputKeyDown(Key.ControlRight),
            InputKeyDown(Key.ShiftLeft) || InputKeyDown(Key.ShiftRight));
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
