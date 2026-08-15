using System.Numerics;
using MSUIClient.Formats;

// Dumps UI_MainMenu.m2 (and any extra models named on the command line) to answer
// ONE question: why are the login/char-select cloud sheets frozen?
//
// It prints, side by side:
//   * what M2Reader's VERIFIED pipeline resolves for each batch
//     (b.TextureTransformIndex @ +22 -> TextureTransformLookup -> TextureTransforms),
//   * what GlueScene's OWN raw-byte parse resolves (its 0x74/0xAC parse + the same
//     b.TextureTransformIndex),
// plus every texture transform's translation track (global sequence, key count,
// first/last key). A frozen sheet is either (A) unresolved to any transform, or
// (B) resolved to a track with <2 keys / a zero-duration global sequence.
//
// Pure data: no GL, no window. Usage: glue-cloud-dump <ClientData\Data> [extra.m2 ...]

string dataRoot = args.Length > 0 ? args[0] : FindDefaultData();
var models = args.Length > 1
    ? args[1..]
    : new[]
    {
        @"Interface\Glues\Models\UI_MainMenu\UI_MainMenu.m2",
        @"Interface\Glues\Models\UI_Human\UI_Human.m2",
    };

Console.WriteLine($"[dump] data root: {dataRoot}");
using var mpq = new MpqMount(dataRoot);

foreach (string path in models)
    DumpModel(mpq, path);

return 0;

static string FindDefaultData()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        string cand = Path.Combine(dir.FullName, "GameData", "Data");
        if (Directory.Exists(cand)) return cand;
        dir = dir.Parent;
    }
    return @"GameData\Data";
}

static void DumpModel(MpqMount mpq, string path)
{
    Console.WriteLine();
    Console.WriteLine(new string('=', 78));
    Console.WriteLine($"MODEL  {path}");
    Console.WriteLine(new string('=', 78));

    byte[]? bytes = mpq.ReadFile(path) ?? mpq.ReadFile(Path.ChangeExtension(path, ".mdx"));
    if (bytes is null) { Console.WriteLine("  NOT FOUND in the MPQs"); return; }

    M2Model? model = M2Reader.Parse(bytes);
    if (model is null) { Console.WriteLine("  parse returned null"); return; }
    Console.WriteLine($"  version {model.Version}  verts {model.Vertices.Count}  batches {model.Batches.Count}");

    // ── Global sequences (their durations decide a global-seq track's loop) ──
    Console.WriteLine($"  global sequences ({model.GlobalSequenceDurations.Count}): " +
        string.Join(", ", model.GlobalSequenceDurations.Select((d, i) => $"[{i}]={d}ms")));

    // ── M2Reader's verified texture transforms + lookup ──
    Console.WriteLine($"  M2Reader.TextureTransforms ({model.TextureTransforms.Count}):");
    for (int i = 0; i < model.TextureTransforms.Count; i++)
    {
        var tr = model.TextureTransforms[i].Translation;
        Console.WriteLine($"    xf[{i}] {DescribeTrack(tr)}");
    }
    Console.WriteLine($"  M2Reader.TextureTransformLookup ({model.TextureTransformLookup.Count}): " +
        string.Join(", ", model.TextureTransformLookup));

    // ── GlueScene's OWN raw parse (replicated exactly) ──
    var glueTexAnims = GlueParseTexAnims(bytes);
    var glueLookup = GlueParseLookup(bytes);
    Console.WriteLine($"  [GlueScene raw] _texAnims ({glueTexAnims.Count}), _texAnimLookup " +
        $"({glueLookup.Count}): {string.Join(", ", glueLookup.Select(x => x == 0xFFFF ? "-1" : x.ToString()))}");

    // ── Per-batch comparison ──
    Console.WriteLine("  batches (idx: sm mat blend unlit  +22 +18 | M2Reader->xf  Glue->xf  tex):");
    for (int bi = 0; bi < model.Batches.Count; bi++)
    {
        var b = model.Batches[bi];
        int blend = b.MaterialIndex < model.RenderFlags.Count ? model.RenderFlags[b.MaterialIndex].BlendingMode : -1;
        bool unlit = b.MaterialIndex < model.RenderFlags.Count && model.RenderFlags[b.MaterialIndex].Unlit;

        string tex = "";
        if (b.TextureIndex < model.TextureLookup.Count)
        {
            int t = model.TextureLookup[b.TextureIndex];
            if (t >= 0 && t < model.Textures.Count) tex = model.Textures[t].Filename;
        }

        int m2xf = model.GetTextureTransformForBatch(b);

        // GlueScene resolution: _texAnimLookup[b.TextureTransformIndex] (with the
        // else-branch fallback to the raw index when there is no lookup table).
        int gluexf = -1;
        if (glueLookup.Count > 0 && b.TextureTransformIndex < glueLookup.Count)
        {
            ushort la = glueLookup[b.TextureTransformIndex];
            if (la != 0xFFFF && la < glueTexAnims.Count) gluexf = la;
        }
        else if (b.TextureTransformIndex < glueTexAnims.Count)
        {
            gluexf = b.TextureTransformIndex;
        }

        string flag = (m2xf != gluexf) ? "  <<< MISMATCH" : "";
        bool interesting = blend >= 2 || unlit || m2xf >= 0 || gluexf >= 0;
        if (!interesting) continue;

        // UV span of the batch's submesh: how many times the texture tiles across
        // the sheet decides how visible a 1.0-U scroll actually is on screen.
        string uvspan = "";
        if (b.SubmeshIndex < model.Submeshes.Count)
        {
            var sm = model.Submeshes[b.SubmeshIndex];
            float uMin = float.MaxValue, uMax = float.MinValue, vMin = float.MaxValue, vMax = float.MinValue;
            for (int vi = sm.VertexStart; vi < sm.VertexStart + sm.VertexCount && vi < model.Vertices.Count; vi++)
            {
                var vv = model.Vertices[vi];
                uMin = Math.Min(uMin, vv.TexU); uMax = Math.Max(uMax, vv.TexU);
                vMin = Math.Min(vMin, vv.TexV); vMax = Math.Max(vMax, vv.TexV);
            }
            if (uMin <= uMax) uvspan = $" U[{uMin:0.##}..{uMax:0.##}]={uMax - uMin:0.##}tiles V[{vMin:0.##}..{vMax:0.##}]";
        }

        Console.WriteLine($"    {bi,3}: sm{b.SubmeshIndex} mat{b.MaterialIndex} blend{blend} " +
            $"{(unlit ? "unlit" : "lit  ")}  +22={b.TextureTransformIndex} +18={b.TextureCoordIndex} | " +
            $"M2={m2xf} Glue={gluexf}  '{Short(tex)}'{uvspan}{flag}");
    }
}

static string DescribeTrack(M2AnimTrack<Vector3> tr)
{
    if (tr.Keys.Count == 0) return "EMPTY (0 keys) -> STATIC";
    string first = $"({tr.Keys[0].X:0.###},{tr.Keys[0].Y:0.###})";
    string last = $"({tr.Keys[^1].X:0.###},{tr.Keys[^1].Y:0.###})";
    string times = tr.Timestamps.Count <= 6
        ? string.Join("/", tr.Timestamps)
        : $"{tr.Timestamps[0]}..{tr.Timestamps[^1]}";
    return $"interp={tr.InterpolationType}({(tr.IsLinear ? "linear" : "STEP")}) " +
        $"gseq={tr.GlobalSequence} keys={tr.Keys.Count} t=[{times}]ms  {first}->{last}" +
        (tr.Keys.Count < 2 ? "  <<< SINGLE KEY = FROZEN" : "") +
        (!tr.IsLinear && tr.Keys.Count >= 2 ? "  <<< STEP+2KEY = GlueScene SampleVec3 FREEZES" : "");
}

static string Short(string p) => string.IsNullOrEmpty(p) ? "" : Path.GetFileName(p);

// ── GlueScene's exact raw-byte parse, replicated ─────────────────────────────

static List<(int Gseq, List<uint> Times, List<Vector3> Vals)> GlueParseTexAnims(byte[] b)
{
    var list = new List<(int, List<uint>, List<Vector3>)>();
    if (!U32(b, 0x74, out uint nt) || nt == 0 || nt >= 4096 || !U32(b, 0x78, out uint ot)) return list;
    for (int i = 0; i < nt; i++)
    {
        long to = ot + (long)i * 0x54; // translation track at +0x00
        int gseq = -1; var times = new List<uint>(); var vals = new List<Vector3>();
        if (to + 0x1c <= b.Length)
        {
            gseq = (short)(b[to + 2] | b[to + 3] << 8);
            if (U32(b, to + 0x0c, out uint nT) && U32(b, to + 0x10, out uint oT) &&
                U32(b, to + 0x14, out uint nV) && U32(b, to + 0x18, out uint oV))
            {
                int n = (int)Math.Min(nT, nV);
                for (int k = 0; k < n; k++)
                {
                    if (oT + (long)k * 4 + 4 <= b.Length) times.Add(BitConverter.ToUInt32(b, (int)(oT + k * 4)));
                    long vo = oV + (long)k * 12;
                    if (vo + 12 <= b.Length)
                        vals.Add(new Vector3(BitConverter.ToSingle(b, (int)vo),
                            BitConverter.ToSingle(b, (int)(vo + 4)), BitConverter.ToSingle(b, (int)(vo + 8))));
                }
            }
        }
        list.Add((gseq, times, vals));
    }
    return list;
}

static List<ushort> GlueParseLookup(byte[] b)
{
    var list = new List<ushort>();
    if (U32(b, 0xAC, out uint nl) && nl > 0 && nl < 4096 && U32(b, 0xB0, out uint ol) && ol + nl * 2 <= (uint)b.Length)
        for (int i = 0; i < nl; i++) list.Add((ushort)(b[ol + i * 2] | b[ol + i * 2 + 1] << 8));
    return list;
}

static bool U32(byte[] b, long o, out uint v)
{
    v = 0;
    if (o < 0 || o + 4 > b.Length) return false;
    v = (uint)(b[o] | b[o + 1] << 8 | b[o + 2] << 16 | b[o + 3] << 24);
    return true;
}
