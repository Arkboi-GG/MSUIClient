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

The DevTools GM console sends each line as `CMSG_MESSAGECHAT` SAY with the
logged-in character's faction language (Common for Alliance, Orcish for Horde).
VMaNGOS rejects Universal from client chat before command parsing. For the
VMaNGOS combat instrument run, paste the commands one at a
time from `scenarios/combat/dummy.txt` while standing at the flat movement-arena
vantage. This deployment's live `.help npc spawn` response and matching
VMaNGOS command table establish the lifecycle syntax: `.npc spawn add 6`,
`.npc info`, and `.npc spawn delete`. Use `.combatstop`, `.respawn`, and `.gps`
as before. Entry 6 is the low-level
Kobold Vermin used as the disposable melee target. The reset deck requires the
operator to select each spawned target before `.npc spawn delete`; it intentionally
contains no automated world or database mutation.

## Autonomous live-run bootstrap

`tools/live-run` is the single entry point. It first checks the configured
realmd endpoint, refuses any account name other than the dedicated `TEST`, then
launches MSUI with auto-connect/auto-character entry and the live bootstrap.
The TEST password remains only in the gitignored config. Example:

```powershell
dotnet run --project tools\live-run\live-run.csproj -- MSUIClient\client-config.json --out live-runs
```

The VMaNGOS deployment is external at the configured LAN host; no server
executable or service launcher exists in this repository. Its one permitted
manual start must therefore be performed on that host once per sitting. A
closed realmd port is recorded as a run-dated `SERVER_UNREACHABLE` artifact and
nonzero exit, rather than prompting for per-scenario intervention.

## VMaNGOS remote administration

The external deployment exposes Remote Administration on the configured LAN
host, port 3443. `tools/vmangos-ra` reads the dedicated TEST account and
password from the gitignored client config at runtime, redacts both from its
run-dated transcript, and sends console commands without storing credentials.
SOAP port 7878 is closed. SSH uses the dedicated ED25519 identity stored only
under the ignored `MSUIClient/local-credentials/` directory. Its fingerprint is
`SHA256:nu7SKMUP8+hBTglZCMgQzHwiui968yVgyF1VUK+gUdc`. The public key is installed
for `wowvmangos@192.168.0.2`; key-only batch authentication was confirmed before
diagnostic work continued. The bootstrap password was entered interactively for
installation, was never stored or printed, and is no longer used. Rotate that
password now that key access is established.

For bounded diagnostics, query and preserve the original state with
`server log level` and `server log filter`. Filters are addressed by name on
this build: use `server log filter combat on|off`, not a numeric index. On this
server, `server log level 3` changes the console sink only; it leaves the file
level unchanged. Restore the observed values after every capture. SPEC-21 P2
observed and restored console/file levels `2/2` and combat filter `off`.

The deployed world process is
`/home/wowvmangos/vmangos/run/bin/mangosd`, working in the same directory and
running as detached screen session `mangosd`. Its config is
`/home/wowvmangos/vmangos/run/etc/mangosd.conf`; `LogsDir = ""`, so `Server.log`
and the other configured logs sit beside the binary. The process console is
`/dev/pts/1`. Bounded console captures use screen's temporary logging, never a
server restart or persistent config edit. The deployed source checkout was
read-only at revision `d7779aee9d43113e78c078b54daef89946be0b1a` with a clean
status. No database query or write was made during the SSH setup or P2 resume.
