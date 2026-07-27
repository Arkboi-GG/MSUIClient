using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace MSUIClient.Net;

// 1.12 world-packet header obfuscation, ported from
// benilla-srp/src/vanilla_header.rs. After CMSG_AUTH_SESSION every packet HEADER
// (not the body) runs through a stateful byte cipher keyed by the 40-byte SRP
// session key:
//     encrypt: c = (p ^ key[i]) + last_c
//     decrypt: p = (c - last_c) ^ key[i]
// where i cycles over the key and last_c is the previous CIPHERTEXT byte; both
// the index and last_c start at 0, and the encrypt/decrypt directions keep
// independent state. Client headers are 6 bytes (u16 BE size + u32 LE opcode);
// server headers are always 4 (u16 BE size + u16 LE opcode) — there is no
// 3-byte large-size variant in 1.12, big object updates go through
// SMSG_COMPRESSED_UPDATE_OBJECT instead.

public sealed class WorldHeaderCrypto
{
    private readonly byte[] _key;
    private byte _encIndex, _encPrev;
    private byte _decIndex, _decPrev;
    private bool _enabled;

    public WorldHeaderCrypto(byte[] sessionKey)
    {
        if (sessionKey.Length != Srp6Client.SessionKeyLength)
            throw new ArgumentException($"session key must be {Srp6Client.SessionKeyLength} bytes");
        _key = sessionKey;
    }

    /// <summary>
    /// Turn the cipher on. Called immediately after CMSG_AUTH_SESSION is sent: that packet's header
    /// and the preceding SMSG_AUTH_CHALLENGE go out/in as plaintext, and cipher state must not have
    /// advanced for them, so both directions correctly start from index 0 on the first real packet.
    /// </summary>
    public void Enable() => _enabled = true;

    public void EncryptInPlace(Span<byte> data)
    {
        if (!_enabled) return;
        for (int j = 0; j < data.Length; j++)
        {
            byte enc = (byte)((data[j] ^ _key[_encIndex]) + _encPrev);
            _encIndex = (byte)((_encIndex + 1) % Srp6Client.SessionKeyLength);
            data[j] = enc;
            _encPrev = enc;
        }
    }

    public void DecryptInPlace(Span<byte> data)
    {
        if (!_enabled) return;
        for (int j = 0; j < data.Length; j++)
        {
            byte enc = data[j];
            byte dec = (byte)((enc - _decPrev) ^ _key[_decIndex]);
            _decIndex = (byte)((_decIndex + 1) % Srp6Client.SessionKeyLength);
            _decPrev = enc;
            data[j] = dec;
        }
    }

    /// <summary>Build + encrypt a 6-byte client header (u16 BE size + u32 LE opcode).</summary>
    public byte[] EncryptClientHeader(ushort size, uint opcode)
    {
        var h = new byte[6];
        BinaryPrimitives.WriteUInt16BigEndian(h.AsSpan(0, 2), size);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(2, 4), opcode);
        EncryptInPlace(h);
        return h;
    }

    /// <summary>Decrypt a 4-byte server header in place, returning (size, opcode). body_len = size - 2.</summary>
    public (ushort Size, ushort Opcode) DecryptServerHeader(Span<byte> header4)
    {
        DecryptInPlace(header4);
        ushort size = BinaryPrimitives.ReadUInt16BigEndian(header4.Slice(0, 2));
        ushort opcode = BinaryPrimitives.ReadUInt16LittleEndian(header4.Slice(2, 2));
        return (size, opcode);
    }

    /// <summary>
    /// The CMSG_AUTH_SESSION proof digest: SHA1( UPPER(user) | 0u32 | clientSeed | serverSeed | K ).
    /// </summary>
    public static byte[] AuthProof(string usernameUpper, byte[] sessionKey, uint serverSeed, uint clientSeed)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        h.AppendData(Encoding.UTF8.GetBytes(usernameUpper));
        Span<byte> u32 = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(u32, 0u); h.AppendData(u32);
        BinaryPrimitives.WriteUInt32LittleEndian(u32, clientSeed); h.AppendData(u32);
        BinaryPrimitives.WriteUInt32LittleEndian(u32, serverSeed); h.AppendData(u32);
        h.AppendData(sessionKey);
        return h.GetHashAndReset();
    }

    /// <summary>A fresh random client seed for the world handshake.</summary>
    public static uint NewClientSeed()
    {
        Span<byte> b = stackalloc byte[4];
        RandomNumberGenerator.Fill(b);
        return BinaryPrimitives.ReadUInt32LittleEndian(b);
    }
}
