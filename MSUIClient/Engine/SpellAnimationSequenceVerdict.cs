using System.Globalization;

namespace MSUIClient.Engine;

public readonly record struct SpellAnimationSequenceVerdict(
    double Time,
    string Character,
    uint SpellId,
    string Cell,
    string RowKind,
    string Coverage,
    int SampleIndex,
    string Frame,
    string ActualStage,
    int ExpectedAnimationId,
    int RequestedAnimationId,
    int PlayedAnimationId,
    AnimChoiceKind Resolution,
    string RendererState,
    string BaseAnimation,
    string PreviousBaseAnimation,
    string ActionAnimation,
    string HoldAnimation,
    float BlendWeight,
    bool Moving,
    string ActiveModels,
    string AssetSources,
    string AnimationVerdict,
    string BlendVerdict,
    string GmMode,
    string Source) : IVerdict
{
    public string Channel => "spell-animation-sequence";

    public string ToLine() => string.Format(CultureInfo.InvariantCulture,
        "time={0:F3} character={1} spell={2} cell={3} row={4} coverage={5} sample={6} frame={7} " +
        "stage={8} expected={9} requested={10} played={11} resolution={12} renderer={13} base={14} " +
        "previousBase={15} action={16} hold={17} blend={18:F4} moving={19} activeModels={20} " +
        "assetSources={21} animationVerdict={22} blendVerdict={23} gmMode={24} source={25}",
        Time, Token(Character), SpellId, Cell, RowKind, Coverage, SampleIndex, Token(Frame), ActualStage,
        ExpectedAnimationId, RequestedAnimationId, PlayedAnimationId, Resolution, Token(RendererState),
        Token(BaseAnimation), Token(PreviousBaseAnimation), Token(ActionAnimation), Token(HoldAnimation),
        BlendWeight, Moving, Token(ActiveModels), Token(AssetSources), AnimationVerdict, BlendVerdict,
        GmMode, Source);

    private static string Token(string value) => string.IsNullOrWhiteSpace(value)
        ? "none" : value.Replace(' ', '_');
}
