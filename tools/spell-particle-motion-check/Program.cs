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
