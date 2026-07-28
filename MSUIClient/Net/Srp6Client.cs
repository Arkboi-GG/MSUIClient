using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace MSUIClient.Net;

// SRP6 client for WoW 1.12.1 logon, ported verbatim from benilla-srp/src/lib.rs
// (which is itself validated against gtker/wow_srp test vectors). WoW uses a
// lightly-customised SRP6: fixed 32-byte safe prime N, generator g = 7,
// multiplier k = 3, and a bespoke SHA1 "interleave" folding the shared secret S
// into the 40-byte session key. Only the client side is implemented. Every byte
// array is little-endian on the wire.

/// <summary>A/M1/K produced by <see cref="Srp6Client.ComputeChallenge"/>.</summary>
public sealed class Srp6Result
{
    /// <summary>Client public key A, 32 bytes LE — send in CMD_AUTH_LOGON_PROOF.</summary>
    public required byte[] A { get; init; }
    /// <summary>Client proof M1, 20 bytes — send in CMD_AUTH_LOGON_PROOF.</summary>
    public required byte[] M1 { get; init; }
    /// <summary>Session key K, 40 bytes — carried into the world server.</summary>
    public required byte[] SessionKey { get; init; }
}

public static class Srp6Client
{
    public const int SessionKeyLength = 40;
    public const byte Generator = 7;
    private const byte KValue = 3;

    /// <summary>WoW safe prime N, little-endian (as sent in CMD_AUTH_LOGON_CHALLENGE_Server).</summary>
    public static readonly byte[] LargeSafePrimeLE =
    {
        0xb7, 0x9b, 0x3e, 0x2a, 0x87, 0x82, 0x3c, 0xab, 0x8f, 0x5e, 0xbf, 0xbf, 0x8e, 0xb1, 0x01, 0x08,
        0x53, 0x50, 0x06, 0x29, 0x8b, 0x5b, 0xad, 0xbd, 0x5b, 0x53, 0xe1, 0x89, 0x5e, 0x64, 0x4b, 0x89,
    };

    /// <summary>ASCII-only, uppercased, 1..16 bytes — the form the 1.12 client hashes and sends.</summary>
    public static string Normalize(string s)
    {
        if (string.IsNullOrEmpty(s) || s.Length > 16)
            throw new ArgumentException("SRP string must be 1..16 characters");
        foreach (char c in s)
            if (c > 0x7F || char.IsControl(c))
                throw new ArgumentException($"character not allowed in SRP string: {c}");
        return s.ToUpperInvariant();
    }

    /// <summary>
    /// First client step: from the server challenge (B, g, N, salt) compute our public key A,
    /// the proof M1, and the session key. Send A + M1 in CMD_AUTH_LOGON_PROOF, then verify M2
    /// with <see cref="VerifyServerProof"/>.
    /// </summary>
    public static Srp6Result ComputeChallenge(
        string username, string password,
        byte[] serverPublicKeyLE, byte generator, byte[] largeSafePrimeLE, byte[] salt)
    {
        string user = Normalize(username);
        string pass = Normalize(password);

        BigInteger n = FromLE(largeSafePrimeLE);
        BigInteger g = generator;
        BigInteger k = KValue;

        // Random 32-byte private key a, A = g^a mod N.
        byte[] priv = new byte[32];
        RandomNumberGenerator.Fill(priv);
        BigInteger a = FromLE(priv);
        byte[] aLE = ToLE32(BigInteger.ModPow(g, a, n));

        BigInteger x = FromLE(CalcX(user, pass, salt));
        BigInteger u = FromLE(Sha1(aLE, serverPublicKeyLE)); // u = SHA1(A | B)

        // S = (B - k * (g^x mod N))^(a + u*x) mod N. The base can go negative, so
        // reduce it into [0, N) before the modpow (.NET's % follows the dividend's sign).
        BigInteger gx = BigInteger.ModPow(g, x, n);
        BigInteger b = FromLE(serverPublicKeyLE);
        BigInteger baseV = ((b - k * gx) % n + n) % n;
        BigInteger s = BigInteger.ModPow(baseV, a + u * x, n);
        byte[] sessionKey = Interleave(ToLE32(s));

        // M1 = SHA1( (SHA1(N) xor SHA1(g)) | SHA1(user) | salt | A | B | K ).
        byte[] m1 = Sha1(XorHash(generator, largeSafePrimeLE), Sha1(Utf8(user)), salt, aLE, serverPublicKeyLE, sessionKey);

        return new Srp6Result { A = aLE, M1 = m1, SessionKey = sessionKey };
    }

    /// <summary>Verify the server's proof M2 = SHA1(A | M1 | K). False usually means a wrong password.</summary>
    public static bool VerifyServerProof(byte[] aLE, byte[] m1, byte[] sessionKey, byte[] serverM2)
        => CryptographicOperations.FixedTimeEquals(Sha1(aLE, m1, sessionKey), serverM2);

    // --- internals -------------------------------------------------------------------------------

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    private static BigInteger FromLE(ReadOnlySpan<byte> le) => new(le, isUnsigned: true, isBigEndian: false);

    /// <summary>Magnitude of v as a zero-padded 32-byte LE array (v is always &lt; N &lt; 2^256).</summary>
    private static byte[] ToLE32(BigInteger v)
    {
        byte[] le = v.ToByteArray(isUnsigned: true, isBigEndian: false);
        var outb = new byte[32];
        Array.Copy(le, outb, Math.Min(le.Length, 32));
        return outb;
    }

    private static byte[] Sha1(params byte[][] parts)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        foreach (var p in parts) h.AppendData(p);
        return h.GetHashAndReset();
    }

    /// <summary>H( SHA1(N) XOR SHA1(g) ) — folded into M1.</summary>
    private static byte[] XorHash(byte generator, byte[] largeSafePrimeLE)
    {
        byte[] nHash = Sha1(largeSafePrimeLE);
        byte[] gHash = Sha1(new[] { generator });
        var outb = new byte[20];
        for (int i = 0; i < 20; i++) outb[i] = (byte)(nHash[i] ^ gHash[i]);
        return outb;
    }

    /// <summary>x = SHA1( salt | SHA1( UPPER(user) ":" UPPER(pass) ) ).</summary>
    private static byte[] CalcX(string user, string pass, byte[] salt)
    {
        byte[] inner = Sha1(Utf8(user), Utf8(":"), Utf8(pass));
        return Sha1(salt, inner);
    }

    /// <summary>
    /// WoW's SHA1_Interleave: fold the shared secret S (32 LE bytes) into the 40-byte session key.
    /// Trim low-order zero bytes to an even offset, split even/odd bytes, SHA1 each half, interleave.
    /// </summary>
    private static byte[] Interleave(byte[] s32)
    {
        int lead = 0;
        while (lead < s32.Length && s32[lead] == 0) lead++;
        if ((lead & 1) != 0) lead++;
        int len = s32.Length - lead;      // always even: lead is even and 32 is even
        int half = len / 2;

        var e = new byte[half];
        var f = new byte[half];
        for (int i = 0; i < half; i++)
        {
            e[i] = s32[lead + i * 2];
            f[i] = s32[lead + i * 2 + 1];
        }
        byte[] g = Sha1(e);
        byte[] h = Sha1(f);

        var outk = new byte[40];
        for (int i = 0; i < 20; i++)
        {
            outk[i * 2] = g[i];
            outk[i * 2 + 1] = h[i];
        }
        return outk;
    }
}
