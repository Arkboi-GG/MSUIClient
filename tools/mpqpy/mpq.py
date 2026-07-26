"""
Minimal MPQ v1 reader — a faithful Python port of MSUIClient/Formats/Mpq/*.cs,
which is itself a port of StormLib's read path.

Exists so format questions about the vanilla archives can be answered from real
bytes in this sandbox instead of guessed. Coverage matches the C#: stored, zlib
(0x02), PKWARE (0x08 / implode flag), single-unit, encrypted.
"""
import struct, zlib

HASH_TABLE_INDEX = 0x000
HASH_NAME_A      = 0x100
HASH_NAME_B      = 0x200
HASH_FILE_KEY    = 0x300
HASH_KEY2_MIX    = 0x400

KEY_HASH_TABLE  = 0xC3AF3770
KEY_BLOCK_TABLE = 0xEC83B3A3

FLAG_IMPLODE     = 0x00000100
FLAG_COMPRESS    = 0x00000200
FLAG_ENCRYPTED   = 0x00010000
FLAG_FIX_KEY     = 0x00020000
FLAG_SINGLE_UNIT = 0x01000000
FLAG_SECTOR_CRC  = 0x04000000
FLAG_EXISTS      = 0x80000000
COMPRESS_MASK    = 0x0000FF00

M32 = 0xFFFFFFFF


def _storm_buffer():
    buf = [0] * 0x500
    seed = 0x00100001
    for i1 in range(0x100):
        i2 = i1
        for _ in range(5):
            seed = (seed * 125 + 3) % 0x2AAAAB
            t1 = (seed & 0xFFFF) << 0x10
            seed = (seed * 125 + 3) % 0x2AAAAB
            t2 = seed & 0xFFFF
            buf[i2] = t1 | t2
            i2 += 0x100
    return buf


STORM = _storm_buffer()

_UPPER = bytearray(range(256))
for _c in range(ord('a'), ord('z') + 1):
    _UPPER[_c] = _c - 0x20
_UPPER[0x2F] = 0x5C


def hash_string(name, hash_type):
    s1, s2 = 0x7FED7FED, 0xEEEEEEEE
    for ch in name.encode('ascii', 'replace'):
        ch = _UPPER[ch]
        s1 = STORM[hash_type + ch] ^ ((s1 + s2) & M32)
        s2 = (ch + s1 + s2 + (s2 << 5) + 3) & M32
    return s1


def decrypt_block(data, key1):
    """data: list[int] of dwords, decrypted in place."""
    key2 = 0xEEEEEEEE
    for i in range(len(data)):
        key2 = (key2 + STORM[HASH_KEY2_MIX + (key1 & 0xFF)]) & M32
        v = data[i] ^ ((key1 + key2) & M32)
        data[i] = v
        key1 = ((((~key1 & M32) << 0x15) & M32) + 0x11111111 | (key1 >> 0x0B)) & M32
        key2 = (v + key2 + (key2 << 5) + 3) & M32
    return data


def decrypt_bytes(buf, key1):
    """buf: bytearray. Whole dwords only, matching StormLib."""
    n = len(buf) // 4
    key2 = 0xEEEEEEEE
    for i in range(n):
        o = i * 4
        val = buf[o] | (buf[o + 1] << 8) | (buf[o + 2] << 16) | (buf[o + 3] << 24)
        key2 = (key2 + STORM[HASH_KEY2_MIX + (key1 & 0xFF)]) & M32
        dec = val ^ ((key1 + key2) & M32)
        buf[o] = dec & 0xFF
        buf[o + 1] = (dec >> 8) & 0xFF
        buf[o + 2] = (dec >> 16) & 0xFF
        buf[o + 3] = (dec >> 24) & 0xFF
        key1 = ((((~key1 & M32) << 0x15) & M32) + 0x11111111 | (key1 >> 0x0B)) & M32
        key2 = (dec + key2 + (key2 << 5) + 3) & M32
    return buf


# ---------------------------------------------------------------- PKWARE DCL
# "explode" — PKWARE Data Compression Library. Same algorithm StormLib and the
# client's PkwareExplode.cs implement.
_DIST_BITS = [2,4,4,5,5,5,5,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,
              6,6,6,6,6,6,6,6,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,
              7,7,7,7,7,7,7,7,7,7,7,7,7,7,8,8]
_DIST_CODE = [0x03,0x0D,0x05,0x19,0x09,0x11,0x01,0x3E,0x1E,0x2E,0x0E,0x36,0x16,0x26,0x06,0x3A,
              0x1A,0x2A,0x0A,0x32,0x12,0x22,0x02,0x7C,0x3C,0x5C,0x1C,0x6C,0x2C,0x4C,0x0C,0x74,
              0x34,0x54,0x14,0x64,0x24,0x44,0x04,0x78,0x38,0x58,0x18,0x68,0x28,0x48,0x08,0xF0,
              0x70,0xB0,0x30,0xD0,0x50,0x90,0x10,0xE0,0x60,0xA0,0x20,0xC0,0x40,0x80,0x00]
_LEN_BITS  = [3,2,3,3,4,4,4,5,5,5,5,6,6,6,7,7]
_LEN_CODE  = [0x05,0x03,0x01,0x06,0x0A,0x02,0x0C,0x14,0x04,0x18,0x08,0x30,0x10,0x20,0x40,0x00]
_LEN_BASE  = [0x00,0x01,0x02,0x03,0x04,0x05,0x06,0x07,0x08,0x0A,0x0E,0x16,0x26,0x46,0x86,0x106]
_EXTRA_LEN = [0,0,0,0,0,0,0,0,1,2,3,4,5,6,7,8]

# ASCII (mode 1) literal Huffman tables, straight from StormLib's explode.c.
_CH_BITS_ASC = [
 11,124,8,7,28,7,188,13,76,4,10,8,12,10,12,10,8,23,8,9,7,6,7,8,7,6,55,8,23,24,12,11,
 7,9,11,12,6,7,22,5,7,8,8,6,11,14,11,20,12,8,8,6,10,12,12,11,11,13,10,10,9,10,10,9,
 10,10,10,10,10,10,10,10,10,10,10,10,10,10,10,10,10,10,10,10,10,10,10,10,10,10,10,10,10,10,10,10,
 10,10,10,10,10,10,10,10,10,10,10,10,10,10,10,10,10,10,10,10,10,10,10,10,10,10,10,10,10,10,10,10]
# Full 0x100-entry tables (bit lengths and codes) — StormLib ChBitsAsc/ChCodeAsc.
ChBitsAsc = [
 0x0B,0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,
 0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,
 0x08,0x0A,0x0C,0x0A,0x08,0x07,0x0C,0x0C,0x0A,0x0B,0x0B,0x0C,0x0A,0x08,0x08,0x09,
 0x0A,0x08,0x08,0x09,0x09,0x09,0x09,0x0A,0x0A,0x0A,0x0A,0x08,0x09,0x0B,0x0B,0x0C,
 0x0A,0x07,0x09,0x08,0x08,0x07,0x09,0x09,0x08,0x07,0x0A,0x0B,0x08,0x08,0x08,0x07,
 0x08,0x0B,0x08,0x07,0x07,0x08,0x09,0x0B,0x0A,0x0A,0x0B,0x09,0x0A,0x0B,0x0C,0x0C,
 0x0C,0x06,0x06,0x06,0x06,0x05,0x07,0x06,0x06,0x05,0x0B,0x09,0x06,0x07,0x06,0x06,
 0x07,0x0B,0x06,0x06,0x06,0x07,0x09,0x08,0x09,0x09,0x0B,0x08,0x0B,0x09,0x0C,0x08,
 0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,
 0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,
 0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,
 0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,0x0C,
 0x0D,0x0D,0x0D,0x0D,0x0D,0x0D,0x0D,0x0D,0x0D,0x0D,0x0D,0x0D,0x0D,0x0D,0x0D,0x0D,
 0x0D,0x0D,0x0D,0x0D,0x0D,0x0D,0x0D,0x0D,0x0D,0x0D,0x0D,0x0D,0x0D,0x0D,0x0D,0x0D,
 0x0D,0x0D,0x0D,0x0D,0x0D,0x0D,0x0D,0x0D,0x0D,0x0D,0x0D,0x0D,0x0D,0x0D,0x0D,0x0D,
 0x0D,0x0D,0x0D,0x0D,0x0D,0x0D,0x0D,0x0D,0x0D,0x0D,0x0D,0x0D,0x0D,0x0D,0x0D,0x0D]
ChCodeAsc = [
 0x0490,0x0FE0,0x07E0,0x0BE0,0x03E0,0x0DE0,0x05E0,0x09E0,0x01E0,0x00B8,0x0062,0x0EE0,0x06E0,0x0022,0x0AE0,0x02E0,
 0x0CE0,0x04E0,0x08E0,0x00E0,0x0F00,0x0700,0x0B00,0x0300,0x0D00,0x0500,0x0900,0x0100,0x0F80,0x0780,0x0B80,0x0380,
 0x0040,0x0288,0x0D80,0x0188,0x0018,0x0020,0x0590,0x0D90,0x0388,0x0788,0x0F88,0x0980,0x0188+0x0400,0x0010,0x0110,0x0090,
 0x0008,0x00A8,0x0028,0x0090+0x0100,0x0050,0x00D0,0x0030,0x0788+0x0400,0x0188+0x0800,0x0588,0x0988,0x00C8,0x0010+0x0100,0x0580,0x0180,0x0480,
 0x0388+0x0800,0x0060,0x0110+0x0100,0x0068,0x00E8,0x0000,0x0210+0x0100,0x0010+0x0200,0x0028+0x0100,0x0038,0x0708,0x0080,0x0018+0x0100,0x00A0,0x0058,0x0078,
 0x0038+0x0100,0x0480+0x0400,0x00B0,0x0004,0x0044,0x0008+0x0100,0x0290,0x0F08,0x0788+0x0800,0x0988+0x0400,0x0308,0x0190,0x0290+0x0100,0x0B08,0x0980+0x0400,0x0180+0x0400,
 0x0980+0x0800,0x0004+0x0004,0x0014,0x0034,0x000C,0x0002,0x0034+0x0004,0x002C,0x001C,0x0012,0x0708+0x0400,0x0090+0x0200,0x000C+0x0004,0x0018+0x0004,0x0024,0x0004+0x0008,
 0x0028+0x0004,0x0708+0x0800,0x001C+0x0004,0x000C+0x0008,0x0014+0x0004,0x0038+0x0004,0x0190+0x0100,0x0058+0x0100,0x0290+0x0200,0x0190+0x0200,0x0F08+0x0400,0x00B0+0x0100,0x0F08+0x0800,0x0290+0x0400,0x0308+0x0800,0x00E8+0x0100,
]


class Bits:
    __slots__ = ('d', 'p', 'b', 'n')

    def __init__(self, data, off):
        self.d = data
        self.p = off
        self.b = 0
        self.n = 0

    def need(self, k):
        while self.n < k:
            if self.p >= len(self.d):
                self.b |= 0 << self.n
                self.n += 8
            else:
                self.b |= self.d[self.p] << self.n
                self.p += 1
                self.n += 8
        return self.b & ((1 << k) - 1)

    def drop(self, k):
        self.b >>= k
        self.n -= k


def pk_explode(data, off, length):
    """PKWARE DCL explode. Returns bytes."""
    d = data[off:off + length]
    if len(d) < 4:
        return b''
    lit_mode = d[0]           # 0 = binary, 1 = ascii
    dict_bits = d[1]          # 4..6
    if dict_bits < 4 or dict_bits > 6:
        raise ValueError(f'pkware: bad dict size {dict_bits}')
    br = Bits(d, 2)
    out = bytearray()

    # Build decode maps once per call (small tables, negligible cost here).
    len_map = {}
    for i in range(16):
        len_map[(_LEN_BITS[i], _LEN_CODE[i])] = i
    dist_map = {}
    for i in range(64):
        dist_map[(_DIST_BITS[i], _DIST_CODE[i])] = i
    asc_map = {}
    if lit_mode == 1:
        for i in range(0x100):
            asc_map[(ChBitsAsc[i], ChCodeAsc[i])] = i

    def read_code(table, maxbits):
        for nb in range(1, maxbits + 1):
            v = br.need(nb)
            # PKWARE codes are stored bit-reversed relative to the table value;
            # StormLib's tables are already in read order, so compare directly.
            hit = table.get((nb, v))
            if hit is not None:
                br.drop(nb)
                return hit
        return None

    while True:
        if br.need(1) == 1:
            br.drop(1)
            li = read_code(len_map, 7)
            if li is None:
                break
            n = _LEN_BASE[li]
            e = _EXTRA_LEN[li]
            if e:
                n += br.need(e); br.drop(e)
            n += 2
            if n == 0x208:
                break
            di = read_code(dist_map, 8)
            if di is None:
                break
            if n == 2:
                dist = (di << 2) | br.need(2); br.drop(2)
            else:
                dist = (di << dict_bits) | br.need(dict_bits); br.drop(dict_bits)
            dist += 1
            if dist > len(out):
                break
            start = len(out) - dist
            for k in range(n):
                out.append(out[start + k])
        else:
            br.drop(1)
            if lit_mode == 1:
                c = read_code(asc_map, 13)
                if c is None:
                    break
                out.append(c)
            else:
                out.append(br.need(8)); br.drop(8)
        if br.p >= len(d) and br.n <= 0:
            break
    return bytes(out)


class MpqArchive:
    def __init__(self, path):
        self.path = path
        self.f = open(path, 'rb')
        f = self.f
        f.seek(0, 2)
        flen = f.tell()
        apos = -1
        p = 0
        while p + 32 <= flen:
            f.seek(p)
            if f.read(4) == b'MPQ\x1a':
                apos = p
                break
            p += 0x200
        if apos < 0:
            raise ValueError('no MPQ header')
        self.apos = apos
        f.seek(apos)
        hdr = f.read(32)
        sector_shift = struct.unpack_from('<H', hdr, 14)[0]
        hash_pos     = struct.unpack_from('<I', hdr, 16)[0]
        block_pos    = struct.unpack_from('<I', hdr, 20)[0]
        hash_size    = struct.unpack_from('<I', hdr, 24)[0]
        block_size   = struct.unpack_from('<I', hdr, 28)[0]
        self.sector_size = 0x200 << sector_shift
        self.hash_count = hash_size
        self.hash = self._table(apos + hash_pos, hash_size, KEY_HASH_TABLE)
        self.block = self._table(apos + block_pos, block_size, KEY_BLOCK_TABLE)

    def _table(self, pos, count, key):
        self.f.seek(pos)
        raw = self.f.read(count * 16)
        u = list(struct.unpack('<%dI' % (count * 4), raw))
        return decrypt_block(u, key)

    def _find(self, name):
        name = name.replace('/', '\\')
        mask = self.hash_count - 1
        idx = hash_string(name, HASH_TABLE_INDEX) & mask
        n1 = hash_string(name, HASH_NAME_A)
        n2 = hash_string(name, HASH_NAME_B)
        for _ in range(self.hash_count):
            e = idx * 4
            bi = self.hash[e + 3]
            if bi == 0xFFFFFFFF:
                return -1
            if bi != 0xFFFFFFFE and self.hash[e] == n1 and self.hash[e + 1] == n2:
                return bi
            idx = (idx + 1) & mask
        return -1

    def has_file(self, name):
        bi = self._find(name)
        return bi >= 0 and (self.block[bi * 4 + 3] & FLAG_EXISTS) != 0

    def read_file(self, name):
        name = name.replace('/', '\\')
        bi = self._find(name)
        if bi < 0:
            return None
        b = bi * 4
        file_pos, csize, fsize, flags = self.block[b:b + 4]
        if not (flags & FLAG_EXISTS):
            return None
        base = self.apos + file_pos
        if fsize == 0:
            return b''

        key = 0
        if flags & FLAG_ENCRYPTED:
            plain = name.rsplit('\\', 1)[-1]
            key = hash_string(plain, HASH_FILE_KEY)
            if flags & FLAG_FIX_KEY:
                key = ((key + file_pos) & M32) ^ fsize

        self.f.seek(base)

        if flags & FLAG_SINGLE_UNIT:
            body = bytearray(self.f.read(csize))
            if flags & FLAG_ENCRYPTED:
                decrypt_bytes(body, key)
            if csize < fsize:
                return self._decomp(bytes(body), fsize, flags)
            return bytes(body[:fsize])

        if flags & COMPRESS_MASK:
            nsec = ((fsize - 1) // self.sector_size) + 1
            noff = nsec + 1 + (1 if (flags & FLAG_SECTOR_CRC) else 0)
            offs = list(struct.unpack('<%dI' % noff, self.f.read(noff * 4)))
            if flags & FLAG_ENCRYPTED:
                decrypt_block(offs, (key - 1) & M32)
            out = bytearray()
            for i in range(nsec):
                raw = offs[i + 1] - offs[i]
                unc = min(self.sector_size, fsize - i * self.sector_size)
                self.f.seek(base + offs[i])
                seg = bytearray(self.f.read(raw))
                if flags & FLAG_ENCRYPTED:
                    decrypt_bytes(seg, (key + i) & M32)
                if raw < unc:
                    out += self._decomp(bytes(seg), unc, flags)
                else:
                    out += seg[:unc]
            return bytes(out)

        out = bytearray(self.f.read(fsize))
        if flags & FLAG_ENCRYPTED:
            nsec = ((fsize - 1) // self.sector_size) + 1
            for i in range(nsec):
                s = i * self.sector_size
                unc = min(self.sector_size, fsize - s)
                view = out[s:s + unc]
                decrypt_bytes(view, (key + i) & M32)
                out[s:s + unc] = view
        return bytes(out)

    def _decomp(self, body, outsize, flags):
        if (flags & FLAG_IMPLODE) and not (flags & FLAG_COMPRESS):
            r = pk_explode(body, 0, len(body))
            return r.ljust(outsize, b'\0')[:outsize]
        method = body[0]
        if method == 0x02:
            r = zlib.decompress(body[1:])
            return r.ljust(outsize, b'\0')[:outsize]
        if method == 0x08:
            r = pk_explode(body, 1, len(body) - 1)
            return r.ljust(outsize, b'\0')[:outsize]
        raise NotImplementedError('MPQ compression byte 0x%02X' % method)
