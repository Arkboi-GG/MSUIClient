using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private sealed record ProfessionRecipe(uint SpellId, string Name, string Rank,
        string Description, uint Product,
        IReadOnlyList<SpellReagent> Reagents, IReadOnlyList<uint> Tools, uint RequiredFocus,
        SkillRecipeInfo Skill);
    private readonly record struct ProfessionDisplayRow(bool Header, ulong GroupKey,
        string Text, int RecipeIndex, bool Expanded);

    private bool _professionOpen;
    private ProfessionPanelKind? _professionPanelKind;
    private uint _professionOpenerSpell;
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
    private int _professionCreateCount = 1;
    private readonly HashSet<ulong> _professionCollapsedGroups = [];
    private ulong? _professionSubclassFilter;
    private int? _professionInventorySlotFilter;
    private int _professionFilterMenu;

    private void InitProfessions() { }

    private bool TryOpenProfession(uint spellId)
    {
        if (_spellCatalog is null || !_spellCatalog.TryGet(spellId, out SpellInfo opener))
            return false;
        ProfessionPanelOpenerProvenance provenance = ProfessionPanelOpenerLaw.Resolve(
            opener.EffectIds, opener.EffectMiscValues);
        if (!provenance.IsProfessionOpener) return false;

        // Effect-47 is intercepted before every ordinary cast gate and never reaches the wire.
        // Missing SLA/recipe data can prevent a host window from opening, but cannot turn the
        // client-local opener back into a CMSG_CAST_SPELL.
        if (_skillLines is null) return true;
        uint line = _skillLines.SpellLine(spellId);
        if (!IsCraftProfessionLine(line) || _spellCatalog.CreatedItem(spellId) != 0) return true;
        bool hasKnownRecipe = _skillLines.Recipes(line).Any(r => _actions.KnownSpells.Contains(r.SpellId) &&
            (_spellCatalog.CreatedItem(r.SpellId) != 0 || _spellCatalog.Reagents(r.SpellId).Count > 0));
        if (!hasKnownRecipe) return true;
        _ = OpenProfession(line, spellId, provenance.PanelKind);
        return true;
    }

    private bool OpenFirstProfession()
    {
        if (_skillLines is null || _spellCatalog is null) return false;
        uint line = _actions.KnownSpells.Select(_skillLines.SpellLine).Where(IsCraftProfessionLine)
            .FirstOrDefault(x => _skillLines.Recipes(x).Any(r => _actions.KnownSpells.Contains(r.SpellId) &&
                (_spellCatalog.CreatedItem(r.SpellId) != 0 || _spellCatalog.Reagents(r.SpellId).Count > 0)));
        return line != 0 && OpenProfession(line, 0, panelKind: null);
    }

    private bool OpenProfessionNamed(string name)
    {
        if (_skillLines is null || _spellCatalog is null) return false;
        uint line = _actions.KnownSpells.Select(_skillLines.SpellLine).Where(IsCraftProfessionLine).Distinct()
            .FirstOrDefault(x => _skillLines.TryGet(x, out SkillLineInfo info) &&
                info.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                _skillLines.Recipes(x).Any(r => _actions.KnownSpells.Contains(r.SpellId) &&
                    (_spellCatalog.CreatedItem(r.SpellId) != 0 || _spellCatalog.Reagents(r.SpellId).Count > 0)));
        return line != 0 && OpenProfession(line, 0, panelKind: null);
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

    private bool OpenProfession(uint line, uint opener, ProfessionPanelKind? panelKind)
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
            _professionRecipes.Add(new(recipe.SpellId, spell.Name, spell.Rank,
                spell.Description, product, reagents, tools, spell.RequiredFocus, recipe));
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
        _professionCreateCount = 1;
        _professionOpen = _professionRecipes.Count > 0;
        _professionPanelKind = _professionOpen ? panelKind : null;
        _professionOpenerSpell = _professionOpen ? opener : 0;
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
        int panelIndex = _professionPanelKind == ProfessionPanelKind.Craft ? 17 : 16;
        if (!BeginVanillaWindow("##profession", UiPanelFrameLogicalOrigin(UiPanelOwnershipRegistry[panelIndex]),
                ProfessionFrameUiLaw.FrameSize(1f),
                out ImDrawListPtr dl, out Vector2 origin, out float s)) { ImGui.End(); return; }
        if (_net is not null && _entities.TryGet(_net.PlayerGuid, out WorldEntity player))
            DrawUnitPortraitImage(dl, player,
                origin + ProfessionFrameUiLaw.PortraitOffset * s,
                ProfessionFrameUiLaw.PortraitSize * s, 0, false);
        DrawFourPieceShell(dl, origin, s,
            @"Interface\ClassTrainerFrame\UI-ClassTrainer-TopLeft",
            @"Interface\ClassTrainerFrame\UI-ClassTrainer-TopRight",
            @"Interface\TradeSkillFrame\UI-TradeSkill-BotLeft",
            @"Interface\ClassTrainerFrame\UI-ClassTrainer-BotRight");
        string lineName = _skillLines?.TryGet(_professionLine, out SkillLineInfo line) == true ? line.Name : $"Skill {_professionLine}";
        GetSkillValue(_professionLine, out ushort value, out ushort max);
        DrawCenteredText(dl, origin + ProfessionFrameUiLaw.TitleCenter * s,
            lineName, 14f * s, VanillaGold);
        DrawProfessionRankBar(dl, origin, s, lineName, value, max);

        bool tradeSkill = _professionPanelKind != ProfessionPanelKind.Craft;
        IReadOnlyList<ProfessionDisplayRow> displayRows = ProfessionDisplayRows(tradeSkill, value);
        int maximumScroll = ProfessionFrameUiLaw.MaximumScroll(displayRows.Count);
        _professionScroll = ProfessionFrameUiLaw.ClampScroll(_professionScroll, displayRows.Count);
        Vector2 listMin = origin + ProfessionFrameUiLaw.List.Min * s;
        ImGui.SetCursorScreenPos(listMin);
        ImGui.InvisibleButton("##profession-list", ProfessionFrameUiLaw.List.Size * s);
        if (ImGui.IsItemHovered() && ImGui.GetIO().MouseWheel != 0)
            _professionScroll = Math.Clamp(_professionScroll - Math.Sign(ImGui.GetIO().MouseWheel), 0, maximumScroll);
        for (int visible = 0; visible < ProfessionFrameUiLaw.VisibleRows &&
             visible + _professionScroll < displayRows.Count; visible++)
        {
            ProfessionDisplayRow displayRow = displayRows[visible + _professionScroll];
            ProfessionFrameUiLaw.LogicalRect logicalRow = ProfessionFrameUiLaw.Row(visible);
            Vector2 rowMin = origin + logicalRow.Min * s;
            if (displayRow.Header)
            {
                ImGui.SetCursorScreenPos(rowMin);
                if (ImGui.InvisibleButton($"##profession-header-{displayRow.GroupKey}",
                        logicalRow.Size * s))
                {
                    if (!_professionCollapsedGroups.Add(displayRow.GroupKey))
                        _professionCollapsedGroups.Remove(displayRow.GroupKey);
                    _professionScroll = Math.Min(_professionScroll,
                        ProfessionFrameUiLaw.MaximumScroll(displayRows.Count));
                }
                GameText.Draw(dl, "GameFontNormalSmall", displayRow.Expanded ? "−" : "+",
                    rowMin + new Vector2(1, 1) * s, s, VanillaGold);
                GameText.Draw(dl, "GameFontNormalSmall", displayRow.Text,
                    rowMin + new Vector2(18, 1) * s, s, VanillaGold);
                continue;
            }
            int i = displayRow.RecipeIndex;
            ProfessionRecipe recipe = _professionRecipes[i];
            string difficulty = ProfessionSkillColor(value, recipe.Skill.TrivialLow, recipe.Skill.TrivialHigh);
            Vector4 c = ProfessionColor(difficulty);
            uint color = ImGui.ColorConvertFloat4ToU32(c);
            int craftable = ProfessionCraftableCount(recipe);
            if (VanillaListRow(dl, $"##recipe-{recipe.SpellId}",
                    rowMin, logicalRow.Size, s,
                    ProfessionFrameUiLaw.RowLabel(recipe.Name, craftable),
                    _professionSelected == i, color))
            {
                _professionSelected = i;
                _professionCreateCount = 1;
            }
            if (!tradeSkill && recipe.Rank.Length > 0)
                GameText.Draw(dl, "GameFontNormalSmall",
                    ProfessionFrameUiLaw.CraftSubText(recipe.Rank),
                    rowMin + new Vector2(190, 1) * s, s, color);
            if (tradeSkill && ImGui.IsItemHovered() && recipe.Product != 0 &&
                _items?.TryGet(recipe.Product, out ItemTemplate? rowProduct) == true && rowProduct is not null)
                OfferPreparedItemTooltip(new("item:profession-row",
                        ((ulong)recipe.SpellId << 32) | recipe.Product),
                    PrepareItemTooltipBodySnapshot(rowProduct, 1));
        }
        if (tradeSkill)
            DrawProfessionTradeSkillControls(dl, origin, s, displayRows);
        if (maximumScroll > 0)
            DrawProfessionScrollBar(dl, origin, s, maximumScroll);
        DrawArt(dl, @"Interface\ClassTrainerFrame\UI-ClassTrainer-HorizontalBar",
            origin + ProfessionFrameUiLaw.HorizontalBar * s, new Vector2(331, 16), s);
        if (_professionRecipes.Count > 0)
        {
            ProfessionRecipe recipe = _professionRecipes[Math.Clamp(_professionSelected, 0, _professionRecipes.Count - 1)];
            int craftable = ProfessionCraftableCount(recipe);
            _professionCreateCount = ProfessionFrameUiLaw.ClampCreateCount(_professionCreateCount, craftable);
            string productIcon = "";
            ItemTemplate? product = null;
            if (recipe.Product != 0 && _items?.TryGet(recipe.Product, out product) == true && product is not null)
                productIcon = product.IconPath;
            if (productIcon.Length > 0)
            {
                uint icon = _gameplayArt.Handle(productIcon);
                Vector2 productMin = origin + ProfessionFrameUiLaw.ProductTexture.Min * s;
                if (icon != 0) dl.AddImage((nint)icon, productMin,
                    productMin + ProfessionFrameUiLaw.ProductTexture.Size * s);
            }
            Vector2 productHit = origin + ProfessionFrameUiLaw.DetailProduct.Min * s;
            ImGui.SetCursorScreenPos(productHit);
            ImGui.InvisibleButton("##profession-product", ProfessionFrameUiLaw.DetailProduct.Size * s);
            if (ImGui.IsItemHovered() && product is not null)
                OfferPreparedItemTooltip(new("item:profession-product",
                        ((ulong)recipe.SpellId << 32) | product.Entry),
                    PrepareItemTooltipBodySnapshot(product, 1));
            GameText.Draw(dl, "GameFontNormal", recipe.Name,
                origin + ProfessionFrameUiLaw.ProductName * s, s, VanillaGold);

            float descriptionHeight = 0;
            if (!tradeSkill && !string.IsNullOrWhiteSpace(recipe.Description))
                descriptionHeight = DrawWrappedText(dl, recipe.Description,
                    origin + ProfessionFrameUiLaw.Description * s, 290, 9f * s,
                    s, 0xffffffff, maxLines: 2) / s;
            GameText.Draw(dl, "GameFontNormalSmall", "Reagents:",
                origin + (ProfessionFrameUiLaw.ReagentLabel + new Vector2(0, descriptionHeight)) * s,
                s, VanillaGold);
            for (int i = 0; i < recipe.Reagents.Count && i < 8; i++)
            {
                SpellReagent reagent = recipe.Reagents[i];
                ItemTemplate? item = null;
                if (_items?.TryGet(reagent.ItemId, out ItemTemplate? found) == true) item = found;
                string name = item?.Name ?? $"Item {reagent.ItemId}";
                uint have = BackpackCount(reagent.ItemId);
                ProfessionFrameUiLaw.LogicalRect reagentRect = ProfessionFrameUiLaw.Reagent(i, descriptionHeight);
                Vector2 row = origin + reagentRect.Min * s;
                if (item is not null)
                {
                    uint icon = _gameplayArt.Handle(item.IconPath);
                    if (icon != 0) dl.AddImage((nint)icon, row, row + new Vector2(28) * s);
                }
                uint reagentColor = have >= reagent.Count ? 0xffffffff : 0xff4040ff;
                GameText.Draw(dl, "GameFontHighlightSmall", name,
                    row + new Vector2(34, 1) * s, s, reagentColor);
                GameText.Draw(dl, "GameFontHighlightSmall", $"{have} /{reagent.Count}",
                    row + new Vector2(34, 15) * s, s, reagentColor);
                ImGui.SetCursorScreenPos(row);
                ImGui.InvisibleButton($"##profession-reagent-{i}", reagentRect.Size * s);
                if (ImGui.IsItemHovered() && item is not null)
                    OfferPreparedItemTooltip(new("item:profession-reagent",
                            ((ulong)recipe.SpellId << 32) | reagent.ItemId),
                        PrepareItemTooltipBodySnapshot(item, reagent.Count));
            }
            int requirementLine = (int)(ProfessionFrameUiLaw.ReagentGrid.Y + descriptionHeight +
                ((Math.Min(recipe.Reagents.Count, 8) + 1) / 2) * 32);
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
            if (tradeSkill)
            {
                if (VanillaButton(dl, "##profession-create-all", "Create All",
                        origin + ProfessionFrameUiLaw.CreateAll.Min * s,
                        ProfessionFrameUiLaw.CreateAll.Size, s, ready))
                    CraftAllProfessionRecipe(_professionSelected);
                DrawProfessionCountSpinner(dl, origin, s, craftable);
            }
            if (VanillaButton(dl, "##profession-create", "Create",
                    origin + ProfessionFrameUiLaw.Create.Min * s,
                    ProfessionFrameUiLaw.Create.Size, s, ready))
            {
                if (tradeSkill) CraftProfessionRecipeCount(_professionSelected, _professionCreateCount);
                else CraftProfessionRecipe(_professionSelected);
            }
        }
        if (VanillaButton(dl, "##profession-exit", _professionBatchRemaining > 0 ? "Cancel" : "Exit",
                origin + ProfessionFrameUiLaw.Exit.Min * s,
                ProfessionFrameUiLaw.Exit.Size, s))
        {
            if (_professionBatchRemaining > 0) { _professionBatchRemaining = 0; _net?.CancelCast(_professionCraftSpell); }
            else _professionOpen = false;
        }
        DrawImageButton(dl, "##profession-close", origin + ProfessionFrameUiLaw.Close * s, new Vector2(32) * s,
            @"Interface\Buttons\UI-Panel-MinimizeButton-Up",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Down",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");
        if (ImGui.IsItemClicked()) _professionOpen = false;
        ImGui.End();
    }

    private IReadOnlyList<ProfessionFrameUiLaw.TradeSkillNode> ProfessionTradeSkillNodes(ushort rank)
    {
        var nodes = new List<ProfessionFrameUiLaw.TradeSkillNode>(_professionRecipes.Count);
        for (int i = 0; i < _professionRecipes.Count; i++)
        {
            ProfessionRecipe recipe = _professionRecipes[i];
            uint itemClass = uint.MaxValue, subclass = uint.MaxValue, inventoryType = 0, itemLevel = 0;
            string groupName = "";
            if (recipe.Product != 0 && _items?.TryGet(recipe.Product, out ItemTemplate? product) == true &&
                product is not null)
            {
                itemClass = product.Class;
                subclass = product.Subclass;
                inventoryType = product.InventoryType;
                itemLevel = product.ItemLevel;
                groupName = _itemSubClasses?.Name(itemClass, subclass) ?? "";
            }
            string difficulty = ProfessionSkillColor(rank, recipe.Skill.TrivialLow, recipe.Skill.TrivialHigh);
            int tier = difficulty switch { "orange" => 0, "yellow" => 1, "green" => 2, _ => 3 };
            nodes.Add(new(i, itemClass, subclass, groupName, inventoryType, itemLevel, tier, recipe.Name));
        }
        return nodes;
    }

    private IReadOnlyList<ProfessionDisplayRow> ProfessionDisplayRows(bool tradeSkill, ushort rank)
    {
        if (!tradeSkill)
            return _professionRecipes.Select((recipe, index) =>
                new ProfessionDisplayRow(false, 0, recipe.Name, index, false)).ToArray();
        return ProfessionFrameUiLaw.BuildTradeSkillTree(ProfessionTradeSkillNodes(rank),
                _professionCollapsedGroups, _professionSubclassFilter,
                _professionInventorySlotFilter)
            .Select(row => new ProfessionDisplayRow(row.Header, row.GroupKey, row.Text,
                row.RecipeIndex, row.Expanded)).ToArray();
    }

    private void DrawProfessionTradeSkillControls(ImDrawListPtr dl, Vector2 origin, float scale,
        IReadOnlyList<ProfessionDisplayRow> rows)
    {
        ulong[] groupKeys = rows.Where(row => row.Header).Select(row => row.GroupKey).ToArray();
        if (groupKeys.Length > 0)
        {
            bool anyCollapsed = groupKeys.Any(_professionCollapsedGroups.Contains);
            if (VanillaButton(dl, "##profession-collapse-all", anyCollapsed ? "+ All" : "− All",
                    origin + ProfessionFrameUiLaw.CollapseAll.Min * scale,
                    ProfessionFrameUiLaw.CollapseAll.Size, scale))
            {
                if (anyCollapsed)
                    foreach (ulong key in groupKeys) _professionCollapsedGroups.Remove(key);
                else
                    foreach (ulong key in groupKeys) _professionCollapsedGroups.Add(key);
                _professionScroll = 0;
            }
        }

        IReadOnlyList<ProfessionFrameUiLaw.TradeSkillNode> nodes =
            ProfessionTradeSkillNodes(GetProfessionRank());
        string subclassCaption = "All Subclasses";
        if (_professionSubclassFilter is ulong subclassKey)
            subclassCaption = nodes.FirstOrDefault(node =>
                ProfessionFrameUiLaw.GroupKey(node.ItemClass, node.Subclass) == subclassKey).GroupName;
        if (string.IsNullOrWhiteSpace(subclassCaption)) subclassCaption = "Other";
        string inventoryCaption = _professionInventorySlotFilter is int slotBit
            ? ProfessionFrameUiLaw.InventorySlotName(slotBit) : "All Slots";
        if (VanillaButton(dl, "##profession-subclass-filter", subclassCaption,
                origin + ProfessionFrameUiLaw.SubClassFilter.Min * scale,
                ProfessionFrameUiLaw.SubClassFilter.Size, scale))
            _professionFilterMenu = _professionFilterMenu == 1 ? 0 : 1;
        if (VanillaButton(dl, "##profession-inventory-filter", inventoryCaption,
                origin + ProfessionFrameUiLaw.InvSlotFilter.Min * scale,
                ProfessionFrameUiLaw.InvSlotFilter.Size, scale))
            _professionFilterMenu = _professionFilterMenu == 2 ? 0 : 2;
        if (_professionFilterMenu != 0)
            DrawProfessionFilterMenu(dl, origin, scale, nodes);
    }

    private ushort GetProfessionRank()
    {
        GetSkillValue(_professionLine, out ushort value, out _);
        return value;
    }

    private void DrawProfessionFilterMenu(ImDrawListPtr dl, Vector2 origin, float scale,
        IReadOnlyList<ProfessionFrameUiLaw.TradeSkillNode> nodes)
    {
        const float rowHeight = 18;
        bool subclass = _professionFilterMenu == 1;
        var choices = new List<(string Text, ulong? Subclass, int? Slot)>
        {
            (subclass ? "All Subclasses" : "All Slots", null, null),
        };
        if (subclass)
        {
            choices.AddRange(nodes.GroupBy(node => new
                {
                    Key = ProfessionFrameUiLaw.GroupKey(node.ItemClass, node.Subclass),
                    node.GroupName,
                })
                .OrderBy(group => group.Key.GroupName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(group => group.Key.Key)
                .Select(group => (string.IsNullOrWhiteSpace(group.Key.GroupName)
                        ? "Other" : group.Key.GroupName,
                    (ulong?)group.Key.Key, (int?)null)));
        }
        else
        {
            choices.AddRange(ProfessionFrameUiLaw.PresentInventorySlots(
                    nodes.Select(node => node.InventoryType))
                .Select(bit => (ProfessionFrameUiLaw.InventorySlotName(bit),
                    (ulong?)null, (int?)bit)));
        }
        Vector2 logicalMin = subclass ? ProfessionFrameUiLaw.FilterMenu :
            ProfessionFrameUiLaw.InventoryFilterMenu;
        float width = subclass ? ProfessionFrameUiLaw.SubClassFilter.Width :
            ProfessionFrameUiLaw.InvSlotFilter.Width;
        Vector2 min = origin + logicalMin * scale;
        Vector2 size = new(width, choices.Count * rowHeight + 4);
        dl.AddRectFilled(min, min + size * scale, 0xf0100804);
        dl.AddRect(min, min + size * scale, VanillaGold, 0, ImDrawFlags.None, scale);
        for (int i = 0; i < choices.Count; i++)
        {
            (string text, ulong? subclassKey, int? slot) = choices[i];
            bool selected = subclass ? _professionSubclassFilter == subclassKey :
                _professionInventorySlotFilter == slot;
            if (VanillaListRow(dl, $"##profession-filter-{_professionFilterMenu}-{i}",
                    min + new Vector2(2, 2 + i * rowHeight) * scale,
                    new Vector2(width - 4, rowHeight), scale,
                    (selected ? "✓ " : "  ") + text, selected, VanillaGold))
            {
                if (subclass) _professionSubclassFilter = subclassKey;
                else _professionInventorySlotFilter = slot;
                _professionFilterMenu = 0;
                _professionScroll = 0;
            }
        }
    }

    private int ProfessionCraftableCount(ProfessionRecipe recipe) =>
        ProfessionFrameUiLaw.CraftableCount(recipe.Reagents.Select(r =>
            (BackpackCount(r.ItemId), r.Count)));

    private bool CraftProfessionRecipeCount(int index, int count)
    {
        if (index < 0 || index >= _professionRecipes.Count) return false;
        int possible = ProfessionCraftableCount(_professionRecipes[index]);
        _professionBatchRemaining = Math.Clamp(count, 1, Math.Max(1, possible));
        return possible > 0 && CraftProfessionRecipe(index);
    }

    private void DrawProfessionRankBar(ImDrawListPtr dl, Vector2 origin, float scale,
        string name, uint value, uint maximum)
    {
        ProfessionFrameUiLaw.LogicalRect logical = ProfessionFrameUiLaw.Rank;
        Vector2 min = origin + logical.Min * scale;
        Vector2 size = logical.Size * scale;
        dl.AddRectFilled(min, min + size, 0x33ffffff);
        float fraction = ProfessionFrameUiLaw.RankFraction(value, maximum);
        uint fill = _gameplayArt?.Handle(ProfessionFrameUiLaw.RankFillPath) ?? 0;
        if (fill != 0 && fraction > 0)
            dl.AddImage((nint)fill, min, min + new Vector2(size.X * fraction, size.Y),
                Vector2.Zero, new Vector2(fraction, 1), 0xffbf4040);
        ProfessionFrameUiLaw.LogicalRect borderLogical = ProfessionFrameUiLaw.RankBorder;
        uint border = _gameplayArt?.Handle(ProfessionFrameUiLaw.RankBorderPath) ?? 0;
        if (border != 0)
        {
            Vector2 borderMin = origin + borderLogical.Min * scale;
            dl.AddImage((nint)border, borderMin, borderMin + borderLogical.Size * scale);
        }
        Vector2 text = min + new Vector2(6, 1) * scale;
        GameText.Draw(dl, "GameFontNormalSmall", name, text, scale, VanillaGold);
        float nameWidth = GameText.MeasureWidth("GameFontNormalSmall", name, scale);
        GameText.Draw(dl, "GameFontHighlightSmall", $"{value} / {maximum}",
            text + new Vector2(nameWidth + 13 * scale, 0), scale, 0xffffffff);
    }

    private void DrawProfessionScrollBar(ImDrawListPtr dl, Vector2 origin, float scale, int maximum)
    {
        void Arrow(string id, ProfessionFrameUiLaw.LogicalRect logical, bool upward)
        {
            bool enabled = upward ? _professionScroll > 0 : _professionScroll < maximum;
            Vector2 min = origin + logical.Min * scale;
            ImGui.SetCursorScreenPos(min);
            if (!enabled) ImGui.BeginDisabled();
            ImGui.InvisibleButton(id, logical.Size * scale);
            bool active = enabled && ImGui.IsItemActive();
            bool hovered = enabled && ImGui.IsItemHovered();
            bool clicked = enabled && ImGui.IsItemClicked();
            if (!enabled) ImGui.EndDisabled();
            string direction = upward ? "Up" : "Down";
            string state = !enabled ? "Disabled" : active ? "Down" : "Up";
            uint texture = _gameplayArt?.Handle(
                $@"Interface\Buttons\UI-ScrollBar-Scroll{direction}Button-{state}") ?? 0;
            if (texture != 0)
                dl.AddImage((nint)texture, min, min + logical.Size * scale,
                    new Vector2(.25f), new Vector2(.75f));
            if (hovered)
            {
                uint highlight = _gameplayArt?.AdditiveHandle(
                    $@"Interface\Buttons\UI-ScrollBar-Scroll{direction}Button-Highlight") ?? 0;
                if (highlight != 0)
                    dl.AddImage((nint)highlight, min, min + logical.Size * scale,
                        new Vector2(.25f), new Vector2(.75f));
            }
            if (clicked) _professionScroll += upward ? -1 : 1;
        }

        Arrow("##profession-scroll-up", ProfessionFrameUiLaw.ScrollUp, upward: true);
        Arrow("##profession-scroll-down", ProfessionFrameUiLaw.ScrollDown, upward: false);
        float thumbY = ProfessionFrameUiLaw.ScrollThumbY(_professionScroll, maximum);
        Vector2 thumbMin = origin + new Vector2(ProfessionFrameUiLaw.ScrollSlider.X, thumbY) * scale;
        Vector2 thumbSize = ProfessionFrameUiLaw.ScrollUp.Size * scale;
        uint knob = _gameplayArt?.Handle(ProfessionFrameUiLaw.ScrollKnobPath) ?? 0;
        if (knob != 0)
            dl.AddImage((nint)knob, thumbMin, thumbMin + thumbSize,
                new Vector2(.25f), new Vector2(.75f));
        Vector2 sliderMin = origin + ProfessionFrameUiLaw.ScrollSlider.Min * scale;
        ImGui.SetCursorScreenPos(sliderMin);
        ImGui.InvisibleButton("##profession-scroll-track",
            ProfessionFrameUiLaw.ScrollSlider.Size * scale);
        if (ImGui.IsItemActive())
        {
            float logicalY = (ImGui.GetIO().MousePos.Y - origin.Y) / scale;
            _professionScroll = ProfessionFrameUiLaw.ScrollFromThumb(logicalY, maximum);
        }
    }

    private void DrawProfessionCountSpinner(ImDrawListPtr dl, Vector2 origin, float scale,
        int craftable)
    {
        bool canDecrease = _professionCreateCount > 1;
        bool canIncrease = _professionCreateCount < Math.Max(1, craftable);
        if (VanillaButton(dl, "##profession-count-down", "−",
                origin + ProfessionFrameUiLaw.CountDecrement.Min * scale,
                ProfessionFrameUiLaw.CountDecrement.Size, scale, canDecrease))
            _professionCreateCount--;
        if (VanillaInputInt(dl, "##profession-count", ref _professionCreateCount,
                origin + ProfessionFrameUiLaw.CountInput.Min * scale,
                ProfessionFrameUiLaw.CountInput.Size, scale))
            _professionCreateCount = ProfessionFrameUiLaw.ClampCreateCount(
                _professionCreateCount, craftable);
        if (VanillaButton(dl, "##profession-count-up", "+",
                origin + ProfessionFrameUiLaw.CountIncrement.Min * scale,
                ProfessionFrameUiLaw.CountIncrement.Size, scale, canIncrease))
            _professionCreateCount++;
    }

    private static Vector4 ProfessionColor(string color) => color switch
    {
        "orange" => new(1f, .5f, 0f, 1f),
        "yellow" => new(1f, 1f, 0f, 1f),
        "green" => new(.2f, 1f, .2f, 1f),
        _ => new(.55f, .55f, .55f, 1f),
    };
}
