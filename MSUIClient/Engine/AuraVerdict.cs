using System.Globalization;

namespace MSUIClient.Engine;

public readonly record struct AuraVerdict(
    double Time, string Character, ulong UnitGuid, byte Slot, uint SpellId,
    string SpellName, string Event, bool Helpful, bool Cancelable, byte Stacks,
    uint DurationMs, uint RemainingMs, string Display, string Source) : IVerdict
{
    public string Channel => "spell-aura";
    public string ToLine() => string.Format(CultureInfo.InvariantCulture,
        "time={0:F3} character={1} unit=0x{2:X16} slot={3} spell={4} name={5} event={6} helpful={7} " +
        "cancelable={8} stacks={9} durationMs={10} remainingMs={11} display={12} source={13}",
        Time, Token(Character), UnitGuid, Slot, SpellId, Token(SpellName), Event, Helpful,
        Cancelable, Stacks, DurationMs, RemainingMs, Display, Source);
    private static string Token(string value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Replace(' ', '_');
}
