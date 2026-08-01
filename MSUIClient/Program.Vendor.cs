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
        if(_vendor is null||_gameplayArt is null) return;
        float s=GameplayUiScale();Vector2 origin=new(0,8*s),logicalSize=new(384,512);
        ImGui.SetNextWindowPos(origin,ImGuiCond.Always);ImGui.SetNextWindowSize(logicalSize*s,ImGuiCond.Always);ImGui.SetNextWindowBgAlpha(0);
        if(!ImGui.Begin("##vendor",ImGuiWindowFlags.NoDecoration|ImGuiWindowFlags.NoMove|ImGuiWindowFlags.NoSavedSettings|ImGuiWindowFlags.NoBackground|ImGuiWindowFlags.NoNav)){ImGui.End();return;}
        ImDrawListPtr dl=ImGui.GetWindowDrawList();if(_uiParityArmed&&_uiParityPanel=="merchant"){BeginUiParityFrame(origin,s);CollectUiParityDraw("MerchantFrame","Frame",origin,logicalSize*s,"",new("",0,"IMGUI_HOST","ANCHOR:ABSOLUTE","","",0,8));}
        (string Element,string Path,Vector2 Offset,Vector2 Size)[] art=[("MerchantFrame/Texture",@"Interface\MerchantFrame\UI-Merchant-TopLeft",Vector2.Zero,new(256,256)),("MerchantFrame/Texture#2",@"Interface\MerchantFrame\UI-Merchant-TopRight",new(256,0),new(128,256)),("MerchantFrame/Texture#3",@"Interface\MerchantFrame\UI-Merchant-BotLeft",new(0,256),new(256,256)),("MerchantFrame/Texture#4",@"Interface\MerchantFrame\UI-Merchant-BotRight",new(256,256),new(128,256))];
        foreach(var r in art){Vector2 m=origin+r.Offset*s;DrawArt(dl,r.Path,m,r.Size,s);if(_uiParityArmed&&_uiParityPanel=="merchant")CollectUiParityDraw(r.Element,"Texture",m,r.Size*s,"MerchantFrame",new(r.Path,0xffffffff,"IMGUI_IMAGE","TOPLEFT","MerchantFrame","TOPLEFT",r.Offset.X,-r.Offset.Y));}
        ImGui.SetCursorScreenPos(origin+new Vector2(30,75)*s);ImGui.BeginChild("##vendor-items",new Vector2(310,340)*s,false);
        foreach(VendorItem row in _vendor.Items)
        {
            string name=_items?.TryGet(row.ItemId,out ItemTemplate? t)==true&&t is not null?t.Name:$"Item {row.ItemId}";
            if(ImGui.Selectable($"{name}  {FormatMoney(row.Price)}##vendor-{row.Slot}"))
            {
                BuyVendorEntry(row.ItemId, 1);
            }
        }
        ImGui.EndChild();
        Vector2 close=origin+new Vector2(322,8)*s;DrawImageButton(dl,"##vendor-close",close,new Vector2(32)*s,@"Interface\Buttons\UI-Panel-MinimizeButton-Up",@"Interface\Buttons\UI-Panel-MinimizeButton-Down",@"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");if(ImGui.IsItemClicked())_vendor=null;
        if(_uiParityArmed&&_uiParityPanel=="merchant")MarkUiParityFrameComplete();
        ImGui.End();
    }
}
