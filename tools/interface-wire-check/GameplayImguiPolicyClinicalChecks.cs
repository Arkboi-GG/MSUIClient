using MSUIClient;
using MSUIClient.Engine.UI;

/// <summary>
/// Enforces <see cref="GameplayImguiPolicyLaw"/>: gameplay windows/modals may not use
/// ImGui's default widgets (dev surfaces are exempt). Enforcement is a ratchet — the
/// ENROLLED list below names gameplay panels held to the standard and asserts they
/// stay free of banned widgets. New/migrated panels are added here; that is how the
/// standard spreads without a big-bang rewrite of the legacy surface.
///
/// Run standalone: interface-wire-check --imgui-policy-only
/// </summary>
internal static class GameplayImguiPolicyClinicalChecks
{
    /// <summary>
    /// Gameplay UI files that MUST contain zero banned ImGui widgets. Add a panel
    /// here once it is authored/migrated to the GameText + VanillaButton vocabulary.
    /// Paths are relative to the repo root, forward-slashed.
    /// </summary>
    private static readonly string[] EnrolledCleanFiles =
    [
        "MSUIClient/GameLoop/Panels/GameLoop.PartyQuestLog.cs",
        "MSUIClient/GameLoop/Panels/GameLoop.RaidInfoPanel.cs",
        "MSUIClient/GameLoop/Panels/GameLoop.StablePanel.cs",
        "MSUIClient/GameLoop/Panels/GameLoop.Companions.cs",
        "MSUIClient/GameLoop/Hud/GameLoop.HudFrames.cs",
        "MSUIClient/GameLoop/Hud/GameLoop.HudLayoutEditor.cs",
        "MSUIClient/GameLoop/Panels/GameLoop.Macro.cs",
    ];

    public static void Run()
    {
        // --- the law's detector is itself sound ---
        Check(GameplayImguiPolicyLaw.BannedWidgets.Contains("Button") &&
              GameplayImguiPolicyLaw.BannedWidgets.Contains("Text") &&
              GameplayImguiPolicyLaw.BannedWidgets.Contains("BeginTable") &&
              GameplayImguiPolicyLaw.BannedWidgets.Contains("Separator") &&
              GameplayImguiPolicyLaw.BannedWidgets.Contains("SetTooltip") &&
              // Host/input primitives must NOT be treated as banned — a vanilla frame
              // needs them, and flagging them would make the policy unfollowable.
              !GameplayImguiPolicyLaw.BannedWidgets.Contains("Begin") &&
              !GameplayImguiPolicyLaw.BannedWidgets.Contains("InvisibleButton") &&
              !GameplayImguiPolicyLaw.BannedWidgets.Contains("GetWindowDrawList"),
            "GameplayImguiPolicyLaw banned/allowed widget partition drift");

        Check(GameplayImguiPolicyLaw.Scan("ImGui.Button(\"x\", size);").Count == 1 &&
              GameplayImguiPolicyLaw.Scan("ImGui.BeginTable(\"t\", 2);").Count == 1 &&
              GameplayImguiPolicyLaw.Scan(
                  "GameText.Draw(dl, f, s, p, sc); VanillaButton(dl, id, c, m, sz, sc); " +
                  "ImGui.Begin(id); ImGui.InvisibleButton(id, sz); ImGui.GetWindowDrawList();")
                  .Count == 0,
            "GameplayImguiPolicyLaw.Scan mis-detects widgets vs host primitives");

        // Line numbers are reported so a failure points at the exact call.
        IReadOnlyList<GameplayImguiPolicyLaw.Usage> sample =
            GameplayImguiPolicyLaw.Scan("ok\nImGui.Text(\"y\");\n");
        Check(sample.Count == 1 && sample[0].Member == "Text" && sample[0].Line == 2,
            "GameplayImguiPolicyLaw.Scan line/member reporting drift");

        // --- dev surfaces are excluded, gameplay panels are not ---
        Check(GameplayImguiPolicyLaw.IsDevExcludedPath(
                  "MSUIClient/GameLoop/Dev/GameLoop.DevWindow.cs") &&
              GameplayImguiPolicyLaw.IsDevExcludedPath(
                  "MSUIClient\\GameLoop\\CreatorMode\\GameLoop.Creator.cs") &&
              !GameplayImguiPolicyLaw.IsDevExcludedPath(
                  "MSUIClient/GameLoop/Panels/GameLoop.RaidInfoPanel.cs"),
            "GameplayImguiPolicyLaw dev-exclusion classification drift");

        // --- enrolled gameplay panels carry zero banned widgets ---
        string root = ClientConfig.FindRepoRoot();
        foreach (string relative in EnrolledCleanFiles)
        {
            string full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            string source = SourceText.Read(full);
            IReadOnlyList<GameplayImguiPolicyLaw.Usage> usages =
                GameplayImguiPolicyLaw.Scan(source);
            Check(usages.Count == 0, GameplayImguiPolicyLaw.Describe(relative, usages));
        }

        // --- and the raid panel positively uses the vanilla vocabulary ---
        string raidPanel = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop",
            "Panels", "GameLoop.RaidInfoPanel.cs"));
        Check(raidPanel.Contains("DrawVanillaPanelChrome(", StringComparison.Ordinal) &&
              raidPanel.Contains("GameText.Draw(", StringComparison.Ordinal) &&
              raidPanel.Contains("VanillaButton(", StringComparison.Ordinal),
            "Raid Info panel must render via DrawVanillaPanelChrome/GameText/VanillaButton");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
