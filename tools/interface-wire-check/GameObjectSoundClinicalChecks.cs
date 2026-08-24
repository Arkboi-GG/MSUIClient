using MSUIClient;
using MSUIClient.Formats;
using MSUIClient.Net;

internal static class GameObjectSoundClinicalChecks
{
    public static void Run()
    {
        uint[] slots = new uint[10];
        slots[1] = 101; slots[3] = 303; slots[6] = 3355;
        GameObjectSoundCatalog catalog = GameObjectSoundCatalog.FromRows((668, slots));
        Check(catalog.Count == 1 && catalog.Sound(668, 1) == 101 &&
              catalog.Sound(668, 3) == 303 && catalog.Sound(668, 6) == 3355 &&
              catalog.Sound(999, 1) == 0,
            "GameObjectDisplayInfo Sound0..9 lookup drift");
        Check(GameObjectSoundLaw.StateSlot(0) == 1 &&
              GameObjectSoundLaw.StateSlot(1) == 3 &&
              GameObjectSoundLaw.StateSlot(2) == -1,
            "GO_STATE open/close/active-alt slot law drift");
        Check(GameObjectSoundLaw.EventSlot("$GO0") == 0 &&
              GameObjectSoundLaw.EventSlot("$GO5") == 5 &&
              GameObjectSoundLaw.EventSlot("$GC0") == 6 &&
              GameObjectSoundLaw.EventSlot("$GC3") == 9 &&
              GameObjectSoundLaw.EventSlot("$GO6") == -1 &&
              GameObjectSoundLaw.EventSlot("$SND") == -1,
            "GameObject M2 event tag fold-back drift");

        var model = new M2Model();
        model.Sequences.Add(new M2Sequence
        {
            AnimationId = 0, StartTimestamp = 1000, EndTimestamp = 11000,
            Flags = 0,
        });
        model.Events.Add(new M2EventMarker
        {
            Identifier = "$GC0", Times = [4870],
        });
        GameObjectSlotEvent[] first = GameObjectSoundLaw.CrossedEvents(
            model, 0, 3.8, 3.9).ToArray();
        GameObjectSlotEvent[] second = GameObjectSoundLaw.CrossedEvents(
            model, 0, 13.8, 13.9).ToArray();
        Check(first.Length == 1 && first[0].Slot == 6 &&
              Math.Abs(first[0].OccurrenceSeconds - 3.87) < 1e-9 &&
              second.Length == 1 && second[0].Slot == 6 &&
              Math.Abs(second[0].OccurrenceSeconds - 13.87) < 1e-9,
            "looped animation-event crossing drift");

        string root = ClientConfig.FindRepoRoot();
        string runtime = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.GameObjectSounds.cs"));
        string renderer = SourceText.Read(Path.Combine(root, "MSUIClient", "World", "Doodads",
            "DoodadRenderer.cs"));
        Check(runtime.Contains("_knownGameObjectSoundStates[go.Guid] = state;",
                  StringComparison.Ordinal) &&
              runtime.Contains(
                  "_gameObjectEventClocks[go.Guid] = (sequence, currentAnimationClock);",
                  StringComparison.Ordinal) &&
              runtime.Contains("forceLoop: false, trackHold: false, category: \"sfx\"",
                  StringComparison.Ordinal) &&
              renderer.Contains("TryGetDynamicEventTimeline", StringComparison.Ordinal) &&
              renderer.Contains("if (m2.Events.Count > 0) model.EventSource = m2;",
                  StringComparison.Ordinal),
            "silent-first-sight, positional one-shot, or renderer event seam drift");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
