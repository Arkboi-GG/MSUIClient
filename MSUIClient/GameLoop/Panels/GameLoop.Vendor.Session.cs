using System.Numerics;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private readonly bool[] _vendorOpenedBags = new bool[5];
    private bool _vendorRepairMode;
    private int _vendorHoveredRow = -1;
    private int _vendorRightPressedRow = -1;
    private int _vendorLeftPressedRow = -1;
    private bool _vendorSuppressRepairAllTooltipUntilLeave;

    private bool IsVendorServiceAvailable(ulong guid, out WorldEntity? vendor,
        out float distance)
    {
        vendor = null;
        distance = float.PositiveInfinity;
        if (!TryGetSessionBodyPose(out WorldBodyPose sessionBody) ||
            !_entities.TryGet(guid, out WorldEntity candidate) ||
            !candidate.IsCreature || candidate.IsDead ||
            (candidate.NpcFlags & NpcVendor) == 0)
            return false;
        vendor = candidate;
        Vector3 delta = sessionBody.Position - candidate.Position;
        distance = delta.Length();
        return NpcSessionUiLaw.InRange(delta.LengthSquared());
    }

    private void OpenVendorSession(VendorInventory inventory)
    {
        bool sameSession = _vendor?.VendorGuid == inventory.VendorGuid;
        if (_vendor is not null && !sameSession)
            CloseVendorSession(playSound: true);

        _vendor = inventory;
        if (sameSession) return;

        _vendorPage = 1;
        _vendorTab = 0;
        _vendorRepairMode = false;
        _vendorHoveredRow = -1;
        _vendorRightPressedRow = -1;
        _vendorLeftPressedRow = -1;
        _vendorSuppressRepairAllTooltipUntilLeave = false;
        Array.Clear(_vendorOpenedBags);

        if (_net is not null && _entities.TryGet(_net.PlayerGuid, out WorldEntity player))
        {
            for (int container = 0; container <= 4; container++)
            {
                bool exists = container == 0 ||
                    player.Fields.PlayerInventorySlot(18 + container) != 0;
                if (!exists || IsBagWindowOpen(container)) continue;
                _vendorOpenedBags[container] = SetBagWindowOpen(
                    container, true, playSound: true);
            }
        }
        PlayUiSound("igCharacterInfoOpen", "ui.vendor");
    }

    private bool CloseVendorSession(bool playSound = true)
    {
        if (_vendor is null) return false;
        _vendor = null;

        for (int container = 0; container <= 4; container++)
        {
            if (_vendorOpenedBags[container] && IsBagWindowOpen(container))
                SetBagWindowOpen(container, false, playSound: true);
            _vendorOpenedBags[container] = false;
        }

        _vendorRepairMode = false;
        _vendorHoveredRow = -1;
        _vendorRightPressedRow = -1;
        _vendorLeftPressedRow = -1;
        _vendorSuppressRepairAllTooltipUntilLeave = false;
        if (playSound) PlayUiSound("igCharacterInfoClose", "ui.vendor");
        return true;
    }

    private void ResetVendor()
    {
        _vendor = null;
        _vendorRepairMode = false;
        _vendorHoveredRow = -1;
        _vendorRightPressedRow = -1;
        _vendorLeftPressedRow = -1;
        _vendorSuppressRepairAllTooltipUntilLeave = false;
        for (int container = 0; container <= 4; container++)
        {
            if (_vendorOpenedBags[container] && IsBagWindowOpen(container))
                SetBagWindowOpen(container, false, playSound: false);
            _vendorOpenedBags[container] = false;
        }
    }

    private void UpdateVendorLifecycle()
    {
        if (_vendor is null) return;
        // The established session closes only when its actor is gone or moves beyond service
        // range. Opener-only type/death/service-bit gates must not tear down a live window, and a
        // temporarily unavailable player transform is not evidence of departure.
        if (!TryGetSessionBodyPose(out WorldBodyPose sessionBody)) return;
        bool sourceAvailable = _entities.TryGet(_vendor.VendorGuid, out WorldEntity vendor);
        float distanceSquared = sourceAvailable
            ? Vector3.DistanceSquared(sessionBody.Position, vendor.Position)
            : float.PositiveInfinity;
        if (NpcSessionUiLaw.ShouldClose(true, true, sourceAvailable, distanceSquared))
            CloseVendorSession(playSound: true);
    }
}
