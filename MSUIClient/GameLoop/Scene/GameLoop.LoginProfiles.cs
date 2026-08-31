using System.Numerics;
using System.Net.Sockets;
using ImGuiNET;
using MSUIClient.Engine;
using MSUIClient.Engine.UI;

namespace MSUIClient;

/// <summary>
/// The login front door's connection and launch profile management. Connections own
/// realmlist-style endpoint facts; launch configurations own credentials and startup
/// behavior. They are deliberately separate so several accounts/modes can reuse one server.
/// </summary>
public sealed partial class GameLoop
{
    private readonly LoginProfileSettings _fallbackLoginProfiles = new();
    private LoginProfileSettings LoginProfiles =>
        SettingsFile?.LoginProfiles ?? _fallbackLoginProfiles;

    private bool _manageConnectionsOpen;
    private bool _launchConfigurationsOpen;
    private ConnectionProfileSetting? _connectionDraft;
    private LaunchConfigurationSetting? _launchDraft;
    private string _loginProfileStatus = "";
    private Task<string>? _connectionTestTask;
    private bool LoginConfigurationModalOpen =>
        _launchMenuOpen || _manageConnectionsOpen || _launchConfigurationsOpen;

    private bool EnsureLoginProfilesInitialized()
    {
        LoginProfileSettings profiles = LoginProfiles;
        bool created = false;

        if (profiles.Connections.Count == 0)
        {
            var connection = new ConnectionProfileSetting
            {
                Id = NewProfileId(),
                Name = string.IsNullOrWhiteSpace(_config.Server.Realm)
                    ? "Home Server" : _config.Server.Realm!.Trim(),
                RealmdHost = _config.RealmdHost,
                RealmdPort = _config.RealmdPort,
                Realm = _config.Server.Realm ?? "",
                WorldPortFallback = _config.Server.WorldPortFallback,
                WorldUsesRealmdHost = _config.Server.WorldUsesRealmdHost,
                TimeoutMs = _config.Server.TimeoutMs,
                RealPortals = _config.Server.RealPortals,
            };
            profiles.Connections.Add(connection);
            profiles.ActiveConnectionId = connection.Id;
            created = true;
        }

        if (profiles.LaunchConfigurations.Count == 0)
        {
            ConnectionProfileSetting connection = ActiveConnectionProfile() ??
                profiles.Connections[0];
            var launch = new LaunchConfigurationSetting
            {
                Id = NewProfileId(),
                Name = "Default",
                ConnectionId = connection.Id,
                Mode = string.Equals(Settings.LaunchMode, LaunchModeCreator,
                    StringComparison.OrdinalIgnoreCase)
                    ? LaunchModeCreator
                    : string.Equals(Settings.LaunchMode, LaunchModeClient,
                        StringComparison.OrdinalIgnoreCase) || _config.Server.Enabled
                        ? LaunchModeClient : LaunchModeCreator,
                AutoLogin = _config.Server.AutoConnect,
                Account = _config.Server.Account,
                SavePassword = !string.IsNullOrEmpty(_config.Server.Password),
                Password = _config.Server.Password,
                AutoEnterWorld = !string.IsNullOrWhiteSpace(_config.Server.Character),
                Character = _config.Server.Character ?? "",
            };
            profiles.LaunchConfigurations.Add(launch);
            profiles.ActiveLaunchConfigurationId = launch.Id;
            created = true;
        }

        RepairActiveProfileIds();
        if (created)
        {
            SettingsFile?.Save();
            Console.WriteLine("[login-profiles] migrated client-config connection/launch values");
        }
        return created;
    }

    private static string NewProfileId() => Guid.NewGuid().ToString("N");

    private void RepairActiveProfileIds()
    {
        LoginProfileSettings profiles = LoginProfiles;
        if (!profiles.Connections.Any(p => p.Id == profiles.ActiveConnectionId))
            profiles.ActiveConnectionId = profiles.Connections.FirstOrDefault()?.Id ?? "";
        if (!profiles.LaunchConfigurations.Any(
                p => p.Id == profiles.ActiveLaunchConfigurationId))
            profiles.ActiveLaunchConfigurationId =
                profiles.LaunchConfigurations.FirstOrDefault()?.Id ?? "";
    }

    private ConnectionProfileSetting? ActiveConnectionProfile() =>
        LoginProfiles.Connections.FirstOrDefault(
            p => p.Id == LoginProfiles.ActiveConnectionId);

    private LaunchConfigurationSetting? ActiveLaunchConfiguration() =>
        LoginProfiles.LaunchConfigurations.FirstOrDefault(
            p => p.Id == LoginProfiles.ActiveLaunchConfigurationId);

    /// <summary>Copy the selected profile pair onto the existing runtime config adapter.</summary>
    private void ApplyActiveLoginProfiles(bool applyLaunchMode)
    {
        RepairActiveProfileIds();
        LaunchConfigurationSetting? launch = ActiveLaunchConfiguration();
        ConnectionProfileSetting? connection = launch is null
            ? ActiveConnectionProfile()
            : LoginProfiles.Connections.FirstOrDefault(p => p.Id == launch.ConnectionId) ??
              ActiveConnectionProfile();
        if (connection is not null)
        {
            LoginProfiles.ActiveConnectionId = connection.Id;
            _config.RealmdHost = connection.RealmdHost.Trim();
            _config.RealmdPort = Math.Clamp(connection.RealmdPort, 1, 65535);
            _config.Server.Realm = EmptyToNull(connection.Realm);
            _config.Server.WorldPortFallback =
                Math.Clamp(connection.WorldPortFallback, 1, 65535);
            _config.Server.WorldUsesRealmdHost = connection.WorldUsesRealmdHost;
            _config.Server.TimeoutMs = Math.Clamp(connection.TimeoutMs, 1000, 120000);
            _config.Server.RealPortals = connection.RealPortals;
        }

        if (launch is null) return;
        if (applyLaunchMode) Settings.LaunchMode = launch.Mode;
        _config.Server.Enabled = launch.Mode != LaunchModeCreator;
        _config.Server.AutoConnect = launch.AutoLogin;
        _config.Server.Account = launch.Account.Trim();
        _config.Server.Password = launch.SavePassword ? launch.Password : "";
        _config.Server.Character = launch.AutoEnterWorld
            ? EmptyToNull(launch.Character) : null;
    }

    private static string? EmptyToNull(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void RebuildNetworkClientForProfiles(bool allowAutoLogin)
    {
        if (_worldLoadStarted)
        {
            _loginProfileStatus = "Profiles can only be changed before entering a world.";
            return;
        }

        _net?.Stop();
        _net?.Dispose();
        _net = null;
        ApplyActiveLoginProfiles(applyLaunchMode: true);
        SettingsFile?.Save();

        if (_config.Server.Enabled)
            EnsureNetworkClient(suppressAutoLogin: !allowAutoLogin);
        _loginInit = false;
        _loginProfileStatus = "Active profile applied.";
    }

    private void PersistManualLogin(string account, string password)
    {
        LaunchConfigurationSetting? launch = ActiveLaunchConfiguration();
        if (launch is null) return;
        launch.Account = _rememberAccount ? account : "";
        if (launch.SavePassword) launch.Password = password;
        else launch.Password = "";
        SettingsFile?.Save();
    }

    private void SetActiveLaunchModeOnProfile(string mode)
    {
        LaunchConfigurationSetting? launch = ActiveLaunchConfiguration();
        if (launch is null || launch.Mode == mode) return;
        launch.Mode = mode;
        SettingsFile?.Save();
    }

    private void OpenConnectionManager()
    {
        EnsureLoginProfilesInitialized();
        _connectionDraft = Clone(ActiveConnectionProfile() ?? LoginProfiles.Connections[0]);
        _loginProfileStatus = "";
        _manageConnectionsOpen = true;
        _launchConfigurationsOpen = false;
        _launchMenuOpen = false;
    }

    private void OpenLaunchConfigurationManager()
    {
        EnsureLoginProfilesInitialized();
        _launchDraft = Clone(ActiveLaunchConfiguration() ??
            LoginProfiles.LaunchConfigurations[0]);
        _loginProfileStatus = "";
        _launchConfigurationsOpen = true;
        _manageConnectionsOpen = false;
        _launchMenuOpen = false;
    }

    private void UseLaunchConfiguration(string id)
    {
        LaunchConfigurationSetting? launch = LoginProfiles.LaunchConfigurations
            .FirstOrDefault(p => p.Id == id);
        if (launch is null) return;
        LoginProfiles.ActiveLaunchConfigurationId = launch.Id;
        LoginProfiles.ActiveConnectionId = launch.ConnectionId;
        RebuildNetworkClientForProfiles(allowAutoLogin: false);
    }

    private void DrawLoginProfileWindows()
    {
        if (_manageConnectionsOpen) DrawManageConnectionsWindow();
        if (_launchConfigurationsOpen) DrawLaunchConfigurationsWindow();
    }

    private void DrawManageConnectionsWindow()
    {
        if (_skin is null) return;

        ClassicProfileModal modal = BeginClassicProfileModal(
            "##manage-connection", "Manage Connection", new Vector2(810f, 650f));
        float s = modal.Scale;
        Vector2 listMin = modal.FrameMin + new Vector2(24f, 60f) * s;
        Vector2 listSize = new Vector2(226f, 520f) * s;
        Vector2 editorMin = modal.FrameMin + new Vector2(262f, 60f) * s;
        Vector2 editorSize = new Vector2(524f, 520f) * s;

        DrawClassicProfileInset(listMin, listSize);
        DrawClassicProfileInset(editorMin, editorSize);
        DrawConnectionPicker(listMin, listSize, s);
        DrawConnectionEditor(editorMin, editorSize, s);

        Vector2 closeSize = new(124f * s, 34f * s);
        ImGui.SetCursorScreenPos(new Vector2(
            modal.FrameMin.X + (modal.FrameSize.X - closeSize.X) * .5f,
            modal.FrameMin.Y + 600f * s));
        if (_skin.GlueButton("Close##connection-close", closeSize, captionPx: 13f * s))
            _manageConnectionsOpen = false;

        EndClassicProfileModal(modal);
    }

    private void DrawConnectionPicker(Vector2 panelMin, Vector2 panelSize, float s)
    {
        Vector2 inset = new(13f * s, 13f * s);
        ImGui.SetCursorScreenPos(panelMin + inset);
        ImGui.BeginChild("##connection-list",
            new Vector2(panelSize.X - inset.X * 2f, panelSize.Y - 126f * s), false,
            ImGuiWindowFlags.NoBackground);
        ImGui.TextColored(WowSkin.GlueGold, "Connections");
        ImGui.Spacing();
        foreach (ConnectionProfileSetting profile in LoginProfiles.Connections.ToArray())
        {
            bool selected = _connectionDraft?.Id == profile.Id;
            bool active = profile.Id == LoginProfiles.ActiveConnectionId;
            string caption = $"{(selected ? "> " : "")}{profile.Name}{(active ? "  [active]" : "")}";
            Vector2 buttonMin = ImGui.GetCursorScreenPos();
            Vector2 buttonSize = new(ImGui.GetContentRegionAvail().X, 31f * s);
            if (_skin!.GlueButton($"{caption}##connection-{profile.Id}", buttonSize,
                    captionPx: 11.5f * s))
                _connectionDraft = Clone(profile);
            if (selected)
                ImGui.GetWindowDrawList().AddRect(buttonMin, buttonMin + buttonSize,
                    ImGui.ColorConvertFloat4ToU32(WowSkin.GlueGold), 0f, ImDrawFlags.None,
                    MathF.Max(1f, s));
            ImGui.Spacing();
        }
        ImGui.EndChild();

        float innerW = panelSize.X - 26f * s;
        float gap = 7f * s;
        Vector2 addSize = new(64f * s, 31f * s);
        Vector2 duplicateSize = new(innerW - addSize.X - gap, 31f * s);
        Vector2 actions = panelMin + new Vector2(13f, 420f) * s;
        ImGui.SetCursorScreenPos(actions);
        if (_skin!.GlueButton("Add##connection-add", addSize, captionPx: 12f * s))
        {
            _connectionDraft = new ConnectionProfileSetting
            {
                Id = NewProfileId(),
                Name = UniqueConnectionName("New Connection"),
            };
        }
        ImGui.SetCursorScreenPos(actions + new Vector2(addSize.X + gap, 0f));
        if (_skin.GlueButton("Duplicate##connection-duplicate", duplicateSize,
                _connectionDraft is not null, 11f * s) && _connectionDraft is not null)
        {
            ConnectionProfileSetting copy = Clone(_connectionDraft);
            copy.Id = NewProfileId();
            copy.Name = UniqueConnectionName($"{copy.Name} Copy");
            _connectionDraft = copy;
        }
        ImGui.SetCursorScreenPos(actions + new Vector2(0f, 40f * s));
        if (_skin.GlueButton("Delete##connection-delete", new Vector2(innerW, 31f * s),
                _connectionDraft is not null, 12f * s) && _connectionDraft is not null)
            DeleteConnection(_connectionDraft.Id);
    }

    private void DrawConnectionEditor(Vector2 panelMin, Vector2 panelSize, float s)
    {
        if (_connectionTestTask is { IsCompleted: true })
        {
            try { _loginProfileStatus = _connectionTestTask.GetAwaiter().GetResult(); }
            catch (Exception ex) { _loginProfileStatus = $"Connection test failed: {ex.Message}"; }
            _connectionTestTask = null;
        }
        Vector2 inset = new(14f * s, 13f * s);
        ImGui.SetCursorScreenPos(panelMin + inset);
        ImGui.BeginChild("##connection-editor", panelSize - inset * 2f, false,
            ImGuiWindowFlags.NoBackground);
        ImGui.TextColored(WowSkin.GlueGold, "Connection Details");
        ImGui.Spacing();
        ConnectionProfileSetting? d = _connectionDraft;
        if (d is null) ImGui.TextDisabled("Select or add a connection.");
        else
        {
            float fieldW = ImGui.GetContentRegionAvail().X;
            d.Name = ClassicTextField("connection-name", "Name", d.Name, 80, fieldW, s);
            d.RealmdHost = ClassicTextField("realmlist-host", "Realmlist host",
                d.RealmdHost, 255, fieldW, s);
            d.RealmdPort = ClassicIntField("realmlist-port", "Realmlist port",
                d.RealmdPort, 1, 65535, fieldW, s);
            d.Realm = ClassicTextField("preferred-realm", "Preferred realm (optional)",
                d.Realm, 120, fieldW, s);
            d.WorldPortFallback = ClassicIntField("world-port-fallback",
                "World port fallback", d.WorldPortFallback, 1, 65535, fieldW, s);
            d.TimeoutMs = ClassicIntField("connection-timeout",
                "Connection timeout (ms)", d.TimeoutMs, 1000, 120000, fieldW, s);
            d.WorldUsesRealmdHost = ClassicCheckBox(
                "Use realmlist host for world server##connection-world-host",
                d.WorldUsesRealmdHost, s);
            d.RealPortals = ClassicCheckBox(
                "Enable SuperUI real portals##connection-real-portals", d.RealPortals, s);

            Vector2 testSize = new(142f * s, 32f * s);
            if (_skin!.GlueButton(
                    _connectionTestTask is null
                        ? "Test Connection##connection-test"
                        : "Testing...##connection-test",
                    testSize, _connectionTestTask is null, 11.5f * s))
                StartConnectionTest(d);
            ImGui.SameLine(0f, 8f * s);
            if (_skin.GlueButton("Save##connection-save", new Vector2(92f, 32f) * s,
                    captionPx: 12f * s))
                SaveConnectionDraft(useNow: false);
            ImGui.SameLine(0f, 8f * s);
            if (_skin.GlueButton("Save & Use##connection-use", new Vector2(116f, 32f) * s,
                    captionPx: 11.5f * s))
                SaveConnectionDraft(useNow: true);
            if (_loginProfileStatus.Length > 0)
            {
                ImGui.Spacing();
                ImGui.PushTextWrapPos();
                ImGui.TextColored(WowSkin.GlueGold, _loginProfileStatus);
                ImGui.PopTextWrapPos();
            }
        }
        ImGui.EndChild();
    }

    private void DrawLaunchConfigurationsWindow()
    {
        if (_skin is null) return;

        ClassicProfileModal modal = BeginClassicProfileModal(
            "##launch-configurations", "Launch Configurations", new Vector2(820f, 680f));
        float s = modal.Scale;
        Vector2 listMin = modal.FrameMin + new Vector2(24f, 60f) * s;
        Vector2 listSize = new Vector2(226f, 550f) * s;
        Vector2 editorMin = modal.FrameMin + new Vector2(262f, 60f) * s;
        Vector2 editorSize = new Vector2(534f, 550f) * s;

        DrawClassicProfileInset(listMin, listSize);
        DrawClassicProfileInset(editorMin, editorSize);
        DrawLaunchPicker(listMin, listSize, s);
        DrawLaunchEditor(editorMin, editorSize, s);

        Vector2 closeSize = new(124f * s, 34f * s);
        ImGui.SetCursorScreenPos(new Vector2(
            modal.FrameMin.X + (modal.FrameSize.X - closeSize.X) * .5f,
            modal.FrameMin.Y + 630f * s));
        if (_skin.GlueButton("Close##launch-close", closeSize, captionPx: 13f * s))
            _launchConfigurationsOpen = false;

        EndClassicProfileModal(modal);
    }

    private void DrawLaunchPicker(Vector2 panelMin, Vector2 panelSize, float s)
    {
        Vector2 inset = new(13f * s, 13f * s);
        ImGui.SetCursorScreenPos(panelMin + inset);
        ImGui.BeginChild("##launch-list",
            new Vector2(panelSize.X - inset.X * 2f, panelSize.Y - 126f * s), false,
            ImGuiWindowFlags.NoBackground);
        ImGui.TextColored(WowSkin.GlueGold, "Configurations");
        ImGui.Spacing();
        foreach (LaunchConfigurationSetting profile in
                 LoginProfiles.LaunchConfigurations.ToArray())
        {
            bool selected = _launchDraft?.Id == profile.Id;
            bool active = profile.Id == LoginProfiles.ActiveLaunchConfigurationId;
            string caption = $"{(selected ? "> " : "")}{profile.Name}{(active ? "  [active]" : "")}";
            Vector2 buttonMin = ImGui.GetCursorScreenPos();
            Vector2 buttonSize = new(ImGui.GetContentRegionAvail().X, 31f * s);
            if (_skin!.GlueButton($"{caption}##launch-{profile.Id}", buttonSize,
                    captionPx: 11.5f * s))
                _launchDraft = Clone(profile);
            if (selected)
                ImGui.GetWindowDrawList().AddRect(buttonMin, buttonMin + buttonSize,
                    ImGui.ColorConvertFloat4ToU32(WowSkin.GlueGold), 0f, ImDrawFlags.None,
                    MathF.Max(1f, s));
            ImGui.Spacing();
        }
        ImGui.EndChild();

        float innerW = panelSize.X - 26f * s;
        float gap = 7f * s;
        Vector2 addSize = new(64f * s, 31f * s);
        Vector2 duplicateSize = new(innerW - addSize.X - gap, 31f * s);
        Vector2 actions = panelMin + new Vector2(13f, 450f) * s;
        ImGui.SetCursorScreenPos(actions);
        if (_skin!.GlueButton("Add##launch-add", addSize, captionPx: 12f * s))
        {
            _launchDraft = new LaunchConfigurationSetting
            {
                Id = NewProfileId(),
                Name = UniqueLaunchName("New Configuration"),
                ConnectionId = ActiveConnectionProfile()?.Id ?? "",
            };
        }
        ImGui.SetCursorScreenPos(actions + new Vector2(addSize.X + gap, 0f));
        if (_skin.GlueButton("Duplicate##launch-duplicate", duplicateSize,
                _launchDraft is not null, 11f * s) && _launchDraft is not null)
        {
            LaunchConfigurationSetting copy = Clone(_launchDraft);
            copy.Id = NewProfileId();
            copy.Name = UniqueLaunchName($"{copy.Name} Copy");
            _launchDraft = copy;
        }
        ImGui.SetCursorScreenPos(actions + new Vector2(0f, 40f * s));
        if (_skin.GlueButton("Delete##launch-delete", new Vector2(innerW, 31f * s),
                _launchDraft is not null, 12f * s) && _launchDraft is not null)
            DeleteLaunchConfiguration(_launchDraft.Id);
    }

    private void DrawLaunchEditor(Vector2 panelMin, Vector2 panelSize, float s)
    {
        Vector2 inset = new(14f * s, 13f * s);
        ImGui.SetCursorScreenPos(panelMin + inset);
        ImGui.BeginChild("##launch-editor", panelSize - inset * 2f, false,
            ImGuiWindowFlags.NoBackground);
        ImGui.TextColored(WowSkin.GlueGold, "Launch Details");
        ImGui.Spacing();
        LaunchConfigurationSetting? d = _launchDraft;
        if (d is null) ImGui.TextDisabled("Select or add a launch configuration.");
        else
        {
            float fieldW = ImGui.GetContentRegionAvail().X;
            d.Name = ClassicTextField("launch-name", "Name", d.Name, 80, fieldW, s);
            string[] connections = LoginProfiles.Connections.Select(p => p.Name).ToArray();
            int connectionIndex = Math.Max(0, LoginProfiles.Connections.FindIndex(
                p => p.Id == d.ConnectionId));
            if (connections.Length > 0 && ClassicCombo("launch-connection", "Connection",
                    ref connectionIndex, connections, fieldW, s))
                d.ConnectionId = LoginProfiles.Connections[connectionIndex].Id;

            int mode = d.Mode == LaunchModeCreator ? 1 : 0;
            if (ClassicCombo("launch-mode", "Mode", ref mode,
                    ["SuperUI Client", "Creator"], fieldW, s))
                d.Mode = mode == 1 ? LaunchModeCreator : LaunchModeClient;

            if (d.Mode == LaunchModeClient)
            {
                d.AutoLogin = ClassicCheckBox(
                    "Log in automatically on startup##launch-auto-login", d.AutoLogin, s);
                d.Account = ClassicTextField("launch-account", "Account", d.Account, 80,
                    fieldW, s);
                d.SavePassword = ClassicCheckBox(
                    "Save password locally##launch-save-password", d.SavePassword, s);
                d.Password = ClassicTextField("launch-password", "Password", d.Password,
                    128, fieldW, s, ImGuiInputTextFlags.Password, d.SavePassword);
                if (d.SavePassword)
                    ImGui.TextDisabled("Stored as plain text in local settings.json.");
                if (!d.SavePassword)
                    ImGui.TextDisabled("The password field on the login screen will be used instead.");
                d.AutoEnterWorld = ClassicCheckBox(
                    "Enter world automatically##launch-auto-enter", d.AutoEnterWorld, s);
                d.Character = ClassicTextField("launch-character", "Character", d.Character,
                    80, fieldW, s, enabled: d.AutoEnterWorld);
            }
            else
            {
                ImGui.TextWrapped("Creator starts the local offline sandbox; connection and credentials are retained but unused.");
            }

            ImGui.Spacing();
            if (_skin!.GlueButton("Save##launch-save", new Vector2(104f, 32f) * s,
                    captionPx: 12f * s))
                SaveLaunchDraft(useNow: false);
            ImGui.SameLine(0f, 8f * s);
            if (_skin.GlueButton("Save & Use##launch-use", new Vector2(126f, 32f) * s,
                    captionPx: 11.5f * s))
                SaveLaunchDraft(useNow: true);
            if (_loginProfileStatus.Length > 0)
            {
                ImGui.Spacing();
                ImGui.PushTextWrapPos();
                ImGui.TextColored(WowSkin.GlueGold, _loginProfileStatus);
                ImGui.PopTextWrapPos();
            }
        }
        ImGui.EndChild();
    }

    private readonly record struct ClassicProfileModal(
        float Scale, Vector2 FrameMin, Vector2 FrameSize, float SavedSkinScale);

    private ClassicProfileModal BeginClassicProfileModal(
        string windowId, string title, Vector2 logicalSize)
    {
        Vector2 display = ImGui.GetIO().DisplaySize;
        float heightScale = display.Y / GlueCanvasH;
        float widthScale = display.X / (logicalSize.X + 24f);
        float s = MathF.Max(MathF.Min(heightScale, widthScale), .5f);
        Vector2 frameSize = logicalSize * s;
        Vector2 frameMin = (display - frameSize) * .5f;
        float savedSkinScale = _skin!.Scale;
        _skin.Scale = s;
        _skin.PushStyle();

        ImGui.SetNextWindowPos(frameMin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(frameSize, ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.Begin(windowId,
            ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoNav);
        ImGui.PopStyleVar();

        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        dl.PushClipRect(frameMin - new Vector2(64f * s),
            frameMin + frameSize + new Vector2(64f * s), false);
        Vector2 fillInset = new(7f * s);
        dl.AddRectFilled(frameMin + fillInset, frameMin + frameSize - fillInset,
            ImGui.ColorConvertFloat4ToU32(new Vector4(.008f, .006f, .005f, .96f)));
        _skin.DrawBackdrop(dl, frameMin, frameMin + frameSize, WowSkin.Dialog);
        _skin.HeaderPlaque(dl, frameMin, frameSize.X, title);
        dl.PopClipRect();
        return new ClassicProfileModal(s, frameMin, frameSize, savedSkinScale);
    }

    private void EndClassicProfileModal(ClassicProfileModal modal)
    {
        ImGui.End();
        _skin!.PopStyle();
        _skin.Scale = modal.SavedSkinScale;
    }

    private void DrawClassicProfileInset(Vector2 min, Vector2 size)
    {
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        Vector2 inset = new(4f * _skin!.Scale);
        dl.AddRectFilled(min + inset, min + size - inset,
            ImGui.ColorConvertFloat4ToU32(new Vector4(.006f, .005f, .004f, .985f)));
        _skin.DrawBackdrop(dl, min, min + size,
            WowSkin.GlueEditBox, WowSkin.GlueBoxFill, WowSkin.GlueBoxBorder);
    }

    private string ClassicTextField(string id, string label, string value, uint capacity,
        float width, float s, ImGuiInputTextFlags flags = ImGuiInputTextFlags.None,
        bool enabled = true)
    {
        ImGui.TextColored(WowSkin.GlueGold, label);
        Vector2 boxMin = ImGui.GetCursorScreenPos();
        float boxHeight = 30f * s;
        _skin!.DrawBackdrop(ImGui.GetWindowDrawList(), boxMin,
            boxMin + new Vector2(width, boxHeight), WowSkin.GlueEditBox,
            WowSkin.GlueBoxFill, WowSkin.GlueBoxBorder);

        float inputOffsetY = MathF.Max(0f, (boxHeight - ImGui.GetFrameHeight()) * .5f);
        ImGui.SetCursorScreenPos(boxMin + new Vector2(7f * s, inputOffsetY));
        ImGui.SetNextItemWidth(width - 14f * s);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, Vector4.Zero);
        ImGui.BeginDisabled(!enabled);
        ImGui.InputText($"##{id}", ref value, capacity, flags);
        ImGui.EndDisabled();
        ImGui.PopStyleColor(3);
        ImGui.SetCursorScreenPos(boxMin + new Vector2(0f, boxHeight + 5f * s));
        return value;
    }

    private int ClassicIntField(string id, string label, int value, int min, int max,
        float width, float s)
    {
        ImGui.TextColored(WowSkin.GlueGold, label);
        Vector2 boxMin = ImGui.GetCursorScreenPos();
        float boxHeight = 30f * s;
        _skin!.DrawBackdrop(ImGui.GetWindowDrawList(), boxMin,
            boxMin + new Vector2(width, boxHeight), WowSkin.GlueEditBox,
            WowSkin.GlueBoxFill, WowSkin.GlueBoxBorder);

        float inputOffsetY = MathF.Max(0f, (boxHeight - ImGui.GetFrameHeight()) * .5f);
        ImGui.SetCursorScreenPos(boxMin + new Vector2(7f * s, inputOffsetY));
        ImGui.SetNextItemWidth(width - 14f * s);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, Vector4.Zero);
        if (ImGui.InputInt($"##{id}", ref value, 0, 0))
            value = Math.Clamp(value, min, max);
        ImGui.PopStyleColor(3);
        ImGui.SetCursorScreenPos(boxMin + new Vector2(0f, boxHeight + 5f * s));
        return value;
    }

    private bool ClassicCombo(string id, string label, ref int selected,
        string[] items, float width, float s)
    {
        ImGui.TextColored(WowSkin.GlueGold, label);
        Vector2 boxMin = ImGui.GetCursorScreenPos();
        float boxHeight = 30f * s;
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        _skin!.DrawBackdrop(dl, boxMin,
            boxMin + new Vector2(width, boxHeight), WowSkin.GlueEditBox,
            WowSkin.GlueBoxFill, WowSkin.GlueBoxBorder);

        ImGui.SetCursorScreenPos(boxMin);
        bool clicked = ImGui.InvisibleButton($"##{id}", new Vector2(width, boxHeight));
        bool hovered = ImGui.IsItemHovered();
        bool held = ImGui.IsItemActive();

        string preview = items.Length > 0
            ? items[Math.Clamp(selected, 0, items.Length - 1)]
            : "";
        Vector2 arrowSize = new(18f * s);
        Vector2 arrowMin = boxMin + new Vector2(
            width - arrowSize.X - 5f * s,
            (boxHeight - arrowSize.Y) * .5f);
        Vector2 arrowMax = arrowMin + arrowSize;
        Vector2 textSize = ImGui.CalcTextSize(preview);
        Vector2 textPos = new(boxMin.X + 11f * s,
            boxMin.Y + (boxHeight - textSize.Y) * .5f);
        dl.PushClipRect(boxMin + new Vector2(9f * s, 2f * s),
            new Vector2(arrowMin.X - 4f * s, boxMin.Y + boxHeight - 2f * s), true);
        dl.AddText(textPos, ImGui.ColorConvertFloat4ToU32(WowSkin.Normal), preview);
        dl.PopClipRect();

        string arrowArt = held && _skin.Has("scroll.dn.dn")
            ? "scroll.dn.dn" : "scroll.dn";
        Vector2 pushed = held ? new Vector2(1f * s) : Vector2.Zero;
        if (!_skin.GlueImageUv(dl, arrowArt, arrowMin + pushed, arrowMax + pushed,
                new Vector2(.25f), new Vector2(.75f)))
        {
            uint gold = ImGui.ColorConvertFloat4ToU32(WowSkin.GlueGold);
            Vector2 center = (arrowMin + arrowMax) * .5f + pushed;
            float wing = 5f * s;
            dl.AddLine(center - new Vector2(wing, wing * .45f), center, gold,
                MathF.Max(1.5f, 1.5f * s));
            dl.AddLine(center, center + new Vector2(wing, -wing * .45f), gold,
                MathF.Max(1.5f, 1.5f * s));
        }
        if (hovered && !held)
            dl.AddRect(arrowMin, arrowMax,
                ImGui.ColorConvertFloat4ToU32(new Vector4(1f, .82f, .18f, .45f)),
                0f, ImDrawFlags.None, MathF.Max(1f, s));

        string popupId = $"##{id}-popup";
        if (clicked) ImGui.OpenPopup(popupId);
        ImGui.SetNextWindowPos(new Vector2(boxMin.X, boxMin.Y + boxHeight), ImGuiCond.Appearing);
        ImGui.SetNextWindowSizeConstraints(new Vector2(width, 0f),
            new Vector2(width, MathF.Max(120f * s, items.Length * 30f * s)));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(5f * s));
        bool popupOpen = ImGui.BeginPopup(popupId);
        ImGui.PopStyleVar();
        bool changed = false;
        if (popupOpen)
        {
            for (int i = 0; i < items.Length; i++)
            {
                bool isSelected = i == selected;
                Vector2 rowMin = ImGui.GetCursorScreenPos();
                Vector2 rowSize = new(ImGui.GetContentRegionAvail().X, 25f * s);
                bool choose = ImGui.InvisibleButton($"##{id}-{i}", rowSize);
                bool rowHovered = ImGui.IsItemHovered();
                if (isSelected || rowHovered)
                    ImGui.GetWindowDrawList().AddRectFilled(rowMin, rowMin + rowSize,
                        ImGui.ColorConvertFloat4ToU32(isSelected
                            ? new Vector4(.31f, .23f, .08f, .92f)
                            : new Vector4(.22f, .17f, .07f, .82f)));
                string rowText = $"{(isSelected ? "> " : "")}{items[i]}";
                Vector2 rowTextSize = ImGui.CalcTextSize(rowText);
                ImGui.GetWindowDrawList().AddText(
                    new Vector2(rowMin.X + 7f * s,
                        rowMin.Y + (rowSize.Y - rowTextSize.Y) * .5f),
                    ImGui.ColorConvertFloat4ToU32(
                        isSelected ? WowSkin.GlueGold : WowSkin.Normal), rowText);
                if (choose)
                {
                    selected = i;
                    changed = true;
                    ImGui.CloseCurrentPopup();
                }
                if (isSelected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndPopup();
        }
        ImGui.SetCursorScreenPos(boxMin + new Vector2(0f, boxHeight + 5f * s));
        return changed;
    }

    private bool ClassicCheckBox(string label, bool value, float s)
    {
        _skin!.CheckBox(label, ref value, 22f, 13f * s);
        return value;
    }

    private void SaveConnectionDraft(bool useNow)
    {
        if (_connectionDraft is null) return;
        if (string.IsNullOrWhiteSpace(_connectionDraft.Name) ||
            string.IsNullOrWhiteSpace(_connectionDraft.RealmdHost))
        {
            _loginProfileStatus = "Name and realmlist host are required.";
            return;
        }
        _connectionDraft.Name = _connectionDraft.Name.Trim();
        if (LoginProfiles.Connections.Any(p => p.Id != _connectionDraft.Id &&
                string.Equals(p.Name, _connectionDraft.Name,
                    StringComparison.OrdinalIgnoreCase)))
        {
            _loginProfileStatus = "Connection names must be unique.";
            return;
        }
        _connectionDraft.RealmdHost = _connectionDraft.RealmdHost.Trim();
        LaunchConfigurationSetting? activeLaunch = ActiveLaunchConfiguration();
        bool updatesActiveConnection = activeLaunch is not null
            ? activeLaunch.ConnectionId == _connectionDraft.Id
            : LoginProfiles.ActiveConnectionId == _connectionDraft.Id;
        int index = LoginProfiles.Connections.FindIndex(p => p.Id == _connectionDraft.Id);
        ConnectionProfileSetting saved = Clone(_connectionDraft);
        if (index < 0) LoginProfiles.Connections.Add(saved);
        else LoginProfiles.Connections[index] = saved;
        if (useNow)
        {
            LoginProfiles.ActiveConnectionId = saved.Id;
            if (activeLaunch is not null) activeLaunch.ConnectionId = saved.Id;
        }

        if (useNow || updatesActiveConnection)
        {
            // The network client captures its endpoint when it is constructed.
            // Recreate it when the saved record backs the active launch profile;
            // otherwise a changed host/port cannot take effect until a restart.
            SettingsFile?.Save();
            RebuildNetworkClientForProfiles(allowAutoLogin: false);
        }
        else
        {
            SettingsFile?.Save();
            _loginProfileStatus = "Connection saved.";
        }
    }

    private void StartConnectionTest(ConnectionProfileSetting draft)
    {
        string host = draft.RealmdHost.Trim();
        int port = Math.Clamp(draft.RealmdPort, 1, 65535);
        int timeout = Math.Clamp(draft.TimeoutMs, 1000, 120000);
        if (host.Length == 0)
        {
            _loginProfileStatus = "A realmlist host is required.";
            return;
        }

        _loginProfileStatus = $"Testing {host}:{port}...";
        _connectionTestTask = Task.Run(async () =>
        {
            using var client = new TcpClient();
            using var timeoutSource = new CancellationTokenSource(timeout);
            try
            {
                await client.ConnectAsync(host, port, timeoutSource.Token);
                return $"Connected to {host}:{port}.";
            }
            catch (OperationCanceledException)
            {
                return $"Timed out connecting to {host}:{port}.";
            }
            catch (Exception ex)
            {
                return $"Could not connect to {host}:{port}: {ex.Message}";
            }
        });
    }

    private void SaveLaunchDraft(bool useNow)
    {
        if (_launchDraft is null || string.IsNullOrWhiteSpace(_launchDraft.Name))
        {
            _loginProfileStatus = "A configuration name is required.";
            return;
        }
        if (_launchDraft.Mode == LaunchModeClient &&
            !LoginProfiles.Connections.Any(p => p.Id == _launchDraft.ConnectionId))
        {
            _loginProfileStatus = "Choose a connection.";
            return;
        }
        if (_launchDraft.Mode == LaunchModeClient && _launchDraft.AutoLogin &&
            (string.IsNullOrWhiteSpace(_launchDraft.Account) ||
             !_launchDraft.SavePassword || string.IsNullOrEmpty(_launchDraft.Password)))
        {
            _loginProfileStatus =
                "Automatic login requires an account and a locally saved password.";
            return;
        }
        if (_launchDraft.Mode == LaunchModeClient && _launchDraft.AutoEnterWorld &&
            string.IsNullOrWhiteSpace(_launchDraft.Character))
        {
            _loginProfileStatus = "Automatic world entry requires a character name.";
            return;
        }
        _launchDraft.Name = _launchDraft.Name.Trim();
        if (LoginProfiles.LaunchConfigurations.Any(p => p.Id != _launchDraft.Id &&
                string.Equals(p.Name, _launchDraft.Name,
                    StringComparison.OrdinalIgnoreCase)))
        {
            _loginProfileStatus = "Launch configuration names must be unique.";
            return;
        }
        if (!_launchDraft.SavePassword) _launchDraft.Password = "";
        if (!_launchDraft.AutoEnterWorld) _launchDraft.Character = "";
        bool updatesActiveLaunch =
            LoginProfiles.ActiveLaunchConfigurationId == _launchDraft.Id;
        int index = LoginProfiles.LaunchConfigurations.FindIndex(
            p => p.Id == _launchDraft.Id);
        LaunchConfigurationSetting saved = Clone(_launchDraft);
        if (index < 0) LoginProfiles.LaunchConfigurations.Add(saved);
        else LoginProfiles.LaunchConfigurations[index] = saved;
        if (useNow)
        {
            LoginProfiles.ActiveLaunchConfigurationId = saved.Id;
            LoginProfiles.ActiveConnectionId = saved.ConnectionId;
        }

        if (useNow || updatesActiveLaunch)
        {
            SettingsFile?.Save();
            RebuildNetworkClientForProfiles(allowAutoLogin: false);
        }
        else
        {
            SettingsFile?.Save();
            _loginProfileStatus = "Launch configuration saved.";
        }
    }

    private void DeleteConnection(string id)
    {
        if (LoginProfiles.Connections.Count <= 1)
        {
            _loginProfileStatus = "At least one connection is required.";
            return;
        }
        if (LoginProfiles.LaunchConfigurations.Any(p => p.ConnectionId == id))
        {
            _loginProfileStatus = "That connection is used by a launch configuration.";
            return;
        }
        LoginProfiles.Connections.RemoveAll(p => p.Id == id);
        RepairActiveProfileIds();
        _connectionDraft = Clone(LoginProfiles.Connections[0]);
        SettingsFile?.Save();
        _loginProfileStatus = "Connection deleted.";
    }

    private void DeleteLaunchConfiguration(string id)
    {
        if (LoginProfiles.LaunchConfigurations.Count <= 1)
        {
            _loginProfileStatus = "At least one launch configuration is required.";
            return;
        }
        LoginProfiles.LaunchConfigurations.RemoveAll(p => p.Id == id);
        RepairActiveProfileIds();
        _launchDraft = Clone(LoginProfiles.LaunchConfigurations[0]);
        SettingsFile?.Save();
        _loginProfileStatus = "Launch configuration deleted.";
    }

    private string UniqueConnectionName(string seed) => UniqueName(seed,
        LoginProfiles.Connections.Select(p => p.Name));
    private string UniqueLaunchName(string seed) => UniqueName(seed,
        LoginProfiles.LaunchConfigurations.Select(p => p.Name));

    private static string UniqueName(string seed, IEnumerable<string> existing)
    {
        var names = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(seed)) return seed;
        for (int i = 2;; i++)
        {
            string candidate = $"{seed} {i}";
            if (!names.Contains(candidate)) return candidate;
        }
    }

    private static ConnectionProfileSetting Clone(ConnectionProfileSetting p) => new()
    {
        Id = p.Id, Name = p.Name, RealmdHost = p.RealmdHost,
        RealmdPort = p.RealmdPort, Realm = p.Realm,
        WorldPortFallback = p.WorldPortFallback,
        WorldUsesRealmdHost = p.WorldUsesRealmdHost,
        TimeoutMs = p.TimeoutMs, RealPortals = p.RealPortals,
    };

    private static LaunchConfigurationSetting Clone(LaunchConfigurationSetting p) => new()
    {
        Id = p.Id, Name = p.Name, ConnectionId = p.ConnectionId, Mode = p.Mode,
        AutoLogin = p.AutoLogin, Account = p.Account, SavePassword = p.SavePassword,
        Password = p.Password, AutoEnterWorld = p.AutoEnterWorld,
        Character = p.Character,
    };

}
