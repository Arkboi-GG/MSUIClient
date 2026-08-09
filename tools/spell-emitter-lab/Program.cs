using System.Globalization;
using System.Numerics;
using System.Text;
using MSUIClient;
using MSUIClient.Creator;
using MSUIClient.Formats;

if (args.Length is < 1 or > 2)
{
    Console.Error.WriteLine("usage: spell-emitter-lab <client-config.json> [output.csv]");
    return 2;
}

ClientConfig config = ClientConfig.Load(args[0]);
using var mpq = new MpqMount(config.ClientDataPath);
SpellCatalog spells = SpellCatalog.Load(mpq)
    ?? throw new InvalidOperationException("Spell.dbc could not be loaded");
SpellVisualCatalog visuals = SpellVisualCatalog.Load(mpq)
    ?? throw new InvalidOperationException("SpellVisual catalogs could not be loaded");

var uses = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
void Add(string? path, string use)
{
    if (string.IsNullOrWhiteSpace(path)) return;
    path = SpellVisualCatalog.ModelPath(path);
    if (!uses.TryGetValue(path, out HashSet<string>? set)) uses[path] = set = [];
    if (set.Count < 24) set.Add(use);
}
void AddKit(in SpellInfo spell, uint kitId, string phase)
{
    if (kitId == 0 || !visuals.TryGetKit(kitId, out SpellVisualKitInfo kit)) return;
    foreach (SpellVisualKitEffect effect in kit.Effects)
        Add(effect.ModelPath, $"{phase}:{spell.Id}:{spell.Name}");
}

foreach (SpellInfo spell in spells.Spells.Where(s => s.VisualId != 0))
{
    if (!visuals.TryGetStages(spell.VisualId, out SpellVisualStages stages)) continue;
    AddKit(spell, stages.Precast, "precast");
    AddKit(spell, stages.Cast, "cast");
    AddKit(spell, stages.Impact, "impact");
    AddKit(spell, stages.State, "state");
    AddKit(spell, stages.Channel, "channel");
    Add(visuals.MissilePath(stages), $"missile:{spell.Id}:{spell.Name}");
    if (visuals.TryGetAreaVisual(spell.VisualId, out SpellAreaVisualInfo area))
    {
        Add(area.LoopingModelPath, $"area-loop:{spell.Id}:{spell.Name}");
        foreach (SpellAreaEmitterInfo emitter in area.Emitters)
            Add(emitter.ModelPath, $"area-shard:{spell.Id}:{spell.Name}");
    }
}

var rows = new List<Row>();
var unresolved = new List<string>();
int checks = 0;
void Check(bool condition, string message)
{
    checks++;
    if (!condition) throw new InvalidOperationException(message);
}
(string Path, byte[] Bytes)? patchFixture = null;
foreach ((string path, HashSet<string> modelUses) in uses.OrderBy(p => p.Key,
             StringComparer.OrdinalIgnoreCase))
{
    byte[]? bytes = mpq.ReadFile(path);
    M2Model? model = bytes is null ? null : M2Reader.Parse(bytes);
    if (bytes is null || model is null)
    {
        unresolved.Add(path);
        continue;
    }

    List<EmitterSnapshot> creatorEmitters = M2EmitterParser.ReadEmitters(bytes);
    Check(creatorEmitters.Count == model.ParticleEmitters.Count,
        $"Creator/runtime emitter count mismatch: {path}");
    List<MSUIClient.Creator.M2TextureEntry> creatorTextures =
        MSUIClient.Creator.M2TextureParser.ParseTextures(bytes);
    Check(creatorTextures.Count == model.Textures.Count,
        $"Creator/runtime texture count mismatch: {path}");
    Dictionary<int, byte> creatorBlendModes =
        MSUIClient.Creator.M2TextureParser.GetTextureBlendModes(bytes);
    foreach (MSUIClient.Creator.M2TextureEntry texture in creatorTextures)
    {
        int[] expectedRefs = model.ParticleEmitters.Select((e, i) => (e, i))
            .Where(pair => pair.e.Texture == texture.Index).Select(pair => pair.i).ToArray();
        Check(texture.ReferencedByEmitters.OrderBy(i => i).SequenceEqual(expectedRefs),
            $"Creator texture back-reference mismatch: {path} t{texture.Index}");
        byte expectedBlend = expectedRefs.Length == 0 ? (byte)4 :
            expectedRefs.Max(i => model.ParticleEmitters[i].BlendingType);
        Check(creatorBlendModes.GetValueOrDefault(texture.Index, byte.MaxValue) == expectedBlend,
            $"Creator texture blend mismatch: {path} t{texture.Index}");
    }
    if (patchFixture is null && creatorEmitters.Any(e =>
            e.TrackValues.GetValueOrDefault("zSource") is not null))
        patchFixture = (path, bytes);

    uint rawCount = bytes.Length >= 0x144 ? BitConverter.ToUInt32(bytes, 0x13C) : 0;
    uint rawOffset = bytes.Length >= 0x144 ? BitConverter.ToUInt32(bytes, 0x140) : 0;
    for (int i = 0; i < model.ParticleEmitters.Count; i++)
    {
        M2ParticleEmitter emitter = model.ParticleEmitters[i];
        int at = checked((int)rawOffset + i * 504);
        ushort shapeId = i < rawCount && at >= 0 && at + 504 <= bytes.Length
            ? BitConverter.ToUInt16(bytes, at + 0x2A) : ushort.MaxValue;
        EmitterSnapshot creator = creatorEmitters[i];
        Check(creator.EmitterType == shapeId, $"Creator shape mismatch: {path} e{i}");
        Check(creator.TextureId == emitter.Texture, $"Creator texture mismatch: {path} e{i}");
        Check(creator.Bone == emitter.Bone, $"Creator bone mismatch: {path} e{i}");
        Check(creator.Flags == emitter.Flags, $"Creator flags mismatch: {path} e{i}");
        Check(Near(creator.PositionX, emitter.PosX) &&
              Near(creator.PositionY, -emitter.PosZ) &&
              Near(creator.PositionZ, emitter.PosY),
            $"Creator raw position mismatch: {path} e{i}");
        Check(Near(creator.Drag, emitter.Drag), $"Creator drag mismatch: {path} e{i}");
        Check(Near(creator.SpriteSpin, emitter.Spin), $"Creator spin mismatch: {path} e{i}");
        Check(Near(creator.TrackValues.GetValueOrDefault("zSource") ?? 0f, emitter.ZSource),
            $"Creator zSource mismatch: {path} e{i}");
        string texture = emitter.Texture < model.Textures.Count
            ? model.Textures[emitter.Texture].Filename : "";
        rows.Add(new Row(path, string.Join('|', modelUses.OrderBy(x => x)), i, shapeId,
            emitter.Shape.ToString(), texture, emitter.BlendingType, emitter.Flags,
            new Vector3(emitter.PosX, emitter.PosY, emitter.PosZ), emitter.Bone,
            emitter.EmissionRate, emitter.Lifespan, emitter.SteadyStatePopulation,
            emitter.EmissionSpeed, emitter.SpeedVariation, emitter.EmissionAreaLength,
            emitter.EmissionAreaWidth, emitter.VerticalRange, emitter.HorizontalRange,
            emitter.Gravity, emitter.ZSource, emitter.Drag, emitter.Spin,
            emitter.HasBoneSpin, emitter.HasBoneMotion,
            emitter.AngularVelocityMin, emitter.AngularVelocityMax,
            emitter.GeometryModel, emitter.RecursionModel, emitter.HeadOrTail,
            emitter.MidPoint, emitter.EnabledTrack.Keys.Count,
            emitter.TrackKeyCounts.Count(k => k > 1)));
    }
}

foreach (Row row in rows)
{
    ParticleShape expected = row.ShapeId switch
    {
        2 => ParticleShape.Sphere,
        3 => ParticleShape.Spline,
        _ => ParticleShape.Plane,
    };
    Check(row.Shape == expected.ToString(), $"shape decode mismatch: {row.Model} e{row.Emitter}");
    Check(float.IsFinite(row.Rate) && float.IsFinite(row.Life) &&
          float.IsFinite(row.Speed) && float.IsFinite(row.Spin),
        $"non-finite core value: {row.Model} e{row.Emitter}");
}

Check(patchFixture is not null, "no patchable spell emitter fixture found");
if (patchFixture is { } fixture)
{
    byte[] patchedBytes = (byte[])fixture.Bytes.Clone();
    EmitterSnapshot before = M2EmitterParser.ReadEmitters(patchedBytes)[0];
    ushort targetShape = before.EmitterType == 2 ? (ushort)1 : (ushort)2;
    float targetZSource = (before.TrackValues["zSource"] ?? 0f) + 0.375f;
    byte paddingBefore = patchedBytes[before.EmitterBase + 0x29];
    int patched = M2EmitterParser.ApplyEmitterPatch(patchedBytes, new EmitterPatch
    {
        EmitterIndex = 0,
        EmitterType = targetShape,
        Flags = before.Flags ^ 0x10u,
        PositionX = before.PositionX + 0.125f,
        PositionY = before.PositionY - 0.25f,
        PositionZ = before.PositionZ + 0.5f,
        Drag = before.Drag + 0.75f,
        SpriteSpin = before.SpriteSpin - 0.625f,
        ZSource = targetZSource,
    });
    Check(patched == 6, $"expected six patch groups, got {patched}: {fixture.Path}");
    EmitterSnapshot after = M2EmitterParser.ReadEmitters(patchedBytes)[0];
    Check(after.EmitterType == targetShape, "shape patch did not round-trip");
    Check(after.TextureId == before.TextureId, "shape patch changed texture slot");
    Check(patchedBytes[after.EmitterBase + 0x29] == paddingBefore,
        "shape patch changed the +0x29 padding byte");
    Check(after.Flags == (before.Flags ^ 0x10u), "flags patch did not round-trip");
    Check(Near(after.PositionX, before.PositionX + 0.125f) &&
          Near(after.PositionY, before.PositionY - 0.25f) &&
          Near(after.PositionZ, before.PositionZ + 0.5f), "position patch did not round-trip");
    Check(Near(after.Drag, before.Drag + 0.75f), "drag patch did not round-trip");
    Check(Near(after.SpriteSpin, before.SpriteSpin - 0.625f),
        "sprite-spin patch did not round-trip");
    Check(Near(after.TrackValues["zSource"] ?? float.NaN, targetZSource),
        "zSource patch did not round-trip");
    M2Model runtimePatched = M2Reader.Parse(patchedBytes)
        ?? throw new InvalidOperationException("runtime rejected patched fixture");
    Check(runtimePatched.ParticleEmitters[0].Shape ==
          (targetShape == 2 ? ParticleShape.Sphere : ParticleShape.Plane),
        "runtime did not observe patched shape");

    (byte[] cloneBytes, int cloneIndex) = M2ParticlePatcher.CloneEmitter(
        fixture.Bytes, 0, before.TextureId)
        ?? throw new InvalidOperationException("clone fixture failed");
    List<EmitterSnapshot> cloned = M2EmitterParser.ReadEmitters(cloneBytes);
    Check(cloned.Count == M2EmitterParser.ReadEmitters(fixture.Bytes).Count + 1,
        "clone did not append exactly one emitter");
    Check(cloned[cloneIndex].TextureId == before.TextureId &&
          cloned[cloneIndex].EmitterType == before.EmitterType,
        "clone did not retain texture/shape schema");
    float sourceZ = cloned[0].TrackValues["zSource"] ?? 0f;
    Check(M2EmitterParser.PatchTrackValue(cloneBytes, cloneIndex, "zSource", sourceZ + 1f),
        "cloned zSource track could not be patched");
    cloned = M2EmitterParser.ReadEmitters(cloneBytes);
    Check(Near(cloned[0].TrackValues["zSource"] ?? float.NaN, sourceZ),
        "clone zSource patch leaked into source track");
    Check(Near(cloned[cloneIndex].TrackValues["zSource"] ?? float.NaN, sourceZ + 1f),
        "clone zSource private track did not round-trip");
}

string output = args.Length == 2 ? Path.GetFullPath(args[1]) :
    Path.Combine(config.RepoRoot, "dumps", "spell-emitter-census.csv");
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
var csv = new List<string>
{
    "model,uses,emitter,shape_id,shape,texture,blend,flags_hex,pos_x,pos_y,pos_z,bone," +
    "rate,life,steady_population,speed,speed_variation,area_length,area_width," +
    "vertical_range,horizontal_range,gravity,z_source,drag,sprite_spin,bone_spin," +
    "bone_motion,angular_min,angular_max,geometry_model,recursion_model,head_tail," +
    "midpoint,enable_keys,animated_scalar_tracks"
};
csv.AddRange(rows.Select(r => string.Join(',', Q(r.Model), Q(r.Uses), r.Emitter,
    r.ShapeId, r.Shape, Q(r.Texture), r.Blend, $"0x{r.Flags:X}", F(r.Position.X),
    F(r.Position.Y), F(r.Position.Z), r.Bone, F(r.Rate), F(r.Life), F(r.Population),
    F(r.Speed), F(r.SpeedVariation), F(r.AreaLength), F(r.AreaWidth),
    F(r.VerticalRange), F(r.HorizontalRange), F(r.Gravity), F(r.ZSource),
    F(r.Drag), F(r.Spin), r.BoneSpin, r.BoneMotion, Q(V(r.AngularMin)),
    Q(V(r.AngularMax)), Q(r.GeometryModel), Q(r.RecursionModel), r.HeadTail,
    F(r.MidPoint), r.EnableKeys, r.AnimatedScalarTracks)));
File.WriteAllLines(output, csv, new UTF8Encoding(false));

Console.WriteLine($"[emitter-lab] spell-models={uses.Count} resolved={uses.Count - unresolved.Count} " +
                  $"unresolved={unresolved.Count} emitters={rows.Count} checks={checks}");
foreach (IGrouping<ushort, Row> group in rows.GroupBy(r => r.ShapeId).OrderBy(g => g.Key))
    Console.WriteLine($"[emitter-lab] shape-id={group.Key} parsed={group.First().Shape} count={group.Count()}");
Console.WriteLine($"[emitter-lab] sprite-spin={rows.Count(r => MathF.Abs(r.Spin) > 1e-6f)} " +
                  $"bone-spin={rows.Count(r => r.BoneSpin)} bone-motion={rows.Count(r => r.BoneMotion)} " +
                  $"model-particle={rows.Count(r => r.GeometryModel.Length > 0)} " +
                  $"recursion={rows.Count(r => r.RecursionModel.Length > 0)} " +
                  $"burst={rows.Count(r => (r.Flags & 0x8000) != 0)}");
Console.WriteLine("[emitter-lab] largest plane birth regions:");
foreach (Row row in rows.Where(r => r.Shape == nameof(ParticleShape.Plane))
             .OrderByDescending(r => r.AreaLength * r.AreaWidth).Take(12))
    Console.WriteLine($"  {row.Model} e{row.Emitter} area={row.AreaLength:0.###}x{row.AreaWidth:0.###} " +
                      $"speed={row.Speed:0.###} rate={row.Rate:0.###} life={row.Life:0.###} " +
                      $"boneSpin={row.BoneSpin} flags=0x{row.Flags:X}");
Console.WriteLine("[emitter-lab] representative spin mechanisms:");
foreach (Row row in rows.Where(r => MathF.Abs(r.Spin) > 1e-6f || r.BoneSpin ||
                                    r.AngularMin != Vector3.Zero || r.AngularMax != Vector3.Zero)
             .OrderByDescending(r => r.BoneSpin).ThenByDescending(r => MathF.Abs(r.Spin)).Take(16))
    Console.WriteLine($"  {row.Model} e{row.Emitter} sprite={row.Spin:0.###} bone={row.BoneSpin} " +
                      $"tumble={V(row.AngularMin)}..{V(row.AngularMax)} geometry={row.GeometryModel}");
Console.WriteLine($"[emitter-lab] wrote {output}");
return 0;

static string Q(string value) => '"' + value.Replace("\"", "\"\"") + '"';
static string F(float value) => value.ToString("R", CultureInfo.InvariantCulture);
static string V(Vector3 value) => $"{F(value.X)}|{F(value.Y)}|{F(value.Z)}";
static bool Near(float a, float b) => float.IsFinite(a) && float.IsFinite(b) &&
                                      MathF.Abs(a - b) <= 1e-5f;

internal sealed record Row(string Model, string Uses, int Emitter, ushort ShapeId,
    string Shape, string Texture, byte Blend, uint Flags, Vector3 Position, ushort Bone,
    float Rate, float Life, float Population, float Speed, float SpeedVariation,
    float AreaLength, float AreaWidth, float VerticalRange, float HorizontalRange,
    float Gravity, float ZSource, float Drag, float Spin, bool BoneSpin, bool BoneMotion,
    Vector3 AngularMin, Vector3 AngularMax, string GeometryModel, string RecursionModel,
    byte HeadTail, float MidPoint, int EnableKeys, int AnimatedScalarTracks);
