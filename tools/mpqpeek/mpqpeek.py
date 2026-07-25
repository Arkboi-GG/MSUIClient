#!/usr/bin/env python3
"""mpqpeek - read the client's own archives without building the client.

WHY THIS EXISTS
    The settings UI (SYSTEM_SETTINGS_UI.md) was built twice from memory and got
    the texture paths and the nine-slice layout wrong both times. What settled it
    was reading interface.MPQ directly: the paths came out of (listfile), every
    layout number came out of the FrameXML that ships in the same archive, and
    the edge-cell order came out of DECODING the texture and looking at it.

    Two rounds of plausible recall lost to one extraction. This is that
    extraction, kept, so the next UI question is a two-minute read.

WHAT IT IS
    A stdlib-only Python port of this repo's own readers - MpqArchive/MpqCrypto
    (mpq.py) and BlpDecoder (blp.py). Read-only, no dependencies, no build. It
    deliberately mirrors MpqMount.LoadOrder so `find` resolves a path to the same
    archive the client would.

WHAT IT IS NOT
    Not part of the client and not on its build path. If a behaviour here
    disagrees with the C#, the C# is right - these are 200 lines of convenience,
    not a second implementation to maintain in lockstep.

USAGE
    python3 mpqpeek.py find  'UI-DialogBox*'
    python3 mpqpeek.py ls    interface.MPQ 'Interface\\FrameXML*'
    python3 mpqpeek.py cat   'Interface\\FrameXML\\GameMenuFrame.xml'
    python3 mpqpeek.py stat  'Interface\\DialogFrame\\UI-DialogBox-Background.blp'
    python3 mpqpeek.py png   'Interface\\Buttons\\UI-Panel-Button-Up.blp' -o btn.png
    python3 mpqpeek.py cells 'Interface\\DialogFrame\\UI-DialogBox-Border.blp' -o grid.png

    --data defaults to <repo>/GameData/Data, found by walking up for
    MSUIClient.sln exactly as ClientConfig.FindRepoRoot does.
"""
import argparse
import fnmatch
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import mpq
import blp


# ── archive discovery ────────────────────────────────────────────────────────

def find_repo_root(start=None):
    """Walk up looking for MSUIClient.sln. Same rule as ClientConfig.FindRepoRoot."""
    d = os.path.abspath(start or os.path.dirname(os.path.abspath(__file__)))
    while True:
        if os.path.exists(os.path.join(d, 'MSUIClient.sln')):
            return d
        parent = os.path.dirname(d)
        if parent == d:
            return None
        d = parent


def default_data_path():
    root = find_repo_root()
    return os.path.join(root, 'GameData', 'Data') if root else None


def load_order(data_path):
    """Patches first, reverse-alphabetical, then base with terrain and model
    first. Transcribed from MpqMount.LoadOrder - getting this wrong means
    reading pre-patch versions of files, which is exactly the subtle bug that
    comment warns about."""
    if not data_path or not os.path.isdir(data_path):
        return []

    names = [f for f in os.listdir(data_path) if f.lower().endswith('.mpq')]
    patches = sorted((f for f in names if f.lower().startswith('patch')),
                     key=str.lower, reverse=True)

    def base_rank(f):
        n = f.lower()
        return 0 if n == 'terrain.mpq' else 1 if n == 'model.mpq' else 10

    base = sorted((f for f in names if not f.lower().startswith('patch')), key=base_rank)
    return [os.path.join(data_path, f) for f in patches + base]


def open_archives(data_path, only=None):
    out = []
    for path in load_order(data_path):
        name = os.path.basename(path)
        if only and name.lower() != only.lower():
            continue
        try:
            out.append((name, mpq.Mpq(path)))
        except Exception as ex:
            print(f'  ! {name}: {ex}', file=sys.stderr)
    return out


def resolve(archives, internal_path):
    """First archive in load order that has it - the client's own resolution."""
    for name, a in archives:
        if a.has(internal_path):
            return name, a.read(internal_path)
    return None, None


def listfile(a):
    raw = a.read('(listfile)')
    if not raw:
        return []
    return [l.strip() for l in raw.decode('latin-1').splitlines() if l.strip()]


# ── commands ─────────────────────────────────────────────────────────────────

def cmd_find(args, archives):
    # Listfile entries are full internal paths, so a bare leaf name has to be
    # wrapped. A pattern that already names a directory is left anchored.
    pattern = args.pattern
    if not pattern.startswith('*') and '\\' not in pattern:
        pattern = '*' + pattern
    if not pattern.endswith('*'):
        pattern += '*'

    hits = 0
    seen = set()
    for name, a in archives:
        for entry in listfile(a):
            if fnmatch.fnmatch(entry.lower(), pattern.lower()):
                key = entry.lower()
                marker = '  ' if key not in seen else ' (shadowed) '
                seen.add(key)
                print(f'{name:16}{marker}{entry}')
                hits += 1
    print(f'\n{hits} match(es)', file=sys.stderr)
    return 0 if hits else 1


def cmd_ls(args, archives):
    return cmd_find(args, archives)


def cmd_cat(args, archives):
    name, data = resolve(archives, args.path)
    if data is None:
        print(f'not found: {args.path}', file=sys.stderr)
        return 1
    print(f'{args.path} from {name}, {len(data):,} bytes', file=sys.stderr)
    if args.out:
        open(args.out, 'wb').write(data)
        print(f'wrote {args.out}', file=sys.stderr)
    else:
        sys.stdout.write(data.decode('latin-1'))
    return 0


def cmd_stat(args, archives):
    name, data = resolve(archives, args.path)
    if data is None:
        print(f'not found: {args.path}', file=sys.stderr)
        return 1

    print(f'{args.path}')
    print(f'  archive      {name}')
    print(f'  bytes        {len(data):,}')

    if data[:4] != b'BLP2':
        print('  (not a BLP2 file)')
        return 0

    enc, adepth, atype = data[8], data[9], data[10]
    rgba, w, h = blp.decode(data, args.mip)
    kinds = {1: 'palettised', 2: 'DXT', 3: 'raw BGRA'}
    print(f'  size         {w} x {h}')
    print(f'  encoding     {enc} ({kinds.get(enc, "?")})  alphaDepth {adepth}  alphaType {atype}')

    # Flat-colour detection. This is the check that found the thing two rounds of
    # eyeballing missed: UI-DialogBox-Background is a UNIFORM black at 60% alpha,
    # so the "stone" inside a real 1.12 dialog is the WORLD showing through, not a
    # texture. Anything that makes the panel opaque destroys it.
    first = rgba[0:4]
    flat = all(rgba[i:i + 4] == first for i in range(0, len(rgba), 4))
    if flat:
        print(f'  FLAT COLOUR  RGBA {tuple(first)}  '
              f'(every texel identical - alpha {first[3] / 255:.0%})')
    else:
        chans = [[rgba[i + c] for i in range(0, len(rgba), 4)] for c in range(4)]
        print(f'  min RGBA     {[min(c) for c in chans]}')
        print(f'  max RGBA     {[max(c) for c in chans]}')
    return 0


def _checker(rgba, w, h, zoom, sq=4):
    """Composite over a checkerboard so alpha is visible. Nearest-neighbour."""
    W, H = w * zoom, h * zoom
    out = bytearray(W * H * 4)
    for y in range(H):
        for x in range(W):
            s = ((y // zoom) * w + (x // zoom)) * 4
            r, g, b, a = rgba[s:s + 4]
            c = 210 if ((x // (sq * zoom)) + (y // (sq * zoom))) % 2 == 0 else 110
            f = a / 255
            d = (y * W + x) * 4
            out[d:d + 4] = bytes((int(r * f + c * (1 - f)),
                                  int(g * f + c * (1 - f)),
                                  int(b * f + c * (1 - f)), 255))
    return bytes(out), W, H


def cmd_png(args, archives):
    name, data = resolve(archives, args.path)
    if data is None:
        print(f'not found: {args.path}', file=sys.stderr)
        return 1
    rgba, w, h = blp.decode(data, args.mip)
    out = args.out or (os.path.basename(args.path).rsplit('.', 1)[0] + '.png')
    if args.checker:
        rgba, w, h = _checker(rgba, w, h, args.zoom)
    elif args.zoom > 1:
        z = args.zoom
        big = bytearray(w * z * h * z * 4)
        for y in range(h * z):
            for x in range(w * z):
                s = ((y // z) * w + (x // z)) * 4
                big[(y * w * z + x) * 4:(y * w * z + x) * 4 + 4] = rgba[s:s + 4]
        rgba, w, h = bytes(big), w * z, h * z
    blp.write_png(out, rgba, w, h)
    print(f'{args.path} from {name} -> {out} ({w}x{h})', file=sys.stderr)
    return 0


def cmd_cells(args, archives):
    """Lay a Blizzard edgeFile out as a labelled grid of its cells.

    A backdrop edgeFile is N equal cells in a horizontal strip - 256x32 is eight
    32x32 cells. THIS is the command that settled the layout: the order
    (LEFT, RIGHT, TOP, BOTTOM, TOPLEFT, TOPRIGHT, BOTTOMLEFT, BOTTOMRIGHT) and
    the fact that TOP and BOTTOM are stored standing up were both read straight
    off the picture after two rounds of guessing them wrong.
    """
    name, data = resolve(archives, args.path)
    if data is None:
        print(f'not found: {args.path}', file=sys.stderr)
        return 1

    rgba, w, h = blp.decode(data, args.mip)
    n = args.cells
    cw = w // n
    if cw * n != w:
        print(f'warning: {w} is not divisible by {n} cells', file=sys.stderr)

    z, pad = args.zoom, 8
    cols = min(n, 4)
    rows = (n + cols - 1) // cols
    cellw, cellh = cw * z + pad, h * z + pad
    W, H = cols * cellw, rows * cellh
    out = bytearray(b'\x28\x28\x28\xff' * (W * H))

    for i in range(n):
        gx, gy = i % cols, i // cols
        for y in range(h * z):
            for x in range(cw * z):
                s = ((y // z) * w + (i * cw + x // z)) * 4
                r, g, b, a = rgba[s:s + 4]
                c = 210 if ((x // (4 * z)) + (y // (4 * z))) % 2 == 0 else 110
                f = a / 255
                dx, dy = gx * cellw + pad // 2 + x, gy * cellh + pad // 2 + y
                d = (dy * W + dx) * 4
                out[d:d + 4] = bytes((int(r * f + c * (1 - f)),
                                      int(g * f + c * (1 - f)),
                                      int(b * f + c * (1 - f)), 255))

    dest = args.out or 'cells.png'
    blp.write_png(dest, bytes(out), W, H)
    print(f'{args.path} from {name}: {w}x{h} = {n} cell(s) of {cw}x{h}', file=sys.stderr)
    print(f'wrote {dest} ({W}x{H}), reading left-to-right then top-to-bottom:', file=sys.stderr)
    print('  a Blizzard edgeFile is LEFT RIGHT TOP BOTTOM TOPLEFT TOPRIGHT '
          'BOTTOMLEFT BOTTOMRIGHT', file=sys.stderr)
    print('  TOP and BOTTOM are stored STANDING UP - their bar runs down the left '
          'and right of\n  the cell, and both are drawn rotated a quarter turn '
          'CLOCKWISE.', file=sys.stderr)
    return 0


# ── entry point ──────────────────────────────────────────────────────────────

def main(argv=None):
    p = argparse.ArgumentParser(
        prog='mpqpeek', description=__doc__,
        formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument('--data', default=None,
                   help='client Data folder (default: <repo>/GameData/Data)')
    p.add_argument('--archive', default=None,
                   help='restrict to one archive, e.g. interface.MPQ')
    p.add_argument('--mip', type=int, default=0, help='BLP mip level (default 0)')
    sub = p.add_subparsers(dest='cmd', required=True)

    s = sub.add_parser('find', help='search every (listfile) for a glob')
    s.add_argument('pattern')
    s.set_defaults(fn=cmd_find)

    s = sub.add_parser('ls', help='alias of find, usually with --archive')
    s.add_argument('pattern', nargs='?', default='*')
    s.set_defaults(fn=cmd_ls)

    s = sub.add_parser('cat', help='extract a file (stdout, or -o)')
    s.add_argument('path')
    s.add_argument('-o', '--out')
    s.set_defaults(fn=cmd_cat)

    s = sub.add_parser('stat', help='BLP dimensions, encoding, and flat-colour check')
    s.add_argument('path')
    s.set_defaults(fn=cmd_stat)

    s = sub.add_parser('png', help='decode a BLP to PNG')
    s.add_argument('path')
    s.add_argument('-o', '--out')
    s.add_argument('--zoom', type=int, default=1)
    s.add_argument('--checker', action='store_true',
                   help='composite over a checkerboard so alpha is visible')
    s.set_defaults(fn=cmd_png)

    s = sub.add_parser('cells', help='lay an edgeFile out as a labelled cell grid')
    s.add_argument('path')
    s.add_argument('-o', '--out')
    s.add_argument('--cells', type=int, default=8)
    s.add_argument('--zoom', type=int, default=10)
    s.set_defaults(fn=cmd_cells)

    args = p.parse_args(argv)

    data = args.data or default_data_path()
    if not data or not os.path.isdir(data):
        print(f'no Data folder at {data!r}. Pass --data.', file=sys.stderr)
        return 2

    archives = open_archives(data, args.archive)
    if not archives:
        print(f'no readable .MPQ in {data}', file=sys.stderr)
        return 2

    try:
        return args.fn(args, archives)
    except NotImplementedError as ex:
        # The most likely gap: PkwareExplode.cs is not ported. Say so plainly
        # rather than dying in a traceback.
        print(f'\nunsupported: {ex}', file=sys.stderr)
        print('mpqpeek handles stored, zlib and single-unit sectors - enough for '
              'interface.MPQ\nand the DBCs. For PKWARE-imploded files, port '
              'Formats/Mpq/PkwareExplode.cs.', file=sys.stderr)
        return 3


if __name__ == '__main__':
    sys.exit(main())
