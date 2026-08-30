using System.Text.RegularExpressions;
using MSUIClient;

internal static class KeyBindingRegistryClinicalChecks
{
    public static void Run()
    {
        string root = ClientConfig.FindRepoRoot();
        string source = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop",
            "Panels", "GameLoop.Bindings.cs"));
        MatchCollection matches = Regex.Matches(source,
            "\\(\\\"(?<category>[^\\\"]+)\\\", GameBinding\\.(?<binding>[A-Za-z0-9]+),");
        var rows = matches.Select(match => (
            Category: match.Groups["category"].Value,
            Binding: match.Groups["binding"].Value)).ToArray();
        string[] categories = rows.Select(row => row.Category).Distinct().ToArray();
        // Benilla's nine, IN ITS ORDER, and then MSUI's own two. The reference ladder is
        // asserted as a PREFIX so a drift inside it still fails; the extension is asserted
        // separately, and must stay last so the shipped list a player scrolls is unchanged
        // until they reach the commands only this client has.
        Check(categories.Take(9).SequenceEqual([
                "Movement", "Chat", "Action Bar", "Targeting", "Interface",
                "Miscellaneous", "Camera", "MultiActionBar", "Raid Targeting",
            ]),
            "Key Bindings visible category order drifted from current Benilla");
        Check(categories.Skip(9).SequenceEqual(["RTS Controls", "CRPG Controls"]),
            "the MSUI CRPG/RTS categories are missing, renamed, or no longer last");
        Check(rows.Select(row => row.Binding).Distinct().Count() == rows.Length,
            "Key Bindings registry exposes one command in more than one visible category");
        Check(rows.Contains(("Movement", "Sheath")) &&
              rows.Contains(("Miscellaneous", "ToggleUi")) &&
              rows.Count(row => row.Category == "Action Bar") == 33 &&
              rows.Contains(("Action Bar", "ShapeshiftButton10")) &&
              rows.Contains(("Action Bar", "BonusActionButton10")) &&
              rows.Contains(("Action Bar", "ToggleActionBarLock")) &&
              rows.Count(row => row.Category == "MultiActionBar") == 24 &&
              rows.Count(row => row.Category == "Raid Targeting") == 9 &&
              !source.Contains("\"MultiActionBar 1\"", StringComparison.Ordinal) &&
              !source.Contains("\"MultiActionBar 2\"", StringComparison.Ordinal),
            "Key Bindings movement/misc seats or unified multibar header drifted");

        CheckCrpgRtsRegistry(source, rows);
    }

    /// <summary>
    /// The CRPG/RTS extension. Nothing here is Benilla's, so these assert MSUI's own
    /// contract: every commander gesture reachable from the Key Bindings frame, no command
    /// left hard-coded behind it, and - the one that actually bites - every new enum member
    /// APPENDED rather than interleaved.
    /// </summary>
    private static void CheckCrpgRtsRegistry(string source,
        (string Category, string Binding)[] rows)
    {
        Check(rows.Count(row => row.Category == "RTS Controls") == 42 &&
              rows.Count(row => row.Category == "CRPG Controls") == 3,
            "the CRPG/RTS binding row count changed - a commander control was added or lost");

        // The gestures and the held modifier: the commands that only exist because
        // BindingPointerKey learned Button1/Button2 and a bare modifier ladder.
        foreach (string binding in new[]
        {
            "RtsToggleFreeView", "RtsSelect", "RtsSelectAdd", "RtsOrderMove",
            "RtsOrderQueueWaypoint", "RtsCyclePrimaryNext", "RtsCyclePrimaryPrevious",
            "RtsSaveGroup1", "RtsSaveGroup10", "RtsRecallGroup1", "RtsRecallGroup10",
            "RtsOrderFocus", "RtsOrderRegroup", "RtsOrderHold", "RtsOrderPatrol",
            "RtsOrderFormationLine", "RtsOrderFormationCircle", "RtsOrderSheath",
            "RtsCommanderMap", "RtsCastOnPrimary", "RtsRigForward", "RtsRigBackward",
            "RtsBoomZoomIn", "RtsBoomZoomOut", "RtsEncounterLab", "RtsUndoWaypoint",
        })
            Check(rows.Contains(("RTS Controls", binding)),
                $"RTS command {binding} left the Key Bindings registry");
        foreach (string binding in new[]
        {
            "CrpgTakeControl", "CrpgCycleControlNext", "CrpgCycleControlPrevious",
        })
            Check(rows.Contains(("CRPG Controls", binding)),
                $"CRPG command {binding} left the Key Bindings registry");

        // THE TRAP. ResetBindingsToDefaults stamps Control:true across the whole
        // ShapeshiftButton1..BonusActionButton10 enum RANGE. A CRPG/RTS member declared inside
        // it would silently default to a Ctrl chord and nothing would say so - the file draws
        // and saves perfectly either way. Every one of them must be declared after
        // RaidTargetNone, which is the last member outside that range.
        int lastVanilla = source.IndexOf("RaidTargetNone,", StringComparison.Ordinal);
        Check(lastVanilla > 0, "the binding enum no longer ends its vanilla run at RaidTargetNone");
        foreach (string binding in rows
            .Where(row => row.Category is "RTS Controls" or "CRPG Controls")
            .Select(row => row.Binding))
            Check(source.IndexOf(binding, StringComparison.Ordinal) > lastVanilla,
                $"{binding} is declared inside the Ctrl-stamped action-bar enum range");

        // The free-view gestures must not quietly fall back to the raw modifier reads they
        // replaced - that is what would make the Key Bindings rows decorative.
        string control = SourceText.Read(Path.Combine(ClientConfig.FindRepoRoot(), "MSUIClient",
            "GameLoop", "Scene", "GameLoop.Control.cs"));
        Check(!control.Contains("click.ShiftDown", StringComparison.Ordinal) &&
              !control.Contains("click.AltDown", StringComparison.Ordinal),
            "a free-view gesture is reading the click's raw modifiers again");
        Check(control.Contains("BindingClaimsClick(GameBinding.CrpgTakeControl", StringComparison.Ordinal) &&
              control.Contains("BindingClaimsClick(GameBinding.RtsSelectAdd", StringComparison.Ordinal) &&
              control.Contains("BindingPressedEdge(GameBinding.RtsToggleFreeView", StringComparison.Ordinal),
            "the free-view click router or its toggle stopped going through the bindings");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
