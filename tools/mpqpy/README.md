# tools/mpqpy — read the vanilla archives without a build

A Python port of the client's own MPQ read path, so **format questions can be
answered from Nico's real archives in an assistant sandbox, in minutes, without
compiling or launching anything.**

Built 2026-07-26 during PLAN_15. Everything in that plan's §4 — the MLIQ
coordinate convention, the tile unit, the flag encoding, the liquid-type
mapping — came out of these two files. Two of those four answers contradicted
comments that were sitting in `WmoReader.cs` at the time.

## Why this exists

Every previous coordinate question on this project cost a round trip: write a
hypothesis, ship code, wait for Nico to build and run it, read a console paste,
repeat. The ADT placement space, the vmap internal space, the three model vertex
conventions, the M2 collision hull — all of them were settled that way, several
of them after two or three wrong guesses.

They did not have to be. The archives are just files, and the client's reader is
about 500 lines. **When the question is "what do the bytes say", read the bytes.**

## Files

| File | What it is |
|---|---|
| `mpq.py` | MPQ v1 reader. Port of `Formats/Mpq/MpqArchive.cs` + `MpqCrypto.cs` — same StormBuffer, same `HashString`, same block flags, same sector logic. Coverage matches the C#: stored, zlib (0x02), PKWARE (0x08 / implode), single-unit, encrypted. |
| `wmoliq.py` | WMO group parser (MOGP header, MOVT bounds, MLIQ) plus the convention scorer that settled PLAN_15 §4.1. Useful as a worked example of the pattern. |

## Use

```python
from mpq import MpqArchive
a = MpqArchive('GameData/Data/wmo.MPQ')
a.has_file(r'World\wmo\Azeroth\Buildings\Stormwind\Stormwind_096.wmo')
data = a.read_file(r'World\wmo\Azeroth\Buildings\Stormwind\Stormwind_096.wmo')
names = a.read_file('(listfile)')     # present in these archives, and worth reading first
```

## Transfer limits, which decide what can be asked

The device bridge caps a staged file at 400 MB. That is the real constraint on
what this tool can answer:

| Archive | Size | Reachable |
|---|---|---|
| `dbc.MPQ` | 3.8 MB | yes |
| `interface.MPQ` | 70 MB | yes |
| `model.MPQ` | 191 MB | yes |
| `wmo.MPQ` | 364 MB | yes, just |
| `texture.MPQ` | 665 MB | **no** |
| `terrain.MPQ` | 1.1 GB | **no** |
| `patch.MPQ` | 2.1 GB | **no** |

`patch.MPQ` being out of reach is why PLAN_15 §4.4 derives the liquid-type
mapping from placement rather than reading `LiquidType.dbc` — that DBC is not in
`dbc.MPQ`. It is also why anything needing patched data still has to go through a
running client.

**Load order matters if you ever read more than one archive**: patches override
base content, and the client's `MpqMount` applies them
patches-reverse-alphabetical then `terrain.MPQ`/`model.MPQ`. Reading `wmo.MPQ`
alone gives you the *base* asset, which for geometry questions is almost always
the right one — but do not assume that for textures or DBCs.

## The method, which is the transferable part

The MLIQ derivation is worth copying whenever a convention is in doubt:

1. **Find an authored yardstick in the same file.** For MLIQ it was the group's
   own MOGP bounding box — authored, in the same space, and not derived from
   anything we compute. Scoring against your own output proves nothing.
2. **Score every candidate, do not test one.** Five layouts were tried. The
   winner beat the reading in the code comment by a factor of 18.
3. **Find the cases that discriminate.** Square grids cannot tell `(i, j)` from
   `(j, i)`. The answer lived in the 187 non-square ones.
4. **Prefer a snap test to a fit test where one exists.** UNIT was settled by
   470/470 corners landing on exact multiples — not by the escape metric, which
   is monotonically biased toward smaller units and ranked the wrong answer
   first. *A metric with a known bias is worse than no metric.*
5. **Check the derivation against something you already know.** `& 3` putting
   Blackrock and Ironforge in magma, Undercity and Stratholme in slime, and
   Stormwind's canals in water is what turns an arithmetic result into a fact.
