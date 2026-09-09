using MSUIClient.Formats;
using MSUIClient.Net;
using MSUIClient.Engine.UI;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private sealed record ReputationState(byte Flags, int Standing);
    private readonly ReputationState[] _reputation = new ReputationState[64];
    private FactionCatalog? _factionCatalog;
    private int _reputationScroll;
    private int _selectedReputationSlot = -1;
    private bool _reputationDetailOpen;

    private void InitReputation()
    {
        if (_mpq is null) return;
        try
        {
            byte[]? bytes = _mpq.ReadFile(FactionCatalog.MpqPath);
            _factionCatalog = bytes is null ? null : FactionCatalog.Parse(bytes);
        }
        catch (Exception ex) { Console.WriteLine($"[reputation] Faction.dbc failed: {ex.Message}"); }
    }

    private Dictionary<ulong, ReputationState[]> _companionReputations = [];

    private ReputationState[]? ReputationFor(ulong owner)
    {
        if (owner == 0) return null;
        if (owner == LocalPlayerGuid) return _reputation;
        return owner == ControlledGuid && _companionReputations?.TryGetValue(owner, out var states) == true ? states : null;
    }

    private void ResetReputationBodyUi()
    {
        _companionReputations?.Clear();
        _selectedReputationSlot = -1; _reputationDetailOpen = false; _reputationScroll = 0;
        ResetReputationFolds();
    }

    private void ResetReputationSession()
    {
        if (_reputation is not null) Array.Clear(_reputation);
        ResetReputationBodyUi();
    }

    private void ApplyInitialFactions(byte[] body) => ApplyReputationPacket(Op.SMSG_INITIALIZE_FACTIONS, body, LocalPlayerGuid);
    private void ApplyFactionVisible(byte[] body) => ApplyReputationPacket(Op.SMSG_SET_FACTION_VISIBLE, body, LocalPlayerGuid);
    private void ApplyFactionAtWar(byte[] body) => ApplyReputationPacket(Op.SMSG_SET_FACTION_ATWAR, body, LocalPlayerGuid);
    private void ApplyFactionStanding(byte[] body) => ApplyReputationPacket(Op.SMSG_SET_FACTION_STANDING, body, LocalPlayerGuid);

    private void ApplyReputationPacket(Op opcode, byte[] body, ulong owner)
    {
        if (owner == 0 || owner != LocalPlayerGuid && owner != ControlledGuid) return;
        var r = new PacketReader(body);
        ReputationState[]? states = ReputationFor(owner);
        if (opcode == Op.SMSG_INITIALIZE_FACTIONS)
        {
            uint count = r.ReadU32();
            if (count > 64 || r.Remaining != count * 5)
                throw new InvalidDataException($"invalid initial faction payload count={count} bytes={body.Length}");
            var snapshot = new ReputationState[64];
            for (int i=0; i<count; i++) snapshot[i] = new(r.ReadU8(),r.ReadI32());
            if (owner == LocalPlayerGuid) { Array.Copy(snapshot,_reputation,64); states = _reputation; }
            else { (_companionReputations ??= [])[owner] = snapshot; states = snapshot; }
        }
        else
        {
            // A companion delta is useful only after its authoritative grant snapshot.
            if (states is null) return;
            if (opcode == Op.SMSG_SET_FACTION_ATWAR)
            {
                var change = ReputationPackets.ParseAtWar(body);
                var previous = states[change.Index] ?? new ReputationState(0,0);
                states[change.Index] = previous with { Flags = ReputationFrameUiLaw.WithAtWar(previous.Flags,change.AtWar) };
            }
            else if (opcode == Op.SMSG_SET_FACTION_VISIBLE)
            {
                if (body.Length != 4) throw new InvalidDataException("invalid faction visible payload");
                uint index = r.ReadU32();
                if (index >= 64) throw new InvalidDataException("invalid faction visible index");
                var previous = states[index] ?? new ReputationState(0,0);
                states[index] = previous with { Flags = (byte)(previous.Flags | 1) };
            }
            else if (opcode == Op.SMSG_SET_FACTION_STANDING)
            {
                uint count = r.ReadU32();
                if (count > 64 || r.Remaining != count * 8)
                    throw new InvalidDataException($"invalid faction standing payload count={count} bytes={body.Length}");
                var updates = new (uint Index,int Standing)[count];
                for (int i=0; i<updates.Length; i++)
                {
                    updates[i] = (r.ReadU32(),r.ReadI32());
                    if (updates[i].Index >= 64) throw new InvalidDataException("invalid faction standing index");
                }
                foreach (var update in updates)
                    states[update.Index] = (states[update.Index] ?? new ReputationState(0,0)) with { Standing = update.Standing };
            }
            else return;
        }
        if (owner != ControlledGuid) return;
        ResetReputationFolds();
        if (_selectedReputationSlot >= 0 &&
            (states[_selectedReputationSlot] is not { } selected || !ReputationFrameUiLaw.IsVisible(selected.Flags)))
        { _selectedReputationSlot = -1; _reputationDetailOpen = false; }
    }

    // Colors are FACTION_BAR_COLORS (ReputationFrame.lua:3-12), ABGR-packed:
    // hostile (0.8,0.3,0.22)=0xff384ccc, unfriendly (0.75,0.27,0)=0xff0045bf,
    // neutral (0.9,0.7,0)=0xff00b2e6, friendly..exalted (0,0.6,0.1)=0xff1a9900.
    private static (string Name, int Floor, int Ceiling, uint Color) ReputationRank(int standing) => standing switch
    {
        < -6000 => ("Hated", -42000, -6000, 0xff384ccc),
        < -3000 => ("Hostile", -6000, -3000, 0xff384ccc),
        < 0 => ("Unfriendly", -3000, 0, 0xff0045bf),
        < 3000 => ("Neutral", 0, 3000, 0xff00b2e6),
        < 9000 => ("Friendly", 3000, 9000, 0xff1a9900),
        < 21000 => ("Honored", 9000, 21000, 0xff1a9900),
        < 42000 => ("Revered", 21000, 42000, 0xff1a9900),
        _ => ("Exalted", 42000, 43000, 0xff1a9900),
    };

    private static byte ReputationRankIndex(int standing) => standing switch
    {
        < -6000 => 0,
        < -3000 => 1,
        < 0 => 2,
        < 3000 => 3,
        < 9000 => 4,
        < 21000 => 5,
        < 42000 => 6,
        _ => 7,
    };

    private bool TryCurrentReputationRank(ulong owner, uint factionId, byte race, byte playerClass, out byte rank)
    {
        rank = 0;
        if (_factionCatalog?.TryGetById(factionId, out FactionInfo info) != true ||
            info.ReputationIndex is < 0 or >= 64 || ReputationFor(owner)?[info.ReputationIndex] is not { } state)
            return false;
        int standing = info.BaseStanding(race,playerClass) + state.Standing;
        rank = ReputationRankIndex(standing);
        return true;
    }

    private void SelectReputationDetail(int slot)
    {
        if (slot is < 0 or >= 64) return;
        if (_reputationDetailOpen && _selectedReputationSlot == slot)
        {
            _reputationDetailOpen = false;
            return;
        }
        _selectedReputationSlot = slot;
        _reputationDetailOpen = true;
    }

    private void SetSelectedFactionAtWar(bool atWar, int totalStanding)
    {
        int slot = _selectedReputationSlot;
        if (_net is null || slot is < 0 or >= 64) return;
        if (ReputationFor(ControlledGuid)?[slot] is not { } state) return;
        if (!ReputationFrameUiLaw.CanToggleAtWar(state.Flags, totalStanding)) return;
        if (RefuseTacticalFreezeLiveCommand("changing faction combat hostility")) return;
        if (!CanAuthorControlledOrSelf || !_entities.TryGet(ControlledGuid,out WorldEntity actor) || actor.InCombat ||
            !_net.SetFactionAtWar((uint)slot, atWar)) return;
        ReputationFor(ControlledGuid)![slot] = state with { Flags = ReputationFrameUiLaw.WithAtWar(state.Flags, atWar) };
    }

    private void SetSelectedFactionInactive(bool inactive)
    {
        int slot = _selectedReputationSlot;
        if (_net is null || slot is < 0 or >= 64) return;
        if (ReputationFor(ControlledGuid)?[slot] is not { } state) return;
        if (!CanAuthorControlledOrSelf || !_net.SetFactionInactive((uint)slot, inactive)) return;
        ReputationFor(ControlledGuid)![slot] = state with { Flags = ReputationFrameUiLaw.WithInactive(state.Flags, inactive) };
        _reputationDetailOpen = false;
        _reputationScroll = 0;
    }

    private void SetSelectedFactionWatched(bool watched)
    {
        if (!CanAuthorControlledOrSelf || _net is null || _selectedReputationSlot is < 0 or >= 64 ||
            ReputationFor(ControlledGuid)?[_selectedReputationSlot] is null) return;
        _net.SetWatchedFaction(watched ? _selectedReputationSlot : ReputationFrameUiLaw.WatchedNone);
    }

    private void ResetReputationFolds()
    {
        _collapsedReputationHeaders?.Clear();
        _collapsedReputationHeaders?.Add(ReputationFrameUiLaw.InactiveHeaderKey);
    }
}
