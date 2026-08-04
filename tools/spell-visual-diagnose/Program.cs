using System.Text;
using MSUIClient;
using MSUIClient.Formats;
using MSUIClient.World.Units;
using System.Numerics;

if (args.Length != 3) { Console.Error.WriteLine("usage: spell-visual-diagnose <config> <expected.csv|--scan-recursion> <output.csv>"); return 2; }
ClientConfig config = ClientConfig.Load(args[0]);
using var mpq = new MpqMount(config.ClientDataPath);
if (args[1].Equals("--scan-recursion", StringComparison.OrdinalIgnoreCase))
    return ScanRecursion(config, mpq, args[2]);
var paths = File.ReadLines(args[1]).Skip(1).SelectMany(Parse).Where(path => path.Length > 0)
    .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path).ToArray();
var lines = new List<string> { "path,mpq_status,supplier,bytes,parse_status,valid_mesh,vertices,indices,emitters,geometry_emitters,recursion_emitters,recursion_models,recursion_models_resolved,ribbons,ribbon_height_peak,events,release_events,colors,alpha_tracks,batches,textures,diagnosis" };
foreach (string path in paths)
{
    var file = mpq.ReadFileWithSupplier(path);
    M2Model? model = null; string parse = "NOT_ATTEMPTED", diagnosis;
    if (file is not null)
    {
        try { model = M2Reader.Parse(file.Value.Data); parse = model is null ? "NULL" : "PARSED"; }
        catch (Exception ex) { parse = "EXCEPTION:" + ex.GetType().Name; }
    }
    diagnosis = file is null ? "ASSET-MISSING" : model is null ? "LOAD-PARSE-FAILED" :
        !model.HasRenderableContent ? "PARSED-NO-DRAWABLE" : "DRAWABLE";
    string[] recursionModels = model?.ParticleEmitters
        .Select(e => e.RecursionModel).Where(p => p.Length > 0)
        .Select(SpellVisualCatalog.ModelPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        ?? [];
    int recursionResolved = recursionModels.Count(p => mpq.ReadFile(p) is { } bytes &&
        M2Reader.Parse(bytes) is { ParticleEmitters.Count: > 0 });
    lines.Add(string.Join(',', Q(path), file is null ? "MISSING" : "PRESENT", Q(file?.Supplier ?? ""),
        file?.Data.Length ?? 0, Q(parse), model?.IsValid ?? false, model?.Vertices.Count ?? 0,
        model?.Indices.Count ?? 0, model?.ParticleEmitters.Count ?? 0,
        model?.ParticleEmitters.Count(e => e.GeometryModel.Length > 0) ?? 0,
        model?.ParticleEmitters.Count(e => e.RecursionModel.Length > 0) ?? 0,
        Q(string.Join('|', recursionModels)), recursionResolved,
        model?.RibbonEmitters.Count ?? 0,
        model?.RibbonEmitters.SelectMany(r => r.HeightAbove.Keys.Concat(r.HeightBelow.Keys)).DefaultIfEmpty().Max() ?? 0,
        model?.Events.Count ?? 0,
        model?.Events.Count(e => e.Identifier is "$CSL" or "$CSR" or "$CST" or "$BWR") ?? 0,
        model?.Colors.Count ?? 0,
        model?.TransparencyTracks.Count ?? 0, model?.Batches.Count ?? 0,
        model?.Textures.Count ?? 0, diagnosis));
}
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[2]))!);
File.WriteAllLines(args[2], lines, new UTF8Encoding(false));
foreach (var group in lines.Skip(1).Select(line => line.Split(',')[^1]).GroupBy(x => x))
    Console.WriteLine($"[spell-visual-diagnose] {group.Key}={group.Count()}");
RunPureSelfTests();
return 0;

void RunPureSelfTests()
{
    int checks = 0;
    void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        checks++;
    }

    SpellVisualCatalog catalog = SpellVisualCatalog.Load(mpq)
        ?? throw new InvalidOperationException("SpellVisual catalog unavailable");
    Check(catalog.TryGetStages(67, out SpellVisualStages fireball), "Fireball visual missing");
    Check(fireball.Precast == 30 && fireball.Cast == 38 && fireball.Impact == 286,
        "Fireball kit chain differs from 30/38/286");
    Check(catalog.TryGetKit(fireball.Precast, out SpellVisualKitInfo precast) &&
          precast.Effects.Select(e => e.AttachmentId).SequenceEqual(new ushort[] { 0x15, 0x16 }),
        "Fireball precast is not attached to both hands");
    Check(catalog.MissilePath(fireball) == @"Spells\Fireball_Missile_Low.m2" &&
          fireball.MissileAttachment == 0x22, "Fireball missile chain differs");

    var attachmentModel = new M2Model
    {
        Attachments = [new M2Attachment { Id = 0x0F, BoneIndex = 0, Position = new Vector3(1, 2, 3) }],
        Bones = [new M2Bone { Pivot = new Vector3(4, 0, 0) }],
    };
    SpellAttachment.Point fallback = SpellAttachment.Resolve(attachmentModel, 0x15)
        ?? throw new InvalidOperationException("attachment fallback missing");
    Check(fallback.ResolvedId == 0x0F && fallback.WasFallback, "attachment fallback differs");
    Matrix4x4 attached = SpellAttachment.World(attachmentModel, fallback,
        Matrix4x4.CreateTranslation(10, 20, 30), _ => Matrix4x4.CreateTranslation(5, 0, 0));
    // The accessor is T(pivot)·Skin (translation 5 here, with pivot 4), so the attachment's
    // model-space position must cancel that pivot and land at 1 + (5-4) + unit 10 = 12.
    Check(new Vector3(attached.M41, attached.M42, attached.M43) == new Vector3(12, 22, 33),
        "live bone attachment transform differs");

    var synthetic = new M2Model
    {
        Sequences = [new M2Sequence { AnimationId = 0, StartTimestamp = 1000, EndTimestamp = 2000 }],
    };
    var track = new M2AnimTrack<float>
    {
        InterpolationType = 1,
        Ranges = [new AnimationRange { Start = 0, End = 1 }],
        Timestamps = [1000, 2000], Keys = [0f, 10f],
    };
    Check(MathF.Abs(M2TrackSampling.Float(track, synthetic, 0, .5f) - 5f) < .001f,
        "instance-clock interpolation differs");
    Console.WriteLine($"[spell-visual-diagnose] SELFTEST={checks} passed");
}

static IEnumerable<string> Parse(string line)
{
    foreach (string token in line.Split(','))
        foreach (string part in token.Split('|'))
        {
            string value = part.Trim('"', ' ');
            if (value.EndsWith(".m2", StringComparison.OrdinalIgnoreCase) ||
                value.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase) ||
                value.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
                yield return SpellVisualCatalog.ModelPath(value);
        }
}
static string Q(string value) => '"' + value.Replace("\"", "\"\"") + '"';

static int ScanRecursion(ClientConfig config, MpqMount mpq, string output)
{
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
            if (path.EndsWith(".m2", StringComparison.OrdinalIgnoreCase) &&
                (path.StartsWith("Spells\\", StringComparison.OrdinalIgnoreCase) ||
                 path.StartsWith("Particles\\", StringComparison.OrdinalIgnoreCase)))
                paths.Add(path);
        }
    }

    var lines = new List<string>
    {
        "path,supplier,emitter,geometry_model,geometry_resolved,geometry_valid_mesh,recursion_model,recursion_resolved,child_emitters,eligible_child_emitters"
    };
    foreach (string path in paths.OrderBy(p => p))
    {
        var file = mpq.ReadFileWithSupplier(path);
        M2Model? model = file is null ? null : M2Reader.Parse(file.Value.Data);
        if (model is null) continue;
        for (int i = 0; i < model.ParticleEmitters.Count; i++)
        {
            M2ParticleEmitter emitter = model.ParticleEmitters[i];
            if (emitter.GeometryModel.Length == 0 && emitter.RecursionModel.Length == 0) continue;
            string geometry = emitter.GeometryModel.Length == 0 ? "" :
                SpellVisualCatalog.ModelPath(emitter.GeometryModel);
            byte[]? geometryBytes = geometry.Length == 0 ? null : mpq.ReadFile(geometry);
            M2Model? geometryModel = geometryBytes is null ? null : M2Reader.Parse(geometryBytes);
            string recursion = emitter.RecursionModel.Length == 0 ? "" :
                SpellVisualCatalog.ModelPath(emitter.RecursionModel);
            byte[]? childBytes = recursion.Length == 0 ? null : mpq.ReadFile(recursion);
            M2Model? child = childBytes is null ? null : M2Reader.Parse(childBytes);
            int eligibleChildren = child?.ParticleEmitters.Take(4).Count(e =>
                e.Texture < child.Textures.Count && child.Textures[e.Texture].Filename.Length > 0 &&
                e.Lifespan > 0f && e.ScalarTracks[6].Keys.Any(v => v > 0f)) ?? 0;
            lines.Add(string.Join(',', Q(path), Q(file?.Supplier ?? ""), i,
                Q(geometry), geometryModel is not null, geometryModel?.IsValid ?? false,
                Q(recursion), child is not null,
                child?.ParticleEmitters.Count ?? 0, eligibleChildren));
        }
    }
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
    File.WriteAllLines(output, lines, new UTF8Encoding(false));
    Console.WriteLine($"[spell-visual-diagnose] scanned={paths.Count} special_emitters={lines.Count - 1}");
    return 0;
}
