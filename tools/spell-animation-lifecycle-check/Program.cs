using System.Numerics;
using MSUIClient;
using MSUIClient.Formats;
using MSUIClient.World.Units;

if (args.Length != 1)
{
    Console.Error.WriteLine("usage: spell-animation-lifecycle-check <client-config.json>");
    return 2;
}

int checks = 0;
void Check(bool condition, string message)
{
    checks++;
    if (!condition) throw new InvalidOperationException(message);
}
void Near(double actual, double expected, double epsilon, string message)
    => Check(Math.Abs(actual - expected) <= epsilon,
        $"{message}: expected {expected}, got {actual}");

ClientConfig config = ClientConfig.Load(args[0]);
using var mpq = new MpqMount(config.ClientDataPath);
DbcFile visuals = Read(@"DBFilesClient\SpellVisual.dbc");
DbcFile kits = Read(@"DBFilesClient\SpellVisualKit.dbc");
DbcFile names = Read(@"DBFilesClient\SpellVisualEffectName.dbc");
Check(visuals.RecordCount == 2165, "SpellVisual.dbc fixture drift");
Check(kits.RecordCount == 1772, "SpellVisualKit.dbc fixture drift");
Check(names.RecordCount == 775, "SpellVisualEffectName.dbc fixture drift");

var paths = new Dictionary<uint, string>();
for (int row = 0; row < names.RecordCount; row++)
{
    uint id = names.GetUInt(row, 0);
    string path = SpellVisualCatalog.ModelPath(names.GetString(row, 2));
    if (id != 0 && path.Length > 0) paths[id] = path;
}

var kitEffects = new Dictionary<uint, uint[]>();
for (int row = 0; row < kits.RecordCount; row++)
    kitEffects[kits.GetUInt(row, 0)] = Enumerable.Range(0, 9)
        .Select(i => kits.GetUInt(row, 3 + i))
        .Where(id => id is not 0 and not uint.MaxValue).ToArray();

var uses = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
void Add(uint effect, string use)
{
    if (!paths.TryGetValue(effect, out string? path)) return;
    if (!uses.TryGetValue(path, out HashSet<string>? set)) uses[path] = set = [];
    set.Add(use);
}
void AddKit(uint kit, string use)
{
    if (kit is 0 or uint.MaxValue || !kitEffects.TryGetValue(kit, out uint[]? effects)) return;
    foreach (uint effect in effects) Add(effect, use);
}
for (int row = 0; row < visuals.RecordCount; row++)
{
    AddKit(visuals.GetUInt(row, 1), "PRECAST_HOLD");
    AddKit(visuals.GetUInt(row, 2), "CAST_ONESHOT");
    AddKit(visuals.GetUInt(row, 3), "IMPACT_ONESHOT");
    AddKit(visuals.GetUInt(row, 4), "AURA_STATE_HOLD");
    AddKit(visuals.GetUInt(row, 5), "CHANNEL_HOLD");
    Add(visuals.GetUInt(row, 7), "MISSILE");
    if (visuals.GetUInt(row, 11) != 0) Add(visuals.GetUInt(row, 12), "AREA_LOOP");
}
foreach (string area in SpellAreaVisualLaw.ClientShardModels)
{
    if (!uses.TryGetValue(area, out HashSet<string>? set)) uses[area] = set = [];
    set.Add("AREA_SHARD_ONESHOT");
}

var oldOneShotIds = new HashSet<int>
    { 1, 7, 9, 16, 17, 18, 19, 20, 21, 22, 23, 24, 30, 37, 39, 85, 87, 88, 117, 187 };
int resolved = 0, missing = 0, sequenceLess = 0, multi = 0, globalSequences = 0;
int oldLoopMismatches = 0, defaultSlotMismatches = 0, missileFallbacks = 0;
int animatorSequences = 0, soundModels = 0;
string? soundFixturePath = null;
M2Model? soundFixtureModel = null;

foreach ((string path, HashSet<string> use) in uses.OrderBy(x => x.Key))
{
    byte[]? bytes = mpq.ReadFile(path);
    M2Model? model = bytes is null ? null : M2Reader.Parse(bytes);
    if (model is null) { missing++; continue; }
    resolved++;
    if (model.Sequences.Count == 0) { sequenceLess++; continue; }
    if (model.Sequences.Count > 1) multi++;
    if (model.GlobalSequenceDurations.Count > 0) globalSequences++;

    M2Sequence first = model.Sequences[0];
    if (first.IsLooping != !oldOneShotIds.Contains(first.AnimationId)) oldLoopMismatches++;
    if (model.TryFindSequenceIndexByAnimationId(first.AnimationId) != 0) defaultSlotMismatches++;

    SpellEffectPlayback ordinary = SpellEffectPlaybackLaw.Resolve(model, missile: false);
    Check(ordinary.SequenceIndex == 0, $"ordinary effect did not select slot zero: {path}");
    Check(ordinary.AnimationId == first.AnimationId, $"ordinary animation id drift: {path}");
    Check(ordinary.Looping == first.IsLooping, $"ordinary loop law drift: {path}");
    Near(ordinary.SpanSeconds, SpellAttachment.SelfTerminatingSpan(model), 1e-9,
        $"ordinary span drift: {path}");

    if (use.Contains("MISSILE"))
    {
        int inFlight = model.TryFindSequenceIndexByAnimationId(
            SpellEffectPlaybackLaw.InFlightAnimationId);
        if (inFlight < 0) missileFallbacks++;
        SpellEffectPlayback missile = SpellEffectPlaybackLaw.Resolve(model, missile: true);
        Check(missile.SequenceIndex == (inFlight >= 0 ? inFlight : 0),
            $"missile sequence decision drift: {path}");
    }

    if (model.Events.Any(e => e.Data != 0 && e.Identifier is "$SND" or "$DSL" or "$DSO"))
    {
        soundModels++;
        if (soundFixturePath is null && SpellAttachment.HasVisibleContent(model) &&
            SpellEffectPlaybackLaw.CrossedSoundEvents(model,
                ordinary with { Looping = false }, -1e-9,
                ordinary.SpanSeconds + .001).Count > 0)
        { soundFixturePath = path; soundFixtureModel = model; }
    }

    if (!model.HasSkeleton) continue;
    TextWriter original = Console.Out;
    M2Animator? animator;
    try
    {
        Console.SetOut(TextWriter.Null);
        animator = M2Animator.Build(model, [ordinary.AnimationId],
            includeStaticSequences: true);
    }
    finally { Console.SetOut(original); }
    Check(animator is not null, $"skeleton did not produce animator: {path}");
    var selectedSequences = new HashSet<int> { ordinary.SequenceIndex };
    if (use.Contains("MISSILE"))
        selectedSequences.Add(SpellEffectPlaybackLaw.Resolve(model, missile: true).SequenceIndex);
    foreach (int sequence in selectedSequences)
    {
        M2Animator.Clip? clip = animator!.FindSequenceOrBake(sequence);
        if (model.Sequences[sequence].DurationMs <= 0) continue;
        Check(clip is not null, $"selected sequence {sequence} did not bake: {path}");
        Check(clip!.SequenceIndex == sequence, $"baked sequence identity drift: {path}");
        Check(clip.AnimationId == model.Sequences[sequence].AnimationId,
            $"baked animation id drift: {path}");
        Check(clip.Looping == model.Sequences[sequence].IsLooping,
            $"rig loop law drift: {path} sequence {sequence}");
        Near(clip.DurationSeconds, model.Sequences[sequence].DurationMs / 1000.0,
            0.0001, $"rig duration drift: {path} sequence {sequence}");
        animatorSequences++;
    }
}

Check(uses.Count == 599, "referenced effect census drift");
Check(resolved == 555 && missing == 44 && sequenceLess == 0,
    "resolved/missing effect census drift");
Check(multi == 157 && globalSequences == 339, "sequence/global-sequence census drift");
Check(oldLoopMismatches == 409,
    "old hardcoded loop-law mismatch census drift");
Check(defaultSlotMismatches == 0, "default slot lookup census drift");
Check(missileFallbacks == 42, "missile first-sequence fallback census drift");
Check(animatorSequences > 0, "no exact animator sequences validated");
Check(soundModels > 0 && soundFixturePath is not null && soundFixtureModel is not null,
    "no referenced effect sound-marker fixture found");

// Synthetic event clocks prove the boundary law independently of any one asset.
var eventModel = new M2Model
{
    Sequences = [new M2Sequence { AnimationId = 0, StartTimestamp = 1000,
        EndTimestamp = 2000, Flags = 0 }],
    Events = [new M2EventMarker { Identifier = "$SND", Data = 77,
        Times = [1250] }],
};
SpellEffectPlayback looping = SpellEffectPlaybackLaw.Resolve(eventModel, missile: false);
IReadOnlyList<SpellEffectSoundEvent> loopEvents =
    SpellEffectPlaybackLaw.CrossedSoundEvents(eventModel, looping, -1e-9, 2.3);
Check(loopEvents.Count == 3, "looped effect event did not repeat once per crossed pass");
Near(loopEvents[0].OccurrenceSeconds, .25, 1e-9, "first loop event time");
Near(loopEvents[1].OccurrenceSeconds, 1.25, 1e-9, "second loop event time");
Near(loopEvents[2].OccurrenceSeconds, 2.25, 1e-9, "third loop event time");
eventModel.Sequences[0].Flags = 1;
SpellEffectPlayback clamped = SpellEffectPlaybackLaw.Resolve(eventModel, missile: false);
Check(SpellEffectPlaybackLaw.CrossedSoundEvents(eventModel, clamped, -1e-9, 2.3).Count == 1,
    "clamped effect event refired");

// M2Animator is shared with units. Pin canonical locomotion and death so fixing
// effect playback cannot silently freeze every character after one stride.
const string humanPath = @"Character\Human\Male\HumanMale.m2";
M2Model human = M2Reader.Parse(mpq.ReadFile(humanPath) ??
    throw new InvalidOperationException($"missing {humanPath}")) ??
    throw new InvalidOperationException($"invalid {humanPath}");
var characterLoops = new Dictionary<int, bool> { [0] = true, [4] = true, [5] = true, [1] = false };
foreach ((int animation, bool expectedLoop) in characterLoops)
{
    int sequence = human.TryFindSequenceIndexByAnimationId(animation);
    Check(sequence >= 0, $"HumanMale missing canonical animation {animation}");
    Check(human.Sequences[sequence].IsLooping == expectedLoop,
        $"HumanMale authored loop law drift for animation {animation}");
}
TextWriter savedOutput = Console.Out;
M2Animator? humanAnimator;
try
{
    Console.SetOut(TextWriter.Null);
    humanAnimator = M2Animator.Build(human, characterLoops.Keys.Concat([11, 12]),
        includeStaticSequences: true);
}
finally { Console.SetOut(savedOutput); }
Check(humanAnimator is not null, "HumanMale did not produce an animator");
foreach ((int animation, bool expectedLoop) in characterLoops)
    Check(humanAnimator!.Find(animation) is { } clip && clip.Looping == expectedLoop,
        $"HumanMale runtime loop law drift for animation {animation}");

// HumanMale's turn clips key only 17 bones. They must ride over Stand so unkeyed shoulders
// do not fall back to the outstretched bind pose. This is the exact real-data regression for
// the stationary A/D arm flare.
M2Animator.Clip humanStand = humanAnimator!.Find(0) ??
    throw new InvalidOperationException("HumanMale Stand clip was not baked");
SpellAttachment.Point rightHand = SpellAttachment.Resolve(human, 1) ??
    throw new InvalidOperationException("HumanMale right-hand attachment is missing");
SpellAttachment.Point leftHand = SpellAttachment.Resolve(human, 2) ??
    throw new InvalidOperationException("HumanMale left-hand attachment is missing");
var standSkin = new Matrix4x4[human.Bones.Count];
humanAnimator.TurnBasePose = humanStand;
foreach (int animation in new[] { 11, 12 })
{
    M2Animator.Clip turn = humanAnimator.Find(animation) ??
        throw new InvalidOperationException($"HumanMale turn clip {animation} was not baked");
    for (int sample = 0; sample <= 10; sample++)
    {
        float phase = sample / 10f;
        float globalTime = humanStand.DurationSeconds * phase;
        humanAnimator.Evaluate(humanStand, globalTime, globalTime, standSkin);
        Vector3 standRight = Vector3.Transform(rightHand.Local, standSkin[rightHand.BoneIndex]);
        Vector3 standLeft = Vector3.Transform(leftHand.Local, standSkin[leftHand.BoneIndex]);
        float standSpan = Vector3.Distance(standRight, standLeft);

        humanAnimator.Evaluate(turn, turn.DurationSeconds * phase, globalTime, standSkin);
        Vector3 right = Vector3.Transform(rightHand.Local, standSkin[rightHand.BoneIndex]);
        Vector3 left = Vector3.Transform(leftHand.Local, standSkin[leftHand.BoneIndex]);
        Near(Vector3.Distance(right, left), standSpan, .01,
            $"HumanMale turn {animation} restored unkeyed arms to bind pose at phase {phase:F2}");
    }
}

const string oneShotPath = @"Spells\BattleShout_Cast_Base.m2";
M2Model oneShotModel = M2Reader.Parse(mpq.ReadFile(oneShotPath) ??
    throw new InvalidOperationException($"missing {oneShotPath}")) ??
    throw new InvalidOperationException($"invalid {oneShotPath}");
double oneShotSpan = SpellAttachment.SelfTerminatingSpan(oneShotModel);
var kit = new SpellVisualKitInfo(null, null,
    [new SpellVisualKitEffect(0x13, oneShotPath)], []);
SpellUnitPose Pose(ulong _) => new(true, new Vector3(10, 20, 30), 0,
    Matrix4x4.CreateTranslation(10, 20, 30), null, null);

// The bool compatibility overload must use the authored first-sequence span.
var lifetimeSource = new SpellEffectSource(mpq);
Check(lifetimeSource.SpawnKit(1, 100, kit, persistent: false, 0, "CAST") == 1,
    "one-shot fixture did not spawn");
lifetimeSource.Tick(oneShotSpan - .001, Pose);
Check(lifetimeSource.ActiveCount == 1, "one-shot died before authored sequence completion");
lifetimeSource.Tick(oneShotSpan + .001, Pose);
Check(lifetimeSource.ActiveCount == 0, "one-shot outlived authored sequence completion");

// A new cast hold replaces a different spell's hold, but aura-state ownership is separate.
var ownershipSource = new SpellEffectSource(mpq);
ownershipSource.SpawnKit(7, 200, kit, StageLife.Persistent, 0, "PRECAST_HOLD");
ownershipSource.SpawnKit(7, 201, kit, StageLife.Persistent, .1, "PRECAST_HOLD");
Check(ownershipSource.ActiveCount == 1, "different-spell cast hold was not replaced");
ownershipSource.SpawnKit(7, 300, kit, StageLife.AuraState, .2, "AURA_STATE");
ownershipSource.SpawnKit(7, 202, kit, StageLife.Persistent, .3, "CHANNEL_HOLD");
Check(ownershipSource.ActiveCount == 2, "cast hold replacement swept aura state");
ownershipSource.BeginCast(7);
Check(ownershipSource.ActiveCount == 1,
    "new cast without a visual did not release prior hold, or swept aura state");
ownershipSource.SpawnKit(7, 202, kit, StageLife.Persistent, .4, "PRECAST_HOLD");
ownershipSource.Reap(7, 999, StageLife.Persistent);
Check(ownershipSource.ActiveCount == 2, "spell-keyed reap removed another spell's hold");
ownershipSource.Reap(7, 202, StageLife.Persistent);
Check(ownershipSource.ActiveCount == 1, "matching spell hold reap did not release its owner");
ownershipSource.Reap(7, 300, StageLife.AuraState);
Check(ownershipSource.ActiveCount == 0, "aura-state reap did not remove its owner");

// Persistent area clocks stay monotonic; the selected track itself decides clamp versus wrap.
var areaSource = new SpellEffectSource(mpq);
Check(areaSource.SpawnAreaVisual(9, 400,
    new SpellAreaVisualInfo(oneShotPath, [], null), Vector3.Zero, 8, 0) == 1,
    "area clock fixture did not spawn");
double observedAreaAge;
SpellMeshDraw[] areaMeshes = areaSource.MeshInstances(oneShotSpan * 2 + .25, Pose).ToArray();
if (areaMeshes.Length > 0) observedAreaAge = areaMeshes[0].Age;
else observedAreaAge = areaSource.EmitterInstances(oneShotSpan * 2 + .25, Pose)
    .First().AnimationTime;
Near(observedAreaAge, oneShotSpan * 2 + .25, .0001,
    "persistent area instance clock rewound at clip boundary");

// Missile selection prefers InFlight 144 and every feed receives that exact slot.
const string missilePath = @"Spells\ArcaneShot_Missile.m2";
M2Model missileModel = M2Reader.Parse(mpq.ReadFile(missilePath) ??
    throw new InvalidOperationException($"missing {missilePath}")) ??
    throw new InvalidOperationException($"invalid {missilePath}");
SpellEffectPlayback missilePlayback = SpellEffectPlaybackLaw.Resolve(missileModel, missile: true);
Check(missilePlayback.AnimationId == SpellEffectPlaybackLaw.InFlightAnimationId,
    "Arcane Shot fixture lacks InFlight selection");
var missileSource = new SpellEffectSource(mpq);
missileSource.SpawnMissile(1, 500, missilePath, Vector3.Zero, Vector3.UnitX * 10, 0, 2);
SpellMeshDraw missileMesh = missileSource.MeshInstances(.1, Pose).First();
Check(missileMesh.SequenceIndex == missilePlayback.SequenceIndex,
    "missile mesh feed diverged from selected sequence slot");
var missileEmitter = missileSource.EmitterInstances(.1, Pose).First();
Check(missileEmitter.SequenceIndex == missilePlayback.SequenceIndex,
    "missile particle feed diverged from selected sequence slot");

// A real mounted model's event markers reach the runtime sound callback once.
SpellEffectPlayback soundPlayback = SpellEffectPlaybackLaw.Resolve(soundFixtureModel!, missile: false)
    with { Looping = false };
IReadOnlyList<SpellEffectSoundEvent> expectedSounds =
    SpellEffectPlaybackLaw.CrossedSoundEvents(soundFixtureModel!, soundPlayback,
        -1e-9, soundPlayback.SpanSeconds + .001);
Check(expectedSounds.Count > 0, "sound fixture has no event in its selected sequence");
var heard = new List<(uint Sound, ulong Unit, Vector3 Position)>();
var soundSource = new SpellEffectSource(mpq)
{
    AnimationSoundEvent = (sound, unit, position) => heard.Add((sound, unit, position)),
};
var soundKit = new SpellVisualKitInfo(null, null,
    [new SpellVisualKitEffect(0x13, soundFixturePath!)], []);
Check(soundSource.SpawnKit(42, 600, soundKit, StageLife.SelfTerminating,
    0, "EVENT_ONESHOT") == 1, "sound-event fixture did not spawn");
soundSource.Tick(soundPlayback.SpanSeconds + .001, Pose);
Check(heard.Select(x => x.Sound).SequenceEqual(expectedSounds.Select(x => x.SoundId)),
    "runtime sound callback diverged from crossed M2 markers");
Check(heard.All(x => x.Unit == 42 && x.Position == new Vector3(10, 20, 30)),
    "runtime effect sound lost its spatial owner");
soundSource.Tick(soundPlayback.SpanSeconds + .1, Pose);
Check(heard.Count == expectedSounds.Count, "one-shot effect sound refired after reap");

Console.WriteLine($"[anim-census] referenced={uses.Count} resolved={resolved} missing={missing} " +
    $"sequence-less={sequenceLess} multi={multi} global-seq={globalSequences}");
Console.WriteLine($"[anim-census] authored-loop-fixes={oldLoopMismatches} " +
    $"default-slot-mismatches={defaultSlotMismatches} missile-first-fallbacks={missileFallbacks}");
Console.WriteLine($"[anim-census] exact-rig-sequences={animatorSequences} " +
    $"sound-models={soundModels} sound-fixture={soundFixturePath} character-loop-probes=4");
Console.WriteLine($"[spell-animation-lifecycle-check] PASS ({checks:N0} checks)");
return 0;

DbcFile Read(string path) => mpq.ReadFile(path) is { } bytes && DbcFile.Parse(bytes) is { } dbc
    ? dbc : throw new InvalidOperationException($"missing/invalid {path}");
