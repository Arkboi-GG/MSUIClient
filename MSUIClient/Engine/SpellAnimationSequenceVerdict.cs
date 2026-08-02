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
    uint PlayerHealth,
    uint PlayerPower,
    uint SelectionHealth,
    float PlayerX,
    float PlayerY,
    float PlayerZ,
    int UnitCount,
    string PlayerAuras,
    string SelectionAuras,
    string InventoryFingerprint,
    bool HealthChanged,
    bool TargetHealthChanged,
    bool PositionChanged,
    bool UnitCountChanged,
    bool AuraChanged,
    bool InventoryChanged,
    bool PowerChanged,
    string PrecastVisual,
    string CastVisual,
    string MissileVisual,
    string ImpactVisual,
    string VisualInstances,
    string SpellVisualVerdict,
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
        "previousBase={15} action={16} hold={17} blend={18:F4} moving={19} playerHealth={20} playerPower={21} " +
        "selectionHealth={22} position={23:F3}|{24:F3}|{25:F3} unitCount={26} playerAuras={27} selectionAuras={28} " +
        "inventory={29} healthChanged={30} targetHealthChanged={31} positionChanged={32} unitCountChanged={33} " +
        "auraChanged={34} inventoryChanged={35} powerChanged={36} precastVisual={37} castVisual={38} missileVisual={39} " +
        "impactVisual={40} visualInstances={41} spellVisualVerdict={42} activeModels={43} assetSources={44} " +
        "casterAnimationVerdict={45} blendVerdict={46} gmMode={47} source={48}",
        Time, Token(Character), SpellId, Cell, RowKind, Coverage, SampleIndex, Token(Frame), ActualStage,
        ExpectedAnimationId, RequestedAnimationId, PlayedAnimationId, Resolution, Token(RendererState),
        Token(BaseAnimation), Token(PreviousBaseAnimation), Token(ActionAnimation), Token(HoldAnimation),
        BlendWeight, Moving, PlayerHealth, PlayerPower, SelectionHealth, PlayerX, PlayerY, PlayerZ, UnitCount,
        Token(PlayerAuras), Token(SelectionAuras), Token(InventoryFingerprint), HealthChanged, TargetHealthChanged,
        PositionChanged, UnitCountChanged, AuraChanged, InventoryChanged, PowerChanged, PrecastVisual, CastVisual, MissileVisual,
        ImpactVisual, Token(VisualInstances), SpellVisualVerdict, Token(ActiveModels), Token(AssetSources),
        AnimationVerdict, BlendVerdict, GmMode, Source);

    private static string Token(string value) => string.IsNullOrWhiteSpace(value)
        ? "none" : value.Replace(' ', '_');
}
