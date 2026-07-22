// MpqArchiveWriter.cs
//
// Managed MPQ v1 writer — replaces the native StormLib create/add path.
// Produces archives in the exact shape StormLib itself writes (and therefore
// the 1.12 client reads), following SFileCreateArchive.cpp / SFileAddFile.cpp:
//   * v1 header, sectored files, zlib per sector (store the sector raw if
//     compression does not shrink it — SCompCompress's own rule),
//   * a (listfile), and encrypted hash/block tables (MpqCrypto).
//
// Two things that broke the previous (War3Net) writer are handled explicitly:
//   * the hash table is sized to nextPow2(fileCount + 1), so there is ALWAYS at
//     least one free slot and a lookup for a file NOT in the archive terminates
//     (a saturated table is what made client lookups resolve to garbage);
//   * every queued file lands in both the hash and block tables — block count
//     equals file count by construction.
//
// Validated: the identical design was round-tripped in Python through the
// StormLib-crypto-validated reader (all files byte-identical, (listfile)
// complete, missing lookup terminates). The definitive gate remains the 1.12
// client loading a patch this produces.

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace MSUIClient.Formats.Mpq;

public static class MpqArchiveWriter
{
    public const int DefaultSectorSize = 0x1000;

    private const uint IdMpq        = 0x1A51504D;
    private const uint FlagCompress = 0x00000200;
    private const uint FlagExists   = 0x80000000;
    private const byte CompZlib     = 0x02;

    /// <summary>
    /// Build a complete v1 MPQ (with a (listfile)) into a byte[]. Names may use
    /// '/' or '\'; they are normalized to '\'.
    /// </summary>
    public static byte[] Build(IReadOnlyList<KeyValuePair<string, byte[]>> files,
                               int sectorSize = DefaultSectorSize)
    {
        // Normalize names; append the (listfile) as a real file.
        var norm = new List<(string name, byte[] data)>(files.Count + 1);
        foreach (var kv in files)
            norm.Add((kv.Key.Replace('/', '\\'), kv.Value));
        byte[] listFile = BuildListFile(norm);
        norm.Add(("(listfile)", listFile));

        using var ms = new MemoryStream();
        ms.Write(new byte[32], 0, 32);   // header placeholder

        var blocks = new List<(uint filePos, uint cSize, uint fSize, uint flags)>(norm.Count);
        foreach (var (name, data) in norm)
        {
            uint filePos = (uint)ms.Position;
            if (data.Length == 0)
            {
                blocks.Add((filePos, 0, 0, FlagExists));
                continue;
            }
            byte[] blob = BuildSectoredFile(data, sectorSize);
            blocks.Add((filePos, (uint)blob.Length, (uint)data.Length, FlagExists | FlagCompress));
            ms.Write(blob, 0, blob.Length);
        }

        uint hashSize = NextPow2AtLeast(norm.Count + 1);   // +1 => at least one free slot
        uint[] hash = BuildHashTable(norm, hashSize);

        uint[] block = new uint[blocks.Count * 4];
        for (int i = 0; i < blocks.Count; i++)
        {
            block[i * 4 + 0] = blocks[i].filePos;
            block[i * 4 + 1] = blocks[i].cSize;
            block[i * 4 + 2] = blocks[i].fSize;
            block[i * 4 + 3] = blocks[i].flags;
        }

        MpqCrypto.EncryptBlock(hash, MpqCrypto.KeyHashTable);
        MpqCrypto.EncryptBlock(block, MpqCrypto.KeyBlockTable);

        uint hashPos = (uint)ms.Position;  WriteUInts(ms, hash);
        uint blockPos = (uint)ms.Position; WriteUInts(ms, block);

        byte[] archive = ms.ToArray();
        WriteHeader(archive, sectorSize, hashPos, blockPos, hashSize, (uint)blocks.Count);
        return archive;
    }

    private static byte[] BuildListFile(List<(string name, byte[] data)> realFiles)
    {
        var sb = new StringBuilder();
        foreach (var (name, _) in realFiles)
            sb.Append(name).Append("\r\n");
        sb.Append("(listfile)").Append("\r\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    // File data = sector offset table (nsec+1 uint32) followed by the sectors.
    private static byte[] BuildSectoredFile(byte[] data, int sectorSize)
    {
        int nsec = (data.Length - 1) / sectorSize + 1;
        var sectors = new byte[nsec][];
        for (int i = 0; i < nsec; i++)
        {
            int off = i * sectorSize;
            int len = Math.Min(sectorSize, data.Length - off);
            var chunk = new byte[len];
            Array.Copy(data, off, chunk, 0, len);

            byte[] comp = ZlibSector(chunk);
            sectors[i] = comp.Length < len ? comp : chunk;   // store raw if not smaller
        }

        var offs = new uint[nsec + 1];
        offs[0] = (uint)((nsec + 1) * 4);
        for (int i = 0; i < nsec; i++)
            offs[i + 1] = offs[i] + (uint)sectors[i].Length;

        using var ms = new MemoryStream();
        var offBytes = new byte[offs.Length * 4];
        for (int i = 0; i < offs.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(offBytes.AsSpan(i * 4), offs[i]);
        ms.Write(offBytes, 0, offBytes.Length);
        for (int i = 0; i < nsec; i++)
            ms.Write(sectors[i], 0, sectors[i].Length);
        return ms.ToArray();
    }

    // [0x02][zlib-format deflate] — matches StormLib and the reader's ZLibStream.
    private static byte[] ZlibSector(byte[] chunk)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(CompZlib);
        using (var z = new ZLibStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
            z.Write(chunk, 0, chunk.Length);
        return ms.ToArray();
    }

    private static uint[] BuildHashTable(List<(string name, byte[] data)> files, uint hashSize)
    {
        uint mask = hashSize - 1;
        var ht = new uint[hashSize * 4];
        for (int i = 0; i < ht.Length; i++) ht[i] = 0xFFFFFFFF;   // all entries free

        for (int bi = 0; bi < files.Count; bi++)
        {
            string name = files[bi].name;
            uint idx = MpqCrypto.HashString(name, MpqCrypto.HashTableIndex) & mask;
            uint n1  = MpqCrypto.HashString(name, MpqCrypto.HashNameA);
            uint n2  = MpqCrypto.HashString(name, MpqCrypto.HashNameB);

            while (ht[idx * 4 + 3] != 0xFFFFFFFF)   // linear probe to a free slot
                idx = (idx + 1) & mask;

            ht[idx * 4 + 0] = n1;
            ht[idx * 4 + 1] = n2;
            ht[idx * 4 + 2] = 0;          // locale 0, platform 0
            ht[idx * 4 + 3] = (uint)bi;   // block index
        }
        return ht;
    }

    private static void WriteHeader(byte[] a, int sectorSize, uint hashPos, uint blockPos,
                                    uint hashSize, uint blockCount)
    {
        ushort shift = 0;
        for (int s = sectorSize; s > 0x200; s >>= 1) shift++;

        BinaryPrimitives.WriteUInt32LittleEndian(a.AsSpan(0),  IdMpq);
        BinaryPrimitives.WriteUInt32LittleEndian(a.AsSpan(4),  0x20);            // header size
        BinaryPrimitives.WriteUInt32LittleEndian(a.AsSpan(8),  (uint)a.Length);  // archive size
        BinaryPrimitives.WriteUInt16LittleEndian(a.AsSpan(12), 0);              // format v1
        BinaryPrimitives.WriteUInt16LittleEndian(a.AsSpan(14), shift);
        BinaryPrimitives.WriteUInt32LittleEndian(a.AsSpan(16), hashPos);
        BinaryPrimitives.WriteUInt32LittleEndian(a.AsSpan(20), blockPos);
        BinaryPrimitives.WriteUInt32LittleEndian(a.AsSpan(24), hashSize);
        BinaryPrimitives.WriteUInt32LittleEndian(a.AsSpan(28), blockCount);
    }

    private static void WriteUInts(MemoryStream ms, uint[] a)
    {
        var buf = new byte[a.Length * 4];
        for (int i = 0; i < a.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(i * 4), a[i]);
        ms.Write(buf, 0, buf.Length);
    }

    private static uint NextPow2AtLeast(int n)
    {
        uint s = 4;
        while (s < (uint)n) s <<= 1;
        return s;
    }
}
