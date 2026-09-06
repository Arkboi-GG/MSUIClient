namespace MSUIClient.Net;

public sealed partial class NetworkClient
{
    private ulong _renameGuid;
    private string _renameName = "";
    private bool _renameInFlight;
    private CharacterRenameResult? _renameResult;

    private void ResetCharacterRename()
    {
        lock (_createLock)
        {
            _renameGuid = 0;
            _renameName = "";
            _renameInFlight = false;
            _renameResult = null;
        }
    }

    public bool RenameCharacter(ulong guid, string name)
    {
        lock (_createLock)
        {
            if (!AtCharacterSelect || _renameInFlight ||
                !CharacterRenamePackets.ValidRequestName(name) ||
                Characters.FirstOrDefault(c => c.Guid == guid)?.RequiresRename != true) return false;
            _renameGuid = guid;
            _renameName = name;
            _renameResult = null;
            _renameInFlight = true;
            _parkReq = ParkReq.Rename;
            _pick.Set();
            return true;
        }
    }

    public bool TryTakeRenameResult(out CharacterRenameResult result)
    {
        lock (_createLock)
        {
            result = _renameResult ?? default;
            if (_renameResult is null) return false;
            _renameResult = null;
            return true;
        }
    }

    private void ServiceCharacterRename(ref List<Character> chars)
    {
        ulong guid; string name;
        lock (_createLock) { guid = _renameGuid; name = _renameName; }
        try
        {
            CharacterRenameResult result = _session!.RenameCharacter(guid, name);
            if (result.Succeeded)
            {
                chars = _session.CharEnum();
                Characters = chars; // Publish authoritative name and flags before the result.
            }
            lock (_createLock) _renameResult = result;
        }
        finally { lock (_createLock) _renameInFlight = false; }
    }
}
