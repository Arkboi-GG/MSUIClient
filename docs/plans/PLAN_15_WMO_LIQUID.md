# Plan 15 — WMO liquid (MLIQ): canals, fountains, indoor pools

Status: **BUILT 2026-08-12, draw-only.** (History: first built 2026-07-26,
reverted the same day for shipping default-ON while also rewriting the shared
`TryGetSurface`. The rebuild leaves that path untouched — a separate WMO mesh
set in `LiquidRenderer`, rebuilt on a `WmoRenderer.LiquidVersion` poll, default
ON via `Water.DrawWmoLiquid`. Submersion/underwater tint for WMO liquid is
still NOT wired; SYSTEM_WATER.md §7 is the authoritative description.)
Written 2026-07-26. Owner docs it will feed: `SYSTEM_WATER.md` (§5's "WMO liquid
(MLIQ)" entry is what this closes) and, for the type mapping,
`SYSTEM_EXTERIOR_LIGHTING.md` §2.2.

> **Read §4 first if you read nothing else.** The MLIQ coordinate convention and
> the tile-flag encoding were settled this session against 235 real vanilla WMO
> groups pulled out of `wmo.MPQ`, not against a wiki. Two of the four answers
> contradict the comments currently in `WmoReader.cs`. The derivation is
> reproducible — the harness is in §6.

---

## 1. Problem

Stormwind's canals are dry. So are Ironforge's lava channels, Undercity's slime,
Blackrock's lava lake, the Maraudon and Blackfathom pools, and every fountain and
indoor pool in the game. **235 WMO groups in `wmo.MPQ` carry an MLIQ chunk and
the client draws none of them.**

From a vantage: stand on the Stormwind canal bridge in Trade District. The canal
bed is textured stone with nothing in it, and you can walk down into it. The
open-world river 200 yards away renders correctly, which makes the omission
read as a bug rather than as an unbuilt feature.

`WmoReader` has parsed MLIQ since the WMO reader was written
(`WmoReader.cs` ~line 567, `WmoLiquid` class ~line 974). `WmoGroupData.Liquid`
is populated and **nothing ever reads it** — the same shape PLAN_10 found with
the portal chunks.

This is `SYSTEM_WATER.md` §5's fourth bullet and handbook §7.1 item 11.

## 2. Class

**Emulation-core.** Measured against the real 1.12 client.

But unusually well-conditioned, in the same way exterior lighting was: the
*placement* half is arithmetic over authored bytes and has an exact yardstick
(§4), so it does not need a capture. Only the *look* — how a canal is shaded
versus the open-world river — needs `refs/`. Split the two and only the second
is blocked on Nico.

## 3. Target

**Placement:** every MLIQ surface lands inside the room that owns it, at the
height Blizzard authored, with the tiles Blizzard marked hidden left out.
Falsifiable without a screenshot — see §7 step 1.

**Look:** Stormwind's canal water should read as the same substance as the
Elwynn river outside the gate — it is the same liquid type (§4.4 proves it:
Stormwind's tiles are `type & 3 == 0`, water). A canal that looks like a
different material than the river 200 yards away is the failure mode.

`refs/stormwind-canal.png` does not exist yet. §9 says what ships without it.

## 4. Ground truth — DERIVED THIS SESSION, do not re-derive

All four facts below come from parsing **235 MLIQ-bearing group files out of
`wmo.MPQ`** with a Python port of the client's own MPQ reader (§6). Counts are
exact, not sampled.

### 4.1 MLIQ is Z-up in WMO local space — the code comment says otherwise and is WRONG

`WmoLiquid`'s docstring currently says:

> Local space convention (Noggit wmo_liquid.cpp): tile (i, j) covers
> `(CornerX + i*UNIT, height, CornerY - j*UNIT)` — note Z grows NEGATIVE in j.

**That is Noggit's own Y-up render space, not the file's.** MLIQ is in the same
Z-up local space as MOVT (handbook §3.4), so the layout is:

```
vertex(i, j) = ( CornerX + i*UNIT,  CornerY + j*UNIT,  VertexHeights[j*XVerts + i] )
```

**This is the same trap the MOVT comment set** (`msui-client-rendering` memory:
*"WmoReader's class doc claims MOVT is converted to Y-up at parse. It is NOT."*).
Two stale Noggit-derived comments in one file. Assume nothing else in that
file's prose either.

**How it was settled.** A liquid surface must lie inside its own group's
**MOGP bounding box** (`MOGP +0x0C` min, `+0x18` max) — which is *authored*, in
the same local space as MOVT, and not derived from anything we compute. Five
candidate layouts were scored by how far outside that box each puts the liquid,
summed over all 235 groups:

| candidate | total escape (yd) | worst group | non-square grids only |
|---|---|---|---|
| **A  `(cx+iU, cy+jU, h)`  Z-up** | **3098** | **108** | **2414** |
| C  `(cx+jU, cy+iU, h)` axes swapped | 5147 | 192 | 4463 |
| B  `(cx+iU, cy-jU, h)` | 14315 | 458 | 12770 |
| D  `(cx+iU, h, cy-jU)` **the comment's Noggit layout** | 54658 | 2008 | 44600 |
| E  `(cx+iU, h, cy+jU)` | 55364 | 2233 | 45498 |

The Z-up/Y-up split is decided by a factor of **18**. A versus C — which axis
`i` indexes — is decided by the 187 **non-square** grids, where the two are not
interchangeable: 2414 against 4463. Square grids cannot settle it, which is why
the column is broken out.

Non-zero escape under A is expected and is not a residual error: the MOGP box
bounds the *render* geometry, and a pool surface legitimately runs a little way
into the wall it meets. 103 of 235 groups escape by exactly 0.00.

### 4.2 UNIT = 4.1666667 yards, and this one is not a judgement call

`WMO_LIQUID_UNIT = 33.3333f / 8.0f` in `WmoReader.cs` is **correct**. Proof, and
it is the cleanest measurement in this document:

> **All 470 corner coordinates from the 235 groups (`CornerX` and `CornerY` of
> each) are exact integer multiples of 4.1666667, to within 0.01 yards. 470 of
> 470. 100.00%.**

| candidate UNIT | corners on grid |
|---|---|
| 1.0 | 18.5% |
| 4.0 | 5.5% |
| **4.1666667** | **100.00%** |
| 4.2 | 1.1% |
| 5.0 | 18.5% |
| 8.3333 | 51.9% |

Blizzard authored MLIQ corners on the grid. Nothing else fits, 4.2 misses by two
orders of magnitude, and 8.3333 scoring 51.9% is just the even multiples of the
real answer. **Do not tune this constant.**

Note the escape metric of §4.1 is *useless* for UNIT — a smaller UNIT shrinks the
grid and mechanically reduces escape, so it ranks 3.5 above 4.1667. That is a
metric with a monotone bias, and it is recorded here as a trap: the snap test is
the valid instrument, the escape test is not.

### 4.3 The tile byte: low nibble is the type, 0x0F means "no liquid"

Every tile is one byte. Across 235 groups the low nibble takes exactly these
values and no others:

```
low nibble present:  0, 2, 3, 4, 6, 7, 15
low nibble 8..14:    NEVER  (0 occurrences)
```

So **`(b & 0x0F) == 0x0F` means the tile is not drawn**, and otherwise the low
nibble is the liquid type. The existing `0x08` dont_render test is *equivalent
on real data* — because nibbles 8–14 never occur — but it is equivalent by luck,
not by construction, and the code comment ("bits 0..2 carry legacy liquid type")
under-counts the field by a bit. Use the nibble test and say why.

Hidden tiles are the bulk of the data: `0x0F` alone accounts for 46,455 tiles
against ~68,000 drawn ones. A WMO's liquid grid is a bounding rectangle with the
actual pool cut out of it, so **skipping hidden tiles is not an optimisation, it
is the difference between a canal and a solid slab of water across the district.**

### 4.4 `type & 3` gives the substance, and the buildings prove it

The six live type codes group into three classes under `& 3`, and every single
building lands where it should:

| `type & 3` | substance | buildings, by rendered tile count |
|---|---|---|
| 0 | **water** | Maraudon (16037), Blackfathom (6044), **Stormwind (2875)**, Wailing Caverns (2271), Crypt (1021), Orgrimmar (843) |
| 2 | **magma** | Blackrock lower instance (11680), Blackrock lower guild (7489), Blackrock (3912), LavaDungeon (2299), **Ironforge (2166)**, Goldmine (1169) |
| 3 | **slime** | **Undercity (4104)**, Stratholme (56), Stratholme_B (56), UndeadZiggurat (16) |

Zero counterexamples in 235 groups. Ironforge appears under magma via *both* raw
codes 2 and 6, which is exactly what `& 3` predicts and is the check that the
masking is real rather than a coincidence of sorting. `& 3 == 1` (ocean) does not
occur in any WMO, which is unsurprising — WMOs are buildings.

This matches vanilla `LiquidType.dbc`'s `Type` column (0 water, 1 ocean, 2 magma,
3 slime), the same convention MaNGOS uses. `LiquidType.dbc` itself is **not in
`dbc.MPQ`** — it lives in `patch.MPQ` — so the mapping above is derived from
placement, not read from the DBC. If the DBC is ever wanted, that is where it is.

### 4.5 THE TRAP: MLIQ type codes are NOT the shader's type codes

`water.frag` routes on the **MCLQ** codes (`SYSTEM_WATER.md` §1.1):

```
1 = ocean      3 = slime      4 (and 0/2/5) = river/lake      6 = magma
```

MLIQ's codes are a different encoding in the same numeric range. Passing an MLIQ
type through unchanged gives **partly-correct output, which is the worst kind**:

| MLIQ raw | `&3` truth | passed through raw, shader reads it as | verdict |
|---|---|---|---|
| 4 (Stormwind canal) | water | 4 → river | accidentally right |
| 6 (Blackrock) | magma | 6 → magma | accidentally right |
| 3 (Undercity) | slime | 3 → slime | accidentally right |
| **2 (Ironforge lava)** | **magma** | **2 → river/lake** | **WRONG — blue water in the forge** |
| **7 (Stratholme slime)** | **slime** | **7 → river/lake** | **WRONG** |
| **0 (Dire Maul, caves)** | **water** | **0 → river/lake** | right, by a different route |

Three of six right by coincidence is precisely the pattern that survives a
casual test in Stormwind and ships broken. **Translate explicitly:**

```csharp
// MLIQ (type & 3): 0 water, 1 ocean, 2 magma, 3 slime   [PLAN_15 §4.4]
// MCLQ shader codes: 4 river/lake, 1 ocean, 6 magma, 3 slime  [SYSTEM_WATER §1.1]
static byte MliqToShaderType(byte tile) => (byte)((tile & 0x03) switch
{
    0 => 4,   // water  -> river/lake
    1 => 1,   // ocean
    2 => 6,   // magma
    _ => 3,   // slime
});
```

### 4.6 `MOGP.groupLiquid` is 15 in all 235 groups — ignore it

Every MLIQ-bearing group reads `groupLiquid == 15` at `MOGP +0x34`. 15 is the
same "none" sentinel the tile nibble uses. `WmoReader`'s comment already suspects
this (*"Some vanilla WMOs leave this 0 even when MLIQ is present; the actual
per-tile liquid_type bits take priority"*) — the measurement upgrades it from a
suspicion to a rule for 1.12: **the header field carries no information; route
per tile, always.**

## 5. Key design decisions

**D1 — LiquidRenderer owns the drawing; WmoRenderer only yields.**
Mirror `EnumerateDoodads()` exactly (`WmoRenderer.cs` ~2102): WMO holds the
local-space surface and the instance transform, `LiquidRenderer` builds and draws
it. One water shader, one draw state, one underwater test. The alternative —
a liquid pass inside `WmoRenderer` — duplicates the whole `water.frag` uniform
block and guarantees a canal drifts out of sync with the river on the next
tuning pass.

**D2 — bake to world space at build, like the MCLQ mesh.**
`Model.Liquids` keeps the surface in WMO **local** space (like `PortalVertices`
and `CollisionTriangles`, and for the same reason: a model may be placed twice).
`EnumerateLiquid()` applies `instance.Transform` and yields world-space vertices,
which `LiquidRenderer` writes into the same 5-float format the MCLQ mesh already
uses: `position(3) + type(1) + depth(1)`. **No shader change, no new vertex
format, no second pipeline.** This is the whole reason the plan is small.

**D3 — depth is a constant, and it is honest about it.**
The MCLQ mesh bakes real per-vertex depth from the terrain grid directly beneath
(`LiquidRenderer.Build`). A WMO pool has no terrain under it — the floor is the
building's own mesh, which would need a raycast per vertex against the collision
BVH. **Not worth it in stage 1.** Ship a per-surface constant depth from
`max(heights) - CornerZ` — `CornerZ` is the authored floor reference — clamped to
a sane range, and record the shortcut in `SYSTEM_WATER.md`. The visible cost is
that a canal will not soften at its edge the way a riverbank does. §9 has the
upgrade.

**D4 — `TryGetSurface` must see WMO liquid too, or you drown standing on a bridge.**
Submersion and the underwater overlay run off `TryGetSurface`, which today walks
only `_tiles`. If WMO surfaces draw but are invisible to it, you swim through
Stormwind's canal with a dry screen — and worse, a canal drawn *above* a terrain
water surface would let the terrain one win the query. Add WMO surfaces to the
same scan and prefer the **nearest surface above the eye**, not the first hit.
The current loop returns the first match, which is already latent-buggy with
overlapping tile water; this is where that gets fixed.

**D5 — residency follows the WMO, not the ADT ring.**
WMO liquid must rebuild whenever `WmoRenderer`'s instance list changes, which is
tile crossings *and* async model adoption (a model can finish loading frames after
the crossing — `SYSTEM_INSTANCES.md` records the generation-guard race this class
of bug produces). Rebuild on an instance-list **version counter**, not on the
crossing event. Cheap: 235 groups is the whole game, and a resident set is a
handful.

## 6. Instrument — and it already exists

**The MPQ harness built this session is the instrument, and it should be kept.**
`mpq.py` (a Python port of `Formats/Mpq/MpqArchive.cs` + `MpqCrypto.cs`, same
hash algorithm, same block flags) plus `wmoliq.py` (the MOGP/MOVT/MLIQ parser and
the convention scorer). Together they answer format questions **from Nico's own
archives, in the assistant's sandbox, without a build**. Everything in §4 came
out of them in about ten minutes.

This is worth more than this plan. Every previous coordinate question in this
project — the ADT placement space, the vmap internal space, the three model
vertex conventions, the M2 collision hull — cost a round trip through Nico
building and running the client. This one did not.

Suggested home: `tools/mpqpy/`. It needs `wmo.MPQ` (363 MB) or `dbc.MPQ` (3.8 MB)
staged; `patch.MPQ` at 2 GB is over the transfer cap, which is why
`LiquidType.dbc` is unread (§4.4).

In-game, the existing instruments cover the rest: the **middle-click group
picker** names which group a canal belongs to, the **scene dump** carries reason
codes, and the **Water Tuning HUD**'s `under you:` readout is the routing check
(§7 step 4).

## 7. Test protocol — written before the change

**Step 1 — placement, no screenshot needed (the numeric gate).**
On load, print one line per WMO liquid surface and one summary:

```
[wmo-liquid] N surface(s) from M instance(s), T tiles drawn, H hidden
[wmo-liquid] escape 0.00 yd over K surface(s)   <- MUST be small
```

`escape` is §4.1's metric recomputed at runtime against each group's MOGP box.
It should reproduce the offline numbers: **103 of 235 groups at exactly 0.00,
worst case ~108 yards on a group whose pool overhangs its render box.** If the
runtime figure is wildly larger, the instance transform is wrong, not the
convention — the convention is settled.

**Step 2 — the substance check, from the type histogram.** Print
`[wmo-liquid] types water=N ocean=N magma=N slime=N`. In Stormwind, expect
**water only**. In Ironforge, **magma only**. If Ironforge reports water, §4.5's
trap was reintroduced.

**Step 3 — the canal, by eye.** Stand on the Trade District canal bridge. Water
in the canal, at the height of the canal walls, not above the bridge and not
below the bed. Save the vantage; pair it with a dump.

**Step 4 — routing.** Stand *in* the canal. The Water Tuning HUD's
`under you: liquid type N -> which texture` must read the river/lake texture, the
same one the Elwynn river reads. This is the definitive routing check and it
already exists.

**Step 5 — submersion (D4).** Walk into the canal until the eye goes under. The
underwater overlay must appear, and must disappear on the way out. Then stand on
the bridge *above* the canal and confirm it does **not** fire.

**Step 6 — A/B.** A `Draw WMO liquid` checkbox, default on. Off must be
bit-identical to today. Same discipline as PLAN_12's `uAuthoredWater`.

**Step 7 — the crossing (D5).** Walk out of Stormwind and back in. The canal must
still be there. This is the async-adoption race and it fails silently.

## 8. Definition of done

- Step 1's escape line reproduces the offline numbers.
- Steps 2–5 pass by eye and by readout.
- Step 7 passes twice.
- `SYSTEM_WATER.md` §5's "WMO liquid (MLIQ)" bullet is deleted and replaced by a
  section, including **D3's constant-depth shortcut recorded as debt**.
- `WmoLiquid`'s wrong docstring is corrected in place with a pointer to §4.1.
  *A comment that is confidently wrong ends the search* —
  `SYSTEM_EXTERIOR_LIGHTING.md` §4.0 paid for that lesson already.
- Handbook §7.1 item 11 struck; §1.2 updated.

## 9. Fallback

- **No `refs/` capture** (the likely case): ship placement, which §4 makes
  verifiable without one, and mark the *look* unverified in `SYSTEM_WATER.md`
  exactly as exterior lighting is marked. Placement does not need Nico; look does.
- **D3 depth looks bad at canal edges:** the upgrade is one raycast per vertex
  down against the collision BVH at build time. The BVH is already built and
  already indexed by WMO instance. Deferred, not unknown.
- **Performance:** if 235 surfaces ever matter (they will not — the whole game is
  fewer tiles than one ADT of river), cull per instance with the bounds
  `WmoRenderer` already computes.
- **Hard block:** the checkbox from step 6 turns the feature off entirely, so no
  step here can leave the client worse than it is today.

## 10. Reconciliation

- **`SYSTEM_WATER.md`** — §5 bullet 4 closed; a new section for WMO liquid; D3's
  shortcut added to the debt list. §1.1's routing table gains the MLIQ column and
  §4.5's warning.
- **`WmoReader.cs`** — the `WmoLiquid` docstring is **wrong** and must be fixed
  (§4.1). The `WMO_LIQUID_UNIT` constant is **right** and now has a proof (§4.2).
  The dont_render comment is right by luck and should say so (§4.3).
- **Handbook** — §7.1 item 11 struck. §1.2 gains `PLAN_15`. §3 gains nothing:
  MLIQ's convention is a water fact and belongs in `SYSTEM_WATER.md`, not in
  cross-cutting truth. But §3.4's "three arrays, two conventions" is now
  **four arrays, two conventions** — MLIQ joins the Z-up side, and saying so
  costs one line and prevents the next re-derivation.
- **PLAN_10** — unaffected. Different chunk, different concern; note only that
  "portal" now has a *fourth* meaning in this repo if anyone calls a canal lock a
  portal. (Handbook §1.2 already flags three.)
- **`SYSTEM_EXTERIOR_LIGHTING.md`** — unaffected, but note WMO liquid consumes the
  bands 13–16 authored colours through the *same* `WorldAtmosphere` path the MCLQ
  water already uses, so PLAN_12's unverified river-pair inversion (H4) will show
  up in canals too. **Do not tune canal colour before PLAN_12 §7 step 1 is run.**
- **Build order** — independent of the frame-pacing work and of PLAN_10. Does not
  block and is not blocked.
