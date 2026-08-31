# mpqpeek — read the client's own archives without building the client

Stdlib-only Python. No dependencies, no build, read-only.

```
python3 tools/mpqpeek/mpqpeek.py find 'UI-DialogBox'
```

`--data` defaults to `<repo>/GameData/Data`, found by walking up for
`MSUIClient.sln` exactly as `ClientConfig.FindRepoRoot` does. So from anywhere in
the tree, the command above just works.

---

## Why it exists

The settings UI was initially built from memory and got both the texture paths and the

nine-slice layout wrong. Reading `interface.MPQ` directly settled it:

- `(listfile)` gave the real paths, and which remembered ones do not exist
- **`Interface\FrameXML\` ships in the archive — 194 files of Blizzard's own UI
  source.** Frame sizes, button tex-coords and every `<Backdrop>` definition are
  in there. The real UI is data, not a description of data.
- decoding the border texture and *looking at it* gave the edge-cell order and
  the rotation, after two rounds of guessing them wrong
- `stat` found the thing nobody spots by eye: `UI-DialogBox-Background` is a
  **uniform** black at 60% alpha, so the "stone" inside a real 1.12 dialog is the
  world showing through, not a texture

Two rounds of plausible recall lost to one extraction. This is that extraction,
kept, so the next UI question is a two-minute read rather than a round-trip.

**Reach for this whenever a question is answerable from the archives** — a
texture path, a frame size, a font, a DBC layout, "does patch-4 override this".

---

## Commands

| | |
|---|---|
| `find <glob>` | Search every archive's `(listfile)`. A bare leaf name is wrapped in `*`; a pattern containing `\` is anchored. Duplicates across archives are marked `(shadowed)`. |
| `ls <glob>` | Same thing, usually with `--archive interface.MPQ`. |
| `cat <path>` | Extract to stdout, or `-o file`. This is how you read FrameXML. |
| `stat <path>` | BLP size, encoding, alpha depth/type, and **the flat-colour check**. |
| `png <path>` | Decode to PNG. `--zoom N`, `--checker` to composite over a checkerboard so alpha is visible. |
| `cells <path>` | Lay a backdrop `edgeFile` out as a labelled grid of its cells. `--cells N` (default 8). |

Global: `--data DIR`, `--archive NAME`, `--mip N`.

### The three that earned their keep

```bash
# What is actually in there, and in which archive?
python3 mpqpeek.py find 'UI-SliderBar'

# Blizzard's own specification for a frame.
python3 mpqpeek.py cat 'Interface\FrameXML\OptionsFrame.xml' | less

# Is this texture what I think it is?
python3 mpqpeek.py stat 'Interface\Tooltips\UI-Tooltip-Background.blp'
#   FLAT COLOUR  RGBA (142, 140, 142, 187)  (every texel identical - alpha 73%)
```

And when a nine-slice looks wrong, stop reasoning about texcoords:

```bash
python3 mpqpeek.py cells 'Interface\DialogFrame\UI-DialogBox-Border.blp' -o grid.png
```

---

## Load order

`load_order()` mirrors `MpqMount.LoadOrder` exactly: **patches first in
reverse-alphabetical order** (so `patch-4` beats `patch-2` beats `patch`), then
base archives with `terrain.MPQ` and `model.MPQ` first.

That is not cosmetic. `find` and `cat` resolve a path to the same archive the
client would, so a retexture patch that overrides a file shows up here the way it
shows up in game. Getting this order wrong means reading pre-patch versions —
the subtle bug `MpqMount`'s own comment warns about.

---

## What this is not

**Not part of the client and not on its build path.** It is a port of
`Formats/Mpq/MpqArchive.cs`, `MpqCrypto.cs` and `Formats/BlpDecoder.cs` — about
200 lines of convenience, not a second implementation to keep in lockstep. **If
it disagrees with the C#, the C# is right.**

### Known gaps

- **PKWARE-imploded sectors are not supported.** `mpq.py` handles stored, zlib
  and single-unit, which covers `interface.MPQ`, `fonts.MPQ` and the DBCs. If you
  hit one, the tool says so and names the file to port:
  `Formats/Mpq/PkwareExplode.cs`.
- **JPEG-compressed BLP2 (type 0) is not supported.** WoW does not use it; the
  C# decoder rejects it too.
- **No writing.** Read-only on purpose — `MpqArchiveWriter.cs` exists in the
  client if you need the other direction.
- **`cells` assumes a horizontal strip.** True for every backdrop `edgeFile` in
  1.12; pass `--cells N` for anything else.

### Files

| | |
|---|---|
| `mpq.py` | MPQ v1 reader — port of `MpqArchive.cs` + `MpqCrypto.cs` |
| `blp.py` | BLP2 → RGBA + a minimal PNG writer — port of `BlpDecoder.cs` |
| `mpqpeek.py` | The CLI |
