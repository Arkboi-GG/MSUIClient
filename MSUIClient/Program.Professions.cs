using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private sealed record ProfessionRecipe(uint SpellId, string Name, uint Product,
        IReadOnlyList<SpellReagent> Reagents, IReadOnlyList<uint> Tools, uint RequiredFocus,
        SkillRecipeInfo Skill);

    private bool _professionOpen;
    private uint _professionLine;
    private int _professionSelected;
    private int _professionScroll;
    private readonly List<ProfessionRecipe> _professionRecipes = [];
    private readonly HashSet<uint> _professionKnownSnapshot = [];
    private uint _professionCraftSpell;
    private ushort _professionSkillBefore;
    private uint _professionSkillPendingSpell;
    private double _professionSkillPendingAt;
    private uint _professionProductPending;
    private uint _professionProductBefore;
    private double _professionProductPendingAt;
    private uint _professionProductPendingSpell;
    private bool _professionProductSpellGoObserved;
    private uint _professionLastCreatedProduct;
    private uint _professionLastCreatedDelta;
    private int _professionBatchRemaining;

    private void InitProfessions() { }

    private bool TryOpenProfession(uint spellId)
    {
        if (_skillLines is null || _spellCatalog is null) return false;
        uint line = _skillLines.SpellLine(spellId);
        if (!IsCraftProfessionLine(line) || _spellCatalog.CreatedItem(spellId) != 0) return false;
        bool hasKnownRecipe = _skillLines.Recipes(line).Any(r => _actions.KnownSpells.Contains(r.SpellId) &&
            (_spellCatalog.CreatedItem(r.SpellId) != 0 || _spellCatalog.Reagents(r.SpellId).Count > 0));
        if (!hasKnownRecipe) return false;
        return OpenProfession(line, spellId);
    }

    private bool OpenFirstProfession()
    {
        if (_skillLines is null || _spellCatalog is null) return false;
        uint line = _actions.KnownSpells.Select(_skillLines.SpellLine).Where(IsCraftProfessionLine)
            .FirstOrDefault(x => _skillLines.Recipes(x).Any(r => _actions.KnownSpells.Contains(r.SpellId) &&
                (_spellCatalog.CreatedItem(r.SpellId) != 0 || _spellCatalog.Reagents(r.SpellId).Count > 0)));
        return line != 0 && OpenProfession(line, 0);
    }

    private bool OpenProfessionNamed(string name)
    {
        if (_skillLines is null || _spellCatalog is null) return false;
        uint line = _actions.KnownSpells.Select(_skillLines.SpellLine).Where(IsCraftProfessionLine).Distinct()
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
        if (_skillLines is null || _spellCatalog is null || !IsCraftProfessionLine(line)) return false;
        _professionRecipes.Clear();
        foreach (SkillRecipeInfo recipe in _skillLines.Recipes(line))
        {
            if (!_actions.KnownSpells.Contains(recipe.SpellId) ||
                !_spellCatalog.TryGet(recipe.SpellId, out SpellInfo spell)) continue;
            uint product = _spellCatalog.CreatedItem(recipe.SpellId);
            IReadOnlyList<SpellReagent> reagents = _spellCatalog.Reagents(recipe.SpellId);
            if (product == 0 && reagents.Count == 0) continue;
            IReadOnlyList<uint> tools = _spellCatalog.Tools(recipe.SpellId);
            _professionRecipes.Add(new(recipe.SpellId, spell.Name, product, reagents, tools,
                spell.RequiredFocus, recipe));
            if (product != 0) _items?.Require(product, 0, _net!);
            foreach (SpellReagent reagent in reagents) _items?.Require(reagent.ItemId, 0, _net!);
            foreach (uint tool in tools) _items?.Require(tool, 0, _net!);
        }
        _professionRecipes.Sort((a, b) =>
        {
            int minimum = a.Skill.Minimum.CompareTo(b.Skill.Minimum);
            if (minimum != 0) return minimum;
            int reagents = a.Reagents.Sum(x => (int)x.Count).CompareTo(b.Reagents.Sum(x => (int)x.Count));
            return reagents != 0 ? reagents : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
        _professionLine = line; _professionSelected = 0; _professionScroll = 0;
        _professionOpen = _professionRecipes.Count > 0;
        _professionKnownSnapshot.Clear();
        foreach (uint known in _actions.KnownSpells) _professionKnownSnapshot.Add(known);
        GetSkillValue(line, out ushort value, out ushort max);
        EmitInterface("profession", "open", _professionOpen ? "OPEN" : "EMPTY", _net?.PlayerGuid ?? 0,
            $"line={line};opener={opener};recipes={_professionRecipes.Count};skill={value}/{max}");
        EmitProfessionRecipeSnapshot();
        return _professionOpen;
    }

    // SkillLine.dbc primary professions plus the three player craft books that
    // build 5875 exposes through TradeSkill/Craft UI.  This deliberately excludes
    // class lines such as Arcane (237): conjured food is a spell, not a profession.
    private static bool IsCraftProfessionLine(uint line) => line is
        40 or 129 or 164 or 165 or 171 or 185 or 186 or 197 or 202 or 333;

    private void EmitProfessionRecipeSnapshot()
    {
        GetSkillValue(_professionLine, out ushort value, out _);
        int craftable = 0;
        foreach (ProfessionRecipe recipe in _professionRecipes)
        {
            string reagents = string.Join('|', recipe.Reagents.Select(r => $"{r.ItemId}:{BackpackCount(r.ItemId)}/{r.Count}"));
            bool ready = recipe.Reagents.All(r => BackpackCount(r.ItemId) >= r.Count) &&
                recipe.Tools.All(t => CarriedCount(t) > 0) && HasNearbySpellFocus(recipe.RequiredFocus);
            if (ready) craftable++;
            EmitInterface("profession", "recipe", "DECODED", recipe.SpellId,
                $"line={_professionLine};name={SanitizeEvidence(recipe.Name)};product={recipe.Product};reagents={reagents};" +
                $"tools={string.Join('|', recipe.Tools.Select(t => $"{t}:{CarriedCount(t)}"))};focus={recipe.RequiredFocus};" +
                $"focusName={SanitizeEvidence(recipe.RequiredFocus == 0 ? "none" : SpellFocusName(recipe.RequiredFocus))};focusAvailable={HasNearbySpellFocus(recipe.RequiredFocus)};" +
                $"color={ProfessionSkillColor(value, recipe.Skill.TrivialLow, recipe.Skill.TrivialHigh)};minimum={recipe.Skill.Minimum}");
        }
        EmitInterface("profession", "recipe-list", "COMPLETE", _net?.PlayerGuid ?? 0,
            $"line={_professionLine};recipes={_professionRecipes.Count};craftable={craftable};skill={value}");
    }

    private bool CraftProfessionRecipe(int index)
    {
        if (!_professionOpen || index < 0 || index >= _professionRecipes.Count) return false;
        ProfessionRecipe recipe = _professionRecipes[index];
        bool ready = recipe.Reagents.All(r => BackpackCount(r.ItemId) >= r.Count) &&
            recipe.Tools.All(t => CarriedCount(t) > 0) && HasNearbySpellFocus(recipe.RequiredFocus);
        if (!ready)
        {
            EmitInterface("profession", "craft-send", "REFUSED", recipe.SpellId, "reason=missing-reagents");
            return false;
        }
        GetSkillValue(_professionLine, out _professionSkillBefore, out _);
        TryCast(recipe.SpellId);
        if (_pendingCastSpell != recipe.SpellId)
        {
            EmitInterface("profession", "craft-send", "REFUSED", recipe.SpellId,
                "reason=cast-not-accepted");
            return false;
        }
        _professionProductPending = recipe.Product;
        _professionProductPendingSpell = recipe.SpellId;
        _professionProductSpellGoObserved = false;
        _professionProductBefore = CarriedCount(recipe.Product);
        _professionProductPendingAt = NowSeconds();
        _professionCraftSpell = recipe.SpellId;
        EmitInterface("profession", "craft-send", "SENT", recipe.SpellId,
            $"line={_professionLine};product={recipe.Product};reagents={string.Join('|', recipe.Reagents.Select(r => $"{r.ItemId}:{r.Count}"))};body={Convert.ToHexString(WorldSession.BuildCastSpellBody(recipe.SpellId, 0))}");
        return true;
    }

    private bool CraftAllProfessionRecipe(int index)
    {
        if (index < 0 || index >= _professionRecipes.Count) return false;
        ProfessionRecipe recipe = _professionRecipes[index];
        int possible = recipe.Reagents.Count == 0 ? 1 : recipe.Reagents
            .Min(r => r.Count == 0 ? 0 : (int)(BackpackCount(r.ItemId) / r.Count));
        _professionBatchRemaining = Math.Clamp(possible, 0, 100);
        return _professionBatchRemaining > 0 && CraftProfessionRecipe(index);
    }

    private void ContinueProfessionBatch()
    {
        if (_professionBatchRemaining <= 0) return;
        _professionBatchRemaining--;
        if (_professionBatchRemaining > 0 && !CraftProfessionRecipe(_professionSelected))
            _professionBatchRemaining = 0;
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
        foreach (uint tool in recipe.Tools)
            if (CarriedCount(tool) == 0) sent &= SendGmCommand($".additem {tool} 1", "profession-tool");
        EmitInterface("profession", "provision", sent ? "SENT" : "SEND_FAILED", recipe.SpellId,
            $"reagents={string.Join('|', recipe.Reagents.Select(r => $"{r.ItemId}:{r.Count}"))}");
        return sent;
    }

    private bool ProvisionProfessionRecipe(int index)
    {
        if (index < 0 || index >= _professionRecipes.Count) return false;
        ProfessionRecipe recipe = _professionRecipes[index]; bool sent = true;
        foreach (SpellReagent reagent in recipe.Reagents)
        {
            uint have = CarriedCount(reagent.ItemId);
            uint missing = reagent.Count > have ? reagent.Count - have : 0;
            if (missing > 0) sent &= SendGmCommand($".additem {reagent.ItemId} {missing}", "profession-provision");
        }
        foreach (uint tool in recipe.Tools)
            if (CarriedCount(tool) == 0) sent &= SendGmCommand($".additem {tool} 1", "profession-tool");
        EmitInterface("profession", "provision", sent ? "SENT" : "SEND_FAILED", recipe.SpellId,
            $"index={index};product={recipe.Product};reagents={string.Join('|', recipe.Reagents.Select(r => $"{r.ItemId}:{r.Count}"))}");
        return sent;
    }

    private bool ProvisionProfessionSpell(uint spellId) =>
        ProvisionProfessionRecipe(_professionRecipes.FindIndex(x => x.SpellId == spellId));

    private bool CraftProfessionSpell(uint spellId) =>
        CraftProfessionRecipe(_professionRecipes.FindIndex(x => x.SpellId == spellId));

    private void ObserveProfessionSpellGo(uint spellId)
    {
        if (spellId != _professionCraftSpell) return;
        if (_professionProductPendingSpell == spellId)
        {
            _professionProductPendingAt = NowSeconds();
            _professionProductSpellGoObserved = true;
        }
        GetSkillValue(_professionLine, out ushort after, out _);
        EmitInterface("profession", "craft", "SUCCESS", spellId,
            $"line={_professionLine};skillBefore={_professionSkillBefore};skillAfter={after};delta={(int)after - _professionSkillBefore}");
        _professionSkillPendingSpell = spellId; _professionSkillPendingAt = NowSeconds();
        _professionCraftSpell = 0;
    }

    private void ObserveProfessionSpellFailure(uint spellId, string reason)
    {
        if (spellId != _professionCraftSpell) return;
        EmitInterface("profession", "product-created", "FAILED", spellId,
            $"line={_professionLine};product={_professionProductPending};reason={SanitizeEvidence(reason)}");
        _professionCraftSpell = 0;
        _professionProductPending = 0;
        _professionProductPendingSpell = 0;
        _professionProductSpellGoObserved = false;
        _professionBatchRemaining = 0;
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

    private void ObserveProfessionProductTransition()
    {
        if (_professionProductPending == 0) return;
        uint after = CarriedCount(_professionProductPending);
        if (after > _professionProductBefore)
        {
            EmitInterface("profession", "product-created", "INCREASED", _professionProductPendingSpell,
                $"line={_professionLine};product={_professionProductPending};before={_professionProductBefore};after={after};delta={after - _professionProductBefore}");
            _professionLastCreatedProduct = _professionProductPending;
            _professionLastCreatedDelta = after - _professionProductBefore;
            _professionProductPending = 0;
            _professionProductPendingSpell = 0;
            _professionProductSpellGoObserved = false;
            ContinueProfessionBatch();
        }
        else if (_professionCraftSpell != _professionProductPendingSpell &&
                 NowSeconds() - _professionProductPendingAt > 8)
        {
            EmitInterface("profession", "product-created", "UNCHANGED", _professionProductPendingSpell,
                $"line={_professionLine};product={_professionProductPending};before={_professionProductBefore};after={after}");
            _professionProductPending = 0;
            _professionProductPendingSpell = 0;
            _professionProductSpellGoObserved = false;
        }
    }

    /// <summary>
    /// VMaNGOS may replace the base item declared by Spell.dbc with a generated item
    /// variant.  In that case the base carried count never changes, but the server still
    /// sends SMSG_ITEM_PUSH_RESULT with its created flag set.  Once the matching cast has
    /// reached SMSG_SPELL_GO, that packet is the authoritative proof of its actual output.
    /// GM provisioning is excluded because its item pushes are not marked created.
    /// </summary>
    private void ObserveProfessionCreatedItemPush(uint actualProduct, uint count)
    {
        if (_professionProductPending == 0 || _professionProductPendingSpell == 0 ||
            !_professionProductSpellGoObserved || actualProduct == 0 || count == 0)
            return;

        uint recipeProduct = _professionProductPending;
        EmitInterface("profession", "product-created", "INCREASED", _professionProductPendingSpell,
            $"line={_professionLine};recipeProduct={recipeProduct};actualProduct={actualProduct};" +
            $"delta={count};source=SMSG_ITEM_PUSH_RESULT;created=1;variant={actualProduct != recipeProduct}");
        _professionLastCreatedProduct = actualProduct;
        _professionLastCreatedDelta = count;
        _professionProductPending = 0;
        _professionProductPendingSpell = 0;
        _professionProductSpellGoObserved = false;
        ContinueProfessionBatch();
    }

    private bool CleanupLastProfessionProduct()
    {
        if (_professionLastCreatedProduct == 0 || _professionLastCreatedDelta == 0) return false;
        bool sent = SendGmCommand($".additem {_professionLastCreatedProduct} -{_professionLastCreatedDelta}",
            "profession-cleanup");
        EmitInterface("profession", "cleanup", sent ? "SENT" : "SEND_FAILED", _professionLastCreatedProduct,
            $"count={_professionLastCreatedDelta}");
        _professionLastCreatedProduct = 0; _professionLastCreatedDelta = 0;
        return sent;
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
        => CarriedCount(entry);

    private uint CarriedCount(uint entry)
    {
        if (_net is null || !_entities.TryGet(_net.PlayerGuid, out WorldEntity player)) return 0;
        uint count = 0;
        // Tools may be equipped (weapon, off-hand, profession item) rather than bagged.
        for (int slot = 0; slot < 19; slot++)
        {
            ulong guid = player.Fields.PlayerInventorySlot(slot);
            if (guid != 0 && _entities.TryGet(guid, out WorldEntity item) && item.Entry == entry)
                count += Math.Max(1, item.Fields.ItemStackCount);
        }
        for (int slot = 0; slot < 16; slot++)
        {
            ulong guid = player.Fields.PlayerBackpackSlot(slot);
            if (guid != 0 && _entities.TryGet(guid, out WorldEntity item) && item.Entry == entry)
                count += Math.Max(1, item.Fields.ItemStackCount);
        }
        for (int bagIndex = 0; bagIndex < 4; bagIndex++)
        {
            ulong bagGuid = player.Fields.PlayerInventorySlot(19 + bagIndex);
            if (bagGuid == 0 || !_entities.TryGet(bagGuid, out WorldEntity bag)) continue;
            int slots = Math.Clamp((int)bag.Fields.ContainerNumSlots, 0, 36);
            for (int slot = 0; slot < slots; slot++)
            {
                ulong guid = bag.Fields.ContainerSlot(slot);
                if (guid != 0 && _entities.TryGet(guid, out WorldEntity item) && item.Entry == entry)
                    count += Math.Max(1, item.Fields.ItemStackCount);
            }
        }
        return count;
    }

    private void DrawProfessionFrame()
    {
        if (!_professionOpen || _gameplayArt is null) return;
        if (!BeginVanillaWindow("##profession", new Vector2(0, 104), new Vector2(384, 512),
                out ImDrawListPtr dl, out Vector2 origin, out float s)) { ImGui.End(); return; }
        DrawFourPieceShell(dl, origin, s,
            @"Interface\ClassTrainerFrame\UI-ClassTrainer-TopLeft",
            @"Interface\ClassTrainerFrame\UI-ClassTrainer-TopRight",
            @"Interface\TradeSkillFrame\UI-TradeSkill-BotLeft",
            @"Interface\ClassTrainerFrame\UI-ClassTrainer-BotRight");
        string lineName = _skillLines?.TryGet(_professionLine, out SkillLineInfo line) == true ? line.Name : $"Skill {_professionLine}";
        GetSkillValue(_professionLine, out ushort value, out ushort max);
        DrawCenteredText(dl, origin + new Vector2(190, 18) * s, lineName, 14f * s, VanillaGold);
        dl.AddText(ImGui.GetFont(), 10f * s, origin + new Vector2(25, 72) * s, 0xffffffff,
            $"{value} / {max}");

        _professionScroll = Math.Clamp(_professionScroll, 0, Math.Max(0, _professionRecipes.Count - 8));
        Vector2 listMin = origin + new Vector2(22, 96) * s;
        ImGui.SetCursorScreenPos(listMin);
        ImGui.InvisibleButton("##profession-list", new Vector2(296, 128) * s);
        if (ImGui.IsItemHovered() && ImGui.GetIO().MouseWheel != 0)
            _professionScroll = Math.Clamp(_professionScroll - Math.Sign(ImGui.GetIO().MouseWheel),
                0, Math.Max(0, _professionRecipes.Count - 8));
        for (int visible = 0; visible < 8 && visible + _professionScroll < _professionRecipes.Count; visible++)
        {
            int i = visible + _professionScroll;
            ProfessionRecipe recipe = _professionRecipes[i];
            string difficulty = ProfessionSkillColor(value, recipe.Skill.TrivialLow, recipe.Skill.TrivialHigh);
            Vector4 c = ProfessionColor(difficulty);
            uint color = ImGui.ColorConvertFloat4ToU32(c);
            if (VanillaListRow(dl, $"##recipe-{recipe.SpellId}",
                    origin + new Vector2(22, 96 + visible * 16) * s,
                    new Vector2(296, 16), s, recipe.Name, _professionSelected == i, color))
                _professionSelected = i;
        }
        DrawArt(dl, @"Interface\ClassTrainerFrame\UI-ClassTrainer-HorizontalBar",
            origin + new Vector2(15, 221) * s, new Vector2(331, 16), s);
        if (_professionRecipes.Count > 0)
        {
            ProfessionRecipe recipe = _professionRecipes[Math.Clamp(_professionSelected, 0, _professionRecipes.Count - 1)];
            string productIcon = "";
            if (recipe.Product != 0 && _items?.TryGet(recipe.Product, out ItemTemplate? product) == true && product is not null)
                productIcon = product.IconPath;
            if (productIcon.Length > 0)
            {
                uint icon = _gameplayArt.Handle(productIcon);
                if (icon != 0) dl.AddImage((nint)icon, origin + new Vector2(24, 240) * s,
                    origin + new Vector2(56, 272) * s);
            }
            dl.AddText(ImGui.GetFont(), 12f * s, origin + new Vector2(62, 242) * s, VanillaGold, recipe.Name);
            dl.AddText(ImGui.GetFont(), 10f * s, origin + new Vector2(24, 282) * s, VanillaGold, "Requires");
            for (int i = 0; i < recipe.Reagents.Count && i < 8; i++)
            {
                SpellReagent reagent = recipe.Reagents[i];
                ItemTemplate? item = null;
                if (_items?.TryGet(reagent.ItemId, out ItemTemplate? found) == true) item = found;
                string name = item?.Name ?? $"Item {reagent.ItemId}";
                uint have = BackpackCount(reagent.ItemId);
                Vector2 row = origin + new Vector2(32 + (i % 2) * 145, 305 + (i / 2) * 24) * s;
                if (item is not null)
                {
                    uint icon = _gameplayArt.Handle(item.IconPath);
                    if (icon != 0) dl.AddImage((nint)icon, row, row + new Vector2(20) * s);
                }
                dl.AddText(ImGui.GetFont(), 9f * s, row + new Vector2(24, 3) * s,
                    have >= reagent.Count ? 0xffffffff : 0xff4040ff, $"{name} {have}/{reagent.Count}");
            }
            int requirementLine = 305 + ((Math.Min(recipe.Reagents.Count, 8) + 1) / 2) * 24;
            foreach (uint tool in recipe.Tools)
            {
                bool have = CarriedCount(tool) > 0;
                string name = _items?.TryGet(tool, out ItemTemplate? item) == true && item is not null
                    ? item.Name : $"Item {tool}";
                dl.AddText(ImGui.GetFont(), 9f * s, origin + new Vector2(32, requirementLine) * s,
                    have ? 0xffffffff : 0xff4040ff, $"Tool: {name}");
                requirementLine += 14;
            }
            if (recipe.RequiredFocus != 0)
            {
                bool haveFocus = HasNearbySpellFocus(recipe.RequiredFocus);
                dl.AddText(ImGui.GetFont(), 9f * s, origin + new Vector2(32, requirementLine) * s,
                    haveFocus ? 0xffffffff : 0xff4040ff, $"Requires: {SpellFocusName(recipe.RequiredFocus)}");
            }
            bool ready = recipe.Reagents.All(r => BackpackCount(r.ItemId) >= r.Count) &&
                recipe.Tools.All(t => CarriedCount(t) > 0) && HasNearbySpellFocus(recipe.RequiredFocus);
            if (VanillaButton(dl, "##profession-create-all", "Create All",
                    origin + new Vector2(18, 411) * s, new Vector2(80, 22), s, ready))
                CraftAllProfessionRecipe(_professionSelected);
            if (VanillaButton(dl, "##profession-create", "Create",
                    origin + new Vector2(184, 411) * s, new Vector2(80, 22), s, ready))
                CraftProfessionRecipe(_professionSelected);
        }
        if (VanillaButton(dl, "##profession-exit", _professionBatchRemaining > 0 ? "Cancel" : "Exit",
                origin + new Vector2(265, 411) * s, new Vector2(80, 22), s))
        {
            if (_professionBatchRemaining > 0) { _professionBatchRemaining = 0; _net?.CancelCast(_professionCraftSpell); }
            else _professionOpen = false;
        }
        DrawImageButton(dl, "##profession-close", origin + new Vector2(323, 8) * s, new Vector2(32) * s,
            @"Interface\Buttons\UI-Panel-MinimizeButton-Up",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Down",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");
        if (ImGui.IsItemClicked()) _professionOpen = false;
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
