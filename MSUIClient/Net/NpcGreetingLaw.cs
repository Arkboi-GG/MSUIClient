namespace MSUIClient.Net;

public enum NpcSelectVocalKind { Hello, Pissed }

public readonly record struct NpcSelectVocal(
    NpcSelectVocalKind Kind, int? Variation, int NextSequence);

public enum NpcWindowVocalKind { None, Hello, Goodbye }

public readonly record struct NpcWindowVocal(NpcWindowVocalKind Kind, ulong Guid);

/// <summary>Pure, byte-verified selection-cycle and SetActiveNPC transition laws.</summary>
public static class NpcGreetingLaw
{
    public const int HelloTakes = 5;

    public static NpcSelectVocal SelectLine(int sequence, int pissedVariations)
    {
        sequence = Math.Max(0, sequence);
        pissedVariations = Math.Max(0, pissedVariations);
        if (sequence < HelloTakes)
            return new(NpcSelectVocalKind.Hello, null, sequence + 1);
        int variation = sequence - HelloTakes;
        if (variation < pissedVariations)
            return new(NpcSelectVocalKind.Pissed, variation, sequence + 1);
        return new(NpcSelectVocalKind.Hello, null, 0);
    }

    public static NpcWindowVocal WindowTransition(ulong previous, ulong active)
    {
        if (previous == active) return new(NpcWindowVocalKind.None, 0);
        if (active != 0) return new(NpcWindowVocalKind.Hello, active);
        if (previous != 0) return new(NpcWindowVocalKind.Goodbye, previous);
        return new(NpcWindowVocalKind.None, 0);
    }
}
