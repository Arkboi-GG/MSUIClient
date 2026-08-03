using System.Text.Json;
using System.Numerics;
using Silk.NET.Input;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private enum GameBinding
    {
        MoveForward, MoveBackward, TurnLeft, TurnRight, StrafeLeft, StrafeRight,
        Jump, ToggleRun, TargetNearestEnemy, OpenBackpack, OpenCharacter,
        OpenSpellbook, OpenWorldMap, Sheath, Action1, Action2, Action3, Action4,
        Action5, Action6, Action7, Action8, Action9, Action10, Action11, Action12,
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
        ("Interface", GameBinding.OpenSpellbook, "Spellbook", Key.P),
        ("Interface", GameBinding.OpenWorldMap, "World Map", Key.M),
        ("Interface", GameBinding.Sheath, "Sheath/Unsheath", Key.Z),
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
    ];

    private readonly record struct BindingPair(Key Primary, Key Secondary)
    {
        public bool Contains(Key key) => key != Key.Unknown && (Primary == key || Secondary == key);
        public BindingPair With(int slot, Key key) => slot == 2 ? this with { Secondary = key } : this with { Primary = key };
        public BindingPair Without(Key key) => new(Primary == key ? Key.Unknown : Primary,
            Secondary == key ? Key.Unknown : Secondary);
    }

    private readonly Dictionary<GameBinding, BindingPair> _bindings = [];
    private bool _bindingsLoaded;
    private bool _targetNearestWasDown;
    private bool _toggleRunWasDown;
    private bool _walkToggled;

    private void EnsureBindingsLoaded()
    {
        if (_bindingsLoaded) return;
        _bindingsLoaded = true;
        ResetBindingsToDefaults();
        string path = Path.Combine(_config.RepoRoot, "keybindings.json");
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
                        Key primary = keys.Length > 0 && Enum.TryParse(keys[0], out Key p) ? p : Key.Unknown;
                        Key secondary = keys.Length > 1 && Enum.TryParse(keys[1], out Key s) ? s : Key.Unknown;
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
                    if (Enum.TryParse(name, out GameBinding binding) && Enum.TryParse(keyName, out Key key))
                        _bindings[binding] = new(key, Key.Unknown);
            }
            catch (Exception ex) { Console.WriteLine($"[bindings] load failed: {ex.Message}"); }
        }
    }

    private void SaveBindings()
    {
        EnsureBindingsLoaded();
        string path = Path.Combine(_config.RepoRoot, "keybindings.json");
        var data = _bindings.ToDictionary(x => x.Key.ToString(), x =>
            new[] { x.Value.Primary.ToString(), x.Value.Secondary.ToString() });
        File.WriteAllText(path, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void ResetBindingsToDefaults()
    {
        _bindings.Clear();
        foreach (var row in BindingRows) _bindings[row.Binding] = new(row.Default, Key.Unknown);
    }

    private Key BoundKey(GameBinding binding)
    {
        EnsureBindingsLoaded();
        return _bindings.GetValueOrDefault(binding).Primary;
    }

    private BindingPair BoundKeys(GameBinding binding)
    {
        EnsureBindingsLoaded();
        return _bindings.GetValueOrDefault(binding);
    }

    private bool BindingDown(GameBinding binding)
    {
        BindingPair pair = BoundKeys(binding);
        return (pair.Primary != Key.Unknown && _window.IsDown(pair.Primary)) ||
               (pair.Secondary != Key.Unknown && _window.IsDown(pair.Secondary));
    }

    private float BindingAxis(GameBinding positive, GameBinding negative) =>
        (BindingDown(positive) ? 1f : 0f) - (BindingDown(negative) ? 1f : 0f);

    private GameBinding ActionBinding(int index) => (GameBinding)((int)GameBinding.Action1 + index);

    private void UpdateTargetBinding(bool typing)
    {
        bool down = BindingDown(GameBinding.TargetNearestEnemy);
        if (down && !_targetNearestWasDown && !typing && _net is { IsInWorld: true } && _controller is not null)
        {
            WorldEntity? nearest = _entities.Units
                .Where(x => x.Guid != _net.PlayerGuid && !x.IsDead && CanAttack(x))
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
}
