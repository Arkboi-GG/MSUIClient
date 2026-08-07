// MpqArchive.cs
//
// Managed MPQ v1 reader. WoW 1.12 archives are MPQ format v1 only, so this
// deliberately targets v1.
//
// Ports the READ path of StormLib (MIT, Ladislav Zezula â€” see LICENSE):
//   * header discovery                     SFileOpenArchive.cpp
//   * encrypted hash / block tables        SBaseFileTable.cpp + SBaseCommon.cpp
//   * sector offset table + per-sector     SFileReadFile.cpp (ReadMpqSectors,
//     decompress                             AllocateSectorOffsets)
//   * compression-method dispatch          SCompression.cpp (SCompDecompress2)
// Crypto is MpqCrypto (validated bit-exact against StormLib's vectors); PKWARE
// explode is PkwareExplode (validated against StormLib's own implode+explode).
//
// CODEC COVERAGE: stored, zlib (0x02), PKWARE (implode flag + mask 0x08),
// single-unit, uncompressed, encrypted. This covers everything vanilla 1.12
// uses for BLP/M2/DBC. Audio-only / late-format codecs (huffman, ADPCM, bzip2,
// LZMA, sparse) are not implemented and throw a clear NotSupportedException.
//
// The full read path (header find, encrypted tables, hash probing, single-unit,
// multi-sector-with-offset-table, stored-sector fallback, empty file) was
// round-tripped in a Python prototype (writer -> reader, byte-identical) before
// this was written.
//
// THREAD SAFETY: reads use RandomAccess positioned I/O (no shared file cursor),
// so concurrent ReadFile calls on one archive are safe. This is strictly better
// than the native path, which needed a global lock because StormLib mutates
// per-handle state on every call.

using System.Buffers.Binary;
using System.IO.Compression;
using Microsoft.Win32.SafeHandles;

namespace MSUIClient.Creator.Mpq;

public sealed class MpqArchive : IDisposable
{
    private const uint IdMpq = 0x1A51504D;   // 'MPQ\x1A'
    private const uint HashDeleted = 0xFFFFFFFE;
    private const uint HashFree = 0xFFFFFFFF;

    private const uint FlagImplode = 0x00000100;
    private const uint FlagCompress = 0x00000200;
    private const uint FlagEncrypted = 0x00010000;
    private const uint FlagSingleUnit = 0x01000000;
    private const uint FlagSectorCrc = 0x04000000;
    private const uint FlagExists = 0x80000000;
    private const uint CompressMask = 0x0000FF00;   // IMPLODE | COMPRESS | ...

    private const byte CompZlib = 0x02;
    private const byte CompPkware = 0x08;

    private readonly SafeFileHandle _handle;
    private readonly long _archivePos;
    private readonly uint _sectorSize;
    private readonly uint _hashCount;
    private readonly uint[] _hash;    // flattened: [n1, n2, localePlatform, blockIndex] * hashCount
    private readonly uint[] _block;   // flattened: [filePos, cSize, fSize, flags]      * blockCount

    public string ArchivePath { get; }

    private MpqArchive(string path, SafeFileHandle h, long archivePos, uint sectorSize,
                       uint hashCount, uint[] hash, uint[] block)
    {
        ArchivePath = path;
        _handle = h;
        _archivePos = archivePos;
        _sectorSize = sectorSize;
        _hashCount = hashCount;
        _hash = hash;
        _block = block;
    }

    /// <summary>Open a v1 MPQ. Returns null if no MPQ header is found.</summary>
    public static MpqArchive? Open(string path)
    {
        if (!BitConverter.IsLittleEndian)
            throw new PlatformNotSupportedException("MpqArchive requires a little-endian host.");

        // FileShare.ReadWrite | FileShare.Delete: this handle is held for the
        // reader's lifetime, and mounted archives (notably patch-4, the retexture
        // patch) get rebuilt on disk while open. A plain FileShare.Read makes .NET
        // take an advisory lock on Linux that ANY in-place write-open of the same
        // file collides with ("...being used by another process") â€” regardless of
        // which of the many services holds it. Sharing write+delete means no writer
        // or atomic rename ever contends with a mounted archive. Torn reads are
        // avoided by writers replacing the file atomically (temp + rename), so this
        // handle keeps reading the intact previous inode until it is remounted.
        var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read,
                                FileShare.ReadWrite | FileShare.Delete);
        try
        {
            long len = RandomAccess.GetLength(h);

            // Locate the MPQ header. StormLib scans on 0x200 boundaries so a
            // header that isn't at offset 0 (user-data prefix) is still found.
            long apos = -1;
            var probe = new byte[4];
            for (long p = 0; p + 32 <= len; p += 0x200)
            {
                ReadExact(h, probe, p);
                if (BinaryPrimitives.ReadUInt32LittleEndian(probe) == IdMpq) { apos = p; break; }
            }
            if (apos < 0) { h.Dispose(); return null; }

            var hdr = new byte[32];
            ReadExact(h, hdr, apos);
            // TMPQHeader v1 (StormLib.h): dwID, dwHeaderSize, dwArchiveSize,
            // wFormatVersion, wSectorSize, dwHashTablePos, dwBlockTablePos,
            // dwHashTableSize, dwBlockTableSize.
            ushort sectorShift = BinaryPrimitives.ReadUInt16LittleEndian(hdr.AsSpan(14));
            uint hashTablePos = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(16));
            uint blockTablePos = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(20));
            uint hashTableSize = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(24));
            uint blockTableSize = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(28));
            uint sectorSize = 0x200u << sectorShift;

            uint[] hash = ReadTable(h, apos + hashTablePos, hashTableSize, MpqCrypto.KeyHashTable);
            uint[] block = ReadTable(h, apos + blockTablePos, blockTableSize, MpqCrypto.KeyBlockTable);

            return new MpqArchive(path, h, apos, sectorSize, hashTableSize, hash, block);
        }
        catch
        {
            h.Dispose();
            throw;
        }
    }

    private static uint[] ReadTable(SafeFileHandle h, long pos, uint entryCount, uint key)
    {
        int dwords = checked((int)(entryCount * 4));
        var bytes = new byte[dwords * 4];
        ReadExact(h, bytes, pos);
        var u = new uint[dwords];
        for (int i = 0; i < dwords; i++)
            u[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i * 4));
        MpqCrypto.DecryptBlock(u, key);
        return u;
    }

    // Hash lookup: index from TableIndex hash, linear probe, terminate on a
    // FREE entry, skip DELETED, match on the NameA/NameB hash pair.
    private int FindBlockIndex(string name)
    {
        uint mask = _hashCount - 1;
        uint idx = MpqCrypto.HashString(name, MpqCrypto.HashTableIndex) & mask;
        uint n1 = MpqCrypto.HashString(name, MpqCrypto.HashNameA);
        uint n2 = MpqCrypto.HashString(name, MpqCrypto.HashNameB);

        for (uint n = 0; n < _hashCount; n++)
        {
            int e = (int)idx * 4;
            uint blockIndex = _hash[e + 3];
            if (blockIndex == HashFree) return -1;
            if (blockIndex != HashDeleted && _hash[e] == n1 && _hash[e + 1] == n2)
                return (int)blockIndex;
            idx = (idx + 1) & mask;
        }
        return -1;
    }

    /// <summary>Cheap existence check (hash table only, no data read).</summary>
    public bool HasFile(string name)
    {
        name = name.Replace('/', '\\');
        int bi = FindBlockIndex(name);
        if (bi < 0 || (long)bi * 4 + 3 >= _block.Length) return false;
        return (_block[bi * 4 + 3] & FlagExists) != 0;
    }

    /// <summary>Extract a file to a managed byte[]. Null if not present.</summary>
    public byte[]? ReadFile(string name)
    {
        name = name.Replace('/', '\\');
        int bi = FindBlockIndex(name);
        if (bi < 0) return null;

        int b = bi * 4;
        if (b + 3 >= _block.Length) return null;
        uint filePos = _block[b];
        uint cSize = _block[b + 1];
        uint fSize = _block[b + 2];
        uint flags = _block[b + 3];

        if ((flags & FlagExists) == 0) return null;

        long baseOff = _archivePos + filePos;
        if (fSize == 0) return Array.Empty<byte>();

        uint fileKey = 0;
        if ((flags & FlagEncrypted) != 0)
            fileKey = MpqCrypto.DecryptFileKey(PlainName(name), filePos, fSize, flags);

        // Single unit: the whole file is one (possibly compressed) blob.
        if ((flags & FlagSingleUnit) != 0)
        {
            var body = ReadBytes(baseOff, checked((int)cSize));
            if ((flags & FlagEncrypted) != 0) MpqCrypto.DecryptBytes(body, fileKey);
            if (cSize < fSize) return Decompress(body, (int)fSize, flags);
            return TrimTo(body, (int)fSize);
        }

        // Compressed, multi-sector: sector offset table then per-sector decode.
        if ((flags & CompressMask) != 0)
        {
            uint sectorCount = ((fSize - 1) / _sectorSize) + 1;
            int noff = (int)sectorCount + 1 + (((flags & FlagSectorCrc) != 0) ? 1 : 0);

            var offBytes = ReadBytes(baseOff, noff * 4);
            var offs = new uint[noff];
            for (int i = 0; i < noff; i++)
                offs[i] = BinaryPrimitives.ReadUInt32LittleEndian(offBytes.AsSpan(i * 4));
            if ((flags & FlagEncrypted) != 0) MpqCrypto.DecryptBlock(offs, fileKey - 1);

            var outBuf = new byte[fSize];
            int outPos = 0;
            for (uint i = 0; i < sectorCount; i++)
            {
                uint raw = offs[i + 1] - offs[i];
                int uncomp = (int)Math.Min(_sectorSize, fSize - i * _sectorSize);
                var seg = ReadBytes(baseOff + offs[i], (int)raw);
                if ((flags & FlagEncrypted) != 0) MpqCrypto.DecryptBytes(seg, fileKey + i);

                if (raw < (uint)uncomp)
                {
                    var dec = Decompress(seg, uncomp, flags);
                    Array.Copy(dec, 0, outBuf, outPos, uncomp);
                }
                else
                {
                    Array.Copy(seg, 0, outBuf, outPos, uncomp);   // stored sector
                }
                outPos += uncomp;
            }
            return outBuf;
        }

        // Uncompressed, multi-sector: contiguous raw data (per-sector decrypt).
        {
            var outBuf = ReadBytes(baseOff, checked((int)fSize));
            if ((flags & FlagEncrypted) != 0)
            {
                uint sectorCount = ((fSize - 1) / _sectorSize) + 1;
                for (uint i = 0; i < sectorCount; i++)
                {
                    int start = (int)(i * _sectorSize);
                    int uncomp = (int)Math.Min(_sectorSize, fSize - i * _sectorSize);
                    MpqCrypto.DecryptBytes(outBuf.AsSpan(start, uncomp), fileKey + i);
                }
            }
            return outBuf;
        }
    }

    // One (already decrypted) sector or single-unit blob -> raw bytes.
    private byte[] Decompress(byte[] body, int outSize, uint flags)
    {
        // IMPLODE flag (no COMPRESS mask byte): the whole blob is PKWARE-exploded.
        if ((flags & FlagImplode) != 0 && (flags & FlagCompress) == 0)
            return FitTo(PkwareExplode.Explode(body, 0, body.Length), outSize);

        // COMPRESS: first byte is the compression-method mask (SCompDecompress2).
        byte method = body[0];
        switch (method)
        {
            case CompZlib:
                {
                    // MPQ zlib is zlib-format (RFC1950, 2-byte header + adler),
                    // which ZLibStream handles directly.
                    using var ms = new MemoryStream(body, 1, body.Length - 1, writable: false);
                    using var z = new ZLibStream(ms, CompressionMode.Decompress);
                    var outBuf = new byte[outSize];
                    int total = 0, r;
                    while (total < outSize && (r = z.Read(outBuf, total, outSize - total)) > 0)
                        total += r;
                    // StormLib zero-fills a short sector rather than failing; the
                    // remainder of outBuf is already zero.
                    return outBuf;
                }

            case CompPkware:
                // PKWARE method inside multi-compression: skip the 0x08 mask byte.
                return FitTo(PkwareExplode.Explode(body, 1, body.Length - 1), outSize);

            default:
                throw new NotSupportedException(
                    $"MPQ compression byte 0x{method:X2} is not supported by the managed reader.");
        }
    }

    // Fit an explode result to the sector's expected size (StormLib zero-fills
    // a short result rather than failing).
    private static byte[] FitTo(byte[] data, int size)
    {
        if (data.Length == size) return data;
        var outBuf = new byte[size];
        Array.Copy(data, outBuf, Math.Min(size, data.Length));
        return outBuf;
    }

    private byte[] ReadBytes(long off, int len)
    {
        var buf = new byte[len];
        if (len > 0) ReadExact(_handle, buf, off);
        return buf;
    }

    private static void ReadExact(SafeFileHandle h, byte[] buf, long off)
    {
        int total = 0;
        while (total < buf.Length)
        {
            int r = RandomAccess.Read(h, buf.AsSpan(total), off + total);
            if (r == 0) throw new EndOfStreamException($"MPQ: unexpected EOF at offset {off + total}.");
            total += r;
        }
    }

    private static byte[] TrimTo(byte[] buf, int len)
    {
        if (buf.Length == len) return buf;
        var outBuf = new byte[len];
        Array.Copy(buf, outBuf, Math.Min(len, buf.Length));
        return outBuf;
    }

    private static string PlainName(string name)
    {
        int i = name.LastIndexOf('\\');
        return i >= 0 ? name[(i + 1)..] : name;
    }

    public void Dispose() => _handle.Dispose();
}