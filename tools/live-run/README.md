# Live client protocols

The client itself accepts `--live-protocol <file> --character <name> --out <directory>
--timeout <seconds>`. Its positional JSON config supplies the server and account.
Networking must be enabled. Use only an account and gameplay actions the owner has
authorized. Credentials belong in ignored local files.

Add **`--background`** to render a hidden window at 30 updates/frames per second.
The normal protocol uses internal gameplay handlers and binding state, with no
desktop mouse/keyboard automation. Hidden startup ignores saved fullscreen/maximized
preferences. Gameplay dumps still capture the actual framebuffer, but do not copy
their paths to the desktop clipboard in background mode.

For isolated runs, set `MSUI_SETTINGS_PATH` to an ignored copy of the settings file.
Its login profiles can override the JSON config; use an empty `LoginProfiles` object
to initialize profiles from the dedicated config. Preserve the owner's normal file.
Sound checks require the test profile's master/effects settings to be enabled.
The background world-music startup focus gate remains in effect, so hidden runs do
not certify music startup or subjective audio quality.

Example (PowerShell, paths relative to the repository):

```powershell
$env:MSUI_SETTINGS_PATH = 'C:\path\to\ignored-test-settings.json'
Start-Process MSUIClient/bin/Release/net8.0/MSUIClient.exe -WindowStyle Hidden `
  -ArgumentList 'C:\path\to\test-config.json --live-protocol C:\path\to\protocol.txt --background --character AuthorizedCharacter --out docs/current/test-run --timeout 120' `
  -RedirectStandardOutput docs/current/test.stdout.log `
  -RedirectStandardError docs/current/test.stderr.log
```

The separate `tools/live-run` wrapper retains its historical TEST/NIGHT roster gate.
The native client CLI above is the existing entry point for explicitly authorized
account-specific runs. Neither entry point deploys or restarts Core.

## Evidence and limits

Bootstrap waits for the live world, then sends the configured `movement-arena`
vantage teleport. Protocol commands can change character state; inspect the file
before launching it. `gm` steps use the authenticated client's command channel.
Use `--native-spawn` to skip that bootstrap teleport and retain the actual saved
or newly created starting position. It also works with `--character-select` when
the protocol later enters the world, enabling ordinary new-character playback.
`simulate` and `stage` commands are fixtures and are not live gameplay proof.

`dump <name>` saves a framebuffer PNG and state JSON under `dumps/`. Completion
writes runner verdicts, wire-derived observations, and an audio journal under `--out`.
Check failures and server replies: a sent request alone does not prove success.

- `sound assert N cue [category]` counts **all** events since `sound mark`.
- `sound assert-category N cue [category]` checks only that category, so NPC
  footsteps cannot falsely fail an inventory or action-bar assertion. It still
  rejects extra or wrong cues within that category.
- `action-gesture pickup-spell ID`, `pickup-slot SLOT`, and `drop SLOT` call the
  production pickup/release handlers. Slot indices are zero-based; `drop -1`
  releases outside the bar. `assert-slot SLOT SPELL_ID` and `assert-cursor SPELL_ID`
  check resulting state; zero means empty.
- `action-gesture create-test-macro`, `pickup-test-macro`, `assert-test-macro-slot
  SLOT`, and `delete-test-macro` create, exercise, and remove a blank character macro
  through the shipping handlers. Creation/deletion changes the authorized character's
  local Macro Book; cleanup targets only the id created by this run.
- `companion list`, `inspect`, `open`, `summon NAME`, `dismiss NAME`, and `possess NAME`
  use **SuperUI companions** (the account's alts), through their capability-gated
  production handlers. `assert-summoned NAME`, `assert-controlled NAME`, and
  `assert-offline NAME` check server-backed state. `assert-status-summoned NAME`
  guards the UI's completed summon message. Summoning requires a non-instance map.
- `companion attack` issues a SuperUI attack order to the summoned companions against
  the current selection. A sent order is not a completed kill. `select-enemy` selects
  the nearest live hostile without requiring its entire unit-flags word to be zero.
- `trainer confirm` accepts an already displayed profession confirmation through the
  normal handler. Check a subsequent server purchase success and learned-spell delta.
- `quest assert-reward-money COPPER` checks the active NPC detail/offer display's
  computed reward using the controlled body's level. Verify the later server payout
  separately. `quest query ID` requests an NPC dialog; it is not GM completion.

An `anchor selected OFFSET` step is a GM teleport, not pathfinding. Offsetting a
creature's position inside a WMO can put the character through a wall or under a floor.
Use known walkable positions and inspect captures; an evade caused by invalid test
placement is not evidence of a combat defect. `waitdeath` is an immediate state
assertion despite its name; its argument is a spawn ordinal, not a duration.

The action protocol supplies the destination slot directly. It certifies live
handler, wire-intent, cursor, and sound dispatch behavior, **not pointer hit testing**.
Space repeated same-kit sound checks by a second: the authored no-duplicates policy
can coalesce requests while the previous voice is still active. Journal entries
prove dispatch and asset resolution, not that a person heard the result.

The adaptive native protocol supports `inbox REPO_RELATIVE_PATH`. At the end of
its current steps it polls an append-only text file, committing complete lines in
order. Append ordinary protocol commands after reviewing a dump; append
`inbox-close` to finish. The overall `--timeout` still applies. Do not truncate a
live inbox. This queue and the `glue` input-context proxy do not send desktop input.
`glue pointer X Y`, `glue down`, `glue up` use framebuffer coordinates; shipping
bindings use `key press NAME` / `key release NAME`. Inspect current UI geometry.

Additional regression commands:
- `item-gesture use-entry ID` uses the production item-action route (dispatch only).
  Pickup/place accepts inventory, equipment and bank coordinates validated by ToWire.
- `pet-gesture` exercises the real book/bar handlers; see the pet regression scenario.
- `unit-state actor|selected|0xGUID` records the client's received position, health,
  NPC flags, ownership and pet fields without requesting or changing any state.
- `combat-text inspect` records the displayed center messages, ages and scroll offsets
  without injecting messages or changing combat state.
- `face selected` uses the selected entity's received position with the existing
  `face RADIANS` yaw control; it refuses missing entities and coincident positions.
- `action-gesture assert-main-wire BUTTON WIRE` and `page DELTA` check bonus pages.
- `pvp-state desire true|false`, `assert-desired`, `assert-flag`, `inspect` distinguish
  the preference from the five-minute visible flag. Mutation fixtures require self.
- `pose inspect`, `assert-animation ID`, `assert-model FRAGMENT`, `assert-transformed
  BOOL`, `assert-dead BOOL` read actual renderer/descriptor state; model existence
  alone is not visual approval.
- `escort-until-complete QUEST PROTECTION_SPELL SECONDS` uses real quest state and
  combat while GM-placing the player alongside the NPC. It certifies neither walking
  nor balance. Record resource setup separately and explicitly verify cleanup.
- Normal-combat protocols must explicitly send `gm .cheat god off` after each
  login and retain the server confirmation. This development server currently has
  `GM.CheatGod = 1`: GM-account characters receive a one-health invincibility
  threshold at login even when `.gm` reports OFF. `--native-spawn` does not change
  that setting. Without a verified disable, combat observations establish spell,
  quest and loot mechanics, but cannot establish ordinary survival or difficulty.
  Record intentional protected fixtures separately; do not alter server config.
- `fight-until-dead SPELL SECONDS` repeatedly attempts a normal cast against the
  existing selected attackable creature (SPELL0 only observes companion combat).
  Starting requires a grounded actor and a living attackable target; neutral wolves
  use the same production attack gate as hostile creatures. The normal target-clear
  on observed death is a completed kill, not an interruption. This does not
  certify that a GM-selected fixture position is appropriate for the encounter.
  It stops on server-observed death, target/actor loss or change, or the bounded
  timeout (maximum600seconds), with a framebuffer every5seconds. It never moves,
  heals, spawns or kills a unit directly. Setup, loot, mechanics and visual review
  remain separate checks. `quest inspect-log` observes the controlled body's
  displayed log, including its server party-facts rows.
- `support-at X Y Z` reports a downward collision probe and terrain height for
  fixture diagnosis. Success means the observation ran, not that a floor was found
  or that the location is walkable. Inspect the hit and the actual actor pose.
- `fish-until-bite SECONDS` observes an already-started fishing channel (maximum60
  seconds), captures its bobber every3seconds, and uses that bobber after observing
  its server state change from waiting to active. Actor/channel changes abort.
  A successful step proves the use was sent; inspect separate fish/loot/skill replies.
- `liquid-visible true|false` is a diagnostic runtime visibility toggle for fixed
  camera comparisons. Restore true after the comparison. `MSUI_LIQUID_PROBE=1`
  enables liquid GPU error/state logging every5seconds; omit for normal runs.

An interrupted protocol now flushes partial runner/verdict/audio artifacts and
records the interruption as a failure. A disconnect never converts unfinished
steps into successful checks.

Framebuffer dimensions <=1 are rejected by screenshot/sequence capture. Such sample
steps fail; retain their logs and recapture. For a streamed/transformed body the spell
sequence reports its animation lifecycle as unmeasured rather than reading the hidden
character rig. Separate pose/model assertions and reviewed frames cover those cases.

Hidden GUI protocols support `glue right-down` / `glue right-up` in addition to
left `down` / `up`. These persist the polled internal button state between steps;
release each button explicitly. Both use `glue pointer X Y` and never move the OS
cursor. This enables actual inventory/vendor right-click paths in background runs.

Hidden world pointer integration (2026-09-07): `glue world-pointer-on` opts into
ClientWindow's supplied pointer source. Existing `glue pointer/down/up/right-down/
right-up` then feed both the normal GUI and world press/release queue. Wait at
least one render after moving across UI boundaries before pressing. World target
selection and ground unprojection run through shipping hit testing; this mode
intentionally does not engage native cursor capture or camera-look gestures.
`glue world-pointer-inspect` reports pixel/ground/armed spell/UI capture state.
`glue world-pointer-off` releases the virtual buttons and clears pending clicks.
This is distinct from `castground`, which supplies a world destination directly.

`select-fight-target` reselects the existing entity last observed by
`fight-until-dead`, including its corpse. Death clears the normal selection;
reselect before `loot request` and check the server's loot reply separately.

Cast-bar/animation/blocked-spell audit classification uses SpellInfo.CastClassification
and the authored channel attributes. Channel-interrupt flags alone do not establish
channel identity (e.g. trap placement9437). The interface-wire-check option
`--spell-classification-only` checks this against mounted rows; packet events remain
independent authoritative evidence.

The next-ten diagnostics add `audit-mail inspect`, plus same-frame text send followed
by `send-release`, `send-port`, or `send-freeze` (recipient argument). These call
production send/control/GM handlers; the recipient and purse must be checked separately.
`send-port` requires the session body and moves it50yd using an authorized GM command.
`audit-control inspect` records authoritative chain/freeze state; `view`, `freeze`,
and `link|unlink|follow NAME` call shipping handlers. Dispatch success is not an
acceptance verdict. `combat-text capture-mixed` captures only when received critical
and scrolling messages coexist; it injects no feedback. Unit-state also logs received
coinage/mana fields; fields on an uncontrolled remote unit may be absent or masked.

Audit movement diagnostics: `audit-mail send-walk RECIPIENT` queues a real text send
and presses the shipping W binding in the same frame. Stop with `key release W`;
legacy `release W` uses a different test-input collection. `audit-control portal ID`
only reports the mounted trigger's authored center and volume; it does not teleport.
