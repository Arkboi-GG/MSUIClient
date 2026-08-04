using System.Numerics;
using MSUIClient;
using MSUIClient.Formats;
using MSUIClient.World.Spells;
using MSUIClient.World.Units;

if (args.Length != 1)
{
    Console.Error.WriteLine("usage: spell-missile-pipeline-check <client-config.json>");
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
void NearV(Vector3 actual, Vector3 expected, float epsilon, string message)
    => Check(Vector3.Distance(actual, expected) <= epsilon,
        $"{message}: expected {expected}, got {actual}");

// Parsed Y-up missile model: +X flies forward, +Y remains world-up, +Z completes the frame.
Matrix4x4 straight = SpellMissileLaw.FlightTransform(new Vector3(10, 20, 30), Vector3.UnitX);
NearV(Vector3.TransformNormal(Vector3.UnitX, straight), Vector3.UnitX, 1e-6f,
    "missile authored +X did not face flight");
NearV(Vector3.TransformNormal(Vector3.UnitY, straight), Vector3.UnitZ, 1e-6f,
    "missile parsed +Y did not remain world-up");
NearV(Vector3.TransformNormal(Vector3.UnitZ, straight), -Vector3.UnitY, 1e-6f,
    "missile parsed +Z handedness drift");
NearV(Vector3.Transform(Vector3.Zero, straight), new Vector3(10, 20, 30), 1e-6f,
    "missile flight translation drift");
Matrix4x4 pitched = SpellMissileLaw.FlightTransform(Vector3.Zero,
    Vector3.Normalize(new Vector3(1, 2, 3)));
Near(Vector3.TransformNormal(Vector3.UnitX, pitched).Length(), 1, 1e-6,
    "pitched missile forward not unit");
Near(Vector3.TransformNormal(Vector3.UnitY, pitched).Length(), 1, 1e-6,
    "pitched missile up not unit");
Near(Vector3.Dot(Vector3.TransformNormal(Vector3.UnitX, pitched),
    Vector3.TransformNormal(Vector3.UnitY, pitched)), 0, 1e-6,
    "pitched missile frame not orthogonal");
Matrix4x4 vertical = SpellMissileLaw.FlightTransform(Vector3.Zero, Vector3.UnitZ);
Check(Matrix4x4.Decompose(vertical, out _, out Quaternion verticalRotation, out _) &&
    float.IsFinite(verticalRotation.X), "vertical missile frame degenerated");

Near(SpellMissileLaw.RemainingAtRelease(24f, 24f, .1), .9, 1e-6,
    "queued time was not subtracted from GO-time deadline");
Near(SpellMissileLaw.RemainingAtRelease(1f, 10f, .5), -.4, 1e-6,
    "past-deadline close missile did not remain negative");
SpellMissileLaw.Motion first = SpellMissileLaw.Advance(Vector3.Zero,
    Vector3.UnitX * 10, Vector3.UnitX, 1, .25);
NearV(first.Position, Vector3.UnitX * 2.5f, 1e-6f,
    "missile mover incorrectly applied particle 100 ms clamp");
Near(first.RemainingSeconds, .75, 1e-6, "missile remaining time after hitch");
SpellMissileLaw.Motion homing = SpellMissileLaw.Advance(first.Position,
    new Vector3(20, 10, 0), first.Direction, first.RemainingSeconds, .25);
NearV(homing.Position, first.Position + (new Vector3(20, 10, 0) - first.Position) / 3f,
    1e-5f, "live target movement did not bend arrive-on-time path");
SpellMissileLaw.Motion arrival = SpellMissileLaw.Advance(homing.Position,
    new Vector3(20, 10, 0), homing.Direction, homing.RemainingSeconds,
    homing.RemainingSeconds);
Check(arrival.Arrived, "missile did not arrive on its fixed deadline");
NearV(arrival.Position, homing.Position, 0f, "arrival visually snapped before handoff");

var releaseModel = new M2Model
{
    Sequences = [new M2Sequence { AnimationId = 42, StartTimestamp = 1000,
        EndTimestamp = 2000 }],
    Events =
    [
        new M2EventMarker { Identifier = "$CSL", Bone = 2,
            Position = new Vector3(1, 2, 3), Times = [1250] },
        new M2EventMarker { Identifier = "$CSR", Bone = 3,
            Position = new Vector3(4, 5, 6), Times = [2500] },
    ],
};
SpellMissileLaw.Release fired = SpellMissileLaw.ResolveRelease(releaseModel, 42);
Near(fired.DelaySeconds, .25, 1e-6, "release marker keyframe delay");
Check(fired.UsesMarker && fired.Identifier == "$CSL" && fired.Bone == 2,
    "fired release marker identity drift");
Check(!fired.StrictlyAfterDelay, "authored release event inherited timeout strictness");
releaseModel.Events[0].Times = [2500];
SpellMissileLaw.Release finish = SpellMissileLaw.ResolveRelease(releaseModel, 42);
Near(finish.DelaySeconds, 1, 1e-6, "markerless release did not wait for animation finish");
Check(finish.Identifier == "$CSL", "animation-finish marker cascade drift");
SpellMissileLaw.Release immediate = SpellMissileLaw.ResolveRelease(releaseModel, null);
Near(immediate.DelaySeconds, 0, 0, "no-animation missile did not launch immediately");
Check(immediate.Identifier == "$CSL", "immediate launch marker cascade drift");
Near(SpellMissileLaw.ResolveRelease(releaseModel, 999).DelaySeconds,
    SpellMissileLaw.MissingAnimationWaitSeconds, 1e-6,
    "never-started animation backstop drift");
Check(SpellMissileLaw.ResolveRelease(releaseModel, 999).StrictlyAfterDelay,
    "never-started animation lost its strict greater-than edge");
SpellMissileLaw.Release modelLess = SpellMissileLaw.ResolveRelease(null, 42);
Near(modelLess.DelaySeconds, SpellMissileLaw.MissingAnimationWaitSeconds, 1e-6,
    "model-less requested animation skipped the never-started backstop");
Check(!modelLess.UsesMarker, "model-less caster invented a release marker");
Near(SpellMissileLaw.ResolveRelease(null, null).DelaySeconds, 0, 0,
    "model-less no-animation cast did not launch immediately");

// An out-of-table SpellVisual ordinal is no tag, not attachment id 0. Its first lookup must
// therefore be the normal 0x0F/0x13 destination fallback tail.
var fallbackModel = new M2Model
{
    Attachments =
    [
        new M2Attachment { Id = 0, BoneIndex = 0, Position = Vector3.UnitX * 100 },
        new M2Attachment { Id = 0x0F, BoneIndex = 0, Position = Vector3.UnitZ * 2 },
        new M2Attachment { Id = 0x13, BoneIndex = 0, Position = Vector3.UnitY * 3 },
    ],
};
SpellAttachment.Point? noTag = SpellAttachment.Resolve(fallbackModel,
    SpellVisualCatalog.NoMissileAttachment);
Check(noTag is { ResolvedId: 0x0F, WasFallback: true },
    "no-tag missile destination incorrectly tried attachment id 0");
Check(SpellAttachment.Resolve(fallbackModel, 0) is { ResolvedId: 0, WasFallback: false },
    "synthetic attachment-id-zero control fixture drift");

ClientConfig config = ClientConfig.Load(args[0]);
using var mpq = new MpqMount(config.ClientDataPath);
SpellCatalog spells = SpellCatalog.Load(mpq) ?? throw new InvalidOperationException("Spell catalog missing");
SpellVisualCatalog visuals = SpellVisualCatalog.Load(mpq) ??
    throw new InvalidOperationException("SpellVisual catalog missing");
DbcFile rawVisuals = DbcFile.Parse(mpq.ReadFile(@"DBFilesClient\SpellVisual.dbc") ?? []) ??
    throw new InvalidOperationException("Raw SpellVisual table missing");
int outOfTableOrdinals = 0;
for (int row = 0; row < rawVisuals.RecordCount; row++)
{
    uint ordinal = rawVisuals.GetUInt(row, 9);
    if (ordinal < SpellVisualCatalog.MissileAttachTable.Length) continue;
    outOfTableOrdinals++;
    uint visualId = rawVisuals.GetUInt(row, 0);
    Check(visuals.TryGetStages(visualId, out SpellVisualStages mapped) &&
        mapped.MissileAttachment == SpellVisualCatalog.NoMissileAttachment,
        $"out-of-table ordinal {ordinal} on visual {visualId} did not preserve no-tag state");
}

int speedSpells = 0, withVisual = 0, withModel = 0, ammoFallback = 0;
int withoutVisual = 0;
int resolvedModels = 0, particleModels = 0, ribbonModels = 0, inFlightModels = 0;
int modelEmitters = 0, followEmitters = 0, inheritEmitters = 0;
var visualIds = new HashSet<uint>();
var modelPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
var destinationTags = new Dictionary<ushort, int>();

foreach (SpellInfo spell in spells.Spells.Where(s => s.Speed > 0f))
{
    speedSpells++;
    if (!visuals.TryGetStages(spell.VisualId, out SpellVisualStages stages))
    { withoutVisual++; ammoFallback++; continue; }
    withVisual++;
    visualIds.Add(spell.VisualId);
    destinationTags[stages.MissileAttachment] =
        destinationTags.GetValueOrDefault(stages.MissileAttachment) + 1;
    string? path = visuals.MissilePath(stages);
    if (path is null) { ammoFallback++; continue; }
    withModel++;
    modelPaths.Add(path);
}

var unresolvedPaths = new List<string>();
foreach (string path in modelPaths.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
{
    byte[]? bytes = mpq.ReadFile(path);
    M2Model? model = bytes is null ? null : M2Reader.Parse(bytes);
    if (model is null) { unresolvedPaths.Add(path); continue; }
    resolvedModels++;
    if (model.ParticleEmitters.Count > 0) particleModels++;
    if (model.RibbonEmitters.Count > 0) ribbonModels++;
    if (model.TryFindSequenceIndexByAnimationId(SpellEffectPlaybackLaw.InFlightAnimationId) >= 0)
        inFlightModels++;
    foreach (M2ParticleEmitter emitter in model.ParticleEmitters)
    {
        modelEmitters++;
        if ((emitter.Flags & 0x4000) != 0) followEmitters++;
        if ((emitter.Flags & 0x40) != 0) inheritEmitters++;
    }
}

Check(speedSpells == 981 && withVisual == 824 && withoutVisual == 157,
    "mounted missile spell/visual census drift");
Check(withModel == 741 && ammoFallback == 240 && visualIds.Count == 227,
    "mounted missile model/ammo census drift");
Check(modelPaths.Count == 64 && resolvedModels == 63 &&
    unresolvedPaths.SequenceEqual([@"Particles\FrostBolt_Missle.m2"],
        StringComparer.OrdinalIgnoreCase),
    "mounted distinct missile asset resolution drift: " + string.Join(',', unresolvedPaths));
Check(particleModels == 45 && ribbonModels == 35 && inFlightModels == 25,
    "mounted missile content/animation census drift");
Check(modelEmitters == 169 && followEmitters == 8 && inheritEmitters == 5,
    "mounted missile emitter census drift");
Check(destinationTags.OrderBy(x => x.Key).SequenceEqual(new Dictionary<ushort, int>
    {
        [0x13] = 53, [0x14] = 20, [0x15] = 1, [0x16] = 2, [0x22] = 748,
    }.OrderBy(x => x.Key)), "mounted missile destination-tag census drift");

// Real fixture: Arcane Shot is both a missile and the canonical follow-response asset.
const string arcanePath = @"Spells\ArcaneShot_Missile.m2";
Check(modelPaths.Contains(arcanePath), "Arcane Shot missing from missile corpus");
M2Model arcane = M2Reader.Parse(mpq.ReadFile(arcanePath) ??
    throw new InvalidOperationException("Arcane Shot missing")) ??
    throw new InvalidOperationException("Arcane Shot invalid");
Check(arcane.ParticleEmitters.Count > 0 &&
    (arcane.ParticleEmitters[0].Flags & 0x4000) != 0,
    "Arcane Shot follow fixture drift");

// Runtime source: free-missile ordinary particles retain world history and never inherit
// host-attachment rotation. The continuous ribbon separately follows the live missile head.
var source = new SpellEffectSource(mpq);
source.SpawnMissile(1, 1, arcanePath, Vector3.Zero, Vector3.UnitX * 10, 0, 1);
SpellUnitPose MissingPose(ulong _) => SpellUnitPose.Missing;
var emitterFeed = source.EmitterInstances(0, MissingPose).First();
Check(!emitterFeed.RootCarriesCloud,
    "free missile particle feed stopped retaining world history");
Check(!emitterFeed.HostAttachmentRotatesCloud,
    "free missile particle feed inherited host attachment rotation");
SpellMeshDraw atStart = source.MeshInstances(0, MissingPose).First();
NearV(Vector3.TransformNormal(Vector3.UnitX, atStart.Transform), Vector3.UnitX, 1e-6f,
    "runtime missile mesh forward axis drift");
NearV(Vector3.TransformNormal(Vector3.UnitY, atStart.Transform), Vector3.UnitZ, 1e-6f,
    "runtime missile mesh up axis drift");
source.Tick(.25, MissingPose);
SpellMeshDraw afterHitch = source.MeshInstances(.25, MissingPose).First();
NearV(new Vector3(afterHitch.Transform.M41, afterHitch.Transform.M42, afterHitch.Transform.M43),
    Vector3.UnitX * 2.5f, 1e-5f, "runtime missile hitch arrived late");
source.Tick(1, MissingPose);
Check(source.ActiveCount == 0, "runtime missile missed its deadline");

// Runtime orchestration: launch, carry the raw elapsed interval, stop flight, then hand impact
// off without snapping. This is deliberately model-less so the timing contract is isolated.
var runtime = new SpellEffectSource(mpq);
SpellUnitPose RuntimePose(ulong unit) => unit switch
{
    10 => new SpellUnitPose(true, Vector3.Zero, 0, Matrix4x4.Identity, null, null),
    20 => new SpellUnitPose(true, Vector3.UnitX * 10, 0,
        Matrix4x4.CreateTranslation(Vector3.UnitX * 10), null, null),
    _ => SpellUnitPose.Missing,
};
int launches = 0, ends = 0, impacts = 0;
runtime.SpawnMissile(10, 2, null, 20, SpellVisualCatalog.NoMissileAttachment,
    10, 0, false, 0, null, RuntimePose,
    (_, _, missed, _) => { Check(!missed, "runtime hit handoff became a miss"); impacts++; },
    launched: () => launches++, ended: () => ends++);
runtime.Tick(0, RuntimePose);
Check(launches == 1 && ends == 0 && impacts == 0,
    "runtime launch event ordering drift");
runtime.Tick(.25, RuntimePose);
NearV(runtime.Snapshot(2, .25, RuntimePose).Single().Position,
    Vector3.UnitX * 2.5f, 1e-5f, "runtime live-target mover applied wrong elapsed time");
runtime.Tick(1, RuntimePose);
Check(runtime.ActiveCount == 0 && launches == 1 && ends == 1 && impacts == 1,
    "runtime end/impact handoff ordering drift");

// If the cast wait consumes the fixed GO-time deadline, Benilla hands impact off immediately:
// it never creates a visible flight and therefore never starts/stops the flight loop.
var expired = new SpellEffectSource(mpq);
int expiredLaunches = 0, expiredEnds = 0, expiredImpacts = 0;
SpellUnitPose ExpiredPose(ulong unit) => unit switch
{
    10 => new SpellUnitPose(true, Vector3.Zero, 0, Matrix4x4.Identity, releaseModel, null),
    20 => new SpellUnitPose(true, Vector3.UnitX, 0,
        Matrix4x4.CreateTranslation(Vector3.UnitX), null, null),
    _ => SpellUnitPose.Missing,
};
expired.SpawnMissile(10, 3, null, 20, SpellVisualCatalog.NoMissileAttachment,
    10, 0, false, 0, 42, ExpiredPose,
    (_, _, _, _) => expiredImpacts++, launched: () => expiredLaunches++,
    ended: () => expiredEnds++);
expired.Tick(.999, ExpiredPose);
Check(expired.ActiveCount == 1 && expiredImpacts == 0,
    "markerless missile released before animation finish");
expired.Tick(1, ExpiredPose);
Check(expired.ActiveCount == 0 && expiredImpacts == 1 && expiredLaunches == 0 &&
    expiredEnds == 0, "past-deadline release created a visible/audio flight");

var neverStarted = new SpellEffectSource(mpq);
int neverStartedImpacts = 0;
neverStarted.SpawnMissile(10, 4, null, 20, SpellVisualCatalog.NoMissileAttachment,
    10, 0, false, 0, 999, ExpiredPose,
    (_, _, _, _) => neverStartedImpacts++);
neverStarted.Tick(SpellMissileLaw.MissingAnimationWaitSeconds, ExpiredPose);
Check(neverStarted.ActiveCount == 1 && neverStartedImpacts == 0,
    "never-started animation backstop fired at equality instead of strictly after it");
neverStarted.Tick(SpellMissileLaw.MissingAnimationWaitSeconds + .001, ExpiredPose);
Check(neverStarted.ActiveCount == 0 && neverStartedImpacts == 1,
    "never-started animation did not flush after the 250 ms backstop");

var lateFrame = new SpellEffectSource(mpq);
lateFrame.SpawnMissile(10, 5, arcanePath, 20, SpellVisualCatalog.NoMissileAttachment,
    1, 0, false, 0, 999, ExpiredPose, (_, _, _, _) => { });
lateFrame.Tick(.3, ExpiredPose);
var lateFeed = lateFrame.EmitterInstances(.3, ExpiredPose).First();
Near(lateFeed.AnimationTime, 0, 0,
    "missile InFlight clock started at scheduled threshold instead of actual launch");
Near(lateFrame.EmitterInstances(.4, ExpiredPose).First().AnimationTime, .1, 1e-6,
    "missile InFlight clock did not advance from actual launch");

Console.WriteLine($"[missile-census] speed-spells={speedSpells} visuals={withVisual} " +
    $"distinct-visuals={visualIds.Count} model-spells={withModel} ammo-fallback={ammoFallback}");
Console.WriteLine($"[missile-census] model-paths={modelPaths.Count} resolved={resolvedModels} " +
    $"particle-models={particleModels} ribbon-models={ribbonModels} inflight={inFlightModels} " +
    $"unresolved={string.Join(',', unresolvedPaths)}");
Console.WriteLine($"[missile-census] emitters={modelEmitters} follow={followEmitters} " +
    $"inherit={inheritEmitters} out-of-table-ordinals={outOfTableOrdinals} destination-tags=" +
    string.Join(',', destinationTags.OrderBy(x => x.Key).Select(x => $"0x{x.Key:X}:{x.Value}")));
Console.WriteLine($"[spell-missile-pipeline-check] PASS ({checks:N0} checks)");
return 0;
