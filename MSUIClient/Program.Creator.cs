using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// Creator Mode - the offline spell-creator sandbox.
//
// The launch choice lives on the login screen (Launch Options): "SuperUI Client
// Mode" is the networked client, "Spell Creator Mode" boots the offline world
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

    /// <summary>Enter the offline world from the glue screen. <paramref name="creator"/> chooses
    /// between the creator sandbox and the plain offline viewer (the pre-creator default).</summary>
    private void EnterOfflineWorld(bool creator)
    {
        if (_gl is null || _worldLoadStarted) return;
        _creatorWorldRequested = creator;
        _worldLoadStarted = true;   // Update()'s pre-world gate follows the net path's convention
        if (_character is not null) _character.Enabled = true;
        _glue?.Dispose(); _glue = null;
        _booth?.Dispose(); _booth = null;
        Console.WriteLine(creator
            ? "[creator] entering the creator world (offline)"
            : "[creator] launch mode Client with no server - entering the offline viewer");
        if (creator) RestoreCreatorLook();
        BeginWorldLoad(_gl);
    }

    /// <summary>Set + persist the sticky launch mode. Called from the Launch Options
    /// modal. The front door stays up either way - Enter World commits.</summary>
    private void SetLaunchMode(string mode)
    {
        if (Settings.LaunchMode == mode) return;
        Settings.LaunchMode = mode;
        SettingsFile?.Save();
        Console.WriteLine($"[creator] launch mode -> {mode} (saved to {SettingsFile?.FilePath})");
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
        float w = 420f * s, h = 250f * s;
        var min = new Vector2((disp.X - w) * 0.5f, (disp.Y - h) * 0.5f);
        var max = min + new Vector2(w, h);
        _skin.DrawBackdrop(dl, min, max, WowSkin.Dialog);
        _skin.HeaderPlaque(dl, min, w, "Launch Options");

        GlueText(dl, "What am I launching?", min.X + w * 0.5f, min.Y + 44f * s,
                 14f * s, WowSkin.GlueGold, 1);

        bool creatorActive = Settings.LaunchMode == LaunchModeCreator;
        var bSize = new Vector2(250f * s, 40f * s);
        float bx = min.X + (w - bSize.X) * 0.5f;
        float tagX = bx + bSize.X + 8f * s;

        ImGui.SetCursorScreenPos(new Vector2(bx, min.Y + 74f * s));
        if (_skin.GlueButton("SuperUI Client Mode", bSize))
            SetLaunchMode(LaunchModeClient);
        if (!creatorActive)
            GlueText(dl, "active", tagX, min.Y + 74f * s + bSize.Y * 0.5f - 6f * s,
                     12f * s, WowSkin.GlueGold, 0);

        ImGui.SetCursorScreenPos(new Vector2(bx, min.Y + 124f * s));
        if (_skin.GlueButton("Spell Creator Mode", bSize))
            SetLaunchMode(LaunchModeCreator);
        if (creatorActive)
            GlueText(dl, "active", tagX, min.Y + 124f * s + bSize.Y * 0.5f - 6f * s,
                     12f * s, WowSkin.GlueGold, 0);

        var okSize = new Vector2(120f * s, 34f * s);
        ImGui.SetCursorScreenPos(new Vector2(min.X + (w - okSize.X) * 0.5f, max.Y - 46f * s));
        if (_skin.GlueButton("Okay", okSize))
            _launchMenuOpen = false;
    }

}
