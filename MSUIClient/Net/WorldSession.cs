using System.Buffers.Binary;
using System.IO.Compression;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace MSUIClient.Net;

public delegate void WirePacketObserver(
    bool outgoing, ushort opcode, ReadOnlySpan<byte> payload);
public delegate void SocketWriteObserver(
    ushort opcode, ReadOnlySpan<byte> packet, ReadOnlySpan<byte> sha256);

// The world server (mangosd) connection: the auth handshake + 1.12 header
// obfuscation, packet framing, and the outbound CMSG builders. Ported from
// benilla-protocol/src/world/{mod,session}.rs.
//
// Framing (world/mod.rs, verified vs vmangos):
//   send: [u16 BE size = body+4][u32 LE opcode] header (encrypted after AUTH_SESSION) + plaintext body
//   recv: [u16 BE size][u16 LE opcode] 4-byte header (decrypted), then body of (size - 2) bytes.
//
// Threading: ReceivePacket() is called only from the net worker thread (owns the
// decrypter). SendPacket() takes a lock so the game-loop thread can send movement
// while the worker reads — the encrypt/decrypt cipher halves keep independent state.

public sealed class WardenRequiredException()
    : Exception("this server requires the Warden anticheat, which this client does not implement");

public sealed class WorldAuthException(byte result)
    : Exception($"world auth rejected: result 0x{result:X2}") { public byte Result { get; } = result; }

/// <summary>A CMSG_CHAR_CREATE request (benilla-protocol CharCreateReq): identity + the five
/// appearance dials the create screen picked. outfit_id is always 0 on the wire.</summary>
public readonly record struct CharCreateParams(
    string Name, byte Race, byte Class, byte Gender,
    byte Skin, byte Face, byte HairStyle, byte HairColor, byte FacialHair);

public sealed class WorldSession : IDisposable
{
    private const byte AuthOk = 0x0C;

    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly WorldHeaderCrypto _crypto;
    private readonly string _account;          // uppercased
    private readonly object _sendLock = new();
    private readonly WirePacketObserver? _wireObserver;
    private readonly SocketWriteObserver? _socketWriteObserver;
    private volatile bool _closed;

    private WorldSession(TcpClient tcp, NetworkStream stream, WorldHeaderCrypto crypto,
        string account, WirePacketObserver? wireObserver, SocketWriteObserver? socketWriteObserver)
    {
        _tcp = tcp;
        _stream = stream;
        _crypto = crypto;
        _account = account;
        _wireObserver = wireObserver;
        _socketWriteObserver = socketWriteObserver;
    }

    public string Account => _account;

    /// <summary>Connect + complete the auth handshake, leaving header obfuscation enabled and the session ready for CharEnum.</summary>
    public static WorldSession Connect(string host, int port, string username, byte[] sessionKey,
        TimeSpan timeout, WirePacketObserver? wireObserver = null,
        SocketWriteObserver? socketWriteObserver = null)
    {
        var tcp = new TcpClient();
        try
        {
            if (!tcp.ConnectAsync(host, port).Wait(timeout))
                throw new IOException($"world connect to {host}:{port} timed out");
            tcp.NoDelay = true; // Nagle off — this is a latency-critical, small-packet stream
            tcp.ReceiveTimeout = (int)timeout.TotalMilliseconds; // handshake only; cleared after login
            var stream = tcp.GetStream();
            string account = Srp6Client.Normalize(username);
            var session = new WorldSession(tcp, stream, new WorldHeaderCrypto(sessionKey),
                account, wireObserver, socketWriteObserver);
            session.Handshake(sessionKey);
            return session;
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    private void Handshake(byte[] sessionKey)
    {
        // 1. SMSG_AUTH_CHALLENGE (plaintext) → server seed.
        var (op, body) = ReceivePacket();
        if (op != (ushort)Op.SMSG_AUTH_CHALLENGE)
            throw new IOException($"expected SMSG_AUTH_CHALLENGE, got opcode 0x{op:X4}");
        uint serverSeed = new PacketReader(body).ReadU32();

        // 2. Bind session key to (server seed, our client seed) → proof + header crypto.
        uint clientSeed = WorldHeaderCrypto.NewClientSeed();
        byte[] proof = WorldHeaderCrypto.AuthProof(_account, sessionKey, serverSeed, clientSeed);

        // 3. CMSG_AUTH_SESSION goes out with a PLAINTEXT header; obfuscation begins right after.
        SendPacket((ushort)Op.CMSG_AUTH_SESSION, BuildAuthSession(RealmClient.ClientBuild, _account, clientSeed, proof));
        _crypto.Enable();

        // 4. Wait for SMSG_AUTH_RESPONSE (first encrypted packet). Skip interleaved packets; a
        //    SMSG_WARDEN_DATA means the server runs Warden and will kick us ~30s later → fail now.
        while (true)
        {
            var (o, b) = ReceivePacket();
            if (o == (ushort)Op.SMSG_AUTH_RESPONSE)
            {
                byte result = b.Length > 0 ? b[0] : (byte)0xFF;
                if (result == AuthOk) break;
                throw new WorldAuthException(result);
            }
            if (o == (ushort)Op.SMSG_WARDEN_DATA)
                throw new WardenRequiredException();
        }

        // Handshake done: clear the read timeout so the streaming phase can block on a quiet world.
        _tcp.ReceiveTimeout = 0;
    }

    // --- framing --------------------------------------------------------------------------------

    /// <summary>Send a client packet (encrypted header + plaintext body). Thread-safe.</summary>
    public void SendPacket(ushort opcode, ReadOnlySpan<byte> body)
    {
        // size counts the 4-byte opcode + the body, but not the size field itself.
        ushort size = checked((ushort)(body.Length + 4));
        lock (_sendLock)
        {
            byte[] header = _crypto.EncryptClientHeader(size, opcode);
            // One write (header + body) so a movement report is one TCP segment.
            var packet = new byte[header.Length + body.Length];
            header.CopyTo(packet, 0);
            body.CopyTo(packet.AsSpan(header.Length));
            _stream.Write(packet, 0, packet.Length);
            _stream.Flush();
            ObserveSocketWrite(opcode, packet);
        }
        ObserveWire(outgoing: true, opcode, body);
    }

    /// <summary>Read + decrypt one server packet. Blocking; call only from the net worker thread.</summary>
    public (ushort Opcode, byte[] Body) ReceivePacket()
    {
        Span<byte> header = stackalloc byte[4];
        _stream.ReadExactly(header);
        var (size, opcode) = _crypto.DecryptServerHeader(header);
        int bodyLen = Math.Max(0, size - 2);
        byte[] body = new byte[bodyLen];
        if (bodyLen > 0) _stream.ReadExactly(body);
        ObserveWire(outgoing: false, opcode, body);
        return (opcode, body);
    }

    private void ObserveWire(bool outgoing, ushort opcode, ReadOnlySpan<byte> body)
    {
        try { _wireObserver?.Invoke(outgoing, opcode, body); }
        catch
        {
            // Instrumentation is observational: it may never fail a successful
            // send or prevent a decoded packet from reaching its handler.
        }
    }

    private void ObserveSocketWrite(ushort opcode, ReadOnlySpan<byte> packet)
    {
        if (_socketWriteObserver is null) return;
        try
        {
            byte[] sha256 = SHA256.HashData(packet);
            _socketWriteObserver(opcode, packet, sha256);
        }
        catch
        {
            // This callback runs only after Write + Flush both succeeded.
            // Evidence collection may never turn a successful socket write into
            // a failed gameplay send.
        }
    }

    // --- high-level requests --------------------------------------------------------------------

    /// <summary>Request the roster and return it (loops past interleaved account-data / tutorial packets).</summary>
    public List<Character> CharEnum()
    {
        SendPacket((ushort)Op.CMSG_CHAR_ENUM, ReadOnlySpan<byte>.Empty);
        while (true)
        {
            var (o, b) = ReceivePacket();
            if (o == (ushort)Op.SMSG_CHAR_ENUM)
            {
                var r = new PacketReader(b);
                int count = r.ReadU8();
                var list = new List<Character>(count);
                for (int i = 0; i < count; i++) list.Add(Character.Read(r));
                return list;
            }
            if (o == (ushort)Op.SMSG_WARDEN_DATA) throw new WardenRequiredException();
        }
    }

    /// <summary>CMSG_CHAR_CREATE (benilla create_character): send the request, skip interleaved
    /// packets, and return the SMSG_CHAR_CREATE result byte (0x2E = success).</summary>
    public byte CreateCharacter(in CharCreateParams p)
    {
        SendPacket((ushort)Op.CMSG_CHAR_CREATE, BuildCharCreate(p));
        while (true)
        {
            var (o, b) = ReceivePacket();
            if (o == (ushort)Op.SMSG_CHAR_CREATE) return b.Length > 0 ? b[0] : (byte)0xFF;
            if (o == (ushort)Op.SMSG_WARDEN_DATA) throw new WardenRequiredException();
        }
    }

    /// <summary>
    /// CMSG_CHAR_DELETE (guid, u64) -> the SMSG_CHAR_DELETE result byte. 0x39 = CHAR_DELETE_SUCCESS
    /// (vmangos Packets/Character.cpp); anything else is a refusal the caller should surface.
    /// Same shape as <see cref="CreateCharacter"/>: sent and awaited on the worker while parked at
    /// character select, so the roster can be re-enumerated straight after.
    /// </summary>
    public byte DeleteCharacter(ulong guid)
    {
        var w = new PacketWriter(8);
        w.WriteU64(guid);
        SendPacket((ushort)Op.CMSG_CHAR_DELETE, w.ToArray());
        while (true)
        {
            var (o, b) = ReceivePacket();
            if (o == (ushort)Op.SMSG_CHAR_DELETE) return b.Length > 0 ? b[0] : (byte)0xFF;
            if (o == (ushort)Op.SMSG_WARDEN_DATA) throw new WardenRequiredException();
        }
    }

    // CMSG_CHAR_CREATE body (benilla-protocol messages/client.rs char_create, byte-exact vs vmangos
    // Packets/Character.cpp): name (CString) + race, class, gender, skin, face, hairStyle, hairColor,
    // facialHair, outfitId(0). The server reads and ignores outfitId, recomputing start gear.
    private static byte[] BuildCharCreate(in CharCreateParams p)
    {
        byte[] name = Encoding.ASCII.GetBytes(p.Name ?? "");
        var w = new PacketWriter(name.Length + 12);
        w.WriteBytes(name);
        w.WriteU8(0);                 // NUL terminator
        w.WriteU8(p.Race);
        w.WriteU8(p.Class);
        w.WriteU8(p.Gender);
        w.WriteU8(p.Skin);
        w.WriteU8(p.Face);
        w.WriteU8(p.HairStyle);
        w.WriteU8(p.HairColor);
        w.WriteU8(p.FacialHair);
        w.WriteU8(0);                 // outfit_id (ignored by the server)
        return w.ToArray();
    }

    public void PlayerLogin(ulong guid) => SendFullGuid(Op.CMSG_PLAYER_LOGIN, guid);

    /// <summary>Declare the unit we control — vmangos drops all MSG_MOVE_* until this "confirmed mover" is set.</summary>
    public void SetActiveMover(ulong guid) => SendFullGuid(Op.CMSG_SET_ACTIVE_MOVER, guid);

    /// <summary>Acknowledge SMSG_TRIGGER_CINEMATIC as an immediate ESC-style skip.</summary>
    public void CompleteCinematic() =>
        SendPacket((ushort)Op.CMSG_COMPLETE_CINEMATIC, ReadOnlySpan<byte>.Empty);

    public void SetSelection(ulong guid) => SendFullGuid(Op.CMSG_SET_SELECTION, guid);
    public void AttackSwing(ulong guid) => SendFullGuid(Op.CMSG_ATTACKSWING, guid);
    public void AttackStop() => SendPacket((ushort)Op.CMSG_ATTACKSTOP, ReadOnlySpan<byte>.Empty);
    public void SendChatSay(string text, uint language)
    {
        var w = new PacketWriter(System.Text.Encoding.UTF8.GetByteCount(text) + 9);
        w.WriteU32(0); // CHAT_MSG_SAY
        w.WriteU32(language); // logged-in character's faction tongue; VMaNGOS rejects client Universal
        w.WriteCString(text);
        SendPacket((ushort)Op.CMSG_MESSAGECHAT, w.AsSpan());
    }
    public void SetSheathed(byte state)
    {
        var w = new PacketWriter(4); w.WriteU32(state);
        SendPacket((ushort)Op.CMSG_SETSHEATHED, w.AsSpan());
    }

    public void CastSpell(uint spellId, ulong targetGuid)
    {
        byte[] body = BuildCastSpellBody(spellId, targetGuid);
        SendPacket((ushort)Op.CMSG_CAST_SPELL, body);
    }

    public static byte[] BuildCastSpellBody(uint spellId, ulong targetGuid)
    {
        var w = new PacketWriter(targetGuid == 0 ? 6 : 14);
        w.WriteU32(spellId);
        w.WriteU16(targetGuid == 0 ? (ushort)0 : (ushort)0x0002); // TARGET_FLAG_SELF / UNIT
        if (targetGuid != 0) w.WritePackedGuid(targetGuid);
        return w.ToArray();
    }

    public void CancelCast(uint spellId)
    {
        var w = new PacketWriter(4); w.WriteU32(spellId);
        SendPacket((ushort)Op.CMSG_CANCEL_CAST, w.AsSpan());
    }

    public void CancelChannelling(uint spellId)
    {
        var w = new PacketWriter(4); w.WriteU32(spellId);
        SendPacket((ushort)Op.CMSG_CANCEL_CHANNELLING, w.AsSpan());
    }

    public void CancelAura(uint spellId)
    {
        SendPacket((ushort)Op.CMSG_CANCEL_AURA, BuildCancelAuraBody(spellId));
    }

    public static byte[] BuildCancelAuraBody(uint spellId)
    {
        var w = new PacketWriter(4); w.WriteU32(spellId); return w.ToArray();
    }

    public void CancelAutoRepeat()
        => SendPacket((ushort)Op.CMSG_CANCEL_AUTO_REPEAT_SPELL, ReadOnlySpan<byte>.Empty);

    public void SetActionButton(byte wireSlot, uint packed)
    {
        var w = new PacketWriter(5);
        w.WriteU8(wireSlot);
        w.WriteU32(packed);
        SendPacket((ushort)Op.CMSG_SET_ACTION_BUTTON, w.AsSpan());
    }

    public void SendMovement(Op moveOp, MovementInfo info)
    {
        var w = new PacketWriter(32);
        info.Write(w);
        SendPacket((ushort)moveOp, w.AsSpan());
    }

    public void Ping(uint sequence, uint lastRttMs)
    {
        var w = new PacketWriter(8);
        w.WriteU32(sequence);
        w.WriteU32(lastRttMs);
        SendPacket((ushort)Op.CMSG_PING, w.AsSpan());
    }

    public void NameQuery(ulong guid) => SendFullGuid(Op.CMSG_NAME_QUERY, guid);

    /// <summary>CMSG_CREATURE_QUERY — resolve a creature template's name/type. Body = entry(u32) + guid(u64).</summary>
    public void CreatureQuery(uint entry, ulong guid)
    {
        var w = new PacketWriter(12);
        w.WriteU32(entry);
        w.WriteU64(guid);
        SendPacket((ushort)Op.CMSG_CREATURE_QUERY, w.AsSpan());
    }

    public void ItemQuery(uint entry, ulong guid)
    {
        var w = new PacketWriter(12);
        w.WriteU32(entry); w.WriteU64(guid);
        SendPacket((ushort)Op.CMSG_ITEM_QUERY_SINGLE, w.AsSpan());
    }

    public void GossipHello(ulong guid) => SendFullGuid(Op.CMSG_GOSSIP_HELLO, guid);

    public void GossipSelect(ulong guid, uint listId, string? code = null)
    {
        var w = new PacketWriter(16 + (code?.Length ?? 0));
        w.WriteFullGuid(guid);
        w.WriteU32(listId);
        if (!string.IsNullOrEmpty(code)) w.WriteCString(code);
        SendPacket((ushort)Op.CMSG_GOSSIP_SELECT_OPTION, w.AsSpan());
    }

    public void TrainerList(ulong guid)
    {
        SendPacket((ushort)Op.CMSG_TRAINER_LIST, BuildTrainerListBody(guid));
    }

    public void TrainerBuySpell(ulong guid, uint spellId)
    {
        SendPacket((ushort)Op.CMSG_TRAINER_BUY_SPELL, BuildTrainerBuyBody(guid, spellId));
    }

    public void BankerActivate(ulong guid)
        => SendPacket((ushort)Op.CMSG_BANKER_ACTIVATE, BuildBankGuidBody(guid));

    public void BuyBankSlot(ulong guid)
        => SendPacket((ushort)Op.CMSG_BUY_BANK_SLOT, BuildBankGuidBody(guid));

    public static byte[] BuildBankGuidBody(ulong guid) => BuildGuidBody(guid);

    public void GetMailList(ulong mailboxGuid)
        => SendPacket((ushort)Op.CMSG_GET_MAIL_LIST, BuildMailGuidBody(mailboxGuid));

    public void SendMail(ulong mailboxGuid, string receiver, string subject, string body,
        ulong itemGuid, uint money, uint cod)
        => SendPacket((ushort)Op.CMSG_SEND_MAIL,
            BuildSendMailBody(mailboxGuid, receiver, subject, body, itemGuid, money, cod));

    public void MailTakeMoney(ulong mailboxGuid, uint mailId)
        => SendPacket((ushort)Op.CMSG_MAIL_TAKE_MONEY, BuildMailActionBody(mailboxGuid, mailId));

    public void MailTakeItem(ulong mailboxGuid, uint mailId)
        => SendPacket((ushort)Op.CMSG_MAIL_TAKE_ITEM, BuildMailActionBody(mailboxGuid, mailId));

    public void MailReturn(ulong mailboxGuid, uint mailId)
        => SendPacket((ushort)Op.CMSG_MAIL_RETURN_TO_SENDER, BuildMailActionBody(mailboxGuid, mailId));

    public void MailDelete(ulong mailboxGuid, uint mailId)
        => SendPacket((ushort)Op.CMSG_MAIL_DELETE, BuildMailActionBody(mailboxGuid, mailId));

    public static byte[] BuildMailGuidBody(ulong mailboxGuid) => BuildGuidBody(mailboxGuid);

    public static byte[] BuildMailActionBody(ulong mailboxGuid, uint mailId)
    { var w = new PacketWriter(12); w.WriteU64(mailboxGuid); w.WriteU32(mailId); return w.ToArray(); }

    public static byte[] BuildSendMailBody(ulong mailboxGuid, string receiver, string subject, string body,
        ulong itemGuid, uint money, uint cod)
    {
        var w = new PacketWriter(49 + receiver.Length + subject.Length + body.Length);
        w.WriteU64(mailboxGuid); w.WriteCString(receiver); w.WriteCString(subject); w.WriteCString(body);
        w.WriteU32(41); // normal stationery
        w.WriteU32(0);  // package
        w.WriteU64(itemGuid); w.WriteU32(money); w.WriteU32(cod);
        w.WriteU64(0); w.WriteU8(0); // build-5875 trailing constants
        return w.ToArray();
    }

    public void AuctionHello(ulong guid) => SendPacket((ushort)Op.MSG_AUCTION_HELLO, BuildAuctionGuidBody(guid));
    public void AuctionBrowse(ulong guid, uint page, string search)
        => SendPacket((ushort)Op.CMSG_AUCTION_LIST_ITEMS, BuildAuctionBrowseBody(guid, page, search));
    public void AuctionOwnerList(ulong guid, uint page)
        => SendPacket((ushort)Op.CMSG_AUCTION_LIST_OWNER_ITEMS, BuildAuctionPageBody(guid, page));
    public void AuctionBid(ulong guid, uint id, uint price)
        => SendPacket((ushort)Op.CMSG_AUCTION_PLACE_BID, BuildAuctionBidBody(guid, id, price));
    public void AuctionCancel(ulong guid, uint id)
        => SendPacket((ushort)Op.CMSG_AUCTION_REMOVE_ITEM, BuildAuctionPageBody(guid, id));
    public void AuctionSell(ulong guid, ulong item, uint bid, uint buyout, uint durationMinutes)
        => SendPacket((ushort)Op.CMSG_AUCTION_SELL_ITEM, BuildAuctionSellBody(guid, item, bid, buyout, durationMinutes));

    public static byte[] BuildAuctionGuidBody(ulong guid) => BuildGuidBody(guid);
    public static byte[] BuildAuctionPageBody(ulong guid, uint value)
    { var w = new PacketWriter(12); w.WriteU64(guid); w.WriteU32(value); return w.ToArray(); }
    public static byte[] BuildAuctionBidBody(ulong guid, uint id, uint price)
    { var w = new PacketWriter(16); w.WriteU64(guid); w.WriteU32(id); w.WriteU32(price); return w.ToArray(); }
    public static byte[] BuildAuctionSellBody(ulong guid, ulong item, uint bid, uint buyout, uint durationMinutes)
    { var w = new PacketWriter(32); w.WriteU64(guid); w.WriteU64(item); w.WriteU32(bid); w.WriteU32(buyout); w.WriteU32(durationMinutes); return w.ToArray(); }
    public static byte[] BuildAuctionBrowseBody(ulong guid, uint page, string search)
    {
        var w = new PacketWriter(42 + search.Length); w.WriteU64(guid); w.WriteU32(page); w.WriteCString(search);
        w.WriteU8(0); w.WriteU8(0); for (int i = 0; i < 4; i++) w.WriteU32(uint.MaxValue); w.WriteU8(0); return w.ToArray();
    }

    public static byte[] BuildTrainerListBody(ulong guid)
    { var w = new PacketWriter(8); w.WriteU64(guid); return w.ToArray(); }

    public static byte[] BuildTrainerBuyBody(ulong guid, uint spellId)
    { var w = new PacketWriter(12); w.WriteU64(guid); w.WriteU32(spellId); return w.ToArray(); }

    public void QuestgiverStatus(ulong guid) => SendPacket((ushort)Op.CMSG_QUESTGIVER_STATUS_QUERY, BuildGuidBody(guid));
    public void QuestgiverHello(ulong guid) => SendPacket((ushort)Op.CMSG_QUESTGIVER_HELLO, BuildGuidBody(guid));
    public void QuestgiverQuery(ulong guid, uint questId) => SendPacket((ushort)Op.CMSG_QUESTGIVER_QUERY_QUEST, BuildQuestGuidBody(guid, questId));
    public void QuestgiverAccept(ulong guid, uint questId) => SendPacket((ushort)Op.CMSG_QUESTGIVER_ACCEPT_QUEST, BuildQuestGuidBody(guid, questId));
    public void QuestgiverComplete(ulong guid, uint questId) => SendPacket((ushort)Op.CMSG_QUESTGIVER_COMPLETE_QUEST, BuildQuestGuidBody(guid, questId));
    public void QuestgiverRequestReward(ulong guid, uint questId) => SendPacket((ushort)Op.CMSG_QUESTGIVER_REQUEST_REWARD, BuildQuestGuidBody(guid, questId));
    public void QuestgiverChooseReward(ulong guid, uint questId, uint choice)
    { var w = new PacketWriter(16); w.WriteU64(guid); w.WriteU32(questId); w.WriteU32(choice); SendPacket((ushort)Op.CMSG_QUESTGIVER_CHOOSE_REWARD, w.AsSpan()); }
    public void QuestLogRemove(byte slot)
    { var w = new PacketWriter(1); w.WriteU8(slot); SendPacket((ushort)Op.CMSG_QUESTLOG_REMOVE_QUEST, w.AsSpan()); }

    public static byte[] BuildQuestGuidBody(ulong guid, uint questId)
    { var w = new PacketWriter(12); w.WriteU64(guid); w.WriteU32(questId); return w.ToArray(); }

    private static byte[] BuildGuidBody(ulong guid)
    { var w = new PacketWriter(8); w.WriteU64(guid); return w.ToArray(); }

    public void NpcTextQuery(uint textId, ulong guid)
    {
        var w = new PacketWriter(12);
        w.WriteU32(textId);
        w.WriteFullGuid(guid);
        SendPacket((ushort)Op.CMSG_NPC_TEXT_QUERY, w.AsSpan());
    }

    public void ListInventory(ulong guid) => SendFullGuid(Op.CMSG_LIST_INVENTORY, guid);
    public void BuyItem(ulong vendorGuid, uint itemId, byte count)
    {
        var w = new PacketWriter(14); w.WriteU64(vendorGuid); w.WriteU32(itemId);
        w.WriteU8(count); w.WriteU8(0); SendPacket((ushort)Op.CMSG_BUY_ITEM, w.AsSpan());
    }
    public void SellItem(ulong vendorGuid, ulong itemGuid, byte count)
    {
        var w = new PacketWriter(17); w.WriteU64(vendorGuid); w.WriteU64(itemGuid);
        w.WriteU8(count); SendPacket((ushort)Op.CMSG_SELL_ITEM, w.AsSpan());
    }
    public void BuybackItem(ulong vendorGuid, uint slot)
    {
        var w = new PacketWriter(12); w.WriteU64(vendorGuid); w.WriteU32(slot);
        SendPacket((ushort)Op.CMSG_BUYBACK_ITEM, w.AsSpan());
    }

    public void UseItem(byte bag, byte slot, byte spellSlot)
    {
        var w = new PacketWriter(5);
        w.WriteU8(bag); w.WriteU8(slot); w.WriteU8(spellSlot); w.WriteU16(0);
        SendPacket((ushort)Op.CMSG_USE_ITEM, w.AsSpan());
    }

    public void AutoEquipItem(byte bag, byte slot)
    {
        SendPacket((ushort)Op.CMSG_AUTOEQUIP_ITEM, BuildAutoEquipBody(bag, slot));
    }

    public void SwapInventoryItems(byte sourceSlot, byte destinationSlot)
    {
        SendPacket((ushort)Op.CMSG_SWAP_INV_ITEM, BuildSwapInventoryBody(sourceSlot, destinationSlot));
    }

    public void SwapItems(byte destinationBag, byte destinationSlot, byte sourceBag, byte sourceSlot)
    {
        SendPacket((ushort)Op.CMSG_SWAP_ITEM,
            BuildSwapItemsBody(destinationBag, destinationSlot, sourceBag, sourceSlot));
    }

    public static byte[] BuildAutoEquipBody(byte bag, byte slot) => [bag, slot];
    public static byte[] BuildSwapInventoryBody(byte sourceSlot, byte destinationSlot) => [sourceSlot, destinationSlot];
    public static byte[] BuildSwapItemsBody(byte destinationBag, byte destinationSlot, byte sourceBag, byte sourceSlot) =>
        [destinationBag, destinationSlot, sourceBag, sourceSlot];

    /// <summary>CMSG_LOOT — request a corpse's loot window. Body = the full 8-byte guid
    /// (vmangos Server/Packets/Loot.cpp:8-11; GameObjects use CMSG_GAMEOBJ_USE instead).</summary>
    public void Loot(ulong guid) => SendPacket((ushort)Op.CMSG_LOOT, BuildLootGuidBody(guid));

    /// <summary>CMSG_LOOT_MONEY — take the whole coin pile. Empty body.</summary>
    public void LootMoney() => SendPacket((ushort)Op.CMSG_LOOT_MONEY, ReadOnlySpan<byte>.Empty);

    /// <summary>CMSG_LOOT_RELEASE — close the loot session on the server. Full guid body.</summary>
    public void LootRelease(ulong guid) => SendPacket((ushort)Op.CMSG_LOOT_RELEASE, BuildLootGuidBody(guid));

    /// <summary>CMSG_AUTOSTORE_LOOT_ITEM — one u8: the 0-based WIRE loot slot; the server
    /// places the item into the first free bag slot (no destination on the wire).</summary>
    public void AutostoreLootItem(byte lootSlot)
    {
        SendPacket((ushort)Op.CMSG_AUTOSTORE_LOOT_ITEM, BuildAutostoreLootBody(lootSlot));
    }

    public static byte[] BuildLootGuidBody(ulong guid)
    {
        var w = new PacketWriter(8); w.WriteU64(guid); return w.ToArray();
    }

    public static byte[] BuildAutostoreLootBody(byte lootSlot) => [lootSlot];

    public void GuildRoster() => SendPacket((ushort)Op.CMSG_GUILD_ROSTER, ReadOnlySpan<byte>.Empty);
    public void GuildMotd(string text) => SendPacket((ushort)Op.CMSG_GUILD_MOTD, BuildCStringBody(text));
    public void GuildPromote(string name) => SendPacket((ushort)Op.CMSG_GUILD_PROMOTE, BuildCStringBody(name));
    public void GuildDemote(string name) => SendPacket((ushort)Op.CMSG_GUILD_DEMOTE, BuildCStringBody(name));
    public void GuildLeave() => SendPacket((ushort)Op.CMSG_GUILD_LEAVE, ReadOnlySpan<byte>.Empty);
    public void GuildDisband() => SendPacket((ushort)Op.CMSG_GUILD_DISBAND, ReadOnlySpan<byte>.Empty);
    public static byte[] BuildCStringBody(string value)
    {
        var w = new PacketWriter(Encoding.UTF8.GetByteCount(value) + 1);
        w.WriteCString(value); return w.ToArray();
    }

    public void WorldportAck() => SendPacket((ushort)Op.CMSG_MOVE_WORLDPORT_ACK, ReadOnlySpan<byte>.Empty);

    /// <summary>
    /// Client half of the build-5875 same-map teleport handshake. The server's
    /// MSG_MOVE_TELEPORT_ACK carries a packed mover guid, counter and destination
    /// MovementInfo; the reply is the full guid, the same counter, and the
    /// client's monotonic movement clock.
    /// </summary>
    public void TeleportAck(ulong guid, uint counter)
    {
        SendPacket((ushort)Op.MSG_MOVE_TELEPORT_ACK, BuildTeleportAckBody(guid, counter));
    }

    public static byte[] BuildTeleportAckBody(ulong guid, uint counter)
    {
        var w = new PacketWriter(16);
        w.WriteU64(guid);
        w.WriteU32(counter);
        w.WriteU32(MovementInfo.ClientUptimeMs());
        return w.ToArray();
    }

    private void SendFullGuid(Op op, ulong guid)
    {
        var w = new PacketWriter(8);
        w.WriteU64(guid);
        SendPacket((ushort)op, w.AsSpan());
    }

    // --- CMSG_AUTH_SESSION body -----------------------------------------------------------------

    private static byte[] BuildAuthSession(ushort build, string accountUpper, uint clientSeed, byte[] proof)
    {
        var w = new PacketWriter(48 + accountUpper.Length);
        w.WriteU32(build);
        w.WriteU32(0);                    // server id
        w.WriteBytes(Encoding.ASCII.GetBytes(accountUpper));
        w.WriteU8(0);                     // NUL
        w.WriteU32(clientSeed);
        w.WriteBytes(proof);              // 20-byte SHA1 digest
        w.WriteU32(0);                    // addon decompressed size = 0
        w.WriteBytes(EmptyAddonZlib());   // empty zlib block (what the client sends, vmangos accepts)
        return w.ToArray();
    }

    private static byte[] EmptyAddonZlib()
    {
        using var ms = new MemoryStream();
        using (var _ = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) { /* zero bytes in */ }
        return ms.ToArray();
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        try { _stream.Dispose(); } catch { /* ignore */ }
        try { _tcp.Dispose(); } catch { /* ignore */ }
    }
}
