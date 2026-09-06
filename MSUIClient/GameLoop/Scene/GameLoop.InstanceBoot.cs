using MSUIClient.Engine.UI;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private readonly InstanceBootState _instanceBoot = new();

    private void ApplyRaidGroupOnly(byte[] body, ulong owner)
    {
        RaidGroupOnlyPacket packet = InstanceBootState.Parse(body);
        if (owner == 0) return;
        bool wasCounting = _instanceBoot.Apply(owner, packet, NowSeconds());
        if (owner == ControlledGuid)
        {
            ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.HideByType(_staticPopupSlots, InstanceBootUiLaw.PopupType));
            if (packet.TimerMs == 0 && !wasCounting)
                ShowUiError(packet.Error == 2
                    ? InventoryGlobalString("ERR_RAID_GROUP_FULL", "The instance is full")
                    : InventoryGlobalString("ERR_RAID_GROUP_ONLY", "You must be in a raid group to enter this instance"));
        }
        else if (owner == LocalPlayerGuid && packet.TimerMs != 0)
            AddChatMessage($"[{_net?.PlayerName}] {InstanceBootText(owner, packet.TimerMs / 1000.0)}");
        UpdateInstanceBootPopup();
    }

    private void UpdateInstanceBootPopup()
    {
        var visible = DuelFrameUiLaw.Visible(_staticPopupSlots, InstanceBootUiLaw.PopupType);
        double remaining = _instanceBoot.Remaining(ControlledGuid, NowSeconds());
        string ownerToken = ControlledGuid.ToString("X16");
        if (visible is { } old && (remaining <= 0 || old.Instance.DataToken != ownerToken))
        {
            ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.HideByType(_staticPopupSlots, InstanceBootUiLaw.PopupType));
            visible = null;
        }
        if (remaining > 0 && visible is null)
            ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Show(_staticPopupSlots,
                InstanceBootUiLaw.Definition(remaining), playerDeadOrGhost: false, dataToken: ownerToken));
    }

    private string InstanceBootText(ulong owner, double seconds)
    {
        string home = owner == LocalPlayerGuid ? _areas?.AreaName(_bindPointAreaId) ?? "" : "";
        if (home.Length == 0) home = "your home";
        var time = InstanceBootUiLaw.TimeUnit(seconds);
        string key = (time.Minutes ? "MINUTES" : "SECONDS") + (time.Count == 1 ? "" : "_P1");
        string unit = InventoryGlobalString(key, time.Minutes
            ? time.Count == 1 ? "Minute" : "Minutes"
            : time.Count == 1 ? "Second" : "Seconds");
        return InstanceBootUiLaw.Text(InventoryGlobalString("INSTANCE_BOOT_TIMER", InstanceBootUiLaw.FallbackText),
            home, time.Count, unit);
    }
}
