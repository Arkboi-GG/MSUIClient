using System.Globalization;
using System.Text;
using MSUIClient;
using MSUIClient.Formats;

string root = ClientConfig.FindRepoRoot();
string data = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(root, "GameData", "Data");
string output = args.Length > 1 ? Path.GetFullPath(args[1]) : Path.Combine(root, "NIGHT_05");
Directory.CreateDirectory(output);
using var mpq = new MpqMount(data);
SkillLineCatalog skills = SkillLineCatalog.Load(mpq) ?? throw new InvalidDataException("SkillLine DBCs unavailable");
SpellCatalog spells = SpellCatalog.Load(mpq) ?? throw new InvalidDataException("Spell.dbc unavailable");
LockCatalog locks = LockCatalog.Load(mpq) ?? throw new InvalidDataException("Lock.dbc unavailable");
SpellFocusCatalog foci = SpellFocusCatalog.Load(mpq) ?? throw new InvalidDataException("SpellFocusObject.dbc unavailable");

static string Csv(string value) => '"' + value.Replace("\"", "\"\"") + '"';
var linesOut = new StringBuilder("skillLine,name,category,categoryName,abilityRows,productRecipes,reagentRecipes\n");
foreach (SkillLineInfo line in skills.Lines.OrderBy(x => x.CategoryId).ThenBy(x => x.Id))
{
    IReadOnlyList<SkillRecipeInfo> abilities = skills.Recipes(line.Id);
    int products = abilities.Count(x => spells.CreatedItem(x.SpellId) != 0);
    int reagents = abilities.Count(x => spells.Reagents(x.SpellId).Count != 0);
    if (abilities.Count == 0) continue;
    string category = skills.TryGetCategory(line.CategoryId, out SkillCategoryInfo c) ? c.Name : "";
    linesOut.Append(CultureInfo.InvariantCulture, $"{line.Id},{Csv(line.Name)},{line.CategoryId},{Csv(category)},{abilities.Count},{products},{reagents}\n");
}
File.WriteAllText(Path.Combine(output, "profession-lines.csv"), linesOut.ToString(), new UTF8Encoding(false));

uint[] crafting = [171, 164, 185, 202, 165, 197, 333, 129, 186, 40];
var matrixOut = new StringBuilder("skillLine,profession,ordinal,spellId,recipe,product,minSkill,trivialLow,trivialHigh,reagents,tools,focus\n");
var scenarioRows = new Dictionary<(uint Line, string Name), List<(uint Spell, uint Product)>>();
foreach (uint lineId in crafting)
{
    string name = skills.TryGet(lineId, out SkillLineInfo line) ? line.Name : $"Skill {lineId}";
    var rows = skills.Recipes(lineId)
        .Where(r => spells.TryGet(r.SpellId, out _) && spells.CreatedItem(r.SpellId) != 0)
        .Select(r => (Recipe: r, Spell: spells.TryGet(r.SpellId, out SpellInfo s) ? s : default,
            Product: spells.CreatedItem(r.SpellId), Reagents: spells.Reagents(r.SpellId),
            Tools: spells.Tools(r.SpellId)))
        .Where(x => x.Reagents.Count > 0)
        .GroupBy(x => x.Product).Select(g => g.OrderBy(x => x.Recipe.Minimum).ThenBy(x => x.Recipe.SpellId).First())
        .OrderBy(x => x.Recipe.Minimum).ThenBy(x => x.Recipe.TrivialHigh)
        .ThenBy(x => x.Reagents.Sum(r => r.Count)).ThenBy(x => x.Recipe.SpellId)
        .Take(10).ToArray();
    if (rows.Length != 10)
        throw new InvalidDataException($"{name} supplied {rows.Length} distinct craft outputs; ten are required");
    for (int i = 0; i < rows.Length; i++)
    {
        var x = rows[i];
        matrixOut.Append(CultureInfo.InvariantCulture,
            $"{lineId},{Csv(name)},{i + 1},{x.Recipe.SpellId},{Csv(x.Spell.Name)},{x.Product},{x.Recipe.Minimum},{x.Recipe.TrivialLow},{x.Recipe.TrivialHigh},{Csv(string.Join('|', x.Reagents.Select(r => $"{r.ItemId}:{r.Count}")))},{Csv(string.Join('|', x.Tools))},{x.Spell.RequiredFocus}\n");
    }
    scenarioRows[(lineId, name)] = rows.Select(x => (x.Recipe.SpellId, x.Product)).ToList();
}
File.WriteAllText(Path.Combine(output, "recipe-matrix.csv"), matrixOut.ToString(), new UTF8Encoding(false));
var focusIds = scenarioRows.Values.SelectMany(x => x).Select(x => spells.TryGet(x.Spell, out SpellInfo s) ? s.RequiredFocus : 0)
    .Where(x => x != 0).Distinct().OrderBy(x => x);
File.WriteAllText(Path.Combine(output, "crafting-focus.csv"), "focusId,name\n" +
    string.Join('\n', focusIds.Select(id => $"{id},{Csv(foci.Name(id))}")) + "\n", new UTF8Encoding(false));

if (locks.ResourceLockType(29) != 2 || locks.ResourceLockType(38) != 3)
    throw new InvalidDataException("Lock.dbc gathering anchors failed (Silverleaf 29 / Copper 38)");
if (!spells.TryGet(2383, out SpellInfo herbs) || herbs.AuraIds?.Contains(45u) != true ||
    herbs.EffectMiscValues?.Contains(2) != true) throw new InvalidDataException("Find Herbs DBC anchor failed");
if (!spells.TryGet(2580, out SpellInfo minerals) || minerals.AuraIds?.Contains(45u) != true ||
    minerals.EffectMiscValues?.Contains(3) != true) throw new InvalidDataException("Find Minerals DBC anchor failed");
File.WriteAllText(Path.Combine(output, "gathering-anchors.csv"),
    "spellId,name,aura,lockType,mask,exampleLock\n" +
    "2383,Find Herbs,45,2,0x00000002,29\n" +
    "2580,Find Minerals,45,3,0x00000004,38\n", new UTF8Encoding(false));

var coverageOut = new StringBuilder("skillLine,profession,kind,craftCases,trackingSpell,trackingMask,interactionAbilities\n");
var coverage = new (uint Line, string Kind, uint Tracker, string Mask)[]
{
    (171, "crafting", 0, ""), (164, "crafting", 0, ""),
    (185, "secondary-crafting", 0, ""), (202, "crafting", 0, ""),
    (165, "crafting", 0, ""), (197, "crafting", 0, ""),
    (333, "crafting", 0, ""), (129, "secondary-crafting", 0, ""),
    (186, "gathering-and-smelting", 2580, "0x00000004"),
    (182, "gathering", 2383, "0x00000002"),
    (393, "gathering", 0, ""), (356, "secondary-gathering", 0, ""),
};
foreach (var item in coverage)
{
    if (!skills.TryGet(item.Line, out SkillLineInfo line))
        throw new InvalidDataException($"profession skill line {item.Line} is unavailable");
    var abilities = item.Kind.Contains("gathering", StringComparison.Ordinal)
        ? skills.Recipes(item.Line)
            .Select(x => spells.TryGet(x.SpellId, out SpellInfo spell) ? $"{x.SpellId}:{spell.Name}" : $"{x.SpellId}:?")
            .ToArray()
        : [];
    int craftCases = scenarioRows.TryGetValue((item.Line, line.Name), out var cases) ? cases.Count : 0;
    coverageOut.Append(CultureInfo.InvariantCulture,
        $"{item.Line},{Csv(line.Name)},{item.Kind},{craftCases},{item.Tracker},{item.Mask},{Csv(string.Join('|', abilities))}\n");
}
coverageOut.Append("40,\"Poisons\",class-crafting,10,0,,\"ten product-producing recipes\"\n");
File.WriteAllText(Path.Combine(output, "profession-coverage.csv"), coverageOut.ToString(), new UTF8Encoding(false));

var gatherAbilities = new StringBuilder("skillLine,profession,spellId,name,rank,passive,targetMask,effects,implicitA,implicitB\n");
foreach (uint lineId in new uint[] { 182, 186, 393, 356 })
{
    skills.TryGet(lineId, out SkillLineInfo line);
    foreach (SkillRecipeInfo ability in skills.Recipes(lineId))
    {
        if (!spells.TryGet(ability.SpellId, out SpellInfo spell)) continue;
        gatherAbilities.Append(CultureInfo.InvariantCulture,
            $"{lineId},{Csv(line.Name)},{spell.Id},{Csv(spell.Name)},{Csv(spell.Rank)},{spell.Passive},0x{spell.Targets:X8},{Csv(string.Join('|', spell.EffectIds ?? []))},{Csv(string.Join('|', spell.ImplicitTargetsA ?? []))},{Csv(string.Join('|', spell.ImplicitTargetsB ?? []))}\n");
    }
}
File.WriteAllText(Path.Combine(output, "gathering-abilities.csv"), gatherAbilities.ToString(), new UTF8Encoding(false));

string scenarioDir = Path.Combine(root, "scenarios", "night05");
Directory.CreateDirectory(scenarioDir);
foreach (var group in scenarioRows)
{
    string slug = new(group.Key.Name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
    var s = new StringBuilder();
    s.AppendLine("gm .gm on").AppendLine("select self");
    if (group.Key.Line == 164)
        s.AppendLine("gm .gobject tmpadd 179886 600").AppendLine("gm .gobject tmpadd 179887 600");
    if (group.Key.Line == 202) s.AppendLine("gm .gobject tmpadd 179887 600");
    if (group.Key.Line == 185) s.AppendLine("gm .gobject tmpadd 176897 600");
    if (group.Key.Line == 186)
        s.AppendLine("gm .gobject tmpadd 179886 600").AppendLine("gm .gobject tmpadd 174045 600");
    foreach (var recipe in group.Value) s.AppendLine($"gm .learn {recipe.Spell}");
    s.AppendLine($"gm .setskill {group.Key.Line} 300 300").AppendLine("wait 3")
        .AppendLine($"profession open {group.Key.Name}")
        .AppendLine("assert family=profession step=open outcome=OPEN");
    foreach (var recipe in group.Value)
    {
        s.AppendLine("gm .gm on")
            .AppendLine($"profession provision-spell {recipe.Spell}")
            .AppendLine("wait 1")
            .AppendLine("gm .gm off")
            .AppendLine($"profession craft-spell {recipe.Spell}")
            .AppendLine("probe-interface profession product-created 20")
            .AppendLine("profession cleanup-last")
            .AppendLine("wait 0.3");
    }
    s.AppendLine("dump profession-" + slug + "-10")
        .AppendLine("wait 1")
        .AppendLine("gm .gm off");
    File.WriteAllText(Path.Combine(scenarioDir, $"profession-{slug}-10.txt"), s.ToString(), new UTF8Encoding(false));
}
Console.WriteLine($"profession audit: {output}; lockRows={locks.Count}; spellRows={spells.Count}");
