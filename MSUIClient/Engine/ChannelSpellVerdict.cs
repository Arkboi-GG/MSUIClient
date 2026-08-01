using System.Globalization;

namespace MSUIClient.Engine;

public readonly record struct ChannelSpellVerdict(
    double Time,
    string Character,
    uint SpellId,
    string SpellName,
    string Event,
    uint DurationMs,
    uint RemainingMs,
    int TickIndex,
    double TickDeltaMs,
    string TickKind,
    uint Amount,
    bool Moving,
    string AnimationState,
    string Source) : IVerdict
{
    public string Channel => "spell-channel";
    public string ToLine() => string.Format(CultureInfo.InvariantCulture,
        "time={0:F3} character={1} spell={2} name={3} event={4} durationMs={5} remainingMs={6} " +
        "tick={7} tickDeltaMs={8:F3} tickKind={9} amount={10} moving={11} animation={12} source={13}",
        Time, Token(Character), SpellId, Token(SpellName), Event, DurationMs, RemainingMs,
        TickIndex, TickDeltaMs, TickKind, Amount, Moving, Token(AnimationState), Source);
    private static string Token(string value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Replace(' ', '_');
}
