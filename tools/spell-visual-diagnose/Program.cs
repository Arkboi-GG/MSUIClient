using System.Text;
using MSUIClient;
using MSUIClient.Formats;
using MSUIClient.World.Units;
using System.Numerics;

if (args.Length != 3) { Console.Error.WriteLine("usage: spell-visual-diagnose <config> <expected.csv> <output.csv>"); return 2; }
ClientConfig config = ClientConfig.Load(args[0]);
using var mpq = new MpqMount(config.ClientDataPath);
var paths = File.ReadLines(args[1]).Skip(1).SelectMany(Parse).Where(path => path.Length > 0)
    .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path).ToArray();
var lines = new List<string> { "path,mpq_status,supplier,bytes,parse_status,valid_mesh,vertices,indices,emitters,ribbons,ribbon_height_peak,events,release_events,colors,alpha_tracks,batches,textures,diagnosis" };
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
    lines.Add(string.Join(',', Q(path), file is null ? "MISSING" : "PRESENT", Q(file?.Supplier ?? ""),
        file?.Data.Length ?? 0, Q(parse), model?.IsValid ?? false, model?.Vertices.Count ?? 0,
        model?.Indices.Count ?? 0, model?.ParticleEmitters.Count ?? 0,
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
    Check(new Vector3(attached.M41, attached.M42, attached.M43) == new Vector3(16, 22, 33),
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
