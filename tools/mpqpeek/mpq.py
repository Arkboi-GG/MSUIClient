"""MPQ v1 reader - a Python port of this repo's Formats/Mpq/MpqArchive.cs and
MpqCrypto.cs. Read-only; stored, zlib and single-unit sectors only, which is
enough for interface.MPQ, fonts.MPQ and the DBCs.

NOT PART OF THE CLIENT. If this disagrees with the C#, the C# is right. See
tools/mpqpeek/README.md and SYSTEM_SETTINGS_UI.md section 7 for why it exists.

PKWARE-imploded files raise NotImplementedError by name - port PkwareExplode.cs
if you need them.
"""
import struct, zlib, sys

def _storm_buffer():
    buf = [0] * 0x500
    seed = 0x00100001
    for i1 in range(0x100):
        i2 = i1
        for _ in range(5):
            seed = (seed * 125 + 3) % 0x2AAAAB
            t1 = (seed & 0xFFFF) << 16
            seed = (seed * 125 + 3) % 0x2AAAAB
            t2 = seed & 0xFFFF
            buf[i2] = t1 | t2
            i2 += 0x100
    return buf

STORM = _storm_buffer()
UPPER = bytearray(range(256))
for c in range(ord('a'), ord('z') + 1):
    UPPER[c] = c - 0x20
UPPER[0x2F] = 0x5C

HASH_TABLE_INDEX, HASH_NAME_A, HASH_NAME_B, HASH_FILE_KEY, HASH_KEY2_MIX = 0x000, 0x100, 0x200, 0x300, 0x400
M = 0xFFFFFFFF


def hash_string(name, hash_type):
    s1, s2 = 0x7FED7FED, 0xEEEEEEEE
    for ch in name.encode('ascii', 'replace'):
        ch = UPPER[ch]
        s1 = STORM[hash_type + ch] ^ ((s1 + s2) & M)
        s2 = (ch + s1 + s2 + (s2 << 5) + 3) & M
    return s1


def decrypt_block(dwords, key1):
    key2 = 0xEEEEEEEE
    out = []
    for v in dwords:
        key2 = (key2 + STORM[HASH_KEY2_MIX + (key1 & 0xFF)]) & M
        d = v ^ ((key1 + key2) & M)
        out.append(d)
        key1 = ((((~key1 & M) << 0x15) & M) + 0x11111111 | (key1 >> 0x0B)) & M
        key2 = (d + key2 + (key2 << 5) + 3) & M
    return out


def decrypt_bytes(data, key1):
    n = len(data) // 4
    dw = list(struct.unpack_from('<%dI' % n, data, 0))
    dec = decrypt_block(dw, key1)
    out = bytearray(data)
    struct.pack_into('<%dI' % n, out, 0, *dec)
    return bytes(out)


ID_MPQ = 0x1A51504D
HASH_DELETED, HASH_FREE = 0xFFFFFFFE, 0xFFFFFFFF
F_IMPLODE, F_COMPRESS, F_ENCRYPTED = 0x100, 0x200, 0x10000
F_FIXKEY, F_SINGLE, F_SECTORCRC, F_EXISTS = 0x20000, 0x1000000, 0x4000000, 0x80000000
COMPRESS_MASK = 0xFF00


class Mpq:
    def __init__(self, path):
        self.f = open(path, 'rb')
        data = self.f
        length = data.seek(0, 2)
        apos = -1
        p = 0
        while p + 32 <= length:
            data.seek(p)
            if struct.unpack('<I', data.read(4))[0] == ID_MPQ:
                apos = p
                break
            p += 0x200
        if apos < 0:
            raise ValueError('no MPQ header')
        self.apos = apos
        data.seek(apos)
        hdr = data.read(32)
        sector_shift = struct.unpack_from('<H', hdr, 14)[0]
        hpos, bpos, hsize, bsize = struct.unpack_from('<IIII', hdr, 16)
        self.sector_size = 0x200 << sector_shift
        self.hash_count = hsize
        self.hash = self._table(apos + hpos, hsize, 0xC3AF3770)
        self.block = self._table(apos + bpos, bsize, 0xEC83B3A3)

    def _table(self, pos, count, key):
        self.f.seek(pos)
        raw = self.f.read(count * 16)
        dw = list(struct.unpack('<%dI' % (count * 4), raw))
        return decrypt_block(dw, key)

    def _find(self, name):
        name = name.replace('/', '\\')
        mask = self.hash_count - 1
        idx = hash_string(name, HASH_TABLE_INDEX) & mask
        n1 = hash_string(name, HASH_NAME_A)
        n2 = hash_string(name, HASH_NAME_B)
        for _ in range(self.hash_count):
            e = idx * 4
            bi = self.hash[e + 3]
            if bi == HASH_FREE:
                return -1
            if bi != HASH_DELETED and self.hash[e] == n1 and self.hash[e + 1] == n2:
                return bi
            idx = (idx + 1) & mask
        return -1

    def has(self, name):
        bi = self._find(name)
        return bi >= 0 and bool(self.block[bi * 4 + 3] & F_EXISTS)

    def _read(self, off, n):
        self.f.seek(off)
        return self.f.read(n)

    def _decompress(self, body, out_size, flags):
        if (flags & F_IMPLODE) and not (flags & F_COMPRESS):
            raise NotImplementedError('PKWARE implode')
        method = body[0]
        if method == 0x02:
            d = zlib.decompress(body[1:])
            return d[:out_size].ljust(out_size, b'\0')
        raise NotImplementedError('compression byte 0x%02X' % method)

    def read(self, name):
        name = name.replace('/', '\\')
        bi = self._find(name)
        if bi < 0:
            return None
        b = bi * 4
        file_pos, csize, fsize, flags = self.block[b:b + 4]
        if not (flags & F_EXISTS):
            return None
        base = self.apos + file_pos
        if fsize == 0:
            return b''

        key = 0
        if flags & F_ENCRYPTED:
            plain = name.rsplit('\\', 1)[-1]
            key = hash_string(plain, HASH_FILE_KEY)
            if flags & F_FIXKEY:
                key = ((key + file_pos) & M) ^ fsize

        if flags & F_SINGLE:
            body = self._read(base, csize)
            if flags & F_ENCRYPTED:
                body = decrypt_bytes(body, key)
            if csize < fsize:
                return self._decompress(body, fsize, flags)
            return body[:fsize]

        if flags & COMPRESS_MASK:
            sectors = (fsize - 1) // self.sector_size + 1
            noff = sectors + 1 + (1 if flags & F_SECTORCRC else 0)
            ob = self._read(base, noff * 4)
            offs = list(struct.unpack('<%dI' % noff, ob))
            if flags & F_ENCRYPTED:
                offs = decrypt_block(offs, (key - 1) & M)
            out = bytearray()
            for i in range(sectors):
                raw = offs[i + 1] - offs[i]
                unc = min(self.sector_size, fsize - i * self.sector_size)
                seg = self._read(base + offs[i], raw)
                if flags & F_ENCRYPTED:
                    seg = decrypt_bytes(seg, (key + i) & M)
                out += self._decompress(seg, unc, flags) if raw < unc else seg[:unc]
            return bytes(out)

        out = self._read(base, fsize)
        if flags & F_ENCRYPTED:
            sectors = (fsize - 1) // self.sector_size + 1
            o = bytearray(out)
            for i in range(sectors):
                s = i * self.sector_size
                unc = min(self.sector_size, fsize - s)
                o[s:s + unc] = decrypt_bytes(bytes(o[s:s + unc]), (key + i) & M)
            out = bytes(o)
        return out
