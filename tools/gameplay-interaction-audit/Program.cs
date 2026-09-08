using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MSUIClient;
using MSUIClient.Formats;

// Read-only discovery, using the shipping MPQ priority and DBC readers.
// Literal matches are leads, never evidence that an event actually plays a cue.
string root = ClientConfig.FindRepoRoot();
string output = args.Length > 0 ? Path.GetFullPath(args[0]) :
    Path.Combine(root, "docs", "current", "gameplay-interaction-audit.json");
using var mpq = new MpqMount(Path.Combine(root, "GameData", "Data"));
SoundEntriesCatalog sounds = SoundEntriesCatalog.Load(mpq)
    ?? throw new InvalidDataException("SoundEntries.dbc unavailable");
var sources = Directory.EnumerateFiles(Path.Combine(root, "MSUIClient"), "*.cs",
        SearchOption.AllDirectories)
    .Where(path => !Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar)
        .Any(part => part is "obj" or "bin"))
    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
    .Select(path => (Path: Path.GetRelativePath(root, path).Replace('\\', '/'),
        Lines: File.ReadAllLines(path))).ToArray();
var soundCalls = new Regex("\\bPlaySound\\s*\\(\\s*[\"'](?<cue>[^\"']+)[\"']",
    RegexOptions.CultureInvariant);
var interactionCalls = new Regex(@"\b(PickupSpell|PickupMacro|PickupAction|PlaceAction|PickupPetAction|PickupContainerItem|PickupInventoryItem|SplitContainerItem|ClearCursor|SetCursor|ResetCursor|SetButtonState|SetDesaturated|SetVertexColor|CooldownFrame_SetTimer|SetChecked)\s*\(");
var visualTags = new Regex(@"<(PushedTexture|HighlightTexture|DisabledTexture|CheckedTexture|OnDragStart|OnReceiveDrag|OnEnter|OnLeave)\b");
var cues = new SortedDictionary<string, List<Site>>(StringComparer.OrdinalIgnoreCase);
var interactions = new List<Interaction>();
int scanned = 0;
foreach (string path in mpq.ListedFiles().Where(path =>
             path.StartsWith(@"Interface\FrameXML\", StringComparison.OrdinalIgnoreCase) &&
             (path.EndsWith(".lua", StringComparison.OrdinalIgnoreCase) ||
              path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
{
    var reference = mpq.ReadFileWithSupplier(path)
        ?? throw new InvalidDataException($"Cannot read {path}");
    byte[] bytes = reference.Data;
    scanned++;
    string[] lines = Encoding.Latin1.GetString(bytes).Split('\n');
    for (int i = 0; i < lines.Length; i++)
    {
        // Comments are intentionally retained: this is a discovery inventory, not a Lua parser.
        foreach (Match match in soundCalls.Matches(lines[i]))
        {
            string cue = match.Groups["cue"].Value;
            if (!cues.TryGetValue(cue, out var sites)) cues[cue] = sites = [];
            sites.Add(new(path, i + 1, reference.Supplier));
        }
        foreach (Match match in interactionCalls.Matches(lines[i]))
            interactions.Add(new(match.Groups[1].Value, new(path, i + 1, reference.Supplier)));
        foreach (Match match in visualTags.Matches(lines[i]))
            interactions.Add(new(match.Groups[1].Value, new(path, i + 1, reference.Supplier)));
    }
}
// These gestures originate in native APIs, so a FrameXML PlaySound scan cannot find them.
foreach (string cue in new[] { "igSpellBookSpellIconPickup", "igSpellBookSpellIconDrop",
             "igAbilityIconPickup", "igAbilityIconDrop" })
    cues.TryAdd(cue, []);
var itemGestures = new List<ItemGesture>();
DbcFile groups = DbcFile.Parse(mpq.ReadFile(ItemGroupSoundsCatalog.MpqPath)
    ?? throw new InvalidDataException("ItemGroupSounds.dbc unavailable"))
    ?? throw new InvalidDataException("Invalid ItemGroupSounds.dbc");
if (groups.FieldCount != 5) throw new InvalidDataException("ItemGroupSounds schema changed");
for (int row = 0; row < groups.RecordCount; row++)
    for (int gesture = 0; gesture < 3; gesture++)
    {
        uint id = groups.GetUInt(row, gesture + 1);
        bool found = sounds.TryGet(id, out SoundEntry entry);
        itemGestures.Add(new(groups.GetUInt(row, 0), ((ItemSoundGesture)gesture).ToString(),
            id, id == 0 ? "authored silence" : found ? entry.Name : "MISSING KIT"));
        if (found) cues.TryAdd(entry.Name, []);
    }
var records = new List<Cue>();
foreach ((string name, List<Site> sites) in cues)
{
    var matches = new List<Site>();
    foreach (var source in sources)
        for (int i = 0; i < source.Lines.Length; i++)
            if (source.Lines[i].Contains('"' + name + '"', StringComparison.OrdinalIgnoreCase))
                matches.Add(new(source.Path, i + 1));
    bool found = sounds.TryGet(name, out SoundEntry entry);
    var assets = new List<Asset>();
    if (found)
        foreach (SoundVariant variant in entry.Variants)
        {
            var asset = mpq.ReadFileWithSupplier(variant.Path);
            byte[]? bytes = asset?.Data;
            assets.Add(new(variant.Path, bytes?.Length ?? 0, asset?.Supplier,
                bytes is null ? "missing" : bytes.Length == 0 ? "empty" : "readable"));
        }
    records.Add(new(name, found ? entry.Id : null, found ? entry.Volume : null,
        sites, matches, assets, !found ? "missing kit" : assets.Count == 0 ? "empty kit" :
        assets.Any(asset => asset.Bytes == 0) ? "missing/empty asset" :
        matches.Count == 0 ? "no literal source match; trace native/data-driven route" :
        "literal source match; handler review required"));
}
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
File.WriteAllText(output, JsonSerializer.Serialize(new
{
    SchemaVersion = 1,
    Scope = "Mounted FrameXML sound literals, interaction APIs, visual hooks, native icon cues and item gesture kits",
    Limitations = "Regex includes comments. Dynamic/numeric sound calls and inherited behavior require manual tracing. Readable assets are not playback proof. No runtime verification.",
    FrameXmlFiles = scanned,
    Cues = records,
    ItemGestures = itemGestures,
    InteractionSites = interactions,
}, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
Console.WriteLine($"gameplay-interaction-audit: {scanned} FrameXML files, {records.Count} cues, " +
    $"{interactions.Count} interaction/visual sites, {itemGestures.Count} item gestures");
foreach (var group in records.GroupBy(record => record.Status))
    Console.WriteLine($"  {group.Count()}: {group.Key}");
Console.WriteLine(output);

internal sealed record Site(string Path, int Line, string? Archive = null);
internal sealed record Interaction(string Behavior, Site Reference);
internal sealed record ItemGesture(uint Group, string Gesture, uint Kit, string Name);
internal sealed record Asset(string Path, int Bytes, string? Archive, string Status);
internal sealed record Cue(string Name, uint? Kit, float? Volume, List<Site> ReferenceSites,
    List<Site> SourceLiteralMatches, List<Asset> Assets, string Status);
