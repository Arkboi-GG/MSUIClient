using System.Numerics;
using ImGuiNET;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private TalentCatalog? _talents;
    private bool _talentOpen;
    private uint _talentSelectedTab;
    private uint _talentWipeCost;
    private ulong _talentWipeTrainer;
    private (uint Talent, uint Rank, uint Points, double SentAt, bool RankSeen)? _pendingTalent;

    private void InitTalents()
    {
        if (_mpq is null) return;
        try
        {
            _talents = TalentCatalog.Load(_mpq);
            Console.WriteLine(_talents is null ? "[talents] Talent/TalentTab DBC unavailable" :
                $"[talents] catalog ready ({_talents.TalentCount} talents, {_talents.Tabs.Count} tabs)");
        }
        catch (Exception ex) { Console.WriteLine($"[talents] catalog failed: {ex.Message}"); }
    }

    private uint TalentPoints()
        => _net is not null && _entities.TryGet(_net.PlayerGuid, out var player) ? player.Fields.TalentPoints : 0;

    private int TalentRank(TalentInfo talent)
    {
        int rank = 0;
        for (int i = 0; i < talent.RankSpells.Length; i++)
            if (_actions.KnownSpells.Contains(talent.RankSpells[i])) rank = i + 1;
        return rank;
    }

    private bool TalentEligible(TalentInfo talent, out string reason)
    {
        if (_talents is null) { reason = "catalog-unavailable"; return false; }
        uint points = TalentPoints();
        int rank = TalentRank(talent);
        if (points == 0) { reason = "no-free-points"; return false; }
        if (rank >= talent.RankSpells.Length) { reason = "max-rank"; return false; }
        int tabSpent = _talents.TalentsForTab(talent.TabId).Sum(TalentRank);
        if (tabSpent < talent.Row * 5) { reason = $"tier-needs-{talent.Row * 5}-has-{tabSpent}"; return false; }
        if (talent.DependsOn != 0 && _talents.TryGet(talent.DependsOn, out TalentInfo prerequisite) &&
            TalentRank(prerequisite) <= talent.DependsOnRank)
        { reason = $"prerequisite-{talent.DependsOn}-rank-{talent.DependsOnRank + 1}"; return false; }
        if (talent.RequiredSpell != 0 && !_actions.KnownSpells.Contains(talent.RequiredSpell))
        { reason = $"required-spell-{talent.RequiredSpell}"; return false; }
        reason = "pass"; return true;
    }

    private bool SpendTalent(uint talentId)
    {
        if (_net is null || _talents is null || !_talents.TryGet(talentId, out TalentInfo talent)) return false;
        int rank = TalentRank(talent);
        bool pass = TalentEligible(talent, out string reason);
        EmitInterface("talent", "pre-send-gate", pass ? "PASS" : "REFUSED", talentId,
            $"requestedRank={rank};points={TalentPoints()};tab={talent.TabId};row={talent.Row};column={talent.Column};reason={reason}");
        if (!pass) return false;
        byte[] body = WorldSession.BuildLearnTalentBody(talentId, (uint)rank);
        _pendingTalent = (talentId, (uint)rank, TalentPoints(), NowSeconds(), false);
        _net.LearnTalent(talentId, (uint)rank);
        EmitInterface("talent", "spend-send", "SENT", talentId,
            $"requestedRank={rank};body={Convert.ToHexString(body)}");
        return true;
    }

    private bool SpendFirstEligibleTalent()
    {
        byte cls = _net is not null && _entities.TryGet(_net.PlayerGuid, out var p) ? p.Fields.Bytes0.Class : (byte)0;
        if (_talents is null) return false;
        foreach (TalentInfo talent in _talents.TabsForClass(cls).SelectMany(t => _talents.TalentsForTab(t.Id)))
            if (TalentEligible(talent, out _)) return SpendTalent(talent.Id);
        EmitInterface("talent", "spend-send", "REFUSED", _net?.PlayerGuid ?? 0,
            $"class={cls};points={TalentPoints()};reason=no-eligible-talent");
        return false;
    }

    private void ObserveTalentTransition()
    {
        if (_pendingTalent is not { } pending || _talents is null || !_talents.TryGet(pending.Talent, out TalentInfo talent)) return;
        uint points = TalentPoints(); int rank = TalentRank(talent);
        if (points < pending.Points)
        {
            EmitInterface("talent", "server-confirm", "CONFIRMED", pending.Talent,
                $"requestedRank={pending.Rank};rankAfter={rank};pointsBefore={pending.Points};pointsAfter={points};delta={(int)points - pending.Points}");
            _pendingTalent = null;
        }
        else if (rank > pending.Rank && !pending.RankSeen)
        {
            EmitInterface("talent", "server-spell-confirm", "CONFIRMED", pending.Talent,
                $"requestedRank={pending.Rank};rankAfter={rank};source=SMSG_LEARNED_SPELL");
            _pendingTalent = (pending.Talent, pending.Rank, pending.Points, pending.SentAt, true);
        }
        else if (NowSeconds() - pending.SentAt > 5)
        {
            EmitInterface("talent", "server-confirm", "NO_DATA", pending.Talent,
                $"requestedRank={pending.Rank};rankAfter={rank};pointsBefore={pending.Points};pointsAfter={points}");
            _pendingTalent = null;
        }
    }

    private void ApplyTalentWipeConfirm(ReadOnlySpan<byte> body)
    {
        var r = new PacketReader(body.ToArray()); ulong guid = r.ReadU64(); uint cost = r.ReadU32();
        if (r.Remaining != 0) throw new InvalidDataException($"talent wipe response trailing bytes={r.Remaining}");
        _talentWipeTrainer = guid; _talentWipeCost = cost; _talentOpen = true;
        EmitInterface("talent", "unlearn-cost", guid == 0 ? "NO_TALENTS" : "DISPLAYED", guid,
            $"costCopper={cost};costText={MoneyText(cost)};body={Convert.ToHexString(body)}");
    }

    private bool ConfirmTalentWipe()
    {
        if (_net is null || _talentWipeTrainer == 0) return false;
        byte[] body = WorldSession.BuildTalentWipeBody(_talentWipeTrainer);
        _net.ConfirmTalentWipe(_talentWipeTrainer);
        EmitInterface("talent", "unlearn-confirm", "SENT", _talentWipeTrainer,
            $"costCopper={_talentWipeCost};body={Convert.ToHexString(body)}");
        return true;
    }

    private bool OpenTalentPanel()
    {
        byte cls = _net is not null && _entities.TryGet(_net.PlayerGuid, out var p) ? p.Fields.Bytes0.Class : (byte)0;
        TalentTabInfo? first = _talents?.TabsForClass(cls).Cast<TalentTabInfo?>().FirstOrDefault();
        if (first is null) return false;
        _talentSelectedTab = first.Value.Id; _talentOpen = true;
        EmitTalentSnapshot(cls); return true;
    }

    private void EmitTalentSnapshot(byte cls)
    {
        if (_talents is null) return;
        var tabs = _talents.TabsForClass(cls).ToArray();
        EmitInterface("talent", "panel", tabs.Length == 3 ? "COMPLETE" : "INCOMPLETE", cls,
            $"class={cls};tabs={tabs.Length};points={TalentPoints()};names={string.Join('|', tabs.Select(x => SanitizeEvidence(x.Name)))}");
        foreach (TalentTabInfo tab in tabs)
            EmitInterface("talent", "tree", "DECODED", tab.Id,
                $"class={cls};page={tab.Page};name={SanitizeEvidence(tab.Name)};talents={_talents.TalentsForTab(tab.Id).Count()};background={SanitizeEvidence(tab.Background)}");
    }

    private void SimulateTalentRoster()
    {
        if (_talents is null) return;
        foreach (byte cls in new byte[] { 1, 2, 3, 4, 5, 7, 8, 9, 11 }) EmitTalentSnapshot(cls);
        var w = new PacketWriter(12); w.WriteU64(0xF1300001CB000001); w.WriteU32(10000);
        ApplyTalentWipeConfirm(w.ToArray());
    }

    private void DrawTalentFrame()
    {
        if (!_talentOpen || _talents is null) return;
        byte cls = _net is not null && _entities.TryGet(_net.PlayerGuid, out var p) ? p.Fields.Bytes0.Class : (byte)0;
        TalentTabInfo[] tabs = _talents.TabsForClass(cls).ToArray();
        ImGui.SetNextWindowPos(new Vector2(230, 55), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(720, 580), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin($"Talents - {ClassName(cls)}##talents", ref _talentOpen)) { ImGui.End(); return; }
        ImGui.TextUnformatted($"Talent Points: {TalentPoints()}"); ImGui.SameLine();
        if (_talentWipeTrainer != 0) ImGui.TextUnformatted($"  Unlearn cost: {MoneyText(_talentWipeCost)}");
        foreach (TalentTabInfo tab in tabs)
        { ImGui.SameLine(); if (ImGui.Button($"{tab.Name}##tab-{tab.Id}")) _talentSelectedTab = tab.Id; }
        ImGui.Separator();
        foreach (TalentInfo talent in _talents.TalentsForTab(_talentSelectedTab))
        {
            int rank = TalentRank(talent); string name = _spellCatalog?.TryGet(talent.RankSpells.FirstOrDefault(), out SpellInfo s) == true ? s.Name : $"Talent {talent.Id}";
            TalentEligible(talent, out string reason);
            ImGui.TextUnformatted($"Tier {talent.Row + 1}, Column {talent.Column + 1}: {name}  {rank}/{talent.RankSpells.Length}");
            ImGui.SameLine(); if (ImGui.SmallButton($"+##talent-{talent.Id}")) SpendTalent(talent.Id);
            if (!reason.Equals("pass", StringComparison.Ordinal)) { ImGui.SameLine(); ImGui.TextDisabled(reason); }
        }
        if (_talentWipeTrainer != 0 && ImGui.Button($"Unlearn talents ({MoneyText(_talentWipeCost)})")) ConfirmTalentWipe();
        ImGui.End();
    }

    private static string MoneyText(uint copper) => $"{copper / 10000}g {(copper / 100) % 100}s {copper % 100}c";
}
