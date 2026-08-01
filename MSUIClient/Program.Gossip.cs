using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private const uint NpcQuestGiver = 0x0000_0002;
    private const uint NpcVendor = 0x0000_0004;
    private const uint NpcFlightMaster = 0x0000_0008;
    private const uint NpcTrainer = 0x0000_0010;
    private const uint NpcInnkeeper = 0x0000_0080;
    private const uint NpcBanker = 0x0000_0100;
    private const uint NpcAuctioneer = 0x0000_1000;
    private const uint GossipNpcFlags = NpcQuestGiver | NpcVendor | NpcFlightMaster |
        NpcTrainer | NpcInnkeeper | NpcBanker | NpcAuctioneer;
    private const float GossipInteractDistance = 6f;

    private GossipMenu? _gossipMenu;
    private NpcText? _gossipText;
    private uint _gossipSourceFlags;

    private void ResetGossip()
    {
        _gossipMenu = null;
        _gossipText = null;
        _gossipSourceFlags = 0;
    }

    private bool RequestGossip(ulong guid)
    {
        string outcome;
        string detail;
        WorldEntity? target = null;
        float distance = float.PositiveInfinity;
        if (_net is not { IsInWorld: true } || _controller is null)
        {
            outcome = "REFUSED_NOT_IN_WORLD";
            detail = "inWorld=false";
        }
        else if (!_entities.TryGet(guid, out target) || !target.IsCreature)
        {
            outcome = "REFUSED_NOT_CREATURE";
            detail = "descriptorPresent=false";
        }
        else if (target.IsDead)
        {
            outcome = "REFUSED_DEAD";
            detail = $"health={target.Fields.Health}/{target.Fields.MaxHealth}";
        }
        else if ((target.NpcFlags & GossipNpcFlags) == 0)
        {
            outcome = "REFUSED_NO_SUPPORTED_NPC_FLAG";
            detail = $"npcFlags=0x{target.NpcFlags:X8}";
        }
        else if ((distance = Vector3.Distance(_controller.Position, target.Position)) > GossipInteractDistance)
        {
            outcome = "REFUSED_RANGE";
            detail = $"distance={distance:R};limit={GossipInteractDistance:R};npcFlags=0x{target.NpcFlags:X8}";
        }
        else
        {
            bool sent = _net.GossipHello(guid);
            outcome = sent ? "SENT" : "SEND_FAILED";
            detail = $"distance={distance:R};npcFlags=0x{target.NpcFlags:X8};route={ClassifyGossipRoute(target.NpcFlags, "")}";
            if (sent)
            {
                _gossipMenu = null;
                _gossipText = null;
                _gossipSourceFlags = target.NpcFlags;
            }
        }
        EmitInterface("gossip", "hello", outcome, guid, detail);
        return outcome == "SENT";
    }

    private void ApplyGossipMenu(byte[] body)
    {
        GossipMenu menu = GossipPackets.ParseMenu(body);
        _gossipMenu = menu;
        _gossipText = null;
        if (_entities.TryGet(menu.SourceGuid, out WorldEntity source)) _gossipSourceFlags = source.NpcFlags;
        EmitInterface("gossip", "menu", "DECODED", menu.SourceGuid,
            $"textId={menu.TextId};options={menu.Options.Count};quests={menu.Quests.Count};npcFlags=0x{_gossipSourceFlags:X8}");
        bool sent = _net?.NpcTextQuery(menu.TextId, menu.SourceGuid) == true;
        EmitInterface("gossip", "text-query", sent ? "SENT" : "SEND_FAILED", menu.SourceGuid,
            $"textId={menu.TextId}");
    }

    private void ApplyNpcText(byte[] body)
    {
        NpcText text = GossipPackets.ParseText(body);
        if (_gossipMenu is null || text.TextId != _gossipMenu.TextId)
        {
            EmitInterface("gossip", "text", "IGNORED_STALE", 0,
                $"textId={text.TextId};openTextId={_gossipMenu?.TextId ?? 0}");
            return;
        }
        _gossipText = text;
        EmitInterface("gossip", "text", "DECODED", _gossipMenu.SourceGuid,
            $"textId={text.TextId};maleChars={text.MaleText.Length};femaleChars={text.FemaleText.Length}");
    }

    private bool SelectGossipOption(int visualIndex)
    {
        if (_gossipMenu is null || visualIndex < 0 || visualIndex >= _gossipMenu.Options.Count)
        {
            EmitInterface("gossip", "select", "REFUSED_NO_OPTION", _gossipMenu?.SourceGuid ?? 0,
                $"visualIndex={visualIndex};count={_gossipMenu?.Options.Count ?? 0}");
            return false;
        }
        GossipOption option = _gossipMenu.Options[visualIndex];
        if (option.Coded)
        {
            EmitInterface("gossip", "select", "REFUSED_CODE_REQUIRED", _gossipMenu.SourceGuid,
                $"visualIndex={visualIndex};listId={option.ListId}");
            return false;
        }
        string route = ClassifyGossipRoute(_gossipSourceFlags, option.Text);
        bool sent = _net?.GossipSelect(_gossipMenu.SourceGuid, option.ListId) == true;
        EmitInterface("gossip", "select", sent ? "SENT" : "SEND_FAILED", _gossipMenu.SourceGuid,
            $"visualIndex={visualIndex};listId={option.ListId};icon={option.Icon};route={route};text={SanitizeEvidence(option.Text)}");
        return sent;
    }

    private static string ClassifyGossipRoute(uint flags, string optionText)
    {
        if ((flags & NpcVendor) != 0) return "vendor";
        if ((flags & NpcTrainer) != 0) return "trainer";
        if ((flags & NpcFlightMaster) != 0) return "flightmaster";
        if ((flags & NpcInnkeeper) != 0) return "innkeeper";
        if ((flags & NpcBanker) != 0) return "banker";
        if ((flags & NpcAuctioneer) != 0) return "auctioneer";
        if ((flags & NpcQuestGiver) != 0) return "quest";
        return optionText.Length == 0 ? "unknown" : "gossip";
    }

    private static string SanitizeEvidence(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ').Replace(';', ',').Trim();

    private void EmitInterface(string family, string step, string outcome, ulong guid, string detail)
    {
        var verdict = new InterfaceVerdict(NowSeconds(), family, step, outcome, guid, detail);
        _verdicts.Add(verdict);
        if (_config.DevTools) Console.WriteLine($"[verdict:interface] {verdict.ToLine()}");
    }

    private void DrawGossipFrame()
    {
        if (_gossipMenu is null) return;
        float s = GameplayUiScale();
        Vector2 size = new Vector2(380f, 390f) * s;
        Vector2 p = new(18f * s, MathF.Max(12f, (ImGui.GetIO().DisplaySize.Y - size.Y) * .48f));
        ImGui.SetNextWindowPos(p, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings;
        if (!ImGui.Begin("Gossip##gossip-frame", flags)) { ImGui.End(); return; }

        string sourceName = _entities.TryGet(_gossipMenu.SourceGuid, out WorldEntity source)
            ? source.IsPlayer
                ? _playerNames.GetValueOrDefault(source.Guid, "Player")
                : _creatureNames.GetValueOrDefault(source.Entry, $"Creature {source.Entry}")
            : $"0x{_gossipMenu.SourceGuid:X16}";
        ImGui.TextColored(new Vector4(1f, .82f, 0f, 1f), sourceName);
        ImGui.Separator();
        string greeting = _gossipText?.MaleText ?? $"Loading text {_gossipMenu.TextId}...";
        ImGui.TextWrapped(greeting.Replace("$N", _net?.PlayerName ?? "traveler", StringComparison.OrdinalIgnoreCase));
        ImGui.Spacing();

        for (int i = 0; i < _gossipMenu.Options.Count; i++)
        {
            GossipOption option = _gossipMenu.Options[i];
            if (ImGui.Selectable($"> {option.Text}##gossip-option-{i}")) SelectGossipOption(i);
        }
        foreach (GossipQuest quest in _gossipMenu.Quests)
            if (ImGui.Selectable($"[{quest.Level}] {quest.Title}##gossip-quest-{quest.QuestId}"))
                RequestQuestDetails(_gossipMenu.SourceGuid, quest.QuestId);

        ImGui.SetCursorPosY(MathF.Max(ImGui.GetCursorPosY(), size.Y / s - 48f));
        if (ImGui.Button("Close##gossip")) ResetGossip();
        if (_config.DevTools)
        {
            ImGui.SameLine();
            if (ImGui.Button("Copy evidence##gossip"))
            {
                string text = string.Join(Environment.NewLine, _verdicts.Snapshot("interface")
                    .OfType<InterfaceVerdict>().Where(v => v.Family == "gossip")
                    .Select(v => $"[verdict:interface] {v.ToLine()}"));
                CopyVerdictText(text);
            }
        }
        ImGui.End();
    }
}
