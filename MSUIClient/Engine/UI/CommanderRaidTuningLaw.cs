using System.Text.Json;
namespace MSUIClient.Engine.UI;
public static class CommanderRaidTuningLaw
{
    private sealed record Field(string Type, double Default, double Min, double Max);
    private static readonly Lazy<IReadOnlyDictionary<string, Field>> Fields = new(() =>
    {
        using var stream = typeof(CommanderRaidTuningLaw).Assembly.GetManifestResourceStream("MSUIClient.Encounters.tuning-fields.json")
            ?? throw new InvalidDataException("Missing encounter tuning schema.");
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.GetProperty("fields").EnumerateArray().ToDictionary(
            f => f.GetProperty("name").GetString()!,
            f => new Field(f.GetProperty("type").GetString()!, f.GetProperty("default").GetDouble(),
                f.GetProperty("min").GetDouble(), f.GetProperty("max").GetDouble()), StringComparer.Ordinal);
    });
    public static bool Valid(IReadOnlyDictionary<string, double>? overrides) => overrides is not null &&
        overrides.All(p => Fields.Value.TryGetValue(p.Key, out var f) && double.IsFinite(p.Value) &&
            p.Value >= f.Min && p.Value <= f.Max && (f.Type == "float" || p.Value == Math.Truncate(p.Value)));
    public static IReadOnlyDictionary<string, double> Effective(IReadOnlyDictionary<string, double> overrides)
    {
        if (!Valid(overrides)) throw new InvalidDataException("Invalid encounter tuning override.");
        return Fields.Value.ToDictionary(p => p.Key, p =>
        {
            double value = overrides.TryGetValue(p.Key, out var authored) ? authored : p.Value.Default;
            return p.Value.Type == "float" ? (double)(float)value : value;
        }, StringComparer.Ordinal);
    }
}
