using System.Globalization;

namespace MSUIClient.Engine;

public readonly record struct SpellSweepVerdict(
    double Time,
    string Character,
    byte ClassId,
    uint SpellId,
    string SpellName,
    string School,
    string CastType,
    string Result,
    string AnimationState,
    string EffectCheck,
    string TargetType,
    bool GcdReady,
    bool CooldownReady,
    string ResourceType,
    uint ResourceBefore,
    uint ResourceCost,
    ulong ResolvedGuid,
    bool Sent) : IVerdict
{
    public string Channel => "spell-sweep";

    public string ToLine() => string.Format(CultureInfo.InvariantCulture,
        "time={0:F3} character={1} class={2} spell={3} name={4} school={5} castType={6} " +
        "result={7} animation={8} effect={9} targetType={10} gcdReady={11} cooldownReady={12} " +
        "resourceType={13} resourceBefore={14} resourceCost={15} resolved=0x{16:X16} sent={17}",
        Time, Token(Character), ClassId, SpellId, Token(SpellName), School, CastType,
        Result, Token(AnimationState), Token(EffectCheck), TargetType, GcdReady, CooldownReady,
        ResourceType, ResourceBefore, ResourceCost, ResolvedGuid, Sent);

    private static string Token(string value) => value.Replace(' ', '_');
}
