# REAL_PORTALS environmental-window vertical slice

**Implemented:** 2026-08-13.  
**Design authority:** `docs/plans/REAL_PORTALS.md`.  
**Current boundary:** a server-described, per-session, live environmental view
through summoned Mage portals. Travel still commits through the stock,
server-authoritative `GameObject::Use` path.

## Player-visible behavior

The six stock city portals are recognized locally by exact GameObject entry and
validated for protocol use as type-22 templates with these `data0` use-spell
pairs: `176296/17334`, `176497/17607`, `176498/17608`, `176499/17609`,
`176500/17610`, and `176501/17611`. As soon as one streams in, the client registers a
GUID-owned procedural aperture independently of the cosmetic M2. It is a
six-yard-wide by eight-yard-high oval disc with a subdued violet preload seal.
Broken gold and pale-gold lanes spiral inward across the full opening, echoing
the authored InstancePortal motion without nesting the narrow legacy M2 inside
the new aperture. The seal grows outward from a central spark for at least 0.6
seconds, so a cold target never appears as a black or incomplete texture. Its
gold contribution fades completely when the destination frame becomes live.
For these six recognized entries, the narrow particle-only `InstancePortal.m2` is suppressed
instead of being stacked inside the larger disc; the authoritative GameObject
entity, tooltip, full-aperture pick target, and stock use path remain intact.

Within 45 yards, the client requests an authoritative descriptor for the
nearest eligible portal. One isolated destination renderer streams the configured
inner residency ring of terrain, WMOs, embedded/outdoor doodads, liquids, collision, and
destination lighting. It renders to a double-buffered RGBA8/depth target. A GL
fence publishes only a complete frame.

The seal cross-fades to that frame over 0.2 seconds only after all of these are
true:

- destination geometry has been adopted;
- a complete fenced frame exists;
- terrain or collision confirms arrival support;
- the minimum seal animation has elapsed;
- the server has confirmed this session's correlated READY lease.

The five-second lease is conservatively shortened by observed latency plus a
safety margin and renewed before that local deadline with another
PREPARE/DESCRIPTOR/READY cycle. A failed renewal, range exit, despawn, map
boundary, disconnect, or candidate failure reseals the portal and retires its
private scene without showing stale content. Other visible Mage portals retain
their animated seals; version 1 has one destination slot and one server
correlation per player session.

The destination camera is transformed coefficient-for-coefficient through the
source and virtual destination frames, so walking sideways changes the view
with perspective parallax. The preview texture is sampled in normalized screen
space through the aperture rather than stretched across portal-local UVs.
Both physical faces are canonicalized into the same inward destination view by
a handed 180-degree basis turn; the virtual camera boom is then shortened
against destination collision and terrain. This prevents a rear-face view, or a
long source camera boom, from placing the preview eye behind an arrival wall and
publishing a permanent clear-colour oval with clipped geometry strips.
Terrain, WMO, doodad, and liquid geometry is additionally clipped to the forward
half-space of the virtual destination doorway. Sky renders outside that clip.
This removes walls, terrain, props, and city silhouette meshes which sit between
the transformed eye and the synthetic exit instead of leaking them into the
window as angle-dependent slabs or triangles.

The full procedural oval is also a two-sided right-click target. A
two-sided swept capsule test detects a player crossing the doorway between
movement samples and uses a per-GUID side latch and cooldown to prevent repeat
activation. With portal-v1, an unready crossing is held on the source side of
the membrane; after READY, crossing issues the ordinary authoritative
`CMSG_GAMEOBJ_USE`. With an older core, the same crossing falls back directly
to ordinary GameObject use and the normal loading curtain. On a prepared READY
transfer, the client retains the last complete far-side frame after
`CMSG_GAMEOBJ_USE` is successfully queued. The authoritative same-map teleport
or `SMSG_NEW_WORLD` must still match the exact prepared descriptor and must have
nearby support at the server's actual landing point. The prepared terrain, WMO,
doodad, liquid, ADT-cache, and collision bundle is then atomically promoted into
the active world; the former source bundle moves into the preview slot and drains
asynchronously. No generic loading phase or loading-screen fade runs on this
path. The retained image is only a brief transfer veil before authoritative
confirmation, and remains the fallback presentation if promotion cannot pass its
strict gates. The server remains free to reject range, ownership,
lifetime, or any other stock use condition.
Prepared-bundle promotion currently requires the active client-geometry
collision mode, which is the shipped configuration. VMap and collision-disabled
configurations retain the guarded generic loader because the isolated preview's
collision result cannot safely replace their different active-world policy.
The latch rearms from whichever face the player is clearly standing on, so
walking around the outside and approaching the reverse face works as well. The
descriptor's `ONE_WAY` flag describes travel topology (source portal to its
city destination), not a restriction to one geometric face of the source disc.

## Authority and wire contract

`server.realPortals` defaults to `true` and, together with `server.enabled`,
enables the local procedural presentation. Existing per-machine configs that
omit the newer key therefore pick up REAL_PORTALS after rebuilding. Set it to
`false` for a stock VMaNGOS core. Before sending any portal opcode, the client
issues a zero-guid request through the older, backwards-compatible
`CMSG_SUI_CONTROL_REQUEST`. A current SuperUI core appends an
eight-byte `SUI1` capability trailer to its ordinary control ACK and advertises
portal-v1 in bit 0. An older core returns its normal denial without that trailer;
the client suppresses the probe denial and keeps the large aperture sealed.
This avoids sending opcode 844 to an older core whose opcode bounds check would
close the connection.

| Opcode | Value | Direction | Size |
|---|---:|---|---:|
| `CMSG_SUI_PORTAL_PREPARE` | 844 | client to server | 16 |
| `SMSG_SUI_PORTAL_DESCRIPTOR` | 845 | server to client | 92 |
| `CMSG_SUI_PORTAL_READY` | 846 | client to server | 28 |
| `SMSG_SUI_PORTAL_STATE` | 847 | server to client | 32 |

All layouts are version 1, exact-length, little-endian, and reject unknown enum
or flag values, non-finite geometry, nonzero reserved/request-flag fields, leases on
non-READY states, and invalid correlation keys. The successful descriptor owns
source geometry, destination
hint, generation, revision, ticket, and remaining lifetime. READY is a
quality-of-experience lease; it does not consume a charge, authorize a
teleport, or bypass `GameObject::Use`.

The server revalidates the live object, 45-yard preparation range, interaction flags,
`PlayerCanUse`, party ownership, exact entry/spell pair, and current
`SpellTargetPosition` before accepting READY. Denial leaves the ordinary
`CMSG_GAMEOBJ_USE` path unchanged.

The procedural doorway is deliberately independent of destination-scene
availability and of the legacy "Portal surface film" tuning switch. A missing
capability advertisement, preview allocation/shader failure, or stale server
therefore leaves a two-sided sealed 6x8 doorway instead of reverting to only
the narrow, one-sided M2 effect.
Presentation is also independently range-bounded: new apertures enter within 90
yards, tracked ones leave beyond 120 yards, and a missing entity receives only a
half-second reconciliation grace. This matters for same-map teleports, where a
delayed source-object out-of-range update must not resurrect the old doorway
after renderer promotion.

## Scene ownership and rendering

`PortalDestinationScene` owns private `AdtCache`, terrain, WMO, doodad, liquid,
collision, camera, atmosphere inputs, and render target. It borrows the shared
asset worker pool, upload context, sky renderer, and visibility overrides. This
duplicates destination GPU assets and is intentionally a memory-heavy spike;
the final design moves immutable resources into `SharedWorldAssets`.

Candidate construction, shader loading, liquid texture creation, main-context
VAO adoption, rendering, target allocation, and disposal stay on the window GL
thread. Runtime cancellation calls `Retire`, hides the candidate immediately,
and advances a nonblocking drain until terrain/WMO/doodad workers, collision,
and the last render fence are complete. Blocking disposal is shutdown-only.
When a formerly active renderer bundle is recycled into the candidate slot, a
new opaque residency epoch clears its old positive appear-fade timestamps and a
private preview clock advances WMO/M2 animation. Without that reset, the first
fresh candidate worked but later candidates could keep every newly placed WMO
and doodad at alpha zero indefinitely despite reporting READY.

The candidate render order is sky, terrain, WMO, doodads, then liquid. WMO must
precede doodads because it publishes the frame's interior portal visibility.
The isolated preview deliberately fails open for WMO room/doorway PVS and
suppresses baked distance-LOD city shells. A transformed portal camera is not an
authoritative active-world room seed; using it for those switches made small
source-angle changes remove complete destination groups or replace detail with
low-poly exterior silhouettes. Frustum and distance culling remain enabled, and
promotion restores the active world's normal WMO culling and occlusion tuning.
The source frame draws opaque geometry, the procedural aperture, then source
water, preserving normal foreground occlusion. Legacy instance entrances retain
their ordinary M2 particles and inferred circular film; only recognized summoned
Mage portals suppress that cosmetic lane. Legacy film/light inference also
requires the source model's exact filename stem `InstancePortal`. Emitter shape
alone is insufficient because candles and lamps use the same model-space sphere
form; the old broad test rendered green film wedges through unrelated interiors.

Dynamic doodad emitter identity now carries the exact GameObject GUID into the
particle pool. Dynamic GameObjects are excluded from the older rounded-position
`InstancePortal.m2` surface inference, preventing nearby summons from sharing a
film or generic sphere emitters from being misclassified.

## Teleport ACK rule

`SMSG_NEW_WORLD` no longer sends `CMSG_WORLD_TELEPORT_ACK` from the socket read
thread. The ordered game-thread boundary records one pending ACK; the existing
world loader sends it exactly once after destination map, renderer, and pose
adoption. Initial `SMSG_LOGIN_VERIFY_WORLD` does not arm this token. Same-map
`MSG_MOVE_TELEPORT_ACK` remains apply-before-ACK on the game thread.

A distant same-map teleport first tries to atomically promote the correlated
prepared scene; only an unprepared or mismatched teleport tears down stale source
residency and raises the guarded generic transition. Cross-map `NEW_WORLD` follows
the same rule. Successful promotion installs destination map identity, renderer
set, controller terrain, and collision before the applicable ACK, then resumes
rendering without entering `BeginWorldLoad`. The previous source scene's terrain
tasks, WMO/M2 queues, collision worker, background ADT parse, and GL resources
retire incrementally and are never synchronously awaited at the plane. Fallback
gameplay stays paused until the active scene has a nearby support surface at the
authoritative arrival pose.

## Deliberately not in this slice

- no server-side movement-segment/doorway crossing authority; automatic
  crossing is currently detected by the client and committed through stock use;
- no destination players, NPCs, spell effects, foliage, audio, or postprocess;
- no recursive portal rendering;
- no geometric arrival-offset preservation.

Until those milestones land, the feature improves the portal's size,
pre-animation, per-player preparation, live far-side view, full-aperture click,
and automatic walk-through behavior, while the actual teleport remains the
safe stock server-authoritative path.

## Verification

```powershell
dotnet build MSUIClient\MSUIClient.csproj --no-restore
dotnet run --project tools\real-portal-wire-check\real-portal-wire-check.csproj --no-restore
git diff --check
```

The wire check round-trips all four packets and rejects short/long bodies,
reserved-field violations, unknown enums, non-finite geometry, and empty
required correlation fields. A live visual pass still requires the matching
SuperUI core plus MPQ data and must be checked in-game from multiple camera
angles and through a lease renewal.
