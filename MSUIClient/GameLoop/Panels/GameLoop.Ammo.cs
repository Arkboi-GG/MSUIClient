namespace MSUIClient;

public sealed partial class GameLoop
{
    private bool SelectAmmo(uint entry)
    {
        if (!CanAuthorControlledOrSelf || _net is null) return false;
        // This Core's SET_AMMO still uses _player, unlike its equip/use handlers.
        // A companion's entry must never select/remove ammunition on the parked main.
        if (ControlledGuid != LocalPlayerGuid)
        {
            ShowUiError("Changing companion ammunition is not supported by this server yet.");
            return false;
        }
        if (RefuseTacticalFreezeLiveCommand("changing ammunition")) return false;
        return _net.SetAmmo(entry);
    }
}
