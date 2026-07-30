using MSUIClient.Net;
using MSUIClient.Engine;
using MSUIClient.Formats;
using MSUIClient.World.Units;

static byte[] Hex(string value) => Convert.FromHexString(value.Replace(" ", ""));
static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidDataException(message);
}
static SpellInfo TargetSpell(uint targets, uint implicitTarget) => new(
    Id: 1, Name: "", Rank: "", IconPath: "", Attributes: 0, AttributesEx2: 0,
    AttributesEx3: 0, InterruptFlags: 0, ChannelInterruptFlags: 0,
    Targets: targets, ImplicitTarget: implicitTarget, RecoveryMs: 0, CategoryRecoveryMs: 0,
    PowerType: 0, ManaCost: 0, ManaCostPercent: 0, StartRecoveryCategory: 0,
    StartRecoveryMs: 0, VisualId: 0, Speed: 0, Description: "", RangeIndex: 0);

var start = (CombatAttackStarted)CombatPacketParser.Parse(
    Op.SMSG_ATTACKSTART,
    Hex("01000000000000002a000045000030f1"));
Check(start.Attacker == 1 && start.Victim == 0xF130_0000_4500_002Aul, "attack-start GUID layout");

var spell = (CombatSpellDamage)CombatPacketParser.Parse(
    Op.SMSG_SPELLNONMELEEDAMAGELOG,
    Hex("c92a4530f1 0101 85000000 f4010000 03 32000000 ecffffff 00 00 0a000000 02000000 00"));
Check(spell.Attacker == 1 && spell.SpellId == 133 && spell.Damage == 500, "spell damage fields");
Check(spell.Absorb == 50 && spell.Resist == -20 && (spell.HitInfo & 2) != 0, "spell mitigation/crit");
var spellCue = CombatFeedbackLaw.WorldText(spell, 1);
Check(spellCue.Count == 1 && spellCue[0].Text == "500" && spellCue[0].Critical &&
      spellCue[0].Style == WorldCombatTextStyle.PlayerSpell, "owned spell world-text law");
Check(CombatFeedbackLaw.WorldText(spell, 99).Count == 0, "foreign damage world-text suppression");

var blockedSwing = new CombatMeleeSwing(1, 2, 0, 0, 5, 0, 0, 12);
var blockCue = CombatFeedbackLaw.WorldText(blockedSwing, 1);
Check(blockCue.Count == 1 && blockCue[0].Text == "Block", "victim-state word precedence");
var incoming = CombatFeedbackLaw.CenterText(new CombatMeleeSwing(2, 1, 0, 17, 1, 0, 0, 0), 1);
Check(incoming.Count == 1 && incoming[0].Text == "-17" &&
      incoming[0].Style == CenterCombatTextStyle.Damage, "incoming center damage law");
var selfHeal = CombatFeedbackLaw.CenterText(new CombatHeal(1, 1, 42, 25, true), 1);
Check(selfHeal.Count == 1 && selfHeal[0].Text == "+25" && selfHeal[0].Critical,
      "self-heal center text law");

var periodic = (CombatPeriodicAura)CombatPacketParser.Parse(
    Op.SMSG_PERIODICAURALOG,
    Hex("c92a4530f1 0101 ac000000 01000000 03000000 58000000 06000000 0c000000 fbffffff"));
Check(periodic.Ticks.Count == 1 && periodic.Ticks[0].Kind == CombatPeriodicKind.Damage,
      "periodic tick kind");
Check(periodic.Ticks[0].Amount == 88 && periodic.Ticks[0].Resist == -5, "periodic tick payload");

var movement = MovementInfo.Create(
    new System.Numerics.Vector3(1, 2, 3), 0.75f,
    MovementFlags.Forward | MovementFlags.Falling);
movement.FallTime = 321;
movement.Jump = new JumpInfo(-7.955547f, 1, 0, 7);
var writer = new PacketWriter();
movement.Write(writer);
var decoded = MovementInfo.Read(new PacketReader(writer.ToArray()));
Check(decoded.Flags == movement.Flags && decoded.FallTime == 321, "movement flags/fall time round-trip");
Check(decoded.Jump is { ZSpeed: < 0, XySpeed: 7 }, "movement jump tail round-trip");

var hostile = new FactionTemplateRow { Faction = 1, EnemyGroupMask = 4 };
var monster = new FactionTemplateRow { Faction = 2, GroupMask = 4 };
Check(hostile.ReactionToward(monster) == FactionReaction.Hostile, "faction enemy-group precedence");
var friendly = new FactionTemplateRow { Faction = 3, FriendGroupMask = 8 };
var ally = new FactionTemplateRow { Faction = 4, GroupMask = 8 };
Check(friendly.ReactionToward(ally) == FactionReaction.Friendly, "faction friend-group comparison");

var playerTarget = new CastTargetCandidate(1, IsSelf: true, Friendly: true, Attackable: false, Dead: false);
var wolfTarget = new CastTargetCandidate(2, IsSelf: false, Friendly: false, Attackable: true, Dead: false);
var selfSpellTarget = CastTargetLaw.Resolve(TargetSpell(0, 1), wolfTarget, playerTarget);
Check(selfSpellTarget.Kind == CastTargetKind.SelfImplicit && selfSpellTarget.Guid == 0,
      "implicit-self spell ignores hostile selection");
var holyLightTarget = CastTargetLaw.Resolve(TargetSpell(0, 21), wolfTarget, playerTarget);
Check(holyLightTarget.Kind == CastTargetKind.Unit && holyLightTarget.Guid == 1,
      "friendly spell on hostile selection auto-targets player");
var fireballTarget = CastTargetLaw.Resolve(TargetSpell(0, 6), wolfTarget, playerTarget);
Check(fireballTarget.Kind == CastTargetKind.Unit && fireballTarget.Guid == 2,
      "hostile spell binds attackable selection");

var camera = new Camera
{
    Target = new System.Numerics.Vector3(10, 20, 30),
    Yaw = 0.7f,
    Pitch = 0.25f,
    AspectRatio = 16f / 9f,
};
var centerRay = camera.ScreenPointToRay(new System.Numerics.Vector2(960, 540),
                                        new System.Numerics.Vector2(1920, 1080));
Check(centerRay is { } ray && System.Numerics.Vector3.Dot(ray.Direction, camera.Forward) > 0.999f,
      "screen-center ray follows camera forward");
Check(MathF.Abs(CreatureRenderer.UnitRenderScale(0.65f) - 0.65f) < 0.0001f,
      "unit render scale does not square the server-folded DBC scale");
float faced = EntityStore.TurnToward(6.20f, 0.10f, 0.08f);
Check(faced < 0.01f || faced > 6.20f, "idle facing takes the short wrapped turn");
Check(MathF.Abs(EntityStore.TurnToward(0f, MathF.PI, 0.5f) - 0.5f) < 0.0001f,
      "idle facing respects the turn-rate cap");

Console.WriteLine("combat/movement/targeting foundation checks passed");
