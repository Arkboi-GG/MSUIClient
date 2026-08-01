using System.Globalization;

namespace MSUIClient.Engine;

public readonly record struct CastBarVerdict(
    double Time, string Character, uint SpellId, string SpellName, string Event,
    string Classification, uint ServerDurationMs, int DbcCastTimeMs, int DbcDurationMs,
    string Phase, double StartedAt, double EndsAt, uint PushbackTotalMs,
    string CancelSource, string AnimationState) : IVerdict
{
    public string Channel => "cast-bar";
    public string ToLine() => string.Format(CultureInfo.InvariantCulture,
        "time={0:F3} character={1} spell={2} name={3} event={4} classification={5} " +
        "serverDurationMs={6} dbcCastTimeMs={7} dbcDurationMs={8} phase={9} " +
        "started={10:F3} ends={11:F3} pushbackMs={12} cancel={13} animation={14}",
        Time, Token(Character), SpellId, Token(SpellName), Event, Classification,
        ServerDurationMs, DbcCastTimeMs, DbcDurationMs, Phase, StartedAt, EndsAt,
        PushbackTotalMs, CancelSource, Token(AnimationState));
    private static string Token(string value) => value.Replace(' ', '_');
}
