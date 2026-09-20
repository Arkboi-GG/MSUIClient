using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualBasic.FileIO;
using MSUIClient;
using MSUIClient.Formats;

// Discovery only. Never executes a generated candidate or promotes discovery to a live pass.
string root = ClientConfig.FindRepoRoot();
if (args.Length != 2) throw new ArgumentException("usage: gameplay-coverage <quest_template.csv> <output-directory>");
string questPath = Path.GetFullPath(args[0]), output = Path.GetFullPath(args[1]);
Directory.CreateDirectory(output);
using var mpq = new MpqMount(Path.Combine(root, "GameData", "Data"));
var spells = SpellCatalog.Load(mpq) ?? throw new InvalidDataException("Spell catalog unavailable");
var visuals = SpellVisualCatalog.Load(mpq) ?? throw new InvalidDataException("Spell visual catalog unavailable");
var skills = SkillLineCatalog.Load(mpq) ?? throw new InvalidDataException("Skill catalog unavailable");
var assets = new SortedDictionary<string, (int Bytes, string Supplier)>(StringComparer.OrdinalIgnoreCase);
string Asset(string raw)
{
    string path = SpellVisualCatalog.ModelPath(raw);
    if (!assets.ContainsKey(path))
    {
        var found = mpq.ReadFileWithSupplier(path);
        assets[path] = (found?.Data.Length ?? 0, found?.Supplier ?? "");
    }
    return path;
}
var routes = skills.AbilityRows.GroupBy(x => x.SpellId).ToDictionary(x => x.Key, x => x.ToArray());
var spellRows = new List<string[]>();
var stageRows = new List<string[]>();
var families = new SortedDictionary<string, List<uint>>(StringComparer.Ordinal);
foreach (SpellInfo spell in spells.Spells.OrderBy(x => x.Id))
{
    bool resolved = visuals.TryGetStages(spell.VisualId, out var stages);
    string family = $"{spell.CastClassification};visual={spell.VisualId};effects={Join(spell.EffectIds)};auras={Join(spell.AuraIds)};targets={Join(spell.ImplicitTargetsA)}/{Join(spell.ImplicitTargetsB)};missile={spell.Speed > 0};passive={spell.Passive}";
    if (!families.TryGetValue(family, out var members)) families[family] = members = [];
    members.Add(spell.Id);
    string cohort = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(family)))[..16];
    var ability = routes.GetValueOrDefault(spell.Id) ?? [];
    string skillLines = string.Join('|', ability.Select(x => x.SkillLineId).Distinct().Order());
    string classMasks = string.Join('|', ability.Select(x => x.ClassMask).Distinct().Order());
    spellRows.Add([S(spell.Id), spell.Name, spell.Rank, S(spell.SpellFamily), skillLines, classMasks,
        spell.Passive.ToString(), spell.HiddenClientSide.ToString(), spell.TradeSkill.ToString(),
        spell.CastClassification, S(spell.CastTimeMs), S(spell.DurationMs), S(spell.VisualId),
        Join(spell.EffectIds), Join(spell.AuraIds), Join(spell.ImplicitTargetsA), Join(spell.ImplicitTargetsB),
        cohort, spell.VisualId == 0 ? "NO_AUTHORED_VISUAL" : resolved ? "VISUAL_ROW_RESOLVED" : "VISUAL_ROW_MISSING",
        "UNTESTED", "UNREVIEWED"]);
    if (!resolved) continue;
    foreach (SpellStage stage in Enum.GetValues<SpellStage>())
    {
        uint kitId = SpellVisualCatalog.KitFor(stages, stage);
        if (kitId == 0) continue;
        bool kitFound = visuals.TryGetKit(kitId, out var kit);
        string[] paths = kitFound ? kit.Effects.Select(x => Asset(x.ModelPath)).Distinct().Order().ToArray() : [];
        stageRows.Add([S(spell.Id), stage.ToString(), S(kitId), kitFound ? S(kit.AnimationId) : "",
            string.Join('|', paths), kitFound ? string.Join('|', kit.CharProcs.Select(x => x.Type).Distinct()) : "",
            !kitFound ? "KIT_MISSING" : paths.Any(p => assets[p].Bytes == 0) ? "ASSET_REFERENCE_UNRESOLVED" : "REFERENCES_RESOLVED",
            "UNREVIEWED"]);
    }
    if (visuals.MissilePath(stages) is { } missile)
    {
        string path = Asset(missile);
        stageRows.Add([S(spell.Id), "Missile", "", "", path, "",
            assets[path].Bytes == 0 ? "ASSET_REFERENCE_UNRESOLVED" : "REFERENCES_RESOLVED", "UNREVIEWED"]);
    }
    if (visuals.TryGetAreaVisual(spell.VisualId, out var area))
    {
        string[] paths = area.Emitters.Select(x => x.ModelPath)
            .Concat(area.LoopingModelPath is { } loop ? [loop] : []).Select(Asset).Distinct().Order().ToArray();
        stageRows.Add([S(spell.Id), "Area", S(stages.AreaKit), "", string.Join('|', paths), "9",
            paths.Any(p => assets[p].Bytes == 0) ? "ASSET_REFERENCE_UNRESOLVED" : "REFERENCES_RESOLVED", "UNREVIEWED"]);
    }
}
Write("spells.csv", "spell_id,name,rank,spell_family,skill_lines,class_masks,passive,hidden,tradeskill,cast_type,cast_ms,duration_ms,visual_id,effects,auras,targets_a,targets_b,cohort,data_status,live_status,visual_review", spellRows);
Write("spell-stages.csv", "spell_id,stage,kit_id,animation_id,models,char_proc_types,data_status,visual_review", stageRows);
Write("spell-cohorts.csv", "cohort,representative_candidate,member_count,spell_ids,signature,status", families.Select(pair => new[] {
    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pair.Key)))[..16], S(pair.Value[0]),
    S(pair.Value.Count), string.Join('|', pair.Value), pair.Key, "UNTESTED_CANDIDATES_ONLY" }));
Write("visual-assets.csv", "path,bytes,supplier,data_status", assets.Select(pair => new[] {
    pair.Key, S(pair.Value.Bytes), pair.Value.Supplier, pair.Value.Bytes > 0 ? "READABLE" : "UNRESOLVED_REFERENCE_TRIAGE" }));

var quests = ReadCsv(questPath);
string[] required = ["entry", "patch", "Method", "SpecialFlags", "LimitTime", "Title", "ReqItemId1", "ReqCreatureOrGOId1", "ReqSpellCast1", "StartScript", "CompleteScript"];
foreach (string field in required)
    if (quests.Count == 0 || !quests[0].ContainsKey(field)) throw new InvalidDataException($"Quest export missing {field}");
// Match the shipping Quest Helper's content-patch selection, including disabled rows in inventory.
var latest = quests.Where(q => N(q, "patch") <= 10).GroupBy(q => N(q, "entry"))
    .Select(g => g.OrderByDescending(q => N(q, "patch")).First()).OrderBy(q => N(q, "entry")).ToArray();
var questRows = new List<string[]>();
var questFamilies = new SortedDictionary<string, List<long>>(StringComparer.Ordinal);
foreach (var q in latest)
{
    var tags = new List<string>();
    bool Any(string field, Func<long, bool> predicate, int count = 4) => Enumerable.Range(1, count).Any(i => predicate(N(q, field + i)));
    if (Any("ReqItemId", n => n != 0)) tags.Add("collect-or-deliver-item");
    if (Any("ReqCreatureOrGOId", n => n > 0)) tags.Add("creature-kill-or-credit");
    if (Any("ReqCreatureOrGOId", n => n < 0)) tags.Add("gameobject-credit");
    if (Any("ReqSpellCast", n => n != 0)) tags.Add("cast-on-objective");
    if ((N(q, "SpecialFlags") & 2) != 0) tags.Add("exploration-or-scripted-event");
    if (tags.Count == 0) tags.Add("dialogue-or-script-review");
    if ((N(q, "SpecialFlags") & 1) != 0) tags.Add("repeatable");
    if (N(q, "LimitTime") > 0) tags.Add("timed");
    if (N(q, "StartScript") != 0 || N(q, "CompleteScript") != 0) tags.Add("database-script");
    if (N(q, "PrevQuestId") != 0 || N(q, "NextQuestInChain") != 0 || N(q, "NextQuestId") != 0) tags.Add("chain");
    if (N(q, "ExclusiveGroup") != 0 || N(q, "BreadcrumbForQuestId") != 0) tags.Add("branch-or-breadcrumb");
    if (Any("RewChoiceItemId", n => n != 0, 6)) tags.Add("reward-choice");
    if (N(q, "RewOrReqMoney") < 0) tags.Add("money-required");
    if (N(q, "RepObjectiveFaction") != 0) tags.Add("reputation-objective");
    if (N(q, "SrcItemId") != 0 || N(q, "SrcSpell") != 0) tags.Add("provided-item-or-spell");
    if (N(q, "RewSpell") != 0 || N(q, "RewSpellCast") != 0) tags.Add("spell-reward");
    if (N(q, "RewMailTemplateId") != 0) tags.Add("mail-reward");
    if (N(q, "RequiredClasses") != 0 || N(q, "RequiredRaces") != 0 || N(q, "RequiredSkill") != 0 || N(q, "RequiredCondition") != 0) tags.Add("eligibility-gated");
    foreach (string tag in tags)
    {
        if (!questFamilies.TryGetValue(tag, out var members)) questFamilies[tag] = members = [];
        members.Add(N(q, "entry"));
    }
    questRows.Add([S(N(q, "entry")), q["Title"], S(N(q, "patch")), S(N(q, "Method")),
        S(N(q, "MinLevel")), S(N(q, "QuestLevel")), string.Join('|', tags),
        string.Join('|', Enumerable.Range(1, 4).Select(i => $"{N(q, "ReqCreatureOrGOId" + i)}:{N(q, "ReqCreatureOrGOCount" + i)}")),
        string.Join('|', Enumerable.Range(1, 4).Select(i => $"{N(q, "ReqItemId" + i)}:{N(q, "ReqItemCount" + i)}")),
        S(N(q, "LimitTime")), "UNTESTED", "UNREVIEWED"]);
}
Write("quests.csv", "quest_id,title,patch,method,min_level,quest_level,families,creature_or_go_objectives,item_objectives,time_limit,live_status,visual_review", questRows);
Write("quest-families.csv", "family,quest_count,candidate_ids,status", questFamilies.Select(p => new[] {
    p.Key, S(p.Value.Count), string.Join('|', p.Value), "UNTESTED_CANDIDATES_ONLY" }));
var summary = new {
    Schema = 1, SpellCount = spellRows.Count, SpellCohorts = families.Count, StageReferences = stageRows.Count,
    UniqueVisualAssets = assets.Count, UnresolvedVisualAssets = assets.Count(x => x.Value.Bytes == 0),
    QuestExportSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(questPath))),
    QuestExportRows = quests.Count, ContentPatch = 10, QuestCount = latest.Length, QuestFamilies = questFamilies.Count,
    ArchiveTables = new[] { "Spell", "SpellVisual", "SpellVisualKit", "SpellVisualEffectName",
        "SpellChainEffects", "SpellCastTimes", "SpellDuration", "SkillLine", "SkillLineAbility" }.Select(name =>
    {
        string path = $@"DBFilesClient\{name}.dbc";
        var source = mpq.ReadFileWithSupplier(path);
        return new { Path = path, Supplier = source?.Supplier,
            Sha256 = source is { } found ? Convert.ToHexString(SHA256.HashData(found.Data)) : "MISSING" };
    }).ToArray(),
    LivePassesGrantedByThisTool = 0,
    Limitations = "Discovery, not execution. Cohorts do not transfer passes. Quest families overlap; escort/defense/script semantics need source and live review. Unresolved assets may be unused legacy rows. Spell chains, nested textures/sounds, equipment, creature models and scripted server-only triggers need further coverage."
};
File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(summary));

void Write(string name, string header, IEnumerable<string[]> rows) => File.WriteAllLines(Path.Combine(output, name),
    new[] { header }.Concat(rows.Select(row => string.Join(',', row.Select(v => '"' + v.Replace("\"", "\"\"") + '"')))), new UTF8Encoding(false));
static string S(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
static string Join(uint[]? values) => string.Join('|', values ?? []);
static long N(Dictionary<string, string> row, string key)
{
    if (!row.TryGetValue(key, out string? text)) throw new InvalidDataException($"Missing quest field {key}");
    // The live export prefixes negative cells with an apostrophe for spreadsheet safety.
    // Losing that sign misclassifies gameobject objectives and money requirements as zero.
    if (text.StartsWith('\'')) text = text[1..];
    return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n)
        ? n : throw new InvalidDataException($"Invalid numeric quest field {key}: {text}");
}
static List<Dictionary<string, string>> ReadCsv(string path)
{
    using var csv = new TextFieldParser(path) { TextFieldType = FieldType.Delimited };
    csv.SetDelimiters(",");
    string[] header = csv.ReadFields() ?? throw new InvalidDataException("Missing CSV header");
    var rows = new List<Dictionary<string, string>>();
    while (!csv.EndOfData)
    {
        string[] fields = csv.ReadFields() ?? [];
        if (fields.Length != header.Length) throw new InvalidDataException("Invalid quest CSV row width");
        rows.Add(header.Select((h, i) => (h, fields[i])).ToDictionary(x => x.h, x => x.Item2, StringComparer.OrdinalIgnoreCase));
    }
    return rows;
}
