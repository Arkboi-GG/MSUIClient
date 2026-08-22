using MSUIClient;
using MSUIClient.Formats;
using MSUIClient.Net;
using MSUIClient.Engine;

internal static class NpcGreetingClinicalChecks
{
    public static void Run()
    {
        NpcGreetingCatalog catalog = NpcGreetingCatalog.FromRows(
            (793, 50, 5977, 5978, 5979),
            (89, 161, 7094, 0, 7095));
        Check(catalog.Count == 2 &&
              catalog.TryGet(793, out NpcGreeting human) &&
              human == new NpcGreeting(5977, 5978, 5979) &&
              catalog.TryGet(89, out NpcGreeting partial) && partial.Goodbye == 0 &&
              !catalog.TryGet(26, out _),
            "CreatureDisplayInfo.NPCSoundID -> NPCSounds join drift");

        for (int sequence = 0; sequence < 5; sequence++)
        {
            NpcSelectVocal line = NpcGreetingLaw.SelectLine(sequence, 3);
            Check(line == new NpcSelectVocal(NpcSelectVocalKind.Hello, null,
                    sequence + 1),
                $"select hello sequence {sequence} drift");
        }
        Check(NpcGreetingLaw.SelectLine(5, 3) ==
                  new NpcSelectVocal(NpcSelectVocalKind.Pissed, 0, 6) &&
              NpcGreetingLaw.SelectLine(6, 3) ==
                  new NpcSelectVocal(NpcSelectVocalKind.Pissed, 1, 7) &&
              NpcGreetingLaw.SelectLine(7, 3) ==
                  new NpcSelectVocal(NpcSelectVocalKind.Pissed, 2, 8) &&
              NpcGreetingLaw.SelectLine(8, 3) ==
                  new NpcSelectVocal(NpcSelectVocalKind.Hello, null, 0) &&
              NpcGreetingLaw.SelectLine(5, 0) ==
                  new NpcSelectVocal(NpcSelectVocalKind.Hello, null, 0),
            "five-hello/pissed-variants/wrapping-hello law drift");

        Check(NpcGreetingLaw.WindowTransition(0, 1) ==
                  new NpcWindowVocal(NpcWindowVocalKind.Hello, 1) &&
              NpcGreetingLaw.WindowTransition(1, 0) ==
                  new NpcWindowVocal(NpcWindowVocalKind.Goodbye, 1) &&
              NpcGreetingLaw.WindowTransition(1, 2) ==
                  new NpcWindowVocal(NpcWindowVocalKind.Hello, 2) &&
              NpcGreetingLaw.WindowTransition(1, 1) ==
                  new NpcWindowVocal(NpcWindowVocalKind.None, 0),
            "SetActiveNPC open/close/swap suppression law drift");

        string root = ClientConfig.FindRepoRoot();
        string targeting = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Combat",
            "GameLoop.Targeting.cs"));
        string runtime = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.NpcGreetings.cs"));
        Check(targeting.IndexOf("RequestNpcSelectionGreeting(picked);", StringComparison.Ordinal) <
              targeting.IndexOf("CommitSelection(picked, beginAttack: false); // empty left clears",
                  StringComparison.Ordinal),
            "left-click greeting must precede SetSelection path");
        Check(runtime.Contains("if (_trainer is not null) return _trainer.TrainerGuid;",
                  StringComparison.Ordinal) &&
              runtime.Contains("if (_binderConfirmOpen) return _binderGuid;",
                  StringComparison.Ordinal) &&
              runtime.Contains("_spellSounds?.Stop(voice);", StringComparison.Ordinal) &&
              runtime.Contains("if (NpcGreetingVoiceLive(guid)) return;", StringComparison.Ordinal),
            "interaction union, despawn stop, or per-unit latch drift");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
