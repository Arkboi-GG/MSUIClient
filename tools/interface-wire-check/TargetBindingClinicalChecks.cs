using MSUIClient;
using MSUIClient.Engine.UI;

internal static class TargetBindingClinicalChecks
{
    public static void Run()
    {
        Check(TargetBindingLaw.ResolveToggle(10, 20, 30) == 20,
            "unit toggle must select its primary token first");
        Check(TargetBindingLaw.ResolveToggle(20, 20, 30) == 30,
            "unit toggle must select the pet when the primary is already targeted");
        Check(TargetBindingLaw.ResolveToggle(20, 20, 0) is null &&
              TargetBindingLaw.ResolveDirect(0) is null,
            "absent target tokens must be no-ops");

        ulong[] board = [0, 0, 300, 0, 0, 0, 0, 0];
        RaidMarkerIntent assign = TargetBindingLaw.ResolveRaidMarker(board, 300, 6);
        Check(assign == new RaidMarkerIntent(true, 5, 300),
            "a different raid marker must assign its zero-based wire slot");
        RaidMarkerIntent toggle = TargetBindingLaw.ResolveRaidMarker(board, 300, 3);
        Check(toggle == new RaidMarkerIntent(true, 2, 0),
            "the marker already on a target must toggle off");
        RaidMarkerIntent clear = TargetBindingLaw.ResolveRaidMarker(board, 300, 0);
        Check(clear == new RaidMarkerIntent(true, 2, 0),
            "clear must address the target's current marker slot");
        Check(!TargetBindingLaw.ResolveRaidMarker(board, 0, 1).Send &&
              !TargetBindingLaw.ResolveRaidMarker(board, 400, 0).Send,
            "missing targets and already-clear targets must not emit wire work");

        string root = ClientConfig.FindRepoRoot();
        string source = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop",
            "Panels", "GameLoop.Bindings.cs"));
        string program = SourceText.Read(Path.Combine(root, "MSUIClient", "Program.cs"));
        Check(program.Contains("UpdateDirectTargetBindings(typing)"),
            "production update loop lost the direct targeting binding dispatcher");
        foreach (string binding in new[]
                 {
                     "TargetSelf", "TargetPartyMember1", "TargetPartyMember4",
                     "TargetPet", "TargetPartyPet1", "TargetPartyPet4", "PetAttack",
                     "RaidTarget1", "RaidTarget8", "RaidTargetNone",
                 })
            Check(source.Contains($"GameBinding.{binding}"),
                $"targeting registry lost {binding}");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
