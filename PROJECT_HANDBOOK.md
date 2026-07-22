# MSUI Client — Project Handbook

**A native C# client for World of Warcraft 1.12.1 (build 5875), talking to a private VMaNGOS server.**

Version: Draft 2 — 2026-07-22
Supersedes: `MSUI_CLIENT_DESIGN.md` (browser-era architecture, now abandoned — see §2)

---

> **If you are a fresh assistant picking this project up cold, read §0, §1, §3 and §4 before touching anything.** §3 lists facts that were established empirically and cost real effort to nail down; §4 lists what is verified versus what is still a guess. Re-deriving §3 from first principles will waste hours and probably reach a wrong answer, because several of these facts are counter-intuitive.

---

## 0. Orientation — what this is in one page

Nico runs a private VMaNGOS server (WoW 1.12.1 vanilla) plus **MangosSuperUI**, an ASP.NET Core admin and content-creation web app he built for it. This project is a **separate, standalone game client** — a real playable client, not a viewer — written in C# on Silk.NET/OpenGL.

**Why it exists.** Two goals converged:
1. A long-standing wish to see WoW 1.12 rendered in a painterly, hand-painted art style. Owning the renderer makes that a shader variant instead of a fight with a platform.
2. A working multiplayer client for his own realm — quest, kill, dungeon, raid, craft — where his AiBot fleet, real 1.12 clients, and this client all coexist.

**Core design stance.** The client reads WoW's own files directly (MPQ → BLP/ADT/M2/WMO/DBC) and speaks the genuine 1.12.1 network protocol. No asset server, no bake step, no format conversion, no coordinate conversion. The server is unmodified.

**Current state (2026-07-22). It runs.** Elwynn Forest renders from the client's own MPQs at 60 fps: 9 ADT tiles, 589,504 triangles, correct 4-layer tileset splat, MCNR-lit, terrain holes punched, frustum culled. `[verify] PASS delta -0.00` — the client independently reproduces the server's own ground height at the Northshire spawn.

Verified on Intel Iris Xe, OpenGL 3.3.0 (integrated graphics — so the perf floor is low).

Not started: collision (vmaps parsed but unused), doodads/M2, WMO buildings, liquid rendering, networking.

---

## 1. Repository layout

Fully standalone. No project reference to MangosSuperUI.

```
MSUIClient/                          <- repo root, open MSUIClient.sln here
├── MSUIClient.sln
├── SETUP.md                         quick start: file placement, config, build
├── PROJECT_HANDBOOK.md              this file
├── .gitignore
├── .gitattributes                   CRLF for C#/shaders/config, LF for markdown
└── MSUIClient/                      project folder
    ├── MSUIClient.csproj
    ├── client-config.json           gitignored — per-machine paths
    ├── client-config.json.example
    ├── Program.cs                   entry point + GameLoop + ImGui HUD
    ├── ClientConfig.cs              config model, JSON load, validation
    │
    ├── Engine/                      platform + GPU primitives, no game logic
    │   ├── ClientWindow.cs          window, GL context, main loop, input, ImGui
    │   ├── Camera.cs                orbit camera, view/projection, frustum planes
    │   ├── Shader.cs                compile/link/uniform cache
    │   └── Texture.cs               2D + 2D-array textures, BGRA upload
    │
    ├── World/                       world representation and rendering
    │   ├── TerrainTile.cs           one ADT -> VAO/VBO/EBO mesh
    │   ├── TerrainTextures.cs       tileset array texture + alpha atlas
    │   └── TerrainRenderer.cs       tile set, culling, draw, height queries
    │
    ├── Formats/                     Blizzard file formats (copied from SuperUI)
    │   ├── Mpq/
    │   │   ├── MpqCrypto.cs
    │   │   ├── MpqArchive.cs
    │   │   ├── PkwareExplode.cs
    │   │   └── MpqArchiveWriter.cs  (optional; only needed to write patches)
    │   ├── BlpDecoder.cs            BLP2 -> BGRA
    │   ├── AdtTerrainReader.cs      ADT terrain (see §3.4)
    │   └── VmapFormat.cs            .vmtile placements + .vmo collision meshes
    │
    └── Shaders/
        ├── terrain.vert
        └── terrain.frag
```

### 1.1 Where each file's responsibility ends

| File | Owns | Does NOT own |
|---|---|---|
| `Program.cs` | Startup, the game loop, HUD | Rendering internals, parsing |
| `ClientConfig.cs` | Config schema + validation | Anything runtime |
| `ClientWindow.cs` | GL context, loop, raw input, ImGui lifetime | What gets drawn |
| `Camera.cs` | View/projection matrices, frustum | Input handling, movement rules |
| `Shader.cs` | GLSL compile/link, uniform locations | Which uniforms exist |
| `Texture.cs` | GL texture objects, pixel upload | Decoding BLP |
| `TerrainTile.cs` | One tile's geometry, GPU buffers | Which tiles to load |
| `TerrainTextures.cs` | One tile's tileset array + alpha atlas | Geometry |
| `TerrainRenderer.cs` | Tile set, culling, height sampling | Per-tile detail |
| `Formats/*` | Pure parsing, no GL, no game logic | Rendering, gameplay |

**Rule:** nothing in `Formats/` may reference Silk.NET or GL. That boundary is what keeps the parsers testable and shareable.

---

## 2. History — why it is native, and what was thrown away

The project began as a **browser client** (TypeScript, three.js, WebGL). That build got as far as: terrain and collision baked server-side to GLB, a manifest over HTTP, a WebSocket↔TCP bridge inside SuperUI, and a character controller. It rendered and it very nearly walked.

It was abandoned on 2026-07-21, deliberately, for one reason: **almost none of what it forced us to build was game code.**

```
Browser:  ADT -> C# parse -> GLB write -> HTTP -> JS parse -> GPU
Native:   ADT -> C# parse -> GPU
```

The bake pipeline (`TerrainTileExporter`, `CollisionTileExporter`, `WorldExportController`), the GLB conversion, the `height.bin` sidecar, the manifest, and `WowSocketBridge` all existed *solely* because a browser cannot open an MPQ or a TCP socket. Nico already had C# that reads every one of those formats. Going native deleted that entire layer.

### 2.1 What survived the pivot

- **All format knowledge.** Every fact in §3 was established during the browser build and is still exactly correct.
- **`AdtTerrainReader.cs`** including the MCVT/MCNR work — this is the single most valuable artifact from that era.
- **`VmapFormat.cs`** — `.vmtile` and `.vmo` readers, validated against real bytes.
- **The phase plan** (§7), essentially unchanged.
- **The protocol work** — opcode and update-field tables, and the generators that produce them, need only be retargeted from TypeScript to C#.

### 2.2 What is dead

In the SuperUI repo, these are now unused and safe to delete: `TerrainTileExporter.cs`, `CollisionTileExporter.cs`, `WorldExportController.cs`, `WowSocketBridge.cs`, and the entire `msui-client/` TypeScript tree.

**Do not delete** the MCVT/MCNR additions to `AdtTerrainReader.cs`. They are what this client renders from.

### 2.3 The shared-code decision

The seven files in `Formats/` were **copied** from SuperUI and their namespaces changed to `MSUIClient.Formats[.Mpq]`. They are now independent and will drift.

This was chosen over a shared library on purpose: the client is meant to be its own project, and these are 2006 file formats that do not change. The cost is that a genuine parser bug must be fixed in two places. If that ever becomes painful, extract a `MangosSuperUI.Formats` class library and have both sides reference it — the namespaces can stay as they are, because assembly name and namespace are independent in .NET.

---

## 3. Ground truth — established facts, do not re-derive

Everything in this section was worked out empirically against Nico's actual server and client files. Several are counter-intuitive. **Treat them as settled.**

### 3.1 Coordinate system — there is no conversion anywhere

WoW world space, used end to end, unmodified:

```
+X = north      +Y = west       +Z = up
orientation = radians CCW about +Z, measured from +X
```

The browser build needed a conversion module because three.js hardcodes Y-up. **OpenGL does not care** — it only needs a consistent view matrix, and `Camera` passes `Vector3.UnitZ` as up. So positions from ADT, vmaps, DBC and the network all mean the same thing everywhere, and the HUD prints values directly comparable to `.gps` in a real client.

**If you find yourself writing an axis swap or a negation, stop.** It is almost certainly wrong. This decision removed an entire category of bug and is not up for casual revision.

### 3.2 Tile indexing — the axes are swapped, and both files exist

ADT and vmap filenames are `{col}_{row}` where:

```
col = floor(32 - worldY / 533.33333)     <- FIRST number, from Y
row = floor(32 - worldX / 533.33333)     <- SECOND number, from X
```

The first number comes from **Y**, the second from **X**. This is the opposite of the obvious reading.

**Northshire** (human start, `playercreateinfo` race 1): `x = -8949.95, y = -132.493, z = 83.5312`, map 0, zone 12 → tile **`[col 32, row 48]`**.

How it was settled: `000_32_48.vmtile` contains `Elwynntreecanopy*.m2`; `000_48_32.vmtile` contains `Farm.wmo`. **Both files exist**, so checking for file existence does *not* disambiguate — only content does. An earlier derivation got this backwards and was corrected by `strings` output.

**Additional trap:** `AdtTerrainReader.ReadFromMpq(clientDataPath, mapName, gridX, gridY)` takes **`gridX = row, gridY = col`** — inverted relative to the filename. See the comment at its line ~117. `TerrainTile.Load` handles this; anything new calling it must too.

### 3.3 The verified terrain chain

On 2026-07-21 the following was confirmed against the live server:

```
Probe:        ok, 256/256 chunks with heights, tile [32,48],
              Elwynn tilesets, 785 doodads, 8 WMOs
VerifyHeight: sampled 83.53 vs expected 83.5312, delta 0.00,
              grid coverage 16641/16641 non-zero, PASS
```

That single result validates: tile index order, the MCVT parse, `BaseZ`, the height-grid mapping, and bilinear sampling. **Do not second-guess any of them.**

**Important subtlety:** that check validated the *height grid* mapping — tile origin plus `chunk.IndexX`/`IndexY` — and **not** the `BaseX`/`BaseY` mapping the old browser exporter used for vertex positions. `TerrainTile.cs` therefore derives vertex positions from the proven mapping:

```
originX = (32 - row) * 533.33333          tile north-west corner
originY = (32 - col) * 533.33333

gridRow = chunk.IndexY * 8 + row          0..128 across the tile
gridCol = chunk.IndexX * 8 + col

worldX  = originX - gridRow * CELL_SIZE   X decreases going south
worldY  = originY - gridCol * CELL_SIZE   Y decreases going east
worldZ  = chunk.BaseZ + mcvtHeight
```

`CELL_SIZE = 33.3333 / 8 ≈ 4.16667`. Mesh and height query use identical arithmetic, so what you see and what you stand on cannot disagree.

### 3.4 ADT / MCNK layout

`AdtTerrainReader` parses: MTEX texture names, MCNK chunks, MCLY layers, MCAL alpha (all three encodings), MCVT heights, MCNR normals, MCLQ liquid, MDDF doodad and MODF WMO placements, and the hole bitmask.

**MCVT is interleaved, not a flat grid.** 145 floats per chunk, ordered 9 outer, 8 inner, 9 outer, 8 inner… (9 outer rows + 8 inner rows = 81 + 64 = 145).

```
outer vertex (col, row) -> index row * 17 + col
inner vertex (col, row) -> index row * 17 + 9 + col
```

Inner vertices sit at the centre of the four surrounding outer vertices. Use `OuterHeight` / `InnerHeight` / `WorldHeightAt` rather than indexing raw.

Heights are **relative to `BaseZ`**. `BaseX`/`BaseY`/`BaseZ` come from MCNK header `0x68`/`0x6C`/`0x70`, and the file stores them in **(Y, X, Z)** order — `AdtTerrainReader` untangles this at the read site, so the properties mean what their names say.

MCNR normals are 145 × 3 signed bytes, `127 = 1.0`, stored `(x, z, y)`; `NormalAt()` reorders to `(x, y, z)`.

**Tessellation must be 4 triangles per cell**, fanned around the inner vertex. This is what the real client does and why MCVT carries inner vertices at all. Two-triangle quads visibly flatten ridges.

### 3.5 Terrain texturing

Each ADT lists up to ~16 textures in MTEX. Each of its 256 MCNK chunks selects up to 4 (MCLY) and blends them with 64×64 alpha masks (MCAL). Layer 0 is the base and has no mask.

`AdtTerrainReader.ParseMcal` normalises all three MCAL encodings — 2048-byte 4-bit packed, 4096-byte 8-bit `bigAlpha`, and RLE-compressed — into a uniform 64×64 stride-64 buffer indexed `[py * 64 + px]`. It also repairs the garbage last row/column present in the old 2048-byte format, which otherwise shows as hard lines at chunk seams.

**Render approach:** one `GL_TEXTURE_2D_ARRAY` per tile holding its whole tileset, plus one 1024×1024 RGBA alpha atlas (16×16 chunks × 64×64; R = layer 1, G = layer 2, B = layer 3). Each vertex carries its chunk's four array indices as a `flat` attribute. **One draw call per tile** instead of 256.

### 3.6 vmap collision formats

Both magic `VMAP_7.0`. Source of truth: `vmangos/src/game/vmap/` — `TileAssembler.cpp`, `WorldModel.cpp`, `BIH.cpp`.

**`.vmtile`** — model placements for one tile:
```
"VMAP_7.0" (8 bytes)
u32 nSpawns
per spawn:
  u32 flags
  u16 adtId
  u32 id
  Vector3 pos
  Vector3 rot          Euler DEGREES
  f32 scale
  [AABox lo, hi]       6 floats, ONLY if flags & MOD_HAS_BOUND
  u32 nameLen
  char[nameLen]        NOT null-terminated
  u32 nodeIndex        <- trailing, one per spawn
```

**The trailing `nodeIndex` is the trap.** Miss it and every spawn after the first is misaligned by 4 bytes and parses into plausible garbage.

Flags: `MOD_M2 = 1`, `MOD_WORLDSPAWN = 2`, `MOD_HAS_BOUND = 4`.

**`.vmo`** — collision geometry for one model:
```
"VMAP_7.0" · "WMOD" u32 chunkSize u32 RootWMOID
"GMOD" u32 groupCount          <- NO chunkSize; commented out in the source
  per group:
    AABox bound (6 floats) · u32 mogpFlags · u32 groupWMOID
    "VERT" u32 chunkSize u32 count  Vector3[count]
    "TRIM" u32 chunkSize u32 count  MeshTriangle[count]   (3 x u32)
    "MBIH" <BIH blob>
    "LIQU" u32 chunkSize [WmoLiquid if > 0]
"GBIH" <BIH blob>
```

`VERT`/`TRIM` `chunkSize` **includes** the 4-byte count (verified: 699 verts → 699×12 + 4 = 8392).

BIH blob layout, from `BIH::writeToFile`: `float lo[3], float hi[3], u32 treeSize, u32[treeSize], u32 count, u32[count]`. Exact size = `24 + 4 + treeSize*4 + 4 + count*4`. **The BIH never needs parsing** — we build our own acceleration structure — but it must be skipped by exact arithmetic.

**Spawn transform:**
```
world = pos + Rz(pi*rot.y/180) * Ry(pi*rot.x/180) * Rx(pi*rot.z/180) * (vertex * scale)
```
The **y/x/z argument order** of G3D's `fromEulerAnglesZYX` is not a typo. Getting it wrong scatters every doodad in the world.

`mogpFlags`: `0x8` outdoor, `0x2000` indoor.

### 3.7 Network protocol (Phase 2 — not started)

**Opcode values, 1.12.1 build 5875:**

| Opcode | Value |
|---|---|
| `SMSG_AUTH_CHALLENGE` | 492 |
| `CMSG_AUTH_SESSION` | 493 |
| `CMSG_CHAR_ENUM` | 55 |
| `CMSG_PLAYER_LOGIN` | 61 |
| `SMSG_LOGIN_VERIFY_WORLD` | 566 |
| `SMSG_UPDATE_OBJECT` | 169 |
| `SMSG_COMPRESSED_UPDATE_OBJECT` | 502 |
| `SMSG_MONSTER_MOVE` | 221 |
| `MSG_MOVE_HEARTBEAT` | 238 |

825 opcodes total, `NUM_MSG_TYPES = 828`.

**Sources.** Never hand-write opcode values or update-field offsets. Generate them:
- Opcodes: `vmangos/src/game/Protocol/Opcodes_1_12_1.h` (`enum OpcodesList`, decimal values)
- Update fields: `UpdateFields_1_12_1.cpp` — the flat `g_updateFieldsData` table, **not** the `.h`, which is enum arithmetic. 324 rows = 316 real fields + 8 `_END` sentinels.
- Message layouts: `gtker/wow_messages` ships a machine-readable `intermediate_representation.json` (~28 MB) covering 1.12 specifically, plus a JSON schema. `gtker/wow_srp` implements vanilla SRP6 and header crypto. `gtker/wow_dbc` covers DBC.
- Ambiguities: VMaNGOS's own handler source is the tiebreaker. It is literally the machine we talk to.

Block sizes: PLAYER 1282 slots, UNIT 188, GAMEOBJECT 26, CORPSE 38.

**Server config is already permissive** — no changes needed:
```
realmd            0.0.0.0:3724
world             0.0.0.0:8085
Anticheat.Enable  0
Warden.*Enabled   0
Network.KickOnBadPacket 0     <- unknown opcodes won't drop us
```

### 3.8 Server environment

```
DataDir           /home/wowvmangos/vmangos/run/data
  maps/           .map — "MAPSz1.4", AREA/MHGT/MLIQ chunks (GridMap.h)
  vmaps/          3490 .vmo, 1251 .vmtile
  mmaps/          1916 .mmtile — Detour navmesh, unused so far
client MPQs       /home/wowvmangos/wowclient/Data
source            /home/wowvmangos/vmangos/src
git rev           c5711527a
```

**`server-config.json` overrides `appsettings.json`** in SuperUI (loaded after it), so the `/home/wowvmangos` paths win over the `/opt/vmangos` ones. This has caused confusion before.

The client itself needs **local** copies: a WoW 1.12.1 `Data` folder on the machine running it, and optionally a copy of `run/data/vmaps` (~580 MB) for building/doodad collision.

---

## 4. Verified vs unverified — read before debugging

### Verified against reality

| Thing | How |
|---|---|
| Tile index order, Northshire = [32,48] | `strings` on both candidate `.vmtile` files |
| MCVT parse, all 256 chunks | Server `Probe`: 256/256 |
| `BaseZ`, height-grid mapping, bilinear sample | Server `VerifyHeight`: delta **0.00** |
| `.vmtile` layout incl. trailing `nodeIndex` | Parsed real bytes; two spawns, exact offsets |
| `.vmo` layout, VERT/TRIM chunk sizes | Parsed real bytes from `1000Needlesbridge.wmo.vmo` |
| BIH blob size arithmetic | Read from `BIH::writeToFile` source |
| Opcode values (825) | Generated from `Opcodes_1_12_1.h`, spot-checked |
| Update fields (316) | Generated, cross-checked vs declared array size 324 |
| BLP → BGRA decode | Renders every item icon in SuperUI today |
| **Native end-to-end render** | Elwynn on screen, 60 fps, 9 tiles, 589,504 tris |
| **`[verify]` on the native client** | sampled 83.53 vs expected 83.53, delta -0.00 |
| **Alpha map orientation** | `TransposeAlpha = false` — road, grass and cliffs all land correctly |
| **Texture repeat rate** | 8.0 per chunk looks right visually |
| **Triangle winding** | CCW front face is correct; terrain is solid from above |
| **Vertex mapping from tile origin** | §3.3 mapping produces correct geometry, not just correct heights |
| **Hole mask** | `IsHole` punches visible gaps in cliff faces where expected |

### Not yet verified — expect these to be where bugs are

| Thing | Where | Symptom if wrong | Fix |
|---|---|---|---|
| Liquid rendering | Not written | Water shows as bare terrain | MCLQ is parsed, just not drawn |
| Everything about collision | Not written | Walk through everything | vmaps parsed, no BVH yet |
| Everything about doodads / WMO | Not written | No trees, no buildings | Needs M2 + WMO readers |
| Everything about networking | Not written | — | — |

Everything in the previous "unverified" list is now confirmed — see the table above.

---

## 5. Runtime architecture

### 5.1 Startup

```
Program.Main
  ClientConfig.Load           find repo root, resolve relative paths,
                              validate, count MPQs and vmaps
  new ClientWindow            (no GL yet)
  window.Run
    HandleLoad                GL context, input, ImGui, GL state
      GameLoop.Load
        new TerrainRenderer
        LoadShaders           Shaders/terrain.{vert,frag}
        LoadAround            (start position, radius) -> N tiles
          per tile: TerrainTile.Load
            AdtTerrainReader.ReadFromMpq(path, map, row, col)
            TerrainTextures.Build      tileset array + alpha atlas
            build interleaved vertices + indices
            upload VAO/VBO/EBO
          per tile: BuildHeightGrid    129x129 CPU-side
        VerifyAgainst          self-check vs known spawn height
    loop: HandleUpdate -> HandleRender
```

### 5.2 Vertex format

12 floats, interleaved, one VBO:

| Attr | Location | Components | Offset (floats) |
|---|---|---|---|
| Position | 0 | 3 | 0 |
| Normal | 1 | 3 | 3 |
| TileUV | 2 | 2 | 6 |
| LayerIndices | 3 | 4 | 8 |

`TileUV` runs 0..1 across the whole ADT tile. The fragment shader derives per-chunk texture UVs as `fract(TileUV * 16) * uTextureScale`, and samples the alpha atlas with `TileUV` directly so atlas texels line up with chunk boundaries.

`LayerIndices` **must** be declared `flat` in GLSL — interpolating array indices across a triangle samples garbage.

### 5.3 Texture units

| Unit | Sampler | Contents |
|---|---|---|
| 0 | `uTileset` | `sampler2DArray`, one layer per MTEX texture |
| 1 | `uAlphaAtlas` | `sampler2D`, 1024×1024 RGBA masks |

Alpha atlas is **clamped and unmipmapped** on purpose — mipmapping a splat mask bleeds neighbouring chunks together at distance and shows as seams.

### 5.4 Debug modes

`TerrainRenderer.DebugMode`, exposed in the HUD:

| Mode | Shows | Diagnoses |
|---|---|---|
| 0 Textured | Full splat | Normal operation |
| 1 Normals | `normal * 0.5 + 0.5` | Normal correctness, MCNR parse |
| 2 UVs | `fract(TileUV * 16)` | UV mapping, chunk boundaries |
| 3 Flat | Solid grey | Silhouette and geometry alone |
| 4 Splat mask | Alpha atlas as RGB | **Alpha orientation** |
| 5 Untextured | Slope/altitude palette | Geometry when texturing is broken |

Mode 4 is the important one: it isolates alpha orientation from whether textures loaded at all.

---

## 6. Troubleshooting playbook

### 6.1 Build errors

| Error | Cause | Fix |
|---|---|---|
| `TriangleFace` / `CullFaceMode` not found | Silk.NET renamed GL enums between versions | Match the installed version; both names exist historically |
| `ImGuiController` not found | Namespace moved | `Silk.NET.OpenGL.Extensions.ImGui` |
| Silk.NET package not found | 2.21.0 unavailable | Bump **all five** Silk packages together to the same version |
| `MSUIClient.Formats` types missing | Namespaces not renamed on copy | See SETUP.md table — one line per file, two for `AdtTerrainReader` |
| `SkiaSharp` missing | Only `AdtTerrainReader`'s PNG helpers need it | Keep the package, or strip those methods |
| `'Texture' is an ambiguous reference` | `Silk.NET.OpenGL` has its own `Texture` / `Shader` | Add `using Texture = MSUIClient.Engine.Texture;` (same for `Shader`). Rename ours to `GlTexture`/`GlShader` if it spreads |
| `The name 'MangosSuperUI' does not exist` | A fully-qualified reference the namespace rewrite missed | `AdtTerrainReader` line ~185 has `MangosSuperUI.Services.Mpq.MpqArchive.Open` — grep for any remaining `MangosSuperUI.` |
| `McnkChunk` has no `Heights` / `IsHole` / `BaseZ` | The **original** `AdtTerrainReader` was copied, not the extended one | Use the version with MCVT/MCNR support (~1979 lines) |

### 6.2 Black screen / nothing renders

Work down this list in order:

1. **Console `[verify]` line.** PASS means parsing and mapping are fine and the problem is rendering.
2. **`[terrain]` lines.** No tiles loaded → `ClientDataPath` is wrong or the ADT is absent.
3. **HUD triangle count.** Zero → mesh build failed. Non-zero → geometry exists.
4. **Debug mode 3 (Flat).** Visible → texturing is the problem, not geometry.
5. **Disable `CullFace`** in `ClientWindow.HandleLoad`. Terrain appears → **winding is inverted**, flip `FrontFace`.
6. **Camera position.** HUD shows WoW coords; compare against the tile bounds printed at load.

### 6.3 Terrain looks wrong

| Symptom | Likely cause | Where |
|---|---|---|
| Right materials, wrong places | Alpha orientation | `TerrainTextures.TransposeAlpha` |
| Textures too large or too small | Repeat rate | HUD "Texture repeat" slider |
| Hard lines at chunk seams | Alpha atlas filtering, or MCAL edge repair | `Texture.FromRgbaNoMips` |
| Terrain flat / ridges missing | Cell tessellation reverted to 2 triangles | `TerrainTile.Load` |
| Spiky garbage vertices | MCVT interleave misread | `TerrainTile.Load`, the 9/8 loop |
| Lighting wrong, normals odd | MCNR component order | `AdtTerrainReader.NormalAt` |
| Holes not punched | Hole bitmask | `McnkChunk.IsHole` |
| Untextured grey everywhere | `vLayers.x < 0` — no textures loaded | Check `[terrain] textures: 0/N` |

### 6.4 Sanity checks that pay for themselves

- **`[verify]` at startup** — `TerrainRenderer.VerifyAgainst` compares sampled ground at the spawn against `83.53`. Under ~2 m is correct.
- **HUD ground vs Z** — with ground-snap on, `Z` should equal `ground + eyeOffset`.
- **Tile index in HUD** — standing at the spawn must read `[32, 48]`.

### 6.5 Rules learned the hard way

- **Never let a fetch or parse failure return null silently.** A missing height grid once presented as a physics bug: the character fell 5,300 units over 23 seconds with no error anywhere. Every failure path must log loudly and name its likely cause.
- **When two candidate interpretations both "exist", existence is not evidence.** Both `000_32_48.vmtile` and `000_48_32.vmtile` are real files. Only their *contents* settled it.
- **Validate binary parsers against real bytes before writing the C#.** Every format in §3.6 was prototyped in Python against a hexdump first. Both caught real mistakes.
- **Assert the invariants the format states about itself.** `VERT`/`TRIM` `chunkSize == count*12 + 4`; the update-field table declares its own row count. Free correctness checks.
- **Never transpose a matrix for GLSL when uploading with `transpose: false`.** System.Numerics is row-major in memory; GL reads those bytes as column-major, which *is* the flip GLSL needs. Transposing first double-flips: every vertex lands in garbage clip space and the screen shows only the clear colour. This looks exactly like "geometry isn't rendering" — draw calls fire, tile counts are right, culling reports sensible numbers. Cost an hour. See the comment on `Camera.ViewProjection`.
- **Keep shaders and scripts pure ASCII.** One em-dash in a comment made Intel's GLSL compiler report `pre-mature EOF` on a complete shader, and the same character made PowerShell report brace mismatches across a whole file. Both diagnostics point everywhere except the offending byte, and it is invisible when you print the file. `Shader.Sanitize` now strips non-ASCII and BOMs defensively.
- **A HUD that prints the raw numbers pays for itself.** Live WoW coordinates, tile index, ground height and camera position turn "it looks wrong" into a specific claim. `[verify]` at startup does the same for the parse chain.

---

## 7. Phase plan

| Phase | Deliverable | Status |
|---|---|---|
| **1** | Northshire offline: terrain, textures, collision, character controller | **terrain + textures DONE and verified**; collision and controller remain |
| **2** | Enter world: SRP6, header crypto, char select, spawn, other entities moving, chat | not started |
| **3** | Combat: targeting, autoattack, one spell, damage, death, loot, XP | not started |
| **4** | Systems: quests, bags, vendors, trainers, professions, mail, groups | not started |
| **5** | Dungeons: WMO interiors, instance transfer, Deadmines | not started — softest estimate |
| **6** | Raids: 40-entity perf, raid frames | not started |
| **7** | Painterly render mode | parallel from P4 |

Rough solo part-time estimate: questing playable ~month 6–7, dungeons ~month 9–11. Native is expected to be somewhat faster than the browser estimate because the asset pipeline is gone.

### 7.1 Immediate next steps

1. **Collision** — `VmapFormat` is written and validated but nothing consumes it. Needs: load `.vmtile` placements for the loaded tiles, resolve each to its `.vmo`, transform triangles to world space (§3.6), build a BVH, then a character controller (sweep, wall slide, step-up, gravity, ground snap). The browser build's `controller.ts` ports nearly line for line. Requires populating `GameData\vmaps` (~580 MB from the server's `run/data/vmaps`).
2. **Doodads** — M2 rendering. Needs an `M2Reader` in `Formats/`. Trees and fences are the single biggest visual win per unit effort, and the ADT already gives us 785 placements on the Northshire tile alone.
3. **Liquid** — MCLQ is parsed into `MclqLayer` but never drawn; water currently shows as bare terrain.
4. **WMO** — buildings. Also the gate for Phase 5 dungeons, and the least-documented of the three world formats.
5. **Tile streaming** — currently loads a fixed block at startup (6.8 s for 9 tiles, two ADT reads per tile). Needs load/unload as the camera moves, and the double read collapsed into one.

### 7.2 Deliberately out of scope, permanently

Warden/anti-cheat compatibility. FrameXML/Lua addon compatibility (UI is ImGui, later custom). Sound. Cinematics. Glue screens. Patcher. Multi-realm. Locales other than enUS. Any client version other than 1.12.1. Retail servers — this client will never implement Warden responses, so it structurally cannot be used where anticheat matters.

---

## 7.3 Environment and how to run

**Dev machine (Windows).** The client runs here. Needs its own copy of the WoW
1.12.1 `Data` folder — the repo keeps it at `GameData/Data`, which is
gitignored. `setup-gamedata.ps1 -CopyFrom "C:\WoW Vanilla"` populates it.

**Path resolution.** Relative paths in `client-config.json` resolve against the
REPO ROOT, found by walking up from the exe looking for `MSUIClient.sln` — not
against the working directory, because `dotnet run`, F5 and a published exe all
differ. Absolute paths pass through untouched.

**Server (192.168.0.2, WSL).** Not needed until Phase 2. Runs VMaNGOS plus the
MangosSuperUI web app. Anticheat and Warden are already off; realmd 3724, world
8085.

```powershell
cd C:\Users\nico\source\repos\MSUIClient
dotnet build
dotnet run --project MSUIClient
```

Expected console on a healthy run: config paths, MPQ count, GL renderer,
`[shader] terrain compiled and linked`, nine `[terrain]` tile pairs, then
`[verify] PASS`. Startup is ~7 s, dominated by ADT parsing.

**Controls.** WASD move, Shift boost, hold mouse to look, wheel to zoom,
Space/Ctrl for height when ground-snap is off, Esc to quit. `window.uiScale`
in the config scales the HUD (1.8 default; raise it on a 4K panel).

**Known cosmetic gaps.** Water renders as bare terrain (MCLQ parsed, not drawn).
`BlastedLandsBlack.blp` is 8x8 rather than 256x256 and is skipped from the
texture array with a console note — correct behaviour, not a bug.

---

## 8. Working agreements

- **CRLF** for `.cs`, shaders and config; **LF** for markdown. Enforced by `.gitattributes`.
- **Complete file replacements**, not diffs, when handing over changed files.
- **Ask for the relevant file before writing code against it.** Guessing at an existing signature wastes a build cycle.
- **Say plainly what each delivered file is and whether it needs action.** Downloads arrive flat with no directory structure, so every handover must state where files go.
- **Empirical verification over documentation.** Every important claim in §3 was checked against real data, and two were wrong on the first attempt.
