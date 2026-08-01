using System.Globalization;

namespace MSUIClient.Engine;

public readonly record struct SpellAnimationVerdict(
    double Time,
    string Character,
    uint SpellId,
    string SpellName,
    string School,
    string Stage,
    uint KitId,
    int AuthoredAnimationId,
    int RequestedAnimationId,
    int PlayedAnimationId,
    AnimChoiceKind Resolution,
    string RendererState,
    string Source) : IVerdict
{
    public string Channel => "spell-animation";

    public string ToLine() => string.Format(CultureInfo.InvariantCulture,
        "time={0:F3} character={1} spell={2} name={3} school={4} stage={5} kit={6} authored={7} " +
        "requested={8} played={9} resolution={10} renderer={11} source={12}",
        Time, Token(Character), SpellId, Token(SpellName), School, Stage, KitId,
        AuthoredAnimationId, RequestedAnimationId, PlayedAnimationId, Resolution,
        Token(RendererState), Source);

    private static string Token(string value) => string.IsNullOrWhiteSpace(value)
        ? "none" : value.Replace(' ', '_');
}
