using MSUIClient.Engine.UI;
using MSUIClient.Formats;

namespace MSUIClient;

/// <summary>Zone-channel auto-join (General / Trade / LocalDefense) — see <see cref="ZoneChannelLaw"/>.</summary>
public sealed partial class GameLoop
{
    private ChatChannelsCatalog? _chatChannelCatalog;
    private bool _chatChannelCatalogLoaded;
    /// <summary>The composed names last REQUESTED (not server truth): keying the diff off requests stops a refused join from re-sending every zone tick.</summary>
    private readonly List<string> _zoneChannelsHeld = [];
    private uint _zoneChannelsZone;

    private void ResetZoneChannels()
    {
        _zoneChannelsHeld.Clear();
        _zoneChannelsZone = 0;
    }

    /// <summary>
    /// Re-walk on the SESSION body's zone (the one CMSG_ZONEUPDATE reports): channels belong to
    /// the logged-in character, not to a possessed bot or the observer rig.
    /// </summary>
    private void RefreshZoneChannels(uint reportedZoneId)
    {
        if (_net is not { IsInWorld: true } || reportedZoneId == 0) return;
        if (!_chatChannelCatalogLoaded && _mpq is not null)
        {
            _chatChannelCatalogLoaded = true;
            try { _chatChannelCatalog = ChatChannelsCatalog.Load(_mpq); }
            catch (Exception e) { Console.WriteLine($"[chat] ChatChannels.dbc load failed: {e.Message}"); }
        }
        if (_chatChannelCatalog is null || _areas is null) return;
        uint zoneId = _areas.ParentZoneId(reportedZoneId);
        if (zoneId == 0) zoneId = reportedZoneId;
        if (zoneId == _zoneChannelsZone) return;
        _zoneChannelsZone = zoneId;
        string zoneName = _areas.ZoneName(zoneId);
        bool inCity = ((_areas.Flags(zoneId) ?? 0) & ZoneChannelLaw.AreaFlagTradeChannel) != 0;
        string? cityWord = _areas.FirstIdWithFlag(ZoneChannelLaw.AreaFlagCityNameRow) is uint cityRow
            ? _areas.AreaName(cityRow) : null;
        List<string> wanted = ZoneChannelLaw.Wanted(_chatChannelCatalog, zoneName, inCity, cityWord);
        (List<string> leave, List<string> join) = ZoneChannelLaw.Diff(_zoneChannelsHeld, wanted);
        foreach (string name in leave) _net.LeaveChannel(name);
        foreach (string name in join) _net.JoinChannel(name);
        _zoneChannelsHeld.Clear();
        _zoneChannelsHeld.AddRange(wanted);
        EmitInterface("chat", "zone-channels", "WALKED", _net.PlayerGuid,
            $"zone={zoneId};name={SanitizeEvidence(zoneName)};city={inCity};leave={leave.Count};join={join.Count};held={string.Join('|', wanted)}");
    }
}
