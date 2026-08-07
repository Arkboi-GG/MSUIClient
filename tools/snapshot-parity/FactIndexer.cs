using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace SnapshotParity;

internal static partial class FactIndexer
{
    private static readonly HashSet<string> IgnoredMethodNames = new(StringComparer.Ordinal)
    {
        "if", "for", "foreach", "while", "switch", "catch", "using", "lock", "return", "new",
    };

    public static FactIndex Build(SnapshotManifest manifest)
    {
        SnapshotCapture.Verify(manifest);
        HashSet<string> knownUpdateFields = DiscoverUpdateFields(manifest);
        var index = new FactIndex
        {
            SnapshotId = manifest.Id,
            SnapshotKind = manifest.Kind,
            SnapshotSha256 = manifest.AggregateSha256,
            IndexedUtc = DateTimeOffset.UtcNow,
        };
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (SnapshotFile file in manifest.Files)
        {
            Add(index, ids, file, "file", file.Role, file.Path, 1, $"{file.Role} file ({file.Size} bytes)", true);
            string extension = Path.GetExtension(file.Path).ToLowerInvariant();
            if (extension is not (".rs" or ".cs" or ".xml" or ".wgsl" or ".vert" or ".frag" or ".toml" or ".csproj"))
                continue;
            string full = Path.Combine(manifest.Root, file.Path.Replace('/', Path.DirectorySeparatorChar));
            string text = File.ReadAllText(full, Encoding.UTF8);
            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            if (extension == ".xml") ExtractXml(index, ids, file, text, lines, knownUpdateFields);
            else ExtractCode(index, ids, file, extension, lines, knownUpdateFields);
        }
        index.Facts.Sort((a, b) =>
        {
            int path = StringComparer.Ordinal.Compare(a.Path, b.Path);
            if (path != 0) return path;
            int line = a.Line.CompareTo(b.Line);
            return line != 0 ? line : StringComparer.Ordinal.Compare(a.Id, b.Id);
        });
        return index;
    }

    private static void ExtractCode(FactIndex index, HashSet<string> ids, SnapshotFile file,
        string extension, string[] lines, HashSet<string> knownUpdateFields)
    {
        bool rustTestPending = false;
        for (int i = 0; i < lines.Length; i++)
        {
            string raw = lines[i];
            string line = raw.Trim();
            int number = i + 1;
            if (line.Length == 0) continue;
            if (extension == ".rs" && line.StartsWith("#[test", StringComparison.Ordinal))
                rustTestPending = true;

            bool shaderSource = extension is ".wgsl" or ".vert" or ".frag";
            bool embeddedScript = extension == ".xml-script";
            Match type = extension == ".rs" ? RustTypeRegex().Match(raw) :
                shaderSource || embeddedScript ? Match.Empty : CSharpTypeRegex().Match(raw);
            if (type.Success)
                Add(index, ids, file, "type", "runtime", type.Groups[2].Value, number, line, true);

            Match method = extension == ".rs" ? RustFunctionRegex().Match(raw) :
                shaderSource || embeddedScript ? Match.Empty : CSharpMethodRegex().Match(raw);
            string methodEvidence = line;
            if (extension == ".cs" && !method.Success && LooksLikeCSharpDeclaration(line))
            {
                methodEvidence = LogicalDeclaration(lines, i);
                method = CSharpMethodRegex().Match(methodEvidence);
                if (!method.Success) method = CSharpConstructorRegex().Match(methodEvidence);
            }
            if (method.Success && !IgnoredMethodNames.Contains(method.Groups[1].Value))
            {
                string kind = rustTestPending || line.Contains("[Fact]", StringComparison.Ordinal) ||
                    line.Contains("[Test]", StringComparison.Ordinal) ? "test" : "function";
                Add(index, ids, file, kind, kind == "test" ? "verification" : "runtime",
                    method.Groups[1].Value, number, methodEvidence, true);
                rustTestPending = false;
            }

            foreach (Match opcode in OpcodeRegex().Matches(raw))
                Add(index, ids, file, "opcode", "protocol", opcode.Value, number, line, true);
            foreach (Match field in UpperIdentifierRegex().Matches(raw))
                if (knownUpdateFields.Contains(field.Value))
                    Add(index, ids, file, "update-field", "state", CanonicalUpdateField(field.Value), number, line, true);
            foreach (Match asset in AssetRegex().Matches(raw))
                Add(index, ids, file, "asset", "presentation", NormalizeAsset(asset.Groups[1].Value), number, line, true);
            foreach (Match dbc in DbcRegex().Matches(raw))
                Add(index, ids, file, "dbc", "data", dbc.Groups[1].Value, number, line, true);
            foreach (Match eventName in EventStringRegex().Matches(raw))
                Add(index, ids, file, "event", "events", eventName.Groups[1].Value, number, line, true);

            if (extension == ".cs")
            {
                foreach (Match ui in ImGuiRegex().Matches(raw))
                {
                    string name = ui.Groups[2].Success && ui.Groups[2].Value.Length > 0
                        ? $"{ui.Groups[1].Value}:{ui.Groups[2].Value}"
                        : ui.Groups[1].Value;
                    Add(index, ids, file, "ui-call", "presentation", name, number, line, true);
                }
                if (line.Contains("Check(", StringComparison.Ordinal) || line.Contains("Assert.", StringComparison.Ordinal))
                    Add(index, ids, file, "test-assertion", "verification", AssertionName(line), number, line, true);
            }
            else if (extension == ".rs" && (line.Contains("assert!", StringComparison.Ordinal) ||
                line.Contains("assert_eq!", StringComparison.Ordinal) || line.Contains("assert_ne!", StringComparison.Ordinal)))
                Add(index, ids, file, "test-assertion", "verification", AssertionName(line), number, line, true);

            if (extension is ".wgsl" or ".vert" or ".frag")
            {
                Match shader = ShaderEntryRegex().Match(raw);
                if (shader.Success)
                    Add(index, ids, file, "shader-entry", "rendering", shader.Groups[1].Value, number, line, true);
                foreach (Match binding in ShaderBindingRegex().Matches(raw))
                    Add(index, ids, file, "shader-binding", "rendering", binding.Value, number, line, true);
            }
            if (extension is ".toml" or ".csproj")
            {
                Match manifest = ManifestEntryRegex().Match(raw);
                if (manifest.Success)
                    Add(index, ids, file, "manifest-entry", "build", manifest.Groups[1].Value, number, line, false);
            }
        }
    }

    private static void ExtractXml(FactIndex index, HashSet<string> ids, SnapshotFile file,
        string text, string[] lines, HashSet<string> knownUpdateFields)
    {
        try
        {
            XDocument document = XDocument.Parse(text, LoadOptions.SetLineInfo | LoadOptions.PreserveWhitespace);
            foreach (XElement element in document.Descendants())
            {
                int line = (element as IXmlLineInfo)?.LineNumber ?? 1;
                string local = element.Name.LocalName;
                string? name = (string?)element.Attribute("name");
                if (!string.IsNullOrWhiteSpace(name))
                    Add(index, ids, file, "ui-element", "presentation", $"{local}:{name}", line,
                        XmlEvidence(element), true);
                if (local is "OnClick" or "OnLoad" or "OnEvent" or "OnUpdate" or "OnEnter" or
                    "OnLeave" or "OnShow" or "OnHide" or "OnDragStart" or "OnReceiveDrag")
                    Add(index, ids, file, "ui-handler", "input-events", local, line, XmlEvidence(element), true);
                if (local == "Anchor")
                    Add(index, ids, file, "ui-anchor", "presentation", AnchorName(element), line, XmlEvidence(element), true);
                if (local == "Include" && element.Attribute("file") is { } include)
                    Add(index, ids, file, "include", "build", include.Value, line, XmlEvidence(element), false);
                foreach (XAttribute attribute in element.Attributes())
                {
                    if (attribute.Name.LocalName is "file" or "texture" or "bgFile" or "edgeFile")
                        Add(index, ids, file, "asset", "presentation", NormalizeAsset(attribute.Value), line,
                            $"{local} {attribute.Name.LocalName}={attribute.Value}", true);
                }
            }
        }
        catch (XmlException)
        {
            Add(index, ids, file, "parse-warning", "tooling", file.Path, 1, "XML parser rejected file", false);
        }

        ExtractCode(index, ids, file, ".xml-script", lines, knownUpdateFields);
        for (int i = 0; i < lines.Length; i++)
        {
            Match luaFunction = LuaFunctionRegex().Match(lines[i]);
            if (luaFunction.Success)
                Add(index, ids, file, "script-function", "runtime", luaFunction.Groups[1].Value,
                    i + 1, lines[i].Trim(), true);
        }
    }

    private static void Add(FactIndex index, HashSet<string> ids, SnapshotFile file, string kind,
        string surface, string name, int line, string evidence, bool reviewRequired)
    {
        evidence = evidence.Trim();
        if (evidence.Length > 500) evidence = evidence[..500];
        string evidenceHash = Hash(evidence);
        string seed = $"{kind}\0{file.Path}\0{line}\0{name}\0{evidenceHash}";
        string id = $"fact-{Hash(seed)[..20]}";
        if (!ids.Add(id)) return;
        index.Facts.Add(new SourceFact
        {
            Id = id,
            Kind = kind,
            Surface = surface,
            Name = name,
            Path = file.Path,
            Line = line,
            Evidence = evidence,
            EvidenceSha256 = evidenceHash,
            FileSha256 = file.Sha256,
            ReviewRequired = reviewRequired,
        });
    }

    private static string Hash(string value) => SnapshotCapture.Hex(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static HashSet<string> DiscoverUpdateFields(SnapshotManifest manifest)
    {
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (SnapshotFile file in manifest.Files.Where(IsUpdateFieldDefinitionFile))
        {
            string full = Path.Combine(manifest.Root, file.Path.Replace('/', Path.DirectorySeparatorChar));
            foreach (string line in File.ReadLines(full, Encoding.UTF8))
            {
                Match definition = UpdateFieldDeclarationRegex().Match(line);
                if (!definition.Success) continue;
                string name = definition.Groups[1].Value;
                if (manifest.Kind != "benilla" || name.StartsWith("FIELD_", StringComparison.Ordinal))
                    fields.Add(name);
            }
        }
        return fields;
    }

    private static bool IsUpdateFieldDefinitionFile(SnapshotFile file) =>
        file.Path.Contains("update_object/fields", StringComparison.OrdinalIgnoreCase) ||
        file.Path.EndsWith("/ObjectFields.cs", StringComparison.OrdinalIgnoreCase);

    private static string CanonicalUpdateField(string name)
    {
        if (name.StartsWith("FIELD_", StringComparison.Ordinal)) name = name[6..];
        return name.Replace("_FIELD_", "_", StringComparison.Ordinal);
    }
    private static string NormalizeAsset(string value) => value.Trim('"', '\'').Replace('\\', '/').ToLowerInvariant();
    private static string AssertionName(string line)
    {
        Match message = Regex.Match(line, "\"([^\"]{3,160})\"");
        return message.Success ? message.Groups[1].Value : line[..Math.Min(120, line.Length)];
    }
    private static string XmlEvidence(XElement element) => element.ToString(SaveOptions.DisableFormatting);
    private static string AnchorName(XElement element) => string.Join('|', new[]
    {
        (string?)element.Attribute("point") ?? "",
        (string?)element.Attribute("relativeTo") ?? "",
        (string?)element.Attribute("relativePoint") ?? "",
    });
    private static bool LooksLikeCSharpDeclaration(string line) =>
        line.Contains('(') && !line.EndsWith(';') && !line.StartsWith("//", StringComparison.Ordinal) &&
        !line.StartsWith("if ", StringComparison.Ordinal) && !line.StartsWith("for ", StringComparison.Ordinal) &&
        !line.StartsWith("foreach ", StringComparison.Ordinal) && !line.StartsWith("while ", StringComparison.Ordinal) &&
        !line.StartsWith("switch ", StringComparison.Ordinal) && !line.StartsWith("return ", StringComparison.Ordinal);

    private static string LogicalDeclaration(string[] lines, int start)
    {
        var declaration = new StringBuilder(lines[start].Trim());
        int depth = lines[start].Count(c => c == '(') - lines[start].Count(c => c == ')');
        for (int i = start + 1; i < lines.Length && i <= start + 20; i++)
        {
            string next = lines[i].Trim();
            declaration.Append(' ').Append(next);
            depth += next.Count(c => c == '(') - next.Count(c => c == ')');
            if (depth <= 0 && (next.Contains('{') || next.Contains("=>", StringComparison.Ordinal) || next.StartsWith(':')))
                break;
            if (next.EndsWith(';')) break;
        }
        return declaration.ToString();
    }

    [GeneratedRegex(@"^\s*(?:pub(?:\([^)]*\))?\s+)?(?:async\s+)?fn\s+([A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex RustFunctionRegex();
    [GeneratedRegex(@"^\s*(?:pub(?:\([^)]*\))?\s+)?(struct|enum|trait|type)\s+([A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex RustTypeRegex();
    [GeneratedRegex(@"^\s*(?:(?:public|private|internal|protected|static|sealed|abstract|partial|readonly|file|ref)\s+)*(class|record(?:\s+struct)?|struct|interface|enum)\s+([A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex CSharpTypeRegex();
    [GeneratedRegex(@"^\s*(?:(?:public|private|internal|protected|static|sealed|abstract|partial|virtual|override|async|unsafe|extern|new|readonly)\s+)*(?:[A-Za-z_][A-Za-z0-9_<>,.\[\]?]*\s+)+([A-Za-z_][A-Za-z0-9_]*)\s*\([^;]*\)\s*(?:=>|\{|$)")]
    private static partial Regex CSharpMethodRegex();
    [GeneratedRegex(@"^\s*(?:(?:public|private|internal|protected|static|unsafe|extern)\s+)*([A-Za-z_][A-Za-z0-9_]*)\s*\([^;]*\)\s*(?::[^\{]+)?\{")]
    private static partial Regex CSharpConstructorRegex();
    [GeneratedRegex(@"\b(?:CMSG|SMSG|MSG)_[A-Z0-9_]+\b")]
    private static partial Regex OpcodeRegex();
    [GeneratedRegex(@"\b[A-Z][A-Z0-9_]{2,}\b")]
    private static partial Regex UpperIdentifierRegex();
    [GeneratedRegex(@"^\s*(?:(?:public|private|internal|protected|pub(?:\([^)]*\))?)\s+)?const\s+(?:(?:u16|u32|usize|ushort|uint|int|short|byte)\s+)?([A-Z][A-Z0-9_]+)\s*(?::[^=]+)?=")]
    private static partial Regex UpdateFieldDeclarationRegex();
    [GeneratedRegex("[\"']([^\"']*(?:Interface[\\\\/]|\\.(?:blp|m2|wav|ogg|mp3|xml|wgsl|vert|frag))[^\"']*)[\"']", RegexOptions.IgnoreCase)]
    private static partial Regex AssetRegex();
    [GeneratedRegex(@"\b([A-Za-z_][A-Za-z0-9_]*(?:Dbc|DBC|Catalog)|[A-Za-z_][A-Za-z0-9_]*\.dbc)\b")]
    private static partial Regex DbcRegex();
    [GeneratedRegex("[\"']((?:UNIT|PLAYER|PET|SPELL|ACTIONBAR|BAG|BANK|MAIL|GUILD|QUEST|TRADE|AUCTION|PARTY|RAID|CHAT|UPDATE|VARIABLES|CHARACTER|SKILL|TALENT|MERCHANT|TRAINER)_[A-Z0-9_]+)[\"']")]
    private static partial Regex EventStringRegex();
    [GeneratedRegex(@"ImGui\.(Begin|Button|InvisibleButton|MenuItem|BeginMenu|BeginPopup|OpenPopup|BeginTabItem|Selectable|Checkbox|RadioButton|InputText)\s*\(\s*\$?@?""([^""]*)""")]
    private static partial Regex ImGuiRegex();
    [GeneratedRegex(@"(?:@(?:vertex|fragment|compute)\s+)?fn\s+([A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex ShaderEntryRegex();
    [GeneratedRegex(@"@(group|binding)\s*\(\s*\d+\s*\)")]
    private static partial Regex ShaderBindingRegex();
    [GeneratedRegex(@"^\s*(?:\[([^]]+)\]|<([A-Za-z][A-Za-z0-9_.:-]*))")]
    private static partial Regex ManifestEntryRegex();
    [GeneratedRegex(@"^\s*function\s+([A-Za-z_][A-Za-z0-9_.:]*)")]
    private static partial Regex LuaFunctionRegex();
}
