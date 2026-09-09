using System.Numerics;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private readonly BattlefieldQueueState _battlefieldQueues = new();
    private ulong _battlefieldStatusRequestedOwner;
    private BattlefieldListPacket? _battlefieldList;
    private ulong _battlefieldListOwner;
    private int _battlefieldListOriginMap;
    private Vector3 _battlefieldPortalPosition;
    private int _battlefieldSelected, _battlefieldScroll;
    private bool _battlefieldQueueMenu;
    private readonly Dictionary<uint, double> _battlefieldPendingActions = [];
    private bool CanAuthorBattlefield => _net is { IsInWorld: true } && LocalPlayerGuid != 0 && ControlledGuid == LocalPlayerGuid;

    private string BattlefieldName(uint map) => _maps?.Get((int)map)?.Name ?? $"Battleground {map}";
    private static string BattlefieldInviteType(int slot) => $"CONFIRM_BATTLEFIELD_ENTRY_{slot}";
    private static bool IsBattlefieldInvite(string type) => type.StartsWith("CONFIRM_BATTLEFIELD_ENTRY_", StringComparison.Ordinal);

    private void CloseBattlefieldList()
    {
        _battlefieldList = null; _battlefieldListOwner = 0;
        _battlefieldSelected = _battlefieldScroll = 0;
    }

    private void ResetBattlefieldBodyUi()
    {
        ResetBattlefieldScores();
        ResetBattlefieldPositions();
        ResetAreaSpiritHealer();
        CloseBattlefieldList(); _battlefieldQueueMenu = false; _battlefieldStatusRequestedOwner = 0;
        for (int slot = 0; slot < 3; slot++)
            ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.HideByType(_staticPopupSlots, BattlefieldInviteType(slot)));
    }

    private void ResetBattlefieldSession()
    {
        ResetBattlefieldBodyUi(); _battlefieldQueues?.Clear(); _battlefieldPendingActions?.Clear();
    }

    private void UpdateBattlefields()
    {
        if (_net?.State == NetState.CharacterSelect)
        {
            ResetBattlefieldSession(); return;
        }
        if (!CanAuthorBattlefield) { _battlefieldStatusRequestedOwner = 0; return; }
        if (_battlefieldStatusRequestedOwner != LocalPlayerGuid && _net?.RequestBattlefieldStatus() == true)
            _battlefieldStatusRequestedOwner = LocalPlayerGuid;
        if (_battlefieldList is not null && !BattlefieldListContextCurrent()) CloseBattlefieldList();
        for (int slot = 0; slot < 3; slot++)
            if (_battlefieldQueues[slot] is not { } entry || !entry.CanEnter(NowSeconds()))
                ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.HideByType(_staticPopupSlots, BattlefieldInviteType(slot)));
    }

    private bool BattlefieldListContextCurrent()
    {
        if (!CanAuthorBattlefield || _battlefieldList is not { } list || _battlefieldListOwner != ControlledGuid ||
            _config.Start.Map != _battlefieldListOriginMap || !TryGetInteractionBodyPose(out WorldBodyPose actor)) return false;
        if (list.Source == ControlledGuid)
            return Vector3.DistanceSquared(actor.Position, _battlefieldPortalPosition) <= 50 * 50;
        return _entities.TryGet(list.Source, out WorldEntity npc) && npc.IsCreature && !npc.IsDead &&
            (npc.NpcFlags & WorldCursorUiLaw.Battlemaster) != 0 &&
            NpcSessionUiLaw.InRange(Vector3.DistanceSquared(actor.Position, npc.Position));
    }

    private void ApplyBattlefieldList(byte[] body)
    {
        BattlefieldListPacket list = BattlefieldListPacket.Parse(body);
        if (!CanAuthorBattlefield || !TryGetInteractionBodyPose(out WorldBodyPose actor)) return;
        // Core supplies the player GUID at a portal. An NPC reply must belong to
        // the conversation still in range; an old main reply cannot open on a bot.
        if (list.Source != ControlledGuid && (list.Source != _gossipMenu?.SourceGuid || _gossipOwnerGuid != ControlledGuid)) return;
        _battlefieldList = list; _battlefieldListOwner = ControlledGuid; _battlefieldPortalPosition = actor.Position;
        _battlefieldListOriginMap = _config.Start.Map;
        _battlefieldSelected = _battlefieldScroll = 0;
        if (!BattlefieldListContextCurrent()) { CloseBattlefieldList(); return; }
        ResetGossip();
    }

    private bool JoinSelectedBattlefield(bool asGroup)
    {
        if (!BattlefieldListContextCurrent() || _battlefieldList is not { } list ||
            _battlefieldSelected < 0 || _battlefieldSelected > list.Instances.Count ||
            (asGroup && (list.Map == 30 || _partyMembers.Count == 0 || _partyLeaderGuid != ControlledGuid)) ||
            RefuseTacticalFreezeLiveCommand("joining a battleground") ||
            (list.Source != ControlledGuid && RefuseTacticalFrozenActor(list.Source, "join its battleground"))) return false;
        // The server owns eligibility, deserter status, group-member preflight and
        // queue capacity. Never infer enrollment or teleport from a successful send.
        uint instance = _battlefieldSelected == 0 ? 0 : list.Instances[_battlefieldSelected - 1];
        if (_net?.JoinBattlefield(list.Source, list.Map, instance, asGroup) != true) return false;
        CloseBattlefieldList(); return true;
    }

    private void ApplyBattlefieldStatus(byte[] body)
    {
        BattlefieldStatusPacket packet = BattlefieldStatusPacket.Parse(body);
        uint oldMap = _battlefieldQueues[(int)packet.Slot]?.Packet.Map ?? 0;
        _battlefieldPendingActions.Remove(oldMap); _battlefieldPendingActions.Remove(packet.Map);
        // These direct packets always describe the main. Preserve them while it is
        // parked, but expose controls only while that body is actually driven.
        _battlefieldQueues.Apply(packet, NowSeconds());
        string type = BattlefieldInviteType((int)packet.Slot);
        ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.HideByType(_staticPopupSlots, type));
        if (CanAuthorBattlefield && _battlefieldQueues[(int)packet.Slot] is { } entry && entry.CanEnter(NowSeconds()))
            ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Show(_staticPopupSlots,
                new(type, WhileDead: true, HideOnEscape: true, HasAccept: true), false,
                dataToken: packet.Slot.ToString()));
    }

    private bool SubmitBattlefieldPort(int slot, bool enter)
    {
        BattlefieldQueueState.Entry? entry = _battlefieldQueues[slot];
        if (!CanAuthorBattlefield || entry is null ||
            (enter ? !entry.CanEnter(NowSeconds()) : entry.Packet.Status is not (BattlefieldStatus.Queued or BattlefieldStatus.Invited)) ||
            (_battlefieldPendingActions.TryGetValue(entry.Packet.Map, out double deadline) && NowSeconds() < deadline) ||
            RefuseTacticalFreezeLiveCommand(enter ? "entering a battleground" : "leaving a battleground queue")) return false;
        if (_net?.BattlefieldPort(entry.Packet.Map, enter) != true) return false;
        _battlefieldPendingActions[entry.Packet.Map] = NowSeconds() + 10;
        return true;
    }

    private string BattlefieldInviteText(string type)
    {
        for (int slot = 0; slot < 3; slot++)
            if (type == BattlefieldInviteType(slot) && _battlefieldQueues[slot] is { } entry)
                return $"You are eligible to enter {BattlefieldName(entry.Packet.Map)}.\nTime remaining: {Math.Ceiling(entry.RemainingMilliseconds(NowSeconds()) / 1000):0} seconds.";
        return "This battleground invitation has expired.";
    }

    private void ApplyBattlefieldPopupEffect(StaticPopupCoordinatorLaw.Effect effect)
    {
        if (effect.Kind != StaticPopupCoordinatorLaw.EffectKind.Accept) return;
        for (int slot = 0; slot < 3; slot++)
            if (effect.Type == BattlefieldInviteType(slot)) { SubmitBattlefieldPort(slot, true); break; }
    }

    private void ApplyBattlefieldJoinResult(byte[] body)
    {
        if (body.Length != 4) throw new InvalidDataException("Invalid battlefield group result");
        int result = unchecked((int)new PacketReader(body).ReadU32());
        if (!CanAuthorBattlefield) return;
        if (result < 0) ShowUiError(result == -2 ? "You cannot join a battleground while affected by Deserter." : "Your group could not join the battleground.");
        // Positive results name a map; the separate per-slot status is authoritative.
    }
}
