using MSUIClient;
using MSUIClient.Formats;

internal static class SpellClassificationClinicalChecks
{
    public static void Run()
    {
        using var mpq = new MpqMount(Path.Combine(ClientConfig.FindRepoRoot(), "GameData", "Data"));
        SpellCatalog spells = SpellCatalog.Load(mpq) ?? throw new InvalidDataException("Spell DBC unavailable");
        foreach (var (id, expected) in new (uint, string)[]
        {
            (9437, "CAST_TIME"), // Placing Bear Trap: interrupt flags do not imply a channel.
            (133, "CAST_TIME"), (1459, "INSTANT"),
            (5143, "CHANNEL"), (10, "CHANNEL"), (1515, "CHANNEL")
        })
        {
            if (!spells.TryGet(id, out SpellInfo spell)) throw new InvalidDataException($"Missing spell {id}");
            Console.WriteLine($"SpellClassification {id} {spell.Name}: {spell.CastClassification}; " +
                $"attributesEx=0x{spell.AttributesEx:X}; channelInterrupts=0x{spell.ChannelInterruptFlags:X}");
            Check(spell.CastClassification == expected, $"Spell {id}: expected {expected}, got {spell.CastClassification}");
        }

        spells.TryGet(9437, out SpellInfo trap);
        Check((trap with { AttributesEx = 0, ChannelInterruptFlags = 8 }).CastClassification == "CAST_TIME",
            "Interrupt policy alone must not classify a channel");
        foreach (uint attribute in new uint[] { 4, 64 })
            Check((trap with { AttributesEx = attribute, ChannelInterruptFlags = 0 }).CastClassification == "CHANNEL",
                "Both authored channel attributes must work without interrupt flags");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
