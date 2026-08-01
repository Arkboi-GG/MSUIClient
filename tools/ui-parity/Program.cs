using System.Globalization;
using System.Text;
using System.Xml.Linq;
using MSUIClient.Formats;
using SkiaSharp;

const string DefaultXml = @"Interface\FrameXML\GameMenuFrame.xml";
const string TemplatesXml = @"Interface\FrameXML\UIPanelTemplates.xml";
const string FontsXml = @"Interface\FrameXML\Fonts.xml";

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: ui-parity extract|render|diff|contact ...");
    return 2;
}

try
{
    return args[0].ToLowerInvariant() switch
    {
        "extract" => Extract(args[1..]),
        "render" => Render(args[1..]),
        "diff" => Diff(args[1..]),
        "crop" => Crop(args[1..]),
        "contact" => Contact(args[1..]),
        _ => throw new ArgumentException($"unknown command {args[0]}")
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[ui-parity] FAIL {ex.Message}");
    return 1;
}

static int Extract(string[] args)
{
    var o = Options(args);
    string data = Need(o, "data"), output = Need(o, "out");
    string panel = o.GetValueOrDefault("panel", "game-menu");
    string rootName = o.GetValueOrDefault("root", "GameMenuFrame");
    string xmlPath = o.GetValueOrDefault("xml", DefaultXml);
    using var mpq = new MpqMount(data);
    var documents = new List<(string Path, string Supplier, XDocument Doc)>();
    foreach (string path in new[] { FontsXml, TemplatesXml, xmlPath }.Distinct(StringComparer.OrdinalIgnoreCase))
    {
        var hit = mpq.ReadFileWithSupplier(path) ?? throw new FileNotFoundException(path);
        documents.Add((path, hit.Supplier, XDocument.Parse(Encoding.UTF8.GetString(hit.Data))));
    }

    var named = documents.SelectMany(d => d.Doc.Descendants()
            .Where(e => e.Attribute("name") is not null)
            .Select(e => (Name: (string)e.Attribute("name")!, Element: e, d.Path, d.Supplier)))
        .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
    if (!named.TryGetValue(rootName, out var root)) throw new InvalidDataException($"frame {rootName} not found");

    var rows = new List<Row>();
    AddElement(root.Element, rootName, "", root.Path, root.Supplier, "", "", named, rows, panel);
    Resolve(rows);
    foreach (Row row in rows)
    {
        row.AssetSource = string.Join('|', new[] { row.Texture, row.BgFile, row.EdgeFile }
            .Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => mpq.ReadFileWithSupplier(path) is { } hit ? $"{hit.Supplier}:{path}" : $"MISSING:{path}"));
        if (row.FontPath.Length > 0)
            row.FontSource = mpq.ReadFileWithSupplier(row.FontPath) is { } hit
                ? $"{hit.Supplier}:{row.FontPath}" : $"MISSING:{row.FontPath}";
    }
    WriteRows(output, rows);
    Console.WriteLine($"[ui-parity] extracted {rows.Count} rows for {rootName} from {root.Supplier}:{root.Path}");
    return 0;
}

static void AddElement(XElement element, string instanceName, string parentName, string sourcePath,
    string supplier, string inheritedLayer, string inheritedStrata,
    Dictionary<string, (string Name, XElement Element, string Path, string Supplier)> named,
    List<Row> rows, string panel)
{
    string kind = element.Name.LocalName;
    string inherits = (string?)element.Attribute("inherits") ?? "";
    var chain = new List<(XElement E, string Path, string Supplier)>();
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    void AddInheritance(string names)
    {
        foreach (string name in names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!seen.Add(name) || !named.TryGetValue(name, out var template)) continue;
            AddInheritance((string?)template.Element.Attribute("inherits") ?? "");
            chain.Add((template.Element, template.Path, template.Supplier));
        }
    }
    AddInheritance(inherits);
    chain.Add((element, sourcePath, supplier));

    XElement? size = chain.Select(x => x.E.ElementAny("Size")?.ElementAny("AbsDimension")).LastOrDefault(x => x is not null);
    XElement? anchor = chain.SelectMany(x => x.E.ElementAny("Anchors")?.ElementsAny("Anchor") ?? []).LastOrDefault();
    string layer = inheritedLayer;
    XElement? layerParent = element.Ancestors().FirstOrDefault(x => x.Name.LocalName == "Layer");
    if (layerParent is not null) layer = (string?)layerParent.Attribute("level") ?? layer;
    string strata = chain.Select(x => (string?)x.E.Attribute("frameStrata")).LastOrDefault(x => !string.IsNullOrEmpty(x)) ?? inheritedStrata;
    string texture = TextureOf(chain, named);
    string font = FontOf(chain);
    (string fontPath, string fontSupplier, string fontSource, string fontSize, string color) = ResolveFont(font, named);
    string elementSource = chain.Last().Supplier + ":" + chain.Last().Path;
    XElement? backdrop = chain.Select(x => x.E.ElementAny("Backdrop")).LastOrDefault(x => x is not null);
    XElement? inset = backdrop?.ElementAny("BackgroundInsets")?.ElementAny("AbsInset");
    XElement? tile = backdrop?.ElementAny("TileSize")?.ElementAny("AbsValue");
    XElement? edge = backdrop?.ElementAny("EdgeSize")?.ElementAny("AbsValue");
    XElement? texCoords = chain.Select(x => x.E.ElementAny("TexCoords")).LastOrDefault(x => x is not null);

    var row = new Row
    {
        Panel = panel, Element = instanceName, Type = kind, Parent = parentName,
        Width = A(size, "x"), Height = A(size, "y"), Point = A(anchor, "point"),
        RelativeTo = Expand(A(anchor, "relativeTo"), instanceName, parentName),
        RelativePoint = A(anchor, "relativePoint"), OffsetX = A(anchor?.ElementAny("Offset")?.ElementAny("AbsDimension"), "x"),
        OffsetY = A(anchor?.ElementAny("Offset")?.ElementAny("AbsDimension"), "y"),
        Texture = NormalizeTexture(texture), Font = font, FontPath = fontPath, FontSize = fontSize,
        Color = color, Layer = layer, Strata = strata, Source = elementSource,
        BgFile = NormalizeTexture(A(backdrop, "bgFile")), EdgeFile = NormalizeTexture(A(backdrop, "edgeFile")),
        TileSize = A(tile, "val"), EdgeSize = A(edge, "val"),
        Insets = inset is null ? "" : $"{A(inset,"left")}|{A(inset,"top")}|{A(inset,"right")}|{A(inset,"bottom")}",
        TexCoords = texCoords is null ? "" : $"{A(texCoords,"left")}|{A(texCoords,"top")}|{A(texCoords,"right")}|{A(texCoords,"bottom")}",
        AssetSource = "", FontSource = fontSource.Length == 0 ? fontSupplier : fontSupplier + ":" + fontSource,
    };
    rows.Add(row);

    foreach (XElement child in element.Elements())
    {
        if (child.Name.LocalName == "Frames")
        {
            foreach (XElement nested in child.Elements().Where(IsDrawable)
                .Where(e => !A(e, "hidden").Equals("true", StringComparison.OrdinalIgnoreCase)))
            {
                string rawName = (string?)nested.Attribute("name") ?? $"{instanceName}/{nested.Name.LocalName}";
                string name = Expand(rawName, instanceName, instanceName);
                AddElement(nested, name, instanceName, sourcePath, supplier, layer, strata, named, rows, panel);
            }
        }
        else if (child.Name.LocalName == "Layers")
        {
            foreach (XElement layerNode in child.Elements().Where(x => x.Name.LocalName == "Layer"))
            foreach (XElement nested in layerNode.Elements().Where(IsDrawable)
                .Where(e => !A(e, "hidden").Equals("true", StringComparison.OrdinalIgnoreCase)))
            {
                string rawName = (string?)nested.Attribute("name") ?? $"{instanceName}/{nested.Name.LocalName}";
                string name = Expand(rawName, instanceName, instanceName);
                AddElement(nested, name, instanceName, sourcePath, supplier,
                    A(layerNode, "level"), strata, named, rows, panel);
            }
        }
        else if (IsDrawable(child) && !A(child, "hidden").Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            string rawName = (string?)child.Attribute("name") ?? $"{instanceName}/{child.Name.LocalName}";
            AddElement(child, Expand(rawName, instanceName, instanceName), instanceName, sourcePath, supplier, layer, strata, named, rows, panel);
        }
    }
}

static string TextureOf(List<(XElement E, string Path, string Supplier)> chain,
    Dictionary<string, (string Name, XElement Element, string Path, string Supplier)> named)
{
    string ResolveTexture(XElement node, HashSet<string> seen)
    {
        string file = A(node, "file");
        if (file.Length > 0) return file;
        foreach (string inherited in A(node, "inherits").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (seen.Add(inherited) && named.TryGetValue(inherited, out var def) && ResolveTexture(def.Element, seen) is string resolved && resolved.Length > 0)
                return resolved;
        return "";
    }
    foreach (var item in chain.AsEnumerable().Reverse())
    {
        string direct = ResolveTexture(item.E, new(StringComparer.OrdinalIgnoreCase));
        if (direct.Length > 0) return direct;
        foreach (string childName in new[] { "NormalTexture", "Texture" })
        {
            XElement? child = item.E.ElementAny(childName);
            if (child is null) continue;
            direct = ResolveTexture(child, new(StringComparer.OrdinalIgnoreCase));
            if (direct.Length > 0) return direct;
        }
    }
    return "";
}

static string FontOf(List<(XElement E, string Path, string Supplier)> chain)
{
    foreach (var item in chain.AsEnumerable().Reverse())
    {
        if (item.E.Name.LocalName == "FontString" && A(item.E, "inherits").Length > 0) return A(item.E, "inherits");
        XElement? normal = item.E.ElementAny("NormalFont");
        if (normal is not null && A(normal, "inherits").Length > 0) return A(normal, "inherits");
    }
    return "";
}

static (string Path, string Supplier, string Source, string Size, string Color) ResolveFont(string font,
    Dictionary<string, (string Name, XElement Element, string Path, string Supplier)> named)
{
    if (font.Length == 0 || !named.TryGetValue(font, out var def)) return ("", "", "", "", "");
    var chain = new List<(XElement E,string Path,string Supplier)>();
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    void Add(string name)
    {
        if (!seen.Add(name) || !named.TryGetValue(name, out var f)) return;
        foreach (string parent in A(f.Element,"inherits").Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries)) Add(parent);
        chain.Add((f.Element,f.Path,f.Supplier));
    }
    Add(font);
    XElement? height = chain.Select(x=>x.E.ElementAny("FontHeight")?.ElementAny("AbsValue")).LastOrDefault(x=>x is not null);
    XElement? color = chain.Select(x=>x.E.ElementAny("Color")).LastOrDefault(x=>x is not null);
    string path = chain.Select(x=>A(x.E,"font")).LastOrDefault(x=>x.Length>0) ?? "";
    var source = chain.Last();
    return (path, source.Supplier, source.Path, A(height, "val"), color is null ? "" : ColorHex(color));
}

static void Resolve(List<Row> rows)
{
    var byName = rows.GroupBy(r => r.Element, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
    foreach (Row row in rows)
    {
        if (row.Parent.Length == 0) { row.X = "0"; row.Y = "0"; continue; }
        ResolveOne(row, byName, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }
}

static void ResolveOne(Row row, Dictionary<string, Row> rows, HashSet<string> seen)
{
    if (row.X.Length > 0 || !seen.Add(row.Element)) return;
    string relative = row.RelativeTo.Length > 0 ? row.RelativeTo : row.Parent;
    if (!rows.TryGetValue(relative, out Row? parent)) return;
    ResolveOne(parent, rows, seen);
    if (!F(parent.X, out float px) || !F(parent.Y, out float py) || !F(parent.Width, out float pw) || !F(parent.Height, out float ph) ||
        !F(row.Width, out float w) || !F(row.Height, out float h)) return;
    string point = row.Point.Length == 0 ? "CENTER" : row.Point;
    string relativePoint = row.RelativePoint.Length == 0 ? point : row.RelativePoint;
    (float rx, float ry) = Anchor(px, py, pw, ph, relativePoint);
    (float sx, float sy) = Anchor(0, 0, w, h, point);
    F(row.OffsetX, out float ox); F(row.OffsetY, out float oy);
    row.X = N(rx - sx + ox); row.Y = N(ry - sy - oy);
}

static (float X, float Y) Anchor(float x, float y, float w, float h, string point) => point.ToUpperInvariant() switch
{
    "TOPLEFT" => (x, y), "TOP" => (x + w / 2, y), "TOPRIGHT" => (x + w, y),
    "LEFT" => (x, y + h / 2), "RIGHT" => (x + w, y + h / 2),
    "BOTTOMLEFT" => (x, y + h), "BOTTOM" => (x + w / 2, y + h), "BOTTOMRIGHT" => (x + w, y + h),
    _ => (x + w / 2, y + h / 2),
};

static int Render(string[] args)
{
    var o = Options(args); string data = Need(o, "data"), spec = Need(o, "spec"), output = Need(o, "out");
    List<Row> rows = ReadRows(spec); Row root = rows[0];
    var placed = rows.Where(r => F(r.X,out _) && F(r.Y,out _) && F(r.Width,out _) && F(r.Height,out _)).ToArray();
    float minX=Math.Min(0,placed.Min(r=>Parse(r.X,0))), minY=Math.Min(0,placed.Min(r=>Parse(r.Y,0)));
    float maxX=Math.Max(Parse(root.Width,256),placed.Max(r=>Parse(r.X,0)+Parse(r.Width,0)));
    float maxY=Math.Max(Parse(root.Height,256),placed.Max(r=>Parse(r.Y,0)+Parse(r.Height,0)));
    int width = (int)MathF.Ceiling(maxX-minX), height = (int)MathF.Ceiling(maxY-minY);
    using var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
    using var canvas = new SKCanvas(bitmap); canvas.Clear(new SKColor(32, 36, 40));
    using var mpq = new MpqMount(data);
    var cache = new Dictionary<string, SKBitmap?>(StringComparer.OrdinalIgnoreCase);
    SKBitmap? Asset(string path)
    {
        if (path.Length == 0) return null;
        if (cache.TryGetValue(path, out var cached)) return cached;
        var hit = mpq.ReadFileWithSupplier(path);
        if (hit is null) return cache[path] = null;
        byte[] bgra = BlpDecoder.GetPixels(hit.Value.Data, 0, out int w, out int h);
        var b = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        System.Runtime.InteropServices.Marshal.Copy(bgra, 0, b.GetPixels(), bgra.Length);
        return cache[path] = b;
    }
    foreach (Row row in rows)
    {
        if (!F(row.X, out float x) || !F(row.Y, out float y) || !F(row.Width, out float w) || !F(row.Height, out float h)) continue;
        x-=minX; y-=minY;
        if (row.BgFile.Length > 0 && Asset(row.BgFile) is SKBitmap bg)
            canvas.DrawBitmap(bg, new SKRect(x, y, x + w, y + h));
        if (row.EdgeFile.Length > 0 && Asset(row.EdgeFile) is SKBitmap edge)
            canvas.DrawBitmap(edge, new SKRect(x, y, x + w, y + h));
        if (row.Texture.Length > 0 && Asset(row.Texture) is SKBitmap art)
        {
            SKRect src = new(0, 0, art.Width, art.Height);
            string[] tc = row.TexCoords.Split('|');
            if (tc.Length == 4 && tc.All(v => F(v, out _)))
                src = new(Parse(tc[0],0)*art.Width, Parse(tc[1],0)*art.Height, Parse(tc[2],1)*art.Width, Parse(tc[3],1)*art.Height);
            canvas.DrawBitmap(art, src, new SKRect(x, y, x + w, y + h));
        }
    }
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
    using SKData png = bitmap.Encode(SKEncodedImageFormat.Png, 95); using FileStream fs = File.Create(output); png.SaveTo(fs);
    foreach (SKBitmap? b in cache.Values) b?.Dispose();
    Console.WriteLine($"[ui-parity] rendered {output} with original MPQ assets ({width}x{height})");
    return 0;
}

static int Crop(string[] args)
{
    var o=Options(args); string input=Need(o,"image"), spec=Need(o,"spec"), output=Need(o,"out");
    float scale=Parse(o.GetValueOrDefault("scale","1"),1); List<Row> rows=ReadRows(spec); Row root=rows[0];
    var placed=rows.Where(r=>F(r.X,out _)&&F(r.Y,out _)&&F(r.Width,out _)&&F(r.Height,out _)).ToArray();
    float minX=Math.Min(0,placed.Min(r=>Parse(r.X,0))),minY=Math.Min(0,placed.Min(r=>Parse(r.Y,0)));
    float maxX=Math.Max(Parse(root.Width,256),placed.Max(r=>Parse(r.X,0)+Parse(r.Width,0)));
    float maxY=Math.Max(Parse(root.Height,256),placed.Max(r=>Parse(r.Y,0)+Parse(r.Height,0)));
    using SKBitmap source=SKBitmap.Decode(input)??throw new InvalidDataException(input);
    float rootLeft=o.TryGetValue("left",out string? left) ? Parse(left,0)*scale : (source.Width-Parse(root.Width,256)*scale)/2f;
    float rootTop=o.TryGetValue("top",out string? top) ? Parse(top,0)*scale : (source.Height-Parse(root.Height,256)*scale)/2f;
    var src=new SKRect(rootLeft+minX*scale,rootTop+minY*scale,rootLeft+maxX*scale,rootTop+maxY*scale);
    int w=(int)MathF.Ceiling(maxX-minX),h=(int)MathF.Ceiling(maxY-minY);
    using var cropped=new SKBitmap(w,h,SKColorType.Rgba8888,SKAlphaType.Premul); using var c=new SKCanvas(cropped);
    c.DrawBitmap(source,src,new SKRect(0,0,w,h)); using SKData png=cropped.Encode(SKEncodedImageFormat.Png,95); using FileStream fs=File.Create(output);png.SaveTo(fs);
    Console.WriteLine($"[ui-parity] cropped actual panel {output} ({w}x{h})"); return 0;
}

static int Diff(string[] args)
{
    var o = Options(args); string expectedPath = Need(o, "expected"), actualPath = Need(o, "actual"), output = Need(o, "out");
    List<Row> expected = ReadRows(expectedPath), actual = ReadRows(actualPath);
    var e = expected.ToDictionary(r => r.Element, StringComparer.OrdinalIgnoreCase);
    var a = actual.ToDictionary(r => r.Element, StringComparer.OrdinalIgnoreCase);
    var lines = new List<string> { "panel,element,field,expected,actual,verdict" };
    int deltas = 0;
    foreach (string name in e.Keys.Union(a.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x))
    {
        e.TryGetValue(name, out Row? er); a.TryGetValue(name, out Row? ar);
        Add("presence", er is null ? "ABSENT" : "PRESENT", ar is null ? "ABSENT" : "PRESENT");
        if (er is null || ar is null) continue;
        Add("geometry", $"{er.X}|{er.Y}|{er.Width}|{er.Height}", $"{ar.X}|{ar.Y}|{ar.Width}|{ar.Height}");
        Add("anchor", $"{er.Point}|{er.RelativeTo}|{er.RelativePoint}|{er.OffsetX}|{er.OffsetY}", $"{ar.Point}|{ar.RelativeTo}|{ar.RelativePoint}|{ar.OffsetX}|{ar.OffsetY}");
        Add("texture-path", $"{er.Texture}|{er.BgFile}|{er.EdgeFile}", $"{ar.Texture}|{ar.BgFile}|{ar.EdgeFile}");
        Add("font", $"{er.Font}|{er.FontSize}", $"{ar.Font}|{ar.FontSize}"); Add("color", er.Color, ar.Color); Add("layer", $"{er.Strata}|{er.Layer}", $"{ar.Strata}|{ar.Layer}");
        void Add(string field, string expectedValue, string actualValue)
        {
            bool pass = string.Equals(expectedValue, actualValue, StringComparison.OrdinalIgnoreCase);
            if (!pass) deltas++;
            lines.Add(Csv((er ?? ar)!.Panel, name, field, expectedValue, actualValue, pass ? "PASS" : "DELTA"));
        }
    }
    File.WriteAllLines(output, lines);
    Console.WriteLine($"[ui-parity] diff {deltas} mechanical delta(s) across {lines.Count - 1} verdict rows");
    return deltas == 0 ? 0 : 3;
}

static int Contact(string[] args)
{
    var o = Options(args); string reference = Need(o, "reference"), actual = Need(o, "actual"), output = Need(o, "out");
    using SKBitmap left = SKBitmap.Decode(reference) ?? throw new InvalidDataException(reference);
    using SKBitmap right = SKBitmap.Decode(actual) ?? throw new InvalidDataException(actual);
    const int label = 38, gap = 8; int h = Math.Max(left.Height, right.Height);
    using var sheet = new SKBitmap(left.Width + right.Width + gap, h + label, SKColorType.Rgba8888, SKAlphaType.Premul);
    using var c = new SKCanvas(sheet); c.Clear(new SKColor(18,18,20)); c.DrawBitmap(left, 0, label); c.DrawBitmap(right, left.Width + gap, label);
    using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true }; using var font = new SKFont(SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold), 18);
    c.DrawText("VANILLA 1.12 REFERENCE", 8, 25, SKTextAlign.Left, font, paint); c.DrawText("MSUI ACTUAL", left.Width + gap + 8, 25, SKTextAlign.Left, font, paint);
    using SKData png = sheet.Encode(SKEncodedImageFormat.Png, 95); using FileStream fs = File.Create(output); png.SaveTo(fs);
    Console.WriteLine($"[ui-parity] contact sheet {output}"); return 0;
}

static bool IsDrawable(XElement e) => e.Name.LocalName is "Frame" or "Button" or "CheckButton" or "Texture" or "FontString";
static string A(XElement? e, string name) => (string?)e?.Attribute(name) ?? "";
static string Expand(string value, string instance, string parent) => value.Replace("$parent", parent.Length > 0 ? parent : instance, StringComparison.OrdinalIgnoreCase);
static string NormalizeTexture(string value) => value.Length == 0 ? "" : value.EndsWith(".blp", StringComparison.OrdinalIgnoreCase) ? value : value + ".blp";
static string ColorHex(XElement e) => $"#{Byte(A(e,"r")):X2}{Byte(A(e,"g")):X2}{Byte(A(e,"b")):X2}{Byte(A(e,"a"),1):X2}";
static int Byte(string s, float fallback=0) => (int)MathF.Round(Math.Clamp(Parse(s, fallback),0,1)*255);
static string N(float f) => f.ToString("0.###", CultureInfo.InvariantCulture);
static bool F(string s, out float value) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
static float Parse(string s, float fallback) => F(s, out float v) ? v : fallback;
static string Need(Dictionary<string,string> o, string key) => o.TryGetValue(key, out string? v) ? v : throw new ArgumentException($"--{key} required");
static Dictionary<string,string> Options(string[] args)
{
    var o = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
    for (int i=0;i<args.Length;i++) { if (!args[i].StartsWith("--") || i+1>=args.Length) throw new ArgumentException($"bad option {args[i]}"); o[args[i][2..]]=args[++i]; }
    return o;
}
static string Csv(params string[] values) => string.Join(',', values.Select(v => '"' + v.Replace("\"", "\"\"") + '"'));

static void WriteRows(string path, IEnumerable<Row> rows)
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    using var w = new StreamWriter(path, false, new UTF8Encoding(false)); w.WriteLine(Row.Header);
    foreach (Row row in rows) w.WriteLine(row.ToCsv());
}
static List<Row> ReadRows(string path) => File.ReadLines(path).Skip(1).Where(x => x.Length > 0).Select(Row.FromCsv).ToList();

sealed class Row
{
    public const string Header = "panel,element,type,parent,x,y,width,height,point,relativeTo,relativePoint,offsetX,offsetY,texture,font,fontPath,fontSize,color,layer,strata,bgFile,edgeFile,tileSize,edgeSize,insets,texCoords,source,assetSource,fontSource";
    public string Panel="",Element="",Type="",Parent="",X="",Y="",Width="",Height="",Point="",RelativeTo="",RelativePoint="",OffsetX="",OffsetY="",Texture="",Font="",FontPath="",FontSize="",Color="",Layer="",Strata="",BgFile="",EdgeFile="",TileSize="",EdgeSize="",Insets="",TexCoords="",Source="",AssetSource="",FontSource="";
    public string ToCsv() => Join(Panel,Element,Type,Parent,X,Y,Width,Height,Point,RelativeTo,RelativePoint,OffsetX,OffsetY,Texture,Font,FontPath,FontSize,Color,Layer,Strata,BgFile,EdgeFile,TileSize,EdgeSize,Insets,TexCoords,Source,AssetSource,FontSource);
    public static Row FromCsv(string line)
    {
        string[] v=ParseCsv(line).ToArray(); if(v.Length!=29) throw new InvalidDataException($"expected 29 columns, got {v.Length}");
        return new Row{Panel=v[0],Element=v[1],Type=v[2],Parent=v[3],X=v[4],Y=v[5],Width=v[6],Height=v[7],Point=v[8],RelativeTo=v[9],RelativePoint=v[10],OffsetX=v[11],OffsetY=v[12],Texture=v[13],Font=v[14],FontPath=v[15],FontSize=v[16],Color=v[17],Layer=v[18],Strata=v[19],BgFile=v[20],EdgeFile=v[21],TileSize=v[22],EdgeSize=v[23],Insets=v[24],TexCoords=v[25],Source=v[26],AssetSource=v[27],FontSource=v[28]};
    }
    static IEnumerable<string> ParseCsv(string line)
    {
        var b=new StringBuilder(); bool quoted=false;
        for(int i=0;i<line.Length;i++){char c=line[i];if(c=='"'){if(quoted&&i+1<line.Length&&line[i+1]=='"'){b.Append('"');i++;}else quoted=!quoted;}else if(c==','&&!quoted){yield return b.ToString();b.Clear();}else b.Append(c);}yield return b.ToString();
    }
    static string Join(params string[] values) => string.Join(',', values.Select(v => '"' + v.Replace("\"", "\"\"") + '"'));
}

static class XmlExtensions
{
    public static XElement? ElementAny(this XElement e,string name)=>e.Elements().FirstOrDefault(x=>x.Name.LocalName==name);
    public static IEnumerable<XElement> ElementsAny(this XElement e,string name)=>e.Elements().Where(x=>x.Name.LocalName==name);
}
