using System.Numerics;
using ImGuiNET;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private VendorInventory? _vendor;
    private int _vendorPage;
    private int _vendorTab;

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
        _vendor=VendorPackets.ParseList(body); _vendorPage = 0; _vendorTab = 0;
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

    private bool SellToOpenVendor(ulong itemGuid, byte count = 0)
    {
        if (_vendor is null || itemGuid == 0 || _net is null) return false;
        bool sent = _net.SellItem(_vendor.VendorGuid, itemGuid, count);
        EmitInterface("vendor", "sell", sent ? "SENT" : "SEND_FAILED", _vendor.VendorGuid,
            $"itemGuid=0x{itemGuid:X16};count={count}");
        return sent;
    }

    private void DrawVendorFrame()
    {
        if(_vendor is null||_gameplayArt is null) return;
        float s=GameplayUiScale();Vector2 origin=new(0,104*s),logicalSize=new(384,512);
        ImGui.SetNextWindowPos(origin,ImGuiCond.Always);ImGui.SetNextWindowSize(logicalSize*s,ImGuiCond.Always);ImGui.SetNextWindowBgAlpha(0);
        if(!ImGui.Begin("##vendor",ImGuiWindowFlags.NoDecoration|ImGuiWindowFlags.NoMove|ImGuiWindowFlags.NoSavedSettings|ImGuiWindowFlags.NoBackground|ImGuiWindowFlags.NoNav)){ImGui.End();return;}
        ImDrawListPtr dl=ImGui.GetWindowDrawList();if(_uiParityArmed&&_uiParityPanel=="merchant"){BeginUiParityFrame(origin,s);CollectUiParityDraw("MerchantFrame","Frame",origin,logicalSize*s,"",new("",0,"IMGUI_HOST","ANCHOR:ABSOLUTE","","",0,8));}
        (string Element,string Path,Vector2 Offset,Vector2 Size)[] art=[("MerchantFrame/Texture",@"Interface\MerchantFrame\UI-Merchant-TopLeft",Vector2.Zero,new(256,256)),("MerchantFrame/Texture#2",@"Interface\MerchantFrame\UI-Merchant-TopRight",new(256,0),new(128,256)),("MerchantFrame/Texture#3",@"Interface\MerchantFrame\UI-Merchant-BotLeft",new(0,256),new(256,256)),("MerchantFrame/Texture#4",@"Interface\MerchantFrame\UI-Merchant-BotRight",new(256,256),new(128,256))];
        foreach(var r in art){Vector2 m=origin+r.Offset*s;DrawArt(dl,r.Path,m,r.Size,s);if(_uiParityArmed&&_uiParityPanel=="merchant")CollectUiParityDraw(r.Element,"Texture",m,r.Size*s,"MerchantFrame",new(r.Path,0xffffffff,"IMGUI_IMAGE","TOPLEFT","MerchantFrame","TOPLEFT",r.Offset.X,-r.Offset.Y));}
        if (_net is not null && _entities.TryGet(_net.PlayerGuid, out WorldEntity player))
        {
            if (_vendorTab == 0)
            {
                int pages = Math.Max(1, (_vendor.Items.Count + 11) / 12);
                _vendorPage = Math.Clamp(_vendorPage, 0, pages - 1);
                for (int visible = 0; visible < 12; visible++)
                {
                    int index = _vendorPage * 12 + visible;
                    if (index >= _vendor.Items.Count) break;
                    VendorItem row = _vendor.Items[index];
                    ItemTemplate? item = null;
                    if (_items?.TryGet(row.ItemId, out ItemTemplate? found) == true) item = found;
                    int col = visible & 1, listRow = visible / 2;
                    Vector2 cell = origin + new Vector2(24 + col * 165, 80 + listRow * 52) * s;
                    uint slotArt = _gameplayArt.Handle(@"Interface\Buttons\UI-EmptySlot");
                    if (slotArt != 0) dl.AddImage((nint)slotArt, cell - new Vector2(13) * s, cell + new Vector2(51) * s);
                    uint icon = item is null ? 0 : _gameplayArt.Handle(item.IconPath);
                    if (icon != 0) dl.AddImage((nint)icon, cell, cell + new Vector2(37) * s);
                    ImGui.SetCursorScreenPos(cell); ImGui.InvisibleButton($"##vendor-{row.Slot}", new Vector2(153, 44) * s);
                    if (ImGui.IsItemClicked()) BuyVendorEntry(row.ItemId, 1);
                    if (ImGui.IsItemHovered() && item is not null) DrawItemTooltip(item, 1);
                    dl.AddText(ImGui.GetFont(), 10f * s, cell + new Vector2(42, 2) * s, VanillaGold,
                        item?.Name ?? $"Item {row.ItemId}");
                    dl.AddText(ImGui.GetFont(), 9f * s, cell + new Vector2(42, 22) * s, 0xffffffff, FormatMoney(row.Price));
                }
                DrawCenteredText(dl, origin + new Vector2(181, 358) * s, $"Page {_vendorPage + 1} of {pages}", 10f * s, VanillaGold);
                if (TabardArrow(dl, "##vendor-prev", origin + new Vector2(21, 340) * s, false, s) && _vendorPage > 0) _vendorPage--;
                if (TabardArrow(dl, "##vendor-next", origin + new Vector2(308, 340) * s, true, s) && _vendorPage + 1 < pages) _vendorPage++;
            }
            else
            {
                for (int i = 0; i < 12; i++)
                {
                    ulong guid = player.Fields.PlayerBuybackSlot(i);
                    if (guid == 0 || !_entities.TryGet(guid, out WorldEntity instance)) continue;
                    _items?.Require(instance.Entry, guid, _net);
                    ItemTemplate? item = null;
                    if (_items?.TryGet(instance.Entry, out ItemTemplate? found) == true) item = found;
                    int col = i & 1, listRow = i / 2;
                    Vector2 cell = origin + new Vector2(24 + col * 165, 80 + listRow * 52) * s;
                    uint icon = item is null ? 0 : _gameplayArt.Handle(item.IconPath);
                    if (icon != 0) dl.AddImage((nint)icon, cell, cell + new Vector2(37) * s);
                    ImGui.SetCursorScreenPos(cell); ImGui.InvisibleButton($"##buyback-{i}", new Vector2(153, 44) * s);
                    if (ImGui.IsItemClicked()) _net.BuybackItem(_vendor.VendorGuid, (uint)i);
                    dl.AddText(ImGui.GetFont(), 10f * s, cell + new Vector2(42, 2) * s, VanillaGold,
                        item?.Name ?? $"Item {instance.Entry}");
                    dl.AddText(ImGui.GetFont(), 9f * s, cell + new Vector2(42, 22) * s, 0xffffffff,
                        FormatMoney(player.Fields.PlayerBuybackPrice(i)));
                }
            }
        }
        float merchantWidth=VanillaCharacterTabWidth("Merchant",s,0);
        float buybackWidth=VanillaCharacterTabWidth("Buyback",s,0);
        float merchantX=60-merchantWidth*.5f;
        if (VanillaTab(dl,"##vendor-tab",origin+new Vector2(merchantX,450)*s,"Merchant",merchantWidth,s,_vendorTab==0)) _vendorTab=0;
        if (VanillaTab(dl,"##buyback-tab",origin+new Vector2(merchantX+merchantWidth-16,450)*s,"Buyback",buybackWidth,s,_vendorTab==1)) _vendorTab=1;
        Vector2 close=origin+new Vector2(322,8)*s;DrawImageButton(dl,"##vendor-close",close,new Vector2(32)*s,@"Interface\Buttons\UI-Panel-MinimizeButton-Up",@"Interface\Buttons\UI-Panel-MinimizeButton-Down",@"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");if(ImGui.IsItemClicked())_vendor=null;
        if(_uiParityArmed&&_uiParityPanel=="merchant")MarkUiParityFrameComplete();
        ImGui.End();
    }
}
