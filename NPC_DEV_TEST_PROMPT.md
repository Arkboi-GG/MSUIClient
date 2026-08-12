# Prompt for the next agent — NPC Dev Window first live test

Copy everything below the line into a fresh agent session.

---

You are picking up the **NPC Dev Window** feature in MSUIClient (WoW 1.12 client repo).
P1–P3 are built and build-verified; **the owner (Nico) has NOT hand-tested anything yet
and does not know the controls.** Your FIRST action, before any tool calls or questions:
**print the "CONTROLS & TEST SCRIPT" section below for the owner, verbatim.** Then stand
by to fix whatever the test surfaces.

Ground truth to load before fixing anything: repo-root **`NPC_DEV_WINDOW.md`** — the
architecture handbook (layer rules §2, file map §3, aggro math §5, HTTP contracts §7,
edit spec as-built §10, deploy §12, verification §13, known limitations §14). Do not
redesign owner decisions listed there. The vmangos box is `wowvmangos@192.168.0.2`
(ssh key: `MSUIClient/local-credentials/vmangos_ed25519`); MangosSuperUI serves
`http://192.168.0.2:5000`.

**Current state:**
- Client: P1 (window + aggro discs + x-ray + observed paths) and P2 (DB spawn rows +
  patrol routes + provenance, via CSV fallback) live-verified by scripted runs; P3
  (editing → change-set files) build-verified only — **the click flow below is exactly
  what needs human testing.**
- `NpcDevController.cs` (JSON snapshot endpoint) has been copied to this PC for deploy
  into the MangosSuperUI checkout → publish → box (recipe in NPC_DEV_WINDOW.md §12;
  ⚠ restarting mangossuperui also restarts the bot brain). Until deployed the client
  falls back to CSV exports automatically — everything still works.
- P4 (MangosSuperUI upload/verify/apply pages) is NOT built; the change-set JSON files
  are the end of the line for now. That is expected, not a bug.
- Setup gotcha: `MSUIClient/client-config.json` needs `"server": { "enabled": true }`
  to go live (it has been left false for offline/creator sessions before).

---

## CONTROLS & TEST SCRIPT (print this for the owner)

### Getting in
| Key | Does |
|---|---|
| **Ctrl+N** | Toggle the **NPC Dev window** (works in live AND creator mode) |
| **Ctrl+F** | Free view (detached RTS fly camera) — the intended way to use the window |
| **Mouse wheel in free view** | Fly the rig toward/away from where you look — wheel DOWN to gain altitude, no height cap (the wheel no longer pumps the 40-yd orbit zoom) |
| **F** | Fly WITH the character (single-character mode). Same scheme everywhere: W/S flat, **Space = up, hold Ctrl = down**, Shift = boost, no height cap. Live-verified. |
| **Ctrl+LeftClick a creature** | While the window is open: add/remove it from the **"Selected only"** overlay set |
| **Esc** | While a dev EDIT mode is armed: cancels the edit (before the game menu) |

Opening the window kicks the data fetches automatically (creature templates + all
spawn/waypoint rows for the current map). The toolbar tells you what loaded:
"10381 templates…", "24646 spawn rows, 3221+285 paths (csv…)", and
"in range: N streamed, M DB-only".

### Window sections (all collapsible)
- **Overlays** — scope radios: **All NPCs** (everything in range) / **Selected only**
  (just the Ctrl+LeftClick focus set; plain click retargets the set, empty click clears
  it; DB-only markers hide in this scope). Then checkboxes: Spawn labels · Observed
  pathing (records only while the window is open) · DB patrol routes · DB spawn points +
  wander circles · Aggro discs · Who-would-aggro highlight · Hostiles only · range +
  opacity sliders.
- **Aggro reference** — the Fire-Emblem selector: **vs level 60 (raid)** / **vs my
  level (dungeon)** / **vs NPC's own level**, plus band count (each band = one level
  lower = one BIGGER ring; legend shows the colors).
- **Selected NPC** — click any creature (normal left-click; in free view left-click
  also works) → identity, faction reaction, detection_range with its exact DB source,
  spawn row (position/respawn timers/movement type), patrol source, computed aggro
  radii, **and the EDIT controls**.
- **Change set** — every queued edit as a packet with a revert ✕; shows the JSON file
  path (`dev-changes\<stamp>-<character>.json`, saved on every change).
- **Data source** — MangosSuperUI URL, cache age, refresh.

### What to eyeball first (10-minute pass)
1. Fly (Ctrl+F) over a hostile low-level area (Elwynn wolves/Defias): **aggro discs**
   should be concentric colored bands hugging the terrain, bodies occluding the far arc.
   Switch reference modes and band count, watch radii change.
2. Walk your toon into a disc: the mob gets a **red through-wall beam** + `! AGGRO`
   badge; when it truly aggros, the disc rim **flashes white** and the console logs
   `[dev-aggro] … at X yd (predicted Y yd)` — predicted vs actual should be close
   (it's distance-only: no line-of-sight, so a mob behind a wall showing the beam is a
   KNOWN limitation, not a bug).
3. Find a patroller (Stormwind gate guards, Defias patrols): **solid cyan** route =
   its own creature_movement path, **solid gold** = shared template path; numbered
   nodes, waittime badges, dashed closing segment. Its **dashed colored** trail is the
   observed live movement — the two should overlap.
4. **Wander circles** (thin teal rings) around movement_type-1 spawns; **teal dots** at
   authored spawn points; **dimmed grey labels "(not streamed)"** on DB rows the server
   currently has despawned; dashed white line from a wandering mob back to its spawn.
5. **Provenance**: select a mob — every value should name a real table/row/column.

### EDIT CONTROLS (the untested part — P3)
Select a creature that has a DB row, then in **Selected NPC → EDIT**:

**"Edit path"** (waypoint mode — working path draws bright green):
| Action | Does |
|---|---|
| Left-click a node | Select it (click again to deselect; white ring = selected) |
| Left-click ground | Insert a new node AFTER the selected one (append if none selected) |
| **Shift + left-click ground** | MOVE the selected node there |
| Right-click a node | Delete it |
| Window field | Edit the selected node's waittime (ms) |
| **Commit path** button | Queue the packet · **Cancel** / **Esc** abandons |

**"Move spawn"**: left-click the ground → green ring "new spawn" + model ghost (if the
creature is streamed) + dashed old→new line → **Commit move**.

**Field edits** (same section): respawn min/max, movement_type, wander_distance →
"Queue spawn changes"; detection_range → "Queue detection_range change" (**warns how
many spawns of the entry it affects; the drawn discs immediately preview the new
radius** — DB untouched).

**While any edit mode is armed, ALL world clicks belong to it** (no RTS orders, no
targeting). Queued edits stay visible as dim-green previews until reverted.

**After queuing**: open `dev-changes\` at the repo root — one JSON per session, every
packet with before/after values. Nothing is ever applied from the client; applying
happens later in MangosSuperUI (P4, not built yet).

### Expected non-bugs (don't chase these)
- Flat (non-conforming) discs inside dungeons/buildings — WMO floors aren't projected yet.
- Aggro beam through walls on a mob that wouldn't really see you — no LoS in the estimate.
- Friendly/neutral mobs having no discs — "Hostiles only" is on by default.
- Observed trails only exist for movement seen WHILE the window was open.
- Creator mode: pathing/aggro-highlight sections say "requires live server".

---

**Agent, after printing the above:** offer to (a) deploy the NpcDevController into this
PC's MangosSuperUI checkout + publish to the box (recipe §12 — confirm with the owner
before the restart, it bounces the bot brain), and (b) triage whatever the test turns
up. Client-side fixes need no server pairing. Keep NPC_DEV_WINDOW.md current as things
change — it is the canonical doc.
