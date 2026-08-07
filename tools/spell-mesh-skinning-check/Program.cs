using System.Numerics;
using System.Text;
using MSUIClient;
using MSUIClient.Formats;
using MSUIClient.World.Units;

if (args.Length != 1)
{
    Console.Error.WriteLine("usage: spell-mesh-skinning-check <client-config.json>");
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
void NearM(Matrix4x4 actual, Matrix4x4 expected, float epsilon, string message)
{
    float[] a = MatrixValues(actual), e = MatrixValues(expected);
    for (int i = 0; i < 16; i++) Near(a[i], e[i], epsilon, $"{message} component {i}");
}

// Weight normalization and explicit negative policies use the production VBO resolver.
M2Vertex fourVertex = Vertex([64, 64, 64, 64], [0, 1, 2, 3]);
SpellMeshSkinningLaw.VertexSkin four = SpellMeshSkinningLaw.Resolve(fourVertex);
Near(four.Weights.X + four.Weights.Y + four.Weights.Z + four.Weights.W, 1f, 1e-7f,
    "four byte weights did not normalize");
Near(four.Indices.W, 3f, 0f, "fourth global bone index was not preserved");
M2Vertex oddTotal = Vertex([1, 2, 3, 4], [3, 2, 1, 0]);
SpellMeshSkinningLaw.VertexSkin odd = SpellMeshSkinningLaw.Resolve(oddTotal);
Near(odd.Weights.X, .1f, 1e-7f, "non-255 total normalized against 255 instead of authored sum");
Near(odd.Weights.W, .4f, 1e-7f, "fourth non-255 weight drift");
SpellMeshSkinningLaw.VertexSkin zero = SpellMeshSkinningLaw.Resolve(Vertex([0, 0, 0, 0],
    [91, 92, 93, 94]));
Check(zero.Weights == new Vector4(1, 0, 0, 0) && zero.Indices == Vector4.Zero,
    "zero-total vertex did not bind fully to bone zero");

var fourPalette = new[]
{
    Matrix4x4.CreateTranslation(1, 0, 0), Matrix4x4.CreateTranslation(2, 0, 0),
    Matrix4x4.CreateTranslation(4, 0, 0), Matrix4x4.CreateTranslation(8, 0, 0),
};
NearV(SpellMeshSkinningLaw.SkinPoint(Vector3.Zero, fourPalette, four),
    new Vector3(3.75f, 0, 0), 1e-6f, "four-weight position sum");
float[] removedExpected = [14f / 3f, 13f / 3f, 11f / 3f, 7f / 3f];
for (int removed = 0; removed < 4; removed++)
{
    Vector4 weights = four.Weights;
    SetComponent(ref weights, removed, 0f);
    Vector3 result = SpellMeshSkinningLaw.SkinPoint(Vector3.Zero, fourPalette,
        new SpellMeshSkinningLaw.VertexSkin(weights, four.Indices));
    Near(result.X, removedExpected[removed], 1e-6f,
        $"removing influence {removed} did not change output by its known contribution");
}

// Invalid positive-weight indices are not partly clamped on the CPU. The exact shader policy is to
// reject them and renormalize the surviving valid influences; duplicates naturally add together.
SpellMeshSkinningLaw.VertexSkin invalid = SpellMeshSkinningLaw.Resolve(
    Vertex([128, 127, 0, 0], [0, 250, 0, 0]));
Check(invalid.Indices.Y == 250f, "out-of-range byte index was silently rebound on upload");
NearV(SpellMeshSkinningLaw.SkinPoint(Vector3.Zero, fourPalette, invalid),
    new Vector3(1, 0, 0), 1e-6f, "invalid influence was not rejected and renormalized");
SpellMeshSkinningLaw.VertexSkin duplicate = SpellMeshSkinningLaw.Resolve(
    Vertex([64, 64, 64, 64], [0, 1, 1, 2]));
NearV(SpellMeshSkinningLaw.SkinPoint(Vector3.Zero, fourPalette, duplicate),
    new Vector3(2.25f, 0, 0), 1e-6f, "duplicate bone influences did not add independently");

// Nonzero pivot rotation: T(-p) * R * T(p) in row-vector order.
Vector3 pivot = new(3, 4, 5);
Matrix4x4 pivotSkin = Matrix4x4.CreateTranslation(-pivot) *
    Matrix4x4.CreateRotationZ(MathF.PI / 2f) * Matrix4x4.CreateTranslation(pivot);
NearV(SpellMeshSkinningLaw.SkinPoint(pivot + Vector3.UnitX, [pivotSkin],
        SpellMeshSkinningLaw.Resolve(Vertex([255, 0, 0, 0], [0, 0, 0, 0]))),
    pivot + Vector3.UnitY, 1e-5f, "nonzero pivot rotated around model origin");

// Production animator bind, hierarchy, and non-uniform scale composition.
M2Model synthetic = new();
synthetic.Bones.Add(new M2Bone { ParentBone = -1, Pivot = new Vector3(2, 3, 4) });
synthetic.Bones.Add(new M2Bone { ParentBone = 0, Pivot = new Vector3(5, 3, 4) });
M2Animator syntheticAnimator = BuildQuiet(synthetic);
var bind = new Matrix4x4[2];
syntheticAnimator.Evaluate(null, 0, 0, bind);
NearM(bind[0], Matrix4x4.Identity, 1e-6f, "root nonzero-pivot bind pose");
NearM(bind[1], Matrix4x4.Identity, 1e-6f, "child nonzero-pivot bind pose");

var channels = new M2Animator.BoneChannels[2];
channels[0] = Channels(rotation: Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f),
    scale: new Vector3(2, 3, 4));
channels[1] = Channels(translation: new Vector3(0, 2, 0));
var clip = new M2Animator.Clip
{
    SequenceIndex = 0, AnimationId = 0, DurationSeconds = 1, Looping = false,
    Bones = channels, AnimatedBones = 2,
};
var posed = new Matrix4x4[2];
syntheticAnimator.Evaluate(clip, 0, 0, posed);
Matrix4x4 expectedRootGlobal = Matrix4x4.CreateScale(2, 3, 4) *
    Matrix4x4.CreateRotationZ(MathF.PI / 2f) * Matrix4x4.CreateTranslation(2, 3, 4);
Matrix4x4 expectedChildGlobal = Matrix4x4.CreateTranslation(3, 2, 0) * expectedRootGlobal;
NearM(posed[0], Matrix4x4.CreateTranslation(-synthetic.Bones[0].Pivot) * expectedRootGlobal,
    1e-5f, "root scale/rotation/pivot composition");
NearM(posed[1], Matrix4x4.CreateTranslation(-synthetic.Bones[1].Pivot) * expectedChildGlobal,
    1e-5f, "parent-child transform composed other than once");

// Benilla/Bevy policy: inverse-transpose of the blended skin+root matrix, never position
// skinning's forward 3x3. The adversarial scales make the two answers visibly different.
Matrix4x4[] normalPalette =
[
    Matrix4x4.CreateScale(2, 1, 1) * Matrix4x4.CreateRotationZ(.25f),
    Matrix4x4.CreateScale(1, 3, 1) * Matrix4x4.CreateRotationX(-.35f),
];
SpellMeshSkinningLaw.VertexSkin normalSkin = SpellMeshSkinningLaw.Resolve(
    Vertex([128, 127, 0, 0], [0, 1, 0, 0]));
Matrix4x4 normalRoot = Matrix4x4.CreateScale(.5f, 2f, 4f) * Matrix4x4.CreateRotationY(.4f);
Vector3 bindNormal = Vector3.Normalize(new Vector3(1, 2, 3));
Check(SpellMeshSkinningLaw.TryBlendSkin(normalPalette, normalSkin, out Matrix4x4 normalBlend),
    "normal skin did not blend");
Matrix4x4 normalWorld = normalBlend * normalRoot;
Check(Matrix4x4.Invert(normalWorld, out Matrix4x4 normalInverse),
    "normal fixture unexpectedly singular");
Vector3 expectedNormal = Vector3.Normalize(Vector3.TransformNormal(bindNormal,
    Matrix4x4.Transpose(normalInverse)));
Vector3 actualNormal = SpellMeshSkinningLaw.SkinNormal(bindNormal, normalPalette,
    normalSkin, normalRoot);
NearV(actualNormal, expectedNormal, 1e-5f, "weighted non-uniform normal inverse-transpose");
Vector3 wrongForwardNormal = Vector3.Normalize(Vector3.TransformNormal(bindNormal, normalWorld));
Check(Vector3.Distance(actualNormal, wrongForwardNormal) > .25f,
    "normal fixture cannot distinguish inverse-transpose from position transform");
Vector3 translatedNormal = SpellMeshSkinningLaw.SkinNormal(bindNormal,
    normalPalette.Select(m => m * Matrix4x4.CreateTranslation(100, -50, 25)).ToArray(),
    normalSkin, normalRoot * Matrix4x4.CreateTranslation(-8, 9, 10));
NearV(translatedNormal, actualNormal, 1e-5f, "translation leaked into skinned normal");

// Billboard palette rewrite: parent faces the camera; its ordinary child keeps exactly the authored
// local relationship. A mixed-weight vertex consumes both rewritten palette entries independently.
M2Model billboardModel = new();
billboardModel.Bones.Add(new M2Bone { ParentBone = -1, Pivot = new Vector3(2, 0, 0), Flags = 0x08u });
billboardModel.Bones.Add(new M2Bone { ParentBone = 0, Pivot = new Vector3(5, 1, 0) });
var billboardSkin = new[] { Matrix4x4.Identity, Matrix4x4.Identity };
var disabledBillboardSkin = new[]
{
    Matrix4x4.CreateRotationX(.31f) * Matrix4x4.CreateTranslation(1, 2, 3),
    Matrix4x4.CreateScale(1.2f, .8f, 1.1f) * Matrix4x4.CreateTranslation(4, 5, 6),
};
Matrix4x4[] disabledBillboardExpected = disabledBillboardSkin.ToArray();
SpellMeshSkinningLaw.ApplyBillboardBonesIfEnabled(false, billboardModel,
    Matrix4x4.CreateTranslation(20, -4, 7), new Vector3(10, -10, 8),
    Vector3.UnitY, 2, disabledBillboardSkin);
for (int i = 0; i < disabledBillboardSkin.Length; i++)
    NearM(disabledBillboardSkin[i], disabledBillboardExpected[i], 0f,
        $"disabled billboard A/B gate changed palette bone {i}");

M2Model nonBillboardModel = new();
nonBillboardModel.Bones.Add(new M2Bone { ParentBone = -1, Pivot = new Vector3(2, 0, 0) });
nonBillboardModel.Bones.Add(new M2Bone { ParentBone = 0, Pivot = new Vector3(5, 1, 0) });
var nonBillboardSkin = new[]
{
    Matrix4x4.CreateRotationY(.2f),
    Matrix4x4.CreateRotationZ(-.4f) * Matrix4x4.CreateTranslation(1, 2, 3),
};
Matrix4x4[] nonBillboardExpected = nonBillboardSkin.ToArray();
SpellMeshSkinningLaw.ApplyBillboardBonesIfEnabled(true, nonBillboardModel,
    Matrix4x4.Identity, new Vector3(10, -10, 8), Vector3.UnitY, 2,
    nonBillboardSkin);
for (int i = 0; i < nonBillboardSkin.Length; i++)
    NearM(nonBillboardSkin[i], nonBillboardExpected[i], 0f,
        $"enabled A/B gate changed non-billboard control bone {i}");

Matrix4x4 oldParentGlobal = Matrix4x4.CreateTranslation(billboardModel.Bones[0].Pivot);
Matrix4x4 oldChildGlobal = Matrix4x4.CreateTranslation(billboardModel.Bones[1].Pivot);
Check(Matrix4x4.Invert(oldParentGlobal, out Matrix4x4 oldParentInverse),
    "bind billboard parent is singular");
Matrix4x4 oldLocal = oldChildGlobal * oldParentInverse;
SpellMeshSkinningLaw.ApplyBillboardBones(billboardModel, Matrix4x4.Identity,
    new Vector3(10, -10, 8), Vector3.UnitY, 2, billboardSkin);
Matrix4x4 newParentGlobal = Matrix4x4.CreateTranslation(billboardModel.Bones[0].Pivot) * billboardSkin[0];
Matrix4x4 newChildGlobal = Matrix4x4.CreateTranslation(billboardModel.Bones[1].Pivot) * billboardSkin[1];
Check(Matrix4x4.Invert(newParentGlobal, out Matrix4x4 newParentInverse),
    "rewritten billboard parent is singular");
NearM(newChildGlobal * newParentInverse, oldLocal, 1e-5f,
    "billboard child was not propagated from the rewritten parent");
Vector3 mixedBillboard = SpellMeshSkinningLaw.SkinPoint(new Vector3(6, 2, 0), billboardSkin,
    SpellMeshSkinningLaw.Resolve(Vertex([128, 127, 0, 0], [0, 1, 0, 0])));
Check(Vector3.Distance(mixedBillboard, new Vector3(6, 2, 0)) > .1f,
    "mixed billboard/child weights did not consume rewritten palette");

// Root is appended once after skinning, and camera subtraction is exactly one translation.
Vector3 localPoint = new(1, 2, 3), camera = new(1000, -500, 250);
Matrix4x4 root = Matrix4x4.CreateScale(2, 3, 4) * Matrix4x4.CreateRotationZ(.2f) *
    Matrix4x4.CreateTranslation(1100, -400, 300);
Matrix4x4 relativeRoot = SpellMeshSkinningLaw.CameraRelativeModel(root, camera);
NearV(Vector3.Transform(localPoint, relativeRoot), Vector3.Transform(localPoint, root) - camera,
    1e-4f, "root/camera boundary applied either transform more than once");

// CPU/GPU packing contract and source interface.
Matrix4x4 adversarial = new(1.1f, .2f, .3f, 0, -.4f, .9f, .5f, 0,
    .6f, -.7f, 1.2f, 0, 13, -17, 19, 1);
var packed = new float[M2Animator.MaxBones * 12];
M2Animator.Pack([adversarial], 1, packed);
NearV(SpellMeshSkinningLaw.TransformPackedPoint(packed, 0, localPoint),
    Vector3.Transform(localPoint, adversarial), 1e-6f, "packed shader point dots");
NearV(SpellMeshSkinningLaw.TransformPackedVector(packed, 0, bindNormal),
    Vector3.TransformNormal(bindNormal, adversarial), 1e-6f, "packed shader vector dots");
string shader = SpellMeshSkinningLaw.VertexShaderSource;
Check(shader.Contains("for (int i=0;i<4;i++)", StringComparison.Ordinal) &&
      shader.Contains("sp += skinPoint(aPosition,b)*w", StringComparison.Ordinal) &&
      shader.Contains("transpose(inverse(worldLinear))", StringComparison.Ordinal) &&
      shader.Contains("b>=uBoneCount", StringComparison.Ordinal),
    "production shader interface no longer exercises four weights/invalid guard/inverse-transpose");

ClientConfig config = ClientConfig.Load(args[0]);
using var mpq = new MpqMount(config.ClientDataPath);

// Fire/Frost Ward are ideal A/B fixtures: they are emitter-only assets (no mesh), and every
// particle/ribbon joint descends from an authored billboard joint. A must leave their evaluated
// palette exactly untouched; B must respond to the camera without changing attachment/root data.
foreach (string wardPath in new[]
{
    @"Spells\FireWard_Impact_Chest.m2",
    @"Spells\FrostWard_Impact_Chest.m2",
})
{
    M2Model ward = LoadModel(mpq, wardPath);
    Check(ward.Vertices.Count == 0 && ward.ParticleEmitters.Count == 4 &&
          ward.RibbonEmitters.Count == 3 && ward.Bones.Count == 20,
        $"ward A/B fixture drift: {wardPath}");
    int[] carrierBones = ward.ParticleEmitters.Select(e => (int)e.Bone)
        .Concat(ward.RibbonEmitters.Select(r => (int)r.Bone)).Distinct().ToArray();
    Check(carrierBones.Length > 0 && carrierBones.All(b => HasBillboardAncestor(ward, b)),
        $"ward emitter/ribbon carrier escaped billboard hierarchy: {wardPath}");

    M2Animator wardAnimator = BuildQuiet(ward);
    M2Animator.Clip wardClip = wardAnimator.FindSequenceOrBake(0) ??
        throw new InvalidOperationException($"ward sequence zero missing: {wardPath}");
    float wardAge = MathF.Min(.2f, wardClip.DurationSeconds * .37f);
    var wardA = new Matrix4x4[wardAnimator.BoneCount];
    wardAnimator.Evaluate(wardClip, wardAge, wardAge, wardA);
    Matrix4x4[] wardExpected = wardA.ToArray();
    SpellMeshSkinningLaw.ApplyBillboardBonesIfEnabled(false, ward,
        Matrix4x4.CreateTranslation(30, -12, 4), new Vector3(40, -2, 11),
        Vector3.UnitX, wardAnimator.BoneCount, wardA);
    for (int i = 0; i < wardA.Length; i++)
        NearM(wardA[i], wardExpected[i], 0f, $"ward A changed bone {i}: {wardPath}");

    Matrix4x4[] wardBForward = wardExpected.ToArray();
    Matrix4x4[] wardBSide = wardExpected.ToArray();
    SpellMeshSkinningLaw.ApplyBillboardBonesIfEnabled(true, ward,
        Matrix4x4.CreateTranslation(30, -12, 4), new Vector3(40, -2, 11),
        Vector3.UnitY, wardAnimator.BoneCount, wardBForward);
    SpellMeshSkinningLaw.ApplyBillboardBonesIfEnabled(true, ward,
        Matrix4x4.CreateTranslation(30, -12, 4), new Vector3(40, -2, 11),
        Vector3.UnitX, wardAnimator.BoneCount, wardBSide);
    Check(carrierBones.Any(b => MatrixDelta(wardExpected[b], wardBForward[b]) > 1e-4f),
        $"ward B did not rewrite a particle/ribbon carrier: {wardPath}");
    Check(carrierBones.Any(b => MatrixDelta(wardBForward[b], wardBSide[b]) > 1e-4f),
        $"ward B carrier did not respond to camera direction: {wardPath}");
    Console.WriteLine($"[mesh-fixture-ward-ab] {wardPath} carriers={carrierBones.Length} " +
        $"particles={ward.ParticleEmitters.Count} ribbons={ward.RibbonEmitters.Count} mesh=0");
}

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

int parsed = 0, referencedListed = paths.Count(referenced.Contains), referencedParsed = 0,
    referencedMeshModels = 0, bindModels = 0, bindBones = 0, overBudgetModels = 0,
    referencedOverBudgetModels = 0;
long[] verticesByInfluence = new long[5], referencedVerticesByInfluence = new long[5];
int[] modelsByInfluence = new int[5], referencedModelsByInfluence = new int[5];
long zeroWeight = 0, badTotal = 0, badIndexVertices = 0, badIndexInfluences = 0,
    duplicateIndex = 0, referencedZeroWeight = 0, referencedBadTotal = 0,
    referencedBadIndexVertices = 0, referencedBadIndexInfluences = 0,
    referencedDuplicateIndex = 0;
int badIndexModels = 0, referencedBadIndexModels = 0;
long referencedMultiPivot = 0, referencedMultiTranslation = 0, referencedMultiRotation = 0,
    referencedMultiScale = 0, referencedMultiBillboard = 0, referencedMultiIgnoreRotation = 0;
int referencedMultiPivotModels = 0;
var candidates = new List<Candidate>();
var badIndexPaths = new List<(string Path, long Vertices, long Influences, bool Referenced)>();
foreach (string path in paths.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
{
    byte[]? bytes = mpq.ReadFile(path);
    M2Model? model = bytes is null ? null : M2Reader.Parse(bytes);
    if (model is null) continue;
    parsed++;
    bool isReferenced = referenced.Contains(path);
    if (isReferenced) referencedParsed++;
    if (isReferenced && model.Vertices.Count > 0) referencedMeshModels++;
    if (model.Bones.Count > M2Animator.MaxBones)
    {
        overBudgetModels++;
        if (isReferenced) referencedOverBudgetModels++;
    }
    var modelInfluences = new bool[5];
    var referencedInfluences = new bool[5];
    long multiPivot = 0, multiTranslation = 0, multiRotation = 0, multiScale = 0,
        multiBillboard = 0, multiIgnore = 0;
    int maxInfluences = 0;
    long modelBadIndexVertices = 0, modelBadIndexInfluences = 0;
    foreach (M2Vertex vertex in model.Vertices)
    {
        byte[] weights = Weights(vertex);
        byte[] indices = Indices(vertex);
        int influenceCount = weights.Count(w => w > 0);
        maxInfluences = Math.Max(maxInfluences, influenceCount);
        verticesByInfluence[influenceCount]++;
        modelInfluences[influenceCount] = true;
        if (isReferenced)
        {
            referencedVerticesByInfluence[influenceCount]++;
            referencedInfluences[influenceCount] = true;
        }
        int total = weights.Sum(w => (int)w);
        if (total == 0) { zeroWeight++; if (isReferenced) referencedZeroWeight++; }
        if (total != 255) { badTotal++; if (isReferenced) referencedBadTotal++; }
        var live = new List<int>(4);
        bool invalidVertex = false;
        for (int k = 0; k < 4; k++)
        {
            if (weights[k] == 0) continue;
            int bone = indices[k];
            live.Add(bone);
            if (bone >= model.Bones.Count || bone >= M2Animator.MaxBones)
            {
                invalidVertex = true;
                modelBadIndexInfluences++;
                badIndexInfluences++;
                if (isReferenced) referencedBadIndexInfluences++;
            }
        }
        if (invalidVertex)
        {
            modelBadIndexVertices++;
            badIndexVertices++;
            if (isReferenced) referencedBadIndexVertices++;
        }
        if (live.Count != live.Distinct().Count())
        {
            duplicateIndex++;
            if (isReferenced) referencedDuplicateIndex++;
        }
        if (!isReferenced || influenceCount < 2) continue;

        bool pivoted = false, translated = false, rotated = false, scaled = false,
            billboard = false, ignore = false;
        foreach (int boneIndex in live.Distinct())
        {
            if (boneIndex < 0 || boneIndex >= model.Bones.Count) continue;
            M2Bone direct = model.Bones[boneIndex];
            pivoted |= direct.Pivot.LengthSquared() > 1e-8f;
            billboard |= (direct.Flags & 0x78) != 0;
            ignore |= (direct.Flags & 0x04) != 0;
            int at = boneIndex;
            var seen = new HashSet<int>();
            while (at >= 0 && at < model.Bones.Count && seen.Add(at))
            {
                M2Bone bone = model.Bones[at];
                translated |= bone.Translation.Keys.Count > 1;
                rotated |= bone.Rotation.Keys.Count > 1;
                scaled |= bone.Scale.Keys.Count > 1;
                at = bone.ParentBone;
            }
        }
        if (pivoted) multiPivot++;
        if (translated) multiTranslation++;
        if (rotated) multiRotation++;
        if (scaled) multiScale++;
        if (billboard) multiBillboard++;
        if (ignore) multiIgnore++;
    }
    if (modelBadIndexVertices > 0)
    {
        badIndexModels++;
        if (isReferenced) referencedBadIndexModels++;
        badIndexPaths.Add((path, modelBadIndexVertices, modelBadIndexInfluences, isReferenced));
    }
    for (int i = 0; i <= 4; i++)
    {
        if (modelInfluences[i]) modelsByInfluence[i]++;
        if (referencedInfluences[i]) referencedModelsByInfluence[i]++;
    }

    if (isReferenced && model.Bones.Count > 0)
    {
        TextWriter savedOutput = Console.Out;
        M2Animator animator;
        try
        {
            Console.SetOut(TextWriter.Null);
            animator = M2Animator.Build(model, []) ??
                throw new InvalidOperationException($"animator missing for {path}");
        }
        finally { Console.SetOut(savedOutput); }
        var bindPalette = new Matrix4x4[model.Bones.Count];
        animator.Evaluate(null, 0, 0, bindPalette);
        bindModels++;
        for (int i = 0; i < bindPalette.Length; i++)
        {
            bindBones++;
            NearM(bindPalette[i], Matrix4x4.Identity, 3e-4f,
                $"mounted bind pose {path} bone {i}");
        }
    }

    if (isReferenced && maxInfluences >= 2)
    {
        if (multiPivot > 0) referencedMultiPivotModels++;
        referencedMultiPivot += multiPivot;
        referencedMultiTranslation += multiTranslation;
        referencedMultiRotation += multiRotation;
        referencedMultiScale += multiScale;
        referencedMultiBillboard += multiBillboard;
        referencedMultiIgnoreRotation += multiIgnore;
        candidates.Add(new Candidate(path, model.Vertices.Count, model.Bones.Count, maxInfluences,
            multiPivot, multiTranslation, multiRotation, multiScale, multiBillboard, multiIgnore));
    }
}

Check(paths.Count == 9717 && parsed == 9654, "mounted M2 list/parse corpus drift");
Check(bindModels > 0 && bindBones > 0, "mounted corpus did not exercise animator bind poses");
Check(referenced.Count == 599, "referenced SpellVisual path census drift");
Check(referencedListed == 555 && referencedParsed == 555 && referencedMeshModels == 350,
    "referenced listed/parsed/mesh-model corpus drift");
Check(modelsByInfluence.SequenceEqual(new[] { 0, 9315, 741, 446, 273 }) &&
      verticesByInfluence.SequenceEqual(new long[] { 0, 2109706, 135947, 24172, 4679 }),
    "full mounted influence histogram drift");
Check(referencedModelsByInfluence.SequenceEqual(new[] { 0, 350, 17, 4, 1 }) &&
      referencedVerticesByInfluence.SequenceEqual(new long[] { 0, 37399, 2485, 170, 8 }),
    "referenced influence histogram drift");
Check(referencedMultiPivotModels == 16 && referencedMultiPivot == 2643 &&
      referencedMultiTranslation == 1264 && referencedMultiRotation == 2663 &&
      referencedMultiScale == 264 && referencedMultiBillboard == 0 &&
      referencedMultiIgnoreRotation == 0, "referenced multi-weight behavior census drift");
Check(referencedZeroWeight == 0 && referencedBadTotal == 0 &&
      referencedBadIndexVertices == 0 && referencedDuplicateIndex == 1351,
    $"referenced effect mesh anomaly census drift: zero={referencedZeroWeight} " +
    $"bad-total={referencedBadTotal} invalid-vertices={referencedBadIndexVertices} " +
    $"duplicates={referencedDuplicateIndex}");
Check(referencedOverBudgetModels == 0,
    "referenced effect model exceeds the 160-bone spell palette budget");
Check(bindModels == 555 && bindBones == 5222,
    "referenced mounted bind-pose coverage drift");
Check(overBudgetModels == 3 && badIndexModels == 3 && badIndexVertices == 999 &&
      badIndexInfluences == 1061 && duplicateIndex == 2840,
    "full mounted palette anomaly census drift");
Check(badIndexPaths.Select(p => p.Path).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(
    [@"Interface\Glues\Models\UI_Tauren\UI_Tauren.m2", @"Creature\Dragon\Taerar.m2",
     @"World\Generic\PassiveDoodads\Ships\ShipAnimation\transportship_sails.m2"]),
    "mounted invalid-index control path set drift");

Console.WriteLine($"[mesh-census] listed={paths.Count} parsed={parsed} referenced={referenced.Count} " +
    $"referenced-listed={referencedListed} referenced-parsed={referencedParsed} " +
    $"referenced-mesh-models={referencedMeshModels} bind-models={bindModels} " +
    $"bind-bones={bindBones} over-160-models={overBudgetModels} " +
    $"referenced-over-160-models={referencedOverBudgetModels}");
Console.WriteLine("[mesh-influences-all] " + InfluenceLine(modelsByInfluence, verticesByInfluence));
Console.WriteLine("[mesh-influences-referenced] " +
    InfluenceLine(referencedModelsByInfluence, referencedVerticesByInfluence));
Console.WriteLine($"[mesh-anomalies-all] zero-total-vertices={zeroWeight} " +
    $"total-not-255-vertices={badTotal} invalid-models={badIndexModels} " +
    $"invalid-vertices={badIndexVertices} invalid-influences={badIndexInfluences} " +
    $"duplicate-live-index-vertices={duplicateIndex}");
Console.WriteLine($"[mesh-anomalies-referenced] zero-total-vertices={referencedZeroWeight} " +
    $"total-not-255-vertices={referencedBadTotal} invalid-models={referencedBadIndexModels} " +
    $"invalid-vertices={referencedBadIndexVertices} invalid-influences={referencedBadIndexInfluences} " +
    $"duplicate-live-index-vertices={referencedDuplicateIndex}");
Console.WriteLine($"[mesh-referenced-multi] pivot-models={referencedMultiPivotModels} " +
    $"pivot-vertices={referencedMultiPivot} translation-chain={referencedMultiTranslation} " +
    $"rotation-chain={referencedMultiRotation} scale-chain={referencedMultiScale} " +
    $"billboard={referencedMultiBillboard} ignore-parent-rotation={referencedMultiIgnoreRotation}");
foreach (Candidate candidate in candidates
    .OrderByDescending(c => c.MaxInfluences).ThenByDescending(c => c.Scale)
    .ThenByDescending(c => c.Billboard).ThenByDescending(c => c.Pivot)
    .ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase).Take(24))
    Console.WriteLine($"[mesh-candidate] {candidate.Path} verts={candidate.Vertices} bones={candidate.Bones} " +
        $"max={candidate.MaxInfluences} pivot={candidate.Pivot} t={candidate.Translation} " +
        $"r={candidate.Rotation} s={candidate.Scale} billboard={candidate.Billboard} ignore={candidate.Ignore}");
foreach (var invalidPath in badIndexPaths
    .OrderByDescending(p => p.Referenced).ThenByDescending(p => p.Vertices)
    .ThenBy(p => p.Path, StringComparer.OrdinalIgnoreCase).Take(12))
    Console.WriteLine($"[mesh-invalid-control] {invalidPath.Path} referenced={invalidPath.Referenced} " +
        $"vertices={invalidPath.Vertices} influences={invalidPath.Influences}");

// Named real fixtures. Rake is the only referenced four-influence asset; Undying Strength is a
// three-influence nonzero-pivot asset under live T/R/S chains; Arcane Shot is the single-weight
// negative control used by the neighboring missile/ribbon validators.
M2Model rake = LoadModel(mpq, @"Spells\Rake.m2");
int[] rakeHistogram = InfluenceHistogram(rake);
Check(rakeHistogram.SequenceEqual(new[] { 0, 489, 200, 20, 8 }),
    $"Rake four-influence fixture drift: {string.Join(',', rakeHistogram)}");
M2Animator rakeAnimator = BuildQuiet(rake);
M2Animator.Clip rakeClip = rakeAnimator.FindSequenceOrBake(0) ??
    throw new InvalidOperationException("Rake sequence zero missing");
float rakeAge = rakeClip.DurationSeconds * .37f;
var rakePalette = new Matrix4x4[rake.Bones.Count];
rakeAnimator.Evaluate(rakeClip, rakeAge, rakeAge, rakePalette);
AdversarialVertex rakeVertex = BestRemovalFixture(rake, rakePalette, 4);
Console.WriteLine($"[mesh-fixture-rake] seq=0 duration={rakeClip.DurationSeconds:R} age={rakeAge:R} " +
    $"histogram={string.Join(',', rakeHistogram)} vertex={rakeVertex.Index} " +
    $"weights={string.Join(',', Weights(rake.Vertices[rakeVertex.Index]))} " +
    $"bones={string.Join(',', Indices(rake.Vertices[rakeVertex.Index]))} " +
    $"posed={rakeVertex.Posed} min-removal-delta={rakeVertex.MinRemovalDelta:R}");
Check(rakeVertex.MinRemovalDelta > 1e-5f,
    "Rake four-weight fixture cannot distinguish every influence");
Check(rakeVertex.Index == 302 &&
      Weights(rake.Vertices[rakeVertex.Index]).SequenceEqual(new byte[] { 64, 64, 64, 63 }) &&
      Indices(rake.Vertices[rakeVertex.Index]).SequenceEqual(new byte[] { 4, 14, 5, 16 }),
    "Rake selected four-weight vertex drift");
NearV(rakeVertex.Posed, new Vector3(1.0220361f, 1.3673285f, -.12145541f), 2e-5f,
    "Rake animated four-weight pose drift");
Near(rakeVertex.MinRemovalDelta, .04776045f, 2e-5f,
    "Rake weakest fourth-influence contribution drift");

M2Model undying = LoadModel(mpq, @"Spells\Undying_Strength_Impact_Chest.m2");
int[] undyingHistogram = InfluenceHistogram(undying);
Check(undyingHistogram.SequenceEqual(new[] { 0, 296, 70, 28, 0 }),
    $"Undying Strength influence histogram drift: {string.Join(',', undyingHistogram)}");
M2Animator undyingAnimator = BuildQuiet(undying);
int undyingSequence = Enumerable.Range(0, undying.Sequences.Count).First(i =>
    undyingAnimator.FindSequenceOrBake(i)?.Bones.Any(b => b.ScaleKeys.Length > 1) == true);
M2Animator.Clip undyingClip = undyingAnimator.FindSequenceOrBake(undyingSequence)!;
float undyingAge = undyingClip.DurationSeconds * .37f;
var undyingPalette = new Matrix4x4[undying.Bones.Count];
undyingAnimator.Evaluate(undyingClip, undyingAge, undyingAge, undyingPalette);
AdversarialVertex undyingVertex = BestRemovalFixture(undying, undyingPalette, 3);
Console.WriteLine($"[mesh-fixture-undying] seq={undyingSequence} duration={undyingClip.DurationSeconds:R} " +
    $"age={undyingAge:R} vertex={undyingVertex.Index} " +
    $"weights={string.Join(',', Weights(undying.Vertices[undyingVertex.Index]))} " +
    $"bones={string.Join(',', Indices(undying.Vertices[undyingVertex.Index]))} " +
    $"posed={undyingVertex.Posed} min-removal-delta={undyingVertex.MinRemovalDelta:R}");
Check(undyingVertex.MinRemovalDelta > 1e-5f,
    "Undying Strength three-weight fixture cannot distinguish every influence");
Check(undyingSequence == 0 && undyingVertex.Index == 378 &&
      Weights(undying.Vertices[undyingVertex.Index]).SequenceEqual(new byte[] { 85, 85, 85, 0 }) &&
      Indices(undying.Vertices[undyingVertex.Index]).SequenceEqual(new byte[] { 5, 9, 15, 0 }),
    "Undying Strength selected scale-chain vertex drift");
NearV(undyingVertex.Posed, new Vector3(-.17665625f, 1.1976844f, -.052858792f),
    2e-5f, "Undying Strength animated T/R/S pose drift");
Near(undyingVertex.MinRemovalDelta, .0058016935f, 2e-5f,
    "Undying Strength weakest live influence drift");

M2Model arcaneControl = LoadModel(mpq, @"Spells\ArcaneShot_Missile.m2");
int[] arcaneHistogram = InfluenceHistogram(arcaneControl);
Check(arcaneHistogram[2] == 0 && arcaneHistogram[3] == 0 && arcaneHistogram[4] == 0 &&
      arcaneHistogram[1] == arcaneControl.Vertices.Count &&
      arcaneControl.Vertices.Count == 7 && arcaneControl.Bones.Count == 7,
    "Arcane Shot ceased to be the single-weight control fixture");
Console.WriteLine($"[mesh-fixture-control] Spells\\ArcaneShot_Missile.m2 " +
    $"vertices={arcaneControl.Vertices.Count} bones={arcaneControl.Bones.Count} all-single-weight=true");
Console.WriteLine($"[spell-mesh-skinning-check] PASS ({checks:N0} checks)");
return 0;

static M2Vertex Vertex(byte[] weights, byte[] indices) => new()
{
    BoneWeight0 = weights[0], BoneWeight1 = weights[1], BoneWeight2 = weights[2],
    BoneWeight3 = weights[3], BoneIndex0 = indices[0], BoneIndex1 = indices[1],
    BoneIndex2 = indices[2], BoneIndex3 = indices[3],
};

static byte[] Weights(in M2Vertex v) =>
    [v.BoneWeight0, v.BoneWeight1, v.BoneWeight2, v.BoneWeight3];
static byte[] Indices(in M2Vertex v) =>
    [v.BoneIndex0, v.BoneIndex1, v.BoneIndex2, v.BoneIndex3];

static M2Animator.BoneChannels Channels(Vector3? translation = null,
    Quaternion? rotation = null, Vector3? scale = null) => new()
{
    TranslationTimes = translation.HasValue ? [0f] : [],
    TranslationKeys = translation.HasValue ? [translation.Value] : [],
    RotationTimes = rotation.HasValue ? [0f] : [],
    RotationKeys = rotation.HasValue ? [rotation.Value] : [],
    ScaleTimes = scale.HasValue ? [0f] : [],
    ScaleKeys = scale.HasValue ? [scale.Value] : [],
};

static void SetComponent(ref Vector4 value, int index, float component)
{
    if (index == 0) value.X = component;
    else if (index == 1) value.Y = component;
    else if (index == 2) value.Z = component;
    else value.W = component;
}

static float[] MatrixValues(Matrix4x4 m) =>
[
    m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24,
    m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44,
];

static float MatrixDelta(Matrix4x4 a, Matrix4x4 b)
{
    float[] av = MatrixValues(a), bv = MatrixValues(b);
    float max = 0f;
    for (int i = 0; i < av.Length; i++) max = MathF.Max(max, MathF.Abs(av[i] - bv[i]));
    return max;
}

static bool HasBillboardAncestor(M2Model model, int bone)
{
    var seen = new HashSet<int>();
    while (bone >= 0 && bone < model.Bones.Count && seen.Add(bone))
    {
        M2Bone current = model.Bones[bone];
        if ((current.Flags & 0x78) != 0) return true;
        bone = current.ParentBone;
    }
    return false;
}

static string InfluenceLine(int[] models, long[] vertices)
    => string.Join(" ", Enumerable.Range(0, 5)
        .Select(i => $"i{i}-models={models[i]} i{i}-vertices={vertices[i]}"));

static int[] InfluenceHistogram(M2Model model)
{
    var result = new int[5];
    foreach (M2Vertex vertex in model.Vertices) result[Weights(vertex).Count(w => w > 0)]++;
    return result;
}

static M2Model LoadModel(MpqMount mpq, string path)
    => M2Reader.Parse(mpq.ReadFile(path) ?? throw new InvalidOperationException($"missing {path}"))
       ?? throw new InvalidOperationException($"invalid {path}");

static M2Animator BuildQuiet(M2Model model)
{
    TextWriter output = Console.Out;
    try
    {
        Console.SetOut(TextWriter.Null);
        return M2Animator.Build(model, []) ?? throw new InvalidOperationException("animator missing");
    }
    finally { Console.SetOut(output); }
}

static AdversarialVertex BestRemovalFixture(M2Model model, Matrix4x4[] palette, int influences)
{
    AdversarialVertex best = default;
    float bestScore = -1f;
    for (int index = 0; index < model.Vertices.Count; index++)
    {
        M2Vertex vertex = model.Vertices[index];
        if (Weights(vertex).Count(w => w > 0) != influences) continue;
        Vector3 bindPoint = new(vertex.PosX, vertex.PosY, vertex.PosZ);
        SpellMeshSkinningLaw.VertexSkin skin = SpellMeshSkinningLaw.Resolve(vertex);
        Vector3 full = SpellMeshSkinningLaw.SkinPoint(bindPoint, palette, skin);
        float min = float.MaxValue;
        for (int removed = 0; removed < 4; removed++)
        {
            if (Weights(vertex)[removed] == 0) continue;
            Vector4 reduced = skin.Weights;
            SetComponent(ref reduced, removed, 0f);
            Vector3 without = SpellMeshSkinningLaw.SkinPoint(bindPoint, palette,
                new SpellMeshSkinningLaw.VertexSkin(reduced, skin.Indices));
            min = MathF.Min(min, Vector3.Distance(full, without));
        }
        if (min <= bestScore) continue;
        bestScore = min;
        best = new AdversarialVertex(index, full, min);
    }
    return best;
}

static HashSet<string> ReferencedSpellModels(MpqMount mpq)
{
    DbcFile Read(string path) => mpq.ReadFile(path) is { } bytes && DbcFile.Parse(bytes) is { } dbc
        ? dbc : throw new InvalidOperationException($"missing {path}");
    DbcFile visuals = Read(@"DBFilesClient\SpellVisual.dbc");
    DbcFile kits = Read(@"DBFilesClient\SpellVisualKit.dbc");
    DbcFile names = Read(@"DBFilesClient\SpellVisualEffectName.dbc");
    if (visuals.RecordCount != 2165 || kits.RecordCount != 1772 || names.RecordCount != 775)
        throw new InvalidOperationException("spell visual DBC fixture drift");
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

readonly record struct Candidate(string Path, int Vertices, int Bones, int MaxInfluences,
    long Pivot, long Translation, long Rotation, long Scale, long Billboard, long Ignore);
readonly record struct AdversarialVertex(int Index, Vector3 Posed, float MinRemovalDelta);
