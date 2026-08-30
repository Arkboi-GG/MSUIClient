# System — WMO Interior Lighting (MOCV)

**How the inside of a building is lit: walls, floors, ceilings.** One of the
per-system docs the handbook indexes (see PROJECT_HANDBOOK.md §1.2). Its sibling
is SYSTEM_DOODAD_LIGHTING.md, which covers the *furniture* inside those rooms and
depends on everything here. Read this plus the handbook's cross-cutting ground
truth (§3.1 coordinates, §8.5 shader ASCII rule, §11 working agreements) before
touching interior lighting. You should not need the rest of the handbook.

Version: Draft 1 — 2026-07-24, written after the pass Nico signed off with
"the interior lighting looked good".

Owner files: the MOCV parse and `FixVertexColors` in `Formats/WmoReader.cs`,
`World/Wmo/WmoRenderer.cs`, `Shaders/wmo.vert`, `Shaders/wmo.frag`, and the two
Buildings-panel knobs in `Program.cs`.

> **This system is signed off and is not to be re-opened casually.** It is the
> reference the doodad system was built to match. If interior lighting looks
> wrong after some later change, the bug is almost certainly in the thing that
> changed, not here — check that first. `wmo.vert` and `wmo.frag` are at
> md5 `67011bc2d59f75c3c4ec9932f7d5c0c4` and `90062f5ca31856a3a8fc9896fe93aa9d`
> as of sign-off; if either moved, something touched them.

---

## 0. The bar

Vanilla 1.12 does not light building interiors at runtime. There is no indoor
sun, no dynamic point lights, no per-frame shadowing. Every interior surface
carries **a colour the artist baked into the file at map-compile time**, and the
client's entire job is to read it back faithfully. A tavern is warm and dim
because someone painted it warm and dim in 2004, not because we are simulating a
hearth.

That single fact is the whole system, and it is also the trap: every instinct
that says "add a light here" is wrong. The correct move is always to find the
authored value and stop adding things on top of it.

---

## 1. What is implemented now

### 1.1 MOCV — the baked colour

`MOCV` is a per-group chunk, four bytes per `MOVT` vertex, stored **BGRA** on
disk. `WmoReader` swizzles to RGBA on read and keeps it in
`WmoGroupData.VertexColors`.

Only the *first* MOCV is read. A second MOCV chunk exists from Cataclysm
onwards; vanilla data never has one, and the reader ignores anything past the
first so that a future expansion's file cannot silently override the classic
values.

A group with no MOCV is, in vanilla, an exterior group lit by the sun. The
absence is meaningful — it is not a missing-data case to paper over.

### 1.2 `FixVertexColors` — Blizzard's own load-time fixup

Reproduced from the client, and it must stay reproduced exactly:

```
settledAlpha = (GroupFlags & 0x08) ? 255 : 0     // 0x08 = EXTERIOR
intStart     = Batches[TransBatchCount - 1].VertexEnd + 1   // INCLUSIVE, hence +1

if (MOHD.flags & 0x08)                            // do_not_fix_vertex_color_alpha
    alpha[intStart..] = settledAlpha              // RGB is final as authored
else
    for each vertex from intStart:
        v += v * alpha / 64      (per channel, clamped to 255)
        alpha = settledAlpha
```

Three things about this that cost time to get right:

- `v * boost` reaches 65025. It must not be computed in a byte.
- `MOBA.VertexEnd` is **inclusive**. The `+1` is not an off-by-one, it is the
  fix for one.
- The fixup starts at `begin_second_fixup` — the first vertex past the
  transparent batches — not at vertex 0. Vertices before that are left entirely
  alone, alpha included.

**Measured: the boost is a near no-op indoors.** Across the sampled WMO corpus,
MOCV alpha is 0 for ~95% of interior-region vertices (mean 0.7–3.3). This is
worth knowing because it means raw MOCV and fixed MOCV are, for interiors,
effectively the same array — which is what lets doodads match walls (see
SYSTEM_DOODAD_LIGHTING.md §2).

### 1.3 The `/2` and the `x2` — why `VertexColorScale = 2.0`

The retail client **halves MOCV at load and doubles it at draw**. We skip the
halving and multiply by `VertexColorScale = 2.0` in the shader instead. Same
destination, one fewer precision loss.

`2.0` is therefore **the authored value, not a preference.** The slider exists to
diagnose, not to taste. Anything that renders alongside a wall and wants to match
it must go through the same 2.0 (this is exactly why the doodad path has its own
copy of the knob, and why the HUD warns you to keep them equal).

### 1.4 No MOHD ambient is added

`MOHD.ambColor` is **not** added on top of MOCV on the classic path. In
classic-era data the ambient is already baked into the authored vertex colours;
adding it again double-counts and washes interiors out.

Independent corroboration: noclip.website short-circuits its own ambient add with
`if (false && ...)` — someone else hit this and reached the same conclusion.

### 1.5 The three-way batch classification

`wmo.frag` branches on `uBatchType`:

| Value | Meaning | Lighting |
|---|---|---|
| 1 | interior | baked MOCV only, no runtime sun |
| 2 | transitional | MOCV blended toward daylight |
| 3 | exterior | daylight only, MOCV ignored |

A group with no MOCV at all also resolves to 3, which is how a vanilla exterior
group ends up correctly sunlit without a special case.

### 1.6 The interior gate

A group counts as interior-lit when:

```
(GroupFlags & 0x2000) != 0  &&  (GroupFlags & 0x48) == 0
```

`0x2000` = INTERIOR, `0x08` = EXTERIOR, `0x40` = EXTERIOR_LIT. Both exterior
bits mean "use daylight", and `CMapObj::QueryLighting` rejects such a group
outright and falls back to the outdoor sun. The `0x48` mask is that rejection.

This same gate is reused verbatim by the doodad system. Keep them identical; if
one changes, both must.

---

## 2. Ground truth — do not re-derive

| Fact | Value |
|---|---|
| MOCV on disk | BGRA bytes, one per MOVT vertex |
| Second MOCV | Cataclysm+ only; ignored |
| `MOHD.flags & 0x08` | `do_not_fix_vertex_color_alpha` |
| `GroupFlags & 0x04` | group ships a MOCV chunk |
| `GroupFlags & 0x08` | EXTERIOR |
| `GroupFlags & 0x40` | EXTERIOR_LIT |
| `GroupFlags & 0x2000` | INTERIOR |
| `GroupFlags & 0x1000` | LIQUIDSURFACE (MLIQ present) |
| `GroupFlags & 0x800` | HAS_DOODADS (MODR present) |
| `GroupFlags & 0x04000000` | antiportal |
| MOGP GroupFlags offset | `+0x08` in the MOGP header |
| MOGP subchunks begin | `+0x44` |
| MOBA entry size | 24 bytes; `VertexEnd` at `+0x14`, **inclusive** |
| MOCV alpha, interiors | 0 for ~95% of vertices — the boost is nearly a no-op |
| Client scale chain | `/2` at load, `x2` at draw; we do `x2.0` only |
| MOHD.ambColor | already baked in; **never added** |

---

## 3. Files and responsibilities

| File | Responsibility |
|---|---|
| `Formats/WmoReader.cs` | Parse MOCV; run `FixVertexColors`; expose `VertexColors`, `GroupFlags`, `InteriorVertexStart` |
| `World/Wmo/WmoRenderer.cs` | Classify each batch (1/2/3); upload colours; own `UseVertexColors`, `VertexColorScale` and `InteriorBrightness` (the doorway-glow multiplier, persisted since v6) |
| `Shaders/wmo.vert` | Pass the vertex colour through as `vColor` |
| `Shaders/wmo.frag` | The three-way `uBatchType` branch; apply `uVertexColorScale` |
| `Program.cs` | Buildings panel: the MOCV checkbox and the brightness slider |

---

## 4. Tuning — the Buildings panel

- **Baked interior light (MOCV)** — untick to light every interior with the
  outdoor sun again. This is the fastest side-by-side for "is this too dark".
- **Interior brightness** — `VertexColorScale`, 0.5–4.0. **2.00 is vanilla.**
  Buildings must be reloaded to re-read MOCV after some changes; the tooltip
  says so.
- **Interior doorway glow** (added to the schema 2026-08-12, settings v6) —
  `WmoRenderer.InteriorBrightness`, persisted as `Lighting.InteriorSpill`. A
  SECOND multiplier stacked on `VertexColorScale` in `wmo.frag`
  (`vColor.rgb * uVertexColorScale * uInteriorBrightness`), so it scales the
  baked light on interior AND transition batches — which is what decides how
  strongly a lit room spills through its doorway (the Northshire Abbey glow).
  It used to be a DevTools-only slider that reset to `1.0` every launch, which
  is why the spill always shipped faint. Now applied by `ApplySettings`, with
  per-lighting-mode recommended defaults: **MSUI Lighting `1.8`** (owner: the
  abbey spill was far too faint), **1.12 Parity `1.10`** (the authored `2.0`
  chain, plus the shipped 1.10 brightness balance). The DevTools Buildings-panel slider and the modal's
  Advanced slider write the same value.

---

## 5. Not done — the honest ceiling

- **MOLT (interior light sources) is not parsed at all** (corrected 2026-08-12;
  this line used to claim "parsed but unused" — `WmoReader` has no MOLT chunk
  handler, only the unrelated liquid `SMOLTile` bytes). In vanilla it is
  essentially decorative — the baked MOCV already contains the result of those
  lights. Two independent editors (noggit3, noggit-red) compute a nearest-MOLT
  direction per doodad and then never read it; that dead code was the same trap
  we fell into once and backed out of. Do not wire MOLT up expecting it to fix
  anything.
- **MFOG (per-interior fog) is not implemented.** Offered, not accepted. It is
  the most likely next real gain for interiors. **Its blocker is now gone
  (2026-07-25):** MFOG needs to know which group the camera is in, and PLAN_10
  D1 built exactly that readout — `WmoRenderer.CameraGroup`, shown in the
  Portals panel (`GameLoop/Dev/GameLoop.Portals.cs`). Note also that exterior fog is no longer
  invented; `LightIntBand` band 7 is the authored *outdoor* fog colour
  (SYSTEM_EXTERIOR_LIGHTING.md §2.2), so an interior fog pass now has a
  data-driven neighbour to blend against rather than a hand-tuned one.
- **Portal culling is not implemented.** MOPV/MOPT/MOPR are parsed, and
  PLAN_10 D1's *instrument* is built (camera group, portal quad draw,
  `DumpPortalGraph`) — but **no traversal culls anything yet** and the 120-yard
  interior rule still runs. This is a performance and correctness feature
  (seeing through walls into unlit rooms), not a lighting one.
  **PLAN_10_WMO_PORTALS.md** is the live plan.

---

## 6. Lineage — where the ground truth came from

- `FixVertexColors` and the `/2 x2` chain: the retail 1.12 client's own load and
  draw paths.
- The `0x48` rejection: `CMapObj::QueryLighting`.
- "Do not add ambColor": noclip.website's disabled ambient branch, plus direct
  measurement of classic-era MOCV.
- Third-party readers were consulted and are **not** trustworthy verbatim:
  libwarcraft reads MOHD flags as a `uint32` where the layout is
  `uint16 flags; uint16 numLod`; noggit-red has an apparent `r += ...` typo
  where noggit3 has `b += ...`. Cross-check anything taken from them.
