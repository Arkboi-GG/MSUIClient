using System.Globalization;
using System.Numerics;
using System.Text;
using ImGuiNET;
using MSUIClient.Engine;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private sealed record MailRow(uint Id, byte Type, ulong Sender, uint SenderId, string Subject,
        uint ItemTextId, uint Stationery, uint ItemEntry, uint ItemPermEnchant,
        int ItemRandomProperty, uint ItemSuffixFactor, byte ItemCount, uint ItemCharges,
        uint ItemMaxDurability, uint ItemDurability, uint Money, uint Cod, uint Checked,
        float ExpireDays, uint TemplateId);

    private sealed record PendingMailAction(string Kind, uint MailId, uint MoneyBefore, ulong ItemGuid,
        uint ItemEntry, uint AttachedMoney, uint Cod, double SentAt);

    private enum MailConfirmationKind { Cod, DeleteItem, DeleteMoney, SendMoney }
    private sealed record MailConfirmation(MailConfirmationKind Kind, uint MailId);

    private readonly List<MailRow> _mail = [];
    private readonly Dictionary<uint, string> _mailBodies = [];
    private readonly HashSet<uint> _mailBodyPending = [];
    private bool _mailOpen;
    private bool _hasNewMail;
    private float _nextMailSeconds = -86400f;
    private bool _mailRefreshPending;
    private ulong _mailboxGuid;
    private uint _openMailId;
    private int _mailPage = 1;
    private int _mailTab;
    private double _mailLastListQuery = double.NegativeInfinity;
    private ulong _mailAttachmentGuid;
    private uint _mailAttachmentEntry;
    private PendingMailAction? _pendingMail;
    private MailConfirmation? _mailConfirmation;
    private readonly byte[] _mailRecipient = new byte[48];
    private readonly byte[] _mailSubject = new byte[68];
    private readonly byte[] _mailBody = new byte[504];
    private int _mailGoldInput;
    private int _mailSilverInput;
    private int _mailCopperInput;
    private bool _mailCodMode;
    private bool _mailSendPending;
    private bool _mailReplyMode;
    private string _mailError = "";

    private void InitMail() => ResetComposeMail();

    private void ResetMail()
    {
        _mail.Clear();
        _mailBodies.Clear();
        _mailBodyPending.Clear();
        _mailOpen = false;
        _hasNewMail = false;
        _nextMailSeconds = -86400f;
        _mailRefreshPending = false;
        _mailboxGuid = 0;
        _openMailId = 0;
        _mailPage = 1;
        _mailTab = 0;
        _mailLastListQuery = double.NegativeInfinity;
        _pendingMail = null;
        _mailConfirmation = null;
        ResetComposeMail();
    }

    private void ResetComposeMail()
    {
        Array.Clear(_mailRecipient);
        Array.Clear(_mailSubject);
        Array.Clear(_mailBody);
        _mailAttachmentGuid = 0;
        _mailAttachmentEntry = 0;
        _mailGoldInput = _mailSilverInput = _mailCopperInput = 0;
        _mailCodMode = false;
        _mailSendPending = false;
        _mailReplyMode = false;
    }

    private static void WriteBuffer(byte[] buffer, string value)
    {
        Array.Clear(buffer);
        Encoding.UTF8.GetBytes(value.AsSpan(), buffer.AsSpan(0, buffer.Length - 1));
    }

    private static string ReadBuffer(byte[] buffer)
    {
        int end = Array.IndexOf(buffer, (byte)0);
        if (end < 0) end = buffer.Length;
        return Encoding.UTF8.GetString(buffer, 0, end);
    }

    private bool RequestMail(ulong guid)
    {
        if (_net is null || _controller is null ||
            !_entities.TryGet(guid, out WorldEntity mailbox) || !mailbox.IsGameObject ||
            mailbox.GameObjectType != 19 ||
            Vector3.Distance(_controller.Position, mailbox.Position) > MailUiLaw.InteractionDistance)
            return false;

        bool newMailbox = !_mailOpen || _mailboxGuid != guid;
        if (newMailbox)
        {
            _mail.Clear();
            _mailBodies.Clear();
            _mailBodyPending.Clear();
            _mailLastListQuery = double.NegativeInfinity;
            _mailPage = 1;
            _openMailId = 0;
            ResetComposeMail();
        }

        _characterOpen = false;
        _spellbookOpen = false;
        _talentOpen = false;
        CloseInspect(playSound: false);
        _mailboxGuid = guid;
        _mailOpen = true;
        SetBagWindowOpen(0, true);
        SetMailTab(0, playSound: true);
        PlayUiSound("igAbiliityPageTurn");
        PlayUiSound("igCharacterInfoOpen");
        bool sent = RefreshMailList(force: newMailbox);
        EmitInterface("mail", "open", sent ? "OPENED" : "OPENED_THROTTLED", guid,
            $"local=true;distance={Vector3.Distance(_controller.Position, mailbox.Position):R};limit={MailUiLaw.InteractionDistance:R}");
        return true;
    }

    private bool RefreshMailList(bool force)
    {
        if (!_mailOpen || _mailboxGuid == 0 || _net is null) return false;
        double now = NowSeconds();
        if (!force && now - _mailLastListQuery < MailUiLaw.InboxThrottleSeconds) return false;
        bool sent = _net.GetMailList(_mailboxGuid);
        if (sent) _mailLastListQuery = now;
        EmitInterface("mail", "list-send", sent ? "SENT" : "SEND_FAILED", _mailboxGuid,
            $"force={force};body={Convert.ToHexString(WorldSession.BuildMailGuidBody(_mailboxGuid))}");
        return sent;
    }

    private void CloseMailSession(bool playSound = true)
    {
        if (!_mailOpen) return;
        CloseOpenMail(playSound: true, autoDelete: true);
        _mailOpen = false;
        _mailConfirmation = null;
        _mailSendPending = false;
        SetBagWindowOpen(0, false);
        if (_mailRefreshPending && _net?.QueryNextMailTime() == true)
        {
            _nextMailSeconds = -86400f;
            _hasNewMail = false;
        }
        _mailRefreshPending = false;
        _mailboxGuid = 0;
        _mail.Clear();
        _mailBodies.Clear();
        _mailBodyPending.Clear();
        ResetComposeMail();
        if (playSound) PlayUiSound("igCharacterInfoClose");
    }

    private bool TryDismissMailConfirmationOnEscape()
    {
        if (_mailConfirmation is null) return false;
        _mailConfirmation = null;
        _mailSendPending = false;
        return true;
    }

    private void UpdateMail(float dt)
    {
        if (_nextMailSeconds > .01f)
        {
            _nextMailSeconds = Math.Max(0, _nextMailSeconds - Math.Max(0, dt));
            if (MailUiLaw.HasNewMail(_nextMailSeconds)) _hasNewMail = true;
        }
        if (!_mailOpen) return;
        // Synthetic/live-run mail panels deliberately have no world mailbox. Only enforce
        // the vanilla five-yard auto-close rule for a panel opened from a real mailbox.
        if (_mailboxGuid == 0) return;
        if (_controller is null || !_entities.TryGet(_mailboxGuid, out WorldEntity mailbox) ||
            Vector3.Distance(_controller.Position, mailbox.Position) > MailUiLaw.InteractionDistance)
            CloseMailSession();
    }

    private void ApplyMailList(byte[] body)
    {
        try
        {
            var r = new PacketReader(body);
            byte count = r.ReadU8();
            if (count > 100) throw new InvalidDataException($"mail count {count} exceeds server cap");
            var rows = new List<MailRow>(count);
            for (int i = 0; i < count; i++)
            {
                uint id = r.ReadU32();
                byte type = r.ReadU8();
                ulong sender = 0;
                uint senderId = 0;
                if (type == 0) sender = r.ReadU64();
                else if (type is 2 or 3 or 4) senderId = r.ReadU32();
                string subject = r.ReadCString();
                uint text = r.ReadU32();
                r.ReadU32(); // package: always zero on build 5875
                uint stationery = r.ReadU32();
                uint item = r.ReadU32();
                uint enchant = r.ReadU32();
                int randomProperty = r.ReadI32();
                uint suffixFactor = r.ReadU32();
                byte itemCount = r.ReadU8();
                uint charges = r.ReadU32();
                uint maxDurability = r.ReadU32();
                uint durability = r.ReadU32();
                uint money = r.ReadU32();
                uint cod = r.ReadU32();
                uint check = r.ReadU32();
                float expiry = r.ReadF32();
                uint template = r.ReadU32();
                if (expiry <= 0f)
                {
                    _net?.MailDelete(_mailboxGuid, id);
                    continue;
                }
                rows.Add(new(id, type, sender, senderId, subject, text, stationery, item, enchant,
                    randomProperty, suffixFactor, itemCount, charges, maxDurability, durability,
                    money, cod, check, expiry, template));
            }
            if (r.Remaining != 0) throw new InvalidDataException($"trailing={r.Remaining}");
            _mail.Clear();
            _mail.AddRange(rows);
            _mailPage = MailUiLaw.ClampPage(_mailPage, _mail.Count);
            if (_openMailId != 0 && _mail.All(x => x.Id != _openMailId))
                CloseOpenMail(playSound: true, autoDelete: false);
            foreach (MailRow row in rows)
            {
                if (row.ItemEntry != 0) _items?.Require(row.ItemEntry, 0, _net!);
                if (row.Type == 0 && row.Sender != 0 && !_playerNames.ContainsKey(row.Sender))
                    _net?.NameQuery(row.Sender);
            }
            EmitInterface("mail", "list", "DECODED", _mailboxGuid,
                $"count={rows.Count};ids={string.Join('|', rows.Select(x => x.Id))};bytes={body.Length}");
            foreach (MailRow row in rows)
                EmitInterface("mail", "expiry", "DISPLAYED", _mailboxGuid,
                    $"id={row.Id};subject={SanitizeEvidence(row.Subject)};days={row.ExpireDays.ToString("0.000", CultureInfo.InvariantCulture)};display={MailUiLaw.ExpiryText(row.ExpireDays)};item={row.ItemEntry};count={row.ItemCount};money={row.Money};cod={row.Cod}");
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
        var r = new PacketReader(body);
        uint id = r.ReadU32();
        uint action = r.ReadU32();
        uint error = r.ReadU32();
        uint item = 0, itemCount = 0, equip = 0;
        if (error == 1 && r.Remaining >= 4) equip = r.ReadU32();
        else if (action == 2 && error == 0 && r.Remaining >= 8)
        { item = r.ReadU32(); itemCount = r.ReadU32(); }
        string kind = action switch
        { 0 => "send", 1 => "take-money", 2 => "take-item", 3 => "return", 4 => "delete", 5 => "make-permanent", _ => $"action-{action}" };
        EmitInterface("mail", kind, error == 0 ? "SUCCESS" : $"FAILED-{error}", _mailboxGuid,
            $"mail={id};action={action};error={error};equip={equip};item={item};count={itemCount};body={Convert.ToHexString(body)}");

        _mailConfirmation = null;
        if (action == 0) _mailSendPending = false; // MAIL_FAILED fires for every send ack.
        if (error != 0)
        {
            _mailError = MailUiLaw.ErrorText(error);
            _pendingMail = null;
            return;
        }

        _mailError = "";
        if (action == 0)
        {
            bool reply = _mailReplyMode;
            ResetComposeMail();
            PlayUiSound("igAbiliityPageTurn");
            if (reply) SetMailTab(0, playSound: true);
        }
        else
        {
            if (_openMailId == id && action is 3 or 4)
                CloseOpenMail(playSound: true, autoDelete: false);
            RefreshMailList(force: true);
        }
        _pendingMail = null;
    }

    private void ApplyMailItemText(byte[] body)
    {
        var r = new PacketReader(body);
        uint textId = r.ReadU32();
        string text = r.ReadCString();
        if (r.Remaining != 0) throw new InvalidDataException($"item text trailing={r.Remaining}");
        _mailBodies[textId] = text;
        _mailBodyPending.Remove(textId);
        EmitInterface("mail", "body", "DECODED", _mailboxGuid,
            $"text={textId};length={text.Length}");
    }

    private void ApplyReceivedMail(byte[] body)
    {
        if (body.Length != 4) throw new InvalidDataException($"received mail bytes={body.Length}");
        float delay = new PacketReader(body).ReadF32();
        if (_mailOpen)
        {
            _mailRefreshPending = true;
            RefreshMailList(force: true);
        }
        else
        {
            _nextMailSeconds = delay;
            _hasNewMail = MailUiLaw.HasNewMail(delay);
        }
        EmitInterface("mail", "notification", "RECEIVED", _mailboxGuid, $"delay={delay:R}");
    }

    private void ApplyNextMailTime(byte[] body)
    {
        if (body.Length != 4) throw new InvalidDataException($"next mail time bytes={body.Length}");
        _nextMailSeconds = new PacketReader(body).ReadF32();
        _hasNewMail = MailUiLaw.HasNewMail(_nextMailSeconds);
        EmitInterface("mail", "next-mail-time", "RECEIVED", 0,
            $"seconds={_nextMailSeconds:R};waiting={_hasNewMail}");
    }

    private void SetMailTab(int tab, bool playSound)
    {
        tab = Math.Clamp(tab, 0, 1);
        if (_openMailId != 0) CloseOpenMail(playSound: true, autoDelete: true);
        _mailTab = tab;
        if (tab == 1 && !_mailReplyMode) _mailCodMode = false;
        if (playSound) PlayUiSound("igSpellBookOpen");
    }

    private MailRow? OpenMailRow() => _mail.FirstOrDefault(x => x.Id == _openMailId);

    private string MailSender(MailRow row)
        => row.Type == 0 && row.Sender != 0 && _playerNames.TryGetValue(row.Sender, out string? name)
            ? name : "Unknown";

    private bool SenderResolved(MailRow row)
        => row.Type == 0 && row.Sender != 0 && _playerNames.ContainsKey(row.Sender);

    private ItemTemplate? MailItem(MailRow row)
    {
        if (row.ItemEntry == 0 || _items is null) return null;
        _items.TryGet(row.ItemEntry, out ItemTemplate? item);
        return item;
    }

    private static string MailStationeryStem(uint id) => id switch
    {
        61 => "GMSTATIONERY",
        62 => "AUCTIONSTATIONERY",
        64 => "STATIONERY_VAL",
        _ => "STATIONERYTEST"
    };

    private static string MailStationeryIcon(MailRow row) => row.Stationery == 61
        ? @"Interface\Icons\Mail_GMIcon" : @"Interface\Icons\INV_Misc_Note_01";

    private void ToggleOpenMail(MailRow row)
    {
        if (_openMailId == row.Id)
        {
            CloseOpenMail(playSound: true, autoDelete: true);
            return;
        }
        if (_openMailId != 0) CloseOpenMail(playSound: true, autoDelete: true);
        _openMailId = row.Id;
        _mailRefreshPending = true;
        int index = _mail.FindIndex(x => x.Id == row.Id);
        if (!MailUiLaw.IsRead(row.Checked))
        {
            _net?.MailMarkAsRead(_mailboxGuid, row.Id);
            if (index >= 0) _mail[index] = row with { Checked = row.Checked | MailUiLaw.CheckedRead };
        }
        if (row.ItemTextId != 0 && !_mailBodies.ContainsKey(row.ItemTextId) &&
            _mailBodyPending.Add(row.ItemTextId))
            _net?.ItemTextQuery(row.ItemTextId, row.Id);
        PlayUiSound("igSpellBookOpen");
    }

    private void CloseOpenMail(bool playSound, bool autoDelete)
    {
        if (_openMailId == 0) return;
        MailRow? row = OpenMailRow();
        uint id = _openMailId;
        _openMailId = 0;
        _mailConfirmation = null;
        if (autoDelete && row is not null && row.Money == 0 && row.ItemEntry == 0 &&
            MailUiLaw.IsCopied(row.Checked))
            _net?.MailDelete(_mailboxGuid, id);
        if (playSound) PlayUiSound("igSpellBookClose");
    }

    private bool TakeMailMoney(uint id)
    {
        MailRow? row = _mail.FirstOrDefault(x => x.Id == id && x.Money > 0);
        if (row is null || _net is null) return false;
        bool sent = _net.MailTakeMoney(_mailboxGuid, id);
        EmitInterface("mail", "take-money-send", sent ? "SENT" : "SEND_FAILED", _mailboxGuid,
            $"mail={id};money={row.Money};body={Convert.ToHexString(WorldSession.BuildMailActionBody(_mailboxGuid, id))}");
        return sent;
    }

    private bool TakeMailItem(uint id, bool codConfirmed = false)
    {
        MailRow? row = _mail.FirstOrDefault(x => x.Id == id && x.ItemEntry != 0);
        if (row is null || _net is null) return false;
        if (row.Cod > 0 && !codConfirmed)
        {
            _mailConfirmation = new(MailConfirmationKind.Cod, id);
            return true;
        }
        bool sent = _net.MailTakeItem(_mailboxGuid, id);
        EmitInterface("mail", "take-item-send", sent ? "SENT" : "SEND_FAILED", _mailboxGuid,
            $"mail={id};item={row.ItemEntry};count={row.ItemCount};cod={row.Cod};body={Convert.ToHexString(WorldSession.BuildMailActionBody(_mailboxGuid, id))}");
        PlayUiSound("igMainMenuOptionCheckBoxOn");
        return sent;
    }

    private bool ReturnMail(uint id)
    {
        if (_mail.All(x => x.Id != id) || _net is null) return false;
        bool sent = _net.MailReturn(_mailboxGuid, id);
        EmitInterface("mail", "return-send", sent ? "SENT" : "SEND_FAILED", _mailboxGuid,
            $"mail={id};body={Convert.ToHexString(WorldSession.BuildMailActionBody(_mailboxGuid, id))}");
        return sent;
    }

    private bool DeleteMail(uint id, bool confirmed = false)
    {
        MailRow? row = _mail.FirstOrDefault(x => x.Id == id);
        if (row is null || _net is null) return false;
        if (!MailUiLaw.CanDelete(row.Type, row.Checked, row.ItemEntry != 0, row.Money))
            return ReturnMail(id);
        if (!confirmed && row.ItemEntry != 0)
        { _mailConfirmation = new(MailConfirmationKind.DeleteItem, id); return true; }
        if (!confirmed && row.Money != 0)
        { _mailConfirmation = new(MailConfirmationKind.DeleteMoney, id); return true; }
        bool sent = _net.MailDelete(_mailboxGuid, id);
        EmitInterface("mail", "delete-send", sent ? "SENT" : "SEND_FAILED", _mailboxGuid,
            $"mail={id};body={Convert.ToHexString(WorldSession.BuildMailActionBody(_mailboxGuid, id))}");
        return sent;
    }

    private bool MakeMailPermanent(uint id)
    {
        MailRow? row = _mail.FirstOrDefault(x => x.Id == id && x.ItemTextId != 0 &&
            !MailUiLaw.IsCopied(x.Checked));
        if (row is null || _net is null) return false;
        bool sent = _net.MailCreateTextItem(_mailboxGuid, id);
        if (sent) PlayUiSound("igMainMenuOptionCheckBoxOn");
        return sent;
    }

    private void ReplyToMail(MailRow row)
    {
        if (!MailUiLaw.CanReply(row.Type, row.Checked, SenderResolved(row))) return;
        WriteBuffer(_mailRecipient, MailSender(row));
        string subject = row.Subject.StartsWith("RE: ", StringComparison.Ordinal) ? row.Subject : "RE: " + row.Subject;
        WriteBuffer(_mailSubject, subject);
        _mailReplyMode = true;
        CloseOpenMail(playSound: true, autoDelete: true);
        _mailTab = 1;
        PlayUiSound("igSpellBookOpen");
    }

    private uint MailAmountCopper()
    {
        _mailGoldInput = Math.Max(0, _mailGoldInput);
        _mailSilverInput = Math.Clamp(_mailSilverInput, 0, 99);
        _mailCopperInput = Math.Clamp(_mailCopperInput, 0, 99);
        ulong amount = (ulong)_mailGoldInput * 10_000ul + (ulong)_mailSilverInput * 100ul +
            (uint)_mailCopperInput;
        return (uint)Math.Min(uint.MaxValue, amount);
    }

    private void AttachMailItem(ulong guid, uint entry)
    {
        _mailAttachmentGuid = guid;
        _mailAttachmentEntry = entry;
        if (_mailCodMode && guid == 0) _mailCodMode = false;
        if (entry != 0 && ReadBuffer(_mailSubject).Length == 0 &&
            _items?.TryGet(entry, out ItemTemplate? item) == true && item is not null)
            WriteBuffer(_mailSubject, item.Name);
    }

    private bool SendCurrentMail(bool moneyConfirmed = false)
    {
        string receiver = ReadBuffer(_mailRecipient);
        string subject = ReadBuffer(_mailSubject);
        string body = ReadBuffer(_mailBody);
        uint amount = MailAmountCopper();
        if (!MailUiLaw.CanSend(receiver, subject, _mailCodMode, amount,
                _mailAttachmentGuid != 0, _mailSendPending)) return false;
        if (!_mailCodMode && amount > 0 && !moneyConfirmed)
        {
            _mailConfirmation = new(MailConfirmationKind.SendMoney, 0);
            _mailSendPending = true;
            return true;
        }
        uint money = _mailCodMode ? 0 : amount;
        uint cod = _mailCodMode ? amount : 0;
        if (_net is null) return false;
        byte[] wire = WorldSession.BuildSendMailBody(_mailboxGuid, receiver, subject, body,
            _mailAttachmentGuid, money, cod);
        bool sent = _net.SendMail(_mailboxGuid, receiver, subject, body,
            _mailAttachmentGuid, money, cod);
        _mailSendPending = sent;
        if (sent)
            _pendingMail = new("send", 0,
                _entities.TryGet(_net.PlayerGuid, out WorldEntity p) ? p.Fields.Coinage : 0,
                _mailAttachmentGuid, _mailAttachmentEntry, money, cod, NowSeconds());
        EmitInterface("mail", "send-send", sent ? "SENT" : "SEND_FAILED", _mailboxGuid,
            $"receiver={SanitizeEvidence(receiver)};subject={SanitizeEvidence(subject)};item={_mailAttachmentEntry};itemGuid=0x{_mailAttachmentGuid:X16};money={money};cod={cod};postage={MailUiLaw.PostageCopper};body={Convert.ToHexString(wire)}");
        return sent;
    }

    // Retained for the protocol runner: its item-entry input is resolved to the same live guid the
    // UI attachment slot parks, then it enters the normal send path.
    private bool SendMailFlow(string receiver, uint itemEntry, uint money, uint cod,
        string? subject = null, string? body = null)
    {
        if (!_mailOpen || _net is null || string.IsNullOrWhiteSpace(receiver) ||
            (cod > 0 && itemEntry == 0)) return false;
        ulong itemGuid = 0;
        if (itemEntry != 0 && _entities.TryGet(_net.PlayerGuid, out WorldEntity player))
            itemGuid = Enumerable.Range(0, 16).Select(i => player.Fields.PlayerBackpackSlot(i))
                .FirstOrDefault(g => g != 0 && _entities.TryGet(g, out WorldEntity item) && item.Entry == itemEntry);
        if (itemEntry != 0 && itemGuid == 0) return false;
        subject ??= ReadBuffer(_mailSubject);
        body ??= ReadBuffer(_mailBody);
        bool sent = _net.SendMail(_mailboxGuid, receiver, subject, body, itemGuid, money, cod);
        if (sent) _mailSendPending = true;
        return sent;
    }

    private uint FirstMailId(string filter) => filter switch
    {
        "money" => _mail.FirstOrDefault(x => x.Money > 0)?.Id ?? 0,
        "item" => _mail.FirstOrDefault(x => x.ItemEntry > 0)?.Id ?? 0,
        "cod" => _mail.FirstOrDefault(x => x.Cod > 0)?.Id ?? 0,
        "deletable" => _mail.FirstOrDefault(x =>
            MailUiLaw.CanDelete(x.Type, x.Checked, x.ItemEntry != 0, x.Money))?.Id ?? 0,
        _ => _mail.FirstOrDefault()?.Id ?? 0,
    };

    private static string FormatExpiry(float days) => MailUiLaw.ExpiryText(days);

    private void SimulateMailList()
    {
        var w = new PacketWriter();
        w.WriteU8(5);
        WriteSyntheticMail(w, 100, "Money proof", 0, 0, 321, 0, 29.75f);
        WriteSyntheticMail(w, 101, "COD attachment proof", 159, 1, 0, 25, 6.5f);
        WriteSyntheticMail(w, 102, "Return proof", 117, 2, 0, 0, 11.25f);
        WriteSyntheticMail(w, 103, "Delete proof", 0, 0, 0, 0, 2.125f);
        WriteSyntheticMail(w, 104, "Expiry proof", 0, 0, 0, 0, 0.5f);
        ApplyMailList(w.ToArray());
        _mailOpen = true;
        EmitInterface("mail", "simulate-list", "REPLAYED", _mailboxGuid,
            "rows=5;source=build-5875-shape");
    }

    private static void WriteSyntheticMail(PacketWriter w, uint id, string subject, uint item,
        byte count, uint money, uint cod, float expiry)
    {
        w.WriteU32(id); w.WriteU8(0); w.WriteU64(0x1234); w.WriteCString(subject);
        w.WriteU32(id + 1000); w.WriteU32(0); w.WriteU32(41); w.WriteU32(item);
        w.WriteU32(0); w.WriteU32(0); w.WriteU32(0); w.WriteU8(count); w.WriteU32(0);
        w.WriteU32(item == 159 ? 100u : 0u); w.WriteU32(item == 159 ? 100u : 0u);
        w.WriteU32(money); w.WriteU32(cod); w.WriteU32(0); w.WriteF32(expiry); w.WriteU32(0);
    }

    private void SimulateMailActions()
    {
        static byte[] Result(uint id, uint action, uint error = 0, uint item = 0, uint count = 0)
        { var w = new PacketWriter(); w.WriteU32(id); w.WriteU32(action); w.WriteU32(error); if (action == 2 && error == 0) { w.WriteU32(item); w.WriteU32(count); } return w.ToArray(); }
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
        float s = GameplayUiScale();
        Vector2 origin = new(0, 104 * s), logicalSize = new(384, 512);
        ImGui.SetNextWindowPos(origin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(logicalSize * s, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        if (!ImGui.Begin("##mail", VanillaWindowFlags)) { ImGui.End(); return; }
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        if (_uiParityArmed && _uiParityPanel == "mail")
        {
            BeginUiParityFrame(origin, s);
            CollectUiParityDraw("MailFrame", "Frame", origin, logicalSize * s, "",
                new("", 0, "IMGUI_HOST", "ANCHOR:ABSOLUTE", "", "", 0, 8));
        }

        Vector2 shell = origin + (_mailTab == 1 ? new Vector2(2, 1) * s : Vector2.Zero);
        if (_mailTab == 0)
            DrawFourPieceShell(dl, shell, s,
                @"Interface\ItemTextFrame\UI-ItemText-TopLeft",
                @"Interface\Spellbook\UI-SpellbookPanel-TopRight",
                @"Interface\ItemTextFrame\UI-ItemText-BotLeft",
                @"Interface\Spellbook\UI-SpellbookPanel-BotRight");
        else
            DrawFourPieceShell(dl, shell, s,
                @"Interface\ClassTrainerFrame\UI-ClassTrainer-TopLeft",
                @"Interface\ClassTrainerFrame\UI-ClassTrainer-TopRight",
                @"Interface\ClassTrainerFrame\UI-ClassTrainer-BotLeft",
                @"Interface\ClassTrainerFrame\UI-ClassTrainer-BotRight");
        DrawArt(dl, @"Interface\MailFrame\Mail-Icon", origin + new Vector2(10, 8) * s,
            new Vector2(58), s);

        if (_mailTab == 0) DrawMailInbox(dl, origin, s);
        else DrawMailCompose(dl, origin, s);

        Vector2 close = origin + new Vector2(323, 9) * s;
        DrawImageButton(dl, "##mail-close", close, new Vector2(32) * s,
            @"Interface\Buttons\UI-Panel-MinimizeButton-Up",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Down",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");
        bool closeClicked = ImGui.IsItemClicked();

        float inboxWidth = VanillaCharacterTabWidth("Inbox", s, 0);
        float sendWidth = VanillaCharacterTabWidth("Send Mail", s, 0);
        if (VanillaTab(dl, "##mail-inbox-tab", origin + new Vector2(24, 436) * s,
                "Inbox", inboxWidth, s, _mailTab == 0)) SetMailTab(0, playSound: true);
        if (VanillaTab(dl, "##mail-send-tab", origin + new Vector2(24 + inboxWidth - 8, 436) * s,
                "Send Mail", sendWidth, s, _mailTab == 1)) SetMailTab(1, playSound: true);
        if (_uiParityArmed && _uiParityPanel == "mail") MarkUiParityFrameComplete();
        ImGui.End();
        if (closeClicked) CloseMailSession();
        if (_openMailId != 0) DrawOpenMailFrame(s);
    }

    private void DrawMailInbox(ImDrawListPtr dl, Vector2 origin, float s)
    {
        GameText.DrawCentered(dl, "GameFontNormal", "Inbox",
            origin + new Vector2(198, 26) * s, s);
        int first = MailUiLaw.FirstIndex(_mailPage, _mail.Count);
        for (int visible = 0; visible < MailUiLaw.InboxItemsPerPage; visible++)
        {
            int index = first + visible;
            if (index >= _mail.Count) break;
            DrawMailInboxRow(dl, origin + new Vector2(28, 80 + visible * 45) * s,
                _mail[index], s);
        }

        int pages = MailUiLaw.PageCount(_mail.Count);
        if (DrawMailPageButton(dl, "##mail-prev", origin + new Vector2(34, 392) * s,
                previous: true, enabled: _mailPage > 1, s))
        { _mailPage--; PlayUiSound("igMainMenuOptionCheckBoxOn"); }
        if (DrawMailPageButton(dl, "##mail-next", origin + new Vector2(298, 392) * s,
                previous: false, enabled: _mailPage < pages, s))
        { _mailPage++; PlayUiSound("igMainMenuOptionCheckBoxOn"); }
    }

    private void DrawMailInboxRow(ImDrawListPtr dl, Vector2 min, MailRow row, float s)
    {
        uint border = _gameplayArt?.Handle(@"Interface\MailFrame\MailItemBorder") ?? 0;
        if (border != 0)
        {
            dl.AddImage((nint)border, min, min + new Vector2(42, 48) * s,
                Vector2.Zero, new Vector2(.1640625f, .75f));
            dl.AddImage((nint)border, min + new Vector2(42, 0) * s,
                min + new Vector2(305, 48) * s, new Vector2(.1640625f, 0), new Vector2(1, .75f));
        }
        dl.AddRectFilled(min + new Vector2(-8, 43) * s, min + new Vector2(314, 45) * s,
            0x4d002955);

        bool read = MailUiLaw.IsRead(row.Checked);
        uint senderColor = read ? 0xffbfbfbf : VanillaGold;
        uint subjectColor = read ? 0xffbfbfbf : 0xffffffff;
        GameText.Draw(dl, "GameFontNormal", MailSender(row), min + new Vector2(47, 4) * s,
            s, senderColor);
        GameText.Draw(dl, "GameFontHighlightSmall", row.Subject,
            min + new Vector2(47, 20) * s, s, subjectColor);
        string expiry = MailUiLaw.ExpiryText(row.ExpireDays);
        uint expiryColor = row.ExpireDays >= 1f ? 0xff20ff20 : 0xff2020ff;
        GameText.DrawRightAligned(dl, "GameFontHighlightSmall", expiry,
            min + new Vector2(301, 4) * s, s, expiryColor);
        ImGui.SetCursorScreenPos(min + new Vector2(201, 1) * s);
        ImGui.InvisibleButton($"##mail-expiry-{row.Id}", new Vector2(100, 16) * s);
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(MailUiLaw.CanDelete(row.Type, row.Checked,
                row.ItemEntry != 0, row.Money)
                ? "Time until message is deleted" : "Time until message is returned");
            ImGui.EndTooltip();
        }

        Vector2 button = min + new Vector2(4, 3) * s;
        ItemTemplate? item = MailItem(row);
        string iconPath = row.ItemEntry != 0 && row.Stationery != 61 && item is not null
            ? item.IconPath : MailStationeryIcon(row);
        uint ring = _gameplayArt?.Handle(@"Interface\Buttons\UI-EmptySlot-White") ?? 0;
        uint tint = read ? 0xff808080 : 0xffffffff;
        if (ring != 0)
            dl.AddImage((nint)ring, button - new Vector2(13.5f) * s,
                button + new Vector2(50.5f) * s, Vector2.Zero, Vector2.One,
                read ? 0xff808080 : VanillaGold);
        uint icon = _gameplayArt?.Handle(iconPath) ?? 0;
        if (icon != 0) dl.AddImage((nint)icon, button, button + new Vector2(37) * s,
            Vector2.Zero, Vector2.One, tint);
        ImGui.SetCursorScreenPos(button);
        ImGui.InvisibleButton($"##mail-row-{row.Id}", new Vector2(37) * s,
            ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
        bool hovered = ImGui.IsItemHovered();
        if (hovered)
        {
            uint hi = _gameplayArt?.AdditiveHandle(@"Interface\Buttons\ButtonHilight-Square") ?? 0;
            if (hi != 0) dl.AddImage((nint)hi, button, button + new Vector2(37) * s);
            DrawMailRowTooltip(row, item);
        }
        if (row.Cod > 0)
            GameText.DrawCentered(dl, "GameFontHighlightSmall", "COD",
                button + new Vector2(18.5f, 30) * s, s);
        if (row.Id == _openMailId)
        {
            uint checkedArt = _gameplayArt?.AdditiveHandle(@"Interface\Buttons\CheckButtonHilight") ?? 0;
            if (checkedArt != 0) dl.AddImage((nint)checkedArt, button, button + new Vector2(37) * s);
        }
        if (ImGui.IsItemClicked()) ToggleOpenMail(row);
    }

    private void DrawMailRowTooltip(MailRow row, ItemTemplate? item)
    {
        ImGui.BeginTooltip();
        if (item is not null)
        {
            ImGui.TextUnformatted(item.Name);
            if (row.ItemCount > 1) ImGui.TextUnformatted($"Count: {row.ItemCount}");
        }
        if (row.Money > 0)
        {
            if (item is not null) ImGui.Separator();
            ImGui.TextUnformatted("Enclosed amount");
            ImGui.TextUnformatted(FormatMoney(row.Money));
        }
        else if (row.Cod > 0)
        {
            if (item is not null) ImGui.Separator();
            ImGui.TextUnformatted("Cash on Delivery Amount:");
            ImGui.TextUnformatted(FormatMoney(row.Cod));
        }
        ImGui.EndTooltip();
    }

    private bool DrawMailPageButton(ImDrawListPtr dl, string id, Vector2 min,
        bool previous, bool enabled, float s)
    {
        Vector2 size = new Vector2(32) * s;
        ImGui.SetCursorScreenPos(min);
        if (!enabled) ImGui.BeginDisabled();
        ImGui.InvisibleButton(id, size);
        bool clicked = enabled && ImGui.IsItemClicked();
        bool active = enabled && ImGui.IsItemActive();
        bool hovered = enabled && ImGui.IsItemHovered();
        if (!enabled) ImGui.EndDisabled();
        string stem = previous ? "PrevPage" : "NextPage";
        string state = !enabled ? "Disabled" : active ? "Down" : "Up";
        DrawArt(dl, $@"Interface\Buttons\UI-SpellbookIcon-{stem}-{state}", min,
            new Vector2(32), s);
        if (hovered) DrawArt(dl, @"Interface\Buttons\UI-Common-MouseHilight", min,
            new Vector2(32), s);
        if (previous)
            GameText.Draw(dl, "GameFontNormal", "Prev", min + new Vector2(32, 9) * s, s);
        else
            GameText.DrawRightAligned(dl, "GameFontNormal", "Next",
                min + new Vector2(0, 9) * s, s);
        return clicked;
    }

    private void DrawMailCompose(ImDrawListPtr dl, Vector2 origin, float s)
    {
        GameText.DrawCentered(dl, "GameFontNormal", "Send Mail",
            origin + new Vector2(198, 26) * s, s);
        DrawMailStationery(dl, origin + new Vector2(21, 97) * s, "STATIONERYTEST", s);
        DrawMailScrollRest(dl, origin + new Vector2(317, 97) * s, 257, s);
        DrawArt(dl, @"Interface\ClassTrainerFrame\UI-ClassTrainer-HorizontalBar",
            origin + new Vector2(15, 350) * s, new Vector2(256, 16), s);

        GameText.DrawRightAligned(dl, "GameFontNormal", "To:",
            origin + new Vector2(93, 50) * s, s);
        VanillaInputText(dl, "##mail-to", _mailRecipient, origin + new Vector2(105, 46) * s,
            new Vector2(122, 20), s);
        GameText.DrawRightAligned(dl, "GameFontNormal", "Subject:",
            origin + new Vector2(93, 73) * s, s);
        VanillaInputText(dl, "##mail-subject", _mailSubject,
            origin + new Vector2(105, 69) * s, new Vector2(237, 20), s);
        DrawMailBodyInput("##mail-body", _mailBody,
            origin + new Vector2(41, 107) * s, new Vector2(270, 200), s);

        GameText.DrawRightAligned(dl, "GameFontNormal", "Cost:",
            origin + new Vector2(282, 50) * s, s);
        DrawMailMoneyDisplay(dl, MailUiLaw.PostageCopper, origin + new Vector2(286, 47) * s, s,
            PlayerMoney() < MailUiLaw.PostageCopper ? 0xff1a1aff : 0xffffffff);

        Vector2 slot = origin + new Vector2(30, 368) * s;
        DrawMailSendSlot(dl, slot, s);
        string moneyLabel = _mailCodMode ? "Cash on Delivery Amount:" : "Amount to send:";
        GameText.Draw(dl, "GameFontNormalSmall", moneyLabel,
            origin + new Vector2(79, 373) * s, s);
        DrawMailMoneyInputs(dl, origin + new Vector2(82, 386) * s, s);
        DrawMailRadio(dl, "##mail-send-money", origin + new Vector2(252, 362) * s,
            "Send Money", !_mailCodMode, enabled: true, s, () =>
            { _mailCodMode = false; PlayUiSound("igMainMenuOptionCheckBoxOn"); });
        DrawMailRadio(dl, "##mail-cod", origin + new Vector2(252, 379) * s,
            "C.O.D.", _mailCodMode, _mailAttachmentGuid != 0, s, () =>
            { _mailCodMode = true; PlayUiSound("igMainMenuOptionCheckBoxOn"); });

        uint amount = MailAmountCopper();
        if (_mailCodMode && amount > MailUiLaw.MaxCodCopper)
        {
            GameText.Draw(dl, "GameFontRedSmall", "COD amount is too high.",
                origin + new Vector2(79, 358) * s, s);
        }
        DrawMailMoneyDisplay(dl, PlayerMoney(), origin + new Vector2(55, 421) * s, s);
        bool ready = MailUiLaw.CanSend(ReadBuffer(_mailRecipient), ReadBuffer(_mailSubject),
            _mailCodMode, amount, _mailAttachmentGuid != 0, _mailSendPending);
        if (VanillaButton(dl, "##mail-send", "Send", origin + new Vector2(185, 410) * s,
                new Vector2(80, 22), s, ready)) SendCurrentMail();
        if (VanillaButton(dl, "##mail-cancel", "Cancel", origin + new Vector2(265, 410) * s,
                new Vector2(80, 22), s)) CloseMailSession();
        if (_mailError.Length > 0)
            GameText.DrawCentered(dl, "GameFontRedSmall", _mailError,
                origin + new Vector2(192, 445) * s, s);
    }

    private uint PlayerMoney() => _net is not null &&
        _entities.TryGet(_net.PlayerGuid, out WorldEntity player) ? player.Fields.Coinage : 0;

    private void DrawMailStationery(ImDrawListPtr dl, Vector2 min, string stem, float s)
    {
        DrawArt(dl, $@"Interface\Stationery\{stem}1", min, new Vector2(252, 256), s);
        DrawArt(dl, $@"Interface\Stationery\{stem}2", min + new Vector2(252, 0) * s,
            new Vector2(64, 256), s);
    }

    private void DrawMailScrollRest(ImDrawListPtr dl, Vector2 right, float height, float s)
    {
        DrawMailArtUv(dl, @"Interface\PaperDollInfoFrame\UI-Character-ScrollBar",
            right + new Vector2(-2, -5) * s, new Vector2(31, 256), s,
            Vector2.Zero, new Vector2(.484375f, 1));
        DrawMailArtUv(dl, @"Interface\PaperDollInfoFrame\UI-Character-ScrollBar",
            right + new Vector2(-2, height - 104) * s, new Vector2(31, 106), s,
            new Vector2(.515625f, 0), new Vector2(1, .4140625f));
        Vector2 controlUv0 = new(.25f, .25f);
        Vector2 controlUv1 = new(.75f, .75f);
        DrawMailArtUv(dl, @"Interface\Buttons\UI-ScrollBar-ScrollUpButton-Disabled",
            right + new Vector2(6, 0) * s, new Vector2(16), s, controlUv0, controlUv1);
        DrawMailArtUv(dl, @"Interface\Buttons\UI-ScrollBar-ScrollDownButton-Disabled",
            right + new Vector2(6, height - 16) * s, new Vector2(16), s, controlUv0, controlUv1);
        DrawMailArtUv(dl, @"Interface\Buttons\UI-ScrollBar-Knob",
            right + new Vector2(6, 16) * s, new Vector2(16), s, controlUv0, controlUv1);
    }

    private static void DrawMailBodyInput(string id, byte[] buffer, Vector2 min,
        Vector2 logicalSize, float s)
    {
        string value = ReadBuffer(buffer);
        ImGui.SetCursorScreenPos(min);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.Border, Vector4.Zero);
        bool changed = ImGui.InputTextMultiline(id, ref value, (uint)buffer.Length,
            logicalSize * s);
        ImGui.PopStyleColor(4);
        if (changed) WriteBuffer(buffer, value);
    }

    private void DrawMailSendSlot(ImDrawListPtr dl, Vector2 min, float s)
    {
        DrawMailArtUv(dl, @"Interface\Buttons\UI-Slot-Background",
            min + new Vector2(-2, -2) * s, new Vector2(39), s,
            Vector2.Zero, new Vector2(.640625f));
        ItemTemplate? item = null;
        if (_mailAttachmentEntry != 0) _items?.TryGet(_mailAttachmentEntry, out item);
        if (item is not null)
        {
            uint icon = _gameplayArt?.Handle(item.IconPath) ?? 0;
            if (icon != 0) dl.AddImage((nint)icon, min, min + new Vector2(37) * s);
        }
        ImGui.SetCursorScreenPos(min);
        ImGui.InvisibleButton("##mail-send-item", new Vector2(37) * s);
        if (ImGui.IsItemHovered())
        {
            uint hi = _gameplayArt?.AdditiveHandle(@"Interface\Buttons\ButtonHilight-Square") ?? 0;
            if (hi != 0) dl.AddImage((nint)hi, min, min + new Vector2(37) * s);
            if (item is not null) DrawItemTooltip(item, 1);
            else { ImGui.BeginTooltip(); ImGui.TextUnformatted("Attach an item to send."); ImGui.EndTooltip(); }
        }
        if (ImGui.IsItemClicked())
        {
            if (HasCarriedItem && ResolveCarriedItem() is { } carried)
            { AttachMailItem(carried.Guid, carried.Entry); ClearCarriedItem(); }
            else if (_mailAttachmentGuid != 0) AttachMailItem(0, 0);
        }
        if (ImGui.BeginDragDropTarget())
        {
            ImGui.AcceptDragDropPayload("MSUI_INVENTORY_ITEM");
            if (ImGui.IsMouseReleased(ImGuiMouseButton.Left) &&
                ResolveCarriedItem() is { } carried)
            { AttachMailItem(carried.Guid, carried.Entry); ClearCarriedItem(); }
            ImGui.EndDragDropTarget();
        }
    }

    private void DrawMailArtUv(ImDrawListPtr dl, string path, Vector2 min, Vector2 size,
        float s, Vector2 uv0, Vector2 uv1)
    {
        uint art = _gameplayArt?.Handle(path) ?? 0;
        if (art != 0) dl.AddImage((nint)art, min, min + size * s, uv0, uv1);
    }

    private void DrawMailMoneyInputs(ImDrawListPtr dl, Vector2 min, float s)
    {
        VanillaInputInt(dl, "##mail-gold", ref _mailGoldInput, min,
            new Vector2(58, 20), s);
        DrawMailCoin(dl, 0, min + new Vector2(60, 4) * s, s);
        VanillaInputInt(dl, "##mail-silver", ref _mailSilverInput,
            min + new Vector2(84, 0) * s, new Vector2(30, 20), s);
        DrawMailCoin(dl, 1, min + new Vector2(106, 4) * s, s);
        VanillaInputInt(dl, "##mail-copper", ref _mailCopperInput,
            min + new Vector2(130, 0) * s, new Vector2(30, 20), s);
        DrawMailCoin(dl, 2, min + new Vector2(152, 4) * s, s);
    }

    private void DrawMailRadio(ImDrawListPtr dl, string id, Vector2 min, string caption,
        bool selected, bool enabled, float s, Action click)
    {
        uint radio = _gameplayArt?.Handle(@"Interface\Buttons\UI-RadioButton") ?? 0;
        Vector2 uv0 = selected ? new(.25f, 0) : Vector2.Zero;
        Vector2 uv1 = selected ? new(.5f, 1) : new(.25f, 1);
        if (radio != 0) dl.AddImage((nint)radio, min, min + new Vector2(16) * s,
            uv0, uv1, enabled ? 0xffffffff : 0xff808080);
        ImGui.SetCursorScreenPos(min);
        if (!enabled) ImGui.BeginDisabled();
        ImGui.InvisibleButton(id, new Vector2(80, 16) * s);
        bool clicked = enabled && ImGui.IsItemClicked();
        if (!enabled) ImGui.EndDisabled();
        GameText.Draw(dl, "GameFontNormalSmall", caption, min + new Vector2(18, 2) * s,
            s, enabled ? null : 0xff808080);
        if (clicked) click();
    }

    private void DrawMailMoneyDisplay(ImDrawListPtr dl, uint copper, Vector2 min, float s,
        uint color = 0xffffffff)
    {
        uint gold = copper / 10_000;
        uint silver = (copper / 100) % 100;
        uint coin = copper % 100;
        float x = min.X;
        foreach ((uint value, int icon) in new[] { (gold, 0), (silver, 1), (coin, 2) })
        {
            string text = value.ToString(CultureInfo.InvariantCulture);
            GameText.Draw(dl, "NumberFontNormal", text, new Vector2(x, min.Y), s, color);
            x += GameText.MeasureWidth("NumberFontNormal", text, s) + 2 * s;
            DrawMailCoin(dl, icon, new Vector2(x, min.Y) + new Vector2(0, 1) * s, s, color);
            x += 17 * s;
        }
    }

    private void DrawMailCoin(ImDrawListPtr dl, int icon, Vector2 min, float s,
        uint color = 0xffffffff)
    {
        uint money = _gameplayArt?.Handle(@"Interface\MoneyFrame\UI-MoneyIcons") ?? 0;
        if (money == 0) return;
        float u0 = icon * .25f;
        dl.AddImage((nint)money, min, min + new Vector2(13) * s,
            new Vector2(u0, 0), new Vector2(u0 + .25f, 1), color);
    }

    private void DrawOpenMailFrame(float s)
    {
        MailRow? row = OpenMailRow();
        if (row is null) return;
        Vector2 origin = new(374 * s, 104 * s), size = new Vector2(384, 512) * s;
        ImGui.SetNextWindowPos(origin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        if (!ImGui.Begin("##open-mail", VanillaWindowFlags)) { ImGui.End(); return; }
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        DrawFourPieceShell(dl, origin, s,
            @"Interface\ClassTrainerFrame\UI-ClassTrainer-TopLeft",
            @"Interface\ClassTrainerFrame\UI-ClassTrainer-TopRight",
            @"Interface\MailFrame\UI-OpenMail-BotLeft",
            @"Interface\ClassTrainerFrame\UI-ClassTrainer-BotRight");
        DrawArt(dl, MailStationeryIcon(row), origin + new Vector2(9, 6) * s,
            new Vector2(60), s);
        GameText.DrawCentered(dl, "GameFontNormal", "Open Mail",
            origin + new Vector2(198, 24) * s, s);
        GameText.DrawRightAligned(dl, "GameFontHighlight", "From:",
            origin + new Vector2(114, 45) * s, s);
        GameText.Draw(dl, "GameFontNormal", MailSender(row),
            origin + new Vector2(119, 45) * s, s);
        GameText.DrawRightAligned(dl, "GameFontHighlight", "Subject:",
            origin + new Vector2(114, 65) * s, s);
        GameText.Draw(dl, "GameFontNormalSmall", row.Subject,
            origin + new Vector2(119, 65) * s, s);
        DrawArt(dl, @"Interface\ClassTrainerFrame\UI-ClassTrainer-HorizontalBar",
            origin + new Vector2(15, 350) * s, new Vector2(256, 16), s);
        DrawMailStationery(dl, origin + new Vector2(21, 97) * s,
            MailStationeryStem(row.Stationery), s);
        DrawMailScrollRest(dl, origin + new Vector2(317, 97) * s, 257, s);
        string body = row.ItemTextId != 0 && _mailBodies.TryGetValue(row.ItemTextId, out string? text)
            ? text : "";
        DrawMailWrappedText(dl, body, origin + new Vector2(31, 107) * s, 276, 240, s);

        bool copy = row.ItemTextId != 0 && !MailUiLaw.IsCopied(row.Checked);
        bool package = row.ItemEntry != 0;
        bool money = row.Money != 0;
        bool attachments = copy || package || money;
        string attachmentText = attachments ? "Take Attachments:" : "No Attachments";
        Vector2 captionCenter = origin + new Vector2(attachments ? 124 : 187, 389) * s;
        GameText.DrawCentered(dl, "GameFontHighlightSmall", attachmentText, captionCenter, s,
            attachments ? null : 0xff808080);
        float slotX = attachments ? 189 : 0;
        if (copy)
        {
            if (DrawOpenMailSlot(dl, "##mail-copy", origin + new Vector2(slotX, 371) * s,
                    MailStationeryIcon(row), 1, s, "Click to make a permanent\ncopy of this letter."))
                MakeMailPermanent(row.Id);
            slotX += 47;
        }
        if (package)
        {
            ItemTemplate? item = MailItem(row);
            string path = item?.IconPath ?? @"Interface\Icons\INV_Misc_QuestionMark";
            if (DrawOpenMailSlot(dl, "##mail-package", origin + new Vector2(slotX, 371) * s,
                    path, row.ItemCount, s, null, row, item))
                TakeMailItem(row.Id);
            slotX += 47;
        }
        if (money)
        {
            if (DrawOpenMailSlot(dl, "##mail-money", origin + new Vector2(slotX, 371) * s,
                    @"Interface\Icons\INV_Misc_Coin_01", 1, s, FormatMoney(row.Money)))
                TakeMailMoney(row.Id);
        }

        bool canDelete = MailUiLaw.CanDelete(row.Type, row.Checked, package, row.Money);
        bool canReply = MailUiLaw.CanReply(row.Type, row.Checked, SenderResolved(row));
        if (VanillaButton(dl, "##open-mail-reply", "Reply", origin + new Vector2(101, 410) * s,
                new Vector2(82, 22), s, canReply)) ReplyToMail(row);
        if (VanillaButton(dl, "##open-mail-delete", canDelete ? "Delete" : "Return",
                origin + new Vector2(183, 410) * s, new Vector2(82, 22), s)) DeleteMail(row.Id);
        if (VanillaButton(dl, "##open-mail-close-bottom", "Close",
                origin + new Vector2(265, 410) * s, new Vector2(80, 22), s))
            CloseOpenMail(playSound: true, autoDelete: true);
        Vector2 close = origin + new Vector2(321, 9) * s;
        DrawImageButton(dl, "##open-mail-close", close, new Vector2(32) * s,
            @"Interface\Buttons\UI-Panel-MinimizeButton-Up",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Down",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");
        bool closeClicked = ImGui.IsItemClicked();
        ImGui.End();
        if (closeClicked) CloseOpenMail(playSound: true, autoDelete: true);
    }

    private bool DrawOpenMailSlot(ImDrawListPtr dl, string id, Vector2 min, string iconPath,
        uint count, float s, string? tooltip, MailRow? row = null, ItemTemplate? item = null)
    {
        DrawArt(dl, @"Interface\Buttons\UI-EmptySlot", min - new Vector2(10.5f) * s,
            new Vector2(58), s);
        uint icon = _gameplayArt?.Handle(iconPath) ?? 0;
        if (icon != 0) dl.AddImage((nint)icon, min, min + new Vector2(37) * s);
        if (count > 1)
            GameText.DrawRightAligned(dl, "NumberFontNormal", count.ToString(),
                min + new Vector2(35, 25) * s, s);
        ImGui.SetCursorScreenPos(min);
        ImGui.InvisibleButton(id, new Vector2(37) * s);
        if (ImGui.IsItemHovered())
        {
            uint hi = _gameplayArt?.AdditiveHandle(@"Interface\Buttons\ButtonHilight-Square") ?? 0;
            if (hi != 0) dl.AddImage((nint)hi, min, min + new Vector2(37) * s);
            if (item is not null && row is not null)
            {
                DrawItemTooltip(item, row.ItemCount, row.ItemDurability, row.ItemMaxDurability);
            }
            else if (!string.IsNullOrEmpty(tooltip))
            { ImGui.BeginTooltip(); ImGui.TextUnformatted(tooltip); ImGui.EndTooltip(); }
        }
        return ImGui.IsItemClicked();
    }

    private void DrawMailWrappedText(ImDrawListPtr dl, string text, Vector2 min,
        float logicalWidth, float logicalHeight, float s)
    {
        float width = logicalWidth * s;
        float pitch = GameText.LinePitch("MailTextFontNormal", s);
        int maxLines = Math.Max(1, (int)MathF.Floor(logicalHeight * s / pitch));
        int line = 0;
        foreach (string paragraph in text.Replace("\r", "").Split('\n'))
        {
            string current = "";
            foreach (string word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = current.Length == 0 ? word : current + " " + word;
                if (current.Length > 0 &&
                    GameText.MeasureWidth("MailTextFontNormal", candidate, s) > width)
                {
                    GameText.Draw(dl, "MailTextFontNormal", current,
                        min + new Vector2(0, line * pitch), s);
                    if (++line >= maxLines) return;
                    current = word;
                }
                else current = candidate;
            }
            if (current.Length > 0)
            {
                GameText.Draw(dl, "MailTextFontNormal", current,
                    min + new Vector2(0, line * pitch), s);
                if (++line >= maxLines) return;
            }
        }
    }

    private void DrawMailConfirmation()
    {
        if (_mailConfirmation is not { } confirmation || _skin is null) return;
        MailRow? row = confirmation.MailId == 0 ? null :
            _mail.FirstOrDefault(x => x.Id == confirmation.MailId);
        if (confirmation.MailId != 0 && row is null)
        { _mailConfirmation = null; return; }
        float s = GameplayUiScale();
        Vector2 display = ImGui.GetIO().DisplaySize;
        Vector2 size = new Vector2(360, 96) * s;
        Vector2 origin = new((display.X - size.X) * .5f, 128 * s);
        ImGui.SetNextWindowPos(origin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        ImGui.SetNextWindowFocus();
        if (!ImGui.Begin("##mail-confirm", VanillaWindowFlags)) { ImGui.End(); return; }
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        _skin.DrawBackdrop(dl, origin, origin + size, WowSkin.Dialog);
        bool alert = confirmation.Kind is MailConfirmationKind.DeleteItem or MailConfirmationKind.DeleteMoney;
        if (alert) _skin.GlueImage(dl, "dialog.alert", origin + new Vector2(12, 8) * s,
            origin + new Vector2(76, 72) * s);
        string message = confirmation.Kind switch
        {
            MailConfirmationKind.Cod => "Accepting this item will cost:",
            MailConfirmationKind.DeleteItem => $"Deleting this mail will also destroy {MailItem(row!)?.Name ?? $"item {row!.ItemEntry}"}",
            MailConfirmationKind.DeleteMoney => "Deleting this mail will also destroy the enclosed money.",
            _ => $"Really send {ReadBuffer(_mailRecipient)} the following amount?"
        };
        GameText.DrawCentered(dl, "GameFontNormal", message,
            origin + new Vector2(alert ? 218 : 180, 30) * s, s);
        if (confirmation.Kind == MailConfirmationKind.Cod && row is not null)
            DrawMailMoneyDisplay(dl, row.Cod, origin + new Vector2(145, 44) * s, s);
        if (confirmation.Kind == MailConfirmationKind.SendMoney)
            DrawMailMoneyDisplay(dl, MailAmountCopper(), origin + new Vector2(145, 44) * s, s);
        bool accept = DrawMailPopupButton(dl, "Accept", origin + new Vector2(48, 68) * s, s, "accept");
        bool cancel = DrawMailPopupButton(dl, "Cancel", origin + new Vector2(184, 68) * s, s, "cancel");
        ImGui.End();
        if (cancel)
        { _mailConfirmation = null; _mailSendPending = false; }
        else if (accept)
        {
            _mailConfirmation = null;
            switch (confirmation.Kind)
            {
                case MailConfirmationKind.Cod: TakeMailItem(confirmation.MailId, codConfirmed: true); break;
                case MailConfirmationKind.DeleteItem:
                case MailConfirmationKind.DeleteMoney: DeleteMail(confirmation.MailId, confirmed: true); break;
                case MailConfirmationKind.SendMoney:
                    _mailSendPending = false;
                    SendCurrentMail(moneyConfirmed: true);
                    break;
            }
        }
    }

    private bool DrawMailPopupButton(ImDrawListPtr dl, string caption, Vector2 min, float s, string id)
    {
        Vector2 size = new Vector2(128, 20) * s;
        ImGui.SetCursorScreenPos(min);
        ImGui.InvisibleButton($"##mail-confirm-{id}", size);
        bool active = ImGui.IsItemActive();
        bool hovered = ImGui.IsItemHovered();
        uint art = _skin!.TextureHandle(active ? "dialog.button.down" : "dialog.button.up");
        if (art != 0) dl.AddImage((nint)art, min, min + size, Vector2.Zero, new Vector2(1, .625f));
        if (hovered)
        {
            uint hi = _skin.TextureHandle("dialog.button.hi");
            if (hi != 0) dl.AddImage((nint)hi, min, min + size, Vector2.Zero, new Vector2(1, .625f));
        }
        GameText.DrawCentered(dl, hovered ? "DialogButtonHighlightText" : "DialogButtonNormalText",
            caption, min + size * .5f, s);
        return ImGui.IsItemClicked();
    }
}
