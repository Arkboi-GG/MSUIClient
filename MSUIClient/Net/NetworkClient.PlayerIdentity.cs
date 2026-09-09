namespace MSUIClient.Net;

public sealed partial class NetworkClient
{
    public bool TryNameQuery(ulong guid) => guid != 0 && InWorld(session => session.NameQuery(guid));

    public void RefreshPlayerIdentity(PlayerNameQueryResponse response)
    {
        if (response.Guid == 0 || string.IsNullOrWhiteSpace(response.Name)) return;
        if (response.Guid == PlayerGuid)
        {
            PlayerName = response.Name;
            if (Player is { } player) Update(player);
        }
        foreach (Character character in Characters)
            if (character.Guid == response.Guid) Update(character);

        void Update(Character character)
        {
            character.Name = response.Name;
            character.Race = response.Traits.Race;
            character.Class = response.Traits.Class;
            character.Gender = response.Traits.Gender;
        }
    }
}
