# terrain-tileset-size-probe

Which ADT tiles lose ground textures because a GL array texture needs one size
for every layer and `MTEX` does not promise one.

```
python tools/terrain-tileset-size-probe/probe.py Kalimdor 36 44 26 36
python tools/terrain-tileset-size-probe/probe.py Azeroth 0 63 0 63
```

Stdlib only — it reads the archives through `tools/mpqpeek/mpq.py`, so it needs
no build and cannot be blocked by a running client.

## What it found

`TerrainTextures.Prepare` used to let the FIRST texture that decoded fix the
array dimensions and skip every texture that disagreed. MTEX order is not on our
side: `Tileset\Generic\Black.blp` is 16x16 and
`Tileset\The Blasted Lands\BlastedLandsBlack.blp` is 8x8, and where one of those
leads the list, every real 256x256 ground texture in the tile was thrown away.
Those chunks then carry layer index -1, and `terrain.frag` paints them with
`proceduralAlbedo` — flat slope-coloured grey and brown over a whole 533-yard
ADT.

Six tiles lost all 256 chunks that way:

| map | tile | zone |
|---|---|---|
| Kalimdor | 40,31 | southern Durotar, below Tiragarde Keep |
| Kalimdor | 38,40 | Dustwallow Marsh |
| Azeroth | 33,45 / 34,45 / 35,45 / 35,46 | Blasted Lands |

158 more tiles lost a single overlay layer, the same bug with the coin toss the
other way up.

The fix chooses the size — most common in the tile, largest wins a tie — and
resamples a mismatch into it rather than dropping it. Re-run the probe to see
the chosen size per tile; it prints what the fixed rule picks.
