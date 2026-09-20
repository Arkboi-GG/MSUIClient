using MSUIClient.Engine.UI;

internal static class RegisteredUiPanelClinicalChecks
{
    public static void Run()
    {
        var character = new UiPanelOwnershipLaw.Panel("Character", UiPanelOwnershipLaw.Area.Left);
        var book = new UiPanelOwnershipLaw.Panel("Book", UiPanelOwnershipLaw.Area.Left);
        var craft = new UiPanelOwnershipLaw.Panel("Craft", UiPanelOwnershipLaw.Area.Left, 4);
        var bank = new UiPanelOwnershipLaw.Panel("Bank", UiPanelOwnershipLaw.Area.Left, 6);
        var vendor = new UiPanelOwnershipLaw.Panel("Vendor", UiPanelOwnershipLaw.Area.Left);
        var menu = new UiPanelOwnershipLaw.Panel("Menu", UiPanelOwnershipLaw.Area.Center);
        var map = new UiPanelOwnershipLaw.Panel("Map", UiPanelOwnershipLaw.Area.Fullscreen);

        Check([craft], character, false, null, [character, craft], UiPanelHostTransition.Outcome.Opened);
        Check([vendor], character, false, null, [character], UiPanelHostTransition.Outcome.Opened);
        Check([character, bank], book, false, null, [book, bank], UiPanelHostTransition.Outcome.Opened);
        Check([menu], character, false, null, [menu], UiPanelHostTransition.Outcome.RefusedByNativeCenter);
        Check([map], book, false, null, [map], UiPanelHostTransition.Outcome.RefusedByFullscreen);
        Check([craft], character, true, null, [craft], UiPanelHostTransition.Outcome.RefusedWhileDead);
        Check([], character, false, "profession-opener-kind-not-retained", [],
            UiPanelHostTransition.Outcome.ObservationUnknown);
        Check([vendor], character, false, null, [vendor],
            UiPanelHostTransition.Outcome.DisplacementCallbackFailed, refuseClose: true);
        Check([vendor], character, false, null, [vendor],
            UiPanelHostTransition.Outcome.DisplacementNotConfirmed, keepClosedHost: true);
        Check([craft], character, false, null, [craft],
            UiPanelHostTransition.Outcome.OpenNotConfirmed, keepIncomingHidden: true);
    }

    private static void Check(UiPanelOwnershipLaw.Panel[] initial,
        UiPanelOwnershipLaw.Panel incoming, bool dead, string? unresolved,
        UiPanelOwnershipLaw.Panel[] expected, UiPanelHostTransition.Outcome outcome,
        bool refuseClose = false, bool keepClosedHost = false, bool keepIncomingHidden = false)
    {
        var observer = new UiPanelOwnershipObserver();
        var visible = new List<UiPanelOwnershipLaw.Panel>();
        foreach (var panel in initial)
        {
            visible.Add(panel);
            observer.Observe(new(visible.ToArray(), false));
        }
        int callbacks = 0;
        var host = new UiPanelHostTransition.Host(incoming, () => true,
            () => { callbacks++; if (!keepIncomingHidden) visible.Add(incoming); return true; },
            () => true, () => false);
        var result = UiPanelRegisteredHostTransition.Show(observer,
            () => new(visible.ToArray(), dead, unresolved), host,
            id =>
            {
                callbacks++;
                if (refuseClose) return false;
                if (!keepClosedHost) visible.RemoveAll(panel => panel.Id == id);
                return true;
            });
        if (result.Outcome != outcome || visible.Count != expected.Length ||
            !expected.All(visible.Contains))
            throw new InvalidDataException($"Registered panel transition {string.Join(',', initial.Select(p => p.Id))} -> {incoming.Id}: {result.Outcome}, expected {outcome}");
        if (result.Succeeded && (result.Observation.Seats.Left != incoming ||
            (expected.Length == 2 && result.Observation.Seats.Center != expected[1])))
            throw new InvalidDataException("Registered panel transition confirmed incorrect seats");
        if (outcome is UiPanelHostTransition.Outcome.RefusedByNativeCenter or
            UiPanelHostTransition.Outcome.RefusedByFullscreen or UiPanelHostTransition.Outcome.RefusedWhileDead or
            UiPanelHostTransition.Outcome.ObservationUnknown && callbacks != 0)
            throw new InvalidDataException("Registered panel refusal invoked a native mutation");
    }
}
