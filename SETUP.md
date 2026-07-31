# MSUI Client — Setup

Native C# client for VMaNGOS 1.12.1 (client build 5875). Standalone: no
dependency on MangosSuperUI or anything else.

## Folder layout

```
MSUIClient/                        <- repo root, open MSUIClient.sln here
├── MSUIClient.sln
├── .gitignore
├── .gitattributes
├── SETUP.md
└── MSUIClient/                    <- project folder
    ├── MSUIClient.csproj
    ├── client-config.json         <- gitignored; copy from the .example
    ├── client-config.json.example
    ├── Program.cs
    ├── ClientConfig.cs
    ├── Engine/
    │   ├── ClientWindow.cs
    │   └── Camera.cs
    ├── Formats/                   <- files you bring over (see below)
    │   ├── Mpq/
    │   │   ├── MpqCrypto.cs
    │   │   ├── MpqArchive.cs
    │   │   ├── PkwareExplode.cs
    │   │   └── MpqArchiveWriter.cs
    │   ├── BlpDecoder.cs
    │   ├── AdtTerrainReader.cs
    │   └── VmapFormat.cs
    ├── Render/                    <- coming next
    └── Shaders/                   <- coming next
```

## Files to bring over

Copy these seven from the SuperUI repo, then change the namespace line at the
top of each. Namespace edits are one line per file (two for
AdtTerrainReader), and nothing else in the files needs touching.

| Copy from SuperUI | To | Change namespace |
|---|---|---|
| `Services/Mpq/MpqCrypto.cs` | `Formats/Mpq/` | `MangosSuperUI.Services.Mpq` → `MSUIClient.Formats.Mpq` |
| `Services/Mpq/MpqArchive.cs` | `Formats/Mpq/` | same |
| `Services/Mpq/PkwareExplode.cs` | `Formats/Mpq/` | same |
| `Services/Mpq/MpqArchiveWriter.cs` | `Formats/Mpq/` | same (optional — write path, only needed if the client ever builds patches) |
| `Services/BlpDecoder.cs` | `Formats/` | `MangosSuperUI.Services` → `MSUIClient.Formats` |
| `Services/AdtTerrainReader.cs` | `Formats/` | `MangosSuperUI.Services` → `MSUIClient.Formats`, **and** the `using MangosSuperUI.Services.Mpq;` on line 1 → `using MSUIClient.Formats.Mpq;` |
| `Services/WorldExport/VmapFormat.cs` | `Formats/` | `MangosSuperUI.Services.WorldExport` → `MSUIClient.Formats` |

`MpqReaderService`, `MpqBuilderService`, `DbcService` and friends are DI
services, not format readers — leave them in SuperUI. The client will get its
own thin MPQ mount later, without the ASP.NET lifetime plumbing.

### Do NOT bring over

`TerrainTileExporter.cs`, `CollisionTileExporter.cs`, `WorldExportController.cs`,
`WowSocketBridge.cs`. All four existed only to feed a browser: bake terrain to
GLB, bake collision to GLB, serve a manifest over HTTP, tunnel TCP over
WebSocket. A native client reads the MPQs directly and opens a real socket, so
none of it has a job here.

### One thing worth knowing

`AdtTerrainReader.cs` carries verified work: MCVT heights, MCNR normals,
`BaseX/BaseY/BaseZ`, and the `OuterHeight` / `InnerHeight` / `WorldHeightAt` /
`NormalAt` / `IsHole` accessors. That parse was checked against the server's own
height data at the Northshire spawn and matched to 0.00. Its axis handling is
correct — don't "fix" it.

## Configuration

```
copy client-config.json.example client-config.json
```

Then set `clientDataPath` to this machine's WoW 1.12.1 `Data` folder — the one
with the `.MPQ` archives. The client reads them directly; there is no asset
server.

`vmapPath` is optional. Point it at a copy of the server's
`run/data/vmaps` folder (~580 MB) for building, tree and fence collision.
Without it, terrain collision still works from MCVT heights — you'll simply
walk through solid objects.

## Build and run

```
dotnet build
dotnet run --project MSUIClient
```

Expect a window, sky-blue clear, an ImGui panel with live WoW coordinates and
the current tile index, and free-fly movement.

Controls: WASD move, Space/Ctrl up-down, Shift boost, hold mouse to look,
wheel to zoom, Esc to quit.

### Benilla comparison launch (movement M3 assessment)

The local comparison checkout is `C:\Users\nico\Desktop\benilla-main`.
Its committed PowerShell launcher documents this exact launch line from that
working directory:

```powershell
powershell -ExecutionPolicy Bypass -File .\run-benilla.ps1
```

Optional launcher arguments are `-Data`, `-WowHost`, `-User`, `-Pass`,
`-Char`, and `-Debug`. The script's build/run command is
`cargo run --release -p benilla` (or `cargo run -p benilla` with `-Debug`). It
sets `WOW_DATA`, `WOW_HOST`, and optional credential/character variables before
launch. Nico's personalized invocation beyond the checked-in launcher line has
not been supplied; this records the exact available line without inventing one.

Read-only assessment: benilla currently reads Bevy `ButtonInput<KeyCode>`
directly in `crates/benilla/src/player.rs`; no committed scripted movement
source or movement CSV recorder was found. Both are feasible as additive test
instrumentation at that input resource and the post-controller/animation
systems, but would require benilla source changes. No benilla file was changed.

## Conventions

**Coordinates.** Everything is WoW world space: X north, Y west, Z up,
orientation in radians CCW about +Z from +X. There is no conversion layer
anywhere in this client — OpenGL doesn't care about handedness as long as the
view matrix is consistent, so the camera simply passes `Vector3.UnitZ` as up.
Positions from ADT, vmaps, DBC and the network all mean the same thing end to
end, and the HUD prints values you can compare against `.gps` in a real client
with no translation.

**Tile index.** ADT and vmap filenames are `{col}_{row}` where
`col = floor(32 - worldY / 533.33333)` and `row = floor(32 - worldX / 533.33333)`.
The first number comes from **Y**, the second from **X**. Northshire's human
start (-8949.95, -132.493) is tile `[32, 48]`; note that
`AdtTerrainReader.ReadFromMpq` takes `(gridX = row, gridY = col)`, inverted from
the filename.

**Line endings.** CRLF for C#, shaders and config; LF for markdown. Enforced by
`.gitattributes`.
# Combat GM scenario deck

The DevTools GM console sends each line as `CMSG_MESSAGECHAT` SAY with universal
language. For the VMaNGOS combat instrument run, paste the commands one at a
time from `scenarios/combat/dummy.txt` while standing at the flat movement-arena
vantage. It uses VMaNGOS/MaNGOS chat-command syntax: `.gm on`, `.npc add 6`,
`.combatstop`, `.npc delete`, `.respawn`, and `.gps`. Entry 6 is the low-level
Kobold Vermin used as the disposable melee target. The reset deck requires the
operator to select each spawned target before `.npc delete`; it intentionally
contains no automated world or database mutation.
