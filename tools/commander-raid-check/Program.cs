using MSUIClient.Engine.UI;
using System.Numerics;
using MSUIClient.Net;
using MSUIClient.World.Encounters;

int passed = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception(name); passed++; Console.WriteLine("PASS " + name); }
var roster = Enumerable.Range(1, 39).Select(i => new CommanderRaidMember((ulong)i, "Raider" + i,
    i <= 3 ? 1u : i <= 13 ? 5u : 8u, true, true,
    new HashSet<uint>(i <= 13 && i > 3 ? [100u] : [200u]), i <= 13 && i > 3 ? 100u : 0u, 200u)).ToArray();
roster = new[] { new CommanderRaidMember(1000, "Main", 4, false, true, new HashSet<uint> { 200 }) }.Concat(roster).ToArray();
using var fixtureStream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("CommanderTests.LegacyLayout.json")!;
using var fixtureReader = new StreamReader(fixtureStream);
var legacyFixture = CommanderEncounterDefinition.Parse(fixtureReader.ReadToEnd());
var compiledDefinition = CommanderEncounterCatalog.Default;
var compiledPlan = CommanderRaidPlanLaw.AutoAssign(roster, compiledDefinition, 1000, CommanderRaidRole.Melee);
Check(CommanderEncounterLaw.Validate(compiledDefinition).Count == 0 &&
    CommanderRaidPlanLaw.Validate(compiledPlan, roster).Count == 0,
    "current compiled definition parses and assigns the full raid");
var compiledMove = compiledDefinition.Rules.Single(r => r.Action == "move" && r.Trigger == "always");
Check(compiledPlan.Assignments.Single(a => a.Role == CommanderRaidRole.MainTank).Air ==
    CommanderEncounterLaw.Point(compiledMove.Station), "current compiled movement data supplies the tank station");
var movedDefinition = compiledDefinition with { Rules = compiledDefinition.Rules.Select(r =>
    r.Id == compiledMove.Id ? r with { Station = new float[] { -10, -210, -88 } } : r).ToArray() };
var movedPlan = CommanderRaidPlanLaw.AutoAssign(roster, movedDefinition, 1000, CommanderRaidRole.Melee);
Check(movedPlan.Assignments.Single(a => a.Role == CommanderRaidRole.MainTank).Air == new Vector3(-10,-210,-88),
    "changing movement data changes the tank station without planner code");
CommanderRaidPlan Assign(IReadOnlyList<CommanderRaidMember> members, CommanderEncounterDefinition? fight = null,
    CommanderRaidRole role = CommanderRaidRole.Melee) => CommanderRaidPlanLaw.AutoAssign(members, fight ?? legacyFixture, 1000, role);
var plan = Assign(roster);
Check(plan.Assignments.Count == 40 && plan.Assignments.Count(a => !a.Manual) == 39, "39 bots and the manually controlled human are assigned");
Check(CommanderRaidPlanLaw.Validate(plan, roster).Count == 0, "full roster covers tank, healers and both whelp wings");
Check(plan.Assignments.Count(a => a.Role == CommanderRaidRole.MainTank) == 1, "one main tank");
Check(plan.Assignments.Where(a => a.Role == CommanderRaidRole.Healer).All(a =>
    Vector3.Distance(a.Ground, plan.Assignments.Single(p => p.Guid == a.HealPrimary).Ground) <= 40),
    "authored healer stations remain within ordinary range of their assigned tank");
Check(plan.Assignments.Where(a => a.Role == CommanderRaidRole.AddTank).Select(a => a.Team).Distinct().Count() == 2, "whelp tank in each wing");
// Ground support must stay clear of the measured 120-degree front/rear cones
// while the boss settles within the reachable north-wall tank geometry.
Check(new[] { 5f, 10f }.All(bossX => plan.Assignments.Where(a => a.Role is CommanderRaidRole.Healer or CommanderRaidRole.AddTank).All(a =>
{
    var delta = a.Ground - new Vector3(bossX, -215, -87);
    float angle = MathF.Abs(MathF.Atan2(delta.Y, delta.X)) * 180 / MathF.PI;
    return angle > 65 && angle < 115;
})), "ground support keeps five-degree margins from both measured Onyxia cones");
Check(plan.Assignments.All(a => CommanderRaidPlanLaw.InsideRoom(a.Ground) && CommanderRaidPlanLaw.InsideRoom(a.Air)), "all preset stations inside room bounds");
Check(plan.Assignments.Select(a => a.Air).Distinct().Count() == 40, "unique air stations across roles");
Check(plan.Assignments.Single(a => a.Role == CommanderRaidRole.MainTank).Air == new Vector3(-5,-213,-88), "authored main-tank flight station intercepts landing inside the room");
var gridOnly = CommanderRaidPlanLaw.Layout(plan with { Encounter = plan.Encounter with { Rules = plan.Encounter.Rules.Where(r => r.Action != "move").ToArray() } });
Check(plan.Assignments.Where(a => a.Role != CommanderRaidRole.MainTank).All(a => a.Air == gridOnly.Assignments.Single(b => b.Guid == a.Guid).Air), "a tank movement rule preserves every other role air station");
Check(Assign(roster.Reverse().ToArray()).Assignments.SequenceEqual(plan.Assignments), "assignment stable across roster arrival ordering");
Check(Assign(roster.Append(new(100, "Human", 1, false, true, new HashSet<uint>())).ToArray()).Assignments.Count == 40, "uncommandable humans excluded");
var noHeals = roster.Select(r => r with { PreferredHeal = 0 }).ToArray();
Check(CommanderRaidPlanLaw.Validate(Assign(noHeals), noHeals).Any(x => x.Contains("healer")), "missing healer coverage reported");
Check(CommanderRaidPlanLaw.Validate(plan, roster.Select(r => r.Guid == 2 ? r with { Alive = false } : r).ToArray()).Any(x => x.Contains("dead")), "dead assigned actor blocks apply");
Check(CommanderRaidPlanLaw.Validate(plan with { Assignments = plan.Assignments.Append(plan.Assignments[0]).ToArray() }, roster).Any(x => x.Contains("twice")), "duplicate actor rejected");
Check(!CommanderRaidPlanLaw.InsideRoom(new(float.NaN, -215, -88)) && !CommanderRaidPlanLaw.InsideRoom(new(0, float.PositiveInfinity, -88)), "nonfinite coordinates rejected");
Check(CommanderRaidPlanLaw.Validate(plan with { Version = 99 }, roster).Count > 0, "future plan version rejected");
var packet = CommanderRaidWire.Build(37, 9, CommanderRaidOperation.Apply, plan);
Check(packet.Length == 24 + 71 * 40 + CommanderEncounterWire.Schema1Utf8(plan.Encounter).Length, "full raid request wire length");
string wireDefinition = CommanderEncounterWire.Schema1Json(plan.Encounter);
Check(CommanderRaidWire.Version == 4 && !wireDefinition.Contains("mechanics") && !wireDefinition.Contains("tuning") && !wireDefinition.Contains("elapsedMinMs"),
    "apply carries the deployed Core's version and a schema-1 definition with no schema-2 words");
Check(CommanderEncounterDefinition.Parse(wireDefinition).Rules.Length == plan.Encounter.Rules.Length, "schema-1 projection loads back with every rule");
var reader = new PacketReader(packet);
Check(reader.ReadU8() == CommanderRaidWire.Version && reader.ReadU32() == 37 && reader.ReadU32() == 9 && reader.ReadU8() == 1 && reader.ReadU32() == 10184, "wire version, request, revision, operation, encounter");
reader.ReadU8(); Check(reader.ReadU8() == 40, "wire roster count");
Check(reader.ReadU32() == 249, "wire map identity");
uint definitionLength = reader.ReadU32();
foreach (var assignment in plan.Assignments)
{
    if (reader.ReadU64() != assignment.Guid || reader.ReadU8() != (byte)assignment.Role || reader.ReadU8() != (byte)assignment.Team || reader.ReadU8() != (assignment.Manual ? 1 : 0) ||
        reader.ReadVector3() != assignment.Ground || reader.ReadVector3() != assignment.Air || reader.ReadU32() != assignment.HealSpell || reader.ReadU32() != assignment.DamageSpell || reader.ReadU64() != assignment.HealPrimary || reader.ReadU32() != assignment.FocusEntry ||
        reader.ReadU32() != assignment.InterruptSpell || reader.ReadU32() != assignment.DispelSpell || reader.ReadU32() != assignment.TauntSpell || reader.ReadU32() != assignment.DefensiveSpell)
        throw new Exception("assignment wire mismatch");
}
Check(reader.Remaining == definitionLength, "all assignments round-trip in fixed-width records");
foreach (var op in new[] { CommanderRaidOperation.Inspect, CommanderRaidOperation.Arm, CommanderRaidOperation.Pause, CommanderRaidOperation.Clear })
    Check(CommanderRaidWire.Build(1, 0, op, plan).Length == 24, op + " never retransmits actors");
var response = new PacketWriter();
response.WriteU8(CommanderRaidWire.Version); response.WriteU32(37); response.WriteU32(10); response.WriteU8(0); response.WriteU8(2); response.WriteU8(2); response.WriteU64(999); response.WriteU8(1);
response.WriteU32(500); response.WriteU32(1000); response.WriteU8(7);
response.WriteU64(1); response.WriteU8(6); response.WriteU64(999); response.WriteU8(7);
response.WriteU8(2); response.WriteU16(0); response.WriteU32(4000); response.WriteF32(0); response.WriteF32(-200); response.WriteF32(-85);
byte[] bytes = response.ToArray();
Check(CommanderRaidWire.TryParse(bytes, out var state) && state?.Actors[0].Duty == 6 && state.Phase == 2, "authoritative status parses");
for (int i = 0; i < bytes.Length; i++) if (CommanderRaidWire.TryParse(bytes[..i], out _)) throw new Exception("truncated snapshot accepted");
Check(true, "every truncated snapshot refused");
Check(!CommanderRaidWire.TryParse(bytes.Append((byte)0).ToArray(), out _), "trailing bytes refused");
var corrupt = (byte[])bytes.Clone(); corrupt[0] = 99;
Check(!CommanderRaidWire.TryParse(corrupt, out _), "unknown wire version refused");
corrupt = (byte[])bytes.Clone(); corrupt[20] = 41;
Check(!CommanderRaidWire.TryParse(corrupt, out _), "oversized status roster refused");
Check(state?.BossKnown == true && state.BossAlive && state.BossInCombat && state.BossHealth == 500 && state.BossMaxHealth == 1000, "boss observations remain authoritative outside client visibility");
Check(state?.Actors[0].Known == true && state.Actors[0].Alive && state.Actors[0].Guidance.ImpactMs == 4000, "actor life and impact timing round-trip");
var adviceDefinition = plan.Encounter with { Rules = [plan.Encounter.Rules.First(r => r.Action == "avoidPoints")] };
Check(CommanderRaidGuidanceLaw.Current(state, 1, adviceDefinition, .5)?.State == 2, "fresh bounded guidance is shared by UI and QA");
Check(CommanderRaidGuidanceLaw.Current(state, 1, adviceDefinition, 3) is null, "stale hazard guidance cannot drive movement");
Check(CommanderRaidGuidanceLaw.Current(state, 2, adviceDefinition, .5) is null, "one actor cannot consume another actor's escape route");
Check(CommanderRaidGuidanceLaw.Current(state, 1, adviceDefinition with { Rules = [] }, .5) is null, "unmatched mechanic advice is refused");
corrupt = (byte[])bytes.Clone(); corrupt[47] = 8;
Check(!CommanderRaidWire.TryParse(corrupt, out _), "unknown actor flags are refused");
corrupt = (byte[])bytes.Clone(); corrupt[48] = 5;
Check(!CommanderRaidWire.TryParse(corrupt, out _), "unknown guidance state is refused");
corrupt = (byte[])bytes.Clone(); BitConverter.GetBytes(float.NaN).CopyTo(corrupt, 55);
Check(!CommanderRaidWire.TryParse(corrupt, out _), "nonfinite guidance waypoint is refused");
string path = Path.Combine(Path.GetTempPath(), "commander-plan-" + Guid.NewGuid() + ".json");
try
{
    CommanderRaidPlanStore.Save(path, plan);
    var loaded = CommanderRaidPlanStore.Load(path);
    Check(loaded.Assignments.SequenceEqual(plan.Assignments), "saved coordinates, roles and spell identities round-trip");
    CommanderRaidPlanStore.Save(path, plan with { AvoidBreath = false });
    Check(!CommanderRaidPlanStore.Load(path).AvoidBreath, "atomic save replaces existing plan");
}
finally { if (File.Exists(path)) File.Delete(path); }

var definition = plan.Encounter;
var effectiveTuning = CommanderRaidTuningLaw.Effective(new Dictionary<string,double> { ["tickMs"] = 500 });
Check(effectiveTuning["tickMs"] == 500 && effectiveTuning["bridgeTickMs"] == 1000, "tuning override retains other named defaults");
Check(!CommanderRaidTuningLaw.Valid(new Dictionary<string,double> { ["tickMs"] = 500.5 }), "integer tuning refuses fractional milliseconds");
Check(!CommanderRaidTuningLaw.Valid(new Dictionary<string,double> { ["tickMs"] = -1 }), "tuning refuses out-of-range values");
Check(!CommanderRaidTuningLaw.Valid(new Dictionary<string,double> { ["invented"] = 1 }), "tuning refuses unknown fields");
var surrenderBytes=(byte[])bytes.Clone();surrenderBytes[10]=4;surrenderBytes[29]=27;
Check(CommanderRaidWire.TryParse(surrenderBytes,out var surrendered) && surrendered!.BossAlive && surrendered.BossFriendly && surrendered.BossNonAttackable && !surrendered.BossInCombat,
    "scripted surrender retains live boss health and native friendliness evidence");
var interactionPlan=plan with {Encounter=definition with {Mechanics=new() {Interactions=[new("rune",1,0,5,true)]}}};
Check(CommanderRaidWire.BuildInteraction(1,0,interactionPlan,1234,0,8).Length==95,"reviewed gameobject intent has an exact actor-independent packet shape");
bool refusedInteraction=false;try{CommanderRaidWire.BuildInteraction(1,0,interactionPlan,1234,0,7);}catch(ArgumentOutOfRangeException){refusedInteraction=true;}
Check(refusedInteraction,"progression interaction refuses fewer than eight measured successes");
bool invalidBatch=false;try{CommanderRaidWire.BuildInteraction(1,0,interactionPlan,1234,0,11);}catch(ArgumentOutOfRangeException){invalidBatch=true;}
Check(invalidBatch,"progression interaction refuses a count outside a ten-pull batch");

Check(definition.Rules.Where(r => r.Action == "avoidPoints").All(r => r.Radius >= 31),
    "authored breath footprints cover measured 30-yard effects plus clearance");
Check(CommanderEncounterDefinition.Parse(definition.ToJson()).Rules.Length == 26, "Onyxia loads entirely from definition data");
Check(definition.Rules.Single(r => r.Id == "fireball-spread").Trigger == "castStart", "spread responds to the cast warning before the projectile launches");
Check(definition.Rules.Count(r => r.Action == "avoidCones" && r.Roles == 56 && r.Target == "boss") == 2, "ground support and damage roles have dynamic loaded-cone rules");
Check(definition.Rules.Count(r => r.Trigger == "bossNear") == 8 && definition.Rules.Where(r => r.Trigger == "bossNear").All(r => r.TriggerRadius == 6 && r.Phase == 4), "all eight visible flight points predict their actual breath lanes");
Check(definition.Rules.Single(r => r.Id == "flight-formation") is { Action: "spread", Trigger: "always", Target: "self", Phase: 4, Radius: 7 }, "flight formation spreads before splash and remains phase scoped");
var objectHazard = new CommanderHazardPolicy([],[],5,30000,62,100,false,false) {Objects=[new(321,123)]};
var objectDefinition = definition with {Mechanics=new() {Hazards=[objectHazard]}};
Check(CommanderEncounterLaw.Validate(objectDefinition).Count==0 &&
    CommanderEncounterDefinition.Parse(objectDefinition.ToJson()).Mechanics.Hazards.Single().Objects.Single().CreationSpell==123,
    "summoned-object identity pairs survive client schema round trip");
foreach (var badHazard in new[] {
    objectHazard with {Objects=[new(321,0)]}, objectHazard with {Objects=[new(321,123),new(321,123)]},
    objectHazard with {Entries=[456]}, objectHazard with {FollowSource=true}, objectHazard with {OnDeath=true}
})
    Check(CommanderEncounterLaw.Validate(objectDefinition with {Mechanics=new() {Hazards=[badHazard]}}).Count>0,
        "object source refuses invalid identities or mixed source semantics");
var countedAdds = definition with { RequiredAdds = [new(11262, 2)], AddPolicy = "balance" };
Check(CommanderEncounterDefinition.Parse(countedAdds.ToJson()).RequiredAdds.Single().Count == 2, "counted add requirements round trip with balance policy");
Check(CommanderEncounterLaw.Validate(countedAdds with { RequiredAdds = [new(99999, 2)] }).Count > 0, "counted objectives must be declared adds");
Check(CommanderEncounterLaw.Validate(countedAdds with { RequiredAdds = [new(11262, -1)] }).Count > 0, "negative add requirements are rejected");
Check(CommanderEncounterLaw.Validate(countedAdds with { RequiredAdds = [new(11262, 2), new(11262, 2)] }).Count > 0, "duplicate add requirements are rejected");
Check(CommanderEncounterLaw.Validate(countedAdds with { AddPolicy = "invented" }).Count > 0, "unknown add policy is rejected");
var reservedWard = definition.Rules.Single(r => r.Id == "fear-ward");
Check(reservedWard.ReserveCasters == 2, "essential tank protection retains an authored caster reserve");
Check(CommanderEncounterDefinition.Parse(definition.ToJson()).Rules.Single(r => r.Id == reservedWard.Id).ReserveCasters == 2, "caster reservations survive definition serialization");
Check(CommanderEncounterLaw.Validate(definition with { Rules = [reservedWard with { ReserveCasters = 9 }] }).Count > 0, "support reservation count is bounded");
Check(CommanderEncounterLaw.Validate(definition with { Rules = [reservedWard with { Target = "self" }] }).Count > 0, "self buffs cannot reserve a shared support pool");
Check(CommanderEncounterLaw.Validate(definition with { Rules = [reservedWard with { Action = "move" }] }).Count > 0, "movement cannot reserve support casters");
Check(CommanderEncounterLaw.Validate(definition with { Rules = [reservedWard with { ReserveCasters = 0 }] }).Count == 0, "legacy support without reservations remains valid");
var healerWard = definition.Rules.Single(r => r.Id == "fear-ward-tank-healer");
var priestWard = definition.Rules.Single(r => r.Id == "priest-self-fear-ward");
Check(priestWard is { Action: "cast", Trigger: "always", Target: "self", Spell: 6346, Roles: 8, MissingAura: true } && priestWard.Priority < healerWard.Priority && priestWard.Priority < definition.Rules.Single(r => r.Id == "fear-ward").Priority, "learned priest self wards preserve tank and tank-healer protection priority");
Check(healerWard is { Target: "tankHealer", Action: "cast", Trigger: "always", Spell: 6346, MissingAura: true }, "tank healer protection is authored ordinary support");
Check(CommanderEncounterLaw.Validate(definition with { Rules = [healerWard with { Action = "spread" }] }).Count > 0, "tank healer cannot become an unsupported movement target");
Check(CommanderEncounterLaw.Validate(definition with { Rules = [healerWard with { Trigger = "bossAura", Spells = [18431] }] }).Count > 0, "tank healer rejects unsupported trigger semantics");
var isolation = definition.Rules.Single(r => r.Id == "fireball-spread");
Check(isolation is { Action: "isolate", Radius: 9, DurationMs: 12000, MissingAura: true, Spell: 20019 }, "Fireball isolates its actual target until observed impact with a bounded fallback");
Check(CommanderEncounterLaw.Validate(definition with { Rules = [isolation with { Target = "self" }] }).Count > 0, "isolation refuses an unmarked target");
Check(CommanderEncounterLaw.Validate(definition with { Rules = [isolation with { Trigger = "always" }] }).Count > 0, "isolation refuses an unobserved cast event");
Check(CommanderEncounterLaw.Validate(definition with { Rules = [isolation with { Spell = 0 }] }).Count > 0, "isolation end condition needs the actual aura identity");
Check(CommanderRaidGuidanceLaw.Current(state, 1, adviceDefinition with { Rules = [isolation] }, .5)?.State == 2, "actual marked isolation guidance reaches the ordinary manual controller");
var predictedRule = definition.Rules.First(r => r.Trigger == "bossNear");
Check(definition.Rules.Where(r => r.Trigger == "bossNear").All(r => r.Priority > isolation.Priority && r.Priority < definition.Rules.First(a => a.Action == "avoidPoints" && a.Trigger == "castStart").Priority), "preemptive breath clearance outranks splash isolation but yields to confirmed breath warnings");
Check(definition.Rules.Where(r => r.Action == "avoidPoints" && r.Trigger == "castStart").All(r => r.Priority > isolation.Priority && r.Priority > definition.Rules.Single(a => a.Trigger == "memberAura").Priority), "actual breath warnings retain priority over Fireball and burning-aura movement");
Check(CommanderEncounterLaw.Validate(definition with { Rules = [predictedRule with { TriggerRadius = 0 }] }).Count > 0, "proximity prediction refuses an empty trigger radius");
Check(CommanderEncounterDefinition.Parse(definition.ToJson()).Rules.Single(r => r.Id == predictedRule.Id).TriggerRadius == 6, "prediction geometry survives the actual definition serialization");
var coneRule = definition.Rules.First(r => r.Action == "avoidCones");
var auraSpread = definition.Rules.Single(r => r.Trigger == "memberAura");
Check(auraSpread.Action == "isolate", "a burning carrier remains responsible for leaving nearby allies after the Fireball cast ends");
Check(CommanderEncounterLaw.Validate(definition with { Rules = [auraSpread with { Action = "spread" }] }).Count == 0, "existing member aura spread remains supported");
Check(CommanderEncounterLaw.Validate(definition with { Rules = [auraSpread with { Radius = 0 }] }).Count > 0, "member aura isolation requires real clearance");
Check(auraSpread.Spells.SequenceEqual(new uint[]{20019}) && auraSpread.Target == "marked" && CommanderEncounterLaw.Validate(definition).Count == 0, "member aura uses actual harmful carrier identities in encounter data");
Check(CommanderEncounterLaw.Validate(definition with { Rules = [auraSpread with { Action = "cast" }] }).Count > 0, "member aura trigger cannot silently become an unsupported cast action");
Check(CommanderEncounterLaw.Validate(definition with { Rules = [auraSpread with { Target = "self" }] }).Count > 0, "member aura requires its observed marked carrier");
Check(CommanderRaidGuidanceLaw.Current(state, 1, adviceDefinition with { Rules = [auraSpread] }, .5)?.State == 2, "shared spread guidance reaches the same manual controller");

Check(CommanderRaidAttemptLaw.TankMovementGoal(new(34,0,0), new(-35,0,0), new(34,0,0), 30, false, Vector3.Zero) == new Vector3(-5.75f,0,0), "human QA tank approaches the contact boundary instead of the moving boss center");
Check(CommanderRaidAttemptLaw.TankMovementGoal(new(20,0,0), Vector3.Zero, new(34,0,0), 30, false, Vector3.Zero) == new Vector3(20,0,0), "human QA tank stays in reach until ordinary threat is acquired");
Check(CommanderRaidAttemptLaw.TankMovementGoal(new(20,0,0), Vector3.Zero, new(34,0,0), 30, true, Vector3.Zero) == new Vector3(22,0,0), "aligned tank backs toward the authored station in a bounded step");
Check(CommanderRaidAttemptLaw.TankMovementGoal(new(40,0,0), Vector3.Zero, new(34,0,0), 30, true, Vector3.Zero) == new Vector3(29.25f,0,0), "knocked-back QA tank restores contact without aiming through the boss");
Check(CommanderRaidAttemptLaw.TankMovementGoal(new(39,-215,0), new(36,-215,0), new(34,-215,0), 30, true, new(0,-215,0)) == new Vector3(39,-215,0), "QA tank does not cross a boss that chased beyond the fixed station");
Check(CommanderRaidAttemptLaw.TankMovementGoal(new(0,39,0), new(0,36,0), new(0,34,0), 30, true, Vector3.Zero) == new Vector3(0,39,0), "QA tank side preservation also works on a rotated encounter axis");
Check(CommanderRaidAttemptLaw.TankMovementGoal(new(39,0,0), new(36,0,0), new(36,0,0), 30, true, Vector3.Zero) == new Vector3(39,0,0), "QA tank does not return to the boss centre");

Check(CommanderRaidGuidanceLaw.Current(state, 1, adviceDefinition with { Rules = [definition.Rules.First(r => r.Action == "cast") with { Priority = 1 }, coneRule with { Priority = 100 }] }, .5)?.State == 2, "guidance resolves Core priority even for unsorted definitions");
Check(CommanderEncounterLaw.Validate(definition with { Rules = [coneRule with { Target = "marked" }] }).Count > 0, "cone avoidance cannot silently follow an unsupported marked source");
Check(CommanderEncounterLaw.Validate(definition with { Rules = [coneRule with { Spells = [] }] }).Count > 0, "cone geometry requires actual spell identities");
Check(CommanderEncounterLaw.Validate(definition with { Rules = [coneRule with { Radius = 11 }] }).Count > 0, "cone clearance is bounded independently of its loaded radius");
var reordered = CommanderEncounterDefinition.Parse((definition with { Rules = definition.Rules.Reverse().ToArray() }).ToJson());
Check(reordered.Rules.Select(r => r.Priority).SequenceEqual(reordered.Rules.Select(r => r.Priority).OrderDescending()), "authored rule order normalizes to Core's stable priority order");

var noAuras = new HashSet<uint>();
Check(CommanderEncounterLaw.SelectPhase(definition, 90, false, noAuras).Id == 1, "ground phase from ordinary observations");
Check(CommanderEncounterLaw.SelectPhase(definition, 60, false, noAuras).Id == 2, "takeoff transition from definition threshold");
Check(CommanderEncounterLaw.SelectPhase(definition, 30, true, noAuras).Id == 4, "observed airborne state outranks landing threshold");
Check(CommanderEncounterLaw.SelectPhase(definition, 30, false, noAuras).Id == 3, "landing after airborne observation clears");
var other = definition with
{
    Id = "fixture-other", Name = "Different room, three teams", MapId = 1, BossEntry = 900001, Objectives = [900001],
    Bounds = new([100, 100, 0], [200, 200, 20]), TankAnchor = [190, 150, 5],
    Teams = [new("Left", [175, 175, 5]), new("Right", [175, 125, 5]), new("Reserve", [125, 150, 5])],
    AddTanksPerTeam = 0, HealersPerTeam = 1, HealerCount = 6, AddEntries = [], Rules = [],
    Phases = [new(7, "Normal", 0, 0, 100, -1, 0, false, true, true, .7f),
        new(9, "Shield", 10, 0, 100, -1, 123, true, false, false, 0)]
};
Check(CommanderEncounterLaw.Validate(other).Count == 0, "different encounter, map, bounds and three teams need no executor code");
var otherPlan = Assign(roster, other);
Check(CommanderRaidPlanLaw.Validate(otherPlan, roster).Count == 0, "same planner supports a second encounter with zero add tanks");
Check(otherPlan.Assignments.Select(a => (int)a.Team).Distinct().Count() == 3, "team count is not hardcoded west/east");
Check(otherPlan.Assignments.All(a => CommanderEncounterLaw.Inside(other, a.Ground) && CommanderEncounterLaw.Inside(other, a.Air)), "second encounter positions use its own coordinates");
Check(CommanderEncounterLaw.SelectPhase(other, 85, false, new HashSet<uint> {123}).Id == 9, "aura phase works without a boss hook");
Check(CommanderEncounterLaw.SelectPhase(other, 85, false, noAuras).Id == 7, "arbitrary phase IDs and fallback supported");
var otherWire = CommanderRaidWire.Build(2, 0, CommanderRaidOperation.Apply, otherPlan);
Check(System.Text.Encoding.UTF8.GetString(otherWire.AsSpan(24 + 71 * 40)).Contains("fixture-other"), "actual apply packet transports the second definition");
Check(CommanderRaidPlanLaw.Validate(otherPlan with { BossEntry = 10184 }, roster).Any(e => e.Contains("mismatch")), "plan/definition identity mismatch blocked");
void Bad(CommanderEncounterDefinition d, string name) => Check(CommanderEncounterLaw.Validate(d).Count > 0, name);
Bad(other with { Teams = [] }, "empty team set rejected");
Bad(other with { Teams = Enumerable.Repeat(other.Teams[0], 9).ToArray() }, "more than eight teams rejected");
Bad(other with { Bounds = new([float.NaN, 0, 0], [200,200,20]) }, "nonfinite room rejected");
Bad(other with { Bounds = new([0,0,0], [1000,1000,20]) }, "unbounded navigation search rejected");
Bad(other with { TankAnchor = [0,0,0] }, "tank anchor outside encounter rejected");
Bad(other with { Phases = [other.Phases[0], other.Phases[0]] }, "duplicate phases rejected");
Bad(other with { Phases = [other.Phases[1]] }, "missing fallback phase rejected");
Bad(other with { Phases = [other.Phases[0] with { ThreatRatio = 2 }] }, "invalid threat ratio rejected");
var rule = definition.Rules.Single(r => r.Id == "fear-ward");
Bad(definition with { Rules = [rule with { Action = "runScript" }] }, "unimplemented actions fail closed");
Bad(definition with { Rules = [rule with { Trigger = "mysteryEvent" }] }, "unimplemented triggers fail closed");
Bad(definition with { Rules = [rule with { Phase = 99 }] }, "unknown phase references rejected");
Bad(definition with { Rules = [rule with { Roles = 128 }] }, "unknown role bits rejected");
Bad(definition with { Rules = [rule with { Target = "marked" }] }, "marked target requires an actual event");
Bad(definition with { Rules = [rule with { Action = "avoidPoints", PointSpells = [] }] }, "avoidance requires authored hazard points");
Bad(definition with { Rules = [rule with { Spell = 0 }] }, "cast action requires a spell");
Bad(definition with { Rules = [rule with { DurationMs = 60001 }] }, "unbounded event duration rejected");
Bad(definition with { Rules = [rule, rule] }, "duplicate mechanic IDs rejected");
bool rejected = false;
try { CommanderEncounterDefinition.Parse(other.ToJson().Replace("\"schema\":1", "\"schema\":1,\"executeCode\":\"ignored?\"")); }
catch (System.Text.Json.JsonException) { rejected = true; }
Check(rejected, "unknown JSON fields cannot silently disable intended mechanics");
string directory = Path.Combine(Path.GetTempPath(), "encounter-library-" + Guid.NewGuid());
Directory.CreateDirectory(directory);
try
{
    File.WriteAllText(Path.Combine(directory, "different.json"), other.ToJson());
    Check(CommanderEncounterCatalog.Load(directory).Any(d => d.Id == other.Id), "new definition loads without a client or Core code edit");
    File.WriteAllText(Path.Combine(directory, "different.json"), "{broken");
    rejected = false;
    try { CommanderEncounterCatalog.Load(directory); } catch (System.Text.Json.JsonException) { rejected = true; }
    Check(rejected, "malformed library reload reports failure");
}
finally { File.Delete(Path.Combine(directory, "different.json")); Directory.Delete(directory); }

Check(CommanderRaidPlanLaw.AutoAssign(roster).Assignments.Count == 0, "Auto-assign requires an explicit human role");
foreach (var role in Enum.GetValues<CommanderRaidRole>().Where(r => r != CommanderRaidRole.Unassigned))
{
    var assigned = Assign(roster, role: role);
    Check(assigned.Assignments.Count == 40 && assigned.Assignments.Single(a => a.Manual).Guid == 1000 &&
        assigned.Assignments.Single(a => a.Manual).Role == role, "manual human remains explicit for " + role);
    Check(CommanderRaidPlanLaw.Validate(assigned, roster).Count == 0, "coverage fills around human " + role);
}
var humanTank = Assign(roster, role: CommanderRaidRole.MainTank);
Check(humanTank.Assignments.Count(a => a.Role == CommanderRaidRole.MainTank && !a.Manual) == 0, "human tank prevents assigning a competing bot main tank");
Check(humanTank.Assignments.Any(a => !a.Manual && a.Role == CommanderRaidRole.Healer && a.HealPrimary == 1000), "bot healers explicitly heal the human tank");
Check(humanTank.Assignments.Where(a => a.Role == CommanderRaidRole.AddTank).All(t => humanTank.Assignments.Any(h => h.HealPrimary == t.Guid)), "both add tanks receive primary healers");
Check(CommanderRaidPlanLaw.Validate(plan with { Assignments = plan.Assignments.Select(a => a.Manual ? a with { Manual = false } : a).ToArray() }, roster).Any(e => e.Contains("manual")), "cannot accidentally automate the human");
Check(CommanderRaidPlanLaw.Validate(plan, roster.Select(m => m.Guid == 4 ? m with { FactsReady = false } : m).ToArray()).Any(e => e.Contains("facts")), "unknown live abilities prevent readiness");
var twoBosses = plan.Encounter with { Objectives = [10184, 900002], AddTanksPerTeam = 0 };
var twoPlan = Assign(roster, twoBosses);
Check(twoPlan.Assignments.Count(a => a.Role == CommanderRaidRole.MainTank) == 2 && CommanderRaidPlanLaw.Validate(twoPlan, roster).Count == 0, "multiple boss objectives receive distinct tanks without changing executor code");
Check(twoPlan.Assignments.Where(a => a.Role == CommanderRaidRole.MainTank).Select(a => a.FocusEntry).Distinct().Count() == 2, "tank target objectives do not collide");
var unknownPatient = plan with { Assignments = plan.Assignments.Select(a => a.Role == CommanderRaidRole.Healer ? a with { HealPrimary = 99999 } : a).ToArray() };
Check(CommanderRaidPlanLaw.Validate(unknownPatient, roster).Any(e => e.Contains("patient")), "healing patient must be in the plan");
CommanderRaidSpellFact Spell(uint id, string name, uint level, uint school, uint[] effects, uint[]? auras = null, uint[]? targets = null, int[]? points = null) =>
    new(id, name, level, school, false, effects, auras ?? [], targets ?? [6], points ?? [100], 0, 2000, 0, 100, false);
var facts = new[] { Spell(133, "Fireball", 60, 2, [2]), Spell(116, "Frostbolt", 50, 4, [2,6]),
    Spell(635, "Holy Light", 60, 1, [77], targets: [21]), Spell(2139, "Counterspell", 24, 6, [68]),
    Spell(527, "Dispel Magic", 18, 1, [38], targets: [25]), Spell(355, "Taunt", 10, 0, [114]),
    Spell(871, "Shield Wall", 28, 0, [6], [87], [1], [-75]) };
var capable = CommanderRaidCapabilityLaw.Resolve(new(2000, "Fact fixture", 1, true, true, facts.Select(f => f.Id).ToHashSet()), facts, 4);
Check(capable.PreferredDamage == 116, "fire immunity chooses a learned non-fire attack even when fire spell is higher rank");
Check(capable.PreferredHeal == 635, "script-effect Holy Light counts as healing in this Core database");
var flash = Spell(19943, "Flash of Light", 58, 1, [77], targets: [21]);
Check(CommanderRaidCapabilityLaw.Resolve(new(6, "Holy paladin", 2, true, true, new HashSet<uint>{19943}), [flash], 0).PreferredHeal == 19943, "script-effect Flash of Light supports healer readiness");
var selfHeal = Spell(19243, "Desperate Prayer", 60, 1, [10], targets: [1]);
Check(CommanderRaidCapabilityLaw.Resolve(new(7, "Priest", 5, true, true, new HashSet<uint>{19243}), [selfHeal], 0).PreferredHeal == 0, "a self-only racial cannot claim to heal an assigned patient");
var fakeHeal = flash with { Effects = [6] };
Check(CommanderRaidCapabilityLaw.Resolve(new(6, "Holy paladin", 2, true, true, new HashSet<uint>{19943}), [fakeHeal], 0).PreferredHeal == 0, "paladin healing family still requires its actual healing effect");
Check(capable.InterruptSpell == 2139 && capable.DispelSpell == 527 && capable.TauntSpell == 355 && capable.DefensiveSpell == 871, "support duties come from learned spell effects");
var reckless = Spell(1719, "Recklessness", 60, 0, [6,6,6], [52,87,28], [1], [99,19,-1]);
var slideGoal = CommanderRaidAttemptLaw.TankMovementGoal(new(34,-195,0), new(36,-215,0), new(34,-215,0), 30, true, new(0,-215,0));
Check(slideGoal.X > 34 && slideGoal.Y > -215, "sideways knockback recovers the authored outward boss facing instead of holding the slide");
var rotatedSlide = CommanderRaidAttemptLaw.TankMovementGoal(new(-195,34,0), new(-215,36,0), new(-215,34,0), 30, true, new(-215,0,0));
Check(rotatedSlide.Y > 34 && rotatedSlide.X > -215, "outward facing recovery rotates with encounter geometry");
var wall = facts.Single(f => f.Id == 871);
Check(CommanderRaidCapabilityLaw.Resolve(new(3, "Warrior", 1, true, true, new HashSet<uint>{1719,871}), [reckless,wall], 0).DefensiveSpell == 871, "Recklessness negative unrelated effect is not damage reduction");
var wingClip = Spell(14268, "Wing Clip", 60, 0, [6,2]) with { MaxRange = 5 };
var rangedShot = Spell(14287, "Arcane Shot", 60, 6, [2]) with { MaxRange = 35 };
Check(CommanderRaidCapabilityLaw.Resolve(new(4, "Hunter", 3, true, true, new HashSet<uint>{14268,14287}), [wingClip,rangedShot], 0).PreferredDamage == 14287, "melee damage cannot fill a ranged station");
var mock = Spell(20560, "Mocking Blow", 60, 0, [2,6], [0,11]);
var trueTaunt = facts.Single(f => f.Id == 355);
Check(CommanderRaidCapabilityLaw.Resolve(new(5, "Tank", 1, true, true, new HashSet<uint>{20560,355}), [mock,trueTaunt], 0).TauntSpell == 355, "true taunt outranks a temporary forced-target attack");
var autoShot = Spell(75, "Auto Shot", 1, 0, [58]) with { AutoRepeat = true };
Check(CommanderRaidCapabilityLaw.Resolve(new(2, "Hunter", 3, true, true, new HashSet<uint>{75}), [autoShot], 0).PreferredDamage == 75, "learned ranged auto-repeat is a usable hunter fallback");
Check(CommanderRaidCapabilityLaw.Resolve(capable with { Spells = new HashSet<uint>() }, facts, 0).PreferredHeal == 0, "catalog facts cannot grant an unlearned ability");
Check(CommanderBossCatalog.Count == 217 && CommanderBossCatalog.Find(10184)?.ImmuneSchools == 4, "actual audited boss catalog loads with Onyxia fire immunity");
Check(ReferenceEquals(CommanderEncounterSelectionLaw.Select([plan.Encounter], CommanderBossCatalog.Find(10184)!, 249, default, 40), plan.Encounter), "live target selection prefers an authored encounter");
var basic = CommanderEncounterSelectionLaw.Select([], CommanderBossCatalog.Find(646)!, 36, new(10,20,30), 5);
Check(basic.Coverage == "basic" && basic.Rules.Length == 0 && basic.Teams.Length == 1 && CommanderEncounterLaw.Validate(basic).Count == 0, "unmapped target gets an explicit basic plan, with no invented mechanics");

var qaRoster = Enumerable.Range(115,28).Concat(Enumerable.Range(150,11)).Select(g => (ulong)g).Prepend(787UL).ToArray();
Check(CommanderRaidAttemptLaw.AuthorizedRoster(qaRoster), "QA accepts the existing exact forty-character roster");
Check(!CommanderRaidAttemptLaw.AuthorizedRoster(qaRoster.Skip(1).Append(999UL)), "QA refuses substitution of an unrelated main");
Check(!CommanderRaidAttemptLaw.AuthorizedRoster(qaRoster.Skip(1).Append(115UL)), "QA refuses duplicate GUIDs despite a count of forty");
Check(CommanderRaidAttemptLaw.CommandRefusal(CommanderRaidAttemptStage.Preparation, ".replenish", true) is not null, "premature combat blocks replenishment before it is sent");
Check(CommanderRaidAttemptLaw.CommandRefusal(CommanderRaidAttemptStage.Fighting, ".gm on", false) is not null, "combat evidence stays sealed even between combat-flag updates");
Check(CommanderRaidAttemptLaw.CommandRefusal(CommanderRaidAttemptStage.Fighting, ".list threat", true) is null, "exact read-only threat observation remains available during battle");
Check(CommanderRaidAttemptLaw.CommandRefusal(CommanderRaidAttemptStage.Fighting, ".list threat anything", true) is not null, "command allowlist does not accept arbitrary prefixes");
Check(CommanderRaidAttemptLaw.CommandRefusal(CommanderRaidAttemptStage.Recovery, ".revive Testwar", true) is null, "explicit recovery permits the authorized character recovery");
Check(CommanderRaidAttemptLaw.CommandRefusal(CommanderRaidAttemptStage.Ready, ".gm off", false) is null &&
      CommanderRaidAttemptLaw.CommandRefusal(CommanderRaidAttemptStage.Ready, ".replenish", false) is not null, "sealed setup permits GM-off but requires resealing after a preparation change");
Check(CommanderRaidAttemptLaw.StartRefusal(CommanderRaidAttemptStage.Ready, qaRoster, true, true, true, true, false) is null, "fresh sealed full-health preparation can request ordinary combat");
Check(CommanderRaidAttemptLaw.StartRefusal(CommanderRaidAttemptStage.Ready, qaRoster, false, true, true, true, false) is not null, "stale or incomplete character facts prevent a pull");
Check(CommanderRaidAttemptLaw.StartRefusal(CommanderRaidAttemptStage.Ready, qaRoster, true, true, true, true, true) is not null, "a pre-pulled boss cannot become a fresh acceptance attempt");
Check(CommanderRaidAttemptLaw.StartRefusal(CommanderRaidAttemptStage.Recovery, qaRoster, true, true, true, true, false) is not null, "recovery cannot skip the preparation seal");

Check(CommanderRaidAttemptLaw.CommandRefusal(CommanderRaidAttemptStage.PullPending, ".replenish", false) is not null, "GM-off acknowledgment window cannot admit preparation commands");
Check(CommanderRaidAttemptLaw.StartRefusal(CommanderRaidAttemptStage.Ready, qaRoster, true, true, false, true, false) is not null, "pull boundary must precede leaving confirmed GM preparation mode");
var landingHold = CommanderRaidAttemptLaw.TankMovementGoal(new(-18,-190,0), new(-20,-190,0), new(34,-215,0), 30, true, new(-14.5f,-213,0));
Check(landingHold.X >= -18.001f && Vector3.Dot(landingHold-new Vector3(-18,-190,0),new Vector3(52,-25,0)) > 0 && Vector3.Distance(landingHold,new(-18,-190,0)) <= 2.001f, "landing tank acquires its authored station only through bounded outward steps");



Check(CommanderRaidAttemptLaw.ConsumablePreparationAllowed(CommanderRaidAttemptStage.Recovery, 115, false), "authorized recovery consumable is allowed");
Check(!CommanderRaidAttemptLaw.ConsumablePreparationAllowed(CommanderRaidAttemptStage.Fighting, 115, false), "sealed combat rejects preparation consumables");
Check(!CommanderRaidAttemptLaw.ConsumablePreparationAllowed(CommanderRaidAttemptStage.Recovery, 115, true), "combat blocks preparation consumables");
Check(!CommanderRaidAttemptLaw.ConsumablePreparationAllowed(CommanderRaidAttemptStage.Recovery, 143, false), "unrelated characters cannot receive QA consumables");
Check(CommanderRaidAttemptLaw.ConsumedExactlyOne(2, 1, true), "one consumed item and observed aura prove use");
Check(!CommanderRaidAttemptLaw.ConsumedExactlyOne(2, 2, true) && !CommanderRaidAttemptLaw.ConsumedExactlyOne(2, 0, true) && !CommanderRaidAttemptLaw.ConsumedExactlyOne(1, 0, false), "an existing aura, multiple losses or missing aura cannot certify one use");


Check(CommanderRaidAttemptLaw.TankMovementGoal(new(13,0,0), Vector3.Zero, new(34,0,0), 15, true, Vector3.Zero) == new Vector3(15,0,0), "recovered normal melee contact permits backing toward the authored station");
Check(CommanderRaidAttemptLaw.TankMovementGoal(new(34,0,0), new(20,0,0), new(34,0,0), 15, true, Vector3.Zero) == new Vector3(34,0,0), "wall-backed tank holds legal spacing instead of walking toward a near-wall boss");
Check(CommanderRaidAttemptLaw.TankMovementGoal(new(0,34,0), new(0,20,0), new(0,34,0), 15, true, Vector3.Zero) == new Vector3(0,34,0), "legal wall-backed spacing is independent of encounter orientation");





Check(CommanderRaidGuidanceLaw.Fresh(2.5) && !CommanderRaidGuidanceLaw.Fresh(2.50001) && !CommanderRaidGuidanceLaw.Fresh(2.9), "movement and advice share the exact freshness boundary; no half-second fallback gap");
Check(!CommanderRaidGuidanceLaw.Fresh(double.NaN) && !CommanderRaidGuidanceLaw.Fresh(double.PositiveInfinity) && !CommanderRaidGuidanceLaw.Fresh(-.1), "unknown or reversed advice ages cannot authorize movement");
var recordedTank = new Vector3(36.94167f,-212.06514f,-81.46965f);
var recordedBoss = new Vector3(30,-209.9f,-83);
var recordedGoal = CommanderRaidAttemptLaw.TankMovementGoal(recordedTank, recordedBoss, new(34,-215,-84), 30, true, new(-14.5f,-213,-87.5f));
var radialOffset = new Vector2(recordedTank.X-recordedBoss.X,recordedTank.Y-recordedBoss.Y);
var facingStep = new Vector2(recordedGoal.X-recordedTank.X,recordedGoal.Y-recordedTank.Y);
Check(Vector2.Dot(radialOffset,facingStep) >= -.001f, "recorded post-knockback correction has no inward radial component");
Check(Vector2.Distance(new(recordedGoal.X,recordedGoal.Y), new(recordedBoss.X,recordedBoss.Y)) < radialOffset.Length()*1.1f, "small facing correction does not push out to a fraction of maximum boss reach");
var circleTank = new Vector3(0,10,0);
var followingBoss = Vector3.Zero;
bool spacingPreserved = true;
for(int tick=0;tick<400;++tick)
{
    var goal = CommanderRaidAttemptLaw.TankMovementGoal(circleTank,followingBoss,new(34,0,0),30,true,Vector3.Zero);
    var delta = goal-circleTank;
    if(delta.Length()>.001f) circleTank += Vector3.Normalize(delta)*MathF.Min(.1f,delta.Length());
    var chase = circleTank-followingBoss;
    if(chase.Length()>30) followingBoss += Vector3.Normalize(chase)*MathF.Min(.2f,chase.Length()-30);
    spacingPreserved &= Vector3.Distance(circleTank,followingBoss) >= 9.999f;
}
Check(spacingPreserved, "turning and station acquisition do not collapse tank separation");
Check(Vector2.Distance(new(circleTank.X,circleTank.Y),new(34,0)) < 2.01f, "tank establishes authored station while an ordinary pursuing enemy follows");
Check(CommanderRaidAttemptLaw.TankMovementGoal(new(34,0,0),Vector3.Zero,new(34,0,0),15,true,Vector3.Zero)==new Vector3(34,0,0), "stationed tank waits for the enemy after retained-threat knockback");
Check(CommanderRaidAttemptLaw.TankMovementGoal(new(34,0,0),Vector3.Zero,new(34,0,0),15,false,Vector3.Zero)==new Vector3(14.25f,0,0), "threat loss overrides station hold and reacquires contact");


Check(CommanderRaidAttemptLaw.TankMovementGoal(new(-20,0,0), Vector3.Zero, new(34,0,0),30,true,Vector3.Zero)==new Vector3(2,0,0), "opposite-side acquisition crosses directly without orbiting the chasing boss");
Check(CommanderRaidAttemptLaw.TankMovementGoal(new(0,-20,0), Vector3.Zero, new(0,34,0),30,true,Vector3.Zero)==new Vector3(0,2,0), "opposite-side acquisition rotates with the encounter geometry");
Check(CommanderRaidAttemptLaw.TankMovementGoal(new(29.5f,0,0),Vector3.Zero,new(34,0,0),30,true,Vector3.Zero)==new Vector3(31.5f,0,0), "retained-threat station retreat crosses the attack margin instead of oscillating before enemy pursuit");
Check(CommanderRaidAttemptLaw.TankMovementGoal(new(29.5f,0,0),Vector3.Zero,new(34,0,0),30,false,Vector3.Zero)==new Vector3(29.25f,0,0), "without tank threat the same position reacquires contact");
var holdPacket=(byte[])bytes.Clone();holdPacket[48]=1;holdPacket[49]=255;holdPacket[50]=255;
Array.Clear(holdPacket,51,4);
Check(CommanderRaidWire.TryParse(holdPacket,out var parsedHold),"ordinary coverage hold survives the actual packet parser");
Check(CommanderRaidGuidanceLaw.Current(parsedHold,1,adviceDefinition with { Rules=[] },.5)?.State==1,"parsed coverage hold reaches the controller without an encounter rule");
foreach(byte badHoldState in new byte[]{0,2,3,4}) {holdPacket[48]=badHoldState;Check(!CommanderRaidWire.TryParse(holdPacket,out _),"wire reserved index refuses non-hold state "+badHoldState);}
holdPacket[48]=1;holdPacket[51]=1;Check(!CommanderRaidWire.TryParse(holdPacket,out _),"ordinary hold cannot carry a timed hazard impact");
holdPacket[51]=0;holdPacket[49]=96;holdPacket[50]=0;Check(!CommanderRaidWire.TryParse(holdPacket,out _),"unreserved out-of-range rule remains rejected");
holdPacket[49]=64;Check(CommanderRaidWire.TryParse(holdPacket,out var observedHazardStatus),"observed hazard index round-trips");
Check(CommanderRaidGuidanceLaw.Current(observedHazardStatus,1,adviceDefinition,.5) is null,"absent observed hazard policy cannot authorize advice");
var hazardDefinition=adviceDefinition with {Mechanics=new() {Hazards=[new([1],[],6,1000,62,100,true,false)]}};
Check(CommanderRaidGuidanceLaw.Current(observedHazardStatus,1,hazardDefinition,.5) is not null,"declared observed hazard authorizes bounded advice");
var ordinaryHoldState = state! with { Actors = [state!.Actors[0] with { Guidance = new(1, ushort.MaxValue, 0, new(0,-200,-85)) }] };
Check(CommanderRaidGuidanceLaw.Current(ordinaryHoldState,1,adviceDefinition with { Rules=[] },.5)?.State==1,"generic coverage hold works without encounter-specific rules");
Check(CommanderRaidGuidanceLaw.Current(ordinaryHoldState,1,adviceDefinition,3) is null,"generic hold cannot use stale advice");
Check(CommanderRaidGuidanceLaw.Current(ordinaryHoldState,2,adviceDefinition,.5) is null,"generic hold is actor scoped");
foreach(byte moveState in new byte[]{2,3,4}) {
 var invalidHold=ordinaryHoldState with { Actors=[ordinaryHoldState.Actors[0] with { Guidance=new(moveState,ushort.MaxValue,0,new(0,-200,-85)) }] };
 Check(CommanderRaidGuidanceLaw.Current(invalidHold,1,adviceDefinition,.5) is null,"reserved ordinary hold cannot authorize movement or hazard state "+moveState);
}
var outsideHold=ordinaryHoldState with { Actors=[ordinaryHoldState.Actors[0] with { Guidance=new(1,ushort.MaxValue,0,new(99999,99999,99999)) }] };
Check(CommanderRaidGuidanceLaw.Current(outsideHold,1,adviceDefinition,.5)?.State==1,"ordinary hold has no out-of-bounds movement destination and must not suppress normal combat");
var recordedOutsideHold=ordinaryHoldState with { Actors=[ordinaryHoldState.Actors[0] with { Guidance=new(1,ushort.MaxValue,0,new(36.09003f,-234.25854f,-78.82417f)) }] };
Check(CommanderRaidGuidanceLaw.Current(recordedOutsideHold,1,adviceDefinition,.5)?.State==1,"recorded post-knockback hold reaches combat controller");
var invalidCoordinateHold=ordinaryHoldState with { Actors=[ordinaryHoldState.Actors[0] with { Guidance=new(1,ushort.MaxValue,0,new(float.NaN,0,0)) }] };
Check(CommanderRaidGuidanceLaw.Current(invalidCoordinateHold,1,adviceDefinition,.5) is null,"ordinary hold still refuses nonfinite server facts");
var outsideMovement=ordinaryHoldState with { Actors=[ordinaryHoldState.Actors[0] with { Guidance=new(2,0,0,new(99999,99999,99999)) }] };
Check(CommanderRaidGuidanceLaw.Current(outsideMovement,1,adviceDefinition,.5) is null,"actual movement advice remains inside authored room bounds");
foreach(float rotation in new[]{0f,.7f,1.5707963f,3.1415927f})
foreach(var initial in new[]{(new Vector3(22,-195,0),new Vector3(-7.4f,-191.53f,0)),(new Vector3(20,-230,0),new Vector3(-9,-235,0))})
{
    Vector3 Rotate(Vector3 v) => new(v.X*MathF.Cos(rotation)-v.Y*MathF.Sin(rotation),v.X*MathF.Sin(rotation)+v.Y*MathF.Cos(rotation),v.Z);
    var actor=Rotate(initial.Item1);var enemy=Rotate(initial.Item2);var anchor=Rotate(new(34,-215,0));var room=Rotate(new(-14.5f,-213,0));
    for(int tick=0;tick<4000;++tick)
    {
        var step=CommanderRaidAttemptLaw.TankMovementGoal(actor,enemy,anchor,30,true,room)-actor;
        // Match the real movement driver's arrival tolerance, not an ideal integrator.
        if(step.Length()>.2f)actor+=Vector3.Normalize(step)*MathF.Min(.1f,step.Length());
        var pursuit=actor-enemy;
        if(pursuit.Length()>30)enemy+=Vector3.Normalize(pursuit)*MathF.Min(.2f,pursuit.Length()-30);
    }
    Check(Vector3.Distance(actor,anchor)<2.21f,"recorded off-axis landing reaches station with ordinary pursuit and real arrival tolerance, rotation "+rotation);
}
foreach(float gap in new[]{.01f,.1f,.19f})
{
    var actor=new Vector3(0,29.25f+gap,0);
    var goal=CommanderRaidAttemptLaw.TankMovementGoal(actor,Vector3.Zero,new(34,0,0),30,true,Vector3.Zero);
    Check(Vector3.Distance(actor,goal)>.2f && goal.X>0,"sub-tolerance contact gap permits facing recovery: "+gap);
    Check(CommanderRaidAttemptLaw.TankMovementGoal(actor,Vector3.Zero,new(34,0,0),30,false,Vector3.Zero)==actor,"sub-tolerance legal contact without threat holds: "+gap);
}
var rangedStart = new Vector3(952,-952,-183.78f);
var rangedEnemy = new Vector3(1026.27f,-959.98f,-180.18f);
Check(!CommanderRaidAttemptLaw.RangedPullRouteClear(new(952,-952,-183),new(997,-958,-180),new(1015,-967,-181),40),"recorded westbound patrol blocks ranged approach before contact");
Check(CommanderRaidAttemptLaw.RangedPullRouteClear(new(952,-952,-183),new(997,-958,-180),new(1090,-990,-181),40),"distant patrol permits ordinary ranged approach");
Check(!CommanderRaidAttemptLaw.RangedPullRouteClear(new(0,0,0),new(100,0,0),new(50,10,0),40),"ranged patrol clearance checks the entire route");
Check(!CommanderRaidAttemptLaw.RangedPullRouteClear(Vector3.Zero,Vector3.Zero,new(0,20,0),40),"stationary ranged actor detects nearby patrol without division by zero");
var recordedReturn = CommanderRaidAttemptLaw.RangedPullReturnGoal(new(997,-958,-180),new(1024,-961,-180),new(976,-972,-180));
Check(recordedReturn.X < 997 && recordedReturn.Y < -958,"ranged pull returns toward angled support before melee acquisition");
Check(Vector3.Distance(recordedReturn,new(997,-958,-180)) <= 2.001f,"ranged return uses bounded steps");
Check(CommanderRaidAttemptLaw.RangedPullReturnGoal(new(976,-972,-180),new(1024,-961,-180),new(976,-972,-180))==new Vector3(976,-972,-180),"ranged return waits at station for enemy pursuit");
Check(CommanderRaidAttemptLaw.RangedPullReturnGoal(new(0,0,0),new(5,0,0),new(10,0,0))==Vector3.Zero,"ranged return never crosses an enemy to reach station");
Check(CommanderRaidAttemptLaw.RangedPullReturnGoal(new(0,0,0),new(0,20,0),new(10,-10,0)).Y < 0,"rotated ranged return retains retreat direction");
var rangedGoal = CommanderRaidAttemptLaw.RangedPullGoal(rangedStart,rangedEnemy,30);
Check(MathF.Abs(Vector2.Distance(new(rangedGoal.X,rangedGoal.Y),new(rangedEnemy.X,rangedEnemy.Y))-28)<.001f,"recorded middle pull stops at ordinary ranged distance");
Check(Vector2.Distance(new(rangedGoal.X,rangedGoal.Y),new(1030,-976))>30,"recorded ranged initiation avoids the neighbouring patrol's melee approach");
Check(rangedGoal.Z==rangedStart.Z,"ranged pull uses ordinary grounded movement rather than target altitude");
Check(CommanderRaidAttemptLaw.RangedPullGoal(rangedEnemy,rangedEnemy,30)==rangedEnemy,"overlapping ranged target never divides by zero");
Check(CommanderRaidAttemptLaw.RangedPullGoal(rangedGoal,rangedEnemy,30)==rangedGoal,"arrival tolerance holds for a stationary normal ranged shot");
var retreatGoal=CommanderRaidAttemptLaw.TankMovementGoal(rangedGoal,rangedEnemy,new(976,-960,-181),5,true,new(1010,-963,-183));
Check(retreatGoal.X<rangedGoal.X,"ranged threat acquisition backs toward the authored tank station before melee contact");

var dependentDamage = compiledDefinition with { Schema=2, Mechanics=new CommanderMechanics {
    DamagePolicies=[new CommanderDamagePolicy([123],[],127,true){Roles=56,WhileObjectiveAlive=true}] } };
var dependentJson=dependentDamage.ToJson();
Check(CommanderEncounterDefinition.Parse(dependentJson).Mechanics.DamagePolicies[0].WhileObjectiveAlive,
    "conditional damage policy survives client JSON roundtrip");
var ordinaryDamage=dependentDamage with { Mechanics=new CommanderMechanics {
    DamagePolicies=[new CommanderDamagePolicy([123],[],127,true){Roles=56}] } };
Check(!CommanderEncounterDefinition.Parse(ordinaryDamage.ToJson()).Mechanics.DamagePolicies[0].WhileObjectiveAlive,
    "ordinary damage policy remains independent of primary completion");
bool rejectedConditionalNumber=false;
try { CommanderEncounterDefinition.Parse(dependentJson.Replace("\"whileObjectiveAlive\": true","\"whileObjectiveAlive\": 1").Replace("\"whileObjectiveAlive\":true","\"whileObjectiveAlive\":1")); }
catch(System.Text.Json.JsonException) { rejectedConditionalNumber=true; }
Check(rejectedConditionalNumber,"conditional damage policy rejects a numeric boolean");

var relationDefinition=compiledDefinition with {Schema=2,Mechanics=new CommanderMechanics {
    AddDistances=[new CommanderAddDistancePolicy([compiledDefinition.AddEntries[0]],[compiledDefinition.BossEntry],30,99,true){ReferenceAura=900}]}};
var relationRoundtrip=CommanderEncounterDefinition.Parse(relationDefinition.ToJson()).Mechanics.AddDistances[0];
Check(relationRoundtrip.Minimum==30&&relationRoundtrip.Maximum==99&&relationRoundtrip.Flat&&relationRoundtrip.ReferenceAura==900,
    "source-derived add-distance bounds and activation survive the paired client schema");
foreach(var invalidRelation in new[] {
    relationRoundtrip with {Minimum=100}, relationRoundtrip with {Minimum=0,Maximum=0},
    relationRoundtrip with {References=[uint.MaxValue]}, relationRoundtrip with {Entries=[relationDefinition.BossEntry]},
    relationRoundtrip with {Maximum=float.NaN}, relationRoundtrip with {Entries=[compiledDefinition.AddEntries[0],compiledDefinition.AddEntries[0]]} })
    Check(CommanderEncounterLaw.Validate(relationDefinition with {Mechanics=new CommanderMechanics{AddDistances=[invalidRelation]}}).Count>0,
        "add-distance policy refuses invalid bounds, identities or duplicate subjects");
Check(CommanderEncounterLaw.Validate(relationDefinition with {Objectives=null!}).Count>0,
    "invalid primary collection reports validation errors without throwing in distance policies");
var waveDefinition=compiledDefinition with {Schema=2,Mechanics=new CommanderMechanics {
    Waves=[new CommanderWavePolicy(compiledDefinition.AddEntries[0],8,900,90000,180000)]}};
var waveRoundtrip=CommanderEncounterDefinition.Parse(waveDefinition.ToJson()).Mechanics.Waves[0];
Check(waveRoundtrip.Count==8&&waveRoundtrip.Aura==900&&waveRoundtrip.ActiveMs==90000&&waveRoundtrip.RecoveryMs==180000,
    "conditional summoned-wave count and source countdowns survive the paired schema");
Check(CommanderEncounterLaw.Validate(waveDefinition).Count==0,"valid optional wave does not add required kills");
foreach(var invalidWave in new[] {waveRoundtrip with {Count=0},waveRoundtrip with {Count=65},
    waveRoundtrip with {Entry=compiledDefinition.BossEntry},waveRoundtrip with {Aura=0},
    waveRoundtrip with {ActiveMs=3600001},waveRoundtrip with {RecoveryMs=3600001}})
    Check(CommanderEncounterLaw.Validate(waveDefinition with {Mechanics=new CommanderMechanics{Waves=[invalidWave]}}).Count>0,
        "wave parser refuses invalid identities, counts and timers");
Check(CommanderEncounterLaw.Validate(waveDefinition with {Mechanics=new CommanderMechanics{Waves=[waveRoundtrip,waveRoundtrip]}}).Count>0,
    "duplicate wave identity is rejected");
// Pre-pull readiness reads only Spell.dbc shapes: food trigger, generic-family long self auras, class-family long auras, absorb.
CommanderRaidAuraFact Fact(uint id, uint family, int durationMs, uint[] effects, uint[] auras, uint[] targets, uint[]? triggers = null) =>
    new(id, family, durationMs, effects, auras, targets, triggers ?? []);
var readinessCatalog = new[]
{
    Fact(1000, 0, 30000, [6, 6, 0], [84, 23, 0], [1, 1, 0], [0, 1001, 0]),   // seated food whose trigger is the well-fed buff
    Fact(1001, 0, 15 * 60 * 1000, [6, 6, 0], [29, 29, 0], [1, 1, 0]),         // the well-fed buff itself
    Fact(1002, 0, 60 * 60 * 1000, [6, 6, 0], [29, 52, 0], [1, 1, 0]),         // an elixir: generic family, long, self, aura-only
    Fact(1003, 0, 120 * 60 * 1000, [6, 0, 0], [34, 0, 0], [1, 0, 0]),         // a flask
    Fact(1004, 6, 30 * 60 * 1000, [6, 0, 0], [29, 0, 0], [21, 0, 0]),         // a class buff on a friend
    Fact(1005, 0, 60 * 60 * 1000, [6, 0, 0], [69, 0, 0], [1, 0, 0]),          // a school-absorb potion
    Fact(1006, 0, 2 * 60 * 1000, [6, 0, 0], [29, 0, 0], [1, 0, 0]),           // a short generic aura: neither elixir nor buff
    Fact(1007, 0, 60 * 60 * 1000, [10, 6, 0], [0, 29, 0], [1, 1, 0]),         // heals as well: not aura-only
};
var wellFed = CommanderRaidReadinessLaw.WellFedSpells(readinessCatalog);
Check(wellFed.SetEquals(new HashSet<uint> { 1001 }), "well-fed buffs are the triggers of seated regeneration foods");
CommanderRaidAuraFact? Lookup(uint id) => readinessCatalog.FirstOrDefault(f => f.Id == id) is { Id: not 0 } f ? f : null;
var readiness = CommanderRaidReadinessLaw.Read([1001, 1002, 1003, 1004, 1005, 1006, 1007], Lookup, wellFed);
Check(readiness is { Fed: true, Elixirs: 2, Shielded: true, Buffs: 1 } && readiness.Ready, "readiness counts food, elixir and flask, shield and class buff from aura shapes");
Check(!CommanderRaidReadinessLaw.Read([1004], Lookup, wellFed).Ready && CommanderRaidReadinessLaw.Read([], Lookup, wellFed).Text == "Not fed - no elixirs",
    "a buffed but unfed, unflasked member is not ready and says so");
Check(CommanderRaidReadinessLaw.Read([1001, 1002, 9999], id => null, wellFed).Fed, "an unknown aura never breaks the reading");
Console.WriteLine($"commander-raid-check: {passed} checks passed. Planner/profile/wire checks only; Core execution and raid clears require live testing.");
