using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using MSUIClient.Formats;
using SkiaSharp;

const string DefaultXml = @"Interface\FrameXML\GameMenuFrame.xml";
const string TemplatesXml = @"Interface\FrameXML\UIPanelTemplates.xml";
const string FontsXml = @"Interface\FrameXML\Fonts.xml";
const string ActionButtonTemplatesXml = @"Interface\FrameXML\ActionButtonTemplate.xml";
const string ActionBarFrameXml = @"Interface\FrameXML\ActionBarFrame.xml";
const string ChatFrameXml = @"Interface\FrameXML\ChatFrame.xml";

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: ui-parity source|extract|render|diff|crop|contact|containment|self-test ...");
    return 2;
}

try
{
    return args[0].ToLowerInvariant() switch
    {
        "source" => Source(args[1..]),
        "extract" => Extract(args[1..]),
        "render" => Render(args[1..]),
        "diff" => Diff(args[1..]),
        "crop" => Crop(args[1..]),
        "contact" => Contact(args[1..]),
        "containment" => Containment(args[1..]),
        "self-test" => SelfTest(),
        _ => throw new ArgumentException($"unknown command {args[0]}")
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[ui-parity] FAIL {ex.Message}");
    return 1;
}

static int SelfTest()
{
    if (!IsDrawable(XElement.Parse("<ScrollFrame />")))
        throw new InvalidDataException("ScrollFrame nodes were dropped from authored extraction");
    string root = Path.Combine(Path.GetTempPath(), "MSUIClient", "ui-parity-self-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var expectedRow = new Row
        {
            Panel = "test", Element = "TestButton", Type = "Button", Parent = "TestFrame",
            X = "1", Y = "2", Width = "30", Height = "40", Point = "TOPLEFT",
            RelativeTo = "TestFrame", RelativePoint = "TOPLEFT", OffsetX = "1", OffsetY = "-2",
            Texture = @"Interface\Buttons\Test.blp", TexCoords = "0.25|0.25|0.75|0.75",
            Layer = "ARTWORK", Strata = "DIALOG", ContentRect = "1|2|31|42",
            ClipRect = "0|0|100|100", ClipMask = "round-test", DrawIndex = "1",
            BlendMode = "BLEND", Visible = "true", Enabled = "true", InteractionState = "NORMAL",
            HitRect = "1|2|31|42", AssetAvailability = "PRESENT", Coverage = "DRAWN-INSTRUMENTED"
        };
        Row roundTrip = Row.FromCsv(expectedRow.ToCsv());
        if (roundTrip.TexCoords != expectedRow.TexCoords || roundTrip.ClipMask != expectedRow.ClipMask ||
            roundTrip.HitRect != expectedRow.HitRect || roundTrip.DrawIndex != expectedRow.DrawIndex)
            throw new InvalidDataException("extended CSV round trip lost telemetry");

        string expected = Path.Combine(root, "expected.csv");
        string actual = Path.Combine(root, "actual.csv");
        string selection = Path.Combine(root, "selection.txt");
        string diff = Path.Combine(root, "diff.csv");
        WriteRows(expected, [expectedRow]);
        WriteRows(actual, [roundTrip]);
        File.WriteAllText(selection, "scope=all-reference-elements\n");
        if (Diff(["--expected", expected, "--actual", actual, "--selection", selection, "--out", diff]) != 0)
            throw new InvalidDataException("identical extended rows did not pass");

        roundTrip.HitRect = "2|2|31|42";
        WriteRows(actual, [roundTrip]);
        if (Diff(["--expected", expected, "--actual", actual, "--selection", selection, "--out", diff]) != 3 ||
            !File.ReadAllText(diff).Contains(",\"hit-rect\",", StringComparison.Ordinal))
            throw new InvalidDataException("hit-rectangle regression was not detected");
        string adjudications = Path.Combine(root, "adjudications.csv");
        File.WriteAllLines(adjudications,
        [
            "element,field,expected,actual,decisionId,reason",
            Csv("TestButton", "hit-rect", "1|2|31|42", "2|2|31|42",
                "decision-self-test", "intentional fixture difference")
        ]);
        if (Diff(["--expected", expected, "--actual", actual, "--selection", selection,
                "--adjudications", adjudications, "--out", diff]) != 0 ||
            !File.ReadAllText(diff).Contains("PRESERVED-DIFFERENCE", StringComparison.Ordinal))
            throw new InvalidDataException("an exact field-level preservation decision did not pass");
        roundTrip.HitRect = expectedRow.HitRect;
        WriteRows(actual, [roundTrip]);
        bool staleRejected = false;
        try
        {
            Diff(["--expected", expected, "--actual", actual, "--selection", selection,
                "--adjudications", adjudications, "--out", diff]);
        }
        catch (InvalidDataException ex) when (ex.Message.Contains("unused/stale", StringComparison.Ordinal))
        {
            staleRejected = true;
        }
        if (!staleRejected) throw new InvalidDataException("a stale preservation decision was accepted");

        var expectedHidden = new Row
        {
            Panel = "test", Element = "EmptyItemIcon", Type = "Texture", Parent = "TestFrame",
            Visible = "false", AssetAvailability = "NOT_APPLICABLE", Coverage = "NOT-DRAWN"
        };
        var actualHidden = Row.FromCsv(expectedHidden.ToCsv());
        WriteRows(expected, [expectedHidden]);
        WriteRows(actual, [actualHidden]);
        if (Diff(["--expected", expected, "--actual", actual, "--selection", selection, "--out", diff]) != 0)
            throw new InvalidDataException("an explicitly instrumented expected NOT-DRAWN row did not pass");
        WriteRows(actual, []);
        if (Diff(["--expected", expected, "--actual", actual, "--selection", selection, "--out", diff]) != 3)
            throw new InvalidDataException("a missing NOT-DRAWN instrumentation row was accepted");
        actualHidden.Coverage = "DRAWN-INSTRUMENTED";
        actualHidden.Visible = "true";
        WriteRows(actual, [actualHidden]);
        if (Diff(["--expected", expected, "--actual", actual, "--selection", selection, "--out", diff]) != 3)
            throw new InvalidDataException("a drawn row passed an expected NOT-DRAWN contract");

        string sourceArchive = Path.Combine(root, "frozen-source.zip");
        using (ZipArchive archive = ZipFile.Open(sourceArchive, ZipArchiveMode.Create))
        {
            void Entry(string name, string xml)
            {
                using StreamWriter writer = new(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
                writer.Write(xml);
            }
            Entry("ui/Fonts.xml", "<Ui><Font name=\"ReferenceFont\" font=\"Fonts\\\\FRIZQT__.TTF\" /></Ui>");
            Entry("ui/Panel.xml", "<Ui><Include file=\"Extra.xml\"/><Frame name=\"FrozenPanel\"/></Ui>");
            Entry("ui/Extra.xml", "<Ui><Frame name=\"FrozenTemplate\" virtual=\"true\"/></Ui>");
        }
        List<(string Path, string Supplier, XDocument Doc)> frozen =
            LoadSnapshotDocuments(sourceArchive, "ui/Panel.xml", "Fonts.xml");
        if (frozen.Count != 3 || frozen.All(x => x.Path != "ui/Extra.xml") ||
            frozen.Any(x => !x.Supplier.StartsWith("snapshot-zip:", StringComparison.Ordinal)))
            throw new InvalidDataException("frozen snapshot ZIP extraction lost dependency/include provenance");

        string hidden = Path.Combine(root, "hidden.png"), visible = Path.Combine(root, "visible.png");
        using (var hiddenBitmap = new SKBitmap(8, 8, SKColorType.Rgba8888, SKAlphaType.Premul))
        using (var visibleBitmap = new SKBitmap(8, 8, SKColorType.Rgba8888, SKAlphaType.Premul))
        {
            hiddenBitmap.Erase(SKColors.Black); visibleBitmap.Erase(SKColors.Black);
            visibleBitmap.SetPixel(4, 4, SKColors.Green);
            SavePng(hiddenBitmap, hidden); SavePng(visibleBitmap, visible);
        }
        string containment = Path.Combine(root, "containment.json");
        string[] containmentArgs = ["--visible", visible, "--hidden", hidden, "--out", containment,
            "--left", "2", "--top", "2", "--right", "6", "--bottom", "6", "--shape", "ellipse"];
        if (Containment(containmentArgs) != 0) throw new InvalidDataException("contained pixels did not pass");
        using (SKBitmap leak = SKBitmap.Decode(visible) ?? throw new InvalidDataException(visible))
        {
            leak.SetPixel(0, 0, SKColors.Green); SavePng(leak, visible);
        }
        if (Containment(containmentArgs) != 3) throw new InvalidDataException("outside-aperture leak was not detected");
        Console.WriteLine("[ui-parity] self-test PASS (extended schema and strict diff)");
        return 0;
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}

static void SavePng(SKBitmap bitmap, string path)
{
    using SKData data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
    using FileStream stream = File.Create(path);
    data.SaveTo(stream);
}

static int Source(string[] args)
{
    var o = Options(args);
    string data = Need(o, "data"), path = Need(o, "path");
    using var mpq = new MpqMount(data);
    var hit = mpq.ReadFileWithSupplier(path) ?? throw new FileNotFoundException(path);
    Console.Write(Encoding.UTF8.GetString(hit.Data));
    return 0;
}

static int Extract(string[] args)
{
    var o = Options(args);
    string data = Need(o, "data"), output = Need(o, "out");
    string panel = o.GetValueOrDefault("panel", "game-menu");
    string rootName = o.GetValueOrDefault("root", "GameMenuFrame");
    string visualState = o.GetValueOrDefault("state", "normal").ToLowerInvariant();
    if (visualState is not ("normal" or "highlighted" or "pushed" or "disabled" or "checked"))
        throw new InvalidDataException("--state must be normal, highlighted, pushed, disabled, or checked");
    string sourceZip = o.GetValueOrDefault("source-zip", "");
    string xmlPath = sourceZip.Length > 0 ? Need(o, "xml") : o.GetValueOrDefault("xml", DefaultXml);
    string xmlSupplier = o.GetValueOrDefault("xml-supplier", "");
    using var mpq = new MpqMount(data);
    List<(string Path, string Supplier, XDocument Doc)> documents = sourceZip.Length > 0
        ? LoadSnapshotDocuments(sourceZip, xmlPath, o.GetValueOrDefault("dependencies", ""))
        : LoadMpqDocuments(mpq, xmlPath, xmlSupplier);

    var named = documents.SelectMany(d => d.Doc.Descendants()
            .Where(e => e.Attribute("name") is not null)
            .Select(e => (Name: (string)e.Attribute("name")!, Element: e, d.Path, d.Supplier)))
        .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
    if (!named.TryGetValue(rootName, out var root)) throw new InvalidDataException($"frame {rootName} not found");

    var rows = new List<Row>();
    AddElement(root.Element, rootName, "", root.Path, root.Supplier, "", "", named, rows, panel);
    if (o.TryGetValue("root-size", out string? rootSize))
    {
        string[] dimensions = rootSize.Split('x', StringSplitOptions.TrimEntries);
        if (dimensions.Length != 2 || !F(dimensions[0], out _) || !F(dimensions[1], out _))
            throw new InvalidDataException("--root-size must be WIDTHxHEIGHT");
        rows[0].X = "0"; rows[0].Y = "0"; rows[0].Width = dimensions[0]; rows[0].Height = dimensions[1];
    }
    var occurrences = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    foreach (Row row in rows)
    {
        occurrences.TryGetValue(row.Element, out int occurrence);
        occurrences[row.Element] = ++occurrence;
        if (occurrence > 1) row.Element += $"#{occurrence}";
    }
    Resolve(rows);
    for (int i = 0; i < rows.Count; i++)
    {
        Row row = rows[i];
        row.DrawIndex = i.ToString(CultureInfo.InvariantCulture);
        row.Visible = "true";
        if (row.Type is "NormalTexture" or "PushedTexture" or "DisabledTexture" or
            "HighlightTexture" or "CheckedTexture")
        {
            bool drawn = row.Type switch
            {
                "NormalTexture" => visualState is "normal" or "highlighted",
                "PushedTexture" => visualState == "pushed",
                "DisabledTexture" => visualState == "disabled",
                "HighlightTexture" => visualState == "highlighted",
                "CheckedTexture" => visualState == "checked",
                _ => false,
            };
            row.Visible = drawn.ToString().ToLowerInvariant();
            if (!drawn) row.Coverage = "NOT-DRAWN";
        }
        bool interactive = IsInteractive(row.Type);
        if (interactive)
        {
            row.Enabled = "true";
            row.InteractionState = "NORMAL";
        }
        if (F(row.X, out float x) && F(row.Y, out float y) &&
            F(row.Width, out float width) && F(row.Height, out float height))
        {
            row.ContentRect = Rect(x, y, x + width, y + height);
            if (interactive)
            {
                string[] inset = row.HitRect.StartsWith("INSETS:", StringComparison.Ordinal)
                    ? row.HitRect["INSETS:".Length..].Split('|') : [];
                float left = inset.Length == 4 ? Parse(inset[0], 0) : 0;
                float top = inset.Length == 4 ? Parse(inset[1], 0) : 0;
                float right = inset.Length == 4 ? Parse(inset[2], 0) : 0;
                float bottom = inset.Length == 4 ? Parse(inset[3], 0) : 0;
                row.HitRect = Rect(x + left, y + top, x + width - right, y + height - bottom);
            }
        }
        if (row.Texture.Length > 0 && row.TexCoords.Length == 0)
            row.TexCoords = "0|0|1|1";
    }
    if (o.TryGetValue("elements", out string? elements) && elements.Length > 0)
    {
        var include = elements.Split('|', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        rows.RemoveAll(row => !include.Contains(row.Element));
    }
    foreach (Row row in rows)
    {
        row.AssetSource = string.Join('|', new[] { row.Texture, row.BgFile, row.EdgeFile }
            .Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => mpq.ReadFileWithSupplier(path) is { } hit ? $"{hit.Supplier}:{path}" : $"MISSING:{path}"));
        if (row.FontPath.Length > 0)
            row.FontSource = mpq.ReadFileWithSupplier(row.FontPath) is { } hit
                ? $"{hit.Supplier}:{row.FontPath}" : $"MISSING:{row.FontPath}";
        row.AssetAvailability = row.AssetSource.Contains("MISSING:", StringComparison.OrdinalIgnoreCase) ||
            row.FontSource.Contains("MISSING:", StringComparison.OrdinalIgnoreCase)
            ? "MISSING" : row.AssetSource.Length > 0 || row.FontSource.Length > 0 ? "PRESENT" : "NOT_APPLICABLE";
    }
    WriteRows(output, rows);
    static string FileSha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))
        .ToLowerInvariant();
    string manifest = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!,
        Path.GetFileNameWithoutExtension(output) + "-manifest.json");
    bool containsScripts = documents.Any(document => document.Doc.Descendants().Any(element =>
        element.Name.LocalName is "Script" or "Scripts" or "OnLoad" or "OnShow" or "OnUpdate"));
    File.WriteAllText(manifest, JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        kind = "reference-xml-extract",
        immutableSource = sourceZip.Length > 0,
        sourceArchive = sourceZip.Length > 0 ? Path.GetFileName(sourceZip) : null,
        sourceArchiveSha256 = sourceZip.Length > 0 ? FileSha(Path.GetFullPath(sourceZip)) : null,
        sourceEntry = root.Path,
        sourceSupplier = root.Supplier,
        dependencies = o.GetValueOrDefault("dependencies", "")
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        panel,
        root = rootName,
        visualState,
        authoredXmlOnly = true,
        containsExecutableScripts = containsScripts,
        runtimeScriptStateApplied = false,
        rows = rows.Count,
        csv = new { path = Path.GetFileName(output), bytes = new FileInfo(output).Length, sha256 = FileSha(output) },
    }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"[ui-parity] extracted {rows.Count} authored rows for {rootName} from " +
        $"{root.Supplier}:{root.Path}; runtime scripts applied=false; manifest={manifest}");
    return 0;
}

static List<(string Path, string Supplier, XDocument Doc)> LoadMpqDocuments(
    MpqMount mpq, string xmlPath, string xmlSupplier)
{
    var documents = new List<(string Path, string Supplier, XDocument Doc)>();
    var pending = new Queue<string>(new[]
        {
            FontsXml, TemplatesXml, ActionButtonTemplatesXml, ActionBarFrameXml, ChatFrameXml, xmlPath
        }.Distinct(StringComparer.OrdinalIgnoreCase));
    var loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    while (pending.TryDequeue(out string? path))
    {
        path = path.Replace('/', '\\');
        if (!loaded.Add(path)) continue;
        var hit = path.Equals(xmlPath, StringComparison.OrdinalIgnoreCase) && xmlSupplier.Length > 0
            ? mpq.ReadFileFromSupplier(path, xmlSupplier) ?? throw new FileNotFoundException($"{xmlSupplier}:{path}")
            : mpq.ReadFileWithSupplier(path) ?? throw new FileNotFoundException(path);
        var doc = XDocument.Parse(Encoding.UTF8.GetString(hit.Data));
        documents.Add((path, hit.Supplier, doc));
        string directory = Path.GetDirectoryName(path) ?? "";
        foreach (XElement include in doc.Descendants().Where(e => e.Name.LocalName == "Include"))
        {
            string file = (string?)include.Attribute("file") ?? "";
            if (file.Length > 0) pending.Enqueue(Path.Combine(directory, file));
        }
    }
    return documents;
}

static List<(string Path, string Supplier, XDocument Doc)> LoadSnapshotDocuments(
    string sourceZipPath, string xmlEntry, string dependencySpec)
{
    sourceZipPath = Path.GetFullPath(sourceZipPath);
    string zipSha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sourceZipPath)))
        .ToLowerInvariant();
    string supplier = $"snapshot-zip:{zipSha}";
    using ZipArchive zip = ZipFile.OpenRead(sourceZipPath);
    var entries = zip.Entries.Where(e => e.Length > 0)
        .ToDictionary(e => e.FullName.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase);
    static string ZipPath(string path) => path.Replace('\\', '/').TrimStart('/');
    string rootEntry = ZipPath(xmlEntry);
    string directory = rootEntry.Contains('/') ? rootEntry[..rootEntry.LastIndexOf('/')] : "";
    string Relative(string path)
    {
        path = ZipPath(path);
        return path.Contains('/') || directory.Length == 0 ? path : $"{directory}/{path}";
    }

    var seeds = new List<string> { rootEntry };
    if (dependencySpec.Length > 0)
        seeds.AddRange(dependencySpec.Split('|', StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries).Select(Relative));
    else if (entries.ContainsKey(Relative("Fonts.xml")))
        seeds.Insert(0, Relative("Fonts.xml"));

    var documents = new List<(string Path, string Supplier, XDocument Doc)>();
    var pending = new Queue<string>(seeds);
    var loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    while (pending.TryDequeue(out string? entryPath))
    {
        entryPath = ZipPath(entryPath);
        if (!loaded.Add(entryPath)) continue;
        if (!entries.TryGetValue(entryPath, out ZipArchiveEntry? entry))
            throw new FileNotFoundException($"{sourceZipPath}:{entryPath}");
        using Stream stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        XDocument doc = XDocument.Parse(reader.ReadToEnd());
        documents.Add((entryPath, supplier, doc));
        string includeDirectory = entryPath.Contains('/') ? entryPath[..entryPath.LastIndexOf('/')] : "";
        foreach (XElement include in doc.Descendants().Where(e => e.Name.LocalName == "Include"))
        {
            string file = ZipPath((string?)include.Attribute("file") ?? "");
            if (file.Length == 0) continue;
            pending.Enqueue(file.Contains('/') || includeDirectory.Length == 0
                ? file : $"{includeDirectory}/{file}");
        }
    }
    return documents;
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
    XElement? anchorsNode = chain.Select(x => x.E.ElementAny("Anchors")).LastOrDefault(x => x is not null);
    XElement[] anchors = anchorsNode?.ElementsAny("Anchor").ToArray() ?? [];
    XElement? anchor = anchors.LastOrDefault();
    XElement? fillTopLeft = anchors.FirstOrDefault(x => A(x, "point").Equals("TOPLEFT", StringComparison.OrdinalIgnoreCase));
    bool fillParent = size is null && fillTopLeft is not null &&
        anchors.Any(x => A(x, "point").Equals("BOTTOMRIGHT", StringComparison.OrdinalIgnoreCase));
    if (fillParent) anchor = fillTopLeft;
    string layer = inheritedLayer;
    XElement? layerParent = element.Ancestors().FirstOrDefault(x => x.Name.LocalName == "Layer");
    if (layerParent is not null) layer = (string?)layerParent.Attribute("level") ?? layer;
    if (layer.Length == 0)
        layer = chain.Select(x => A(x.E, "drawLayer")).LastOrDefault(x => x.Length > 0) ?? "";
    string strata = chain.Select(x => (string?)x.E.Attribute("frameStrata")).LastOrDefault(x => !string.IsNullOrEmpty(x)) ?? inheritedStrata;
    string texture = TextureOf(chain, named, kind);
    string font = FontOf(chain);
    (string fontPath, string fontSupplier, string fontSource, string fontSize, string color) = ResolveFont(font, named);
    string elementSource = chain.Last().Supplier + ":" + chain.Last().Path;
    XElement? backdrop = chain.Select(x => x.E.ElementAny("Backdrop")).LastOrDefault(x => x is not null);
    XElement? inset = backdrop?.ElementAny("BackgroundInsets")?.ElementAny("AbsInset");
    XElement? tile = backdrop?.ElementAny("TileSize")?.ElementAny("AbsValue");
    XElement? edge = backdrop?.ElementAny("EdgeSize")?.ElementAny("AbsValue");
    XElement? texCoords = chain.Select(x => x.E.ElementAny("TexCoords")).LastOrDefault(x => x is not null);
    XElement? hitInsets = chain.Select(x => x.E.ElementAny("HitRectInsets")?.ElementAny("AbsInset"))
        .LastOrDefault(x => x is not null);
    string alphaMode = chain.Select(x => A(x.E, "alphaMode")).LastOrDefault(x => x.Length > 0) ?? "";

    var row = new Row
    {
        Panel = panel, Element = instanceName, Type = kind, Parent = parentName,
        Width = fillParent ? "__FILL__" : A(size, "x"), Height = fillParent ? "__FILL__" : A(size, "y"), Point = A(anchor, "point"),
        RelativeTo = Expand(A(anchor, "relativeTo"), instanceName, parentName),
        RelativePoint = A(anchor, "relativePoint"), OffsetX = A(anchor?.ElementAny("Offset")?.ElementAny("AbsDimension"), "x"),
        OffsetY = A(anchor?.ElementAny("Offset")?.ElementAny("AbsDimension"), "y"),
        Texture = NormalizeTexture(texture), Font = font, FontPath = fontPath, FontSize = fontSize,
        Color = color, Layer = layer, Strata = strata, Source = elementSource,
        BgFile = NormalizeTexture(A(backdrop, "bgFile")), EdgeFile = NormalizeTexture(A(backdrop, "edgeFile")),
        TileSize = A(tile, "val"), EdgeSize = A(edge, "val"),
        Insets = inset is null ? "" : $"{A(inset,"left")}|{A(inset,"top")}|{A(inset,"right")}|{A(inset,"bottom")}",
        TexCoords = texCoords is null ? "" : $"{A(texCoords,"left")}|{A(texCoords,"top")}|{A(texCoords,"right")}|{A(texCoords,"bottom")}",
        HitRect = hitInsets is null ? "" : $"INSETS:{A(hitInsets,"left")}|{A(hitInsets,"top")}|{A(hitInsets,"right")}|{A(hitInsets,"bottom")}",
        BlendMode = alphaMode.Length > 0 ? alphaMode.ToUpperInvariant() : texture.Length > 0 ? "BLEND" : "",
        AssetSource = "", FontSource = fontSource.Length == 0 ? fontSupplier : fontSupplier + ":" + fontSource,
    };
    rows.Add(row);

    foreach (var childSource in chain)
    foreach (XElement child in childSource.E.Elements())
    {
        if (child.Name.LocalName == "Frames")
        {
            foreach (XElement nested in child.Elements().Where(IsDrawable)
                .Where(e => !A(e, "hidden").Equals("true", StringComparison.OrdinalIgnoreCase)))
            {
                string rawName = (string?)nested.Attribute("name") ?? $"{instanceName}/{nested.Name.LocalName}";
                string name = Expand(rawName, instanceName, instanceName);
                AddElement(nested, name, instanceName, childSource.Path, childSource.Supplier, layer, strata, named, rows, panel);
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
                AddElement(nested, name, instanceName, childSource.Path, childSource.Supplier,
                    A(layerNode, "level"), strata, named, rows, panel);
            }
        }
        else if (IsDrawable(child) && !A(child, "hidden").Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            string rawName = (string?)child.Attribute("name") ?? $"{instanceName}/{child.Name.LocalName}";
            AddElement(child, Expand(rawName, instanceName, instanceName), instanceName,
                childSource.Path, childSource.Supplier, layer, strata, named, rows, panel);
        }
    }
}

static string TextureOf(List<(XElement E, string Path, string Supplier)> chain,
    Dictionary<string, (string Name, XElement Element, string Path, string Supplier)> named,
    string elementKind)
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
        // Button/Frame child textures are emitted as their own rows. Copying a NormalTexture
        // onto its owning Button (or a Texture onto its Frame) invents an extra draw and makes a
        // mechanically impossible expected CSV. StatusBar's BarTexture is the one authored
        // texture value that belongs to the widget rather than a separately drawable child.
        if (elementKind != "StatusBar") continue;
        foreach (string childName in new[] { "BarTexture" })
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
    if (!F(parent.X, out float px) || !F(parent.Y, out float py) || !F(parent.Width, out float pw) || !F(parent.Height, out float ph)) return;
    // FrameXML's setAllPoints="true" wrappers omit Size and Anchors entirely.
    // They inherit their parent's rectangle and are common around texture/name layers.
    if (row.Width == "__FILL__" && row.Height == "__FILL__")
    {
        F(row.OffsetX, out float fillX); F(row.OffsetY, out float fillY);
        row.X = N(px + fillX); row.Y = N(py - fillY); row.Width = N(pw); row.Height = N(ph);
        return;
    }
    if (!F(row.Width, out float w) || !F(row.Height, out float h))
    {
        if (row.Width.Length == 0 && row.Height.Length == 0 && row.Point.Length == 0)
        {
            row.X = N(px); row.Y = N(py); row.Width = N(pw); row.Height = N(ph);
        }
        return;
    }
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
    var byName = rows.ToDictionary(r => r.Element, StringComparer.OrdinalIgnoreCase);
    foreach (Row row in rows.Where(r => r.Texture.Length > 0 && (!F(r.Width, out _) || !F(r.Height, out _))))
    {
        // FrameXML layer regions without explicit anchors/sizes fill their owning frame.
        // Resolve that authored ownership here so shipped chrome cannot disappear from
        // the perceptual reference merely because its texture row is implicit.
        Row? owner = row.Parent.Length > 0 && byName.TryGetValue(row.Parent, out Row? direct) ? direct : null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (owner is not null && seen.Add(owner.Element) && (!F(owner.Width, out _) || !F(owner.Height, out _)))
            owner = owner.Parent.Length > 0 && byName.TryGetValue(owner.Parent, out Row? next) ? next : null;
        if (owner is not null && F(owner.X, out _) && F(owner.Y, out _) && F(owner.Width, out _) && F(owner.Height, out _))
        { row.X = owner.X; row.Y = owner.Y; row.Width = owner.Width; row.Height = owner.Height; }
    }
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
            bool flipX = src.Left > src.Right, flipY = src.Top > src.Bottom;
            src = new SKRect(Math.Min(src.Left, src.Right), Math.Min(src.Top, src.Bottom), Math.Max(src.Left, src.Right), Math.Max(src.Top, src.Bottom));
            canvas.Save();
            if (flipX) { canvas.Translate(2 * x + w, 0); canvas.Scale(-1, 1); }
            if (flipY) { canvas.Translate(0, 2 * y + h); canvas.Scale(1, -1); }
            canvas.DrawBitmap(art, src, new SKRect(x, y, x + w, y + h));
            canvas.Restore();
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
    string selectionPath = Need(o, "selection");
    string[] selection = File.ReadAllLines(selectionPath).Select(x => x.Trim()).Where(x => x.Length > 0 && !x.StartsWith('#')).ToArray();
    if (!selection.Contains("scope=all-reference-elements", StringComparer.OrdinalIgnoreCase))
        throw new InvalidDataException("selection rule must declare scope=all-reference-elements; post-hoc element enumerations are forbidden");
    List<Row> expected = ReadRows(expectedPath), actual = ReadRows(actualPath);
    List<Adjudication> adjudications = o.TryGetValue("adjudications", out string? adjudicationPath)
        ? ReadAdjudications(adjudicationPath) : [];
    var usedAdjudications = new HashSet<int>();
    foreach (Row row in actual.Where(r => r.Coverage.Length == 0)) row.Coverage = "DRAWN-NOT-INSTRUMENTED";
    var e = expected.ToDictionary(r => r.Element, StringComparer.OrdinalIgnoreCase);
    var a = actual.ToDictionary(r => r.Element, StringComparer.OrdinalIgnoreCase);
    var lines = new List<string> { "panel,element,field,expected,actual,verdict,decisionId,reason" };
    int instrumented = 0, notDrawn = 0, referenceCount = e.Count;
    int deltas = 0, preserved = 0;
    foreach (string name in e.Keys.Union(a.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x))
    {
        e.TryGetValue(name, out Row? er); a.TryGetValue(name, out Row? ar);
        string expectedCoverage = er is null
            ? "NOT-REFERENCE"
            : string.IsNullOrWhiteSpace(er.Coverage) ? "DRAWN-INSTRUMENTED" : er.Coverage;
        string actualCoverage = ar is null
            ? "MISSING"
            : string.IsNullOrWhiteSpace(ar.Coverage) ? "DRAWN-NOT-INSTRUMENTED" : ar.Coverage;
        if (er is not null && expectedCoverage == "DRAWN-INSTRUMENTED" && actualCoverage == "DRAWN-INSTRUMENTED") instrumented++;
        if (er is not null && expectedCoverage == "NOT-DRAWN" && actualCoverage == "NOT-DRAWN") notDrawn++;
        Add("coverage", expectedCoverage, actualCoverage);
        Add("presence", expectedCoverage == "NOT-DRAWN" || er is null ? "ABSENT" : "PRESENT",
            ar is null || actualCoverage == "NOT-DRAWN" ? "ABSENT" : "PRESENT");
        if (er is null || ar is null) continue;
        if (expectedCoverage == "NOT-DRAWN" && actualCoverage == "NOT-DRAWN") continue;
        if (expectedCoverage != "DRAWN-INSTRUMENTED" || actualCoverage != "DRAWN-INSTRUMENTED") continue;
        Add("type", er.Type, ar.Type);
        Add("parent", er.Parent, ar.Parent);
        Add("geometry", $"{er.X}|{er.Y}|{er.Width}|{er.Height}", $"{ar.X}|{ar.Y}|{ar.Width}|{ar.Height}");
        Add("content-rect", er.ContentRect, ar.ContentRect);
        Add("anchor", $"{er.Point}|{er.RelativeTo}|{er.RelativePoint}|{er.OffsetX}|{er.OffsetY}", $"{ar.Point}|{ar.RelativeTo}|{ar.RelativePoint}|{ar.OffsetX}|{ar.OffsetY}");
        Add("texture-path", $"{er.Texture}|{er.BgFile}|{er.EdgeFile}", $"{ar.Texture}|{ar.BgFile}|{ar.EdgeFile}");
        Add("texture-coordinates", er.TexCoords, ar.TexCoords);
        Add("backdrop", $"{er.BgFile}|{er.EdgeFile}|{er.TileSize}|{er.EdgeSize}|{er.Insets}",
            $"{ar.BgFile}|{ar.EdgeFile}|{ar.TileSize}|{ar.EdgeSize}|{ar.Insets}");
        Add("font", $"{er.Font}|{er.FontPath}|{er.FontSize}", $"{ar.Font}|{ar.FontPath}|{ar.FontSize}");
        Add("color", er.Color, ar.Color);
        Add("draw-order", $"{er.Strata}|{er.Layer}|{er.DrawIndex}", $"{ar.Strata}|{ar.Layer}|{ar.DrawIndex}");
        Add("blend-mode", er.BlendMode, ar.BlendMode);
        Add("clip", $"{er.ClipRect}|{er.ClipMask}", $"{ar.ClipRect}|{ar.ClipMask}");
        Add("asset-availability", er.AssetAvailability, ar.AssetAvailability);
        Add("visible", er.Visible, ar.Visible);
        Add("enabled", er.Enabled, ar.Enabled);
        Add("interaction-state", er.InteractionState, ar.InteractionState);
        Add("hit-rect", er.HitRect, ar.HitRect);
        void Add(string field, string expectedValue, string actualValue)
        {
            bool pass = string.Equals(expectedValue, actualValue, StringComparison.OrdinalIgnoreCase);
            string verdict = pass ? "PASS" : "DELTA";
            string decisionId = "", reason = "";
            if (!pass)
            {
                int[] matches = adjudications.Select((rule, index) => (rule, index))
                    .Where(pair => pair.rule.Matches(name, field, expectedValue, actualValue))
                    .Select(pair => pair.index).ToArray();
                if (matches.Length > 1)
                    throw new InvalidDataException($"duplicate adjudication for {name}/{field}");
                if (matches.Length == 1)
                {
                    int index = matches[0];
                    usedAdjudications.Add(index);
                    decisionId = adjudications[index].DecisionId;
                    reason = adjudications[index].Reason;
                    verdict = "PRESERVED-DIFFERENCE";
                    preserved++;
                }
                else deltas++;
            }
            lines.Add(Csv((er ?? ar)!.Panel, name, field, expectedValue, actualValue,
                verdict, decisionId, reason));
        }
    }
    int[] unused = Enumerable.Range(0, adjudications.Count)
        .Where(index => !usedAdjudications.Contains(index)).ToArray();
    if (unused.Length > 0)
        throw new InvalidDataException("unused/stale adjudication(s): " + string.Join(", ",
            unused.Select(index => $"{adjudications[index].Element}/{adjudications[index].Field}")));
    File.WriteAllLines(output, lines);
    static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))
        .ToLowerInvariant();
    string manifest = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!,
        Path.GetFileNameWithoutExtension(output) + "-manifest.json");
    string toolAssembly = typeof(Row).Assembly.Location;
    File.WriteAllText(manifest, JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        kind = "ui-mechanical-diff",
        result = deltas == 0 ? "PASS" : "DELTA",
        referenceRows = referenceCount,
        instrumentedRows = instrumented,
        notDrawnRows = notDrawn,
        verdictRows = lines.Count - 1,
        assertionsPassed = lines.Count - 1,
        assertionsFailed = deltas,
        mechanicalDeltas = deltas,
        preservedDifferences = preserved,
        expected = new { path = Path.GetFileName(expectedPath), sha256 = Sha(expectedPath) },
        actual = new { path = Path.GetFileName(actualPath), sha256 = Sha(actualPath) },
        selection = new { path = Path.GetFileName(selectionPath), sha256 = Sha(selectionPath) },
        adjudications = adjudicationPath is null ? null :
            new { path = Path.GetFileName(adjudicationPath), sha256 = Sha(adjudicationPath), rules = adjudications.Count },
        output = new { path = Path.GetFileName(output), sha256 = Sha(output) },
        tool = new { path = Path.GetFileName(toolAssembly), sha256 = Sha(toolAssembly) },
    }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"[ui-parity] selection {Path.GetFileName(selectionPath)}; coverage {instrumented}/{referenceCount} instrumented/reference; NOT-DRAWN {notDrawn}; preserved {preserved}; diff {deltas} mechanical delta(s) across {lines.Count - 1} verdict rows; manifest={manifest}");
    return deltas == 0 ? 0 : 3;
}

static List<Adjudication> ReadAdjudications(string path)
{
    string[] lines = File.ReadAllLines(path);
    if (lines.Length == 0 || !lines[0].Equals(
            "element,field,expected,actual,decisionId,reason", StringComparison.Ordinal))
        throw new InvalidDataException("adjudication CSV header must be element,field,expected,actual,decisionId,reason");
    var result = new List<Adjudication>();
    foreach (string line in lines.Skip(1).Where(line => line.Length > 0))
    {
        string[] fields = ParseCsvFields(line).ToArray();
        if (fields.Length != 6) throw new InvalidDataException("adjudication rows require exactly 6 columns");
        if (fields.Any(field => field.Contains('*')))
            throw new InvalidDataException("wildcards are forbidden in adjudications");
        if (string.IsNullOrWhiteSpace(fields[0]) || string.IsNullOrWhiteSpace(fields[1]) ||
            string.IsNullOrWhiteSpace(fields[4]) || string.IsNullOrWhiteSpace(fields[5]))
            throw new InvalidDataException("adjudication element/field/decisionId/reason must be nonempty");
        result.Add(new(fields[0], fields[1], fields[2], fields[3], fields[4], fields[5]));
    }
    return result;
}

static IEnumerable<string> ParseCsvFields(string line)
{
    var value = new StringBuilder(); bool quoted = false;
    for (int i = 0; i < line.Length; i++)
    {
        char c = line[i];
        if (c == '"')
        {
            if (quoted && i + 1 < line.Length && line[i + 1] == '"')
            { value.Append('"'); i++; }
            else quoted = !quoted;
        }
        else if (c == ',' && !quoted) { yield return value.ToString(); value.Clear(); }
        else value.Append(c);
    }
    if (quoted) throw new InvalidDataException("unterminated quoted CSV field");
    yield return value.ToString();
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

static int Containment(string[] args)
{
    var o = Options(args);
    string visiblePath = Need(o, "visible"), hiddenPath = Need(o, "hidden"), output = Need(o, "out");
    string shape = o.GetValueOrDefault("shape", "ellipse").ToLowerInvariant();
    if (shape is not ("ellipse" or "rect")) throw new InvalidDataException("--shape must be ellipse or rect");
    float left = Parse(Need(o, "left"), float.NaN), top = Parse(Need(o, "top"), float.NaN);
    float right = Parse(Need(o, "right"), float.NaN), bottom = Parse(Need(o, "bottom"), float.NaN);
    int threshold = (int)Math.Clamp(Parse(o.GetValueOrDefault("threshold", "0"), 0), 0, 255);
    if (!float.IsFinite(left) || !float.IsFinite(top) || !float.IsFinite(right) || !float.IsFinite(bottom) ||
        right <= left || bottom <= top) throw new InvalidDataException("invalid aperture bounds");

    using SKBitmap visible = SKBitmap.Decode(visiblePath) ?? throw new InvalidDataException(visiblePath);
    using SKBitmap hidden = SKBitmap.Decode(hiddenPath) ?? throw new InvalidDataException(hiddenPath);
    if (visible.Width != hidden.Width || visible.Height != hidden.Height)
        throw new InvalidDataException("visible and hidden images must have identical dimensions");
    using var diff = new SKBitmap(visible.Width, visible.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
    diff.Erase(SKColors.Transparent);
    long insideChanged = 0, outsideChanged = 0, insidePixels = 0, outsidePixels = 0;
    int maxInsideDelta = 0, maxOutsideDelta = 0;
    for (int y = 0; y < visible.Height; y++)
    for (int x = 0; x < visible.Width; x++)
    {
        bool inRect = x + .5f >= left && x + .5f < right && y + .5f >= top && y + .5f < bottom;
        bool inside = inRect;
        if (inside && shape == "ellipse")
        {
            float nx = ((x + .5f - left) / (right - left)) * 2f - 1f;
            float ny = ((y + .5f - top) / (bottom - top)) * 2f - 1f;
            inside = nx * nx + ny * ny <= 1f;
        }
        if (inside) insidePixels++; else outsidePixels++;
        SKColor a = visible.GetPixel(x, y), b = hidden.GetPixel(x, y);
        int delta = Math.Max(Math.Max(Math.Abs(a.Red - b.Red), Math.Abs(a.Green - b.Green)),
            Math.Max(Math.Abs(a.Blue - b.Blue), Math.Abs(a.Alpha - b.Alpha)));
        if (inside) maxInsideDelta = Math.Max(maxInsideDelta, delta); else maxOutsideDelta = Math.Max(maxOutsideDelta, delta);
        if (delta <= threshold) continue;
        if (inside) { insideChanged++; diff.SetPixel(x, y, new SKColor(0, 255, 0, 220)); }
        else { outsideChanged++; diff.SetPixel(x, y, new SKColor(255, 0, 0, 220)); }
    }
    bool pass = insideChanged > 0 && outsideChanged == 0;
    int assertionsPassed = (insideChanged > 0 ? 1 : 0) + (outsideChanged == 0 ? 1 : 0);
    int assertionsFailed = 2 - assertionsPassed;
    string? diffPath = o.GetValueOrDefault("diff-image");
    if (!string.IsNullOrWhiteSpace(diffPath)) SavePng(diff, diffPath);
    static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    string toolAssembly = typeof(Row).Assembly.Location;
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
    File.WriteAllText(output, JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        kind = "ui-containment",
        result = pass ? "PASS" : "FAIL",
        assertionsPassed,
        assertionsFailed,
        shape,
        aperture = new { left, top, right, bottom },
        threshold,
        insidePixels,
        outsidePixels,
        insideChanged,
        outsideChanged,
        maxInsideDelta,
        maxOutsideDelta,
        visible = new { path = Path.GetFileName(visiblePath), sha256 = Sha(visiblePath) },
        hidden = new { path = Path.GetFileName(hiddenPath), sha256 = Sha(hiddenPath) },
        diffImage = string.IsNullOrWhiteSpace(diffPath) ? null :
            new { path = Path.GetFileName(diffPath), sha256 = Sha(diffPath) },
        tool = new { path = Path.GetFileName(toolAssembly), sha256 = Sha(toolAssembly) },
    }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"[ui-parity] containment {(pass ? "PASS" : "FAIL")}; insideChanged={insideChanged}; outsideChanged={outsideChanged}; report={output}");
    return pass ? 0 : 3;
}

static bool IsDrawable(XElement e) => e.Name.LocalName is "Frame" or "ScrollFrame" or
    "Button" or "CheckButton" or "StatusBar" or "Slider" or "EditBox" or "Model" or
    "Minimap" or "Texture" or "FontString" or
    "NormalTexture" or "PushedTexture" or "DisabledTexture" or "HighlightTexture" or "CheckedTexture";
static bool IsInteractive(string type) => type is "Button" or "CheckButton" or "Slider" or "EditBox";
static string A(XElement? e, string name) => (string?)e?.Attribute(name) ?? "";
static string Expand(string value, string instance, string parent) => value.Replace("$parent", parent.Length > 0 ? parent : instance, StringComparison.OrdinalIgnoreCase);
static string NormalizeTexture(string value) => value.Length == 0 ? "" : value.EndsWith(".blp", StringComparison.OrdinalIgnoreCase) ? value : value + ".blp";
static string ColorHex(XElement e) => $"#{Byte(A(e,"r")):X2}{Byte(A(e,"g")):X2}{Byte(A(e,"b")):X2}{Byte(A(e,"a"),1):X2}";
static int Byte(string s, float fallback=0) => (int)MathF.Round(Math.Clamp(Parse(s, fallback),0,1)*255);
static string N(float f) => f.ToString("0.###", CultureInfo.InvariantCulture);
static string Rect(float left, float top, float right, float bottom)
    => $"{N(left)}|{N(top)}|{N(right)}|{N(bottom)}";
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

sealed record Adjudication(string Element, string Field, string Expected, string Actual,
    string DecisionId, string Reason)
{
    public bool Matches(string element, string field, string expected, string actual) =>
        Element.Equals(element, StringComparison.OrdinalIgnoreCase) &&
        Field.Equals(field, StringComparison.OrdinalIgnoreCase) &&
        Expected.Equals(expected, StringComparison.OrdinalIgnoreCase) &&
        Actual.Equals(actual, StringComparison.OrdinalIgnoreCase);
}

sealed class Row
{
    public const string Header = "panel,element,type,parent,x,y,width,height,point,relativeTo,relativePoint,offsetX,offsetY,texture,font,fontPath,fontSize,color,layer,strata,bgFile,edgeFile,tileSize,edgeSize,insets,texCoords,contentRect,clipRect,clipMask,drawIndex,blendMode,visible,enabled,interactionState,hitRect,assetAvailability,source,assetSource,fontSource,coverage";
    public string Panel="",Element="",Type="",Parent="",X="",Y="",Width="",Height="",Point="",RelativeTo="",RelativePoint="",OffsetX="",OffsetY="",Texture="",Font="",FontPath="",FontSize="",Color="",Layer="",Strata="",BgFile="",EdgeFile="",TileSize="",EdgeSize="",Insets="",TexCoords="",ContentRect="",ClipRect="",ClipMask="",DrawIndex="",BlendMode="",Visible="",Enabled="",InteractionState="",HitRect="",AssetAvailability="",Source="",AssetSource="",FontSource="",Coverage="";
    public string ToCsv() => Join(Panel,Element,Type,Parent,X,Y,Width,Height,Point,RelativeTo,RelativePoint,OffsetX,OffsetY,Texture,Font,FontPath,FontSize,Color,Layer,Strata,BgFile,EdgeFile,TileSize,EdgeSize,Insets,TexCoords,ContentRect,ClipRect,ClipMask,DrawIndex,BlendMode,Visible,Enabled,InteractionState,HitRect,AssetAvailability,Source,AssetSource,FontSource,Coverage);
    public static Row FromCsv(string line)
    {
        string[] v=ParseCsv(line).ToArray();
        if (v.Length is 29 or 30)
            return new Row{Panel=v[0],Element=v[1],Type=v[2],Parent=v[3],X=v[4],Y=v[5],Width=v[6],Height=v[7],Point=v[8],RelativeTo=v[9],RelativePoint=v[10],OffsetX=v[11],OffsetY=v[12],Texture=v[13],Font=v[14],FontPath=v[15],FontSize=v[16],Color=v[17],Layer=v[18],Strata=v[19],BgFile=v[20],EdgeFile=v[21],TileSize=v[22],EdgeSize=v[23],Insets=v[24],TexCoords=v[25],Source=v[26],AssetSource=v[27],FontSource=v[28],Coverage=v.Length==30?v[29]:""};
        if (v.Length != 40) throw new InvalidDataException($"expected 29, 30, or 40 columns, got {v.Length}");
        return new Row{Panel=v[0],Element=v[1],Type=v[2],Parent=v[3],X=v[4],Y=v[5],Width=v[6],Height=v[7],Point=v[8],RelativeTo=v[9],RelativePoint=v[10],OffsetX=v[11],OffsetY=v[12],Texture=v[13],Font=v[14],FontPath=v[15],FontSize=v[16],Color=v[17],Layer=v[18],Strata=v[19],BgFile=v[20],EdgeFile=v[21],TileSize=v[22],EdgeSize=v[23],Insets=v[24],TexCoords=v[25],ContentRect=v[26],ClipRect=v[27],ClipMask=v[28],DrawIndex=v[29],BlendMode=v[30],Visible=v[31],Enabled=v[32],InteractionState=v[33],HitRect=v[34],AssetAvailability=v[35],Source=v[36],AssetSource=v[37],FontSource=v[38],Coverage=v[39]};
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
