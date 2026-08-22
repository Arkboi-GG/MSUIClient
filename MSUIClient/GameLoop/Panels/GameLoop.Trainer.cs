using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private TrainerList? _trainer;
    private HashSet<uint>? _trainerKnownBefore;
    private int _trainerSelected;
    private int _trainerScroll;
    private readonly HashSet<uint> _trainerCollapsedGroups = [];
    private bool _trainerFilterAvailable = true;
    private bool _trainerFilterUnavailable = true;
    private bool _trainerFilterUsed;
    private bool _trainerFilterOpen;

    private bool RequestTrainer(ulong guid)
    {
        string outcome = "REFUSED"; string detail = "descriptorMissing";
        if (_net is { IsInWorld: true } && _controller is not null &&
            _entities.TryGet(guid, out WorldEntity npc) && npc.IsCreature && !npc.IsDead &&
            (npc.NpcFlags & NpcTrainer) != 0)
        {
            Vector3 delta = _controller.Position - npc.Position;
            float distance = delta.Length();
            if (NpcSessionUiLaw.InRange(delta.LengthSquared()))
            {
                bool sent = _net.TrainerList(guid); outcome = sent ? "SENT" : "SEND_FAILED";
                detail = $"distance={distance:R};npcFlags=0x{npc.NpcFlags:X8}";
            }
            else { outcome = "REFUSED_RANGE"; detail = $"distance={distance:R};limit={GossipInteractDistance:R}"; }
        }
        EmitInterface("trainer", "list", outcome, guid, detail); return outcome == "SENT";
    }

    private bool UpdateTrainerLifecycle()
    {
        if (_trainer is null || _controller is null) return false;
        ulong trainerGuid = _trainer.TrainerGuid;
        bool sourceAvailable = _entities.TryGet(trainerGuid, out WorldEntity trainer);
        float distanceSquared = sourceAvailable
            ? Vector3.DistanceSquared(_controller.Position, trainer.Position)
            : float.PositiveInfinity;
        if (!NpcSessionUiLaw.ShouldClose(true, true, sourceAvailable, distanceSquared))
            return false;
        CloseTrainerSession(playSound: true);
        EmitInterface("trainer", "lifecycle-close", "CLOSED", trainerGuid,
            sourceAvailable
                ? $"distanceSquared={distanceSquared:R};limitSquared={NpcSessionUiLaw.ServiceRangeSquared:R}"
                : "source-despawned");
        return true;
    }

    private void ApplyTrainerList(byte[] body)
    {
        TrainerList incoming = TrainerPackets.ParseList(body);
        bool freshSession = _trainer?.TrainerGuid != incoming.TrainerGuid;
        if (_trainer is not null && freshSession) CloseTrainerSession(playSound: true);
        _trainer = incoming;
        if (freshSession)
        {
            _trainerSelected = 0;
            _trainerScroll = 0;
            _trainerCollapsedGroups.Clear();
            _trainerFilterOpen = false;
            PlayUiSound(TrainerFrameUiLaw.OpenSound, TrainerFrameUiLaw.SoundCategory);
        }
        int available = _trainer.Spells.Count(s => s.State == 0);
        uint money = 0;
        if (_net is not null && _entities.TryGet(_net.PlayerGuid, out WorldEntity player)) money = player.Fields.Coinage;
        EmitInterface("trainer", "list", "DECODED", _trainer.TrainerGuid,
            $"type={_trainer.TrainerType};spells={_trainer.Spells.Count};available={available};money={money};greeting={SanitizeEvidence(_trainer.Greeting)}");
    }

    private bool CloseTrainerSession(bool playSound = true)
    {
        if (_trainer is null) return false;
        ulong guid = _trainer.TrainerGuid;
        _trainer = null;
        _trainerKnownBefore = null;
        if (playSound)
            PlayUiSound(TrainerFrameUiLaw.CloseSound, TrainerFrameUiLaw.SoundCategory);
        EmitInterface("trainer", "close", "CLOSED", guid, $"sound={playSound}");
        return true;
    }

    private void SimulateTrainerList()
    {
        var w = new PacketWriter();
        w.WriteU64(_selectionGuid == 0 ? 0xF13000038Ful : _selectionGuid);
        w.WriteU32(0); w.WriteU32(3);
        WriteTrainerRow(w, 6673, 0, 100, 1);
        WriteTrainerRow(w, 78, 1, 1000, 40);
        WriteTrainerRow(w, 100, 2, 10, 4);
        w.WriteCString("What can I teach you?");
        ApplyTrainerList(w.ToArray());
    }

    private static void WriteTrainerRow(PacketWriter w, uint spell, byte state, uint cost, byte level)
    {
        w.WriteU32(spell); w.WriteU8(state); w.WriteU32(cost);
        w.WriteU32(0); w.WriteU32(0); w.WriteU8(level);
        for (int i = 0; i < 5; i++) w.WriteU32(0);
    }

    private bool BuyTrainerSpell(uint serviceSpellId)
    {
        TrainerSpell? row = _trainer?.Spells.FirstOrDefault(s => s.ServiceSpellId == serviceSpellId);
        if (_trainer is null || row is not { ServiceSpellId: not 0 } spell)
        { EmitInterface("trainer", "buy", "REFUSED_UNKNOWN", _trainer?.TrainerGuid ?? 0, $"spell={serviceSpellId}"); return false; }
        uint money = 0;
        if (_net is not null && _entities.TryGet(_net.PlayerGuid, out WorldEntity player)) money = player.Fields.Coinage;
        if (spell.State != 0 || money < spell.Cost)
        {
            string reason = spell.State != 0 ? $"state={spell.State}" : $"money={money};cost={spell.Cost}";
            EmitInterface("trainer", "buy", "REFUSED_UNAVAILABLE", _trainer.TrainerGuid,
                $"spell={serviceSpellId};{reason}"); return false;
        }
        _trainerKnownBefore = _actions.KnownSpells.ToHashSet();
        bool sent = _net?.TrainerBuySpell(_trainer.TrainerGuid, serviceSpellId) == true;
        EmitInterface("trainer", "buy", sent ? "SENT" : "SEND_FAILED", _trainer.TrainerGuid,
            $"spell={serviceSpellId};cost={spell.Cost};money={money}");
        return sent;
    }

    private bool BuyFirstAvailableTrainerSpell()
    {
        TrainerSpell? row = _trainer?.Spells.FirstOrDefault(s => s.State == 0);
        if (row is not { ServiceSpellId: not 0 } found) return false;
        return BuyTrainerSpell(found.ServiceSpellId);
    }

    private void ApplyTrainerSuccess(byte[] body)
    {
        TrainerResult result = TrainerPackets.ParseSuccess(body);
        EmitInterface("trainer", "buy", "SUCCEEDED", result.TrainerGuid,
            $"serviceSpell={result.ServiceSpellId};knownBefore={_trainerKnownBefore?.Count ?? _actions.KnownSpells.Count}");
    }

    private void ApplyTrainerFailure(byte[] body)
    {
        TrainerResult result = TrainerPackets.ParseFailure(body);
        string reason = result.Error switch { 0 => "UNAVAILABLE", 1 => "NOT_ENOUGH_MONEY", 2 => "NOT_ENOUGH_SKILL", _ => $"ERROR_{result.Error}" };
        EmitInterface("trainer", "buy", "FAILED", result.TrainerGuid,
            $"serviceSpell={result.ServiceSpellId};reason={reason}");
    }

    private void ObserveTrainerLearned(uint spellId)
    {
        if (_trainerKnownBefore is null) return;
        bool added = !_trainerKnownBefore.Contains(spellId) && _actions.KnownSpells.Contains(spellId);
        EmitInterface("trainer", "spellbook-delta", added ? "ADDED" : "UNCHANGED",
            _trainer?.TrainerGuid ?? 0, $"learnedSpell={spellId};knownAfter={_actions.KnownSpells.Count}");
        _trainerKnownBefore = null;
    }

    private void DrawTrainerFrame()
    {
        if (_trainer is null||_gameplayArt is null) return;
        float scale=GameplayUiScale();Vector2 origin=TrainerFrameUiLaw.FrameOrigin(scale),size=TrainerFrameUiLaw.FrameSize(scale);
        ImGui.SetNextWindowPos(origin,ImGuiCond.Always);ImGui.SetNextWindowSize(size,ImGuiCond.Always);ImGui.SetNextWindowBgAlpha(0);
        if(!ImGui.Begin("##trainer",ImGuiWindowFlags.NoDecoration|ImGuiWindowFlags.NoMove|ImGuiWindowFlags.NoSavedSettings|ImGuiWindowFlags.NoBackground|ImGuiWindowFlags.NoNav)){ImGui.End();return;}
        ImDrawListPtr dl=ImGui.GetWindowDrawList();if(_uiParityArmed&&_uiParityPanel=="trainer"){BeginUiParityFrame(origin,scale);CollectUiParityDraw("ClassTrainerFrame","Frame",origin,size,"",new("",0,"IMGUI_HOST","ANCHOR:ABSOLUTE","","",0,8));}
        WorldEntity? trainerNpc = _entities.TryGet(_trainer.TrainerGuid, out WorldEntity foundTrainer)
            ? foundTrainer : null;
        if (trainerNpc is not null)
            DrawUnitPortraitImage(dl, trainerNpc,
                origin + TrainerFrameUiLaw.PortraitOffset * scale,
                TrainerFrameUiLaw.PortraitSize * scale, 0, false);
        (string Element,string Path,Vector2 Offset,Vector2 Size)[] art=[("ClassTrainerFrame/Texture",@"Interface\ClassTrainerFrame\UI-ClassTrainer-TopLeft",Vector2.Zero,new(256,256)),("ClassTrainerFrame/Texture#2",@"Interface\ClassTrainerFrame\UI-ClassTrainer-TopRight",new(256,0),new(128,256)),("ClassTrainerFrameBottomLeft",@"Interface\ClassTrainerFrame\UI-ClassTrainer-BotLeft",new(0,256),new(256,256)),("ClassTrainerFrameBottomRight",@"Interface\ClassTrainerFrame\UI-ClassTrainer-BotRight",new(256,256),new(128,256))];
        foreach(var r in art){Vector2 m=origin+r.Offset*scale;DrawArt(dl,r.Path,m,r.Size,scale);if(_uiParityArmed&&_uiParityPanel=="trainer")CollectUiParityDraw(r.Element,"Texture",m,r.Size*scale,"ClassTrainerFrame",new(r.Path,0xffffffff,"IMGUI_IMAGE","TOPLEFT","ClassTrainerFrame","TOPLEFT",r.Offset.X,-r.Offset.Y));}
        uint money = 0;
        if (_net is not null && _entities.TryGet(_net.PlayerGuid, out WorldEntity player)) money = player.Fields.Coinage;
        string trainerName = TrainerFrameUiLaw.FallbackTitle;
        if (trainerNpc is not null)
        {
            if (trainerNpc.Entry != 0 && TryBeginCreatureQuery(trainerNpc.Entry))
                _net?.CreatureQuery(trainerNpc.Entry, trainerNpc.Guid);
            trainerName = TrainerFrameUiLaw.Title(
                _creatureNames.GetValueOrDefault(trainerNpc.Entry, ""));
        }
        DrawNpcModalTitle(dl, trainerName,
            origin + TrainerFrameUiLaw.TitleCenter(
                GameText.EmPixels("GameFontNormal", scale) / scale) * scale, scale);
        DrawTrainerMoney(dl, money, origin + TrainerFrameUiLaw.PurseRightTop * scale,
            scale, 0xffffffff, rightAligned: true);
        DrawWrappedText(dl,_trainer.Greeting,origin+new Vector2(76,38)*scale,260,10*scale,scale,0xffffffff,2);
        List<TrainerFrameUiLaw.ServiceNode> nodes = [];
        for (int serviceIndex = 0; serviceIndex < _trainer.Spells.Count; serviceIndex++)
        {
            TrainerSpell service = _trainer.Spells[serviceIndex];
            if (_spellCatalog?.TryGet(service.ServiceSpellId, out SpellInfo wire) != true) continue;
            (uint groupKey, string groupName) = TrainerFrameUiLaw.ServiceGroup(
                _trainer.TrainerType, service.State, wire, _skillLines);
            nodes.Add(new(serviceIndex, groupKey, groupName, wire.Name,
                service.State, service.RequiredLevel));
        }
        IReadOnlyList<TrainerFrameUiLaw.TreeRow> tree = TrainerFrameUiLaw.BuildTree(nodes,
            _trainer.TrainerType, _trainerCollapsedGroups, _trainerFilterAvailable,
            _trainerFilterUnavailable, _trainerFilterUsed);
        HashSet<int> visibleServices = tree.Where(row => !row.Header)
            .Select(row => row.ServiceIndex).ToHashSet();
        if (!visibleServices.Contains(_trainerSelected))
            _trainerSelected = visibleServices.FirstOrDefault(-1);

        Vector2 collapseAt = origin + TrainerFrameUiLaw.CollapseAllOffset * scale;
        bool allCollapsed = tree.Where(row => row.Header)
            .All(row => _trainerCollapsedGroups.Contains(row.GroupKey));
        if (VanillaButton(dl, "##trainer-collapse-all", allCollapsed ? "+ All" : "− All",
                collapseAt, new Vector2(44, 22), scale, tree.Any(row => row.Header)))
        {
            uint[] groups = nodes.Select(node => node.GroupKey).Where(key => key != 0).Distinct().ToArray();
            if (groups.Length > 0 && groups.All(_trainerCollapsedGroups.Contains))
                _trainerCollapsedGroups.Clear();
            else
                foreach (uint group in groups) _trainerCollapsedGroups.Add(group);
            _trainerScroll = 0;
        }
        if (VanillaButton(dl, "##trainer-filter", "Filter",
                origin + TrainerFrameUiLaw.FilterOffset * scale,
                new Vector2(96, 22), scale))
            _trainerFilterOpen = !_trainerFilterOpen;

        int maximum=Math.Max(0,tree.Count-TrainerFrameUiLaw.VisibleRows);
        if(ImGui.IsMouseHoveringRect(origin+new Vector2(22,96)*scale,origin+new Vector2(315,280)*scale,false))
        {
            float wheel=ImGui.GetIO().MouseWheel;
            if(wheel!=0)_trainerScroll=Math.Clamp(_trainerScroll-(int)MathF.Sign(wheel),0,maximum);
        }
        for(int visible=0;visible<TrainerFrameUiLaw.VisibleRows;visible++)
        {
            int index=_trainerScroll+visible;if(index>=tree.Count)break;
            TrainerFrameUiLaw.TreeRow displayRow=tree[index];
            Vector2 min=origin+new Vector2(22,100+visible*16)*scale;
            if (displayRow.Header)
            {
                ImGui.SetCursorScreenPos(min);
                if (ImGui.InvisibleButton($"##trainer-header-{displayRow.GroupKey}",
                        new Vector2(293,16)*scale))
                {
                    if (!_trainerCollapsedGroups.Add(displayRow.GroupKey))
                        _trainerCollapsedGroups.Remove(displayRow.GroupKey);
                    _trainerScroll = Math.Min(_trainerScroll, Math.Max(0, tree.Count - 1));
                }
                string fold = displayRow.Expanded ? "−" : "+";
                GameText.Draw(dl, "GameFontNormalSmall", fold,
                    min + new Vector2(1, 1) * scale, scale, VanillaGold);
                GameText.Draw(dl, "GameFontNormalSmall", displayRow.Text,
                    min + new Vector2(18, 1) * scale, scale, VanillaGold);
                continue;
            }
            TrainerSpell row=_trainer.Spells[displayRow.ServiceIndex];
            bool enabled = row.State == 0 && money >= row.Cost;
            uint color=row.State==2?0xff777777u:enabled?VanillaGold:0xff6666ffu;
            if(VanillaListRow(dl,$"##trainer-{row.ServiceSpellId}",min,new Vector2(293,16),scale,
                    "  " + displayRow.Text,displayRow.ServiceIndex==_trainerSelected,color))
                _trainerSelected=displayRow.ServiceIndex;
            if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Requires level {row.RequiredLevel}; skill {row.RequiredSkill}:{row.RequiredSkillValue}");
        }
        DrawVanillaScrollBar(dl,"##trainer-scroll",origin+new Vector2(310,91)*scale,196,scale,
            _trainerScroll,maximum,x=>_trainerScroll=x);
        DrawArt(dl,@"Interface\ClassTrainerFrame\UI-ClassTrainer-HorizontalBar",origin+new Vector2(15,275)*scale,new Vector2(256,16),scale);
        uint bar=_gameplayArt.Handle(@"Interface\ClassTrainerFrame\UI-ClassTrainer-HorizontalBar");
        if(bar!=0)dl.AddImage((nint)bar,origin+new Vector2(271,275)*scale,
            origin+new Vector2(346,291)*scale,new Vector2(0,.25f),new Vector2(.29296875f,.5f));
        if (_trainerFilterOpen)
            DrawTrainerFilterMenu(dl, origin, scale);
        TrainerSpell selected=_trainerSelected<0||_trainerSelected>=_trainer.Spells.Count
            ?default:_trainer.Spells[_trainerSelected];
        if(selected.ServiceSpellId!=0)
        {
            SpellInfo? info=_spellCatalog?.TryGet(selected.ServiceSpellId,out SpellInfo found)==true?found:null;
            uint icon=_gameplayArt.Handle(info?.IconPath??@"Interface\Icons\INV_Misc_QuestionMark.blp");
            Vector2 iconMin=origin+new Vector2(27,294)*scale;
            if(icon!=0)dl.AddImage((nint)icon,iconMin,iconMin+new Vector2(37)*scale);
            DrawArt(dl,@"Interface\Buttons\UI-EmptySlot",iconMin-new Vector2(13,-13)*scale,new Vector2(64),scale);
            dl.AddText(ImGui.GetFont(),11*scale,origin+new Vector2(68,292)*scale,VanillaGold,info?.Name??$"Service {selected.ServiceSpellId}");
            dl.AddText(ImGui.GetFont(),9*scale,origin+new Vector2(68,312)*scale,0xffffffff,$"Requires level {selected.RequiredLevel}");
            Vector2 costAt = origin + TrainerFrameUiLaw.DetailCostLabel * scale;
            GameText.Draw(dl, "GameFontNormalSmall", "Cost:", costAt, scale);
            float labelWidth = GameText.MeasureWidth("GameFontNormalSmall", "Cost:", scale);
            DrawTrainerMoney(dl, selected.Cost,
                costAt + new Vector2(labelWidth + TrainerFrameUiLaw.MoneyGap * scale, 0),
                scale, money >= selected.Cost ? 0xffffffff : 0xff1a1aff,
                rightAligned: false);
            DrawWrappedText(dl,info?.Description??"",origin+new Vector2(27,365)*scale,295,9*scale,scale,0xffffffff,3);
        }
        bool canTrain=selected.ServiceSpellId!=0&&selected.State==0&&money>=selected.Cost;
        if(VanillaButton(dl,"##trainer-train","Train",origin+new Vector2(184,409)*scale,new Vector2(80,22),scale,canTrain))BuyTrainerSpell(selected.ServiceSpellId);
        if(VanillaButton(dl,"##trainer-exit","Exit",origin+new Vector2(265,409)*scale,new Vector2(80,22),scale))CloseTrainerSession();
        Vector2 close=origin+new Vector2(322,8)*scale;DrawImageButton(dl,"##trainer-close",close,new Vector2(32)*scale,@"Interface\Buttons\UI-Panel-MinimizeButton-Up",@"Interface\Buttons\UI-Panel-MinimizeButton-Down",@"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");if(ImGui.IsItemClicked())CloseTrainerSession();
        if(_uiParityArmed&&_uiParityPanel=="trainer")MarkUiParityFrameComplete();
        ImGui.End();
    }

    private void DrawTrainerFilterMenu(ImDrawListPtr draw, Vector2 origin, float scale)
    {
        Vector2 menu = origin + TrainerFrameUiLaw.FilterMenuOffset * scale;
        draw.AddRectFilled(menu, menu + new Vector2(126, 58) * scale, 0xf0181818u);
        draw.AddRect(menu, menu + new Vector2(126, 58) * scale, VanillaGold);
        (string Label, bool Value, uint Color)[] rows =
        [
            ("Available", _trainerFilterAvailable, 0xff20ff20),
            ("Unavailable", _trainerFilterUnavailable, 0xff2020ff),
            ("Already Known", _trainerFilterUsed, 0xff808080),
        ];
        for (int i = 0; i < rows.Length; i++)
        {
            Vector2 min = menu + new Vector2(3, 3 + i * 17) * scale;
            ImGui.SetCursorScreenPos(min);
            if (ImGui.InvisibleButton($"##trainer-filter-{i}", new Vector2(120, 16) * scale))
            {
                if (i == 0) _trainerFilterAvailable = !_trainerFilterAvailable;
                else if (i == 1) _trainerFilterUnavailable = !_trainerFilterUnavailable;
                else _trainerFilterUsed = !_trainerFilterUsed;
                _trainerScroll = 0;
            }
            GameText.Draw(draw, "GameFontNormalSmall", (rows[i].Value ? "✓ " : "  ") + rows[i].Label,
                min + new Vector2(2, 1) * scale, scale, rows[i].Color);
        }
    }

    private void DrawTrainerMoney(ImDrawListPtr draw, uint copper, Vector2 anchor, float scale,
        uint color, bool rightAligned)
    {
        IReadOnlyList<MailUiLaw.MoneyDenomination> denominations = MailUiLaw.Money(copper);
        float width = denominations.Sum(denomination =>
            GameText.MeasureWidth("NumberFontNormal", denomination.Value.ToString(), scale) +
            TrainerFrameUiLaw.MoneyIconSize * scale) +
            Math.Max(0, denominations.Count - 1) * TrainerFrameUiLaw.MoneyGap * scale;
        float x = rightAligned ? anchor.X - width : anchor.X;
        foreach (MailUiLaw.MoneyDenomination denomination in denominations)
        {
            string text = denomination.Value.ToString();
            GameText.Draw(draw, "NumberFontNormal", text, new Vector2(x, anchor.Y), scale, color);
            x += GameText.MeasureWidth("NumberFontNormal", text, scale);
            DrawMailCoin(draw, denomination.Icon, new Vector2(x, anchor.Y), scale, color);
            x += (TrainerFrameUiLaw.MoneyIconSize + TrainerFrameUiLaw.MoneyGap) * scale;
        }
    }
}
