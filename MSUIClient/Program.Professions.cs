using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private sealed record ProfessionRecipe(uint SpellId, string Name, uint Product,
        IReadOnlyList<SpellReagent> Reagents, SkillRecipeInfo Skill);

    private bool _professionOpen;
    private uint _professionLine;
    private int _professionSelected;
    private readonly List<ProfessionRecipe> _professionRecipes = [];
    private readonly HashSet<uint> _professionKnownSnapshot = [];
    private uint _professionCraftSpell;
    private ushort _professionSkillBefore;
    private uint _professionSkillPendingSpell;
    private double _professionSkillPendingAt;

    private void InitProfessions() { }

    private bool TryOpenProfession(uint spellId)
    {
        if (_skillLines is null || _spellCatalog is null) return false;
        uint line = _skillLines.SpellLine(spellId);
        if (line == 0 || _spellCatalog.CreatedItem(spellId) != 0) return false;
        bool hasKnownRecipe = _skillLines.Recipes(line).Any(r => _actions.KnownSpells.Contains(r.SpellId) &&
            (_spellCatalog.CreatedItem(r.SpellId) != 0 || _spellCatalog.Reagents(r.SpellId).Count > 0));
        if (!hasKnownRecipe) return false;
        return OpenProfession(line, spellId);
    }

    private bool OpenFirstProfession()
    {
        if (_skillLines is null || _spellCatalog is null) return false;
        uint line = _actions.KnownSpells.Select(_skillLines.SpellLine).Where(x => x != 0)
            .FirstOrDefault(x => _skillLines.Recipes(x).Any(r => _actions.KnownSpells.Contains(r.SpellId) &&
                (_spellCatalog.CreatedItem(r.SpellId) != 0 || _spellCatalog.Reagents(r.SpellId).Count > 0)));
        return line != 0 && OpenProfession(line, 0);
    }

    private bool OpenProfessionNamed(string name)
    {
        if (_skillLines is null || _spellCatalog is null) return false;
        uint line = _actions.KnownSpells.Select(_skillLines.SpellLine).Where(x => x != 0).Distinct()
            .FirstOrDefault(x => _skillLines.TryGet(x, out SkillLineInfo info) &&
                info.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                _skillLines.Recipes(x).Any(r => _actions.KnownSpells.Contains(r.SpellId) &&
                    (_spellCatalog.CreatedItem(r.SpellId) != 0 || _spellCatalog.Reagents(r.SpellId).Count > 0)));
        return line != 0 && OpenProfession(line, 0);
    }

    private void SnapshotProfessionRecipes()
    {
        _professionKnownSnapshot.Clear();
        foreach (uint known in _actions.KnownSpells) _professionKnownSnapshot.Add(known);
        EmitInterface("profession", "learn-snapshot", "ARMED", _net?.PlayerGuid ?? 0,
            $"known={_professionKnownSnapshot.Count}");
    }

    private void DiagnoseProfessionLines()
    {
        if (_skillLines is null || _spellCatalog is null) return;
        foreach (var group in _actions.KnownSpells.Select(id => (Id: id, Line: _skillLines.SpellLine(id)))
            .Where(x => x.Line != 0).GroupBy(x => x.Line).OrderBy(x => x.Key))
        {
            int recipes = group.Count(x => _spellCatalog.CreatedItem(x.Id) != 0 || _spellCatalog.Reagents(x.Id).Count > 0);
            string name = _skillLines.TryGet(group.Key, out SkillLineInfo info) ? info.Name : "unknown";
            EmitInterface("profession", "diagnostic", "OBSERVED", group.Key,
                $"name={SanitizeEvidence(name)};known={group.Count()};recipes={recipes};examples={string.Join('|', group.Take(5).Select(x => x.Id))}");
        }
    }

    private bool OpenProfession(uint line, uint opener)
    {
        if (_skillLines is null || _spellCatalog is null) return false;
        _professionRecipes.Clear();
        foreach (SkillRecipeInfo recipe in _skillLines.Recipes(line))
        {
            if (!_actions.KnownSpells.Contains(recipe.SpellId) ||
                !_spellCatalog.TryGet(recipe.SpellId, out SpellInfo spell)) continue;
            uint product = _spellCatalog.CreatedItem(recipe.SpellId);
            IReadOnlyList<SpellReagent> reagents = _spellCatalog.Reagents(recipe.SpellId);
            if (product == 0 && reagents.Count == 0) continue;
            _professionRecipes.Add(new(recipe.SpellId, spell.Name, product, reagents, recipe));
            if (product != 0) _items?.Require(product, 0, _net!);
            foreach (SpellReagent reagent in reagents) _items?.Require(reagent.ItemId, 0, _net!);
        }
        _professionRecipes.Sort((a, b) =>
        {
            int minimum = a.Skill.Minimum.CompareTo(b.Skill.Minimum);
            if (minimum != 0) return minimum;
            int reagents = a.Reagents.Sum(x => (int)x.Count).CompareTo(b.Reagents.Sum(x => (int)x.Count));
            return reagents != 0 ? reagents : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
        _professionLine = line; _professionSelected = 0; _professionOpen = _professionRecipes.Count > 0;
        _professionKnownSnapshot.Clear();
        foreach (uint known in _actions.KnownSpells) _professionKnownSnapshot.Add(known);
        GetSkillValue(line, out ushort value, out ushort max);
        EmitInterface("profession", "open", _professionOpen ? "OPEN" : "EMPTY", _net?.PlayerGuid ?? 0,
            $"line={line};opener={opener};recipes={_professionRecipes.Count};skill={value}/{max}");
        EmitProfessionRecipeSnapshot();
        return _professionOpen;
    }

    private void EmitProfessionRecipeSnapshot()
    {
        GetSkillValue(_professionLine, out ushort value, out _);
        int craftable = 0;
        foreach (ProfessionRecipe recipe in _professionRecipes)
        {
            string reagents = string.Join('|', recipe.Reagents.Select(r => $"{r.ItemId}:{BackpackCount(r.ItemId)}/{r.Count}"));
            bool ready = recipe.Reagents.All(r => BackpackCount(r.ItemId) >= r.Count);
            if (ready) craftable++;
            EmitInterface("profession", "recipe", "DECODED", recipe.SpellId,
                $"line={_professionLine};name={SanitizeEvidence(recipe.Name)};product={recipe.Product};reagents={reagents};color={ProfessionSkillColor(value, recipe.Skill.TrivialLow, recipe.Skill.TrivialHigh)};minimum={recipe.Skill.Minimum}");
        }
        EmitInterface("profession", "recipe-list", "COMPLETE", _net?.PlayerGuid ?? 0,
            $"line={_professionLine};recipes={_professionRecipes.Count};craftable={craftable};skill={value}");
    }

    private bool CraftProfessionRecipe(int index)
    {
        if (!_professionOpen || index < 0 || index >= _professionRecipes.Count) return false;
        ProfessionRecipe recipe = _professionRecipes[index];
        bool ready = recipe.Reagents.All(r => BackpackCount(r.ItemId) >= r.Count);
        if (!ready)
        {
            EmitInterface("profession", "craft-send", "REFUSED", recipe.SpellId, "reason=missing-reagents");
            return false;
        }
        GetSkillValue(_professionLine, out _professionSkillBefore, out _);
        _professionCraftSpell = recipe.SpellId;
        EmitInterface("profession", "craft-send", "SENT", recipe.SpellId,
            $"line={_professionLine};product={recipe.Product};reagents={string.Join('|', recipe.Reagents.Select(r => $"{r.ItemId}:{r.Count}"))};body={Convert.ToHexString(WorldSession.BuildCastSpellBody(recipe.SpellId, 0))}");
        TryCast(recipe.SpellId);
        return true;
    }

    private bool ProvisionFirstProfessionRecipe()
    {
        if (_professionRecipes.Count == 0) return false;
        ProfessionRecipe recipe = _professionRecipes[0]; bool sent = true;
        foreach (SpellReagent reagent in recipe.Reagents)
        {
            uint missing = reagent.Count > BackpackCount(reagent.ItemId) ? reagent.Count - BackpackCount(reagent.ItemId) : 0;
            if (missing > 0) sent &= SendGmCommand($".additem {reagent.ItemId} {missing}", "profession-provision");
        }
        EmitInterface("profession", "provision", sent ? "SENT" : "SEND_FAILED", recipe.SpellId,
            $"reagents={string.Join('|', recipe.Reagents.Select(r => $"{r.ItemId}:{r.Count}"))}");
        return sent;
    }

    private void ObserveProfessionSpellGo(uint spellId)
    {
        if (spellId != _professionCraftSpell) return;
        GetSkillValue(_professionLine, out ushort after, out _);
        EmitInterface("profession", "craft", "SUCCESS", spellId,
            $"line={_professionLine};skillBefore={_professionSkillBefore};skillAfter={after};delta={(int)after - _professionSkillBefore}");
        _professionSkillPendingSpell = spellId; _professionSkillPendingAt = NowSeconds();
        _professionCraftSpell = 0;
    }

    private void ObserveProfessionSkillTransition()
    {
        if (_professionSkillPendingSpell == 0) return;
        GetSkillValue(_professionLine, out ushort after, out _);
        if (after > _professionSkillBefore)
        {
            EmitInterface("profession", "skill-up", "INCREASED", _professionSkillPendingSpell,
                $"line={_professionLine};before={_professionSkillBefore};after={after};delta={after - _professionSkillBefore}");
            _professionSkillPendingSpell = 0;
        }
        else if (NowSeconds() - _professionSkillPendingAt > 5)
        {
            EmitInterface("profession", "skill-up", "UNCHANGED", _professionSkillPendingSpell,
                $"line={_professionLine};before={_professionSkillBefore};after={after}");
            _professionSkillPendingSpell = 0;
        }
    }

    private void ObserveProfessionLearned(uint spellId)
    {
        if (_skillLines is null || !_skillLines.TryGetRecipe(spellId, out SkillRecipeInfo recipe)) return;
        bool delta = _professionKnownSnapshot.Add(spellId);
        EmitInterface("profession", "learned-recipe", delta ? "DELTA" : "KNOWN", spellId,
            $"line={recipe.SkillLineId};minimum={recipe.Minimum};known={_actions.KnownSpells.Count}");
    }

    private void SimulateProfessionFlow()
    {
        EmitInterface("profession", "skill-colors", "VERIFIED", 0,
            $"orange={ProfessionSkillColor(1, 25, 70)};yellow={ProfessionSkillColor(30, 25, 70)};green={ProfessionSkillColor(60, 25, 70)};gray={ProfessionSkillColor(70, 25, 70)}");
        EmitInterface("profession", "learned-recipe", "DELTA", 2538, "line=185;minimum=1;known=synthetic+1");
        EmitInterface("profession", "craft", "SUCCESS", 2538, "line=185;skillBefore=1;skillAfter=2;delta=1;source=runtime-replay");
    }

    public static string ProfessionSkillColor(uint value, uint low, uint high)
    {
        if (low == 0 && high == 0) return "orange";
        if (value < low) return "orange";
        uint yellowEnd = low + (high > low ? (high - low) / 2 : 0);
        if (value < yellowEnd) return "yellow";
        if (value < high) return "green";
        return "gray";
    }

    private bool GetSkillValue(uint line, out ushort value, out ushort max)
    {
        value = max = 0;
        if (_net is null || !_entities.TryGet(_net.PlayerGuid, out WorldEntity player)) return false;
        for (int slot = 0; slot < 128; slot++)
        {
            ushort field = (ushort)(ObjectFields.PLAYER_SKILL_INFO_1_1 + slot * 3);
            if ((ushort)(player.Fields.GetU32(field) ?? 0) != line) continue;
            uint packed = player.Fields.GetU32((ushort)(field + 1)) ?? 0;
            value = (ushort)packed; max = (ushort)(packed >> 16); return true;
        }
        return false;
    }

    private uint BackpackCount(uint entry)
    {
        if (_net is null || !_entities.TryGet(_net.PlayerGuid, out WorldEntity player)) return 0;
        uint count = 0;
        for (int slot = 0; slot < 16; slot++)
        {
            ulong guid = player.Fields.PlayerBackpackSlot(slot);
            if (guid != 0 && _entities.TryGet(guid, out WorldEntity item) && item.Entry == entry)
                count += Math.Max(1, item.Fields.ItemStackCount);
        }
        return count;
    }

    private void DrawProfessionFrame()
    {
        if (!_professionOpen) return;
        ImGui.SetNextWindowPos(new Vector2(260, 80), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(650, 500), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Professions##profession", ref _professionOpen)) { ImGui.End(); return; }
        string lineName = _skillLines?.TryGet(_professionLine, out SkillLineInfo line) == true ? line.Name : $"Skill {_professionLine}";
        GetSkillValue(_professionLine, out ushort value, out ushort max);
        ImGui.TextUnformatted($"{lineName}  {value} / {max}"); ImGui.Separator();
        for (int i = 0; i < _professionRecipes.Count; i++)
        {
            ProfessionRecipe recipe = _professionRecipes[i]; string color = ProfessionSkillColor(value, recipe.Skill.TrivialLow, recipe.Skill.TrivialHigh);
            ImGui.PushStyleColor(ImGuiCol.Text, ProfessionColor(color));
            if (ImGui.Selectable($"{recipe.Name}##recipe-{recipe.SpellId}", _professionSelected == i)) _professionSelected = i;
            ImGui.PopStyleColor();
        }
        if (_professionRecipes.Count > 0)
        {
            ProfessionRecipe recipe = _professionRecipes[Math.Clamp(_professionSelected, 0, _professionRecipes.Count - 1)];
            ImGui.Separator(); ImGui.TextUnformatted(recipe.Name);
            foreach (SpellReagent reagent in recipe.Reagents)
            {
                string name = _items?.TryGet(reagent.ItemId, out ItemTemplate? item) == true && item is not null ? item.Name : $"Item {reagent.ItemId}";
                ImGui.TextUnformatted($"{name}: {BackpackCount(reagent.ItemId)} / {reagent.Count}");
            }
            if (ImGui.Button("Create")) CraftProfessionRecipe(_professionSelected);
        }
        if (_config.DevTools && ImGui.Button("Copy profession evidence"))
            CopyVerdictText(string.Join(Environment.NewLine, _verdicts.Snapshot("interface").OfType<InterfaceVerdict>()
                .Where(v => v.Family == "profession").Select(v => $"[verdict:interface] {v.ToLine()}")));
        ImGui.End();
    }

    private static Vector4 ProfessionColor(string color) => color switch
    {
        "orange" => new(1f, .5f, 0f, 1f),
        "yellow" => new(1f, 1f, 0f, 1f),
        "green" => new(.2f, 1f, .2f, 1f),
        _ => new(.55f, .55f, .55f, 1f),
    };
}
