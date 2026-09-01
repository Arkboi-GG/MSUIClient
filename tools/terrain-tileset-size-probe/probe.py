#!/usr/bin/env python3
"""Which ADT tiles lose ground textures to the "first decoded BLP fixes the
array dimensions" rule in TerrainTextures.Prepare, and how many MCNK chunks are
left with layer 0 = -1 (the proceduralAlbedo fallback in terrain.frag).

Stdlib only, reads the archives directly - see tools/mpqpeek/README.md.

    python tools/terrain-tileset-size-probe/probe.py Kalimdor 36 44 26 36
"""
import os
import struct
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, '..', 'mpqpeek'))
import mpqpeek  # noqa: E402


def iff(data, pos, end):
    """Yield (logical fourcc, data offset, size). ADT stores magics reversed."""
    while pos + 8 <= end:
        magic = data[pos:pos + 4][::-1].decode('ascii', 'replace')
        size = struct.unpack_from('<I', data, pos + 4)[0]
        yield magic, pos + 8, size
        pos += 8 + size


def parse_adt(data):
    """-> (mtex list, [(indexX, indexY, [texture ids by layer])])."""
    textures, chunks = [], []
    for magic, off, size in iff(data, 0, len(data)):
        if magic == 'MTEX':
            textures = [s.decode('ascii', 'replace')
                        for s in data[off:off + size].split(b'\0') if s]
        elif magic == 'MCNK':
            base = off - 8
            ix = struct.unpack_from('<I', data, off + 0x04)[0]
            iy = struct.unpack_from('<I', data, off + 0x08)[0]
            n = struct.unpack_from('<I', data, off + 0x0C)[0]
            ofs_layer = struct.unpack_from('<I', data, off + 0x1C)[0]
            ids = []
            if ofs_layer and base + ofs_layer + 8 <= len(data):
                p = base + ofs_layer
                if data[p:p + 4][::-1] == b'MCLY':
                    for li in range(min(n, 4)):
                        q = p + 8 + li * 16
                        if q + 16 <= len(data):
                            ids.append(struct.unpack_from('<I', data, q)[0])
            chunks.append((ix, iy, ids))
    return textures, chunks


def main():
    map_name = sys.argv[1] if len(sys.argv) > 1 else 'Kalimdor'
    c0, c1 = int(sys.argv[2]), int(sys.argv[3])
    r0, r1 = int(sys.argv[4]), int(sys.argv[5])

    data_path = mpqpeek.default_data_path()
    archives = mpqpeek.open_archives(data_path)
    size_cache = {}

    def blp_size(path):
        if path in size_cache:
            return size_cache[path]
        try:
            _, raw = mpqpeek.resolve(archives, path)
        except Exception:
            raw = None
        wh = (0, 0)
        if raw and raw[:4] == b'BLP2':
            wh = struct.unpack_from('<II', raw, 12)
        size_cache[path] = wh
        return wh

    scanned = affected = lost_base_total = lost_overlay_total = 0
    offenders = {}
    fixed_rows = []

    for col in range(c0, c1 + 1):
        for row in range(r0, r1 + 1):
            path = r'World\Maps\%s\%s_%d_%d.adt' % (map_name, map_name, col, row)
            try:
                _, raw = mpqpeek.resolve(archives, path)
            except Exception:
                continue
            if not raw:
                continue
            textures, chunks = parse_adt(raw)
            if not textures:
                continue
            scanned += 1

            # Replay Prepare()'s rule: the first texture that decodes wins.
            exp = None
            kept, dropped = set(), []
            for i, name in enumerate(textures):
                w, h = blp_size(name)
                if w == 0:
                    dropped.append((i, name, 0, 0))
                    continue
                if exp is None:
                    exp = (w, h)
                    kept.add(i)
                elif (w, h) != exp:
                    dropped.append((i, name, w, h))
                else:
                    kept.add(i)

            # What the FIXED rule picks: the most common size, largest wins a
            # tie. Nothing is dropped - a mismatch is resampled into this.
            votes = {}
            for name in textures:
                wh = blp_size(name)
                if wh[0]:
                    votes[wh] = votes.get(wh, 0) + 1
            chosen = max(votes.items(), key=lambda kv: (kv[1], kv[0][0] * kv[0][1]))[0]                 if votes else (0, 0)
            fixed_rows.append((col, row, exp, chosen, len(textures)))

            if not dropped:
                continue
            affected += 1

            lost_base = sum(1 for _, _, ids in chunks if ids and ids[0] not in kept)
            lost_overlay = sum(1 for _, _, ids in chunks
                               for li, t in enumerate(ids) if li and t not in kept)
            lost_base_total += lost_base
            lost_overlay_total += lost_overlay
            for _, name, w, h in dropped:
                key = '%s (%dx%d)' % (name, w, h)
                offenders[key] = offenders.get(key, 0) + 1

            print('tile [%d,%d]  tileset %dx%d  kept %d/%d  '
                  'chunks with NO base texture: %d/%d  lost overlays: %d'
                  % (col, row, exp[0], exp[1], len(kept), len(textures),
                     lost_base, len(chunks), lost_overlay))
            for i, name, w, h in dropped:
                print('        dropped [%d] %s  %dx%d' % (i, name, w, h))

    print()
    print('scanned %d tile(s), %d affected' % (scanned, affected))
    print('chunks falling back to proceduralAlbedo (no base texture): %d' % lost_base_total)
    print('chunk slots silently missing an overlay: %d' % lost_overlay_total)
    for k, v in sorted(offenders.items(), key=lambda kv: -kv[1]):
        print('  %3d tile(s): %s' % (v, k))

    print()
    print('--- what the fixed rule picks on those tiles ---')
    changed = [r for r in fixed_rows if r[2] != r[3]]
    for col, row, old, new, n in changed:
        print('  tile [%d,%d]  %dx%d -> %dx%d  (%d textures, 0 dropped)'
              % (col, row, old[0], old[1], new[0], new[1], n))
    print('  %d of %d affected tile(s) get a different, and larger, tileset size'
          % (len(changed), len(fixed_rows)))
    print('  chunks left with no base texture under the fixed rule: 0')


main()
