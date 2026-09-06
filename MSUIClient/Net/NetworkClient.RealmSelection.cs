namespace MSUIClient.Net;

public sealed partial class NetworkClient
{
    private readonly object _realmSelectionLock = new();
    private readonly ManualResetEventSlim _realmPick = new(false);
    private IReadOnlyList<RealmInfo> _realms = Array.Empty<RealmInfo>();
    private int _realmSelectionIndex = -1;
    public IReadOnlyList<RealmInfo> Realms => Volatile.Read(ref _realms);

    private void ResetRealmSelection()
    {
        lock (_realmSelectionLock)
        {
            Volatile.Write(ref _realms, Array.Empty<RealmInfo>());
            _realmSelectionIndex = -1;
            _realmPick.Reset();
        }
    }

    public bool SelectRealm(RealmInfo realm)
    {
        lock (_realmSelectionLock)
        {
            if (State != NetState.RealmSelect || _realmSelectionIndex >= 0 || !realm.CanSelect) return false;
            // The index belongs to this exact immutable published list, not the zero
            // realm-number byte or an untrusted caller-supplied address.
            int index = -1;
            for (int i = 0; i < _realms.Count; i++)
                if (ReferenceEquals(_realms[i], realm)) { index = i; break; }
            if (index < 0) return false;
            _realmSelectionIndex = index;
            _realmPick.Set();
            return true;
        }
    }

    private RealmInfo? WaitForRealm(IReadOnlyList<RealmInfo> realms, bool forceSelection)
    {
        lock (_realmSelectionLock)
        {
            if (!_running) return null;
            _realmSelectionIndex = -1;
            _realmPick.Reset();
            Volatile.Write(ref _realms, Array.AsReadOnly(realms.ToArray()));
            if (!forceSelection)
            {
                if (!string.IsNullOrWhiteSpace(_cfg.RealmName))
                {
                    RealmInfo? configured = _realms.FirstOrDefault(r => r.CanSelect &&
                        string.Equals(r.Name, _cfg.RealmName, StringComparison.OrdinalIgnoreCase));
                    if (configured is not null) return configured;
                }
                else if (_realms.Count == 1 && _realms[0].CanSelect) return _realms[0];
            }
            SetState(NetState.RealmSelect, "choose a realm");
        }
        _realmPick.Wait();
        lock (_realmSelectionLock)
            return _running && _realmSelectionIndex >= 0 ? _realms[_realmSelectionIndex] : null;
    }

    private void WakeRealmSelection()
    {
        lock (_realmSelectionLock) _realmPick.Set();
    }

    public bool RequestRealmSelection()
    {
        lock (_createLock)
        {
            if (State != NetState.CharacterSelect || _parkReq != ParkReq.None || _renameInFlight) return false;
            _parkReq = ParkReq.ChangeRealm;
            SetState(NetState.ConnectingRealm, "refreshing realm list");
            _pick.Set();
            return true;
        }
    }
}
