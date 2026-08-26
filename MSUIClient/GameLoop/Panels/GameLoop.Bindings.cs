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
        Jump, SitOrStand, ToggleRun, ToggleAutorun, FollowTarget, TargetNearestEnemy,
        TargetPreviousEnemy, TargetSelf, TargetPartyMember1, TargetPartyMember2,
        TargetPartyMember3, TargetPartyMember4, TargetPet, TargetPartyPet1,
        TargetPartyPet2, TargetPartyPet3, TargetPartyPet4, PetAttack,
        ToggleEnemyNameplates,
        ToggleFriendlyNameplates, ToggleAllNameplates, AttackTarget, OpenChat, OpenChatSlash,
        ChatPageUp, ChatPageDown, ChatBottom, Reply,
        CameraZoomIn, CameraZoomOut, MinimapZoomIn, MinimapZoomOut,
        ToggleMusic, ToggleSound, MasterVolumeUp, MasterVolumeDown,
        // OpenBags must stay OUTSIDE the ShapeshiftButton1..BonusActionButton10 run:
        // ResetBindingsToDefaults applies Control:true to that whole enum RANGE, so a member
        // landing inside it would silently default to Ctrl+I instead of I.
        OpenBackpack, OpenBags, OpenCharacter, OpenSkills,
        OpenSpellbook, OpenPetSpellbook, OpenTalents, OpenQuestLog, OpenSocial,
        OpenSocialFriends, OpenSocialWho, OpenSocialGuild, OpenWorldMap, ToggleMinimap,
        Sheath, ToggleUi,
        Screenshot,
        Action1, Action2, Action3, Action4,
        Action5, Action6, Action7, Action8, Action9, Action10, Action11, Action12,
        ShapeshiftButton1, ShapeshiftButton2, ShapeshiftButton3, ShapeshiftButton4,
        ShapeshiftButton5, ShapeshiftButton6, ShapeshiftButton7, ShapeshiftButton8,
        ShapeshiftButton9, ShapeshiftButton10,
        BonusActionButton1, BonusActionButton2, BonusActionButton3, BonusActionButton4,
        BonusActionButton5, BonusActionButton6, BonusActionButton7, BonusActionButton8,
        BonusActionButton9, BonusActionButton10, ToggleActionBarLock,
        MultiActionBar1Button1, MultiActionBar1Button2, MultiActionBar1Button3,
        MultiActionBar1Button4, MultiActionBar1Button5, MultiActionBar1Button6,
        MultiActionBar1Button7, MultiActionBar1Button8, MultiActionBar1Button9,
        MultiActionBar1Button10, MultiActionBar1Button11, MultiActionBar1Button12,
        MultiActionBar2Button1, MultiActionBar2Button2, MultiActionBar2Button3,
        MultiActionBar2Button4, MultiActionBar2Button5, MultiActionBar2Button6,
        MultiActionBar2Button7, MultiActionBar2Button8, MultiActionBar2Button9,
        MultiActionBar2Button10, MultiActionBar2Button11, MultiActionBar2Button12,
        RaidTarget1, RaidTarget2, RaidTarget3, RaidTarget4, RaidTarget5,
        RaidTarget6, RaidTarget7, RaidTarget8, RaidTargetNone,
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
        ("Movement", GameBinding.SitOrStand, "Sit/Stand", Key.X),
        ("Movement", GameBinding.ToggleRun, "Run/Walk", Key.KeypadDivide),
        ("Movement", GameBinding.Sheath, "Sheath/Unsheath", Key.Z),
        ("Movement", GameBinding.ToggleAutorun, "Auto Run", Key.NumLock),
        ("Movement", GameBinding.FollowTarget, "Follow Target", Key.Unknown),
        ("Chat", GameBinding.OpenChat, "Open Chat", Key.Enter),
        ("Chat", GameBinding.OpenChatSlash, "Open Chat Slash", Key.Slash),
        ("Chat", GameBinding.ChatPageUp, "Chat Page Up", Key.PageUp),
        ("Chat", GameBinding.ChatPageDown, "Chat Page Down", Key.PageDown),
        ("Chat", GameBinding.ChatBottom, "Chat Bottom", Key.PageDown),
        ("Chat", GameBinding.Reply, "Reply", Key.R),
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
        ("Action Bar", GameBinding.ShapeshiftButton1, "Shapeshift Button 1", Key.F1),
        ("Action Bar", GameBinding.ShapeshiftButton2, "Shapeshift Button 2", Key.F2),
        ("Action Bar", GameBinding.ShapeshiftButton3, "Shapeshift Button 3", Key.F3),
        ("Action Bar", GameBinding.ShapeshiftButton4, "Shapeshift Button 4", Key.F4),
        ("Action Bar", GameBinding.ShapeshiftButton5, "Shapeshift Button 5", Key.F5),
        ("Action Bar", GameBinding.ShapeshiftButton6, "Shapeshift Button 6", Key.F6),
        ("Action Bar", GameBinding.ShapeshiftButton7, "Shapeshift Button 7", Key.F7),
        ("Action Bar", GameBinding.ShapeshiftButton8, "Shapeshift Button 8", Key.F8),
        ("Action Bar", GameBinding.ShapeshiftButton9, "Shapeshift Button 9", Key.F9),
        ("Action Bar", GameBinding.ShapeshiftButton10, "Shapeshift Button 10", Key.F10),
        ("Action Bar", GameBinding.BonusActionButton1, "Secondary Action Button 1", Key.Number1),
        ("Action Bar", GameBinding.BonusActionButton2, "Secondary Action Button 2", Key.Number2),
        ("Action Bar", GameBinding.BonusActionButton3, "Secondary Action Button 3", Key.Number3),
        ("Action Bar", GameBinding.BonusActionButton4, "Secondary Action Button 4", Key.Number4),
        ("Action Bar", GameBinding.BonusActionButton5, "Secondary Action Button 5", Key.Number5),
        ("Action Bar", GameBinding.BonusActionButton6, "Secondary Action Button 6", Key.Number6),
        ("Action Bar", GameBinding.BonusActionButton7, "Secondary Action Button 7", Key.Number7),
        ("Action Bar", GameBinding.BonusActionButton8, "Secondary Action Button 8", Key.Number8),
        ("Action Bar", GameBinding.BonusActionButton9, "Secondary Action Button 9", Key.Number9),
        ("Action Bar", GameBinding.BonusActionButton10, "Secondary Action Button 10", Key.Number0),
        ("Action Bar", GameBinding.ToggleActionBarLock, "Toggle Action Bar Lock", Key.Unknown),
        ("Targeting", GameBinding.TargetNearestEnemy, "Target Nearest Enemy", Key.Tab),
        ("Targeting", GameBinding.TargetPreviousEnemy, "Target Previous Enemy", Key.Tab),
        ("Targeting", GameBinding.TargetSelf, "Target Self", Key.F1),
        ("Targeting", GameBinding.TargetPartyMember1, "Target Party Member 1", Key.F2),
        ("Targeting", GameBinding.TargetPartyMember2, "Target Party Member 2", Key.F3),
        ("Targeting", GameBinding.TargetPartyMember3, "Target Party Member 3", Key.F4),
        ("Targeting", GameBinding.TargetPartyMember4, "Target Party Member 4", Key.F5),
        ("Targeting", GameBinding.TargetPet, "Target Pet", Key.F1),
        ("Targeting", GameBinding.TargetPartyPet1, "Target Party Pet 1", Key.F2),
        ("Targeting", GameBinding.TargetPartyPet2, "Target Party Pet 2", Key.F3),
        ("Targeting", GameBinding.TargetPartyPet3, "Target Party Pet 3", Key.F4),
        ("Targeting", GameBinding.TargetPartyPet4, "Target Party Pet 4", Key.F5),
        ("Targeting", GameBinding.ToggleEnemyNameplates, "Show Enemy Name Plates", Key.V),
        ("Targeting", GameBinding.ToggleFriendlyNameplates, "Show Friendly Name Plates", Key.V),
        ("Targeting", GameBinding.ToggleAllNameplates, "Show All Name Plates", Key.V),
        ("Targeting", GameBinding.AttackTarget, "Attack Target", Key.T),
        ("Targeting", GameBinding.PetAttack, "Pet Attack", Key.T),
        ("Interface", GameBinding.OpenCharacter, "Character Info", Key.C),
        ("Interface", GameBinding.OpenBackpack, "Open Backpack (Shift: all bags)", Key.B),
        ("Interface", GameBinding.OpenBags, "Inventory (party bags in Free View)", Key.I),
        ("Interface", GameBinding.OpenSkills, SkillFrameUiLaw.BindingLabel, Key.K),
        ("Interface", GameBinding.OpenPetSpellbook, "Pet Spellbook", Key.P),
        ("Interface", GameBinding.OpenSpellbook, "Spellbook", Key.P),
        ("Interface", GameBinding.OpenTalents, "Talents", Key.N),
        ("Interface", GameBinding.OpenQuestLog, "Quest Log", Key.L),
        ("Interface", GameBinding.ToggleMinimap, "Toggle Minimap", Key.Unknown),
        ("Interface", GameBinding.OpenWorldMap, "World Map", Key.M),
        ("Interface", GameBinding.OpenSocial, "Social", Key.O),
        ("Interface", GameBinding.OpenSocialFriends, "Toggle Friends Pane", Key.Unknown),
        ("Interface", GameBinding.OpenSocialWho, "Toggle Who Pane", Key.Unknown),
        ("Interface", GameBinding.OpenSocialGuild, "Toggle Guild Pane", Key.Unknown),
        ("Miscellaneous", GameBinding.MinimapZoomIn, "Minimap Zoom In", Key.KeypadAdd),
        ("Miscellaneous", GameBinding.MinimapZoomOut, "Minimap Zoom Out", Key.KeypadSubtract),
        ("Miscellaneous", GameBinding.ToggleMusic, "Toggle Music", Key.M),
        ("Miscellaneous", GameBinding.ToggleSound, "Toggle Sound", Key.S),
        ("Miscellaneous", GameBinding.MasterVolumeUp, "Master Volume Up", Key.Equal),
        ("Miscellaneous", GameBinding.MasterVolumeDown, "Master Volume Down", Key.Minus),
        ("Miscellaneous", GameBinding.ToggleUi, "Toggle User Interface", Key.Z),
        ("Miscellaneous", GameBinding.Screenshot, "Screen Shot", Key.PrintScreen),
        ("Camera", GameBinding.CameraZoomIn, "Zoom In", Key.Unknown),
        ("Camera", GameBinding.CameraZoomOut, "Zoom Out", Key.Unknown),
        ("MultiActionBar", GameBinding.MultiActionBar1Button1, "Action Button 1", Key.Unknown),
        ("MultiActionBar", GameBinding.MultiActionBar1Button2, "Action Button 2", Key.Unknown),
        ("MultiActionBar", GameBinding.MultiActionBar1Button3, "Action Button 3", Key.Unknown),
        ("MultiActionBar", GameBinding.MultiActionBar1Button4, "Action Button 4", Key.Unknown),
        ("MultiActionBar", GameBinding.MultiActionBar1Button5, "Action Button 5", Key.Unknown),
        ("MultiActionBar", GameBinding.MultiActionBar1Button6, "Action Button 6", Key.Unknown),
        ("MultiActionBar", GameBinding.MultiActionBar1Button7, "Action Button 7", Key.Unknown),
        ("MultiActionBar", GameBinding.MultiActionBar1Button8, "Action Button 8", Key.Unknown),
        ("MultiActionBar", GameBinding.MultiActionBar1Button9, "Action Button 9", Key.Unknown),
        ("MultiActionBar", GameBinding.MultiActionBar1Button10, "Action Button 10", Key.Unknown),
        ("MultiActionBar", GameBinding.MultiActionBar1Button11, "Action Button 11", Key.Unknown),
        ("MultiActionBar", GameBinding.MultiActionBar1Button12, "Action Button 12", Key.Unknown),
        ("MultiActionBar", GameBinding.MultiActionBar2Button1, "Action Button 1", Key.Unknown),
        ("MultiActionBar", GameBinding.MultiActionBar2Button2, "Action Button 2", Key.Unknown),
        ("MultiActionBar", GameBinding.MultiActionBar2Button3, "Action Button 3", Key.Unknown),
        ("MultiActionBar", GameBinding.MultiActionBar2Button4, "Action Button 4", Key.Unknown),
        ("MultiActionBar", GameBinding.MultiActionBar2Button5, "Action Button 5", Key.Unknown),
        ("MultiActionBar", GameBinding.MultiActionBar2Button6, "Action Button 6", Key.Unknown),
        ("MultiActionBar", GameBinding.MultiActionBar2Button7, "Action Button 7", Key.Unknown),
        ("MultiActionBar", GameBinding.MultiActionBar2Button8, "Action Button 8", Key.Unknown),
        ("MultiActionBar", GameBinding.MultiActionBar2Button9, "Action Button 9", Key.Unknown),
        ("MultiActionBar", GameBinding.MultiActionBar2Button10, "Action Button 10", Key.Unknown),
        ("MultiActionBar", GameBinding.MultiActionBar2Button11, "Action Button 11", Key.Unknown),
        ("MultiActionBar", GameBinding.MultiActionBar2Button12, "Action Button 12", Key.Unknown),
        ("Raid Targeting", GameBinding.RaidTarget1, "Assign Star to Target", Key.Unknown),
        ("Raid Targeting", GameBinding.RaidTarget2, "Assign Circle to Target", Key.Unknown),
        ("Raid Targeting", GameBinding.RaidTarget3, "Assign Diamond to Target", Key.Unknown),
        ("Raid Targeting", GameBinding.RaidTarget4, "Assign Triangle to Target", Key.Unknown),
        ("Raid Targeting", GameBinding.RaidTarget5, "Assign Moon to Target", Key.Unknown),
        ("Raid Targeting", GameBinding.RaidTarget6, "Assign Square to Target", Key.Unknown),
        ("Raid Targeting", GameBinding.RaidTarget7, "Assign Cross to Target", Key.Unknown),
        ("Raid Targeting", GameBinding.RaidTarget8, "Assign Skull to Target", Key.Unknown),
        ("Raid Targeting", GameBinding.RaidTargetNone, "Clear Raid Target Icon", Key.Unknown),
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
    private bool _targetPreviousWasDown;
    private readonly TargetCycleHistory _targetCycleHistory = new();
    private bool _attackTargetBindingWasDown;
    private readonly HashSet<GameBinding> _directTargetBindingsDown = [];
    private bool _toggleRunWasDown;
    private bool _sitOrStandWasDown;
    private bool _walkToggled;
    private bool _toggleAutorunWasDown;
    private bool _followTargetWasDown;
    private bool _autorunToggled;
    private bool _autorunForwardWasDown;
    private bool _autorunBackwardWasDown;
    private bool _autorunBothButtonsWereDown;
    private bool _openChatWasDown;
    private bool _openChatSlashWasDown;
    private bool _replyWasDown;
    private bool _chatPageUpWasDown;
    private bool _chatPageDownWasDown;
    private bool _chatBottomWasDown;
    private bool _cameraZoomInWasDown;
    private bool _cameraZoomOutWasDown;
    private bool _minimapZoomInWasDown;
    private bool _minimapZoomOutWasDown;
    private bool _toggleMinimapWasDown;
    private bool _toggleMusicWasDown;
    private bool _toggleSoundWasDown;
    private bool _masterVolumeUpWasDown;
    private bool _masterVolumeDownWasDown;
    private bool _toggleUiWasDown;
    private bool _uiHidden;
    private readonly Dictionary<Key, HashSet<GameBinding>> _bindingLatches = [];
    private readonly HashSet<Key> _bindingPhysicalDown = [];
    private readonly Dictionary<BindingPointerKey, HashSet<GameBinding>>
        _bindingPointerLatches = [];
    private readonly HashSet<BindingPointerKey> _bindingPointerPhysicalDown = [];
    private readonly HashSet<GameBinding> _bindingPointerPulse = [];

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
                Alt: row.Binding == GameBinding.ToggleUi,
                Control: row.Binding is >= GameBinding.ShapeshiftButton1 and
                    <= GameBinding.BonusActionButton10,
                Shift: row.Binding == GameBinding.OpenPetSpellbook), default);
        _bindings[GameBinding.ToggleAutorun] = new(
            new BindingChord(Key.NumLock),
            BindingChordLaw.LivePointer(BindingPointerKey.Button4, false, false, false));
        _bindings[GameBinding.MoveForward] = new(new BindingChord(Key.W),
            new BindingChord(Key.Up));
        _bindings[GameBinding.MoveBackward] = new(new BindingChord(Key.S),
            new BindingChord(Key.Down));
        _bindings[GameBinding.TurnLeft] = new(new BindingChord(Key.A),
            new BindingChord(Key.Left));
        _bindings[GameBinding.TurnRight] = new(new BindingChord(Key.D),
            new BindingChord(Key.Right));
        _bindings[GameBinding.Jump] = new(new BindingChord(Key.Space),
            new BindingChord(Key.Keypad0));
        _bindings[GameBinding.CameraZoomIn] = new(
            BindingChordLaw.LivePointer(BindingPointerKey.WheelUp, false, false, false), default);
        _bindings[GameBinding.CameraZoomOut] = new(
            BindingChordLaw.LivePointer(BindingPointerKey.WheelDown, false, false, false), default);
        _bindings[GameBinding.ToggleFriendlyNameplates] = new(
            new BindingChord(Key.V, Shift: true), default);
        _bindings[GameBinding.ToggleAllNameplates] = new(
            new BindingChord(Key.V, Control: true), default);
        _bindings[GameBinding.ChatBottom] = new(
            new BindingChord(Key.PageDown, Shift: true), default);
        _bindings[GameBinding.TargetPreviousEnemy] = new(
            new BindingChord(Key.Tab, Shift: true), default);
        _bindings[GameBinding.TargetPet] = new(
            new BindingChord(Key.F1, Shift: true), default);
        _bindings[GameBinding.TargetPartyPet1] = new(
            new BindingChord(Key.F2, Shift: true), default);
        _bindings[GameBinding.TargetPartyPet2] = new(
            new BindingChord(Key.F3, Shift: true), default);
        _bindings[GameBinding.TargetPartyPet3] = new(
            new BindingChord(Key.F4, Shift: true), default);
        _bindings[GameBinding.TargetPartyPet4] = new(
            new BindingChord(Key.F5, Shift: true), default);
        _bindings[GameBinding.PetAttack] = new(
            new BindingChord(Key.T, Shift: true), default);
        _bindings[GameBinding.ToggleMusic] = new(
            new BindingChord(Key.M, Control: true), default);
        _bindings[GameBinding.ToggleSound] = new(
            new BindingChord(Key.S, Control: true), default);
        _bindings[GameBinding.MasterVolumeUp] = new(
            new BindingChord(Key.Equal, Control: true), default);
        _bindings[GameBinding.MasterVolumeDown] = new(
            new BindingChord(Key.Minus, Control: true), default);
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
        return _bindingLatches.Values.Any(active => active.Contains(binding)) ||
            _bindingPointerLatches.Values.Any(active => active.Contains(binding)) ||
            _bindingPointerPulse.Contains(binding);
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
        bool pointerBlocked = typing || ImGuiNET.ImGui.GetIO().WantCaptureMouse;
        _bindingPointerPulse.Clear();
        if (typing) _bindingLatches.Clear();
        if (pointerBlocked) _bindingPointerLatches.Clear();
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

            HashSet<GameBinding> resolved = ResolveBindingChord(
                BindingChordLaw.Live(key, alt, control, shift));
            if (resolved.Count > 0) _bindingLatches[key] = resolved;
        }

        foreach (BindingPointerKey pointer in new[]
        {
            BindingPointerKey.Button3,
            BindingPointerKey.Button4,
            BindingPointerKey.Button5,
        })
        {
            bool down = PointerInputDown(pointer);
            bool wasDown = _bindingPointerPhysicalDown.Contains(pointer);
            if (!down)
            {
                _bindingPointerPhysicalDown.Remove(pointer);
                _bindingPointerLatches.Remove(pointer);
                continue;
            }
            _bindingPointerPhysicalDown.Add(pointer);
            if (wasDown || pointerBlocked || super) continue;
            HashSet<GameBinding> resolved = ResolveBindingChord(
                BindingChordLaw.LivePointer(pointer, alt, control, shift));
            if (resolved.Count > 0) _bindingPointerLatches[pointer] = resolved;
        }

        if (!pointerBlocked && !super && _window.BindingWheelDelta != 0)
        {
            BindingPointerKey wheel = _window.BindingWheelDelta > 0
                ? BindingPointerKey.WheelUp : BindingPointerKey.WheelDown;
            _bindingPointerPulse.UnionWith(ResolveBindingChord(
                BindingChordLaw.LivePointer(wheel, alt, control, shift)));
        }
    }

    private HashSet<GameBinding> ResolveBindingChord(BindingChord live)
    {
        GameBinding[] exact = _bindings
            .Where(candidate => candidate.Value.Contains(live))
            .Select(candidate => candidate.Key).ToArray();
        if (exact.Length > 0) return exact.ToHashSet();
        if (BindingChordLaw.Fallback(live) is not { } fallback) return [];
        return _bindings.Where(candidate => candidate.Value.Contains(fallback))
            .Select(candidate => candidate.Key).ToHashSet();
    }

    private bool BindingBaseDown(GameBinding binding)
    {
        BindingPair pair = BoundKeys(binding);
        return BindingInputDown(pair.Primary) || BindingInputDown(pair.Secondary);
    }

    private bool BindingInputDown(in BindingChord chord) => chord.IsBound &&
        (chord.Pointer == BindingPointerKey.None
            ? InputKeyDown(chord.Key)
            : PointerInputDown(chord.Pointer));

    private bool PointerInputDown(BindingPointerKey pointer) => pointer switch
    {
        BindingPointerKey.Button3 => _window.MouseMiddleDown,
        BindingPointerKey.Button4 => _window.MouseButton4Down,
        BindingPointerKey.Button5 => _window.MouseButton5Down,
        BindingPointerKey.WheelUp => _window.BindingWheelDelta > 0,
        BindingPointerKey.WheelDown => _window.BindingWheelDelta < 0,
        _ => false,
    };

    /// <summary>The production key-state path, with protocol-held keys entering at the same seam.</summary>
    private bool InputKeyDown(Key key) => key != Key.Unknown &&
        (_window.IsDown(key) || _liveInputHeld.Contains(key));

    private float BindingAxis(GameBinding positive, GameBinding negative) =>
        (BindingDown(positive) ? 1f : 0f) - (BindingDown(negative) ? 1f : 0f);

    private GameBinding ActionBinding(int index) => (GameBinding)((int)GameBinding.Action1 + index);

    private static GameBinding ShapeshiftBinding(int index) =>
        (GameBinding)((int)GameBinding.ShapeshiftButton1 + Math.Clamp(index, 0, 9));

    private static GameBinding BonusActionBinding(int index) =>
        (GameBinding)((int)GameBinding.BonusActionButton1 + Math.Clamp(index, 0, 9));

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
        bool previous = BindingDown(GameBinding.TargetPreviousEnemy) && !ctrlHeld;
        if (!typing && _net is { IsInWorld: true } && _controller is not null)
        {
            if (previous && !_targetPreviousWasDown) CycleEnemyTarget(reverse: true);
            else if (down && !_targetNearestWasDown) CycleEnemyTarget(reverse: false);
        }
        _targetNearestWasDown = down;
        _targetPreviousWasDown = previous;

        bool attack = BindingDown(GameBinding.AttackTarget);
        if (attack && !_attackTargetBindingWasDown && !typing &&
            _net is { IsInWorld: true } && _controller is not null)
        {
            bool engaged = _attackTargetGuid != 0 || _combat.IsEngaged(ControlledGuid);
            if (engaged)
                StopAttack("attack-target-toggle");
            else if (_selectionGuid != 0 &&
                _entities.TryGet(_selectionGuid, out WorldEntity selected) && CanAttack(selected))
                CommitSelection(_selectionGuid, beginAttack: true);
            else if (_selectionGuid == 0 && NearestAttackableUnit() is { } nearest)
                CommitSelection(nearest.Guid, beginAttack: true);
        }
        _attackTargetBindingWasDown = attack;
    }

    private void UpdateDirectTargetBindings(bool typing)
    {
        GameBinding[] commands =
        [
            GameBinding.TargetSelf,
            GameBinding.TargetPartyMember1, GameBinding.TargetPartyMember2,
            GameBinding.TargetPartyMember3, GameBinding.TargetPartyMember4,
            GameBinding.TargetPet,
            GameBinding.TargetPartyPet1, GameBinding.TargetPartyPet2,
            GameBinding.TargetPartyPet3, GameBinding.TargetPartyPet4,
            GameBinding.PetAttack,
            GameBinding.RaidTarget1, GameBinding.RaidTarget2,
            GameBinding.RaidTarget3, GameBinding.RaidTarget4,
            GameBinding.RaidTarget5, GameBinding.RaidTarget6,
            GameBinding.RaidTarget7, GameBinding.RaidTarget8,
            GameBinding.RaidTargetNone,
        ];

        foreach (GameBinding command in commands)
        {
            bool down = BindingDown(command);
            if (down && !_directTargetBindingsDown.Contains(command) && !typing)
                FireDirectTargetBinding(command);
            if (down) _directTargetBindingsDown.Add(command);
            else _directTargetBindingsDown.Remove(command);
        }
    }

    private void FireDirectTargetBinding(GameBinding command)
    {
        if (_net is not { IsInWorld: true }) return;

        if (command == GameBinding.PetAttack)
        {
            int attackSlot = Array.FindIndex(_petActions, packed =>
                PetActionBarUiLaw.Kind(packed) == 7 &&
                PetActionBarUiLaw.Action(packed) == 2);
            if (attackSlot >= 0)
            {
                WorldEntity? pet = _entities.TryGet(_petGuid, out WorldEntity entity) &&
                    entity.IsUnit ? entity : null;
                UsePetAction(attackSlot, _petGuid, pet);
            }
            return;
        }

        if (command is >= GameBinding.RaidTarget1 and <= GameBinding.RaidTargetNone)
        {
            byte requested = command == GameBinding.RaidTargetNone ? (byte)0 :
                checked((byte)((int)command - (int)GameBinding.RaidTarget1 + 1));
            RaidMarkerIntent intent = TargetBindingLaw.ResolveRaidMarker(
                _partyRaidTargets, _selectionGuid, requested);
            if (intent.Send && !TryPartyTestRaidTarget(_selectionGuid, requested))
                _net.SetRaidTarget(intent.WireIcon, intent.Guid);
            return;
        }

        ulong self = ControlledGuid;
        ulong selfPet = _petGuid;
        ulong member = 0;
        ulong memberPet = 0;
        int partyIndex = command switch
        {
            GameBinding.TargetPartyMember1 or GameBinding.TargetPartyPet1 => 0,
            GameBinding.TargetPartyMember2 or GameBinding.TargetPartyPet2 => 1,
            GameBinding.TargetPartyMember3 or GameBinding.TargetPartyPet3 => 2,
            GameBinding.TargetPartyMember4 or GameBinding.TargetPartyPet4 => 3,
            _ => -1,
        };
        if (partyIndex >= 0)
        {
            member = PartyFrameMemberGuid(partyIndex);
            if (member != 0 && _entities.TryGet(member, out WorldEntity partyUnit))
                memberPet = partyUnit.Fields.Summon ?? partyUnit.Fields.Charm ?? 0;
        }

        ulong? target = command switch
        {
            GameBinding.TargetSelf =>
                TargetBindingLaw.ResolveToggle(_selectionGuid, self, selfPet),
            >= GameBinding.TargetPartyMember1 and <= GameBinding.TargetPartyMember4 =>
                TargetBindingLaw.ResolveToggle(_selectionGuid, member, memberPet),
            GameBinding.TargetPet => TargetBindingLaw.ResolveDirect(selfPet),
            >= GameBinding.TargetPartyPet1 and <= GameBinding.TargetPartyPet4 =>
                TargetBindingLaw.ResolveDirect(memberPet),
            _ => null,
        };
        if (target.HasValue) CommitSelection(target.Value, beginAttack: false);
    }

    private WorldEntity? NearestAttackableUnit()
    {
        if (!TryGetControlledBodyPose(out WorldBodyPose body)) return null;
        return _entities.Units
            .Where(x => x.Guid != ControlledGuid && !x.IsDead && CanAttack(x))
            .OrderBy(x => Vector3.DistanceSquared(x.Position, body.Position))
            .FirstOrDefault();
    }

    private void CycleEnemyTarget(bool reverse)
    {
        if (!TryGetControlledBodyPose(out WorldBodyPose body)) return;
        Vector2 viewport = ImGuiNET.ImGui.GetIO().DisplaySize;
        List<TargetCycleLaw.Candidate> candidates = [];
        foreach (WorldEntity unit in _entities.Units)
        {
            if (unit.Guid == ControlledGuid || unit.Fields.ReadsDead || !CanAttack(unit)) continue;
            float distance = Vector3.Distance(unit.Position, body.Position);
            if (distance > TargetCycleLaw.Range) continue;
            if (unit.IsCreature &&
                _creatureQueryRecords.TryGetValue(unit.Entry, out CreatureQueryInfo? query) &&
                query?.CreatureType == 8)
                continue;

            float? offCenter = null;
            if (_window.Camera.TryProjectToScreen(unit.Position + new Vector3(0f, 0f, 1f),
                    viewport, out Vector2 pixel, out _))
                offCenter = TargetCycleLaw.ScreenOffCenter(pixel, viewport);
            bool combatWithMe = unit.Fields.Target == ControlledGuid && unit.Fields.InCombat;
            candidates.Add(new TargetCycleLaw.Candidate(unit.Guid, offCenter.HasValue,
                TargetCycleLaw.PriorityScore(offCenter, distance, combatWithMe)));
        }

        List<TargetCycleLaw.Candidate> pool = TargetCycleLaw.SortedPool(candidates);
        double now = NowSeconds();
        _targetCycleHistory.Prune(now);
        TargetCycleLaw.Pick? pick = TargetCycleLaw.Select(
            pool, _targetCycleHistory.Guids, _selectionGuid, reverse);
        if (pick is null) return;
        if (pick.Value.Wrapped) _targetCycleHistory.Clear();
        ulong outgoing = _selectionGuid;
        if (outgoing != 0) _targetCycleHistory.Push(outgoing, now);
        CommitSelection(pick.Value.Guid, beginAttack: false);
        if (_selectionGuid == pick.Value.Guid && pick.Value.Guid != outgoing)
            _targetCycleHistory.Push(pick.Value.Guid, now);
    }

    private void UpdateRunBinding(bool typing)
    {
        bool down = BindingDown(GameBinding.ToggleRun);
        if (down && !_toggleRunWasDown && !typing) _walkToggled = !_walkToggled;
        _toggleRunWasDown = down;
    }

    private void UpdateStandStateBinding(bool typing)
    {
        bool down = BindingDown(GameBinding.SitOrStand);
        if (down && !_sitOrStandWasDown && !typing &&
            _entities.TryGet(LocalPlayerGuid, out WorldEntity self))
            TrySetLocalStandState(self.Fields.UnitStandState == StandStateUiLaw.Stand
                ? StandStateUiLaw.Sit : StandStateUiLaw.Stand);
        _sitOrStandWasDown = down;
    }

    private void UpdateAutorunBinding(bool typing)
    {
        bool toggle = BindingDown(GameBinding.ToggleAutorun);
        if (toggle && !_toggleAutorunWasDown && !typing)
            _autorunToggled = !_autorunToggled;
        _toggleAutorunWasDown = toggle;

        bool forward = BindingDown(GameBinding.MoveForward);
        bool backward = BindingDown(GameBinding.MoveBackward);
        bool bothButtons = _window.MouseLeftDown && _window.MouseRightDown;
        bool lostMover = _movementRooted || _iceBlockFrozen ||
            _taxiOpen && _taxiLocked || _controller is null;
        if (_autorunToggled && BindingCommandLaw.AutorunCancelled(
                forward && !_autorunForwardWasDown,
                backward && !_autorunBackwardWasDown,
                bothButtons && !_autorunBothButtonsWereDown,
                lostMover))
            _autorunToggled = false;
        _autorunForwardWasDown = forward;
        _autorunBackwardWasDown = backward;
        _autorunBothButtonsWereDown = bothButtons;
    }

    private void UpdateFollowTargetBinding(bool typing)
    {
        bool down = BindingDown(GameBinding.FollowTarget);
        if (down && !_followTargetWasDown && !typing && _selectionGuid != 0 &&
            _entities.TryGet(_selectionGuid, out WorldEntity target))
            StartAutoFollow(_selectionGuid, AutoFollowTargetName(_selectionGuid, target));
        _followTargetWasDown = down;
    }

    private void UpdateChatBindings(bool typing)
    {
        bool open = BindingDown(GameBinding.OpenChat);
        bool slash = BindingDown(GameBinding.OpenChatSlash);
        bool reply = BindingDown(GameBinding.Reply);
        bool pageUp = BindingDown(GameBinding.ChatPageUp);
        bool pageDown = BindingDown(GameBinding.ChatPageDown);
        bool bottom = BindingDown(GameBinding.ChatBottom);
        if (!_chatEditOpen && !typing)
        {
            if (open && !_openChatWasDown) OpenChatEdit();
            else if (slash && !_openChatSlashWasDown) OpenChatEditWith("/");
            else if (reply && !_replyWasDown) OpenChatEditWith("/r ");
            if (pageUp && !_chatPageUpWasDown)
                _chatScroll = ChatFrameLaw.PageUpOffset(_chatScroll, ChatVisibleLineCount());
            if (pageDown && !_chatPageDownWasDown)
                _chatScroll = ChatFrameLaw.PageDownOffset(_chatScroll, ChatVisibleLineCount());
            if (bottom && !_chatBottomWasDown) _chatScroll = 0;
        }
        _openChatWasDown = open;
        _openChatSlashWasDown = slash;
        _replyWasDown = reply;
        _chatPageUpWasDown = pageUp;
        _chatPageDownWasDown = pageDown;
        _chatBottomWasDown = bottom;
    }

    private int ChatVisibleLineCount()
    {
        float scale = GameplayUiScale();
        float pitch = GameText.LinePitch(ChatFrameLaw.ChatFont, scale);
        return Math.Max(1, (int)(ChatFrameLaw.FrameHeight * scale / MathF.Max(1f, pitch)));
    }

    private void UpdateCameraZoomBindings(bool typing)
    {
        bool zoomIn = BindingDown(GameBinding.CameraZoomIn);
        bool zoomOut = BindingDown(GameBinding.CameraZoomOut);
        if (!typing && !_window.FreeSelectMode && !_scopedViewActive)
        {
            float zoomInAmount = _bindingPointerPulse.Contains(GameBinding.CameraZoomIn)
                ? MathF.Max(1f, MathF.Abs(_window.BindingWheelDelta)) : 1f;
            float zoomOutAmount = _bindingPointerPulse.Contains(GameBinding.CameraZoomOut)
                ? MathF.Max(1f, MathF.Abs(_window.BindingWheelDelta)) : 1f;
            if (zoomIn && (!_cameraZoomInWasDown ||
                    _bindingPointerPulse.Contains(GameBinding.CameraZoomIn)))
                _window.Camera.Zoom(zoomInAmount);
            if (zoomOut && (!_cameraZoomOutWasDown ||
                    _bindingPointerPulse.Contains(GameBinding.CameraZoomOut)))
                _window.Camera.Zoom(-zoomOutAmount);
        }
        _cameraZoomInWasDown = zoomIn;
        _cameraZoomOutWasDown = zoomOut;
    }

    private void UpdateMinimapZoomBindings(bool typing)
    {
        bool zoomIn = BindingDown(GameBinding.MinimapZoomIn);
        bool zoomOut = BindingDown(GameBinding.MinimapZoomOut);
        bool insideWmo = _minimapAreaInterior is not null;
        if (!typing)
        {
            if (zoomIn && !_minimapZoomInWasDown) StepMinimapZoom(zoomIn: true, insideWmo);
            if (zoomOut && !_minimapZoomOutWasDown) StepMinimapZoom(zoomIn: false, insideWmo);
        }
        _minimapZoomInWasDown = zoomIn;
        _minimapZoomOutWasDown = zoomOut;
    }

    private void UpdateMinimapVisibilityBinding(bool typing)
    {
        bool down = BindingDown(GameBinding.ToggleMinimap);
        if (down && !_toggleMinimapWasDown && !typing)
            SetMinimapVisible(!_minimapVisible);
        _toggleMinimapWasDown = down;
    }

    private void UpdateAudioBindings(bool typing)
    {
        bool music = BindingDown(GameBinding.ToggleMusic);
        bool sound = BindingDown(GameBinding.ToggleSound);
        bool volumeUp = BindingDown(GameBinding.MasterVolumeUp);
        bool volumeDown = BindingDown(GameBinding.MasterVolumeDown);
        bool changed = false;
        if (!typing)
        {
            if (music && !_toggleMusicWasDown)
            {
                Settings.Audio.EnableMusic = !Settings.Audio.EnableMusic;
                changed = true;
            }
            if (sound && !_toggleSoundWasDown)
            {
                Settings.Audio.EnableAll = !Settings.Audio.EnableAll;
                changed = true;
            }
            if (volumeUp && !_masterVolumeUpWasDown)
            {
                Settings.Audio.MasterVolume = BindingCommandLaw.StepMasterVolume(
                    Settings.Audio.MasterVolume, 1);
                changed = true;
            }
            if (volumeDown && !_masterVolumeDownWasDown)
            {
                Settings.Audio.MasterVolume = BindingCommandLaw.StepMasterVolume(
                    Settings.Audio.MasterVolume, -1);
                changed = true;
            }
        }
        _toggleMusicWasDown = music;
        _toggleSoundWasDown = sound;
        _masterVolumeUpWasDown = volumeUp;
        _masterVolumeDownWasDown = volumeDown;
        if (!changed) return;
        Settings.ActivePreset = "Custom";
        ApplyAudioSettings(Settings);
        SettingsFile?.Save();
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
