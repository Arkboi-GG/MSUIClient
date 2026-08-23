using System.Numerics;
using MSUIClient;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;

internal static class TrainerFrameClinicalChecks
{
    public static void Run()
    {
        Check(TrainerFrameUiLaw.FrameOrigin(1.5f) == new Vector2(0, 156) &&
              TrainerFrameUiLaw.FrameSize(1.5f) == new Vector2(576, 768) &&
              TrainerFrameUiLaw.PortraitOffset == new Vector2(7, 6) &&
              TrainerFrameUiLaw.PortraitSize == 60 &&
              TrainerFrameUiLaw.Title("") == "Trainer" &&
              TrainerFrameUiLaw.Title("  Woo Ping  ") == "Woo Ping" &&
              TrainerFrameUiLaw.PurseRightTop == new Vector2(180, 413) &&
              TrainerFrameUiLaw.DetailCostLabel == new Vector2(30, 340),
            "trainer identity/window geometry drift");

        var available = new TrainerFrameUiLaw.ServiceNode(0, 26, "Arms", "Heroic Strike", 0, 1);
        var used = new TrainerFrameUiLaw.ServiceNode(1, 26, "Arms", "Cleave", 2, 20);
        var unavailable = new TrainerFrameUiLaw.ServiceNode(2, 256, "Fury", "Bloodrage", 1, 10);
        IReadOnlyList<TrainerFrameUiLaw.TreeRow> tree = TrainerFrameUiLaw.BuildTree(
            [available, used, unavailable], 0, new HashSet<uint>(), true, true, false);
        Check(tree.Select(row => row.Text).SequenceEqual(
                new[] { "Arms", "Heroic Strike", "Fury", "Bloodrage" }) &&
              TrainerFrameUiLaw.BuildTree([available, used, unavailable], 0,
                  new HashSet<uint> { 26 }, true, true, true)
                  .Select(row => row.Text).SequenceEqual(new[] { "Arms", "Fury", "Bloodrage" }),
            "trainer filter/collapsible skill-line tree drift");

        SpellInfo wrapper = new(Id: 1000, Name: "Teach", Rank: "", IconPath: "",
            Attributes: 0, AttributesEx2: 0, AttributesEx3: 0,
            InterruptFlags: 0, ChannelInterruptFlags: 0, Targets: 0, ImplicitTarget: 0,
            RecoveryMs: 0, CategoryRecoveryMs: 0, PowerType: 0, ManaCost: 0,
            ManaCostPercent: 0, StartRecoveryCategory: 0, StartRecoveryMs: 0,
            VisualId: 0, Speed: 0, Description: "", RangeIndex: 0,
            EffectIds: [36u, 0u, 0u], EffectTriggerSpells: [2457u, 0u, 0u]);
        Check(TrainerFrameUiLaw.TaughtSpell(wrapper) == 2457 &&
              TrainerFrameUiLaw.ServiceGroup(2, 0, wrapper, null).Name == "Recipes",
            "trainer taught-spell/group-type law drift");

        string root = ClientConfig.FindRepoRoot();
        string runtime = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Trainer.cs"));
        Check(runtime.Contains("UiPanelFrameOrigin(UiPanelOwnershipRegistry[4], scale)",
                  StringComparison.Ordinal) &&
              runtime.Contains("DrawUnitPortraitImage", StringComparison.Ordinal) &&
              runtime.Contains("DrawNpcModalTitle", StringComparison.Ordinal) &&
              runtime.Contains("DrawTrainerMoney", StringComparison.Ordinal) &&
              runtime.Contains("TrainerFrameUiLaw.BuildTree", StringComparison.Ordinal) &&
              runtime.Contains("DrawTrainerFilterMenu", StringComparison.Ordinal) &&
              runtime.Contains("PlayUiSound(TrainerFrameUiLaw.OpenSound",
                  StringComparison.Ordinal) &&
              runtime.Contains("CloseTrainerSession", StringComparison.Ordinal) &&
              !runtime.Contains("DrawCenteredText(dl,origin+new Vector2(192,17)*scale,\"Trainer\"",
                  StringComparison.Ordinal) &&
              !runtime.Contains("$\"Cost: {FormatMoney", StringComparison.Ordinal),
            "trainer production window bypasses title/portrait/money/sound law");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
