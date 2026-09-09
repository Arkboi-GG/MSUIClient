using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace MSUIClient.Net;

// The realmd (login) protocol — 1.12.1 login protocol version 3. Ported from
// benilla-protocol/src/auth.rs + lib.rs::logon. Login packets are NOT
// header-encrypted and each is self-delimiting, read in request/response order.
// Opcodes: CMD_AUTH_LOGON_CHALLENGE = 0x00, CMD_AUTH_LOGON_PROOF = 0x01,
// CMD_REALM_LIST = 0x10.
// The challenge's crc_salt is answered with the mangos-family build-5875 Windows integrity
// digest, allowing the ordinary desktop client to authenticate with StrictVersionCheck enabled.

/// <summary>A realm as advertised by the auth server's realm list.</summary>
public sealed record RealmInfo(string Name, string Address, float Population, byte Characters, uint RealmType,
    byte Flags = 0, byte Category = 0, byte WireId = 0)
{
    // Core sets OFFLINE for inaccessible security levels and incompatible builds too.
    public bool CanSelect => (Flags & 0x03) == 0 && !string.IsNullOrWhiteSpace(Name) &&
        !string.IsNullOrWhiteSpace(Address);
}

/// <summary>Result of a successful logon: the SRP6 session key (carried into the world server) + the realm list.</summary>
public sealed class LogonResult
{
    public required byte[] SessionKey { get; init; }   // 40 bytes
    public required List<RealmInfo> Realms { get; init; }
}

/// <summary>The server rejected the logon at the challenge or proof stage (WOW_FAIL_* code).</summary>
public sealed class AuthRejectException(byte code)
    : Exception($"server rejected logon (result 0x{code:X2}{Describe(code)})")
{
    public byte Code { get; } = code;

    private static string Describe(byte code) => code switch
    {
        0x04 => " unknown account / wrong password",
        0x09 => " wrong client version",
        0x0A => " version update required",
        _ => "",
    };
}

public static class RealmClient
{
    public const ushort ClientBuild = 5875;

    private const byte CmdAuthLogonChallenge = 0x00;
    private const byte CmdAuthLogonProof = 0x01;
    private const byte CmdRealmList = 0x10;

    // u32 tags, written little-endian (so the wire bytes spell the reversed tag the server expects).
    private const uint GameNameWow = 0x0057_6f57; // "WoW\0"
    private const uint PlatformX86 = 0x0078_3836;
    private const uint OsWindows   = 0x0057_696e;
    private const uint LocaleEnUs  = 0x656e_5553;

    // vmangos and cmangos both use this fixed challenge and this stored build-5875 Win/x86
    // integrity digest. The proof field is SHA1(A || H), where A is the exact 32 wire bytes.
    private static readonly byte[] MangosVersionChallenge =
        Convert.FromHexString("BAA31E99A00B2157FC373FB369CDD2F1");
    private static readonly byte[] IntegrityHash5875Windows =
        Convert.FromHexString("95EDB27C7823B363CBDDAB56A392E7CB73FCCA20");

    /// <summary>Full SRP6 logon against a vanilla realmd, then fetch the realm list.</summary>
    public static LogonResult Logon(string host, int port, string username, string password, TimeSpan timeout)
    {
        string account = Srp6Client.Normalize(username); // uppercased ASCII, sent on the wire
        _ = Srp6Client.Normalize(password);               // reject invalid credentials before dialing

        var dialed = DialEncodingUnambiguousChallenge(host, port, account, timeout);
        using TcpClient tcp = dialed.Tcp;
        using Stream s = dialed.Stream;
        ChallengeReply challenge = dialed.Challenge;

        var srp = Srp6Client.ComputeChallenge(
            username, password, challenge.ServerPublicKey, challenge.Generator, challenge.LargeSafePrime, challenge.Salt);

        WriteLogonProof(s, srp.A, srp.M1, challenge.CrcSalt);
        s.Flush();
        byte[] serverProof = ReadProofReply(s);
        if (!Srp6Client.VerifyServerProof(srp.A, srp.M1, srp.SessionKey, serverProof))
            throw new AuthRejectException(0x04); // proof mismatch = wrong password

        WriteRealmListRequest(s);
        s.Flush();
        var realms = ReadRealmList(s);

        return new LogonResult { SessionKey = srp.SessionKey, Realms = realms };
    }

    private static (TcpClient Tcp, Stream Stream, ChallengeReply Challenge)
        DialEncodingUnambiguousChallenge(string host, int port, string account, TimeSpan timeout)
    {
        for (int dial = 0; dial < RealmLogonLaw.MaximumChallengeDials; dial++)
        {
            var tcp = new TcpClient();
            Stream? stream = null;
            try
            {
                if (!tcp.ConnectAsync(host, port).Wait(timeout))
                    throw new IOException($"realmd connect to {host}:{port} timed out");
                tcp.NoDelay = true;
                tcp.ReceiveTimeout = (int)timeout.TotalMilliseconds;
                tcp.SendTimeout = (int)timeout.TotalMilliseconds;
                stream = new BufferedStream(tcp.GetStream());

                WriteLogonChallenge(stream, account);
                stream.Flush();
                ChallengeReply challenge = ReadChallengeReply(stream);
                if (RealmLogonLaw.KeepChallenge(dial, challenge.ServerPublicKey))
                    return (tcp, stream, challenge);
            }
            catch
            {
                stream?.Dispose();
                tcp.Dispose();
                throw;
            }

            // An ambiguous B is abandoned before a proof is sent. A new TCP connection is
            // required because realmd has already advanced this socket's authentication state.
            stream.Dispose();
            tcp.Dispose();
        }

        throw new InvalidOperationException("challenge dial loop did not yield a connection");
    }

    // --- challenge ------------------------------------------------------------------------------

    private static void WriteLogonChallenge(Stream s, string account)
    {
        var body = new PacketWriter(40 + account.Length);
        body.WriteU32(GameNameWow);
        body.WriteU8(1);   // version major
        body.WriteU8(12);  // version minor
        body.WriteU8(1);   // version patch
        body.WriteU16(ClientBuild);
        body.WriteU32(PlatformX86);
        body.WriteU32(OsWindows);
        body.WriteU32(LocaleEnUs);
        body.WriteU32(0);  // utc timezone offset
        body.WriteBytes(new byte[] { 127, 0, 0, 1 }); // client ip (network order)
        var acct = Encoding.ASCII.GetBytes(account);
        body.WriteU8((byte)acct.Length);
        body.WriteBytes(acct);

        var packet = new PacketWriter(4 + body.Length);
        packet.WriteU8(CmdAuthLogonChallenge);
        packet.WriteU8(3); // protocol version three
        packet.WriteU16((ushort)body.Length);
        packet.WriteBytes(body.AsSpan());
        WriteAll(s, packet.AsSpan());
    }

    private readonly record struct ChallengeReply(byte[] ServerPublicKey, byte Generator,
        byte[] LargeSafePrime, byte[] Salt, byte[] CrcSalt);

    private static ChallengeReply ReadChallengeReply(Stream s)
    {
        byte opcode = ReadU8(s);
        if (opcode != CmdAuthLogonChallenge)
            throw new IOException($"expected CMD_AUTH_LOGON_CHALLENGE (0x00), got 0x{opcode:X2}");
        ReadU8(s); // protocol version
        byte result = ReadU8(s);
        if (result != 0) throw new AuthRejectException(result);

        byte[] serverPublicKey = ReadN(s, 32);
        int genLen = ReadU8(s);
        byte[] gen = ReadN(s, genLen);
        byte generator = gen.Length > 0 ? gen[0] : throw new IOException("empty generator");
        int primeLen = ReadU8(s);
        byte[] prime = ReadN(s, primeLen);
        if (prime.Length != 32) throw new IOException($"safe prime was {primeLen} bytes, expected 32");
        byte[] salt = ReadN(s, 32);
        byte[] crcSalt = ReadN(s, 16);
        byte securityFlag = ReadU8(s);
        if ((securityFlag & 0x01) != 0) { ReadN(s, 4); ReadN(s, 16); } // PIN block (vmangos sends 0)

        return new ChallengeReply(serverPublicKey, generator, prime, salt, crcSalt);
    }

    // --- proof ----------------------------------------------------------------------------------

    private static void WriteLogonProof(Stream s, byte[] aLE, byte[] m1, byte[] crcSalt)
        => WriteAll(s, BuildLogonProof(aLE, m1, crcSalt));

    public static byte[] BuildLogonProof(ReadOnlySpan<byte> aLE, ReadOnlySpan<byte> m1,
        ReadOnlySpan<byte> crcSalt)
    {
        if (aLE.Length != 32) throw new ArgumentException("client public key must be 32 bytes");
        if (m1.Length != 20) throw new ArgumentException("client proof must be 20 bytes");
        var p = new PacketWriter(75);
        p.WriteU8(CmdAuthLogonProof);
        p.WriteBytes(aLE);            // A (32)
        p.WriteBytes(m1);             // M1 (20)
        p.WriteBytes(VersionProof(crcSalt, aLE)); // crc_hash = SHA1(A || build-integrity H)
        p.WriteU8(0);                 // number of telemetry keys
        p.WriteU8(0);                 // security flag = none
        return p.ToArray();
    }

    /// <summary>Answer realmd's version challenge for the desktop Windows build-5875 client.</summary>
    public static byte[] VersionProof(ReadOnlySpan<byte> crcSalt,
        ReadOnlySpan<byte> clientPublicKey)
    {
        if (crcSalt.Length != 16) throw new ArgumentException("crc salt must be 16 bytes");
        if (clientPublicKey.Length != 32)
            throw new ArgumentException("client public key must be 32 bytes");
        if (!crcSalt.SequenceEqual(MangosVersionChallenge)) return new byte[20];
        Span<byte> input = stackalloc byte[52];
        clientPublicKey.CopyTo(input);
        IntegrityHash5875Windows.CopyTo(input[32..]);
        return SHA1.HashData(input);
    }

    private static byte[] ReadProofReply(Stream s)
    {
        byte opcode = ReadU8(s);
        if (opcode != CmdAuthLogonProof)
            throw new IOException($"expected CMD_AUTH_LOGON_PROOF (0x01), got 0x{opcode:X2}");
        byte result = ReadU8(s);
        if (result != 0) throw new AuthRejectException(result);
        byte[] serverProof = ReadN(s, 20);
        ReadN(s, 4); // hardware survey id
        return serverProof;
    }

    // --- realm list -----------------------------------------------------------------------------

    private static void WriteRealmListRequest(Stream s)
    {
        var p = new PacketWriter(5);
        p.WriteU8(CmdRealmList);
        p.WriteU32(0); // padding
        WriteAll(s, p.AsSpan());
    }

    private static List<RealmInfo> ReadRealmList(Stream s)
    {
        byte opcode = ReadU8(s);
        if (opcode != CmdRealmList)
            throw new IOException($"expected CMD_REALM_LIST (0x10), got 0x{opcode:X2}");
        int size = ReadU16LE(s);
        return ParseRealmListPayload(ReadN(s, size));
    }

    public static List<RealmInfo> ParseRealmListPayload(byte[] body)
    {
        // The declared u16 frame length bounds strings and rows; malformed data cannot
        // eat the next auth response or wait indefinitely for a string terminator.
        using var s = new MemoryStream(body, writable: false);
        ReadU32LE(s);           // header padding
        int count = ReadU8(s);
        var realms = new List<RealmInfo>(count);
        for (int i = 0; i < count; i++)
        {
            uint type = ReadU32LE(s);
            byte flags = ReadU8(s);
            string name = ReadCStr(s);
            string address = ReadCStr(s);
            float population = ReadF32(s);
            byte characters = ReadU8(s);
            byte category = ReadU8(s);
            byte wireId = ReadU8(s); // This Core sends zero, so never use it as row identity.
            realms.Add(new RealmInfo(name, address, population, characters, type, flags, category, wireId));
        }
        ReadU16LE(s);           // footer padding
        if (s.Position != s.Length) throw new InvalidDataException("realm list has trailing data");
        return realms;
    }

    // --- stream helpers -------------------------------------------------------------------------

    private static void WriteAll(Stream s, ReadOnlySpan<byte> b) => s.Write(b);

    private static byte ReadU8(Stream s)
    {
        int b = s.ReadByte();
        if (b < 0) throw new EndOfStreamException("realmd stream closed");
        return (byte)b;
    }

    private static byte[] ReadN(Stream s, int n)
    {
        var b = new byte[n];
        s.ReadExactly(b);
        return b;
    }

    private static ushort ReadU16LE(Stream s) => BinaryPrimitives.ReadUInt16LittleEndian(ReadN(s, 2));
    private static uint ReadU32LE(Stream s) => BinaryPrimitives.ReadUInt32LittleEndian(ReadN(s, 4));
    private static float ReadF32(Stream s) => BinaryPrimitives.ReadSingleLittleEndian(ReadN(s, 4));

    private static string ReadCStr(Stream s)
    {
        var bytes = new List<byte>();
        int c;
        while ((c = s.ReadByte()) > 0) bytes.Add((byte)c);
        if (c < 0) throw new EndOfStreamException("realmd stream closed mid-string");
        return Encoding.UTF8.GetString(bytes.ToArray());
    }
}
