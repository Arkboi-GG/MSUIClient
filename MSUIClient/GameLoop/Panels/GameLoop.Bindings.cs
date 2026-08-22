using System.Text.Json;
using System.Numerics;
using Silk.NET.Input;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private enum GameBinding
    {
        MoveForward, MoveBackward, TurnLeft, TurnRight, StrafeLeft, StrafeRight,
        Jump, ToggleRun, TargetNearestEnemy, OpenBackpack, OpenCharacter, OpenSkills,
        OpenSpellbook, OpenTalents, OpenQuestLog, OpenSocial, OpenWorldMap, Sheath, ToggleUi,
        Action1, Action2, Action3, Action4,
        Action5, Action6, Action7, Action8, Action9, Action10, Action11, Action12,
        MultiActionBar1Button1, MultiActionBar1Button2, MultiActionBar1Button3,
        MultiActionBar1Button4, MultiActionBar1Button5, MultiActionBar1Button6,
        MultiActionBar1Button7, MultiActionBar1Button8, MultiActionBar1Button9,
        MultiActionBar1Button10, MultiActionBar1Button11, MultiActionBar1Button12,
        MultiActionBar2Button1, MultiActionBar2Button2, MultiActionBar2Button3,
        MultiActionBar2Button4, MultiActionBar2Button5, MultiActionBar2Button6,
        MultiActionBar2Button7, MultiActionBar2Button8, MultiActionBar2Button9,
        MultiActionBar2Button10, MultiActionBar2Button11, MultiActionBar2Button12,
    }

    private static readonly (string Category, GameBinding Binding, string Label, Key Default)[] BindingRows =
    [
        ("Movement", GameBinding.MoveForward, "Move Forward", Key.W),
        ("Movement", GameBinding.MoveBackward, "Move Backward", Key.S),
        ("Movement", GameBinding.TurnLeft, "Turn Left", Key.A),
        ("Movement", GameBinding.TurnRight, "Turn Right", Key.D),
        ("Movement", GameBinding.StrafeLeft, "Strafe Left", Key.Q),
        ("Movement", GameBinding.StrafeRight, "Strafe Right", Key.E),
        ("Movement", GameBinding.Jump, "Jump", Key.Space),
        ("Movement", GameBinding.ToggleRun, "Run/Walk", Key.Slash),
        ("Targeting", GameBinding.TargetNearestEnemy, "Target Nearest Enemy", Key.Tab),
        ("Interface", GameBinding.OpenBackpack, "Open Backpack", Key.B),
        ("Interface", GameBinding.OpenCharacter, "Character Info", Key.C),
        ("Interface", GameBinding.OpenSkills, SkillFrameUiLaw.BindingLabel, Key.K),
        ("Interface", GameBinding.OpenSpellbook, "Spellbook", Key.P),
        ("Interface", GameBinding.OpenTalents, "Talents", Key.N),
        ("Interface", GameBinding.OpenQuestLog, "Quest Log", Key.L),
        ("Interface", GameBinding.OpenSocial, "Social", Key.O),
        ("Interface", GameBinding.OpenWorldMap, "World Map", Key.M),
        ("Interface", GameBinding.Sheath, "Sheath/Unsheath", Key.Z),
        ("Interface", GameBinding.ToggleUi, "Toggle User Interface", Key.Z),
        ("Action Bar", GameBinding.Action1, "Action Button 1", Key.Number1),
        ("Action Bar", GameBinding.Action2, "Action Button 2", Key.Number2),
        ("Action Bar", GameBinding.Action3, "Action Button 3", Key.Number3),
        ("Action Bar", GameBinding.Action4, "Action Button 4", Key.Number4),
        ("Action Bar", GameBinding.Action5, "Action Button 5", Key.Number5),
        ("Action Bar", GameBinding.Action6, "Action Button 6", Key.Number6),
        ("Action Bar", GameBinding.Action7, "Action Button 7", Key.Number7),
        ("Action Bar", GameBinding.Action8, "Action Button 8", Key.Number8),
        ("Action Bar", GameBinding.Action9, "Action Button 9", Key.Number9),
        ("Action Bar", GameBinding.Action10, "Action Button 10", Key.Number0),
        ("Action Bar", GameBinding.Action11, "Action Button 11", Key.Minus),
        ("Action Bar", GameBinding.Action12, "Action Button 12", Key.Equal),
        ("MultiActionBar 1", GameBinding.MultiActionBar1Button1, "Action Button 1", Key.Unknown),
        ("MultiActionBar 1", GameBinding.MultiActionBar1Button2, "Action Button 2", Key.Unknown),
        ("MultiActionBar 1", GameBinding.MultiActionBar1Button3, "Action Button 3", Key.Unknown),
        ("MultiActionBar 1", GameBinding.MultiActionBar1Button4, "Action Button 4", Key.Unknown),
        ("MultiActionBar 1", GameBinding.MultiActionBar1Button5, "Action Button 5", Key.Unknown),
        ("MultiActionBar 1", GameBinding.MultiActionBar1Button6, "Action Button 6", Key.Unknown),
        ("MultiActionBar 1", GameBinding.MultiActionBar1Button7, "Action Button 7", Key.Unknown),
        ("MultiActionBar 1", GameBinding.MultiActionBar1Button8, "Action Button 8", Key.Unknown),
        ("MultiActionBar 1", GameBinding.MultiActionBar1Button9, "Action Button 9", Key.Unknown),
        ("MultiActionBar 1", GameBinding.MultiActionBar1Button10, "Action Button 10", Key.Unknown),
        ("MultiActionBar 1", GameBinding.MultiActionBar1Button11, "Action Button 11", Key.Unknown),
        ("MultiActionBar 1", GameBinding.MultiActionBar1Button12, "Action Button 12", Key.Unknown),
        ("MultiActionBar 2", GameBinding.MultiActionBar2Button1, "Action Button 1", Key.Unknown),
        ("MultiActionBar 2", GameBinding.MultiActionBar2Button2, "Action Button 2", Key.Unknown),
        ("MultiActionBar 2", GameBinding.MultiActionBar2Button3, "Action Button 3", Key.Unknown),
        ("MultiActionBar 2", GameBinding.MultiActionBar2Button4, "Action Button 4", Key.Unknown),
        ("MultiActionBar 2", GameBinding.MultiActionBar2Button5, "Action Button 5", Key.Unknown),
        ("MultiActionBar 2", GameBinding.MultiActionBar2Button6, "Action Button 6", Key.Unknown),
        ("MultiActionBar 2", GameBinding.MultiActionBar2Button7, "Action Button 7", Key.Unknown),
        ("MultiActionBar 2", GameBinding.MultiActionBar2Button8, "Action Button 8", Key.Unknown),
        ("MultiActionBar 2", GameBinding.MultiActionBar2Button9, "Action Button 9", Key.Unknown),
        ("MultiActionBar 2", GameBinding.MultiActionBar2Button10, "Action Button 10", Key.Unknown),
        ("MultiActionBar 2", GameBinding.MultiActionBar2Button11, "Action Button 11", Key.Unknown),
        ("MultiActionBar 2", GameBinding.MultiActionBar2Button12, "Action Button 12", Key.Unknown),
    ];

    private readonly record struct BindingPair(BindingChord Primary, BindingChord Secondary)
    {
        public bool Contains(in BindingChord chord) => chord.IsBound &&
            (Primary == chord || Secondary == chord);
        public bool ContainsBase(Key key) => key != Key.Unknown &&
            (Primary.Key == key || Secondary.Key == key);
        public BindingPair With(int slot, in BindingChord chord) => slot == 2
            ? this with { Secondary = chord }
            : this with { Primary = chord };
        public BindingPair Without(in BindingChord chord) =>
            new(Primary == chord ? default : Primary,
                Secondary == chord ? default : Secondary);
    }

    private readonly Dictionary<GameBinding, BindingPair> _bindings = [];
    private bool _bindingsLoaded;
    private ulong _bindingsCharacterGuid;
    private bool _targetNearestWasDown;
    private bool _toggleRunWasDown;
    private bool _walkToggled;
    private bool _toggleUiWasDown;
    private bool _uiHidden;
    private readonly Dictionary<Key, HashSet<GameBinding>> _bindingLatches = [];
    private readonly HashSet<Key> _bindingPhysicalDown = [];

    private void EnsureBindingsLoaded()
    {
        ulong playerGuid = LocalPlayerGuid;
        if (_bindingsLoaded && _bindingsCharacterGuid == playerGuid) return;
        _bindingsLoaded = true;
        _bindingsCharacterGuid = playerGuid;
        string characterPath = CharacterBindingsPath(playerGuid);
        _characterSpecificBindings = File.Exists(characterPath);
        LoadBindingsFromPath(_characterSpecificBindings
            ? characterPath
            : AccountBindingsPath());
    }

    private string AccountBindingsPath() => Path.Combine(_config.RepoRoot, "keybindings.json");

    private string CharacterBindingsPath(ulong playerGuid) => Path.Combine(
        _config.RepoRoot, CharacterBindingsUiLaw.CharacterFileName(playerGuid));

    private string CurrentBindingsPath() => _characterSpecificBindings
        ? CharacterBindingsPath(_bindingsCharacterGuid)
        : AccountBindingsPath();

    private void LoadBindingsFromPath(string path)
    {
        ResetBindingsToDefaults();
        try
        {
            if (!File.Exists(path)) return;
            string json = File.ReadAllText(path);
            var saved = JsonSerializer.Deserialize<Dictionary<string, string[]>>(json);
            if (saved is not null)
            {
                foreach ((string name, string[] keys) in saved)
                    if (Enum.TryParse(name, out GameBinding binding))
                    {
                        BindingChord primary = keys.Length > 0 &&
                            BindingChordLaw.TryParse(keys[0], out BindingChord p) ? p : default;
                        BindingChord secondary = keys.Length > 1 &&
                            BindingChordLaw.TryParse(keys[1], out BindingChord s) ? s : default;
                        _bindings[binding] = new(primary, secondary);
                    }
                return;
            }
        }
        catch
        {
            // Night 04 wrote the original one-key schema. Preserve it as a
            // backwards-compatible migration rather than discarding user bindings.
            try
            {
                var legacy = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
                if (legacy is null) return;
                foreach ((string name, string keyName) in legacy)
                    if (Enum.TryParse(name, out GameBinding binding) &&
                        BindingChordLaw.TryParse(keyName, out BindingChord chord))
                        _bindings[binding] = new(chord, default);
            }
            catch (Exception ex) { Console.WriteLine($"[bindings] load failed: {ex.Message}"); }
        }
    }

    private void SaveBindings()
    {
        EnsureBindingsLoaded();
        SaveBindingsToPath(CurrentBindingsPath());
    }

    private void SaveBindingsToPath(string path)
    {
        var data = _bindings.ToDictionary(x => x.Key.ToString(), x =>
            new[] { BindingChordLaw.Canonical(x.Value.Primary),
                BindingChordLaw.Canonical(x.Value.Secondary) });
        File.WriteAllText(path, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void EnableCharacterSpecificBindings()
    {
        EnsureBindingsLoaded();
        _characterSpecificBindings = true;
        SaveBindingsToPath(CharacterBindingsPath(_bindingsCharacterGuid));
        _bindingSnapshot = new Dictionary<GameBinding, BindingPair>(_bindings);
        _bindingCapture = null;
        _bindingFeedback = "";
    }

    private void AcceptDeleteCharacterSpecificBindings()
    {
        EnsureBindingsLoaded();
        string characterPath = CharacterBindingsPath(_bindingsCharacterGuid);

        // Reference order is LoadBindings(1), then SaveBindings(1). Loading first is essential:
        // the character set must never be copied into the account set during deletion.
        LoadBindingsFromPath(AccountBindingsPath());
        _characterSpecificBindings = false;
        if (File.Exists(characterPath)) File.Delete(characterPath);
        SaveBindingsToPath(AccountBindingsPath());
        _bindingSnapshot = new Dictionary<GameBinding, BindingPair>(_bindings);
        _bindingCapture = null;
        _bindingFeedback = "";
    }

    private void ResetBindingsToDefaults()
    {
        _bindings.Clear();
        foreach (var row in BindingRows)
            _bindings[row.Binding] = new(new BindingChord(row.Default,
                Alt: row.Binding == GameBinding.ToggleUi), default);
    }

    private Key BoundKey(GameBinding binding)
    {
        EnsureBindingsLoaded();
        return _bindings.GetValueOrDefault(binding).Primary.Key;
    }

    private BindingPair BoundKeys(GameBinding binding)
    {
        EnsureBindingsLoaded();
        return _bindings.GetValueOrDefault(binding);
    }

    private bool BindingDown(GameBinding binding)
    {
        EnsureBindingsLoaded();
        return _bindingLatches.Values.Any(active => active.Contains(binding));
    }

    /// <summary>
    /// Resolve once on the base-key edge and hold that command set until the base releases. This
    /// is the reference Held latch: changing/releasing modifiers beside a held base never changes
    /// commands or manufactures a second press. Entering a typing owner clears latches while the
    /// physical-state scan continues, so leaving chat cannot consume an already-held key.
    /// </summary>
    private void UpdateBindingLatches(bool typing)
    {
        EnsureBindingsLoaded();
        bool alt = InputKeyDown(Key.AltLeft) || InputKeyDown(Key.AltRight);
        bool control = InputKeyDown(Key.ControlLeft) || InputKeyDown(Key.ControlRight);
        bool shift = InputKeyDown(Key.ShiftLeft) || InputKeyDown(Key.ShiftRight);
        bool super = InputKeyDown(Key.SuperLeft) || InputKeyDown(Key.SuperRight);
        if (typing) _bindingLatches.Clear();
        foreach (Key key in Enum.GetValues<Key>().Distinct())
        {
            if (key == Key.Unknown || BindingChordLaw.IsModifier(key)) continue;
            bool down = InputKeyDown(key);
            bool wasDown = _bindingPhysicalDown.Contains(key);
            if (!down)
            {
                _bindingPhysicalDown.Remove(key);
                _bindingLatches.Remove(key);
                continue;
            }
            _bindingPhysicalDown.Add(key);
            if (wasDown || typing || super) continue;

            BindingChord live = BindingChordLaw.Live(key, alt, control, shift);
            GameBinding[] exact = _bindings
                .Where(candidate => candidate.Value.Contains(live))
                .Select(candidate => candidate.Key).ToArray();
            if (exact.Length > 0)
            {
                _bindingLatches[key] = exact.ToHashSet();
                continue;
            }
            if (BindingChordLaw.Fallback(live) is { } fallback)
            {
                GameBinding[] retry = _bindings
                    .Where(candidate => candidate.Value.Contains(fallback))
                    .Select(candidate => candidate.Key).ToArray();
                if (retry.Length > 0) _bindingLatches[key] = retry.ToHashSet();
            }
        }
    }

    private bool BindingBaseDown(GameBinding binding)
    {
        BindingPair pair = BoundKeys(binding);
        return pair.Primary.IsBound && InputKeyDown(pair.Primary.Key) ||
               pair.Secondary.IsBound && InputKeyDown(pair.Secondary.Key);
    }

    /// <summary>The production key-state path, with protocol-held keys entering at the same seam.</summary>
    private bool InputKeyDown(Key key) => key != Key.Unknown &&
        (_window.IsDown(key) || _liveInputHeld.Contains(key));

    private float BindingAxis(GameBinding positive, GameBinding negative) =>
        (BindingDown(positive) ? 1f : 0f) - (BindingDown(negative) ? 1f : 0f);

    private GameBinding ActionBinding(int index) => (GameBinding)((int)GameBinding.Action1 + index);

    private static GameBinding MultiActionBinding(BottomMultiActionBar bar, int index) =>
        (GameBinding)((bar == BottomMultiActionBar.Left
            ? (int)GameBinding.MultiActionBar1Button1
            : (int)GameBinding.MultiActionBar2Button1) + Math.Clamp(index, 0, 11));

    private void UpdateTargetBinding(bool typing)
    {
        // Ctrl+Tab is the control-cycle chord (Program.Control.cs); with Ctrl held the
        // target binding must not also fire.
        bool ctrlHeld = InputKeyDown(Key.ControlLeft) || InputKeyDown(Key.ControlRight);
        bool down = BindingDown(GameBinding.TargetNearestEnemy) && !ctrlHeld;
        if (down && !_targetNearestWasDown && !typing && _net is { IsInWorld: true } && _controller is not null)
        {
            WorldEntity? nearest = _entities.Units
                .Where(x => x.Guid != ControlledGuid && !x.IsDead && CanAttack(x))
                .OrderBy(x => Vector3.DistanceSquared(x.Position, _controller.Position))
                .FirstOrDefault();
            if (nearest is not null) CommitSelection(nearest.Guid, beginAttack: false);
        }
        _targetNearestWasDown = down;
    }

    private void UpdateRunBinding(bool typing)
    {
        bool down = BindingDown(GameBinding.ToggleRun);
        if (down && !_toggleRunWasDown && !typing) _walkToggled = !_walkToggled;
        _toggleRunWasDown = down;
    }

    private void UpdateUiHideBinding(bool typing)
    {
        bool chordDown = BindingDown(GameBinding.ToggleUi);
        if (UiHideLaw.ToggleFired(chordDown, _toggleUiWasDown, typing))
        {
            _uiHidden = !_uiHidden;
            Console.WriteLine($"[ui] {(_uiHidden ? "hidden" : "shown")} (TOGGLEUI)");
        }
        _toggleUiWasDown = chordDown;
    }
}
