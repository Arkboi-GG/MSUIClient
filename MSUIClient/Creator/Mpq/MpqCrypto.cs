// MpqCrypto.cs
//
// Managed port of StormLib's MPQ cryptography (SBaseCommon.cpp), part of the
// effort to drop the native StormLib P/Invoke dependency. StormLib is MIT
// (Copyright (c) Ladislav Zezula) â€” see the bundled LICENSE.
//
// VALIDATED against StormLib's own documented constants (StormLib.h):
//   HashString("(hash table)",  FileKey) == 0xC3AF3770
//   HashString("(block table)", FileKey) == 0xEC83B3A3
//   StormBuffer[0]                        == 0x55C636E2
// (checked bit-exact in the Python prototype before this was written).
//
// All arithmetic is done in `uint` so it wraps mod 2^32 exactly like the C
// DWORD math. Do NOT let a `byte` or `int` sneak into these expressions â€”
// C# would promote to `long` and break the wrap. That is why `ch` is `uint`.

namespace MSUIClient.Creator.Mpq;

internal static class MpqCrypto
{
    // Hash "types" â€” offsets into StormBuffer (SBaseCommon.cpp / StormCommon.h).
    public const uint HashTableIndex = 0x000;
    public const uint HashNameA      = 0x100;
    public const uint HashNameB      = 0x200;
    public const uint HashFileKey    = 0x300;
    public const uint HashKey2Mix    = 0x400;

    // Table decryption keys (StormLib.h). These are HashString of the internal
    // pseudo-file names; hard-coded here exactly as StormLib does.
    public const uint KeyHashTable  = 0xC3AF3770; // HashString("(hash table)",  HashFileKey)
    public const uint KeyBlockTable = 0xEC83B3A3; // HashString("(block table)", HashFileKey)

    private const uint FlagFixKey = 0x00020000;   // MPQ_FILE_KEY_V2 / FIX_KEY

    private static readonly uint[] StormBuffer = BuildStormBuffer();
    private static readonly byte[] Upper       = BuildUpperTable();

    // InitializeMpqCryptography (SBaseCommon.cpp): the 0x500-entry key table.
    private static uint[] BuildStormBuffer()
    {
        var buf = new uint[0x500];
        uint seed = 0x00100001;
        for (uint index1 = 0; index1 < 0x100; index1++)
        {
            uint index2 = index1;
            for (int i = 0; i < 5; i++, index2 += 0x100)
            {
                seed = (seed * 125 + 3) % 0x2AAAAB;
                uint temp1 = (seed & 0xFFFF) << 0x10;
                seed = (seed * 125 + 3) % 0x2AAAAB;
                uint temp2 = (seed & 0xFFFF);
                buf[index2] = temp1 | temp2;
            }
        }
        return buf;
    }

    // AsciiToUpperTable (SBaseCommon.cpp): identity, except 'a'..'z' -> 'A'..'Z'
    // and '/' (0x2F) -> '\' (0x5C). This is the slash-converting variant that
    // HashString uses for file-name hashing.
    private static byte[] BuildUpperTable()
    {
        var t = new byte[256];
        for (int i = 0; i < 256; i++) t[i] = (byte)i;
        for (int c = 'a'; c <= 'z'; c++) t[c] = (byte)(c - 0x20);
        t[0x2F] = 0x5C;
        return t;
    }

    // HashString (SBaseCommon.cpp). MPQ names are ASCII.
    public static uint HashString(string name, uint hashType)
    {
        uint seed1 = 0x7FED7FED;
        uint seed2 = 0xEEEEEEEE;
        foreach (char cc in name)
        {
            uint ch = Upper[(byte)cc];                 // uint on purpose â€” see file header
            seed1 = StormBuffer[hashType + ch] ^ (seed1 + seed2);
            seed2 = ch + seed1 + seed2 + (seed2 << 5) + 3;
        }
        return seed1;
    }

    // DecryptMpqBlock (SBaseCommon.cpp), aligned path, operating on DWORDs.
    public static void DecryptBlock(Span<uint> data, uint key1)
    {
        uint key2 = 0xEEEEEEEE;
        for (int i = 0; i < data.Length; i++)
        {
            key2 += StormBuffer[HashKey2Mix + (key1 & 0xFF)];
            uint v = data[i] ^ (key1 + key2);
            data[i] = v;
            key1 = ((~key1 << 0x15) + 0x11111111) | (key1 >> 0x0B);
            key2 = v + key2 + (key2 << 5) + 3;
        }
    }

    // EncryptMpqBlock (SBaseCommon.cpp). Note the plaintext (pre-XOR) value
    // feeds key2, unlike decrypt which feeds the post-XOR value.
    public static void EncryptBlock(Span<uint> data, uint key1)
    {
        uint key2 = 0xEEEEEEEE;
        for (int i = 0; i < data.Length; i++)
        {
            key2 += StormBuffer[HashKey2Mix + (key1 & 0xFF)];
            uint v = data[i];
            data[i] = v ^ (key1 + key2);
            key1 = ((~key1 << 0x15) + 0x11111111) | (key1 >> 0x0B);
            key2 = v + key2 + (key2 << 5) + 3;
        }
    }

    // Decrypt a byte region interpreted as little-endian DWORDs. Only whole
    // DWORDs are processed (StormLib rounds the length down: dwLength >>= 2).
    // Endianness-explicit so it is correct regardless of host byte order.
    public static void DecryptBytes(Span<byte> bytes, uint key1)
    {
        int dwords = bytes.Length / 4;
        uint key2 = 0xEEEEEEEE;
        for (int i = 0; i < dwords; i++)
        {
            int o = i * 4;
            uint val = (uint)(bytes[o] | (bytes[o + 1] << 8) | (bytes[o + 2] << 16) | (bytes[o + 3] << 24));
            key2 += StormBuffer[HashKey2Mix + (key1 & 0xFF)];
            uint dec = val ^ (key1 + key2);
            bytes[o]     = (byte)dec;
            bytes[o + 1] = (byte)(dec >> 8);
            bytes[o + 2] = (byte)(dec >> 16);
            bytes[o + 3] = (byte)(dec >> 24);
            key1 = ((~key1 << 0x15) + 0x11111111) | (key1 >> 0x0B);
            key2 = dec + key2 + (key2 << 5) + 3;
        }
    }

    // DecryptFileKey (SBaseCommon.cpp). filePos is the block's dwFilePos.
    public static uint DecryptFileKey(string plainName, uint filePos, uint fileSize, uint flags)
    {
        uint key = HashString(plainName, HashFileKey);
        if ((flags & FlagFixKey) != 0)
            key = (key + filePos) ^ fileSize;
        return key;
    }
}
