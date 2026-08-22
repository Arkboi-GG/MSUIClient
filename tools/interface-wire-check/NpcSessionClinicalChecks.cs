using MSUIClient;
using MSUIClient.Engine.UI;

internal static class NpcSessionClinicalChecks
{
    public static void Run()
    {
        Check(NpcSessionUiLaw.InRange(30.864f) &&
              !NpcSessionUiLaw.InRange(MathF.BitIncrement(30.864f)) &&
              !NpcSessionUiLaw.ShouldClose(true, false, false, float.PositiveInfinity) &&
              NpcSessionUiLaw.ShouldClose(true, true, false, float.PositiveInfinity) &&
              NpcSessionUiLaw.ShouldClose(true, true, true, MathF.BitIncrement(30.864f)) &&
              !NpcSessionUiLaw.ShouldClose(true, true, true, 30.864f),
            "shared NPC session range/lifetime drift");

        string root = ClientConfig.FindRepoRoot();
        string program = SourceText.Read(Path.Combine(root, "MSUIClient", "Program.cs"));
        foreach (string lifecycle in new[]
        {
            "UpdateQuestNpcLifecycle()", "UpdateVendorLifecycle()", "UpdateGossipLifecycle()",
            "UpdateTrainerLifecycle()", "UpdateTaxiLifecycle()", "UpdateBankLifecycle()",
        })
            Check(program.Contains(lifecycle, StringComparison.Ordinal),
                $"missing always-pumped lifecycle: {lifecycle}");

        string[] sources =
        {
            "GameLoop.Gossip.cs", "GameLoop.Trainer.cs", "GameLoop.Taxi.cs",
            "GameLoop.Bank.cs", "GameLoop.Mail.cs", "GameLoop.Quest.cs",
            "GameLoop.Vendor.Session.cs", "GameLoop.Hearth.cs",
        };
        foreach (string source in sources)
        {
            string runtime = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
                source));
            Check(runtime.Contains("NpcSessionUiLaw", StringComparison.Ordinal) ||
                  runtime.Contains("BinderConfirmUiLaw", StringComparison.Ordinal),
                $"session source bypasses shared law: {source}");
        }

        string taxi = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Taxi.cs"));
        Check(taxi.Contains("if (accepted) CloseTaxiMap", StringComparison.Ordinal) &&
              !taxi.Contains("_taxiStart = move.Points[0]; _taxiOpen = true", StringComparison.Ordinal),
            "accepted taxi flight must close, not reopen, the NPC map session");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
