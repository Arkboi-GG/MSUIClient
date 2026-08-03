using System.Globalization;
using System.Numerics;
using System.Text;
using ImGuiNET;
using MSUIClient.Engine;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private sealed record MailRow(uint Id, byte Type, ulong Sender, string Subject, uint ItemTextId,
        uint Stationery, uint ItemEntry, byte ItemCount, uint ItemMaxDurability, uint ItemDurability,
        uint Money, uint Cod, uint Checked, float ExpireDays, uint TemplateId);

    private sealed record PendingMailAction(string Kind, uint MailId, uint MoneyBefore, ulong ItemGuid,
        uint ItemEntry, uint AttachedMoney, uint Cod, double SentAt);

    private readonly List<MailRow> _mail = [];
    private bool _mailOpen;
    private bool _hasNewMail;
    private ulong _mailboxGuid;
    private int _mailSelected;
    private int _mailTab;
    private uint _mailAttachmentEntry;
    private PendingMailAction? _pendingMail;
    private readonly byte[] _mailRecipient = new byte[48];
    private readonly byte[] _mailSubject = new byte[68];
    private readonly byte[] _mailBody = new byte[504];
    private int _mailMoneyInput;
    private int _mailCodInput;

    private void InitMail()
    {
        WriteBuffer(_mailSubject, "NIGHT01 mail proof");
        WriteBuffer(_mailBody, "MSUIClient build-5875 mail lifecycle proof.");
    }

    private void ResetMail()
    {
        _mail.Clear(); _mailOpen = false; _hasNewMail = false; _mailboxGuid = 0; _mailSelected = 0; _pendingMail = null;
    }

    private static void WriteBuffer(byte[] buffer, string value)
    {
        Array.Clear(buffer); Encoding.UTF8.GetBytes(value.AsSpan(), buffer.AsSpan(0, buffer.Length - 1));
    }

    private static string ReadBuffer(byte[] buffer)
    {
        int end = Array.IndexOf(buffer, (byte)0); if (end < 0) end = buffer.Length;
        return Encoding.UTF8.GetString(buffer, 0, end);
    }

    private bool RequestMail(ulong guid)
    {
        bool eligible = _entities.TryGet(guid, out WorldEntity mailbox) && mailbox.IsGameObject && mailbox.GameObjectType == 19;
        bool sent = eligible && _net?.GetMailList(guid) == true;
        EmitInterface("mail", "list-send", sent ? "SENT" : "REFUSED", guid,
            $"eligible={eligible};type={mailbox?.Type};goType={mailbox?.GameObjectType ?? 0};entry={mailbox?.Entry ?? 0};body={Convert.ToHexString(WorldSession.BuildMailGuidBody(guid))}");
        if (sent) _mailboxGuid = guid;
        return sent;
    }

    private void ApplyMailList(byte[] body)
    {
        try
        {
            var r = new PacketReader(body); byte count = r.ReadU8();
            if (count > 100) throw new InvalidDataException($"mail count {count} exceeds server cap");
            var rows = new List<MailRow>(count);
            for (int i = 0; i < count; i++)
            {
                uint id = r.ReadU32(); byte type = r.ReadU8();
                ulong sender = type == 0 ? r.ReadU64() : type == 4 ? 0 : r.ReadU32();
                string subject = r.ReadCString(); uint text = r.ReadU32(); r.ReadU32();
                uint stationery = r.ReadU32(); uint item = r.ReadU32(); r.ReadU32(); r.ReadU32(); r.ReadU32();
                byte itemCount = r.ReadU8(); r.ReadU32(); uint maxDurability = r.ReadU32(); uint durability = r.ReadU32();
                uint money = r.ReadU32(); uint cod = r.ReadU32(); uint check = r.ReadU32(); float expiry = r.ReadF32();
                uint template = r.ReadU32();
                rows.Add(new(id, type, sender, subject, text, stationery, item, itemCount, maxDurability,
                    durability, money, cod, check, expiry, template));
            }
            if (r.Remaining != 0) throw new InvalidDataException($"trailing={r.Remaining}");
            _mail.Clear(); _mail.AddRange(rows); _mailOpen = true; _hasNewMail = false; _mailTab = 0;
            _mailSelected = Math.Clamp(_mailSelected, 0, Math.Max(0, _mail.Count - 1));
            foreach (MailRow row in rows)
                if (row.ItemEntry != 0) _items?.Require(row.ItemEntry, 0, _net!);
            EmitInterface("mail", "list", "DECODED", _mailboxGuid,
                $"count={rows.Count};ids={string.Join('|', rows.Select(x => x.Id))};bytes={body.Length}");
            foreach (MailRow row in rows)
                EmitInterface("mail", "expiry", "DISPLAYED", _mailboxGuid,
                    $"id={row.Id};subject={SanitizeEvidence(row.Subject)};days={row.ExpireDays.ToString("0.000", CultureInfo.InvariantCulture)};display={FormatExpiry(row.ExpireDays)};item={row.ItemEntry};count={row.ItemCount};money={row.Money};cod={row.Cod}");
        }
        catch (Exception ex)
        {
            EmitInterface("mail", "list", "MALFORMED", _mailboxGuid,
                $"error={SanitizeEvidence(ex.Message)};bytes={body.Length};hex={Convert.ToHexString(body)}");
        }
    }

    private void ApplyMailResult(byte[] body)
    {
        if (body.Length < 12) return;
        var r = new PacketReader(body); uint id = r.ReadU32(); uint action = r.ReadU32(); uint error = r.ReadU32();
        uint item = 0, count = 0, equip = 0;
        if (error == 1 && r.Remaining >= 4) equip = r.ReadU32();
        else if (action == 2 && r.Remaining >= 8) { item = r.ReadU32(); count = r.ReadU32(); }
        string outcome = error == 0 ? "SUCCESS" : $"FAILED-{error}";
        string kind = action switch { 0 => "send", 1 => "take-money", 2 => "take-item", 3 => "return", 4 => "delete", 5 => "make-permanent", _ => $"action-{action}" };
        EmitInterface("mail", kind, outcome, _mailboxGuid,
            $"mail={id};action={action};error={error};equip={equip};item={item};count={count};body={Convert.ToHexString(body)}");
        if (error == 0)
        {
            MailRow? row = _mail.FirstOrDefault(x => x.Id == id);
            if (row is not null)
            {
                int index = _mail.IndexOf(row);
                if (action == 1) _mail[index] = row with { Money = 0 };
                else if (action == 2) _mail[index] = row with { ItemEntry = 0, ItemCount = 0, Cod = 0 };
                else if (action is 3 or 4) _mail.RemoveAt(index);
            }
        }
        _pendingMail = null;
    }

    private void ApplyReceivedMail(byte[] body)
    {
        uint delay = body.Length >= 4 ? BitConverter.ToUInt32(body, 0) : 0;
        _hasNewMail = true;
        EmitInterface("mail", "notification", "RECEIVED", _mailboxGuid, $"delay={delay}");
    }

    private bool SendMailFlow(string receiver, uint itemEntry, uint money, uint cod,
        string? subject = null, string? body = null)
    {
        if (!_mailOpen || _net is null || string.IsNullOrWhiteSpace(receiver) || (cod > 0 && itemEntry == 0)) return false;
        ulong itemGuid = 0;
        if (itemEntry != 0 && _entities.TryGet(_net.PlayerGuid, out WorldEntity player))
            itemGuid = Enumerable.Range(0, 16).Select(i => player.Fields.PlayerBackpackSlot(i))
                .FirstOrDefault(g => g != 0 && _entities.TryGet(g, out WorldEntity item) && item.Entry == itemEntry);
        if (itemEntry != 0 && itemGuid == 0) return false;
        subject ??= ReadBuffer(_mailSubject); body ??= ReadBuffer(_mailBody);
        uint moneyBefore = _entities.TryGet(_net.PlayerGuid, out WorldEntity p) ? p.Fields.Coinage : 0;
        byte[] wire = WorldSession.BuildSendMailBody(_mailboxGuid, receiver, subject, body, itemGuid, money, cod);
        bool sent = _net.SendMail(_mailboxGuid, receiver, subject, body, itemGuid, money, cod);
        EmitInterface("mail", "send-send", sent ? "SENT" : "SEND_FAILED", _mailboxGuid,
            $"receiver={SanitizeEvidence(receiver)};subject={SanitizeEvidence(subject)};item={itemEntry};itemGuid=0x{itemGuid:X16};money={money};cod={cod};postage=30;body={Convert.ToHexString(wire)}");
        if (sent) _pendingMail = new("send", 0, moneyBefore, itemGuid, itemEntry, money, cod, NowSeconds());
        return sent;
    }

    private bool TakeMailMoney(uint id)
    {
        MailRow? row = _mail.FirstOrDefault(x => x.Id == id && x.Money > 0); if (row is null || _net is null) return false;
        bool sent = _net.MailTakeMoney(_mailboxGuid, id);
        EmitInterface("mail", "take-money-send", sent ? "SENT" : "SEND_FAILED", _mailboxGuid,
            $"mail={id};money={row.Money};body={Convert.ToHexString(WorldSession.BuildMailActionBody(_mailboxGuid, id))}"); return sent;
    }

    private bool TakeMailItem(uint id)
    {
        MailRow? row = _mail.FirstOrDefault(x => x.Id == id && x.ItemEntry != 0); if (row is null || _net is null) return false;
        bool sent = _net.MailTakeItem(_mailboxGuid, id);
        EmitInterface("mail", "take-item-send", sent ? "SENT" : "SEND_FAILED", _mailboxGuid,
            $"mail={id};item={row.ItemEntry};count={row.ItemCount};cod={row.Cod};body={Convert.ToHexString(WorldSession.BuildMailActionBody(_mailboxGuid, id))}"); return sent;
    }

    private bool ReturnMail(uint id)
    {
        if (_mail.All(x => x.Id != id) || _net is null) return false; bool sent = _net.MailReturn(_mailboxGuid, id);
        EmitInterface("mail", "return-send", sent ? "SENT" : "SEND_FAILED", _mailboxGuid,
            $"mail={id};body={Convert.ToHexString(WorldSession.BuildMailActionBody(_mailboxGuid, id))}"); return sent;
    }

    private bool DeleteMail(uint id)
    {
        if (_mail.All(x => x.Id != id) || _net is null) return false; bool sent = _net.MailDelete(_mailboxGuid, id);
        EmitInterface("mail", "delete-send", sent ? "SENT" : "SEND_FAILED", _mailboxGuid,
            $"mail={id};body={Convert.ToHexString(WorldSession.BuildMailActionBody(_mailboxGuid, id))}"); return sent;
    }

    private uint FirstMailId(string filter) => filter switch
    {
        "money" => _mail.FirstOrDefault(x => x.Money > 0)?.Id ?? 0,
        "item" => _mail.FirstOrDefault(x => x.ItemEntry > 0)?.Id ?? 0,
        "cod" => _mail.FirstOrDefault(x => x.Cod > 0)?.Id ?? 0,
        "deletable" => _mail.FirstOrDefault(x => x.Cod == 0 && x.ItemEntry == 0 && x.Money == 0)?.Id ?? 0,
        _ => _mail.FirstOrDefault()?.Id ?? 0,
    };

    private static string FormatExpiry(float days)
    {
        if (!float.IsFinite(days)) return "invalid"; TimeSpan left = TimeSpan.FromDays(Math.Max(0, days));
        return left.TotalDays >= 1 ? $"{(int)left.TotalDays}d {left.Hours}h" : $"{left.Hours}h {left.Minutes}m";
    }

    private void SimulateMailList()
    {
        var w = new PacketWriter(); w.WriteU8(5);
        WriteSyntheticMail(w, 100, "Money proof", 0, 0, 321, 0, 29.75f);
        WriteSyntheticMail(w, 101, "COD attachment proof", 159, 1, 0, 25, 6.5f);
        WriteSyntheticMail(w, 102, "Return proof", 117, 2, 0, 0, 11.25f);
        WriteSyntheticMail(w, 103, "Delete proof", 0, 0, 0, 0, 2.125f);
        WriteSyntheticMail(w, 104, "Expiry proof", 0, 0, 0, 0, 0.5f);
        ApplyMailList(w.ToArray());
        EmitInterface("mail", "simulate-list", "REPLAYED", _mailboxGuid, "rows=5;source=build-5875-shape");
    }

    private static void WriteSyntheticMail(PacketWriter w, uint id, string subject, uint item, byte count,
        uint money, uint cod, float expiry)
    {
        w.WriteU32(id); w.WriteU8(0); w.WriteU64(0x1234); w.WriteCString(subject);
        w.WriteU32(0); w.WriteU32(0); w.WriteU32(41); w.WriteU32(item);
        w.WriteU32(0); w.WriteU32(0); w.WriteU32(0); w.WriteU8(count); w.WriteU32(0);
        w.WriteU32(item == 159 ? 100u : 0u); w.WriteU32(item == 159 ? 100u : 0u);
        w.WriteU32(money); w.WriteU32(cod); w.WriteU32(0); w.WriteF32(expiry); w.WriteU32(0);
    }

    private void SimulateMailActions()
    {
        static byte[] Result(uint id, uint action, uint error = 0, uint item = 0, uint count = 0)
        { var w = new PacketWriter(); w.WriteU32(id); w.WriteU32(action); w.WriteU32(error); if (action == 2) { w.WriteU32(item); w.WriteU32(count); } return w.ToArray(); }
        ApplyMailResult(Result(0, 0));
        ApplyMailResult(Result(100, 1));
        ApplyMailResult(Result(101, 2, item: 159, count: 1));
        ApplyMailResult(Result(102, 3));
        ApplyMailResult(Result(103, 4));
        EmitInterface("mail", "simulate-actions", "REPLAYED", _mailboxGuid,
            "send=SUCCESS;takeMoney=SUCCESS;takeItemCod=SUCCESS;return=SUCCESS;delete=SUCCESS");
    }

    private void DrawMailFrame()
    {
        if (!_mailOpen || _gameplayArt is null) return;
        float s=GameplayUiScale();Vector2 origin=new(0,104*s),logicalSize=new(384,512);
        ImGui.SetNextWindowPos(origin,ImGuiCond.Always);ImGui.SetNextWindowSize(logicalSize*s,ImGuiCond.Always);ImGui.SetNextWindowBgAlpha(0);
        if (!ImGui.Begin("##mail",ImGuiWindowFlags.NoDecoration|ImGuiWindowFlags.NoMove|ImGuiWindowFlags.NoSavedSettings|ImGuiWindowFlags.NoBackground|ImGuiWindowFlags.NoNav)) { ImGui.End(); return; }
        ImDrawListPtr dl=ImGui.GetWindowDrawList();if(_uiParityArmed&&_uiParityPanel=="mail"){BeginUiParityFrame(origin,s);CollectUiParityDraw("MailFrame","Frame",origin,logicalSize*s,"",new("",0,"IMGUI_HOST","ANCHOR:ABSOLUTE","","",0,8));}
        (string Element,string Path,Vector2 Offset,Vector2 Size)[] art=[
            ("MailFrame/Texture",@"Interface\MailFrame\Mail-Icon",new(10,8),new(58,58)),
            ("MailFrameTopLeft",@"Interface\ItemTextFrame\UI-ItemText-TopLeft",Vector2.Zero,new(256,256)),
            ("MailFrameTopRight",@"Interface\Spellbook\UI-SpellbookPanel-TopRight",new(256,0),new(128,256)),
            ("MailFrameBotLeft",@"Interface\ItemTextFrame\UI-ItemText-BotLeft",new(0,256),new(256,256)),
            ("MailFrameBotRight",@"Interface\Spellbook\UI-SpellbookPanel-BotRight",new(256,256),new(128,256))];
        foreach(var r in art){Vector2 m=origin+r.Offset*s;DrawArt(dl,r.Path,m,r.Size,s);if(_uiParityArmed&&_uiParityPanel=="mail")CollectUiParityDraw(r.Element,"Texture",m,r.Size*s,"MailFrame",new(r.Path,0xffffffff,"IMGUI_IMAGE","TOPLEFT","MailFrame","TOPLEFT",r.Offset.X,-r.Offset.Y));}
        if (_gameplayArt is not null)
        {
            DrawVanillaMail(dl, origin, s);
            Vector2 mailClose=origin+new Vector2(323,10)*s;
            DrawImageButton(dl,"##mail-close",mailClose,new Vector2(32)*s,@"Interface\Buttons\UI-Panel-MinimizeButton-Up",@"Interface\Buttons\UI-Panel-MinimizeButton-Down",@"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");
            if(ImGui.IsItemClicked())_mailOpen=false;
            if(_uiParityArmed&&_uiParityPanel=="mail")MarkUiParityFrameComplete();
            ImGui.End(); return;
        }
        ImGui.SetCursorScreenPos(origin+new Vector2(28,78)*s);ImGui.BeginChild("mail-list",new Vector2(305,315)*s,false);
        for (int i = 0; i < _mail.Count; i++)
        {
            MailRow row = _mail[i]; string marker = row.ItemEntry != 0 ? "item" : row.Money != 0 ? "money" : "letter";
            if (ImGui.Selectable($"{row.Subject} [{marker}]##mail-{row.Id}", _mailSelected == i)) _mailSelected = i;
            ImGui.TextDisabled($"Expires {FormatExpiry(row.ExpireDays)} · COD {FormatMoney(row.Cod)}");
        }
        ImGui.EndChild();ImGui.SetCursorScreenPos(origin+new Vector2(28,400)*s);
        ImGui.BeginChild("mail-detail",new Vector2(305,65)*s,false);
        if (_mail.Count > 0)
        {
            MailRow row = _mail[Math.Clamp(_mailSelected, 0, _mail.Count - 1)];
            ImGui.TextWrapped(row.Subject); ImGui.Separator();
            ImGui.TextUnformatted($"Expires: {FormatExpiry(row.ExpireDays)} ({row.ExpireDays:0.000} days)");
            ImGui.TextUnformatted($"Money: {FormatMoney(row.Money)}   COD: {FormatMoney(row.Cod)}");
            ImGui.TextUnformatted($"Attachment: {(row.ItemEntry == 0 ? "none" : $"item {row.ItemEntry} x{row.ItemCount}")}");
            if (row.Money > 0 && ImGui.Button("Take money")) TakeMailMoney(row.Id);
            if (row.ItemEntry > 0 && ImGui.Button("Take attachment")) TakeMailItem(row.Id);
            if (ImGui.Button("Return")) ReturnMail(row.Id); ImGui.SameLine();
            if (row.Cod == 0 && ImGui.Button("Delete")) DeleteMail(row.Id);
        }
        ImGui.Separator();ImGui.TextUnformatted("Compose");
        ImGui.InputText("To",_mailRecipient,(uint)_mailRecipient.Length);
        ImGui.InputText("Subject",_mailSubject,(uint)_mailSubject.Length);
        ImGui.InputText("Body",_mailBody,(uint)_mailBody.Length);
        ImGui.InputInt("Money (copper)",ref _mailMoneyInput);ImGui.InputInt("COD (copper)",ref _mailCodInput);
        if(ImGui.Button("Send letter"))SendMailFlow(ReadBuffer(_mailRecipient),0,(uint)Math.Max(0,_mailMoneyInput),(uint)Math.Max(0,_mailCodInput));
        if(_config.DevTools&&ImGui.Button("Copy mail evidence"))CopyVerdictText(string.Join(Environment.NewLine,_verdicts.Snapshot("interface").OfType<InterfaceVerdict>().Where(v=>v.Family=="mail").Select(v=>$"[verdict:interface] {v.ToLine()}")));
        ImGui.EndChild();
        Vector2 close=origin+new Vector2(323,10)*s;DrawImageButton(dl,"##mail-close",close,new Vector2(32)*s,@"Interface\Buttons\UI-Panel-MinimizeButton-Up",@"Interface\Buttons\UI-Panel-MinimizeButton-Down",@"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");if(ImGui.IsItemClicked())_mailOpen=false;
        if(_uiParityArmed&&_uiParityPanel=="mail")MarkUiParityFrameComplete();
        ImGui.End();
    }

    private void DrawVanillaMail(ImDrawListPtr dl, Vector2 origin, float s)
    {
        if (_mailTab == 0)
        {
            DrawCenteredText(dl, origin + new Vector2(192, 20) * s, "Inbox", 14f * s, VanillaGold);
            for (int i = 0; i < _mail.Count && i < 7; i++)
            {
                MailRow row = _mail[i];
                if (VanillaListRow(dl,$"##mail-row-{row.Id}",origin+new Vector2(34,76+i*38)*s,
                        new Vector2(296,34),s,row.Subject,_mailSelected==i)) _mailSelected=i;
                dl.AddText(ImGui.GetFont(),9f*s,origin+new Vector2(42,96+i*38)*s,0xff999999,
                    $"Expires {FormatExpiry(row.ExpireDays)}");
            }
            if (_mail.Count > 0)
            {
                MailRow row=_mail[Math.Clamp(_mailSelected,0,_mail.Count-1)];
                dl.AddText(ImGui.GetFont(),10f*s,origin+new Vector2(36,360)*s,0xffffffff,
                    $"{FormatMoney(row.Money)}   COD {FormatMoney(row.Cod)}");
                if(row.Money>0&&VanillaButton(dl,"##mail-take-money","Take Money",origin+new Vector2(34,390)*s,new Vector2(95,22),s))TakeMailMoney(row.Id);
                if(row.ItemEntry>0&&VanillaButton(dl,"##mail-take-item","Take Item",origin+new Vector2(134,390)*s,new Vector2(90,22),s))TakeMailItem(row.Id);
                if(VanillaButton(dl,"##mail-return","Return",origin+new Vector2(229,390)*s,new Vector2(70,22),s))ReturnMail(row.Id);
                if(row.Cod==0&&VanillaButton(dl,"##mail-delete","Delete",origin+new Vector2(304,390)*s,new Vector2(60,22),s))DeleteMail(row.Id);
            }
        }
        else
        {
            DrawCenteredText(dl, origin + new Vector2(192, 20) * s, "Send Mail", 14f * s, VanillaGold);
            dl.AddText(ImGui.GetFont(),10*s,origin+new Vector2(34,86)*s,VanillaGold,"To:");
            VanillaInputText(dl,"##mail-to",_mailRecipient,origin+new Vector2(60,80)*s,new Vector2(255,22),s);
            dl.AddText(ImGui.GetFont(),10*s,origin+new Vector2(17,122)*s,VanillaGold,"Subject:");
            VanillaInputText(dl,"##mail-subject",_mailSubject,origin+new Vector2(60,116)*s,new Vector2(255,22),s);
            VanillaInputTextMultiline(dl,"##mail-body",_mailBody,origin+new Vector2(45,162)*s,new Vector2(290,145),s);
            dl.AddText(ImGui.GetFont(),10f*s,origin+new Vector2(48,330)*s,0xffffffff,
                _mailAttachmentEntry==0?"Attachment: none":$"Attachment: item {_mailAttachmentEntry}");
            dl.AddText(ImGui.GetFont(),9*s,origin+new Vector2(48,345)*s,VanillaGold,"Money");
            VanillaInputInt(dl,"##mail-money",ref _mailMoneyInput,origin+new Vector2(48,354)*s,new Vector2(120,22),s);
            dl.AddText(ImGui.GetFont(),9*s,origin+new Vector2(205,345)*s,VanillaGold,"C.O.D.");
            VanillaInputInt(dl,"##mail-cod",ref _mailCodInput,origin+new Vector2(205,354)*s,new Vector2(120,22),s);
            bool ready=ReadBuffer(_mailRecipient).Length>0&&(_mailCodInput<=0||_mailAttachmentEntry!=0);
            if(VanillaButton(dl,"##mail-send","Send",origin+new Vector2(244,405)*s,new Vector2(80,22),s,ready))
                SendMailFlow(ReadBuffer(_mailRecipient),_mailAttachmentEntry,(uint)Math.Max(0,_mailMoneyInput),(uint)Math.Max(0,_mailCodInput));
        }
        float inboxWidth=VanillaCharacterTabWidth("Inbox",s,0);
        float sendWidth=VanillaCharacterTabWidth("Send Mail",s,0);
        if(VanillaTab(dl,"##mail-inbox-tab",origin+new Vector2(24,436)*s,"Inbox",inboxWidth,s,_mailTab==0))_mailTab=0;
        if(VanillaTab(dl,"##mail-send-tab",origin+new Vector2(24+inboxWidth-8,436)*s,"Send Mail",sendWidth,s,_mailTab==1))_mailTab=1;
    }
}
