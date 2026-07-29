# Terrain chunk blending — the seams, and why they were there

Symptom, from Nico's Dun Morogh screenshot: two distinct artifacts on snow.

- **Sharp thin dark lines** forming a grid, one pixel wide, at every distance.
- **Soft dark smears** roughly a yard wide, following the same grid.

They look like one problem. They are two, with two unrelated causes, and both sit on the 33.33-yard
MCNK boundary — which is why they read as "chunks that aren't cleanly blended".

Both are fixed, and fixed **structurally** rather than corrected: there is now no UV wrap anywhere
in the terrain shader, and the alpha masks are a per-chunk array texture instead of an atlas, so
neither artifact has anywhere to come from. Benilla hit a milder version of the second and documents
its fix at `terrain.wgsl:252-258`; it never had the first, because it never wrapped a UV in a
shader.

A first pass fixed both in the fragment shader alone — multiply instead of `fract`, and a half-texel
inset on the atlas tap. That worked. It was also a workaround for a layout that should not have been
an atlas, so the shipped version goes further; the inset is gone because it is no longer needed.

---

## Artifact 1 — the sharp grid was `fract()` in the tileset UV

### What it was

```glsl
vec2 texUV = fract(vTileUV * CHUNKS) * uTextureScale;   // CHUNKS = 16, uTextureScale = 8
```

`vTileUV` is `gridCol / 128` (`TerrainTile.cs:286-287`), and a chunk edge is `gridCol = 8k`. So
`vTileUV * 16` crosses an integer *exactly* at every chunk boundary, and `fract` jumps 1 → 0 there.
After the `* 8` scale, that is a discontinuity of **8 whole texture repeats**, and critically it
lives **inside a triangle** rather than at a vertex.

### Why a value discontinuity becomes a dark line

Fragment LOD comes from screen-space derivatives evaluated across a 2×2 quad — including helper
lanes that lie outside the triangle. At a chunk's trailing edge the helper lane's `vTileUV * 16`
extrapolates just past the integer, its `fract` collapses from ~1.0 to ~0.0, and:

```
|d(texUV)/dx| ≈ 8.0 UV units per pixel
tileset is 256²  →  texels per pixel ≈ 8.0 × 256 = 2048
LOD = log2(2048) = 11, clamped to the deepest level a 256² texture has (8)
```

So that pixel row samples the **1×1 mip: the texture's flat average colour**. Ground tilesets
average to dark mud. Hence a one-pixel dark line along every chunk edge, in both axes, a 16×16 grid
per ADT — and it stays one pixel wide at any distance, because it is a derivative artifact rather
than a texture-space one.

Anisotropy makes it worse rather than better: aniso derives its sample footprint from the same
broken derivative, so a degenerate derivative gives a degenerate footprint.

### The fix

The UV is now **per chunk** rather than tile-wide — `(gridCol − IndexX·8)/8` and
`(gridRow − IndexY·8)/8`, emitted per vertex — so the tiling coordinate is a plain multiply with no
wrap to perform:

```glsl
vec2 texUV = vChunkUV * uTextureScale;   // 8 repeats per chunk
```

Algebraically identical to the old expression under a `GL_REPEAT` sampler, and that is provable
rather than hopeful. With `x = vTileUV * 16` (so `fract(x) == vChunkUV`):

```
fract(8 · fract(x)) = fract(8x − 8·floor(x)) = fract(8x)      since 8·floor(x) ∈ ℤ
```

and a `REPEAT` sampler only ever consumes `fract` of the coordinate. So the sampled texel is
unchanged; what changes is that `texUV` is now a **linear** function of position, its derivatives
are correct everywhere, and the wrap happens in the hardware address unit per tap.

The discontinuity between chunks does not vanish — it moves. Chunk A's edge vertex carries
`texUV = 8`, chunk B's carries `0`, and under `REPEAT` those are the same texel. Because that jump
sits on a **vertex boundary between two separate triangles**, the derivative inside each triangle is
the same constant rate on both sides, so implicit LOD and the aniso footprint match across the seam.
That is the whole trick, and it is why the wrap has to be the sampler's job and not the shader's.

Verified precondition: the tileset array is created with `repeat: true`
(`Texture.Array2D` → `ApplyParameters`, `Engine/Texture.cs:88,144-153`), mipmapped, with anisotropy.

Benilla does the same thing and has no `fract` anywhere in any of its terrain shaders —
`terrain.wgsl:247` is `let tiled = in.uv * t.params.x;` against a `Repeat` sampler
(`benilla-assets/src/terrain.rs:131-139`).

---

## Artifact 2 — the soft smears were the alpha atlas bleeding across chunks

### What it was

`TerrainTextures` packs all 256 chunks' 64×64 masks edge-to-edge into one 1024×1024 RGBA8 atlas
(`TerrainTextures.cs:44-46, 107, 143-146`) — no gutter, no padding, no border replication. Chunk
(cx,cy) owns texels `x ∈ [64cx, 64cx+63]`.

The shader sampled it with the tile-wide UV directly:

```glsl
vec3 splat = texture(uAlphaAtlas, vTileUV).rgb;
```

At the vertical border between chunk `cx-1` and `cx`:

```
u = 8cx / 128 = cx/16
texel coord t = u × 1024 = 64·cx          ← exactly an integer

bilinear: i0 = floor(t − 0.5) = 64cx − 1  ← LAST column of chunk (cx−1)
          i1 = 64cx                        ← FIRST column of chunk (cx)
          w  = 0.5

result = 0.5·A[64cx−1] + 0.5·A[64cx]
```

So every fragment on a shared chunk edge read **exactly half of the neighbouring chunk's blend
weights** — and then applied them to *its own* chunk's four texture indices (`vLayers` is `flat`,
from `TerrainTile.cs:141-144`), which are generally a different set of MTEX textures entirely.

That is a wrong-texture-at-wrong-weight band, not a subtle interpolation error. One alpha texel is
533.333 / 16 / 64 = **0.52 yd**, and the bilinear footprint reaches one texel either side, so the
contaminated band is about **1.04 yd wide**, falling linearly from 50% error at the seam to zero one
texel in. It reads *dark* because overlays are usually the darker layer (rock, dirt) over a lighter
base, and pulling one in at half strength where it does not belong darkens.

`ClampToEdge` on the atlas (`Texture.FromRgbaNoMips`, `Engine/Texture.cs:135-136`) does not help:
it clamps at the *atlas* border only, and these neighbours are genuinely adjacent inside it. **No
sampler state can fix this layout.**

### The fix — give each chunk its own texture

The masks are now a **256-layer 64×64 `GL_TEXTURE_2D_ARRAY`**, one layer per MCNK, `CLAMP_TO_EDGE`,
`LINEAR`, no mips (`Texture.ArrayRgbaNoMips`). The lookup is:

```glsl
vec3 splat = texture(uAlphaArray, vec3(vChunkUV, vAlphaLayer)).rgb;
```

A neighbouring chunk's texels are **not addressable at any UV**, so the problem is structurally
impossible rather than avoided, and `CLAMP_TO_EDGE` now means what it says: the outer half-texel is
constant-extended, exactly as the inset was faking.

It costs nothing. Same 4 MB per tile (256 × 64 × 64 × 4 = 1024 × 1024 × 4), same single bind, same
one draw call per tile. GL 3.3 core guarantees `GL_MAX_ARRAY_TEXTURE_LAYERS ≥ 256` — note we are
*at* that guarantee, not under it, so there is no headroom if anyone adds a layer.

Uploaded as `PixelFormat.Rgba`, not `Bgra` like the tileset: these are three independent blend
weights (R = layer 1, G = layer 2, B = layer 3), not a colour, so there is no channel convention to
honour. Getting this backwards would silently swap layers 1 and 3.

Chunks with no alpha data keep their zero-filled layer, which resolves to pure base texture — the
correct fallback.

### What benilla does, and why its version of this bug was milder

Benilla stores each chunk's alpha map as its **own layer of a `texture_2d_array`**, 64×64
`Rgba8Unorm`, one layer per chunk (`benilla-assets/src/terrain.rs:145-147`), with the layer index
carried per-vertex. Array layers never bleed into each other, so a neighbouring chunk's data is
structurally unreachable.

It still needed an inset, because it shares one sampler across the layer, alpha and shadow arrays
(Metal caps fragment samplers at 16, `benilla/src/terrain.rs:388-391`) and that sampler is `Repeat`
for the layer textures' sake. Under `Repeat`, a bilinear tap at `u → 0` wraps to texel 63 **of the
same chunk's own map**. `terrain.wgsl:252-258`:

```wgsl
// The old "repeat == clamp here" assumption is FALSE under LINEAR filtering: at a chunk edge
// (uv→0/1) the bilinear footprint WRAPS to the chunk map's opposite edge, blending unrelated
// weights → a thin seam at every chunk border (the creases; introduced when 228d336 switched
// Nearest→Linear). Inset by half a texel so the footprint clamps instead.
let auv = clamp(in.uv, vec2<f32>(0.5 / 64.0), vec2<f32>(1.0 - 0.5 / 64.0));
```

Same constant, same reasoning. **Our case was strictly worse**: theirs wrapped onto the chunk's own
opposite edge, ours read a genuinely different chunk's data.

Benilla applies the identical `auv` to its MCSH shadow sample (`terrain.wgsl:289`). We have no MCSH
yet — worth remembering when we add it, because the atlas's alpha channel is allocated and never
written (`TerrainTextures.cs:107` allocates ×4, `:146` writes offsets 0/1/2 only), so there is a
free 1024² byte plane sitting there that would inherit exactly this problem.

---

## Checked and found already correct

Two things that would each independently cause a chunk-edge line, and that this client already
handles:

**The 63×63 alpha map fix is present.** Vanilla's uncompressed 4-bit MCAL has garbage in the last
row and column. `AdtTerrainReader.cs:1717-1724` duplicates column 62 into 63 and row 62 into 63,
with the corner from (62,62) — the same fix benilla applies in `combined_alpha.rs:150-166`. Ours is
applied only to the 2048-byte path; benilla applies it to all encodings. For 1.12 that is a
distinction without a difference, since big-alpha ADTs effectively do not occur.

**The 4-bit expansion is full-range**, and slightly better than the reference:
`AdtTerrainReader.cs:1710-1711` does `v | (v << 4)` so 0xA → 0xAA (×17, reaching 255), where
benilla's `combined_alpha.rs:79-82` does `v * 16` (reaching only 240). Ours is the more correct
expansion. Noting it because it means our blends are ~6% stronger at full weight than benilla's, and
that is a real if invisible divergence if anyone ever pixel-compares the two.

**Vertex positions are bit-identical across a boundary.** Each chunk emits its own copy of the 9
shared edge vertices, but `wx`/`wy` are computed from `gridRow`/`gridCol` in `double`
(`TerrainTile.cs:267-268`) and chunk k's `8k+8` is the same expression as chunk k+1's `8(k+1)+0`.
No geometric crack.

---

## Still open, in priority order

1. **MCSH is still absent.** Now cheap to add correctly: a second `R8` array texture on the same
   per-chunk layer index, sampled with the same `vChunkUV`. Under the old atlas it would have
   inherited the bleeding exactly; under the array it cannot. Note the alpha array's fourth channel
   is allocated and unwritten, so it is also available if a separate texture is not wanted.
2. **Normals are per-chunk and unwelded.** `TerrainTile.cs:275-279` takes each duplicate edge
   vertex's normal from its own chunk's MCNR. Vanilla authors these to agree, so it is usually fine
   — but nothing enforces it, and the NaN guard tests only `normal.X`, so a NaN confined to Y or Z
   slips through and poisons the lighting. If a grid survives both fixes above, check `uDebugMode 1`
   (normals) — a line that persists there is this, not texturing.
3. **`TerrainTextures.TransposeAlpha` (`:49`) was never verified.** It is a global applied at build
   time (`:143-144`). If it is wrong, every chunk's mask is transposed within itself, which reads as
   heavy per-chunk mismatch. Cheap to A/B against `uDebugMode 4` (raw splat).
4. **A chunk with an unresolvable base layer renders entirely procedural.** `TerrainTextures.cs:126`
   yields `-1` when an MTEX entry failed to decode *or* had different dimensions from the first kept
   texture (`:91-99`, array textures need uniform size), and `terrain.frag` then falls the whole
   chunk to the slope palette. That is a chunk-shaped discontinuity far louder than anything above.
   Watch stdout for `[terrain]` texture-skip lines.
5. **`GL_MAX_ARRAY_TEXTURE_LAYERS` is exactly 256 on a conformant GL 3.3 baseline**, and the alpha
   array uses all 256. That is legal everywhere but leaves no room — if a per-chunk layer is ever
   needed for something else, it needs its own texture rather than an extra slice of this one.
