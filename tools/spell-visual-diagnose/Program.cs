using System.Text;
using MSUIClient;
using MSUIClient.Formats;

if (args.Length != 3) { Console.Error.WriteLine("usage: spell-visual-diagnose <config> <expected.csv> <output.csv>"); return 2; }
ClientConfig config = ClientConfig.Load(args[0]);
using var mpq = new MpqMount(config.ClientDataPath);
var paths = File.ReadLines(args[1]).Skip(1).SelectMany(Parse).Where(path => path.Length > 0)
    .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path).ToArray();
var lines = new List<string> { "path,mpq_status,supplier,bytes,parse_status,valid_mesh,vertices,indices,emitters,batches,textures,diagnosis" };
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
        !model.IsValid && model.ParticleEmitters.Count == 0 ? "PARSED-NO-DRAWABLE" : "DRAWABLE";
    lines.Add(string.Join(',', Q(path), file is null ? "MISSING" : "PRESENT", Q(file?.Supplier ?? ""),
        file?.Data.Length ?? 0, Q(parse), model?.IsValid ?? false, model?.Vertices.Count ?? 0,
        model?.Indices.Count ?? 0, model?.ParticleEmitters.Count ?? 0, model?.Batches.Count ?? 0,
        model?.Textures.Count ?? 0, diagnosis));
}
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[2]))!);
File.WriteAllLines(args[2], lines, new UTF8Encoding(false));
foreach (var group in lines.Skip(1).Select(line => line.Split(',')[^1]).GroupBy(x => x))
    Console.WriteLine($"[spell-visual-diagnose] {group.Key}={group.Count()}");
return 0;

static IEnumerable<string> Parse(string line)
{
    foreach (string token in line.Split(','))
        foreach (string part in token.Split('|'))
        {
            int marker = part.IndexOf(":Spells\\", StringComparison.OrdinalIgnoreCase);
            if (marker >= 0) yield return part[(marker + 1)..].Trim('"');
        }
}
static string Q(string value) => '"' + value.Replace("\"", "\"\"") + '"';
