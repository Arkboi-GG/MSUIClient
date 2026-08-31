using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// Creator Mode - the offline sandbox (spells, characters, gear, world tools).
//
// The launch choice lives on the login screen (Launch Options): "SuperUI Client
// Mode" is the networked client, "Creator Mode" boots the offline world
// with the full presentation stack (spell FX, creatures, gameplay UI) and no
// socket. The choice is sticky via GameSettings.LaunchMode. Batch instruments
// (portrait/variant/movement/live-run) always ignore it.
//
// Creator-mode UI deliberately replaces the developer overlay ("MSUI Client"
// window + inspectors): BuildGui returns before the DevTools stack once the
// creator world is entered, and the creator's own menus draw from NetHud.
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    private const string LaunchModeClient = "Client";
    private const string LaunchModeCreator = "Creator";

    /// <summary>Synthetic local-player guid for creator mode. Never sent anywhere; it only
    /// keys the presentation path (spell effects, creature renderer self-id).</summary>
    private const ulong CreatorLocalGuid = 0xF000_0000_0000_0001UL;

    private bool _creatorWorldRequested;   // Enter World clicked; the sandbox owns the world
    private bool _launchMenuOpen;          // the login screen's Launch Options modal

    /// <summary>The unit key for "the local player" on the presentation path: the server's
    /// guid when logged in, the synthetic creator guid otherwise.</summary>
    internal ulong LocalPlayerGuid => _net?.PlayerGuid ?? CreatorLocalGuid;

    /// <summary>A scripted/batch run that must never see menus or front doors.</summary>
    private bool BatchInstrumentActive =>
        _movementSuiteOptions is not null || _liveRunOptions is not null ||
        _portraitBatchOptions is not null || _variantBatchOptions is not null;

    /// <summary>Creator launch selected and this is an interactive session.</summary>
    private bool CreatorLaunchActive =>
        Settings.LaunchMode == LaunchModeCreator && !BatchInstrumentActive;

    /// <summary>
    /// True while an interactive session should sit at the glue front door
    /// (login screen + Launch Options) instead of being in, or loading, a world:
    /// creator launch, or no server configured. Serverless boots used to load
    /// straight into the dev world, which skipped mode selection entirely -
    /// now every interactive boot goes through the front door. Batch
    /// instruments never see it.
    /// </summary>
    private bool GlueFrontDoorActive =>
        !_worldLoadStarted && !_creatorWorldRequested && !BatchInstrumentActive &&
        (CreatorLaunchActive || !_config.Server.Enabled);

    /// <summary>True once the creator sandbox owns the world (load armed, running or done).</summary>
    internal bool CreatorInWorld => _creatorWorldRequested;

    /// <summary>Enter the creator sandbox world from the glue screen. Restores the
    /// persisted look AND the persisted location (map + position + facing) so a
    /// session picks up exactly where the last one ended.</summary>
    private void EnterOfflineWorld()
    {
        if (_gl is null || _worldLoadStarted) return;

        // The front door may keep an idle network client ready so switching back to
        // Client Mode is immediate. The creator WORLD must not keep it: a live session
        // stopped by SetLaunchMode remains Disconnected, so PumpNet would run its
        // session-loss reset every frame and force the creator controller out of flight.
        // Dropping the client here also restores CreatorLocalGuid as the offline identity.
        _net?.Stop();
        _net?.Dispose();
        _net = null;
        Array.Clear(_passBuf);

        _creatorWorldRequested = true;
        _worldLoadStarted = true;   // Update()'s pre-world gate follows the net path's convention
        if (_character is not null) _character.Enabled = true;
        _glue?.Dispose(); _glue = null;
        _booth?.Dispose(); _booth = null;
        Console.WriteLine("[creator] entering the creator world (offline)");
        RestoreCreatorLook();
        RestoreCreatorLocation();
        BeginWorldLoad(_gl);
    }

    // ── location persistence ─────────────────────────────────────────────────
    // The spot you leave the creator world at is the spot the next session loads
    // into, same as the character look. Saved every few seconds while moving
    // (settings.json is cheap to write) and once more at shutdown.

    private double _creatorLocSavedAt;
    private Vector3 _creatorLocSavedPos;
    private float _creatorLocSavedYaw;

    /// <summary>Point the pre-world state at the persisted creator location, so the
    /// initial world load streams in around it. Runs before BeginWorldLoad.</summary>
    private void RestoreCreatorLocation()
    {
        var saved = Settings.Creator;
        if (saved.LocMap < 0 || string.IsNullOrWhiteSpace(saved.LocMapName)) return;

        _config.Start.Map = saved.LocMap;
        _config.Start.MapName = saved.LocMapName;
        _config.Start.X = saved.LocX;
        _config.Start.Y = saved.LocY;
        _config.Start.Z = saved.LocZ;
        _config.Start.Orientation = saved.LocYaw;

        // The boot-time Load() already pointed the ADT cache and resident centre
        // at the config default; re-aim both before the load arms.
        _adts?.SetMap(saved.LocMapName);
        _residentCentre = null;
        if (_controller is not null)
        {
            _controller.Teleport(saved.LocX, saved.LocY, saved.LocZ);
            _controller.Yaw = saved.LocYaw;
        }
        _window.Camera.Target = new Vector3(saved.LocX, saved.LocY, saved.LocZ);
        _window.Camera.Yaw = saved.LocYaw;
        _creatorLocSavedPos = new Vector3(saved.LocX, saved.LocY, saved.LocZ);
        _creatorLocSavedYaw = saved.LocYaw;
        Console.WriteLine($"[creator] restored location: map {saved.LocMap} ({saved.LocMapName}) " +
                          $"({saved.LocX:F1}, {saved.LocY:F1}, {saved.LocZ:F1})");
    }

    /// <summary>Persist the creator position. Cheap no-op unless 5s have passed AND the
    /// player actually moved; <paramref name="force"/> (shutdown) skips both gates.</summary>
    private void UpdateCreatorLocationPersist(bool force = false)
    {
        if (!_creatorWorldRequested || _controller is null || _worldLoading || _travelInProgress)
            return;
        double now = NowSeconds();
        if (!force && now - _creatorLocSavedAt < 5.0) return;
        Vector3 p = _controller.Position;
        if (!force && Vector3.Distance(p, _creatorLocSavedPos) < 0.5f &&
            MathF.Abs(_controller.Yaw - _creatorLocSavedYaw) < 0.05f)
        {
            _creatorLocSavedAt = now;
            return;
        }

        var target = Settings.Creator;
        target.LocMap = _config.Start.Map;
        target.LocMapName = _config.Start.MapName;
        target.LocX = p.X;
        target.LocY = p.Y;
        target.LocZ = p.Z;
        target.LocYaw = _controller.Yaw;
        SettingsFile?.Save();
        _creatorLocSavedAt = now;
        _creatorLocSavedPos = p;
        _creatorLocSavedYaw = _controller.Yaw;
    }

    /// <summary>Set + persist the sticky launch mode. Called from the Launch Options
    /// modal. The front door stays up either way - Enter World commits.</summary>
    private void SetLaunchMode(string mode)
    {
        if (Settings.LaunchMode == mode) return;
        Settings.LaunchMode = mode;
        SetActiveLaunchModeOnProfile(mode);
        ApplyActiveLoginProfiles(applyLaunchMode: true);
        SettingsFile?.Save();
        Console.WriteLine($"[creator] launch mode -> {mode} (saved to {SettingsFile?.FilePath})");

        // The launch choice wins over the legacy server.enabled master switch, and it
        // takes effect NOW: on a serverless boot the network client was never built,
        // so switching to client mode used to change nothing until a manual config
        // edit + restart. Enable the server path and build the client on the spot —
        // the login screen re-renders with the account fields on the next frame
        // (auto-login fires if configured).
        if (mode == LaunchModeClient)
        {
            if (!_config.Server.Enabled)
            {
                _config.Server.Enabled = true;
                Console.WriteLine("[net] server enabled by Launch Options (client mode)");
            }
            bool hadNet = _net is not null;
            EnsureNetworkClient(suppressAutoLogin: false);
            // A creator-mode boot with the server enabled built the client but held
            // auto-login back; honour it now that the user chose client mode.
            if (hadNet && _net is { State: NetState.Idle } net && _config.Server.AutoConnect &&
                !string.IsNullOrWhiteSpace(_config.Server.Account) &&
                !string.IsNullOrWhiteSpace(_config.Server.Password))
            {
                net.Login(_config.Server.Account, _config.Server.Password);
                Console.WriteLine($"[net] auto-login as {_config.Server.Account} (launch mode switch)");
            }
        }
        else if (mode == LaunchModeCreator && !_worldLoadStarted)
        {
            // Choosing creator must actually LAND on the creator front door, not just
            // flip the sticky flag: a connected/connecting session keeps NetHud on the
            // account/character screens where the flag changes nothing visible. Same
            // teardown as the character-select Back button. Pre-world only — the
            // Launch Options modal is unreachable in-world anyway.
            if (_net is not null && _net.State != NetState.Idle)
            {
                _net.Stop();
                _net.Dispose();
                _net = null;
                Array.Clear(_passBuf);
                Console.WriteLine("[creator] session disconnected - creator front door up");
            }
        }
    }

    /// <summary>
    /// The "what am I launching" modal, drawn over the login screen in glue units
    /// (same red GlueButtons as the rest of the login). The active mode is tagged;
    /// clicking the other one switches and saves immediately.
    /// </summary>
    private void DrawLaunchOptionsMenu(ImDrawListPtr dl, float s)
    {
        if (!_launchMenuOpen || _skin is null) return;

        var disp = ImGui.GetIO().DisplaySize;
        LoginUiLaw.LaunchOptionsLayout layout = LoginUiLaw.LaunchOptions(disp, s);
        _skin.DrawBackdrop(dl, layout.Frame.Min, layout.Frame.Max, WowSkin.Dialog);
        _skin.HeaderPlaque(dl, layout.Frame.Min, layout.Frame.Size.X, "Launch Options");

        GlueText(dl, "What am I launching?", layout.PromptCenter.X, layout.PromptCenter.Y,
                 14f * s, WowSkin.GlueGold, 1);

        string[] configurationNames = LoginProfiles.LaunchConfigurations
            .Select(p => p.Name).ToArray();
        int configurationIndex = Math.Max(0, LoginProfiles.LaunchConfigurations.FindIndex(
            p => p.Id == LoginProfiles.ActiveLaunchConfigurationId));
        ImGui.SetCursorScreenPos(layout.ConfigurationCombo.Min);
        ImGui.SetNextItemWidth(layout.ConfigurationCombo.Size.X);
        if (configurationNames.Length > 0 && ImGui.Combo("##launch-configuration-quick",
                ref configurationIndex, configurationNames, configurationNames.Length))
            UseLaunchConfiguration(LoginProfiles.LaunchConfigurations[configurationIndex].Id);

        bool creatorActive = Settings.LaunchMode == LaunchModeCreator;

        ImGui.SetCursorScreenPos(layout.ClientButton.Min);
        if (_skin.GlueButton("SuperUI Client Mode", layout.ClientButton.Size))
            SetLaunchMode(LaunchModeClient);
        if (!creatorActive)
            GlueText(dl, "active", layout.ClientActiveLabel.X, layout.ClientActiveLabel.Y,
                     12f * s, WowSkin.GlueGold, 0);

        ImGui.SetCursorScreenPos(layout.CreatorButton.Min);
        if (_skin.GlueButton("Creator Mode", layout.CreatorButton.Size))
            SetLaunchMode(LaunchModeCreator);
        if (creatorActive)
            GlueText(dl, "active", layout.CreatorActiveLabel.X, layout.CreatorActiveLabel.Y,
                     12f * s, WowSkin.GlueGold, 0);

        ImGui.SetCursorScreenPos(layout.OkayButton.Min);
        if (_skin.GlueButton("Okay", layout.OkayButton.Size))
            _launchMenuOpen = false;
    }

}
