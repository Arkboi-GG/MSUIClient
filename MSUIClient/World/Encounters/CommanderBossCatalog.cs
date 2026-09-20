using System.Text.Json;

namespace MSUIClient.World.Encounters;

/// <summary>Read-only creature and script facts. A catalog entry is not a completed strategy.</summary>
public sealed record CommanderBossFact(uint Entry, string Name, uint ImmuneSchools,
    uint[] SpellIds, string[] Features);

public static class CommanderBossCatalog
{
    private static readonly Lazy<IReadOnlyDictionary<uint, CommanderBossFact>> Facts = new(() =>
    {
        using var stream = typeof(CommanderBossCatalog).Assembly.GetManifestResourceStream("MSUIClient.Encounters.boss-catalog.json")
            ?? throw new InvalidDataException("Missing boss content catalog.");
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.GetProperty("bosses").EnumerateArray().Select(b => new CommanderBossFact(
            b.GetProperty("entry").GetUInt32(), b.GetProperty("name").GetString()!,
            b.GetProperty("immuneSchools").GetUInt32(),
            b.GetProperty("spellIds").EnumerateArray().Select(s => s.GetUInt32()).ToArray(),
            b.GetProperty("features").EnumerateArray().Select(s => s.GetString()!).ToArray()))
            .ToDictionary(b => b.Entry);
    });
    public static CommanderBossFact? Find(uint entry) => Facts.Value.GetValueOrDefault(entry);
    public static int Count => Facts.Value.Count;
}
