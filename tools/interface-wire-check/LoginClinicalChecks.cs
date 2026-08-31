using System.Numerics;
using MSUIClient;
using MSUIClient.Engine;
using MSUIClient.Engine.UI;

internal static class LoginClinicalChecks
{
    public static void Run()
    {
        LoginUiLaw.ScreenRect host = LoginUiLaw.Host(new Vector2(1920f, 1080f));
        LoginUiLaw.ScreenRect tuning = LoginUiLaw.TuningWindow;
        Check(host.Min == Vector2.Zero && host.Size == new Vector2(1920f, 1080f) &&
              tuning.Min == new Vector2(48f, 48f) && tuning.Size == new Vector2(380f, 0f),
            "login host/tuning window geometry law drift");

        LoginUiLaw.DialogLayout oneLine = LoginUiLaw.Dialog(new Vector2(1024f, 768f), 1f, 22f);
        Check(oneLine.Frame.Min == new Vector2(256f, 330f) &&
              oneLine.Frame.Size == new Vector2(512f, 108f) &&
              oneLine.Message.Min == new Vector2(292f, 346f) &&
              oneLine.Message.Size == new Vector2(440f, 22f) &&
              oneLine.Button.Min == new Vector2(412f, 382f) &&
              oneLine.Button.Size == new Vector2(200f, 40f) &&
              LoginUiLaw.DialogHeight(44f) == 129f &&
              LoginUiLaw.FailureText("failed: Incorrect Password") == "Incorrect Password" &&
              LoginUiLaw.FailureText("") == "Unable to connect",
            "login GlueDialog authored geometry or failure text drift");

        LoginUiLaw.LaunchOptionsLayout launch =
            LoginUiLaw.LaunchOptions(new Vector2(1024f, 768f), 1f);
        Check(launch.Frame.Min == new Vector2(302f, 234f) &&
              launch.Frame.Size == new Vector2(420f, 300f) &&
              launch.PromptCenter == new Vector2(512f, 278f) &&
              launch.ConfigurationCombo.Min == new Vector2(387f, 302f) &&
              launch.ConfigurationCombo.Size == new Vector2(250f, 30f) &&
              launch.ClientButton.Min == new Vector2(387f, 346f) &&
              launch.ClientButton.Size == new Vector2(250f, 40f) &&
              launch.ClientActiveLabel == new Vector2(645f, 360f) &&
              launch.CreatorButton.Min == new Vector2(387f, 396f) &&
              launch.OkayButton.Min == new Vector2(452f, 488f) &&
              launch.OkayButton.Size == new Vector2(120f, 34f),
            "launch-options modal authored geometry drift");

        string root = ClientConfig.FindRepoRoot();
        string runtime = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Net.cs"));
        string creator = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "CreatorMode",
            "GameLoop.Creator.cs"));
        string profiles = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.LoginProfiles.cs"));
        Check(runtime.Contains("LoginUiLaw.Dialog(disp, s", StringComparison.Ordinal) &&
              runtime.Contains("LoginUiLaw.Host(disp)", StringComparison.Ordinal) &&
              runtime.Contains("LoginUiLaw.TuningWindow", StringComparison.Ordinal) &&
              runtime.Contains("DrawLoginFailureDialog", StringComparison.Ordinal) &&
              runtime.Contains("LoginUiLaw.FailureText", StringComparison.Ordinal) &&
              runtime.Contains("GlueButton(\"Okay\", dialog.Button.Size)", StringComparison.Ordinal) &&
              !runtime.Contains("failedNet.Status, cx, 519f", StringComparison.Ordinal) &&
              !runtime.Contains("new Vector2(380f, 0f)", StringComparison.Ordinal) &&
              !runtime.Contains("SetNextWindowSize(new Vector2(460, 0)", StringComparison.Ordinal),
            "login dialogs must be blocking and law-owned rather than inline/raw ImGui placement");
        Check(runtime.Contains("Settings.SavedAccountName", StringComparison.Ordinal) &&
              runtime.Contains("SettingsFile?.Save()", StringComparison.Ordinal),
            "Remember Account Name must persist through the settings store");
        Check(runtime.Contains("GlueButton(\"Manage Connection\"", StringComparison.Ordinal) &&
              runtime.Contains("GlueButton(\"Launch Configurations\"",
                  StringComparison.Ordinal) &&
              runtime.Contains("new Vector2(176f * s, small.Y)",
                  StringComparison.Ordinal) &&
              !runtime.Contains("GlueMenuButton(\"Manage Account\"", StringComparison.Ordinal) &&
              !runtime.Contains("GlueMenuButton(\"Community Site\"", StringComparison.Ordinal),
            "login connection/configuration menu replacement drift");
        Check(profiles.Contains("class GameLoop", StringComparison.Ordinal) &&
              profiles.Contains("EnsureLoginProfilesInitialized", StringComparison.Ordinal) &&
              profiles.Contains("_config.RealmdHost = connection.RealmdHost.Trim()",
                  StringComparison.Ordinal) &&
              profiles.Contains("launch.SavePassword ? launch.Password : \"\"",
                  StringComparison.Ordinal) &&
              profiles.Contains("Enter world automatically", StringComparison.Ordinal) &&
              profiles.Contains("Save & Use", StringComparison.Ordinal) &&
              profiles.Contains("WowSkin.Dialog", StringComparison.Ordinal) &&
              profiles.Contains("WowSkin.GlueEditBox", StringComparison.Ordinal) &&
              profiles.Contains("_skin.HeaderPlaque", StringComparison.Ordinal) &&
              profiles.Contains("_skin.GlueButton", StringComparison.Ordinal) &&
              profiles.Contains("_skin!.CheckBox", StringComparison.Ordinal) &&
              profiles.Contains("dl.AddRectFilled(frameMin + fillInset",
                  StringComparison.Ordinal) &&
              profiles.Contains("dl.AddRectFilled(min + inset",
                  StringComparison.Ordinal) &&
              profiles.Contains("\"scroll.dn.dn\"", StringComparison.Ordinal) &&
              profiles.Contains("_skin.GlueImageUv", StringComparison.Ordinal) &&
              profiles.Contains("boxMin.X + 11f * s", StringComparison.Ordinal) &&
              !profiles.Contains("ImGui.Combo", StringComparison.Ordinal) &&
              !profiles.Contains("ImGui.SetWindowFontScale(s)",
                  StringComparison.Ordinal) &&
              !profiles.Contains("ImGuiWindowFlags.NoCollapse", StringComparison.Ordinal) &&
              !profiles.Contains("ImGui.Selectable", StringComparison.Ordinal),
            "connection/launch profile runtime wiring drift");
        Check(creator.Contains("LoginUiLaw.LaunchOptions(disp, s)", StringComparison.Ordinal) &&
              creator.Contains("UseLaunchConfiguration(", StringComparison.Ordinal) &&
              !creator.Contains("float w = 420f * s", StringComparison.Ordinal) &&
              !creator.Contains("var bSize = new Vector2(250f * s", StringComparison.Ordinal),
            "launch-options modal geometry must stay in LoginUiLaw");

        string settingsPath = Path.Combine(Path.GetTempPath(),
            $"msui-login-account-{Guid.NewGuid():N}.json");
        try
        {
            SettingsStore store = SettingsStore.Load(root, settingsPath);
            store.Settings.SavedAccountName = "RememberedAccount";
            store.LoginProfiles.ActiveConnectionId = "home";
            store.LoginProfiles.ActiveLaunchConfigurationId = "raid";
            store.LoginProfiles.Connections.Add(new ConnectionProfileSetting
            {
                Id = "home", Name = "Home Server", RealmdHost = "127.0.0.1",
                RealmdPort = 3724, Realm = "Barrens Chat", WorldPortFallback = 8085,
                WorldUsesRealmdHost = true, TimeoutMs = 9000, RealPortals = true,
            });
            store.LoginProfiles.LaunchConfigurations.Add(new LaunchConfigurationSetting
            {
                Id = "raid", Name = "Raid Night", ConnectionId = "home", Mode = "Client",
                AutoLogin = true, Account = "NICO", SavePassword = true,
                Password = "local-home-password", AutoEnterWorld = true,
                Character = "Testwar",
            });
            store.Save();
            SettingsStore restored = SettingsStore.Load(root, settingsPath);
            Check(restored.Settings.SavedAccountName == "RememberedAccount",
                "Remember Account Name settings round-trip drift");
            Check(restored.LoginProfiles.ActiveConnectionId == "home" &&
                  restored.LoginProfiles.ActiveLaunchConfigurationId == "raid" &&
                  restored.LoginProfiles.Connections is
                      [{ Name: "Home Server", RealmdHost: "127.0.0.1", RealmdPort: 3724 }] &&
                  restored.LoginProfiles.LaunchConfigurations is
                      [{ Name: "Raid Night", AutoLogin: true, SavePassword: true,
                         Password: "local-home-password", AutoEnterWorld: true,
                         Character: "Testwar" }],
                "connection/launch profiles or optional local password round-trip drift");
        }
        finally
        {
            if (File.Exists(settingsPath)) File.Delete(settingsPath);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
