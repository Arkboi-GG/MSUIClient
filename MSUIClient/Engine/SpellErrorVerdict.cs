using System.Globalization;

namespace MSUIClient.Engine;

public readonly record struct SpellErrorVerdict(
    double Time, string Character, uint SpellId, string SpellName,
    string Reason, string DisplayText, bool Displayed, string Source) : IVerdict
{
    public string Channel => "spell-error";
    public string ToLine() => string.Format(CultureInfo.InvariantCulture,
        "time={0:F3} character={1} spell={2} name={3} reason={4} text={5} displayed={6} source={7}",
        Time, Token(Character), SpellId, Token(SpellName), Reason, Token(DisplayText), Displayed, Source);
    private static string Token(string value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Replace(' ', '_');
}
