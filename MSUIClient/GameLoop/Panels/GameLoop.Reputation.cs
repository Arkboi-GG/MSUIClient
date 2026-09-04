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

    private void ApplyInitialFactions(byte[] body)
    {
        var r = new PacketReader(body);
        uint count = r.ReadU32();
        if (count > 64 || r.Remaining != count * 5)
            throw new InvalidDataException($"invalid initial faction payload count={count} bytes={body.Length}");
        Array.Clear(_reputation);
        for (int i = 0; i < count; i++) _reputation[i] = new(r.ReadU8(), r.ReadI32());
        ResetReputationFolds();
        if (_selectedReputationSlot >= count ||
            _selectedReputationSlot >= 0 &&
            !ReputationFrameUiLaw.IsVisible(_reputation[_selectedReputationSlot].Flags))
        {
            _selectedReputationSlot = -1;
            _reputationDetailOpen = false;
        }
    }

    private void ApplyFactionVisible(byte[] body)
    {
        var r = new PacketReader(body);
        uint index = r.ReadU32();
        if (index < 64)
        {
            _reputation[index] = _reputation[index] with { Flags = (byte)(_reputation[index].Flags | 1) };
            ResetReputationFolds();
        }
    }

    private void ApplyFactionStanding(byte[] body)
    {
        var r = new PacketReader(body);
        uint count = r.ReadU32();
        if (count > 64 || r.Remaining != count * 8)
            throw new InvalidDataException($"invalid faction standing payload count={count} bytes={body.Length}");
        for (int i = 0; i < count; i++)
        {
            uint index = r.ReadU32(); int standing = r.ReadI32();
            if (index < 64) _reputation[index] = _reputation[index] with { Standing = standing };
        }
        ResetReputationFolds();
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

    private byte CurrentReputationRank(uint factionId, byte race, byte playerClass)
    {
        if (_factionCatalog?.TryGetById(factionId, out FactionInfo info) != true ||
            info.ReputationIndex is < 0 or >= 64)
            return 0;
        ReputationState? state = _reputation[info.ReputationIndex];
        int standing = info.BaseStanding(race, playerClass) + (state?.Standing ?? 0);
        return ReputationRankIndex(standing);
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
        ReputationState state = _reputation[slot];
        if (!ReputationFrameUiLaw.CanToggleAtWar(state.Flags, totalStanding)) return;
        if (RefuseTacticalFreezeLiveCommand("changing faction combat hostility")) return;
        _reputation[slot] = state with
        {
            Flags = ReputationFrameUiLaw.WithAtWar(state.Flags, atWar)
        };
        _net.SetFactionAtWar((uint)slot, atWar);
    }

    private void SetSelectedFactionInactive(bool inactive)
    {
        int slot = _selectedReputationSlot;
        if (_net is null || slot is < 0 or >= 64) return;
        ReputationState state = _reputation[slot];
        _reputation[slot] = state with
        {
            Flags = ReputationFrameUiLaw.WithInactive(state.Flags, inactive)
        };
        _net.SetFactionInactive((uint)slot, inactive);
        _reputationDetailOpen = false;
        _reputationScroll = 0;
    }

    private void SetSelectedFactionWatched(bool watched)
    {
        if (_net is null || _selectedReputationSlot is < 0 or >= 64) return;
        _net.SetWatchedFaction(watched ? _selectedReputationSlot : ReputationFrameUiLaw.WatchedNone);
    }

    private void ResetReputationFolds()
    {
        _collapsedReputationHeaders.Clear();
        _collapsedReputationHeaders.Add(ReputationFrameUiLaw.InactiveHeaderKey);
    }
}
