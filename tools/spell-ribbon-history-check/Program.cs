using System.Numerics;
using System.Text;
using MSUIClient;
using MSUIClient.Formats;
using MSUIClient.World.Units;

if (args.Length != 1)
{
    Console.Error.WriteLine("usage: spell-ribbon-history-check <client-config.json>");
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

Near(SpellRibbonHistoryLaw.SimulationStep(1f / 60f), 1f / 60f, 1e-7f,
    "ordinary ribbon step changed");
Near(SpellRibbonHistoryLaw.SimulationStep(.25f), .1f, 1e-7f,
    "ribbon hitch did not clamp at 100 ms");
Near(SpellRibbonHistoryLaw.SimulationStep(float.NaN), 0f, 0f,
    "NaN ribbon step survived");
NearV(SpellRibbonHistoryLaw.CrossSectionAxis(Matrix4x4.Identity, Matrix4x4.Identity),
    -Vector3.UnitZ, 1e-6f,
    "authored WoW +Y did not map to parsed MSUI -Z");

Matrix4x4 scaledRotated = Matrix4x4.CreateScale(2f, 3f, 4f) *
    Matrix4x4.CreateRotationX(MathF.PI / 2f);
NearV(SpellRibbonHistoryLaw.CrossSectionAxis(scaledRotated, Matrix4x4.Identity),
    Vector3.UnitY, 1e-5f, "ribbon axis retained scale or used parsed +Y");

Vector3 pivot = new(3, 4, 5), point = new(7, 8, 9);
Matrix4x4 posed = Matrix4x4.CreateRotationZ(.4f) *
    Matrix4x4.CreateTranslation(10, 20, 30);
Matrix4x4 skin = Matrix4x4.CreateTranslation(-pivot) * posed;
Matrix4x4 root = Matrix4x4.CreateRotationZ(-.2f) *
    Matrix4x4.CreateTranslation(100, 200, 300);
NearV(SpellRibbonHistoryLaw.NodeWorld(point, skin, root),
    Vector3.Transform(point - pivot, posed * root), 1e-4f,
    "ribbon joint pivot was omitted or applied twice");

var history = new SpellRibbonHistoryLaw.State();
Check(!SpellRibbonHistoryLaw.AdvanceLive(history, 0, 10, 1, 0).Commit,
    "new ribbon committed without elapsed time");
var firstLateFrame = new SpellRibbonHistoryLaw.State();
Check(SpellRibbonHistoryLaw.AdvanceLive(firstLateFrame, .25f, 10, 1, 0).Commit &&
    MathF.Abs(firstLateFrame.ClipAge - .1f) < 1e-6f &&
    MathF.Abs(firstLateFrame.RawAge - .25f) < 1e-6f,
    "first observed ribbon frame discarded its source-age interval");
SpellRibbonHistoryLaw.Step hitch = SpellRibbonHistoryLaw.AdvanceLive(history, .25f, 10, 1, 0);
Check(hitch.Commit, "hitch ribbon did not commit at clamped cadence");
Near(history.RawAge, .25f, 1e-6f, "raw ribbon age was clamped");
Near(history.ClipAge, .1f, 1e-6f, "ribbon look clock consumed raw hitch");
Vector3 top = new(1, 2, 3), bottom = new(4, 5, 6);
SpellRibbonHistoryLaw.Commit(history, top, bottom);
Check(history.Edges.Count == 1, "committed ribbon edge missing");

// Live root/bone changes create the next head only. The committed world edge never re-enters
// either transform and therefore remains a true history sample.
Matrix4x4 movedSkin = skin * Matrix4x4.CreateTranslation(50, 0, 0);
_ = SpellRibbonHistoryLaw.NodeWorld(point, movedSkin,
    root * Matrix4x4.CreateTranslation(0, 75, 0));
NearV(history.Edges[0].Top, top, 0f, "old ribbon edge followed a later bone/root pose");
NearV(history.Edges[0].Bottom, bottom, 0f, "old ribbon width followed a later pose");

SpellRibbonHistoryLaw.Step gravityStep = SpellRibbonHistoryLaw.AdvanceLive(
    history, .5f, 0, 1, 2);
Near(gravityStep.SimulationSeconds, .1f, 1e-6f, "ribbon gravity hitch step");
NearV(history.Edges[0].Top, top - Vector3.UnitZ * .4f, 1e-6f,
    "ribbon gravity sag drift");
Near(history.ClipAge, .2f, 1e-6f, "ribbon look clock second hitch");
Near(history.RawAge, .5f, 1e-6f, "ribbon raw age second hitch");

SpellRibbonHistoryLaw.AdvanceDrain(history, .6f, 1, 0);
Near(history.ClipAge, .3f, 1e-6f, "draining ribbon look clock did not clamp");
Near(history.RawAge, 1.1f, 1e-6f, "draining ribbon expiry clock was clamped");
Check(history.Edges.Count == 1, "ribbon edge expired before raw lifetime");
SpellRibbonHistoryLaw.AdvanceDrain(history, .16f, 1, 0);
Check(history.Edges.Count == 0, "ribbon edge did not expire on raw lifetime");
var highRate = new SpellRibbonHistoryLaw.State();
SpellRibbonHistoryLaw.Step highRateStep = SpellRibbonHistoryLaw.AdvanceLive(
    highRate, .1f, 25f, 1f, 0f);
Check(highRateStep.Commit && MathF.Abs(highRate.Accumulator - .5f) < 1e-6f,
    "high-rate ribbon did not commit one edge and retain only fractional remainder");
SpellRibbonHistoryLaw.Commit(highRate, Vector3.One, -Vector3.One);
Check(highRate.Edges.Count == 1, "high-rate ribbon emitted more than one edge per frame");

ClientConfig config = ClientConfig.Load(args[0]);
using var mpq = new MpqMount(config.ClientDataPath);
HashSet<string> referenced = ReferencedSpellModels(mpq);
SpellCatalog spells = SpellCatalog.Load(mpq) ?? throw new InvalidOperationException("Spell catalog missing");
SpellVisualCatalog visuals = SpellVisualCatalog.Load(mpq) ??
    throw new InvalidOperationException("SpellVisual catalog missing");
var missilePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
foreach (SpellInfo spell in spells.Spells.Where(s => s.Speed > 0f))
    if (visuals.TryGetStages(spell.VisualId, out SpellVisualStages stages) &&
        visuals.MissilePath(stages) is { } missile)
        missilePaths.Add(missile);

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

int parsed = 0, ribbonModels = 0, ribbons = 0, spellModels = 0, spellRibbons = 0;
int referencedModels = 0, referencedRibbons = 0, missileModels = 0, missileRibbons = 0;
int gravity = 0, animatedHeight = 0, animatedColor = 0, animatedAlpha = 0;
int keyedVisibility = 0, multiTile = 0, scaledBoneChain = 0, animatedBoneChain = 0;
var ribbonPaths = new List<string>();
var scaledPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
var animatedBonePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
foreach (string path in paths.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
{
    byte[]? bytes = mpq.ReadFile(path);
    M2Model? model = bytes is null ? null : M2Reader.Parse(bytes);
    if (model is null) continue;
    parsed++;
    if (model.RibbonEmitters.Count == 0) continue;
    ribbonModels++;
    ribbons += model.RibbonEmitters.Count;
    ribbonPaths.Add(path);
    bool isSpell = path.StartsWith(@"Spells\", StringComparison.OrdinalIgnoreCase);
    bool isReferenced = referenced.Contains(path);
    bool isMissile = missilePaths.Contains(path);
    if (isSpell) { spellModels++; spellRibbons += model.RibbonEmitters.Count; }
    if (isReferenced) { referencedModels++; referencedRibbons += model.RibbonEmitters.Count; }
    if (isMissile) { missileModels++; missileRibbons += model.RibbonEmitters.Count; }
    foreach (M2RibbonEmitter ribbon in model.RibbonEmitters)
    {
        if (ribbon.Gravity != 0f) gravity++;
        if (ribbon.HeightAbove.Keys.Count > 1 || ribbon.HeightBelow.Keys.Count > 1) animatedHeight++;
        if (ribbon.Color.Keys.Count > 1) animatedColor++;
        if (ribbon.Alpha.Keys.Count > 1) animatedAlpha++;
        if (ribbon.Visibility.Keys.Count > 0) keyedVisibility++;
        if (ribbon.TextureRows > 1 || ribbon.TextureColumns > 1) multiTile++;
        int bone = ribbon.Bone;
        var seen = new HashSet<int>();
        bool scaled = false, animated = false;
        while (bone >= 0 && bone < model.Bones.Count && seen.Add(bone))
        {
            if (model.Bones[bone].Scale.Keys.Any(s =>
                    Vector3.DistanceSquared(s, Vector3.One) > 1e-6f))
                scaled = true;
            animated |= model.Bones[bone].Translation.Keys.Count > 1 ||
                        model.Bones[bone].Rotation.Keys.Count > 1 ||
                        model.Bones[bone].Scale.Keys.Count > 1;
            bone = model.Bones[bone].ParentBone;
        }
        if (scaled) { scaledBoneChain++; scaledPaths.Add(path); }
        if (animated) { animatedBoneChain++; animatedBonePaths.Add(path); }
    }
}

Check(paths.Count == 9717 && parsed == 9654, "mounted M2 corpus drift");
Check(ribbonModels == 176 && ribbons == 590 && spellModels == 115 && spellRibbons == 350,
    "mounted ribbon corpus drift");
Check(referencedModels == 107 && referencedRibbons == 318 &&
    missileModels == 35 && missileRibbons == 80,
    "mounted referenced/missile ribbon census drift");
Check(gravity == 102 && animatedHeight == 142 && animatedColor == 0 &&
    animatedAlpha == 214 && keyedVisibility == 590 && multiTile == 0 &&
    scaledBoneChain == 90, "mounted ribbon behavior census drift");

M2Model holy = Model(@"Spells\HolySmite_Low_Chest.m2");
Check(holy.RibbonEmitters.Count == 2, "Holy Smite ribbon fixture drift");
M2RibbonEmitter holyRibbon = holy.RibbonEmitters[0];
Near(M2TrackSampling.Float(holyRibbon.HeightAbove, holy, 0, .2f), .167f, .002f,
    "Holy Smite keyed height peak drift");
Near(M2TrackSampling.Float(holyRibbon.HeightAbove, holy, 0, .4f), 0f, .002f,
    "Holy Smite keyed height collapse drift");
Near(M2TrackSampling.Fixed16(holyRibbon.Alpha, holy, 0, .2f), 1f, .002f,
    "Holy Smite peak alpha drift");
Check(M2TrackSampling.Fixed16(holyRibbon.Alpha, holy, 0, .6f) < .002f,
    "Holy Smite alpha did not finish fading");
var holyClock = new SpellRibbonHistoryLaw.State();
SpellRibbonHistoryLaw.AdvanceLive(holyClock, 0, 0, 1, 0);
SpellRibbonHistoryLaw.AdvanceLive(holyClock, .5f, 0, 1, 0);
Near(holyClock.ClipAge, .1f, 1e-6f, "Holy Smite hitch look clock drift");
Check(M2TrackSampling.Fixed16(holyRibbon.Alpha, holy, 0, holyClock.ClipAge) > .9f &&
      M2TrackSampling.Fixed16(holyRibbon.Alpha, holy, 0, holyClock.RawAge) < .1f,
    "Holy Smite fixture cannot distinguish clamped ribbon look time from raw expiry time");

const string thrownPath = @"Item\ObjectComponents\Weapon\Thrown_1H_Dagger_A_01.m2";
M2Model thrown = Model(thrownPath);
Check(thrown.RibbonEmitters.Count > 0 &&
    thrown.TryFindSequenceIndexByAnimationId(144) >= 0,
    "thrown InFlight ribbon fixture drift");
int thrownStand = thrown.TryFindSequenceIndexByAnimationId(0);
int thrownFlight = thrown.TryFindSequenceIndexByAnimationId(144);
Check(thrownStand >= 0 &&
    M2TrackSampling.Byte(thrown.RibbonEmitters[0].Visibility, thrown, thrownStand, 0) == 0 &&
    M2TrackSampling.Byte(thrown.RibbonEmitters[0].Visibility, thrown, thrownFlight, 0) != 0,
    "thrown ribbon visibility did not isolate InFlight from Stand");

// A real animated-bone missile fixture proves the coordinate-space negative: parsed +Y and
// authored-WoW +Y are orthogonal axes, and later rig/root poses cannot rewrite a committed edge.
M2Model arcane = Model(@"Spells\ArcaneShot_Missile.m2");
int arcaneSequence = arcane.TryFindSequenceIndexByAnimationId(144);
Check(arcaneSequence >= 0 && arcane.RibbonEmitters.Count > 0,
    "Arcane Shot InFlight ribbon fixture drift");
M2Animator arcaneAnimator = M2Animator.Build(arcane, [144], includeStaticSequences: true) ??
    throw new InvalidOperationException("Arcane Shot animator missing");
M2Animator.Clip arcaneClip = arcaneAnimator.FindSequenceOrBake(arcaneSequence) ??
    throw new InvalidOperationException("Arcane Shot InFlight clip missing");
var arcaneSkin0 = new Matrix4x4[M2Animator.MaxBones];
var arcaneSkin1 = new Matrix4x4[M2Animator.MaxBones];
arcaneAnimator.Evaluate(arcaneClip, 0, 0, arcaneSkin0);
arcaneAnimator.Evaluate(arcaneClip, .2f, .2f, arcaneSkin1);
M2RibbonEmitter arcaneRibbon = arcane.RibbonEmitters[0];
Vector3 arcaneNode0 = SpellRibbonHistoryLaw.NodeWorld(arcaneRibbon.Position,
    arcaneSkin0[arcaneRibbon.Bone], Matrix4x4.Identity);
Vector3 arcaneNode1 = SpellRibbonHistoryLaw.NodeWorld(arcaneRibbon.Position,
    arcaneSkin1[arcaneRibbon.Bone], Matrix4x4.CreateTranslation(3, 4, 5));
Check(Vector3.Distance(arcaneNode0, arcaneNode1) > 1f,
    "Arcane Shot fixture did not exercise later bone/root motion");
Vector3 arcaneAxis = SpellRibbonHistoryLaw.CrossSectionAxis(
    arcaneSkin0[arcaneRibbon.Bone], Matrix4x4.Identity);
Vector3 oldParsedYAxis = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY,
    arcaneSkin0[arcaneRibbon.Bone]));
Check(MathF.Abs(Vector3.Dot(arcaneAxis, oldParsedYAxis)) < .1f,
    "Arcane Shot fixture cannot distinguish authored +Y from parsed +Y");
var arcaneHistory = new SpellRibbonHistoryLaw.State();
SpellRibbonHistoryLaw.AdvanceLive(arcaneHistory, 0, 0, arcaneRibbon.EdgeLifetime, 0);
SpellRibbonHistoryLaw.Commit(arcaneHistory, arcaneNode0 + arcaneAxis,
    arcaneNode0 - arcaneAxis);
Vector3 committedArcaneTop = arcaneHistory.Edges[0].Top;
_ = arcaneNode1;
NearV(arcaneHistory.Edges[0].Top, committedArcaneTop, 0f,
    "real Arcane Shot committed edge followed a later pose");

Console.WriteLine($"[ribbon-census] paths={paths.Count} parsed={parsed} " +
    $"models={ribbonModels} ribbons={ribbons} spell-models={spellModels} spell-ribbons={spellRibbons}");
Console.WriteLine($"[ribbon-census] referenced-models={referencedModels} " +
    $"referenced-ribbons={referencedRibbons} missile-models={missileModels} " +
    $"missile-ribbons={missileRibbons}");
Console.WriteLine($"[ribbon-census] gravity={gravity} animated-height={animatedHeight} " +
    $"animated-color={animatedColor} animated-alpha={animatedAlpha} visibility={keyedVisibility} " +
    $"multi-tile={multiTile} scaled-chain={scaledBoneChain} animated-chain={animatedBoneChain}");
Console.WriteLine("[ribbon-scaled] " + string.Join(" | ", scaledPaths
    .Where(p => p.StartsWith(@"Spells\", StringComparison.OrdinalIgnoreCase)).Take(8)));
Console.WriteLine("[ribbon-animated-bone] " + string.Join(" | ", animatedBonePaths
    .Where(p => p.StartsWith(@"Spells\", StringComparison.OrdinalIgnoreCase)).Take(8)));
Console.WriteLine("[ribbon-fixtures] " + string.Join(" | ", ribbonPaths
    .Where(p => p.StartsWith(@"Spells\", StringComparison.OrdinalIgnoreCase))
    .Take(12)));
Console.WriteLine($"[spell-ribbon-history-check] PASS ({checks:N0} checks)");
return 0;

M2Model Model(string path)
    => M2Reader.Parse(mpq.ReadFile(path) ?? throw new InvalidOperationException($"missing {path}"))
       ?? throw new InvalidOperationException($"invalid {path}");

static HashSet<string> ReferencedSpellModels(MpqMount mpq)
{
    DbcFile Read(string path) => mpq.ReadFile(path) is { } bytes && DbcFile.Parse(bytes) is { } dbc
        ? dbc : throw new InvalidOperationException($"missing {path}");
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
