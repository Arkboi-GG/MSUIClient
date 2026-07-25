"""BLP2 -> RGBA, plus a minimal stdlib PNG writer. Python port of this repo's
Formats/BlpDecoder.cs: palettised (RAW1), DXT1/DXT3/DXT5 and raw BGRA (RAW3).

NOT PART OF THE CLIENT. If this disagrees with the C#, the C# is right.

Exists so Blizzard's UI art can be LOOKED AT rather than reasoned about - which
is what settled the nine-slice layout in SYSTEM_SETTINGS_UI.md section 1.4 after
two rounds of getting it wrong from memory.
"""
import struct

PALETTE_OFFSET = 148


def decode(data, mip_level=0):
    assert data[:4] == b'BLP2', 'not BLP2'
    typ = struct.unpack_from('<I', data, 4)[0]
    encoding, alpha_depth, alpha_type = data[8], data[9], data[10]
    w0, h0 = struct.unpack_from('<II', data, 12)
    assert typ == 1, 'JPEG BLP unsupported'

    offs = struct.unpack_from('<16I', data, 20)
    sizes = struct.unpack_from('<16I', data, 84)
    if mip_level > 15 or offs[mip_level] == 0 or sizes[mip_level] == 0:
        mip_level = 0
    w, h = max(1, w0 >> mip_level), max(1, h0 >> mip_level)
    mip = data[offs[mip_level]:offs[mip_level] + sizes[mip_level]]

    out = bytearray(w * h * 4)          # RGBA
    if encoding == 1:
        _palettized(data, mip, w, h, alpha_depth, out)
    elif encoding == 2:
        _dxt(mip, w, h, alpha_type, out)
    elif encoding == 3:
        for i in range(w * h):
            b, g, r, a = mip[i * 4:i * 4 + 4]
            out[i * 4:i * 4 + 4] = bytes((r, g, b, a))
    else:
        raise ValueError('encoding %d' % encoding)
    return bytes(out), w, h


def _palettized(data, mip, w, h, alpha_depth, out):
    px = w * h
    for i in range(px):
        p = PALETTE_OFFSET + mip[i] * 4
        out[i * 4 + 0] = data[p + 2]
        out[i * 4 + 1] = data[p + 1]
        out[i * 4 + 2] = data[p + 0]
        out[i * 4 + 3] = 255
    a = px
    if alpha_depth == 1:
        for i in range(px):
            out[i * 4 + 3] = 255 if (mip[a + (i >> 3)] >> (i & 7)) & 1 else 0
    elif alpha_depth == 4:
        for i in range(px):
            out[i * 4 + 3] = ((mip[a + (i >> 1)] >> (4 * (i & 1))) & 0xF) * 17


def _expand565(c):
    r5, g6, b5 = (c >> 11) & 0x1F, (c >> 5) & 0x3F, c & 0x1F
    return (r5 << 3) | (r5 >> 2), (g6 << 2) | (g6 >> 4), (b5 << 3) | (b5 >> 2)


def _dxt(mip, w, h, alpha_type, out):
    dxt1 = alpha_type == 0
    bb = 8 if dxt1 else 16
    bx, by = (w + 3) // 4, (h + 3) // 4
    o = 0
    for cy in range(by):
        for cx in range(bx):
            co = o if dxt1 else o + 8
            c0, c1 = struct.unpack_from('<HH', mip, co)
            r0, g0, b0 = _expand565(c0)
            r1, g1, b1 = _expand565(c1)
            col = [(r0, g0, b0, 255), (r1, g1, b1, 255)]
            if dxt1 and c0 <= c1:
                col.append(((r0 + r1) // 2, (g0 + g1) // 2, (b0 + b1) // 2, 255))
                col.append((0, 0, 0, 0))
            else:
                col.append(((2 * r0 + r1) // 3, (2 * g0 + g1) // 3, (2 * b0 + b1) // 3, 255))
                col.append(((r0 + 2 * r1) // 3, (g0 + 2 * g1) // 3, (b0 + 2 * b1) // 3, 255))

            alpha = [255] * 16
            if not dxt1:
                if alpha_type == 1:
                    for i in range(16):
                        alpha[i] = ((mip[o + (i >> 1)] >> (4 * (i & 1))) & 0xF) * 17
                else:
                    a0, a1 = mip[o], mip[o + 1]
                    a = [a0, a1] + [0] * 6
                    if a0 > a1:
                        for i in range(1, 7):
                            a[1 + i] = ((7 - i) * a0 + i * a1) // 7
                    else:
                        for i in range(1, 5):
                            a[1 + i] = ((5 - i) * a0 + i * a1) // 5
                        a[6], a[7] = 0, 255
                    bits = int.from_bytes(mip[o + 2:o + 8], 'little')
                    for i in range(16):
                        alpha[i] = a[(bits >> (3 * i)) & 7]

            idx = struct.unpack_from('<I', mip, co + 4)[0]
            for py in range(4):
                for px_ in range(4):
                    gx, gy = cx * 4 + px_, cy * 4 + py
                    if gx >= w or gy >= h:
                        continue
                    pi = py * 4 + px_
                    ci = (idx >> (2 * pi)) & 3
                    r, g, b, aa = col[ci]
                    d = (gy * w + gx) * 4
                    out[d:d + 4] = bytes((r, g, b, aa if dxt1 else alpha[pi]))
            o += bb


def write_png(path, rgba, w, h):
    import zlib
    raw = b''.join(b'\0' + rgba[y * w * 4:(y + 1) * w * 4] for y in range(h))
    def chunk(tag, data):
        c = tag + data
        return struct.pack('>I', len(data)) + c + struct.pack('>I', zlib.crc32(c) & 0xFFFFFFFF)
    png = (b'\x89PNG\r\n\x1a\n'
           + chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, 8, 6, 0, 0, 0))
           + chunk(b'IDAT', zlib.compress(raw, 9))
           + chunk(b'IEND', b''))
    open(path, 'wb').write(png)
