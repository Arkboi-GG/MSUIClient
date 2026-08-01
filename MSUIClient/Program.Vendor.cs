using System.Numerics;
using ImGuiNET;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private VendorInventory? _vendor;

    private bool RequestVendor(ulong guid)
    {
        string outcome="REFUSED"; string detail="descriptorMissing";
        if(_net is { IsInWorld:true }&&_controller is not null&&_entities.TryGet(guid,out WorldEntity npc)&&
           npc.IsCreature&&!npc.IsDead&&(npc.NpcFlags&NpcVendor)!=0)
        {
            float distance=Vector3.Distance(_controller.Position,npc.Position);
            if(distance<=GossipInteractDistance)
            {
                bool sent=_net.ListInventory(guid); outcome=sent?"SENT":"SEND_FAILED";
                detail=$"distance={distance:R};npcFlags=0x{npc.NpcFlags:X8}";
            }
            else { outcome="REFUSED_RANGE"; detail=$"distance={distance:R};limit={GossipInteractDistance:R}"; }
        }
        EmitInterface("vendor","list",outcome,guid,detail); return outcome=="SENT";
    }

    private void ApplyVendorList(byte[] body)
    {
        _vendor=VendorPackets.ParseList(body);
        EmitInterface("vendor","list",_vendor.Error==0?"DECODED":"ERROR",_vendor.VendorGuid,
            $"items={_vendor.Items.Count};error={_vendor.Error}");
        if(_items is not null&&_net is not null)
            foreach(var row in _vendor.Items) _items.Require(row.ItemId,_vendor.VendorGuid,_net);
    }

    private void ApplyVendorResult(Op opcode, byte[] body)
    {
        var r=new PacketReader(body); ulong vendor=r.Remaining>=8?r.ReadU64():0;
        uint item=r.Remaining>=4?r.ReadU32():0; byte result=r.Remaining>0?r.ReadU8():(byte)0;
        EmitInterface("vendor",opcode.ToString(),result==0?"ACCEPTED":"ERROR",vendor,
            $"item={item};result={result};bytes={body.Length}");
    }

    private bool BuyVendorEntry(uint entry, byte count)
    {
        if (_vendor is null || !_vendor.Items.Any(x => x.ItemId == entry))
        { EmitInterface("vendor", "buy", "REFUSED-NOT-LISTED", 0, $"item={entry};count={count}"); return false; }
        bool sent = _net?.BuyItem(_vendor.VendorGuid, entry, count) == true;
        EmitInterface("vendor", "buy", sent ? "SENT" : "SEND_FAILED", _vendor.VendorGuid,
            $"item={entry};count={count};body={Convert.ToHexString(WorldSession.BuildBuyItemBody(_vendor.VendorGuid, entry, count))}");
        return sent;
    }

    private void DrawVendorFrame()
    {
        if(_vendor is null) return;
        ImGui.SetNextWindowSize(new Vector2(420,420)*GameplayUiScale(),ImGuiCond.Always);
        if(!ImGui.Begin("Merchant##vendor",ImGuiWindowFlags.NoResize)) { ImGui.End(); return; }
        ImGui.Text($"Items: {_vendor.Items.Count}"); ImGui.Separator();
        foreach(VendorItem row in _vendor.Items)
        {
            string name=_items?.TryGet(row.ItemId,out ItemTemplate? t)==true&&t is not null?t.Name:$"Item {row.ItemId}";
            if(ImGui.Selectable($"{name}  {FormatMoney(row.Price)}##vendor-{row.Slot}"))
            {
                BuyVendorEntry(row.ItemId, 1);
            }
        }
        if(ImGui.Button("Close##vendor")) _vendor=null;
        ImGui.End();
    }
}
