# System — Doodad Lighting (MODD.color)

**How the furniture inside a building is lit: barrels, crates, tables,
lanterns.** One of the per-system docs the handbook indexes (see
PROJECT_HANDBOOK.md §1.2). Read SYSTEM_WMO_INTERIOR_LIGHTING.md first — this
system exists to make props match the walls that doc describes, and it reuses
that doc's interior gate and brightness factor verbatim.

Version: Draft 1 — 2026-07-24, the pass that stopped barrels glowing indoors.

Owner files: the MODR parse and `WmoDoodadDef` in `Formats/WmoReader.cs`,
`BuildDoodadLighting` + `EnumerateDoodads` in `World/Wmo/WmoRenderer.cs`,
`World/Doodads/DoodadRenderer.cs`, `Shaders/doodad.vert`, `Shaders/doodad.frag`,
and the Doodads-panel knobs in `Program.cs`.

---

## 0. The bar

Once interiors were correctly dark, the furniture in them was still lit by the
outdoor sun — so every barrel, crate and table glowed inside rooms the wall pass
had just correctly darkened. The prop and the floor it stands on were being lit
by two different systems.

**The invariant this system exists to hold: a barrel matches the floor it stands
on.** Every decision below is downstream of that one sentence. If a change makes
props detach from the ground, it is wrong no matter how defensible it looks.

---

## 1. The pivotal finding — MODD.color is a baked light, not a tint

The wiki documents `MODD.color` as a BGRA **tint**, with `(255,255,255,255)`
meaning "no tint". That is wrong for vanilla data, and building on it produces
either no effect or a wash.

**It is Blizzard's pre-baked per-placement interior light**, computed once at
map-compile time — the same answer `CMapObj::QueryLighting` would return at that
doodad's position.

### How that was established

Sample the owning group's MOCV floor directly beneath each doodad, independently
of the file's own colour, then correlate the two:

| WMO | doodads | corr |
|---|---|---|
| Subway | 1006 | 0.97 |
| TownHall | 442 | 0.86 |
| Farm | — | 0.58 |
| MD_GoldMine | — | 0.42 |
| **pooled (first study, 4929 channels)** | | **0.891** |
| **pooled (Blackrock/Subway/SW, 7428 doodads)** | | **0.824** |

Supporting evidence, each of which independently rules out "tint":

- **It varies per instance for the same model.** `BARREL01` ships as
  `(60,60,60)` in one room and `(114,113,110)` in another, in the same building.
  A tint is a property of a placement's *intent*; this tracks its *position*.
- **Spot checks line up with the sampled floor**: `GNOMEHAZARDLIGHTRED` sampled
  `(0,0,9)` / shipped `(0,0,18)`; `DWARVENBARREL01` `(255,216,159)` /
  `(255,216,169)`; `PICK` `(255,165,132)` / `(255,179,150)`;
  `WESTFALLCRATE` `(15,31,37)` / `(25,58,69)`.
- **Alpha is 255 everywhere measured**, in every vanilla WMO. It carries no
  information — do not read it as a blend factor.
- **MODD flag bits are never set, anywhere** in the corpus. There is no
  per-doodad "don't re-light me" opt-out in vanilla, so that branch is
  unnecessary rather than merely unimplemented.

Props run **slightly brighter than the floor triangle directly beneath them** —
median ratio 1.13 to 1.33 depending on the WMO. That is expected and correct: the
floor under a barrel is contact-shadowed, while the barrel's own baked light is
sampled at body height.

---

## 2. Scale — the single easiest thing to get wrong

`MODD.color` sits on the **same scale as RAW MOCV**. Measured two ways: the
MODD/MOCV ratio is ~1.0 across every brightness bucket, and correlation against
raw MOCV (0.824) very slightly beats correlation against post-`FixVertexColors`
MOCV (0.818) — consistent with MODD.color having been baked from the raw values.

Chain, stated once, precisely:

- Retail halves MOCV at load and doubles at draw, so `QueryLighting` effectively
  returns `2 x raw`.
- **We skip the halving** and apply `VertexColorScale = 2.0` at draw. Walls
  therefore render at `2 x raw`.
- So doodads feed `MODD.color / 255` into that **same 2.0**.

> ### Never pre-double MODD.color.
> Feeding an already-doubled value into the 2.0 gives **4x** — the documented
> "scheme C" error. It looks superficially plausible (props are bright indoors!)
> and is wrong.

**The x2 is empirically confirmed, not merely reasoned.** Interior MODD median
is 94/255; `94/255 x 2.0 = 0.74` on screen, against interior walls at `0.71`.
Drop the x2 and a barrel renders at `0.37` against a `0.71` floor — half as
bright as the ground it sits on. The full distribution: p10 `0.31`, p50 `0.74`,
p90 `1.5`, versus the old exterior lighting these replace at roughly 0.93–0.97.
So a tavern prop dims modestly and a mine prop goes properly dark.

**No MOHD ambient is added.** Same reasoning as the wall path
(SYSTEM_WMO_INTERIOR_LIGHTING.md §1.4). Adding it on one side and not the other
is precisely what makes props detach from the floor.

---

## 3. MODR — the interior gate, and why it is mandatory

The root's `MODD` array is flat and says nothing about which room a barrel is in.
`MODR` — a per-group MOGP subchunk, one `uint16` per doodad indexing that flat
array — is the only thing that does.

A doodad takes its baked light **only** when its owning group passes the same
gate the walls use:

```
group.IsInterior  &&  (GroupFlags & 0x48) == 0
```

Everything else gets `(0, 0, 0, 1)` — "no baked light, full daylight" — and
renders exactly as it did before this system existed.

### Why the gate is not optional

Measured across 335 WMO roots / 191 with doodads / 70,228 placements:

| Class | n | mean RGB | p10 / p50 / p90 | pure black |
|---|---|---|---|---|
| interior-owned | 61,943 | (115.6, 102.9, 99.0) | 39 / 94 / 192 | 0.1% |
| exterior-owned | 8,285 | (50.9, 50.9, 63.3) | — / 33 / — | **12.7%** |

Exterior-owned colours are dark and blue-shifted **because the real client never
reads them**, so nobody ever checked them. Applying `MODD.color` ungated would
black out every lamp post in the game.

**Orphans: 0.** Every one of the 70,228 doodads is referenced by at least one
group, so there is no fallback case to design around.

The mapping is **many-to-many** — a prop standing in a doorway is legitimately
listed by both rooms. Last writer wins, which is fine: both rooms' values are
plausible for a doodad on their boundary.

---

## 4. Unlit materials — the lantern rule

M2 material flag `0x01` is **Unlit**. It was already parsed as
`M2RenderFlag.Unlit` in `Formats/M2Reader.cs` and previously ignored, which was
survivable while everything was sunlit and is not survivable now.

Lantern glows, fire, and glow planes must ignore lighting entirely. Without this,
**a lantern inside a dark room goes out** — the one thing a lantern must not do.
`doodad.frag` forces `lighting = vec3(1.0)` when `uUnlit == 1`.

(`Unfogged`, `0x02`, exists in the format and is deliberately **not** exposed on
`M2RenderFlag`. Do not add it speculatively.)

---

## 5. The pipeline, end to end

1. `WmoReader` parses `MODR` into `WmoGroupData.DoodadRefs`.
2. `WmoRenderer.BuildDoodadLighting(ready)` walks every group, applies the gate,
   and fills a `Vector4[]` index-parallel to the root's `Doodads` — `rgb =
   MODD.color/255`, `a = 0` for interior, `(0,0,0,1)` for everything else.
3. `EnumerateDoodads` yields `(ModelPath, Transform, Light)`.
4. `Program.cs` passes the light to `DoodadRenderer.AddPlaced`.
5. `DoodadRenderer` packs it into the instance buffer as `InstanceData`
   (`Matrix4x4` + `Vector4`, **80-byte stride**), attribute location 7, divisor 1.
6. `doodad.frag`: `lighting = mix(vLight.rgb * uVertexColorScale, daylight, vLight.a)`.

### The load-bearing default

`(0, 0, 0, 1)` means "no baked light, full daylight" — and it is **exactly what
OpenGL supplies for a disabled vertex attribute**. Terrain doodads never set
attribute 7 and therefore land on it for free, rendering bit-identically to
before. This is not a coincidence to be tidied away; it is why the change is safe
for the ~90% of doodads that are not WMO furniture.

### Why `doodad.vert` / `doodad.frag` are a fork

`DoodadRenderer.LoadShaders` used to load `wmo.vert` / `wmo.frag` **directly**.
Any lighting change made there would have altered wall lighting — the one thing
signed off as correct. The shaders were forked *before* any lighting change
landed, and the WMO pair's md5s are provably unchanged. **Do not re-merge them.**

---

## 6. Ground truth — do not re-derive

| Fact | Value |
|---|---|
| MODD entry size | 40 bytes |
| MODD layout | `nameOffset:24 \| flags:8` at `0x00`; pos 3xf32 at `0x04`; quat **XYZW** at `0x10`; scale f32 at `0x20`; `CImVector` BGRA at `0x24` |
| MODS entry size | 32 bytes: `char name[20]; uint32 firstInstanceIndex; uint32 numDoodads; uint32 unused` |
| Doodad set rule | set 0 is always drawn, **in addition to** any selected set |
| MODR | per-group MOGP subchunk, one `uint16` per doodad, indexes the root MODD array |
| `GroupFlags & 0x800` | HAS_DOODADS (MODR present) |
| MODD.color meaning | baked per-placement interior light, **raw MOCV scale** |
| MODD.color alpha | 255 everywhere; carries no information |
| MODD flags byte | never set anywhere in vanilla |
| M2 material `0x01` | Unlit |
| Interior MODD | p10 39, p50 94, p90 192 |
| Exterior MODD | mean (51,51,63), 12.7% pure black — **never read by the client** |
| Orphan doodads | 0 of 70,228 |
| Instance stride | 80 bytes (`Matrix4x4` + `Vector4`), attribute 7, divisor 1 |
| Coordinate basis | raw WMO file space has **Z vertical**; `Basis = (x,y,z)->(x,z,-y)`; `M2ToWmo` is its inverse |

---

## 7. Files and responsibilities

| File | Responsibility |
|---|---|
| `Formats/WmoReader.cs` | Parse MODR into `DoodadRefs`; parse `MODD.color` into `ColorR/G/B/A` |
| `World/Wmo/WmoRenderer.cs` | `BuildDoodadLighting` (the gate); `Model.DoodadLight`; `EnumerateDoodads` carries the light |
| `World/Doodads/DoodadRenderer.cs` | `Instance.Light`, `InstanceData`, attribute 7, `Batch.Unlit`, `VertexColorScale`, `InteriorLighting`, `InteriorLitCount` |
| `Shaders/doodad.vert` | Attribute 7 -> `vLight`; `uInstanceLight` for the non-instanced path |
| `Shaders/doodad.frag` | `mix(baked, daylight, vLight.a)`; the `uUnlit` override |
| `Program.cs` | Doodads panel knobs; passes the light to `AddPlaced` |

---

## 8. Tuning — the Doodads panel

- **Baked interior light (MODD)** — untick to light every prop with the outdoor
  sun again. Takes effect **immediately**; the colour rides the instance buffer,
  so no reload is needed. This is the side-by-side.
- **Interior brightness** — the doodad copy of `VertexColorScale`. **Keep it
  equal to the Buildings slider** or props detach from the floor. 2.00 is vanilla.
- **"N with baked interior light"** — the diagnostic that matters. If this reads
  **0 while you are standing in a tavern**, MODR or the interior gate broke, not
  the shader. Check `DoodadRefs` is populated before touching anything else.

---

## 9. Not done — the honest ceiling

- **The downward-ray MOCV sampler was built and then not needed.** MODD.color is
  Blizzard's own authored answer, so there is no geometry to retain, no
  per-frame cost, and no ray casting. The sampler survives only as a
  cross-check. Do not resurrect it as the primary mechanism.
- **Terrain (ADT) doodads still take pure daylight.** Correct for vanilla, but
  it is the boundary of this system.
- **Nothing handles a doodad that straddles an interior/exterior boundary
  gracefully** — last writer wins. Not observed to matter.

---

## 10. Traps found in third-party sources

Every one of these was hit or nearly hit. Cross-check anything from these
projects rather than porting it.

| Source | Trap |
|---|---|
| wowdev wiki | Calls `MODD.color` a **tint**. It is not. |
| Neo / WoWEditor6 | Types `Modd.color` as `float` |
| Warcraft.NET | `MODDFlags.Unk_0x16 = 0x16` — decimal 22, not a bit |
| libwarcraft | Reads MOHD flags as `uint32` where the layout is `uint16 flags; uint16 numLod` |
| noggit-red | Likely `r += ((b * a / 64.f) - ...)` typo where noggit3 has `b += ...` |
| noggit3 / noggit-red | Compute a nearest-MOLT direction per doodad and **never read it** — exactly the bug we shipped once and backed out |
| WebWowViewerCpp | Its faithful BSP sampler is commented out |
| noclip.website | The only one sampling live; also short-circuits the ambient add with `if (false && ...)` |
