using System.Numerics;
using System.Text;
using MSUIClient;
using MSUIClient.Formats;
using MSUIClient.World.Spells;
using MSUIClient.World.Units;

if (args.Length != 1)
{
    Console.Error.WriteLine("usage: spell-particle-motion-check <client-config.json>");
    return 2;
}

int checks = 0;
void Check(bool condition, string message)
{
    checks++;
    if (!condition) throw new InvalidOperationException(message);
}
void Near(float actual, float expected, float epsilon, string message)
    => Check(MathF.Abs(actual - expected) <= epsilon,
        $"{message}: expected {expected}, got {actual}");
void NearV(Vector3 actual, Vector3 expected, float epsilon, string message)
    => Check(Vector3.Distance(actual, expected) <= epsilon,
        $"{message}: expected {expected}, got {actual}");
float MatrixDelta(Matrix4x4 a, Matrix4x4 b)
{
    float[] av =
    [
        a.M11, a.M12, a.M13, a.M14, a.M21, a.M22, a.M23, a.M24,
        a.M31, a.M32, a.M33, a.M34, a.M41, a.M42, a.M43, a.M44,
    ];
    float[] bv =
    [
        b.M11, b.M12, b.M13, b.M14, b.M21, b.M22, b.M23, b.M24,
        b.M31, b.M32, b.M33, b.M34, b.M41, b.M42, b.M43, b.M44,
    ];
    float max = 0f;
    for (int i = 0; i < av.Length; i++) max = MathF.Max(max, MathF.Abs(av[i] - bv[i]));
    return max;
}

// Pure frame law: the complete joint scale and rotation survive draw and the inverse fold.
Near(SpellParticleFrameLaw.SimulationStep(1f / 60f), 1f / 60f, 1e-7f,
    "ordinary frame step changed");
Near(SpellParticleFrameLaw.SimulationStep(.25f), .1f, 1e-7f,
    "hitch frame did not clamp once at 100 ms");
Near(SpellParticleFrameLaw.SimulationStep(-1f), 0f, 0f, "negative frame step survived");
Near(SpellParticleFrameLaw.SimulationStep(float.NaN), 0f, 0f, "NaN frame step survived");

Matrix4x4 joint = Matrix4x4.CreateScale(2f, 3f, 4f) *
    Matrix4x4.CreateRotationZ(MathF.PI / 2f);
Matrix4x4 root = Matrix4x4.CreateTranslation(10f, 20f, 30f);
Matrix4x4 frame = SpellParticleFrameLaw.ComposeEmitterLinearFrame(joint, root);
Vector3 local = new(1f, 2f, 3f);
Vector3 worldVector = SpellParticleFrameLaw.DrawModelVector(local, frame);
NearV(worldVector, new Vector3(-6f, 2f, 12f), 1e-5f,
    "posed emitter frame lost scale or rotation");
NearV(SpellParticleFrameLaw.StoreModelVector(worldVector, frame), local, 1e-5f,
    "world-to-model motion fold was not inverse to draw");
NearV(SpellParticleFrameLaw.DrawModelPoint(local, new Vector3(7f, 8f, 9f), frame),
    new Vector3(1f, 10f, 21f), 1e-5f, "model-space origin composed twice or not at all");
NearV(SpellParticleFrameLaw.RebasedEmitterOrigin(new Vector3(5f, 6f, 7f),
        new Vector3(1f, 2f, 3f), new Vector3(10f, 20f, 30f), joint),
    new Vector3(-2f, 28f, 46f), 1e-5f, "joint-pivot emitter rebase drift");
Near(SpellParticleFrameLaw.XScale(frame), 2f, 1e-5f, "emitter X scale was discarded");

// Follow response: speed is measured with the already-clamped step. A riding storage frame gets
// the complement correction because the current owner transform supplies the full delta at draw.
Vector3 delta = new(.25f, 0f, 0f);
NearV(SpellParticleFrameLaw.FollowCorrectionWorld(true, delta, .1f,
        2.5f, .1f, 16.666667f, .9f, storedFrameRidesEmitter: true),
    new Vector3(-.225f, 0f, 0f), 1e-5f, "follow lower control point drift");
NearV(SpellParticleFrameLaw.FollowCorrectionWorld(true, new Vector3(10f, 0f, 0f), .1f,
        2.5f, .1f, 16.666667f, .9f, storedFrameRidesEmitter: true),
    Vector3.Zero, 1e-6f, "follow saturation drift");
float hitchStep = SpellParticleFrameLaw.SimulationStep(.25f);
Vector3 hitchFollow = SpellParticleFrameLaw.FollowCorrectionWorld(true, Vector3.UnitX, hitchStep,
    2.5f, .1f, 16.666667f, .9f, storedFrameRidesEmitter: true);
Vector3 rawFollow = SpellParticleFrameLaw.FollowCorrectionWorld(true, Vector3.UnitX, .25f,
    2.5f, .1f, 16.666667f, .9f, storedFrameRidesEmitter: true);
Check(Vector3.Distance(hitchFollow, rawFollow) > .1f,
    "follow hitch probe cannot distinguish clamped from raw dt");
NearV(SpellParticleFrameLaw.FollowCorrectionWorld(false, Vector3.UnitX, hitchStep,
        2.5f, .1f, 16.666667f, .9f, storedFrameRidesEmitter: true),
    Vector3.Zero, 0f, "unflagged emitter received follow correction");

Check(SpellParticleTrailLaw.DrawsHead(0) && !SpellParticleTrailLaw.DrawsTail(0) &&
      !SpellParticleTrailLaw.DrawsHead(1) && SpellParticleTrailLaw.DrawsTail(1) &&
      SpellParticleTrailLaw.DrawsHead(2) && SpellParticleTrailLaw.DrawsTail(2) &&
      SpellParticleTrailLaw.DrawsHead(3) && SpellParticleTrailLaw.DrawsTail(3),
    "particle head/tail mode routing drift");
Check(!SpellParticleTrailLaw.CullBackFaces,
    "particle renderer must remain two-sided; back-face culling erases projected tails");
SpellParticleTrailLaw.Quad streak = SpellParticleTrailLaw.TailQuad(
    new Vector3(10, 20, 30), Vector3.UnitZ * 2f, .25f,
    Vector3.UnitX, Vector3.UnitZ, 1.5f, .2f, clampToParticleAge: false);
Check(streak.Streak, "projected tail collapsed to a billboard");
NearV(streak.Tail, -Vector3.UnitZ * 3f, 1e-6f, "tail vector drift");
NearV(streak.Centre, new Vector3(10, 20, 28.5f), 1e-6f,
    "tail quad did not span head to tip");
Near(streak.AxisRight.Length(), .25f, 1e-6f, "tail width drift");
Vector3 headWinding = Vector3.Normalize(Vector3.Cross(Vector3.UnitX, Vector3.UnitZ));
Vector3 tailWinding = Vector3.Normalize(Vector3.Cross(streak.AxisRight, streak.AxisUp));
Check(Vector3.Dot(headWinding, tailWinding) < -.999f,
    "tail fixture no longer proves why two-sided particle rasterization is required");
SpellParticleTrailLaw.Quad young = SpellParticleTrailLaw.TailQuad(
    Vector3.Zero, Vector3.UnitZ * 2f, .25f, Vector3.UnitX, Vector3.UnitZ,
    1.5f, .2f, clampToParticleAge: true);
NearV(young.Tail, -Vector3.UnitZ * .4f, 1e-6f,
    "age-clamped tail used its full authored time");
SpellParticleTrailLaw.Quad viewParallel = SpellParticleTrailLaw.TailQuad(
    Vector3.Zero, Vector3.UnitY, .25f, Vector3.UnitX, Vector3.UnitZ,
    1f, 1f, clampToParticleAge: false);
Check(!viewParallel.Streak, "view-parallel tail did not use billboard fallback");

// Inherit response: strict >1/30 trigger, current-frame delta only, live gate, and held value.
float accumulator = 0f;
Vector3 held = new(99f, 0f, 0f);
SpellParticleFrameLaw.UpdateInheritedMotion(1f / 60f, Vector3.UnitX, 2f, true,
    ref accumulator, ref held);
Near(accumulator, 1f / 60f, 1e-7f, "inherit accumulator first half-step");
NearV(held, new Vector3(99f, 0f, 0f), 0f, "inherit value was not held before trigger");
SpellParticleFrameLaw.UpdateInheritedMotion(1f / 60f, Vector3.UnitX * 2f, 2f, true,
    ref accumulator, ref held);
Near(accumulator, 1f / 30f, 1e-7f, "inherit fired at equality instead of strict greater-than");
SpellParticleFrameLaw.UpdateInheritedMotion(1f / 60f, Vector3.UnitX * 3f, 2f, true,
    ref accumulator, ref held);
NearV(held, Vector3.UnitX * 4f, 1e-5f,
    "inherit did not sample only the trigger frame's delta");
Near(accumulator, 0f, 0f, "inherit accumulator did not reset after trigger");
SpellParticleFrameLaw.UpdateInheritedMotion(1f / 60f, Vector3.UnitY, 2f, true,
    ref accumulator, ref held);
NearV(held, Vector3.UnitX * 4f, 1e-6f, "inherit sample was not held between triggers");
accumulator = 1f / 30f;
SpellParticleFrameLaw.UpdateInheritedMotion(1f / 60f, Vector3.UnitZ, 3f, false,
    ref accumulator, ref held);
NearV(held, Vector3.Zero, 0f, "inherit live-particle gate drift");
accumulator = 0f;
SpellParticleFrameLaw.UpdateInheritedMotion(hitchStep, Vector3.UnitX * 9f, 3f, true,
    ref accumulator, ref held);
NearV(held, Vector3.UnitX * 9f, 1e-5f, "inherit hitch-step scaling drift");

ClientConfig config = ClientConfig.Load(args[0]);
using var mpq = new MpqMount(config.ClientDataPath);
HashSet<string> referenced = ReferencedSpellModels(mpq);
var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
foreach (string archive in Directory.GetFiles(config.ClientDataPath, "*.MPQ"))
{
    string supplier = Path.GetFileName(archive);
    byte[]? list = mpq.ReadFileFromSupplier("(listfile)", supplier)?.Data;
    if (list is null) continue;
    foreach (string raw in Encoding.UTF8.GetString(list).Split(['\r', '\n'],
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        string path = SpellVisualCatalog.ModelPath(raw);
        if (path.EndsWith(".m2", StringComparison.OrdinalIgnoreCase)) paths.Add(path);
    }
}

int parsed = 0, emitters = 0, modelSpace = 0, inherit = 0, follow = 0, special = 0;
int spellSpecial = 0, scaledSpecial = 0, scaledSpell = 0;
int referencedResolved = 0, referencedSpecial = 0, referencedModel = 0,
    referencedInherit = 0, referencedFollow = 0;
var specialModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
var spellSpecialModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
var referencedResolvedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

foreach (string path in paths.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
{
    byte[]? bytes = mpq.ReadFile(path);
    M2Model? model = bytes is null ? null : M2Reader.Parse(bytes);
    if (model is null) continue;
    parsed++;
    emitters += model.ParticleEmitters.Count;
    bool isSpell = path.StartsWith(@"Spells\", StringComparison.OrdinalIgnoreCase);
    bool isReferenced = referenced.Contains(path);
    if (isReferenced)
    {
        referencedResolved++;
        referencedResolvedPaths.Add(path);
    }
    for (int i = 0; i < model.ParticleEmitters.Count; i++)
    {
        M2ParticleEmitter emitter = model.ParticleEmitters[i];
        bool m = (emitter.Flags & 0x10) != 0;
        bool v = (emitter.Flags & 0x40) != 0;
        bool f = (emitter.Flags & 0x4000) != 0;
        if (m) modelSpace++;
        if (v) inherit++;
        if (f) follow++;
        if (!m && !v && !f) continue;
        special++;
        specialModels.Add(path);
        if (isSpell) { spellSpecial++; spellSpecialModels.Add(path); }
        if (isReferenced)
        {
            referencedSpecial++;
            if (m) referencedModel++;
            if (v) referencedInherit++;
            if (f) referencedFollow++;
        }
        bool scaledAncestor = false;
        int bone = emitter.Bone;
        var seen = new HashSet<int>();
        while (bone >= 0 && bone < model.Bones.Count && seen.Add(bone))
        {
            scaledAncestor |= model.Bones[bone].Scale.Keys.Any(key =>
                Vector3.DistanceSquared(key, Vector3.One) > 1e-6f);
            bone = model.Bones[bone].ParentBone;
        }
        if (scaledAncestor)
        {
            scaledSpecial++;
            if (isSpell) scaledSpell++;
        }
    }
}

Check(paths.Count == 9717 && parsed == 9654 && emitters == 7860,
    "mounted M2 corpus drift");
Check(modelSpace == 2391 && inherit == 124 && follow == 96 && special == 2550,
    "mounted special-motion flag census drift");
Check(spellSpecial == 613 && scaledSpecial == 115 && scaledSpell == 52,
    "spell/scaled special-motion census drift");
Check(specialModels.Count == 839 && spellSpecialModels.Count == 268,
    "special-motion model census drift");
Check(referenced.Count == 599 && referencedResolvedPaths.Count == 555,
    "referenced SpellVisual model census drift");
Check(referencedSpecial == 556 && referencedModel == 505 &&
    referencedInherit == 61 && referencedFollow == 20,
    "referenced special-motion emitter census drift");

M2ParticleEmitter arcane = Emitter(@"Spells\ArcaneShot_Missile.m2", 0);
Check(arcane.Flags == 0x4109, "Arcane Shot follow flags drift");
Near(arcane.FollowSpeed1, 2.5f, 1e-5f, "Arcane Shot follow speed 1");
Near(arcane.FollowScale1, .1f, 1e-5f, "Arcane Shot follow scale 1");
Near(arcane.FollowSpeed2, 16.666667f, .001f, "Arcane Shot follow speed 2");
Near(arcane.FollowScale2, .9f, 1e-5f, "Arcane Shot follow scale 2");
M2ParticleEmitter bloodlust = Emitter(@"Spells\Bloodlust_State_Hand.m2", 0);
Check((bloodlust.Flags & 0x50) == 0x50, "Bloodlust model+inherit fixture drift");
Near(bloodlust.InheritScale, 3f, 1e-5f, "Bloodlust inherit scale drift");
M2ParticleEmitter abolish = Emitter(@"Spells\AbolishMagic_Base.m2", 0);
Check((abolish.Flags & 0x40) != 0, "Abolish Magic inherit fixture drift");

M2Model blizzard = M2Reader.Parse(mpq.ReadFile(@"Spells\Blizzard_Impact_Base.m2") ??
    throw new InvalidOperationException("Blizzard fixture missing")) ??
    throw new InvalidOperationException("Blizzard fixture invalid");
Check(blizzard.ParticleEmitters.Count == 5 &&
      blizzard.ParticleEmitters.Count(e => e.HeadOrTail == 1) == 3 &&
      blizzard.ParticleEmitters.All(e => e.GeometryModel.Length == 0),
    "Blizzard head/tail fixture drift");
Check(blizzard.ParticleEmitters.Skip(1).Take(3).Select(e => e.TailTime)
    .SequenceEqual([1.6999999f, 1.2000002f, 1.05f]),
    "Blizzard authored tail times drift");
for (int i = 0; i < blizzard.ParticleEmitters.Count; i++)
{
    M2ParticleEmitter emitter = blizzard.ParticleEmitters[i];
    string texture = emitter.Texture < blizzard.Textures.Count
        ? blizzard.Textures[emitter.Texture].Filename : "(invalid)";
    emitter.SampleRamp(.25f, out Vector4 rgba25, out float size25);
    Console.WriteLine($"[motion-blizzard] e{i} head-tail={emitter.HeadOrTail} blend={emitter.BlendingType} " +
        $"texture={texture} geometry={emitter.GeometryModel} speed={emitter.EmissionSpeed:0.###} " +
        $"shape={emitter.Shape} cone={emitter.VerticalRange:0.###}/{emitter.HorizontalRange:0.###} " +
        $"area={emitter.EmissionAreaLength:0.###}/{emitter.EmissionAreaWidth:0.###} " +
        $"gravity={emitter.Gravity:0.###} tail={emitter.TailTime:0.###} life={emitter.Lifespan:0.###} " +
        $"rate={emitter.EmissionRate:0.###} rate-keys={string.Join('|', emitter.ScalarTracks[6].Keys)} " +
        $"atlas={emitter.TextureRows}x{emitter.TextureCols} " +
        $"tail-cells={string.Join('|', emitter.TailCellBegin)}->{string.Join('|', emitter.TailCellEnd)} " +
        $"size@25={size25:0.###} alpha@25={rgba25.W:0.###} flags=0x{emitter.Flags:X}");
}

var blizzardSource = new SpellEffectSource(mpq);
var blizzardVisual = new SpellAreaVisualInfo(null,
    [new SpellAreaEmitterInfo(0, @"Spells\Blizzard_Impact_Base.m2", 5f)], null);
Check(blizzardSource.SpawnAreaVisual(77, 10, blizzardVisual, Vector3.Zero, 0f, 100) == 1,
    "Blizzard trail fixture did not arm");
blizzardSource.Tick(100.21, _ => default);
Vector3 BlizzardEmitter(int emitter, double now)
{
    var instance = blizzardSource.EmitterInstances(now, _ => default)
        .Single(x => x.EmitterIndex == emitter);
    Check(instance.LocalOrigin.HasValue,
        $"Blizzard emitter {emitter} lost its posed production origin");
    return Vector3.Transform(instance.LocalOrigin!.Value, instance.Transform);
}
Vector3 BlizzardCentralVelocity(int emitter, double now)
{
    var instance = blizzardSource.EmitterInstances(now, _ => default)
        .Single(x => x.EmitterIndex == emitter);
    Check(instance.LocalFrame.HasValue,
        $"Blizzard emitter {emitter} lost its posed production frame");
    Vector3 direction = Vector3.TransformNormal(Vector3.UnitY,
        instance.LocalFrame!.Value * instance.Transform);
    return direction.LengthSquared() > 1e-8f ? Vector3.Normalize(direction) : Vector3.Zero;
}
Vector3 blizzardE1AtBirth = BlizzardEmitter(1, 100.21);
Vector3 blizzardE1Later = BlizzardEmitter(1, 100.51);
Vector3 blizzardE2AtBirth = BlizzardEmitter(2, 100.21);
Vector3 blizzardE2Later = BlizzardEmitter(2, 100.51);
Vector3 blizzardE1Velocity = BlizzardCentralVelocity(1, 100.21);
Vector3 blizzardE2Velocity = BlizzardCentralVelocity(2, 100.21);
Check(Vector3.Distance(blizzardE1AtBirth, blizzardE1Later) > 5f &&
      Vector3.Distance(blizzardE2AtBirth, blizzardE2Later) > 4f,
    "Blizzard production emitter feed lost its animated shard descent");
Console.WriteLine($"[motion-blizzard-source] e1 {blizzardE1AtBirth} -> {blizzardE1Later}; " +
    $"e2 {blizzardE2AtBirth} -> {blizzardE2Later}; " +
    $"central-velocity e1={blizzardE1Velocity} e2={blizzardE2Velocity}");

// Live source-level ward A/B. The omitted/default argument is the shipping A path and must be
// identical even when camera inputs are supplied. B changes only the evaluated carrier palette
// and therefore must respond to camera direction without changing the root attachment transform.
const string wardPath = @"Spells\FireWard_Impact_Chest.m2";
var wardSource = new SpellEffectSource(mpq);
var wardKit = new SpellVisualKitInfo(null, null,
    [new SpellVisualKitEffect(0x22, wardPath)], []);
Check(wardSource.SpawnKit(88, 543, wardKit, StageLife.SelfTerminating,
        200, "WARD_AB") == 1,
    "Fire Ward A/B fixture did not spawn");
SpellUnitPose WardPose(ulong _) => new(true, Vector3.Zero, 0,
    Matrix4x4.Identity, null, null);
var wardA = wardSource.EmitterInstances(200.2, WardPose)
    .OrderBy(e => e.EmitterIndex).ToArray();
var wardAWithCamera = wardSource.EmitterInstances(200.2, WardPose, false,
        new Vector3(10, -4, 7), Vector3.UnitX)
    .OrderBy(e => e.EmitterIndex).ToArray();
var wardBForward = wardSource.EmitterInstances(200.2, WardPose, true,
        new Vector3(10, -4, 7), Vector3.UnitY)
    .OrderBy(e => e.EmitterIndex).ToArray();
var wardBSide = wardSource.EmitterInstances(200.2, WardPose, true,
        new Vector3(10, -4, 7), Vector3.UnitX)
    .OrderBy(e => e.EmitterIndex).ToArray();
Check(wardA.Length == 4 && wardAWithCamera.Length == 4 &&
      wardBForward.Length == 4 && wardBSide.Length == 4,
    "Fire Ward A/B emitter fixture drift");
for (int i = 0; i < wardA.Length; i++)
{
    Check(wardA[i].LocalOrigin.HasValue && wardA[i].LocalFrame.HasValue &&
          wardAWithCamera[i].LocalOrigin.HasValue && wardAWithCamera[i].LocalFrame.HasValue,
        $"Fire Ward A emitter {i} lost its posed carrier");
    NearV(wardAWithCamera[i].LocalOrigin!.Value, wardA[i].LocalOrigin!.Value, 0f,
        $"Fire Ward A camera input moved emitter {i}");
    Check(MatrixDelta(wardAWithCamera[i].LocalFrame!.Value,
            wardA[i].LocalFrame!.Value) == 0f,
        $"Fire Ward A camera input rotated emitter {i}");
    Check(wardAWithCamera[i].Transform == wardA[i].Transform &&
          wardBForward[i].Transform == wardA[i].Transform &&
          wardBSide[i].Transform == wardA[i].Transform,
        $"Fire Ward A/B changed root attachment transform for emitter {i}");
}
Check(wardA.Zip(wardBForward).Any(pair =>
        Vector3.Distance(pair.First.LocalOrigin!.Value, pair.Second.LocalOrigin!.Value) > 1e-4f ||
        MatrixDelta(pair.First.LocalFrame!.Value, pair.Second.LocalFrame!.Value) > 1e-4f),
    "Fire Ward B did not rewrite any live particle carrier");
Check(wardBForward.Zip(wardBSide).Any(pair =>
        Vector3.Distance(pair.First.LocalOrigin!.Value, pair.Second.LocalOrigin!.Value) > 1e-4f ||
        MatrixDelta(pair.First.LocalFrame!.Value, pair.Second.LocalFrame!.Value) > 1e-4f),
    "Fire Ward B particle carriers did not respond to camera direction");
Console.WriteLine("[motion-ward-ab] A=no-op B=camera-responsive root-attachment=unchanged");

// The runtime source must expose the complete live joint TRS, not regress to origin+quaternion.
const string scaledPath = @"Spells\AbolishMagic_Base.m2";
var scaledSource = new SpellEffectSource(mpq);
var scaledKit = new SpellVisualKitInfo(null, null,
    [new SpellVisualKitEffect(0x13, scaledPath)], []);
Check(scaledSource.SpawnKit(1, 1, scaledKit, StageLife.Persistent, 0, "MOTION_FRAME") == 1,
    "scaled spell fixture did not spawn");
SpellUnitPose Pose(ulong _) => new(true, Vector3.Zero, 0, Matrix4x4.Identity, null, null);
float maxScaleDelta = 0f;
for (int sample = 0; sample <= 20; sample++)
{
    var instance = scaledSource.EmitterInstances(sample * .05, Pose)
        .First(e => e.EmitterIndex == 2);
    Check(instance.LocalFrame.HasValue, "posed spell emitter lost its live joint frame");
    Matrix4x4 localFrame = instance.LocalFrame!.Value;
    float sx = new Vector3(localFrame.M11, localFrame.M12, localFrame.M13).Length();
    float sy = new Vector3(localFrame.M21, localFrame.M22, localFrame.M23).Length();
    float sz = new Vector3(localFrame.M31, localFrame.M32, localFrame.M33).Length();
    maxScaleDelta = MathF.Max(maxScaleDelta,
        MathF.Max(MathF.Abs(sx - 1f), MathF.Max(MathF.Abs(sy - 1f), MathF.Abs(sz - 1f))));
}
Check(maxScaleDelta > .01f, "real scaled spell joint collapsed to quaternion-only data");

Console.WriteLine($"[motion-census] paths={paths.Count} parsed={parsed} emitters={emitters} " +
    $"model-space={modelSpace} inherit={inherit} follow={follow} special={special}");
Console.WriteLine($"[motion-census] special-models={specialModels.Count} " +
    $"spell-records={spellSpecial} spell-models={spellSpecialModels.Count} " +
    $"scaled-records={scaledSpecial} scaled-spell-records={scaledSpell}");
Console.WriteLine($"[motion-census] referenced={referenced.Count} resolved={referencedResolvedPaths.Count} " +
    $"special={referencedSpecial} model-space={referencedModel} " +
    $"inherit={referencedInherit} follow={referencedFollow}");
Console.WriteLine($"[spell-particle-motion-check] PASS ({checks:N0} checks)");
return 0;

M2ParticleEmitter Emitter(string path, int index)
{
    byte[] bytes = mpq.ReadFile(path) ?? throw new InvalidOperationException($"missing {path}");
    M2Model model = M2Reader.Parse(bytes) ?? throw new InvalidOperationException($"invalid {path}");
    Check(index < model.ParticleEmitters.Count, $"missing {path} emitter {index}");
    return model.ParticleEmitters[index];
}

static HashSet<string> ReferencedSpellModels(MpqMount mpq)
{
    DbcFile Read(string path) => mpq.ReadFile(path) is { } bytes && DbcFile.Parse(bytes) is { } dbc
        ? dbc : throw new InvalidOperationException($"missing/invalid {path}");
    DbcFile visuals = Read(@"DBFilesClient\SpellVisual.dbc");
    DbcFile kits = Read(@"DBFilesClient\SpellVisualKit.dbc");
    DbcFile names = Read(@"DBFilesClient\SpellVisualEffectName.dbc");
    var effectPaths = new Dictionary<uint, string>();
    for (int row = 0; row < names.RecordCount; row++)
    {
        uint id = names.GetUInt(row, 0);
        string path = SpellVisualCatalog.ModelPath(names.GetString(row, 2));
        if (id != 0 && path.Length > 0) effectPaths[id] = path;
    }
    var kitEffects = new Dictionary<uint, uint[]>();
    for (int row = 0; row < kits.RecordCount; row++)
        kitEffects[kits.GetUInt(row, 0)] = Enumerable.Range(0, 9)
            .Select(i => kits.GetUInt(row, 3 + i))
            .Where(id => id is not 0 and not uint.MaxValue).ToArray();
    var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    void AddEffect(uint id)
    {
        if (effectPaths.TryGetValue(id, out string? path)) result.Add(path);
    }
    void AddKit(uint id)
    {
        if (kitEffects.TryGetValue(id, out uint[]? effects))
            foreach (uint effect in effects) AddEffect(effect);
    }
    for (int row = 0; row < visuals.RecordCount; row++)
    {
        for (int field = 1; field <= 5; field++) AddKit(visuals.GetUInt(row, field));
        AddEffect(visuals.GetUInt(row, 7));
        if (visuals.GetUInt(row, 11) != 0) AddEffect(visuals.GetUInt(row, 12));
    }
    foreach (string area in SpellAreaVisualLaw.ClientShardModels) result.Add(area);
    return result;
}
