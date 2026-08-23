using System.Globalization;
using System.Numerics;
using System.Text;
using ImGuiNET;
using MSUIClient.Engine;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
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
    // EditBox `letters` is a Unicode-letter cap, not a UTF-8 byte cap. Four bytes per scalar plus
    // the NUL preserves the exact 12/64/500 limits without truncating multi-byte input early.
    private readonly byte[] _mailRecipient = new byte[MailUiLaw.MaxRecipientLetters * 4 + 1];
    private readonly byte[] _mailSubject = new byte[MailUiLaw.MaxSubjectLetters * 4 + 1];
    private readonly byte[] _mailBody = new byte[MailUiLaw.MaxBodyLetters * 4 + 1];
    private int _mailGoldInput;
    private int _mailSilverInput;
    private int _mailCopperInput;
    private bool _mailCodMode;
    private bool _mailSendPending;
    private bool _mailReplyMode;
    private bool _mailFocusRecipient;
    private bool _mailFocusSubject;
    private bool _mailFocusBody;
    private bool _mailFocusSilver;
    private bool _mailFocusCopper;
    private string _mailPreviousItemSubject = "";
    private StationeryCatalog? _mailStationery;
    private bool _mailStationeryLoaded;
    private string _mailError = "";
    // Cached only for child/telemetry projection during the current MailFrame draw. The seat
    // itself remains owned by UiPanelOwnershipLaw.
    private Vector2 _mailFrameOrigin;

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
        _mailFocusRecipient = false;
        _mailFocusSubject = false;
        _mailFocusBody = false;
        _mailFocusSilver = false;
        _mailFocusCopper = false;
        _mailPreviousItemSubject = "";
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

    private static void WriteMailBuffer(byte[] buffer, string value, int maximumLetters)
    {
        Array.Clear(buffer);
        string truncated = MailUiLaw.TruncateLetters(value, maximumLetters);
        Encoding.UTF8.GetBytes(truncated.AsSpan(), buffer.AsSpan(0, buffer.Length - 1));
    }

    private static void NormalizeMailBuffer(byte[] buffer, int maximumLetters) =>
        WriteMailBuffer(buffer, ReadBuffer(buffer), maximumLetters);

    private bool RequestMail(ulong guid)
    {
        if (_net is null || _controller is null ||
            !_entities.TryGet(guid, out WorldEntity mailbox) || !mailbox.IsGameObject ||
            mailbox.GameObjectType != 19 ||
            !NpcSessionUiLaw.InRange(
                Vector3.DistanceSquared(_controller.Position, mailbox.Position)))
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

        SetCharacterPageOpen(false);
        _spellbookOpen = false;
        _talentOpen = false;
        CloseInspect(playSound: false);
        _mailboxGuid = guid;
        _mailOpen = true;
        SetBagWindowOpen(0, true);
        SetMailTab(0, playSound: true);
        if (newMailbox) PlayUiSound("igMainMenuOptionCheckBoxOn");
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
        if (_nextMailSeconds > 0f)
        {
            _nextMailSeconds = MailUiLaw.StepNewMailCountdown(_nextMailSeconds, Math.Max(0, dt));
            _hasNewMail = MailUiLaw.HasNewMail(_nextMailSeconds);
        }
        if (!_mailOpen) return;
        // Synthetic/live-run mail panels deliberately have no world mailbox. Only enforce
        // the vanilla five-yard auto-close rule for a panel opened from a real mailbox.
        if (_mailboxGuid == 0) return;
        if (_controller is null) return;
        bool sourceAvailable = _entities.TryGet(_mailboxGuid, out WorldEntity mailbox);
        float distanceSquared = sourceAvailable
            ? Vector3.DistanceSquared(_controller.Position, mailbox.Position)
            : float.PositiveInfinity;
        if (NpcSessionUiLaw.ShouldClose(true, true, sourceAvailable, distanceSquared))
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
            PlayUiSound("igMainMenuOptionCheckBoxOn");
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
        }
        else
        {
            _nextMailSeconds = MailUiLaw.ApplyReceivedMailCountdown(_nextMailSeconds, delay,
                mailboxOpen: false);
            _hasNewMail = MailUiLaw.HasNewMail(_nextMailSeconds);
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
        bool enteringCompose = tab == 1 && _mailTab != 1;
        _mailTab = tab;
        if (enteringCompose)
        {
            _mailCodMode = false;
            _mailFocusRecipient = true;
            _mailFocusBody = false;
            PlayUiSound("igMainMenuOptionCheckBoxOn");
        }
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

    private string MailStationeryStem(uint id)
    {
        if (!_mailStationeryLoaded)
        {
            _mailStationeryLoaded = true;
            try { if (_mpq is not null) _mailStationery = StationeryCatalog.Load(_mpq); }
            catch { _mailStationery = null; }
        }
        if (_mailStationery is not null) return _mailStationery.Texture(id);
        return id switch
        {
            61 => "GMSTATIONERY",
            62 => "AUCTIONSTATIONERY",
            64 => "STATIONERY_VAL",
            _ => StationeryCatalog.DefaultTexture
        };
    }

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
        // The reference owns this cue on the physical attachment click, before the COD branch.
        // Accepting the popup is not a second physical attachment click and must stay silent.
        if (!codConfirmed) PlayUiSound("igMainMenuOptionCheckBoxOn");
        if (row.Cod > 0 && !codConfirmed)
        {
            _mailConfirmation = new(MailConfirmationKind.Cod, id);
            return true;
        }
        bool sent = _net.MailTakeItem(_mailboxGuid, id);
        EmitInterface("mail", "take-item-send", sent ? "SENT" : "SEND_FAILED", _mailboxGuid,
            $"mail={id};item={row.ItemEntry};count={row.ItemCount};cod={row.Cod};body={Convert.ToHexString(WorldSession.BuildMailActionBody(_mailboxGuid, id))}");
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
        PlayUiSound("igMainMenuOptionCheckBoxOn");
        bool sent = _net.MailCreateTextItem(_mailboxGuid, id);
        return sent;
    }

    private void DeleteOpenMail(MailRow row)
    {
        if (DeleteMail(row.Id) && _mailConfirmation is null)
            CloseOpenMail(playSound: true, autoDelete: false);
    }

    private void ReplyToMail(MailRow row)
    {
        if (!MailUiLaw.CanReply(row.Type, row.Checked, SenderResolved(row))) return;
        WriteMailBuffer(_mailRecipient, MailSender(row), MailUiLaw.MaxRecipientLetters);
        string subject = row.Subject.StartsWith("RE: ", StringComparison.Ordinal) ? row.Subject : "RE: " + row.Subject;
        WriteMailBuffer(_mailSubject, subject, MailUiLaw.MaxSubjectLetters);
        _mailReplyMode = true;
        CloseOpenMail(playSound: true, autoDelete: true);
        SetMailTab(1, playSound: true);
        _mailFocusRecipient = false;
        _mailFocusBody = true;
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
        string subject = ReadBuffer(_mailSubject);
        if (entry != 0 && _items?.TryGet(entry, out ItemTemplate? item) == true && item is not null)
        {
            if (subject.Length == 0 || subject == _mailPreviousItemSubject)
            {
                WriteMailBuffer(_mailSubject, item.Name, MailUiLaw.MaxSubjectLetters);
                _mailPreviousItemSubject = MailUiLaw.TruncateLetters(item.Name,
                    MailUiLaw.MaxSubjectLetters);
            }
        }
        else
        {
            if (subject == _mailPreviousItemSubject)
                WriteMailBuffer(_mailSubject, "", MailUiLaw.MaxSubjectLetters);
            _mailPreviousItemSubject = "";
            _mailCodMode = false;
            PlayUiSound("igMainMenuOptionCheckBoxOn");
        }
    }

    private uint MailAttachmentCount() => _mailAttachmentGuid != 0 &&
        _entities.TryGet(_mailAttachmentGuid, out WorldEntity attachment)
            ? Math.Max(1u, attachment.Fields.ItemStackCount) : 0;

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
        Vector2 origin = UiPanelFrameOrigin(UiPanelOwnershipRegistry[2], s), logicalSize = new(384, 512);
        _mailFrameOrigin = origin;
        ImGui.SetNextWindowPos(origin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(logicalSize * s, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        bool began = ImGui.Begin("##mail", VanillaWindowFlags);
        ImGui.PopStyleVar(2);
        if (!began) { ImGui.End(); return; }
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        Vector4 panelClip = new(origin.X, origin.Y, origin.X + logicalSize.X * s,
            origin.Y + logicalSize.Y * s);
        if (_uiParityArmed && _uiParityPanel == "mail")
        {
            BeginUiParityFrame(origin, s);
            CollectUiParityDraw("MailFrame", "Frame", origin, logicalSize * s, "",
                new("", 0, "IMGUI_HOST", "ANCHOR:ABSOLUTE", "UIParent", "TOPLEFT", 0, -104,
                    ContentRect: panelClip, ClipRect: panelClip, ClipMask: "WINDOW_RECT",
                    Visible: true, Enabled: true,
                    InteractionState: _mailTab == 0 ? "inbox" : "compose",
                    HitMin: origin, HitMax: origin + logicalSize * s, Strata: "MEDIUM"));
        }

        Vector2 shell = origin + (_mailTab == 1 ? new Vector2(2, 1) * s : Vector2.Zero);
        // MailFramePortrait is BACKGROUND and the four-piece shell is BORDER. Draw the square
        // portrait first so the shell's round aperture contains it instead of exposing its corners.
        Vector2 portraitMin = origin + new Vector2(10, 8) * s;
        DrawArt(dl, @"Interface\MailFrame\Mail-Icon", portraitMin, new Vector2(58), s);
        if (_uiParityArmed && _uiParityPanel == "mail")
            CollectUiParityDraw("MailFramePortrait", "Texture", portraitMin, new Vector2(58) * s,
                "MailFrame", new(@"Interface\MailFrame\Mail-Icon", 0xffffffff, "BACKGROUND",
                    "TOPLEFT", "MailFrame", "TOPLEFT", 10, -8,
                    ContentRect: MailContent(portraitMin, new Vector2(58) * s),
                    ClipRect: panelClip, ClipMask: "WINDOW_RECT", BlendMode: "BLEND",
                    Visible: true, Strata: "MEDIUM"));
        if (_mailTab == 0)
        {
            DrawFourPieceShell(dl, shell, s,
                @"Interface\ItemTextFrame\UI-ItemText-TopLeft",
                @"Interface\Spellbook\UI-SpellbookPanel-TopRight",
                @"Interface\ItemTextFrame\UI-ItemText-BotLeft",
                @"Interface\Spellbook\UI-SpellbookPanel-BotRight");
            TraceMailShell("MailFrame", shell, s, panelClip,
                @"Interface\ItemTextFrame\UI-ItemText-TopLeft",
                @"Interface\Spellbook\UI-SpellbookPanel-TopRight",
                @"Interface\ItemTextFrame\UI-ItemText-BotLeft",
                @"Interface\Spellbook\UI-SpellbookPanel-BotRight");
        }
        else
        {
            DrawFourPieceShell(dl, shell, s,
                @"Interface\ClassTrainerFrame\UI-ClassTrainer-TopLeft",
                @"Interface\ClassTrainerFrame\UI-ClassTrainer-TopRight",
                @"Interface\ClassTrainerFrame\UI-ClassTrainer-BotLeft",
                @"Interface\ClassTrainerFrame\UI-ClassTrainer-BotRight");
            TraceMailShell("MailFrame", shell, s, panelClip,
                @"Interface\ClassTrainerFrame\UI-ClassTrainer-TopLeft",
                @"Interface\ClassTrainerFrame\UI-ClassTrainer-TopRight",
                @"Interface\ClassTrainerFrame\UI-ClassTrainer-BotLeft",
                @"Interface\ClassTrainerFrame\UI-ClassTrainer-BotRight");
        }

        if (_mailTab == 0) DrawMailInbox(dl, origin, s);
        else DrawMailCompose(dl, origin, s);

        Vector2 close = origin + new Vector2(323, 9) * s;
        DrawImageButton(dl, "##mail-close", close, new Vector2(32) * s,
            @"Interface\Buttons\UI-Panel-MinimizeButton-Up",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Down",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");
        bool closeActive = ImGui.IsItemActive(), closeHovered = ImGui.IsItemHovered();
        bool closeClicked = MailReleasedCurrentItem();
        TraceMailControl("MailFrameCloseButton", "MailFrame", close, new Vector2(32) * s,
            closeActive, closeHovered, true, panelClip,
            closeActive ? @"Interface\Buttons\UI-Panel-MinimizeButton-Down" :
                @"Interface\Buttons\UI-Panel-MinimizeButton-Up");

        float inboxWidth = VanillaCharacterTabWidth("Inbox", s, 0);
        float sendWidth = VanillaCharacterTabWidth("Send Mail", s, 0);
        Vector2 inboxTabMin = origin + new Vector2(24, 436) * s;
        VanillaTab(dl, "##mail-inbox-tab", inboxTabMin, "Inbox", inboxWidth, s, _mailTab == 0);
        bool inboxActive = ImGui.IsItemActive(), inboxHovered = ImGui.IsItemHovered();
        bool inboxClicked = MailReleasedCurrentItem();
        TraceMailControl("MailFrameTab1", "MailFrame", inboxTabMin,
            new Vector2(inboxWidth, 32) * s, inboxActive, inboxHovered, true, panelClip,
            _mailTab == 0 ? @"Interface\PaperDollInfoFrame\UI-Character-ActiveTab" :
                @"Interface\PaperDollInfoFrame\UI-Character-InActiveTab");
        Vector2 sendTabMin = origin + new Vector2(24 + inboxWidth - 8, 436) * s;
        VanillaTab(dl, "##mail-send-tab", sendTabMin, "Send Mail", sendWidth, s, _mailTab == 1);
        bool sendActive = ImGui.IsItemActive(), sendHovered = ImGui.IsItemHovered();
        bool sendClicked = MailReleasedCurrentItem();
        TraceMailControl("MailFrameTab2", "MailFrame", sendTabMin,
            new Vector2(sendWidth, 32) * s, sendActive, sendHovered, true, panelClip,
            _mailTab == 1 ? @"Interface\PaperDollInfoFrame\UI-Character-ActiveTab" :
                @"Interface\PaperDollInfoFrame\UI-Character-InActiveTab");
        ImGui.End();
        if (closeClicked) CloseMailSession();
        else
        {
            if (inboxClicked) SetMailTab(0, playSound: true);
            if (sendClicked) SetMailTab(1, playSound: true);
            if (_openMailId != 0) DrawOpenMailFrame(s);
        }
        if (_uiParityArmed && _uiParityPanel == "mail" && _mailConfirmation is null)
            MarkUiParityFrameComplete();
    }

    private static bool MailReleasedCurrentItem(bool enabled = true) => enabled &&
        ImGui.IsItemDeactivated() && ImGui.IsItemHovered();

    private void TraceMailShell(string parent, Vector2 origin, float s, Vector4 clip,
        string topLeft, string topRight, string bottomLeft, string bottomRight)
    {
        if (!_uiParityArmed || _uiParityPanel != "mail") return;
        (string Element, string Path, Vector2 Offset, Vector2 Size)[] pieces =
        [
            ($"{parent}TopLeft", topLeft, Vector2.Zero, new(256, 256)),
            ($"{parent}TopRight", topRight, new(256, 0), new(128, 256)),
            ($"{parent}BotLeft", bottomLeft, new(0, 256), new(256, 256)),
            ($"{parent}BotRight", bottomRight, new(256, 256), new(128, 256))
        ];
        foreach (var piece in pieces)
        {
            Vector2 min = origin + piece.Offset * s;
            CollectUiParityDraw(piece.Element, "Texture", min, piece.Size * s, parent,
                new(piece.Path, 0xffffffff, "BORDER", "TOPLEFT", parent, "TOPLEFT",
                    piece.Offset.X, -piece.Offset.Y,
                    ContentRect: MailContent(min, piece.Size * s), ClipRect: clip,
                    ClipMask: "WINDOW_RECT", BlendMode: "BLEND", Visible: true,
                    Strata: "MEDIUM"));
        }
    }

    private void TraceMailControl(string element, string parent, Vector2 min, Vector2 size,
        bool active, bool hovered, bool enabled, Vector4 clip, string texture)
    {
        if (!_uiParityArmed || _uiParityPanel != "mail") return;
        float logicalScale = (clip.Z - clip.X) / 384f;
        float offsetX = (min.X - clip.X) / logicalScale;
        float offsetY = -(min.Y - clip.Y) / logicalScale;
        CollectUiParityDraw(element, "Button", min, size, parent,
            new("", 0, "IMGUI_HIT_TARGET", "TOPLEFT", parent, "TOPLEFT", offsetX, offsetY,
                ContentRect: MailContent(min, size), ClipRect: clip,
                ClipMask: "WINDOW_RECT", Visible: true,
                Enabled: enabled, InteractionState: !enabled ? "disabled" : active ? "pushed" :
                    hovered ? "highlighted" : "normal", HitMin: min, HitMax: min + size,
                Strata: "MEDIUM"));
        bool pushedTexture = texture.EndsWith("-Down", StringComparison.OrdinalIgnoreCase) ||
            texture.EndsWith("Button-Down", StringComparison.OrdinalIgnoreCase);
        CollectUiParityDraw(element + (pushedTexture ? "/PushedTexture" : "/NormalTexture"),
            pushedTexture ? "PushedTexture" : "NormalTexture", min, size, element,
            new(texture, 0xffffffff, "ARTWORK", "CENTER", element, "CENTER", 0, 0,
                ContentRect: MailContent(min, size), ClipRect: clip,
                ClipMask: "WINDOW_RECT", BlendMode: "BLEND",
                Visible: true, Strata: "MEDIUM"));
    }

    private void DrawMailInbox(ImDrawListPtr dl, Vector2 origin, float s)
    {
        GameText.DrawCentered(dl, "GameFontNormal", "Inbox",
            origin + new Vector2(198, 26) * s, s);
        int first = MailUiLaw.FirstIndex(_mailPage, _mail.Count);
        for (int visible = 0; visible < MailUiLaw.InboxItemsPerPage; visible++)
        {
            int index = first + visible;
            Vector2 rowMin = origin + new Vector2(28, 80 + visible * 45) * s;
            if (index < _mail.Count)
                DrawMailInboxRow(dl, rowMin, _mail[index], s, visible);
            else if (_uiParityArmed && _uiParityPanel == "mail")
            {
                Vector4 clip = MailPanelClip(origin, s);
                CollectUiParityDraw($"MailItem{visible + 1}", "Frame", rowMin,
                    new Vector2(305, 45) * s, "InboxFrame",
                    new("", 0, "NOT_DRAWN", "TOPLEFT", "InboxFrame", "TOPLEFT", 28,
                        -(80 + visible * 45),
                        ContentRect: MailContent(rowMin, new Vector2(305, 45) * s),
                        ClipRect: clip,
                        ClipMask: "WINDOW_RECT", Visible: false, Enabled: false,
                        InteractionState: "absent", Strata: "MEDIUM"));
            }
        }

        int pages = MailUiLaw.PageCount(_mail.Count);
        if (DrawMailPageButton(dl, "##mail-prev", origin + new Vector2(34, 392) * s,
                previous: true, enabled: _mailPage > 1, s))
        { _mailPage--; PlayUiSound("igMainMenuOptionCheckBoxOn"); }
        if (DrawMailPageButton(dl, "##mail-next", origin + new Vector2(298, 392) * s,
                previous: false, enabled: _mailPage < pages, s))
        { _mailPage++; PlayUiSound("igMainMenuOptionCheckBoxOn"); }
    }

    private void DrawMailInboxRow(ImDrawListPtr dl, Vector2 min, MailRow row, float s,
        int visibleIndex)
    {
        Vector4 clip = MailPanelClip(_mailFrameOrigin, s);
        string frameElement = $"MailItem{visibleIndex + 1}";
        if (_uiParityArmed && _uiParityPanel == "mail")
            CollectUiParityDraw(frameElement, "Frame", min, new Vector2(305, 45) * s,
                "InboxFrame", new("", 0, "IMGUI_COMPOSED", "TOPLEFT", "InboxFrame",
                    "TOPLEFT", 28, -(80 + visibleIndex * 45),
                    ContentRect: MailContent(min, new Vector2(305, 45) * s),
                    ClipRect: clip, ClipMask: "WINDOW_RECT", Visible: true, Enabled: true,
                    InteractionState: MailUiLaw.IsRead(row.Checked) ? "read" : "unread",
                    Strata: "MEDIUM"));
        uint border = _gameplayArt?.Handle(@"Interface\MailFrame\MailItemBorder") ?? 0;
        if (border != 0)
        {
            dl.AddImage((nint)border, min, min + new Vector2(42, 48) * s,
                Vector2.Zero, new Vector2(.1640625f, .75f));
            dl.AddImage((nint)border, min + new Vector2(42, 0) * s,
                min + new Vector2(305, 48) * s, new Vector2(.1640625f, 0), new Vector2(1, .75f));
        }
        if (_uiParityArmed && _uiParityPanel == "mail")
        {
            CollectUiParityDraw(frameElement + "BorderLeft", "TextureUv", min,
                new Vector2(42, 48) * s, frameElement,
                new(@"Interface\MailFrame\MailItemBorder", 0xffffffff, "BACKGROUND",
                    "TOPLEFT", frameElement, "TOPLEFT", 0, 0,
                    TexCoords: "0|0|0.1640625|0.75",
                    ContentRect: MailContent(min, new Vector2(42, 48) * s), ClipRect: clip,
                    ClipMask: "WINDOW_RECT", BlendMode: "BLEND", Strata: "MEDIUM"));
            CollectUiParityDraw(frameElement + "BorderRight", "TextureUv",
                min + new Vector2(42, 0) * s, new Vector2(263, 48) * s, frameElement,
                new(@"Interface\MailFrame\MailItemBorder", 0xffffffff, "BACKGROUND",
                    "TOPRIGHT", frameElement, "TOPRIGHT", 0, 0,
                    TexCoords: "0.1640625|0|1|0.75",
                    ContentRect: MailContent(min + new Vector2(42, 0) * s,
                        new Vector2(263, 48) * s), ClipRect: clip,
                    ClipMask: "WINDOW_RECT", BlendMode: "BLEND", Strata: "MEDIUM"));
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
        bool expiryHovered = ImGui.IsItemHovered();
        if (expiryHovered)
        {
            string expiryTooltip = MailUiLaw.CanDelete(row.Type, row.Checked,
                row.ItemEntry != 0, row.Money)
                ? "Time until message is deleted" : "Time until message is returned";
            GameTooltipOwnerKey tooltipOwner = new("mail-inbox-expiry", (ulong)visibleIndex);
            OfferPreservedSharedGameTooltipRenderer(tooltipOwner, () =>
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted(expiryTooltip);
                ImGui.EndTooltip();
            });
        }
        if (_uiParityArmed && _uiParityPanel == "mail")
        {
            Vector2 expiryMin = min + new Vector2(201, 1) * s;
            CollectUiParityDraw(frameElement + "ExpireTime", "Button", expiryMin,
                new Vector2(100, 16) * s, frameElement,
                new("", 0, "IMGUI_HIT_TARGET", "TOPRIGHT", frameElement, "TOPRIGHT", -4,
                    -4, ContentRect: MailContent(expiryMin, new Vector2(100, 16) * s),
                    ClipRect: clip, ClipMask: "WINDOW_RECT",
                    Visible: true, Enabled: true,
                    InteractionState: expiryHovered ? "highlighted" : "normal",
                    HitMin: expiryMin, HitMax: expiryMin + new Vector2(100, 16) * s,
                    Strata: "MEDIUM"));
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
        bool clicked = ImGui.InvisibleButton($"##mail-row-{row.Id}", new Vector2(37) * s,
            ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
        bool hovered = ImGui.IsItemHovered(), active = ImGui.IsItemActive();
        if (hovered)
        {
            uint hi = _gameplayArt?.AdditiveHandle(@"Interface\Buttons\ButtonHilight-Square") ?? 0;
            if (hi != 0) dl.AddImage((nint)hi, button, button + new Vector2(37) * s);
            OfferMailInboxRowTooltip(visibleIndex, row, item);
        }
        if (row.Cod > 0)
            GameText.DrawCentered(dl, "GameFontHighlightSmall", "COD",
                button + new Vector2(18.5f, 30) * s, s);
        if (row.Id == _openMailId)
        {
            uint checkedArt = _gameplayArt?.AdditiveHandle(@"Interface\Buttons\CheckButtonHilight") ?? 0;
            if (checkedArt != 0) dl.AddImage((nint)checkedArt, button, button + new Vector2(37) * s);
        }
        if (_uiParityArmed && _uiParityPanel == "mail")
        {
            CollectUiParityDraw(frameElement + "ButtonSlot", "Texture", button - new Vector2(13.5f) * s,
                new Vector2(64) * s, frameElement,
                new(@"Interface\Buttons\UI-EmptySlot-White", read ? 0xff808080 : VanillaGold,
                    "BACKGROUND", "CENTER", frameElement + "Button", "CENTER", 0, 0,
                    TexCoords: "0|0|1|1",
                    ContentRect: MailContent(button - new Vector2(13.5f) * s,
                        new Vector2(64) * s), ClipRect: clip,
                    ClipMask: "WINDOW_RECT", BlendMode: "BLEND", Strata: "MEDIUM"));
            CollectUiParityDraw(frameElement + "ButtonIcon", "Texture", button,
                new Vector2(37) * s, frameElement + "Button",
                new(iconPath, tint, "ARTWORK", "CENTER", frameElement + "Button", "CENTER", 0,
                    0, TexCoords: "0|0|1|1",
                    ContentRect: MailContent(button, new Vector2(37) * s), ClipRect: clip,
                    ClipMask: "WINDOW_RECT", BlendMode: "BLEND", Strata: "MEDIUM"));
            CollectUiParityDraw(frameElement + "Button", "CheckButton", button,
                new Vector2(37) * s, frameElement,
                new("", 0, "IMGUI_HIT_TARGET", "TOPLEFT", frameElement, "TOPLEFT", 4, -3,
                    ContentRect: MailContent(button, new Vector2(37) * s), ClipRect: clip,
                    ClipMask: "WINDOW_RECT", Visible: true,
                    Enabled: true, InteractionState: active ? "pushed" : hovered ? "highlighted" :
                        row.Id == _openMailId ? "checked" : "normal", HitMin: button,
                    HitMax: button + new Vector2(37) * s, Strata: "MEDIUM"));
            if (hovered)
                CollectUiParityDraw(frameElement + "ButtonHighlight", "HighlightTexture", button,
                    new Vector2(37) * s, frameElement + "Button",
                    new(@"Interface\Buttons\ButtonHilight-Square", 0xffffffff, "HIGHLIGHT",
                        "CENTER", frameElement + "Button", "CENTER", 0, 0,
                        TexCoords: "0|0|1|1",
                        ContentRect: MailContent(button, new Vector2(37) * s), ClipRect: clip,
                        ClipMask: "WINDOW_RECT", BlendMode: "ADD", Strata: "MEDIUM"));
        }
        if (clicked) ToggleOpenMail(row);
    }

    private void OfferMailInboxRowTooltip(int visibleIndex, MailRow row, ItemTemplate? item)
    {
        // Mail rows deliberately keep their compact authored wording instead of adopting the
        // richer shared item body or its compact money channel. Freeze every value before the
        // deferred renderer is queued; the row and item caches remain mutable network state.
        bool hasItem = item is not null;
        string itemName = item?.Name ?? "";
        uint itemCount = row.ItemCount;
        string? enclosedAmount = row.Money > 0 ? FormatMoney(row.Money) : null;
        string? codAmount = row.Money == 0 && row.Cod > 0 ? FormatMoney(row.Cod) : null;
        GameTooltipOwnerKey owner = new("item:mail-inbox", (ulong)visibleIndex);
        OfferPreservedSharedGameTooltipRenderer(owner, () =>
        {
            ImGui.BeginTooltip();
            if (hasItem)
            {
                ImGui.TextUnformatted(itemName);
                if (itemCount > 1) ImGui.TextUnformatted($"Count: {itemCount}");
            }
            if (enclosedAmount is not null)
            {
                if (hasItem) ImGui.Separator();
                ImGui.TextUnformatted("Enclosed amount");
                ImGui.TextUnformatted(enclosedAmount);
            }
            else if (codAmount is not null)
            {
                if (hasItem) ImGui.Separator();
                ImGui.TextUnformatted("Cash on Delivery Amount:");
                ImGui.TextUnformatted(codAmount);
            }
            ImGui.EndTooltip();
        });
    }

    private bool DrawMailPageButton(ImDrawListPtr dl, string id, Vector2 min,
        bool previous, bool enabled, float s)
    {
        Vector2 size = new Vector2(32) * s;
        ImGui.SetCursorScreenPos(min);
        if (!enabled) ImGui.BeginDisabled();
        bool released = ImGui.InvisibleButton(id, size);
        bool clicked = enabled && released;
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
        string element = previous ? "InboxPrevPageButton" : "InboxNextPageButton";
        TraceMailControl(element, "InboxFrame", min, size, active, hovered, enabled,
            MailPanelClip(_mailFrameOrigin, s),
            $@"Interface\Buttons\UI-SpellbookIcon-{stem}-{state}");
        return clicked;
    }

    private static Vector4 MailPanelClip(Vector2 origin, float s) =>
        new(origin.X, origin.Y, origin.X + 384 * s, origin.Y + 512 * s);

    private static Vector4 MailContent(Vector2 min, Vector2 size) =>
        new(min.X, min.Y, min.X + size.X, min.Y + size.Y);

    private void DrawMailCompose(ImDrawListPtr dl, Vector2 origin, float s)
    {
        Vector4 clip = MailPanelClip(origin, s);
        GameText.DrawCentered(dl, "GameFontNormal", "Send Mail",
            origin + new Vector2(198, 26) * s, s);
        Vector2 stationeryMin = origin + new Vector2(21, 97) * s;
        DrawMailStationery(dl, stationeryMin, "STATIONERYTEST", s);
        TraceMailStationery("SendStationeryBackground", "SendMailScrollFrame", stationeryMin,
            "STATIONERYTEST", s, clip);
        DrawMailScrollRest(dl, origin + new Vector2(317, 97) * s, 257, s,
            "SendMailScrollFrame", "SendMailScroll", clip);
        DrawMailHorizontalBar(dl, origin + new Vector2(15, 350) * s, "SendMailFrame", s,
            clip);

        GameText.DrawRightAligned(dl, "GameFontNormal", "To:",
            origin + new Vector2(93, 50) * s, s);
        if (_mailFocusRecipient) { ImGui.SetKeyboardFocusHere(); _mailFocusRecipient = false; }
        bool recipientChanged = VanillaInputText(dl, "##mail-to", _mailRecipient,
            origin + new Vector2(105, 46) * s, new Vector2(122, 20), s);
        bool recipientActive = ImGui.IsItemActive(), recipientHovered = ImGui.IsItemHovered();
        if (recipientActive && (ImGui.IsKeyPressed(ImGuiKey.Enter) ||
                ImGui.IsKeyPressed(ImGuiKey.Tab)))
            _mailFocusSubject = true;
        if (recipientChanged) NormalizeMailBuffer(_mailRecipient, MailUiLaw.MaxRecipientLetters);
        GameText.DrawRightAligned(dl, "GameFontNormal", "Subject:",
            origin + new Vector2(93, 73) * s, s);
        if (_mailFocusSubject) { ImGui.SetKeyboardFocusHere(); _mailFocusSubject = false; }
        bool subjectChanged = VanillaInputText(dl, "##mail-subject", _mailSubject,
            origin + new Vector2(105, 69) * s, new Vector2(237, 20), s);
        bool subjectActive = ImGui.IsItemActive(), subjectHovered = ImGui.IsItemHovered();
        if (subjectActive && (ImGui.IsKeyPressed(ImGuiKey.Enter) ||
                ImGui.IsKeyPressed(ImGuiKey.Tab)))
            _mailFocusBody = true;
        if (subjectChanged) NormalizeMailBuffer(_mailSubject, MailUiLaw.MaxSubjectLetters);
        DrawMailBodyInput("##mail-body", _mailBody, origin + new Vector2(41, 107) * s,
            new Vector2(270, 200), s, MailUiLaw.MaxBodyLetters, _mailFocusBody);
        bool bodyActive = ImGui.IsItemActive(), bodyHovered = ImGui.IsItemHovered();
        _mailFocusBody = false;

        if (_uiParityArmed && _uiParityPanel == "mail")
        {
            TraceMailEdit("SendMailNameEditBox", origin + new Vector2(105, 46) * s,
                new Vector2(122, 20) * s, ReadBuffer(_mailRecipient),
                MailUiLaw.MaxRecipientLetters, recipientActive, recipientHovered, clip);
            TraceMailEdit("SendMailSubjectEditBox", origin + new Vector2(105, 69) * s,
                new Vector2(237, 20) * s, ReadBuffer(_mailSubject),
                MailUiLaw.MaxSubjectLetters, subjectActive, subjectHovered, clip);
            TraceMailEdit("SendMailBodyEditBox", origin + new Vector2(41, 107) * s,
                new Vector2(270, 200) * s, ReadBuffer(_mailBody), MailUiLaw.MaxBodyLetters,
                bodyActive, bodyHovered, clip);
        }

        GameText.DrawRightAligned(dl, "GameFontNormal", "Cost:",
            origin + new Vector2(269, 50) * s, s);
        uint costColor = PlayerMoney() < MailUiLaw.PostageCopper ? 0xff1a1aff : 0xffffffff;
        DrawMailMoneyDisplay(dl, MailUiLaw.PostageCopper, origin + new Vector2(338, 48) * s, s,
            costColor, "SendMailCost", clip);

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
            const string error = "COD amount is too high.";
            Vector2 errorMin = origin + new Vector2(79, 358) * s;
            GameText.Draw(dl, "GameFontRedSmall", error, errorMin, s);
            DrawMailCoin(dl, 0, new Vector2(errorMin.X +
                GameText.MeasureWidth("GameFontRedSmall", error, s) + s, errorMin.Y - s), s);
        }
        DrawMailMoneyDisplay(dl, PlayerMoney(), origin + new Vector2(183, 415) * s, s,
            0xffffffff, "SendMailMoneyFrame", clip);
        bool ready = MailUiLaw.CanSend(ReadBuffer(_mailRecipient), ReadBuffer(_mailSubject),
            _mailCodMode, amount, _mailAttachmentGuid != 0, _mailSendPending);
        Vector2 sendMin = origin + new Vector2(185, 410) * s;
        VanillaButton(dl, "##mail-send", "Send", sendMin, new Vector2(80, 22), s, ready);
        bool sendActive = ImGui.IsItemActive(), sendHovered = ImGui.IsItemHovered();
        bool sendReleased = MailReleasedCurrentItem(ready);
        TraceMailControl("SendMailMailButton", "SendMailFrame", sendMin,
            new Vector2(80, 22) * s, sendActive, sendHovered, ready, clip,
            !ready ? @"Interface\Buttons\UI-Panel-Button-Disabled" : sendActive ?
                @"Interface\Buttons\UI-Panel-Button-Down" : @"Interface\Buttons\UI-Panel-Button-Up");
        Vector2 cancelMin = origin + new Vector2(265, 410) * s;
        VanillaButton(dl, "##mail-cancel", "Cancel", cancelMin, new Vector2(80, 22), s);
        bool cancelActive = ImGui.IsItemActive(), cancelHovered = ImGui.IsItemHovered();
        bool cancelReleased = MailReleasedCurrentItem();
        TraceMailControl("SendMailCancelButton", "SendMailFrame", cancelMin,
            new Vector2(80, 22) * s, cancelActive, cancelHovered, true, clip,
            cancelActive ? @"Interface\Buttons\UI-Panel-Button-Down" :
                @"Interface\Buttons\UI-Panel-Button-Up");
        if (sendReleased) SendCurrentMail();
        if (cancelReleased) CloseMailSession();
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

    private void TraceMailStationery(string prefix, string parent, Vector2 min, string stem,
        float s, Vector4 clip)
    {
        if (!_uiParityArmed || _uiParityPanel != "mail") return;
        string root = prefix.StartsWith("Send", StringComparison.Ordinal) ?
            "SendMailFrame" : "OpenMailFrame";
        CollectUiParityDraw(parent, "Frame", min, new Vector2(296, 257) * s, root,
            new("", 0, "IMGUI_COMPOSED", "TOPLEFT", root, "TOPLEFT", 21, -97,
                ContentRect: MailContent(min, new Vector2(296, 257) * s), ClipRect: clip,
                ClipMask: "WINDOW_RECT", Visible: true, Enabled: false,
                InteractionState: "inert-scroll", Strata: "MEDIUM"));
        CollectUiParityDraw(prefix + "Left", "Texture", min, new Vector2(252, 256) * s,
            parent, new($@"Interface\Stationery\{stem}1", 0xffffffff, "BACKGROUND",
                "TOPLEFT", parent, "TOPLEFT", 0, 0, TexCoords: "0|0|1|1",
                ContentRect: MailContent(min, new Vector2(252, 256) * s), ClipRect: clip,
                ClipMask: "WINDOW_RECT", BlendMode: "BLEND", Visible: true,
                Strata: "MEDIUM"));
        Vector2 right = min + new Vector2(252, 0) * s;
        CollectUiParityDraw(prefix + "Right", "Texture", right, new Vector2(64, 256) * s,
            parent, new($@"Interface\Stationery\{stem}2", 0xffffffff, "BACKGROUND",
                "TOPLEFT", prefix + "Left", "TOPRIGHT", 0, 0, TexCoords: "0|0|1|1",
                ContentRect: MailContent(right, new Vector2(64, 256) * s), ClipRect: clip,
                ClipMask: "WINDOW_RECT", BlendMode: "BLEND", Visible: true,
                Strata: "MEDIUM"));
    }

    private void DrawMailHorizontalBar(ImDrawListPtr dl, Vector2 min, string parent, float s,
        Vector4 clip)
    {
        const string path = @"Interface\ClassTrainerFrame\UI-ClassTrainer-HorizontalBar";
        DrawMailArtUv(dl, path, min, new Vector2(256, 16), s, Vector2.Zero,
            new Vector2(1, .25f));
        Vector2 right = min + new Vector2(256, 0) * s;
        DrawMailArtUv(dl, path, right, new Vector2(75, 16), s, new Vector2(0, .25f),
            new Vector2(.29296875f, .5f));
        if (!_uiParityArmed || _uiParityPanel != "mail") return;
        CollectUiParityDraw(parent + "HorizontalBarLeft", "TextureUv", min,
            new Vector2(256, 16) * s, parent,
            new(path, 0xffffffff, "ARTWORK", "TOPLEFT", parent, "TOPLEFT", 15, -350,
                TexCoords: "0|0|1|0.25",
                ContentRect: MailContent(min, new Vector2(256, 16) * s), ClipRect: clip,
                ClipMask: "WINDOW_RECT", BlendMode: "BLEND", Strata: "MEDIUM"));
        CollectUiParityDraw(parent + "HorizontalBarRight", "TextureUv", right,
            new Vector2(75, 16) * s, parent,
            new(path, 0xffffffff, "ARTWORK", "LEFT", parent + "HorizontalBarLeft", "RIGHT",
                0, 0, TexCoords: "0|0.25|0.29296875|0.5",
                ContentRect: MailContent(right, new Vector2(75, 16) * s),
                ClipRect: clip, ClipMask: "WINDOW_RECT", BlendMode: "BLEND",
                Strata: "MEDIUM"));
    }

    private void DrawMailScrollRest(ImDrawListPtr dl, Vector2 right, float height, float s,
        string parent, string prefix, Vector4 clip)
    {
        const string track = @"Interface\PaperDollInfoFrame\UI-Character-ScrollBar";
        const string up = @"Interface\Buttons\UI-ScrollBar-ScrollUpButton-Disabled";
        const string down = @"Interface\Buttons\UI-ScrollBar-ScrollDownButton-Disabled";
        const string knob = @"Interface\Buttons\UI-ScrollBar-Knob";
        Vector2 topMin = right + new Vector2(-2, -5) * s;
        Vector2 bottomMin = right + new Vector2(-2, height - 104) * s;
        Vector2 upMin = right + new Vector2(6, 0) * s;
        Vector2 downMin = right + new Vector2(6, height - 16) * s;
        Vector2 knobMin = right + new Vector2(6, 16) * s;
        DrawMailArtUv(dl, track, topMin, new Vector2(31, 256), s,
            Vector2.Zero, new Vector2(.484375f, 1));
        DrawMailArtUv(dl, track, bottomMin, new Vector2(31, 106), s,
            new Vector2(.515625f, 0), new Vector2(1, .4140625f));
        Vector2 controlUv0 = new(.25f, .25f);
        Vector2 controlUv1 = new(.75f, .75f);
        DrawMailArtUv(dl, up, upMin, new Vector2(16), s, controlUv0, controlUv1);
        DrawMailArtUv(dl, down, downMin, new Vector2(16), s, controlUv0, controlUv1);
        DrawMailArtUv(dl, knob, knobMin, new Vector2(16), s, controlUv0, controlUv1);
        if (!_uiParityArmed || _uiParityPanel != "mail") return;
        bool openReader = prefix.StartsWith("Open", StringComparison.Ordinal);
        (string Element, string Path, Vector2 Min, Vector2 Size, string Uv, string State,
            string Layer, string Point, string RelativePoint, float OffsetX, float OffsetY)[] rows =
        [
            ($"{prefix}TrackTop", track, topMin, new Vector2(31, 256) * s,
                "0|0|0.484375|1", "inert-track", openReader ? "OVERLAY" : "ARTWORK",
                "TOPLEFT", "TOPRIGHT", -2, 5),
            ($"{prefix}TrackBottom", track, bottomMin, new Vector2(31, 106) * s,
                "0.515625|0|1|0.4140625", "inert-track", openReader ? "OVERLAY" : "ARTWORK",
                "BOTTOMLEFT", "BOTTOMRIGHT", -2, -2),
            ($"{prefix}UpButton", up, upMin, new Vector2(16) * s,
                "0.25|0.25|0.75|0.75", "disabled", "OVERLAY", "TOPLEFT", "TOPRIGHT", 6, 0),
            ($"{prefix}DownButton", down, downMin, new Vector2(16) * s,
                "0.25|0.25|0.75|0.75", "disabled", "OVERLAY", "BOTTOMLEFT", "BOTTOMRIGHT", 6, 0),
            ($"{prefix}Knob", knob, knobMin, new Vector2(16) * s,
                "0.25|0.25|0.75|0.75", "inert-top", "OVERLAY", "TOPLEFT", "TOPRIGHT", 6, -16)
        ];
        foreach (var row in rows)
            CollectUiParityDraw(row.Element, "TextureUv", row.Min, row.Size, parent,
                new(row.Path, 0xffffffff, row.Layer, row.Point, parent, row.RelativePoint,
                    row.OffsetX, row.OffsetY,
                    TexCoords: row.Uv, ContentRect: MailContent(row.Min, row.Size),
                    ClipRect: clip, ClipMask: "WINDOW_RECT", BlendMode: "BLEND",
                    Visible: true, Enabled: false, InteractionState: row.State,
                    Strata: "MEDIUM"));
    }

    private static void DrawMailBodyInput(string id, byte[] buffer, Vector2 min,
        Vector2 logicalSize, float s, int maximumLetters, bool focus)
    {
        string value = ReadBuffer(buffer);
        ImGui.SetCursorScreenPos(min);
        if (focus) ImGui.SetKeyboardFocusHere();
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.Border, Vector4.Zero);
        bool changed = ImGui.InputTextMultiline(id, ref value, (uint)buffer.Length,
            logicalSize * s);
        ImGui.PopStyleColor(4);
        if (changed) WriteMailBuffer(buffer, value, maximumLetters);
    }

    private void TraceMailEdit(string element, Vector2 min, Vector2 size, string text,
        int maximumLetters, bool active, bool hovered, Vector4 clip)
    {
        if (!_uiParityArmed || _uiParityPanel != "mail") return;
        float logicalScale = (clip.Z - clip.X) / 384f;
        CollectUiParityDraw(element, "EditBox", min, size, "SendMailFrame",
            new(@"Interface\Common\Common-Input-Border", 0xffffffff, "ARTWORK", "TOPLEFT",
                "SendMailFrame", "TOPLEFT", (min.X - clip.X) / logicalScale,
                -(min.Y - clip.Y) / logicalScale, TexCoords: "0|0|1|0.625",
                ContentRect: MailContent(min, size), ClipRect: clip,
                ClipMask: "WINDOW_RECT", BlendMode: "BLEND",
                Visible: true, Enabled: true,
                InteractionState: active ? "focused" : hovered ? "highlighted" :
                    $"normal;letters={text.EnumerateRunes().Count()};max={maximumLetters}",
                HitMin: min, HitMax: min + size, Strata: "MEDIUM"));
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
        uint count = MailAttachmentCount();
        if (count > 1)
            GameText.DrawRightAligned(dl, "NumberFontNormal", count.ToString(CultureInfo.InvariantCulture),
                min + new Vector2(32, 25) * s, s);
        ImGui.SetCursorScreenPos(min);
        bool released = ImGui.InvisibleButton("##mail-send-item", new Vector2(37) * s);
        bool hovered = ImGui.IsItemHovered(), active = ImGui.IsItemActive();
        if (hovered)
        {
            uint hi = _gameplayArt?.AdditiveHandle(@"Interface\Buttons\ButtonHilight-Square") ?? 0;
            if (hi != 0) dl.AddImage((nint)hi, min, min + new Vector2(37) * s);
            if (item is not null)
            {
                ItemTooltipBodySnapshot tooltipBody =
                    PrepareItemTooltipBodySnapshot(item, count);
                OfferPreparedItemTooltip(
                    new("item:mail-send-attachment", 0), tooltipBody);
            }
            else
            {
                GameTooltipOwnerKey tooltipOwner = new("item:mail-send-attachment", 0);
                OfferPreservedSharedGameTooltipRenderer(tooltipOwner, () =>
                {
                    ImGui.BeginTooltip();
                    ImGui.TextUnformatted("Attach an item to send.");
                    ImGui.EndTooltip();
                });
            }
        }
        if (_uiParityArmed && _uiParityPanel == "mail")
        {
            Vector4 clip = MailPanelClip(_mailFrameOrigin, s);
            CollectUiParityDraw("SendMailPackageButton", "Button", min, new Vector2(37) * s,
                "SendMailFrame", new("", 0, "IMGUI_HIT_TARGET", "TOPLEFT", "SendMailFrame",
                    "TOPLEFT", 30, -368,
                    ContentRect: MailContent(min, new Vector2(37) * s), ClipRect: clip,
                    ClipMask: "WINDOW_RECT", Visible: true, Enabled: true,
                    InteractionState: active ? "pushed" : hovered ? "highlighted" :
                        _mailAttachmentGuid != 0 ? "occupied" : "empty", HitMin: min,
                    HitMax: min + new Vector2(37) * s, Strata: "MEDIUM"));
            CollectUiParityDraw("SendMailPackageButtonNormalTexture", "TextureUv",
                min + new Vector2(-2) * s, new Vector2(39) * s, "SendMailPackageButton",
                new(@"Interface\Buttons\UI-Slot-Background", 0xffffffff, "BACKGROUND", "CENTER",
                    "SendMailPackageButton", "CENTER", 0, 0,
                    TexCoords: "0|0|0.640625|0.640625",
                    ContentRect: MailContent(min + new Vector2(-2) * s, new Vector2(39) * s),
                    ClipRect: clip,
                    ClipMask: "WINDOW_RECT", BlendMode: "BLEND", Strata: "MEDIUM"));
            if (item is not null)
                CollectUiParityDraw("SendMailPackageButtonIcon", "Texture", min,
                    new Vector2(37) * s, "SendMailPackageButton",
                    new(item.IconPath, 0xffffffff, "ARTWORK", "CENTER", "SendMailPackageButton",
                        "CENTER", 0, 0, TexCoords: "0|0|1|1",
                        ContentRect: MailContent(min, new Vector2(37) * s),
                        ClipRect: clip, ClipMask: "WINDOW_RECT", BlendMode: "BLEND",
                        Strata: "MEDIUM"));
        }
        if (released)
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
        bool goldActive = ImGui.IsItemActive();
        if (goldActive && (ImGui.IsKeyPressed(ImGuiKey.Enter) ||
                ImGui.IsKeyPressed(ImGuiKey.Tab)))
            _mailFocusSilver = true;
        DrawMailCoin(dl, 0, min + new Vector2(60, 4) * s, s);
        if (_mailFocusSilver) { ImGui.SetKeyboardFocusHere(); _mailFocusSilver = false; }
        VanillaInputInt(dl, "##mail-silver", ref _mailSilverInput,
            min + new Vector2(84, 0) * s, new Vector2(30, 20), s);
        bool silverActive = ImGui.IsItemActive();
        if (silverActive && (ImGui.IsKeyPressed(ImGuiKey.Enter) ||
                ImGui.IsKeyPressed(ImGuiKey.Tab)))
            _mailFocusCopper = true;
        DrawMailCoin(dl, 1, min + new Vector2(106, 4) * s, s);
        if (_mailFocusCopper) { ImGui.SetKeyboardFocusHere(); _mailFocusCopper = false; }
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
        bool released = ImGui.InvisibleButton(id, new Vector2(16) * s);
        bool clicked = enabled && released;
        bool active = enabled && ImGui.IsItemActive();
        bool hovered = enabled && ImGui.IsItemHovered();
        if (!enabled) ImGui.EndDisabled();
        if (hovered && (_gameplayArt?.AdditiveHandle(@"Interface\Buttons\UI-RadioButton") ?? 0)
                is uint highlight && highlight != 0)
            dl.AddImage((nint)highlight, min, min + new Vector2(16) * s,
                new Vector2(.5f, 0), new Vector2(.75f, 1));
        GameText.Draw(dl, "GameFontNormalSmall", caption, min + new Vector2(18, 2) * s,
            s, enabled ? null : 0xff808080);
        if (_uiParityArmed && _uiParityPanel == "mail")
        {
            string element = id.Contains("send-money", StringComparison.Ordinal) ?
                "SendMailSendMoneyButton" : "SendMailCODButton";
            Vector4 clip = MailPanelClip(_mailFrameOrigin, s);
            CollectUiParityDraw(element, "CheckButton", min, new Vector2(16) * s,
                "SendMailFrame", new(@"Interface\Buttons\UI-RadioButton", enabled ? 0xffffffff :
                    0xff808080, "IMGUI_HIT_TARGET", "TOPLEFT", "SendMailFrame", "TOPLEFT",
                    (min.X - clip.X) / s, -(min.Y - clip.Y) / s,
                    TexCoords: selected ? "0.25|0|0.5|1" : "0|0|0.25|1",
                    ContentRect: MailContent(min, new Vector2(16) * s), ClipRect: clip,
                    ClipMask: "WINDOW_RECT", BlendMode: "BLEND",
                    Visible: true, Enabled: enabled,
                    InteractionState: !enabled ? "disabled" : active ? "pushed" : hovered ?
                        "highlighted" : selected ? "checked" : "normal", HitMin: min,
                    HitMax: min + new Vector2(16) * s, Strata: "MEDIUM"));
            if (hovered)
                CollectUiParityDraw(element + "HighlightTexture", "HighlightTexture", min,
                    new Vector2(16) * s, element,
                    new(@"Interface\Buttons\UI-RadioButton", 0xffffffff, "HIGHLIGHT", "CENTER",
                        element, "CENTER", 0, 0, TexCoords: "0.5|0|0.75|1",
                        ContentRect: MailContent(min, new Vector2(16) * s), ClipRect: clip,
                        ClipMask: "WINDOW_RECT",
                        BlendMode: "ADD", Strata: "MEDIUM"));
        }
        if (clicked) click();
    }

    private void DrawMailMoneyDisplay(ImDrawListPtr dl, uint copper, Vector2 rightTop, float s,
        uint color, string parityElement, Vector4 clip)
    {
        IReadOnlyList<MailUiLaw.MoneyDenomination> denominations = MailUiLaw.Money(copper);
        float width = denominations.Sum(d =>
            GameText.MeasureWidth("NumberFontNormal", d.Value.ToString(CultureInfo.InvariantCulture), s) +
            13 * s) + Math.Max(0, denominations.Count - 1) * 4 * s;
        float x = rightTop.X - width;
        for (int index = 0; index < denominations.Count; index++)
        {
            MailUiLaw.MoneyDenomination denomination = denominations[index];
            string text = denomination.Value.ToString(CultureInfo.InvariantCulture);
            float numberWidth = GameText.MeasureWidth("NumberFontNormal", text, s);
            GameText.Draw(dl, "NumberFontNormal", text, new Vector2(x, rightTop.Y), s, color);
            x += numberWidth;
            Vector2 coinMin = new(x, rightTop.Y);
            DrawMailCoin(dl, denomination.Icon, coinMin, s);
            if (_uiParityArmed && _uiParityPanel == "mail")
                CollectUiParityDraw($"{parityElement}Coin{index + 1}", "Frame", coinMin,
                    new Vector2(13) * s, parityElement,
                    new(@"Interface\MoneyFrame\UI-MoneyIcons", 0xffffffff, "OVERLAY", "RIGHT",
                        parityElement, "RIGHT", 0, 0,
                        TexCoords: $"{denomination.Icon * .25f:R}|0|{(denomination.Icon + 1) * .25f:R}|1",
                        ContentRect: MailContent(coinMin, new Vector2(13) * s), ClipRect: clip,
                        ClipMask: "WINDOW_RECT",
                        BlendMode: "BLEND", Visible: true, Enabled: true,
                        InteractionState: $"value={denomination.Value};total={copper};min=0;max={uint.MaxValue}",
                        Strata: "MEDIUM"));
            x += 13 * s;
            if (index + 1 < denominations.Count) x += 4 * s;
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
        Vector2 origin = MailUiLaw.OpenMailOrigin(_mailFrameOrigin, s),
            size = new Vector2(384, 512) * s;
        Vector4 clip = new(origin.X, origin.Y, origin.X + size.X, origin.Y + size.Y);
        ImGui.SetNextWindowPos(origin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        bool began = ImGui.Begin("##open-mail", VanillaWindowFlags);
        ImGui.PopStyleVar(2);
        if (!began) { ImGui.End(); return; }
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        if (_uiParityArmed && _uiParityPanel == "mail")
            CollectUiParityDraw("OpenMailFrame", "Frame", origin, size, "InboxFrame",
                new("", 0, "IMGUI_HOST", "TOPLEFT", "InboxFrame", "TOPRIGHT", -10, 0,
                    ContentRect: clip, ClipRect: clip, ClipMask: "WINDOW_RECT", Visible: true,
                    Enabled: true, InteractionState: "open", HitMin: origin,
                    HitMax: origin + size, Strata: "MEDIUM"));
        Vector2 portraitMin = origin + new Vector2(9, 6) * s;
        DrawArt(dl, MailStationeryIcon(row), portraitMin, new Vector2(60), s);
        if (_uiParityArmed && _uiParityPanel == "mail")
            CollectUiParityDraw("OpenMailFrameIcon", "Texture", portraitMin,
                new Vector2(60) * s, "OpenMailFrame",
                new(MailStationeryIcon(row), 0xffffffff, "BACKGROUND", "TOPLEFT",
                    "OpenMailFrame", "TOPLEFT", 9, -6,
                    ContentRect: MailContent(portraitMin, new Vector2(60) * s), ClipRect: clip,
                    ClipMask: "WINDOW_RECT", BlendMode: "BLEND", Visible: true,
                    Strata: "MEDIUM"));
        DrawFourPieceShell(dl, origin, s,
            @"Interface\ClassTrainerFrame\UI-ClassTrainer-TopLeft",
            @"Interface\ClassTrainerFrame\UI-ClassTrainer-TopRight",
            @"Interface\MailFrame\UI-OpenMail-BotLeft",
            @"Interface\ClassTrainerFrame\UI-ClassTrainer-BotRight");
        TraceMailShell("OpenMailFrame", origin, s, clip,
            @"Interface\ClassTrainerFrame\UI-ClassTrainer-TopLeft",
            @"Interface\ClassTrainerFrame\UI-ClassTrainer-TopRight",
            @"Interface\MailFrame\UI-OpenMail-BotLeft",
            @"Interface\ClassTrainerFrame\UI-ClassTrainer-BotRight");
        GameText.DrawCentered(dl, "GameFontNormal", "Open Mail",
            origin + new Vector2(198, 24) * s, s);
        GameText.DrawRightAligned(dl, "GameFontHighlight", "From:",
            origin + new Vector2(114, 45) * s, s);
        GameText.Draw(dl, "GameFontNormal", MailSender(row),
            origin + new Vector2(119, 45) * s, s);
        GameText.DrawRightAligned(dl, "GameFontHighlight", "Subject:",
            origin + new Vector2(114, 65) * s, s);
        GameText.Draw(dl, "GameFontNormalSmall", row.Subject,
            origin + new Vector2(119, 69) * s, s);
        DrawMailHorizontalBar(dl, origin + new Vector2(15, 350) * s, "OpenMailFrame", s,
            clip);
        Vector2 stationeryMin = origin + new Vector2(21, 97) * s;
        string stationeryStem = MailStationeryStem(row.Stationery);
        DrawMailStationery(dl, stationeryMin, stationeryStem, s);
        TraceMailStationery("OpenStationeryBackground", "OpenMailScrollFrame", stationeryMin,
            stationeryStem, s, clip);
        DrawMailScrollRest(dl, origin + new Vector2(317, 97) * s, 257, s,
            "OpenMailScrollFrame", "OpenMailScroll", clip);
        string body = row.ItemTextId != 0 && _mailBodies.TryGetValue(row.ItemTextId, out string? text)
            ? text : "";
        DrawMailWrappedText(dl, ExpandQuestText(body), origin + new Vector2(31, 107) * s,
            276, 240, s);

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
                    MailStationeryIcon(row), 1, s, new("mail-open-letter", 0),
                    "Click to make a permanent\ncopy of this letter."))
                MakeMailPermanent(row.Id);
            slotX += 47;
        }
        if (package)
        {
            ItemTemplate? item = MailItem(row);
            string path = item?.IconPath ?? @"Interface\Icons\INV_Misc_QuestionMark";
            if (DrawOpenMailSlot(dl, "##mail-package", origin + new Vector2(slotX, 371) * s,
                    path, row.ItemCount, s, new("item:mail-open-package", 0), null, row, item))
                TakeMailItem(row.Id);
            slotX += 47;
        }
        if (money)
        {
            if (DrawOpenMailSlot(dl, "##mail-money", origin + new Vector2(slotX, 371) * s,
                    @"Interface\Icons\INV_Misc_Coin_01", 1, s,
                    new("mail-open-money", 0), FormatMoney(row.Money)))
                TakeMailMoney(row.Id);
        }

        bool canDelete = MailUiLaw.CanDelete(row.Type, row.Checked, package, row.Money);
        bool canReply = MailUiLaw.CanReply(row.Type, row.Checked, SenderResolved(row));
        Vector2 replyMin = origin + new Vector2(101, 410) * s;
        VanillaButton(dl, "##open-mail-reply", "Reply", replyMin,
            new Vector2(82, 22), s, canReply);
        bool replyActive = ImGui.IsItemActive(), replyHovered = ImGui.IsItemHovered();
        bool replyReleased = MailReleasedCurrentItem(canReply);
        TraceMailControl("OpenMailReplyButton", "OpenMailFrame", replyMin,
            new Vector2(82, 22) * s, replyActive, replyHovered, canReply, clip,
            !canReply ? @"Interface\Buttons\UI-Panel-Button-Disabled" : replyActive ?
                @"Interface\Buttons\UI-Panel-Button-Down" : @"Interface\Buttons\UI-Panel-Button-Up");
        Vector2 deleteMin = origin + new Vector2(183, 410) * s;
        VanillaButton(dl, "##open-mail-delete", canDelete ? "Delete" : "Return", deleteMin,
            new Vector2(82, 22), s);
        bool deleteActive = ImGui.IsItemActive(), deleteHovered = ImGui.IsItemHovered();
        bool deleteReleased = MailReleasedCurrentItem();
        TraceMailControl("OpenMailDeleteButton", "OpenMailFrame", deleteMin,
            new Vector2(82, 22) * s, deleteActive, deleteHovered, true, clip,
            deleteActive ? @"Interface\Buttons\UI-Panel-Button-Down" :
                @"Interface\Buttons\UI-Panel-Button-Up");
        Vector2 bottomCloseMin = origin + new Vector2(265, 410) * s;
        VanillaButton(dl, "##open-mail-close-bottom", "Close", bottomCloseMin,
            new Vector2(80, 22), s);
        bool bottomCloseActive = ImGui.IsItemActive(), bottomCloseHovered = ImGui.IsItemHovered();
        bool bottomCloseReleased = MailReleasedCurrentItem();
        TraceMailControl("OpenMailCancelButton", "OpenMailFrame", bottomCloseMin,
            new Vector2(80, 22) * s, bottomCloseActive, bottomCloseHovered, true, clip,
            bottomCloseActive ? @"Interface\Buttons\UI-Panel-Button-Down" :
                @"Interface\Buttons\UI-Panel-Button-Up");
        Vector2 close = origin + new Vector2(321, 9) * s;
        DrawImageButton(dl, "##open-mail-close", close, new Vector2(32) * s,
            @"Interface\Buttons\UI-Panel-MinimizeButton-Up",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Down",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");
        bool closeActive = ImGui.IsItemActive(), closeHovered = ImGui.IsItemHovered();
        bool closeClicked = MailReleasedCurrentItem();
        TraceMailControl("OpenMailCloseButton", "OpenMailFrame", close, new Vector2(32) * s,
            closeActive, closeHovered, true, clip,
            closeActive ? @"Interface\Buttons\UI-Panel-MinimizeButton-Down" :
                @"Interface\Buttons\UI-Panel-MinimizeButton-Up");
        ImGui.End();
        if (replyReleased) ReplyToMail(row);
        else if (deleteReleased) DeleteOpenMail(row);
        else if (bottomCloseReleased || closeClicked)
            CloseOpenMail(playSound: true, autoDelete: true);
    }

    private bool DrawOpenMailSlot(ImDrawListPtr dl, string id, Vector2 min, string iconPath,
        uint count, float s, GameTooltipOwnerKey tooltipOwner, string? tooltip,
        MailRow? row = null, ItemTemplate? item = null)
    {
        DrawArt(dl, @"Interface\Buttons\UI-EmptySlot", min - new Vector2(10.5f) * s,
            new Vector2(58), s);
        uint icon = _gameplayArt?.Handle(iconPath) ?? 0;
        if (icon != 0) dl.AddImage((nint)icon, min, min + new Vector2(37) * s);
        if (count > 1)
            GameText.DrawRightAligned(dl, "NumberFontNormal", count.ToString(),
                min + new Vector2(35, 25) * s, s);
        ImGui.SetCursorScreenPos(min);
        bool released = ImGui.InvisibleButton(id, new Vector2(37) * s,
            ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
        bool hovered = ImGui.IsItemHovered(), active = ImGui.IsItemActive();
        if (hovered)
        {
            uint hi = _gameplayArt?.AdditiveHandle(@"Interface\Buttons\ButtonHilight-Square") ?? 0;
            if (hi != 0) dl.AddImage((nint)hi, min, min + new Vector2(37) * s);
            if (item is not null && row is not null)
            {
                ItemTooltipBodySnapshot tooltipBody = PrepareItemTooltipBodySnapshot(
                    item, row.ItemCount, row.ItemDurability, row.ItemMaxDurability);
                if (row.Cod > 0)
                {
                    string codAmount = FormatMoney(row.Cod);
                    tooltipBody = AppendPreparedItemTooltipBody(tooltipBody,
                        PreparedItemTooltipSeparator(),
                        PreparedItemTooltipPlain("Cash on Delivery Amount:"),
                        PreparedItemTooltipPlain(codAmount));
                }
                OfferPreparedItemTooltip(tooltipOwner, tooltipBody);
            }
            else if (!string.IsNullOrEmpty(tooltip))
            {
                string tooltipText = tooltip;
                OfferPreservedSharedGameTooltipRenderer(tooltipOwner, () =>
                {
                    ImGui.BeginTooltip();
                    ImGui.TextUnformatted(tooltipText);
                    ImGui.EndTooltip();
                });
            }
        }
        if (_uiParityArmed && _uiParityPanel == "mail")
        {
            string element = id.Contains("copy", StringComparison.Ordinal) ? "OpenMailLetterButton" :
                id.Contains("package", StringComparison.Ordinal) ? "OpenMailPackageButton" :
                    "OpenMailMoneyButton";
            Vector4 clip = new(374 * s, 104 * s, 758 * s, 616 * s);
            CollectUiParityDraw(element, "Button", min, new Vector2(37) * s, "OpenMailFrame",
                new("", 0, "IMGUI_HIT_TARGET", "CENTER", "OpenMailAttachmentText", "RIGHT", 5,
                    0, ContentRect: MailContent(min, new Vector2(37) * s), ClipRect: clip,
                    ClipMask: "WINDOW_RECT", Visible: true,
                    Enabled: true, InteractionState: active ? "pushed" : hovered ? "highlighted" :
                        "normal", HitMin: min, HitMax: min + new Vector2(37) * s,
                    Strata: "MEDIUM"));
            CollectUiParityDraw(element + "Icon", "Texture", min, new Vector2(37) * s, element,
                new(iconPath, 0xffffffff, "BORDER", "CENTER", element, "CENTER", 0, 0,
                    TexCoords: "0|0|1|1",
                    ContentRect: MailContent(min, new Vector2(37) * s), ClipRect: clip,
                    ClipMask: "WINDOW_RECT", BlendMode: "BLEND", Strata: "MEDIUM"));
            if (hovered)
                CollectUiParityDraw(element + "HighlightTexture", "HighlightTexture", min,
                    new Vector2(37) * s, element,
                    new(@"Interface\Buttons\ButtonHilight-Square", 0xffffffff, "HIGHLIGHT",
                        "CENTER", element, "CENTER", 0, 0, TexCoords: "0|0|1|1",
                        ContentRect: MailContent(min, new Vector2(37) * s), ClipRect: clip,
                        ClipMask: "WINDOW_RECT",
                        BlendMode: "ADD", Strata: "MEDIUM"));
        }
        return released;
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
        Vector2 size = MailUiLaw.ConfirmationSize(s);
        Vector2 origin = MailUiLaw.ConfirmationOrigin(display, s);
        ImGui.SetNextWindowPos(origin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        ImGui.SetNextWindowFocus();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        bool began = ImGui.Begin("##mail-confirm", VanillaWindowFlags);
        ImGui.PopStyleVar(2);
        if (!began) { ImGui.End(); return; }
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        Vector4 clip = new(origin.X, origin.Y, origin.X + size.X, origin.Y + size.Y);
        if (_uiParityArmed && _uiParityPanel == "mail")
            CollectUiParityDraw("MailConfirmation", "Frame", origin, size, "MailFrame",
                new("", 0, "IMGUI_HOST", "CENTER", "UIParent", "TOP", 0, -128,
                    ContentRect: clip, ClipRect: clip,
                    ClipMask: "WINDOW_RECT", Visible: true,
                    Enabled: true, InteractionState: confirmation.Kind.ToString(), HitMin: origin,
                    HitMax: origin + size, Strata: "DIALOG"));
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
        // Frozen MailFrame names the shared StaticPopup money frames as intentionally inert;
        // confirmation content is text-only here as well.
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
                case MailConfirmationKind.DeleteMoney:
                    if (DeleteMail(confirmation.MailId, confirmed: true))
                        CloseOpenMail(playSound: true, autoDelete: false);
                    break;
                case MailConfirmationKind.SendMoney:
                    _mailSendPending = false;
                    SendCurrentMail(moneyConfirmed: true);
                    break;
            }
        }
        if (_uiParityArmed && _uiParityPanel == "mail") MarkUiParityFrameComplete();
    }

    private bool DrawMailPopupButton(ImDrawListPtr dl, string caption, Vector2 min, float s, string id)
    {
        Vector2 size = new Vector2(128, 20) * s;
        ImGui.SetCursorScreenPos(min);
        bool released = ImGui.InvisibleButton($"##mail-confirm-{id}", size);
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
        if (_uiParityArmed && _uiParityPanel == "mail")
        {
            Vector2 display = ImGui.GetIO().DisplaySize;
            Vector2 dialogSize = new Vector2(360, 96) * s;
            Vector2 dialogOrigin = new((display.X - dialogSize.X) * .5f, 128 * s);
            Vector4 clip = new(dialogOrigin.X, dialogOrigin.Y, dialogOrigin.X + dialogSize.X,
                dialogOrigin.Y + dialogSize.Y);
            CollectUiParityDraw("MailConfirmation" + caption + "Button", "Button", min, size,
                "MailConfirmation", new("", 0, "IMGUI_HIT_TARGET", "TOPLEFT",
                    "MailConfirmation", "TOPLEFT", id == "accept" ? 48 : 184, -68,
                    ContentRect: MailContent(min, size), ClipRect: clip,
                    ClipMask: "WINDOW_RECT", Visible: true,
                    Enabled: true, InteractionState: active ? "pushed" : hovered ? "highlighted" :
                        "normal", HitMin: min, HitMax: min + size, Strata: "DIALOG"));
        }
        return released;
    }
}
