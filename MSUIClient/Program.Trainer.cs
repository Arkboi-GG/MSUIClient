using System.Numerics;
using ImGuiNET;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private TrainerList? _trainer;
    private HashSet<uint>? _trainerKnownBefore;

    private bool RequestTrainer(ulong guid)
    {
        string outcome = "REFUSED"; string detail = "descriptorMissing";
        if (_net is { IsInWorld: true } && _controller is not null &&
            _entities.TryGet(guid, out WorldEntity npc) && npc.IsCreature && !npc.IsDead &&
            (npc.NpcFlags & NpcTrainer) != 0)
        {
            float distance = Vector3.Distance(_controller.Position, npc.Position);
            if (distance <= GossipInteractDistance)
            {
                bool sent = _net.TrainerList(guid); outcome = sent ? "SENT" : "SEND_FAILED";
                detail = $"distance={distance:R};npcFlags=0x{npc.NpcFlags:X8}";
            }
            else { outcome = "REFUSED_RANGE"; detail = $"distance={distance:R};limit={GossipInteractDistance:R}"; }
        }
        EmitInterface("trainer", "list", outcome, guid, detail); return outcome == "SENT";
    }

    private void ApplyTrainerList(byte[] body)
    {
        _trainer = TrainerPackets.ParseList(body);
        int available = _trainer.Spells.Count(s => s.State == 0);
        uint money = 0;
        if (_net is not null && _entities.TryGet(_net.PlayerGuid, out WorldEntity player)) money = player.Fields.Coinage;
        EmitInterface("trainer", "list", "DECODED", _trainer.TrainerGuid,
            $"type={_trainer.TrainerType};spells={_trainer.Spells.Count};available={available};money={money};greeting={SanitizeEvidence(_trainer.Greeting)}");
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
        float scale=GameplayUiScale();Vector2 origin=new(0,8*scale),logicalSize=new(384,512);
        ImGui.SetNextWindowPos(origin,ImGuiCond.Always);ImGui.SetNextWindowSize(logicalSize*scale,ImGuiCond.Always);ImGui.SetNextWindowBgAlpha(0);
        if(!ImGui.Begin("##trainer",ImGuiWindowFlags.NoDecoration|ImGuiWindowFlags.NoMove|ImGuiWindowFlags.NoSavedSettings|ImGuiWindowFlags.NoBackground|ImGuiWindowFlags.NoNav)){ImGui.End();return;}
        ImDrawListPtr dl=ImGui.GetWindowDrawList();if(_uiParityArmed&&_uiParityPanel=="trainer"){BeginUiParityFrame(origin,scale);CollectUiParityDraw("ClassTrainerFrame","Frame",origin,logicalSize*scale,"",new("",0,"IMGUI_HOST","ANCHOR:ABSOLUTE","","",0,8));}
        (string Element,string Path,Vector2 Offset,Vector2 Size)[] art=[("ClassTrainerFrame/Texture",@"Interface\ClassTrainerFrame\UI-ClassTrainer-TopLeft",Vector2.Zero,new(256,256)),("ClassTrainerFrame/Texture#2",@"Interface\ClassTrainerFrame\UI-ClassTrainer-TopRight",new(256,0),new(128,256)),("ClassTrainerFrameBottomLeft",@"Interface\ClassTrainerFrame\UI-ClassTrainer-BotLeft",new(0,256),new(256,256)),("ClassTrainerFrameBottomRight",@"Interface\ClassTrainerFrame\UI-ClassTrainer-BotRight",new(256,256),new(128,256))];
        foreach(var r in art){Vector2 m=origin+r.Offset*scale;DrawArt(dl,r.Path,m,r.Size,scale);if(_uiParityArmed&&_uiParityPanel=="trainer")CollectUiParityDraw(r.Element,"Texture",m,r.Size*scale,"ClassTrainerFrame",new(r.Path,0xffffffff,"IMGUI_IMAGE","TOPLEFT","ClassTrainerFrame","TOPLEFT",r.Offset.X,-r.Offset.Y));}
        ImGui.SetCursorScreenPos(origin+new Vector2(30,75)*scale);ImGui.BeginChild("##trainer-list",new Vector2(310,350)*scale,false);ImGui.TextWrapped(_trainer.Greeting);ImGui.Separator();
        uint money = 0;
        if (_net is not null && _entities.TryGet(_net.PlayerGuid, out WorldEntity player)) money = player.Fields.Coinage;
        foreach (TrainerSpell row in _trainer.Spells)
        {
            string name = _spellCatalog?.TryGet(row.ServiceSpellId, out var spell) == true
                ? spell.Name : $"Service {row.ServiceSpellId}";
            string state = row.State switch { 0 => "Available", 1 => "Unavailable", 2 => "Known", _ => $"State {row.State}" };
            bool enabled = row.State == 0 && money >= row.Cost;
            ImGui.BeginDisabled(!enabled);
            if (ImGui.Selectable($"{name} - {state} - {FormatMoney(row.Cost)}##trainer-{row.ServiceSpellId}"))
                BuyTrainerSpell(row.ServiceSpellId);
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Requires level {row.RequiredLevel}; skill {row.RequiredSkill}:{row.RequiredSkillValue}");
        }
        ImGui.EndChild();
        Vector2 close=origin+new Vector2(322,8)*scale;DrawImageButton(dl,"##trainer-close",close,new Vector2(32)*scale,@"Interface\Buttons\UI-Panel-MinimizeButton-Up",@"Interface\Buttons\UI-Panel-MinimizeButton-Down",@"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");if(ImGui.IsItemClicked())_trainer=null;
        if(_uiParityArmed&&_uiParityPanel=="trainer")MarkUiParityFrameComplete();
        ImGui.End();
    }
}
