using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private void RefreshVisiblePlayerIdentity(PlayerNameQueryResponse response)
    {
        _net?.RefreshPlayerIdentity(response);
        if (response.Guid == 0 || string.IsNullOrWhiteSpace(response.Name)) return;
        for (int i = 0; i < _partyMembers.Count; i++)
            if (_partyMembers[i].Guid == response.Guid)
                _partyMembers[i] = _partyMembers[i] with { Name = response.Name };
        for (int i = 0; i < _guildMembers.Count; i++)
            if (_guildMembers[i].Guid == response.Guid)
                _guildMembers[i] = _guildMembers[i] with { Name = response.Name, Class = response.Traits.Class };
        for (int i = 0; i < _companionRows.Length; i++)
            if (_companionRows[i].Guid == response.Guid)
                _companionRows[i] = _companionRows[i] with
                {
                    Name = response.Name, Race = response.Traits.Race,
                    Class = response.Traits.Class, Gender = response.Traits.Gender,
                };
    }

    private void ApplyInvalidatePlayer(byte[] body)
    {
        ulong guid = ObjectNoticePackets.ParseGuid(body, Op.SMSG_INVALIDATE_PLAYER);
        if (guid == 0) return;
        _playerNames.Remove(guid);
        _playerTraits.Remove(guid);
        _queriedPlayerNames.Remove(guid);
        _chatNameQueried.Remove(guid);
        if (_net?.TryNameQuery(guid) == true)
        {
            _queriedPlayerNames.Add(guid);
            _chatNameQueried.Add(guid);
        }
    }
}
