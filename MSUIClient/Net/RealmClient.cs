using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace MSUIClient.Net;

// The realmd (login) protocol — 1.12.1 login protocol version 3. Ported from
// benilla-protocol/src/auth.rs + lib.rs::logon. Login packets are NOT
// header-encrypted and each is self-delimiting, read in request/response order.
// Opcodes: CMD_AUTH_LOGON_CHALLENGE = 0x00, CMD_AUTH_LOGON_PROOF = 0x01,
// CMD_REALM_LIST = 0x10.
//
// NOTE: like benilla, this sends a zero crc_hash in the logon proof (there is no
// WoW.exe to checksum), so the realmd must run with StrictVersionCheck = 0 or it
// answers 0x09 WOW_FAIL_VERSION_INVALID at the proof stage regardless of account.

/// <summary>A realm as advertised by the auth server's realm list.</summary>
public sealed record RealmInfo(string Name, string Address, float Population, byte Characters, uint RealmType);

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
        0x09 => " version invalid — set realmd StrictVersionCheck = 0",
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

    /// <summary>Full SRP6 logon against a vanilla realmd, then fetch the realm list.</summary>
    public static LogonResult Logon(string host, int port, string username, string password, TimeSpan timeout)
    {
        using var tcp = new TcpClient();
        if (!tcp.ConnectAsync(host, port).Wait(timeout))
            throw new IOException($"realmd connect to {host}:{port} timed out");
        tcp.NoDelay = true;
        tcp.ReceiveTimeout = (int)timeout.TotalMilliseconds;
        tcp.SendTimeout = (int)timeout.TotalMilliseconds;
        Stream s = new BufferedStream(tcp.GetStream());

        string account = Srp6Client.Normalize(username); // uppercased ASCII, sent on the wire

        WriteLogonChallenge(s, account);
        s.Flush();
        var challenge = ReadChallengeReply(s);

        var srp = Srp6Client.ComputeChallenge(
            username, password, challenge.ServerPublicKey, challenge.Generator, challenge.LargeSafePrime, challenge.Salt);

        WriteLogonProof(s, srp.A, srp.M1);
        s.Flush();
        byte[] serverProof = ReadProofReply(s);
        if (!Srp6Client.VerifyServerProof(srp.A, srp.M1, srp.SessionKey, serverProof))
            throw new AuthRejectException(0x04); // proof mismatch = wrong password

        WriteRealmListRequest(s);
        s.Flush();
        var realms = ReadRealmList(s);

        return new LogonResult { SessionKey = srp.SessionKey, Realms = realms };
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

    private readonly record struct ChallengeReply(byte[] ServerPublicKey, byte Generator, byte[] LargeSafePrime, byte[] Salt);

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
        ReadN(s, 16);                 // crc_salt (unused)
        byte securityFlag = ReadU8(s);
        if ((securityFlag & 0x01) != 0) { ReadN(s, 4); ReadN(s, 16); } // PIN block (vmangos sends 0)

        return new ChallengeReply(serverPublicKey, generator, prime, salt);
    }

    // --- proof ----------------------------------------------------------------------------------

    private static void WriteLogonProof(Stream s, byte[] aLE, byte[] m1)
    {
        var p = new PacketWriter(74);
        p.WriteU8(CmdAuthLogonProof);
        p.WriteBytes(aLE);            // A (32)
        p.WriteBytes(m1);             // M1 (20)
        p.WriteBytes(new byte[20]);   // crc_hash (zeros — needs StrictVersionCheck=0 server-side)
        p.WriteU8(0);                 // number of telemetry keys
        p.WriteU8(0);                 // security flag = none
        WriteAll(s, p.AsSpan());
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
        ReadU16LE(s);           // size
        ReadU32LE(s);           // header padding
        int count = ReadU8(s);
        var realms = new List<RealmInfo>(count);
        for (int i = 0; i < count; i++)
        {
            uint type = ReadU32LE(s);
            ReadU8(s);          // flag
            string name = ReadCStr(s);
            string address = ReadCStr(s);
            float population = ReadF32(s);
            byte characters = ReadU8(s);
            ReadU8(s);          // category
            ReadU8(s);          // realm id
            realms.Add(new RealmInfo(name, address, population, characters, type));
        }
        ReadU16LE(s);           // footer padding
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
