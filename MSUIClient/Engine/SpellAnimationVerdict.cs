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
    bool Moving,
    bool LegalWhileMoving,
    bool MovementInterrupts,
    string BaseAnimation,
    string PreviousBaseAnimation,
    string ActionAnimation,
    string HoldAnimation,
    float BlendWeight,
    string Source) : IVerdict
{
    public string Channel => "spell-animation";

    public string ToLine() => string.Format(CultureInfo.InvariantCulture,
        "time={0:F3} character={1} spell={2} name={3} school={4} stage={5} kit={6} authored={7} " +
        "requested={8} played={9} resolution={10} renderer={11} moving={12} legalMoving={13} " +
        "interrupts={14} base={15} previousBase={16} action={17} hold={18} blend={19:F4} source={20}",
        Time, Token(Character), SpellId, Token(SpellName), School, Stage, KitId,
        AuthoredAnimationId, RequestedAnimationId, PlayedAnimationId, Resolution,
        Token(RendererState), Moving, LegalWhileMoving, MovementInterrupts,
        Token(BaseAnimation), Token(PreviousBaseAnimation), Token(ActionAnimation),
        Token(HoldAnimation), BlendWeight, Source);

    private static string Token(string value) => string.IsNullOrWhiteSpace(value)
        ? "none" : value.Replace(' ', '_');
}
