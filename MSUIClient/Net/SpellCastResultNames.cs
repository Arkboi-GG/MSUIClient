namespace MSUIClient.Net;

public static class SpellCastResultNames
{
    private static readonly IReadOnlyDictionary<byte, string> Known = new Dictionary<byte, string>
    {
        [0x13] = "SPELL_FAILED_CASTER_DEAD",
        [0x17] = "SPELL_FAILED_DONT_REPORT",
        [0x23] = "SPELL_FAILED_INTERRUPTED",
        [0x24] = "SPELL_FAILED_INTERRUPTED_COMBAT",
        [0x2F] = "SPELL_FAILED_LINE_OF_SIGHT",
        [0x39] = "SPELL_FAILED_NOT_MOUNTED",
        [0x3C] = "SPELL_FAILED_NOT_READY",
        [0x4D] = "SPELL_FAILED_NO_POWER",
        [0x59] = "SPELL_FAILED_OUT_OF_RANGE",
        [0x5C] = "SPELL_FAILED_TOTEMS",
        [0x61] = "SPELL_FAILED_SPELL_IN_PROGRESS",
        [0x78] = "SPELL_FAILED_REAGENTS",
    };

    public static string Name(byte reason) => Known.TryGetValue(reason, out string? name)
        ? name : $"SPELL_FAILED_0x{reason:X2}";

    public static string Text(byte reason, string powerName = "power") => reason switch
    {
        0x13 => "You are dead.",
        0x17 => "",
        0x23 or 0x24 => "Interrupted",
        0x2F => "Target not in line of sight",
        0x3C => "Spell is not ready yet.",
        0x4D => $"Not enough {powerName.ToLowerInvariant()}",
        0x59 => "Out of range.",
        0x5C => "Requires tools",
        0x61 => "Another action is in progress",
        0x78 => "Missing reagent",
        _ => "Spell failed.",
    };
}
