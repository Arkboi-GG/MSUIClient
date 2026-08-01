# NIGHT_01 autonomous run report

Append-only per-item evidence report. Tier-0 end-of-run packet follows after the list is exhausted.

## 1-1 — SPEC-26 sudo attach / server discrimination

Status: `SHELVED-BLOCKED`

### Actual versus predicted

```text
PREDICTED sudo dry attach: one interactive prompt, attach/detach <=5 min
ACTUAL: successful attach/detach in about 5 seconds; cache dropped immediately
RESULT: PASS

PREDICTED trusted site resolution: source lines available for every address
ACTUAL: runtime function addresses resolve, but all candidates report compiled
        without debugging / no line number information
RESULT: SOURCE_ADDRESS_RESOLUTION_UNAVAILABLE

PREDICTED post-detach health: RA + TEST .gps
ACTUAL: both PASS; ptrace_scope remains 1; original mangosd PID remains running
RESULT: PASS
```

The required labeled interior `Unit::Attack` false-return sites cannot be mapped
honestly in the deployed optimized binary. W1-W3 were not entered. Q1 recommends
an exact Build-ID-matched debug-info sidecar. Full evidence is in
`live-runs/W0b-source-resolution-shelf-20260731-223230.md` and its manifest.

No capture, server/client behavior change, DB access, persistent configuration,
package, sysctl, rebuild, binary replacement, or restart occurred.

W0b manifest: `live-runs/manifests/W0b-20260731-223230.sha256`, SHA-256
`014cd5a10a84d06e6d27f77acf1047884770cca846022d3cf89427d5e4a0f4ae`;
all eleven entries recomputed exactly at the boundary. Four gates passed:
Debug build 0 warnings / 0 errors, combat-wire PASS (established CA2014 during
its dependency build only), portrait-camera 10,534 / 1,224 / 1,289 / 56, and
move-audit PASS.

## 1-2 — SPEC-21 P3/P4 combat matrix completion

Status: `SHELVED-BLOCKED`

### Actual versus predicted

```text
PREDICTED: eight isolated matrix/behavior cells, each with a Z0-standard send gate
ACTUAL: V-B, CB4, quiet CB5, quiet CB6, and CB7 produced clean gated evidence;
        V-A was safely refused for GM-on; V-C/V-D drifted or became guard-
        contaminated before the frozen exact-zero/flags-zero gate could pass
RESULT: five decisive cells; three cells require an explicit gate/profile ruling

PREDICTED: player response after each valid attack send
ACTUAL: zero player-GUID AttackStart/swing/error rows after valid sends; foreign
        guard rows were identified by attacker GUID and excluded
RESULT: prior server-silent finding reproduced

PREDICTED: confirmed target death cancels local intent
ACTUAL: health=0 was descriptor-confirmed, but target-death cancellation never
        appeared; explicit cancel was required
RESULT: CLIENT_BEHAVIOR_FINDING (Q2); no fix attempted
```

CB4, quiet CB5, quiet CB6, and CB7 pass legal-transition and send-pairing audit
checks. Cadence and one-shot-return are honestly `NO_DATA` because the server sent
no player swing. Q3 records the frozen-gate/matrix incompatibility. Full packet:
`live-runs/N1-12-combat-matrix-shelf-20260731-230000.md`.

Manifest: `live-runs/manifests/N1-12-20260731-230000.sha256`, SHA-256
`c4e9133908d07ec06426c6b8fc626f913a61d39971097db676432603b8d6b1e6`;
all 23 entries recomputed exactly. Boundary gates: Debug build 0 warnings /
0 errors, combat-wire PASS (established CA2014 only), portrait-camera 10,534 /
1,224 / 1,289 / 56, move-audit-check PASS.

### Append-only association correction — Tier 0-3

The N3 manifest line rendered earlier beside the N2 section because an append
anchor was not unique. It belongs to the `3-1 through 3-8` section:
`live-runs/manifests/N3-20260731-235000.sha256`, SHA-256
`68903e99640f0fa2b63206c2f0a5bc0e12d7038f3cfdf61c4e16b815600104c7`;
all five entries recomputed exactly. No prior report bytes are retracted.

## 1-3 — attack-error text display readiness

Status: `CLOSED-PASS`

### Actual versus predicted

```text
PREDICTED: five server attack-error opcodes reach visible and copyable text
ACTUAL: exact opcode-to-text law added; receive path uses the existing red center
        text and a copyable combat verdict with opcode, byte count, GUID, and text
RESULT: PASS by combat-wire assertions

PREDICTED: verify a live error returned during 1-2
ACTUAL: the server returned no attack-error opcode in those runs
RESULT: NO_DATA; Q4 preserves the no-synthetic-error ruling
```

No packet construction, combat state, or server behavior changed. Evidence:
`live-runs/N1-13-attack-error-display-20260731-231500.md`.

Manifest: `live-runs/manifests/N1-13-20260731-231500.sha256`, SHA-256
`91eca7d61b6973d262bc3e4770020ecf6b35138b10a00060b578a675b9af5624`;
all four entries recomputed exactly. Boundary gates: build PASS (established
CA2014 only), combat-wire PASS, portrait-camera 10,534 / 1,224 / 1,289 / 56,
move-audit-check PASS.

## 2-1 through 2-12 — gameplay interfaces triage

Statuses: all twelve items `SHELVED-BLOCKED` under the parent document's
whole-item time-pressure rule.

### Actual versus predicted

```text
PREDICTED: each item enters a complete wire → instrument → live fixture → UI loop
ACTUAL: ten interface protocol/state/UI/runner families are absent; loot and
        character/inventory are partial foundations but cannot meet their entire
        live acceptance without mixing root causes or the blocked attack path
RESULT: every item shelved whole; no transaction or partial opcode fix attempted
```

The per-item table, vendor server file:line map, and recommendation are in
`live-runs/N2-interface-triage-20260731-234000.md`. Q5 recommends a dedicated
follow-on ordered vendor → quest → loot work order.

Boundary gates: build PASS (established CA2014 only, 0 errors), combat-wire
PASS, portrait-camera 10,534 / 1,224 / 1,289 / 56, move-audit-check PASS.

Manifest: `live-runs/manifests/N3-20260731-235000.sha256`, SHA-256
`68903e99640f0fa2b63206c2f0a5bc0e12d7038f3cfdf61c4e16b815600104c7`;
all five entries recomputed exactly.

## 4-1 through 4-7 — world-interaction triage

Statuses: all seven items `SHELVED-BLOCKED`.

### Actual versus predicted

```text
PREDICTED: complete gossip/object/rest/rez/hearth/taxi flows and a combined
           dual-band + portal + instance + water regression batch
ACTUAL: interaction families/prerequisites are absent or shelved; move-audit
        passes, but no committed portal/instance/water protocol sets exist
RESULT: whole items shelved; F3-F6 and all server/DB state untouched
```

Per-item map: `live-runs/N4-world-triage-20260801-000000.md`. Q7 recommends
gossip as the interaction root plus separately defined environment protocol sets.

Manifest: `live-runs/manifests/N4-20260801-000000.sha256`, SHA-256
`8d8a3801bd610b3c306fbdd8e0edf2103ae17a8c0f57f9aff8a838123e7946ea`;
all four entries recomputed exactly.

Boundary gates: build PASS (established CA2014 only, 0 errors), combat-wire
PASS, portrait-camera 10,534 / 1,224 / 1,289 / 56, move-audit-check PASS.


Manifest: `live-runs/manifests/N2-20260731-234000.sha256`, SHA-256
`ea5bfd3e2a2fffeae5b03cb46fd96be315b82d510dfd7fa283dfc330a8957f87`;
all four entries recomputed exactly.

## 3-1 through 3-8 — spell-system triage

Statuses: all eight items `SHELVED-BLOCKED`.

### Actual versus predicted

```text
PREDICTED: spell items begin with target/self + GCD + resource pre-send truth
ACTUAL: substantial cast/bar/channel/aura/visual foundations exist, but the
        mandatory mechanical gate, named result law, and roster batch do not
RESULT: all behavior/display sweeps shelved before send-precondition contamination
```

The per-item foundation map is
`live-runs/N3-spell-triage-20260731-235000.md`. Q6 recommends one shared
cast-precondition/result-instrument work order before re-entering the tree.

Boundary gates: build PASS (established CA2014 only, 0 errors), combat-wire
PASS, portrait-camera 10,534 / 1,224 / 1,289 / 56, move-audit-check PASS.



## 1-4 — combat regression pack

Status: `CLOSED-PASS`

### Actual versus predicted

```text
PREDICTED: repeatable X1/Z0/Z1 protocol with fresh identity, delivered chat
           control, gated attack, post-encryption socket evidence, and cleanup
ACTUAL: 31/31 steps PASS; control-to-attack delta 0.013 s; exact Z0 gate PASS;
        14-byte 0x0141 write flushed; delivered .gps response; cleanup PASS
RESULT: PASS

PREDICTED: transition/pairing regression audit
ACTUAL: legal transitions and send pairing PASS; cadence/one-shot NO_DATA because
        the unresolved server path still supplies no player swing
RESULT: PASS / NO_DATA
```

The committed baseline is `scenarios/combat/combat-regression-fresh-target.txt`;
the dated packet is
`live-runs/N1-14-combat-regression-packet-20260731-232500.md`. It changes no
combat behavior and regenerates no cohort/baseline data.

Manifest: `live-runs/manifests/N1-14-20260731-232500.sha256`, SHA-256
`5064f6d273be318275e2bc702f8ecc6e4d607200352fbe991f3364f0fc02c725`;
all seven entries recomputed exactly. Boundary gates: Debug build 0 warnings /
0 errors, combat-wire PASS (established CA2014 only), portrait-camera 10,534 /
1,224 / 1,289 / 56, move-audit-check PASS.

### Tier 0-3 manifest pointer (append-only)

The Tier 0-3 spell triage manifest is
`live-runs/manifests/N3-20260731-235000.sha256`, SHA-256
`68903e99640f0fa2b63206c2f0a5bc0e12d7038f3cfdf61c4e16b815600104c7`;
all five entries recomputed exactly.

## Tier 0-5 — housekeeping and end-of-run packet

### Actual versus predicted

```text
PREDICTED: finish mechanically authorized housekeeping, shelve authority/tool
           boundaries, classify every deferred row, and close with clean evidence
ACTUAL: audit normalization classified; repeated backpedal mechanics pass; pane,
        keybind, Benilla, and contact-sheet dependencies shelved; all 73 deferred
        rows classified needs-Nico; 10 manifests and four gates pass
RESULT: Tier-0 tree exhausted without behavior, law, baseline, or list changes
```

Evidence:

- `live-runs/N5-3-intentoff-20260801-001000.md`
- `live-runs/N5-5-backpedal-20260801-001500.md`
- `live-runs/N5-housekeeping-triage-20260801-002000.md`
- `live-runs/N5-7-night-hygiene-20260801-003000.md`
- final manifest: `live-runs/manifests/N5-final-20260801-003000.sha256`

### Full final status table

| Item | Final status |
|---|---|
| 1-1 | SHELVED-BLOCKED |
| 1-2 | SHELVED-BLOCKED |
| 1-3 | CLOSED-PASS |
| 1-4 | CLOSED-PASS |
| 2-1 | SHELVED-BLOCKED |
| 2-2 | SHELVED-BLOCKED |
| 2-3 | SHELVED-BLOCKED |
| 2-4 | SHELVED-BLOCKED |
| 2-5 | SHELVED-BLOCKED |
| 2-6 | SHELVED-BLOCKED |
| 2-7 | SHELVED-BLOCKED |
| 2-8 | SHELVED-BLOCKED |
| 2-9 | SHELVED-BLOCKED |
| 2-10 | SHELVED-BLOCKED |
| 2-11 | SHELVED-BLOCKED |
| 2-12 | SHELVED-BLOCKED |
| 3-1 | SHELVED-BLOCKED |
| 3-2 | SHELVED-BLOCKED |
| 3-3 | SHELVED-BLOCKED |
| 3-4 | SHELVED-BLOCKED |
| 3-5 | SHELVED-BLOCKED |
| 3-6 | SHELVED-BLOCKED |
| 3-7 | SHELVED-BLOCKED |
| 3-8 | SHELVED-BLOCKED |
| 4-1 | SHELVED-BLOCKED |
| 4-2 | SHELVED-BLOCKED |
| 4-3 | SHELVED-BLOCKED |
| 4-4 | SHELVED-BLOCKED |
| 4-5 | SHELVED-BLOCKED |
| 4-6 | SHELVED-BLOCKED |
| 4-7 | SHELVED-BLOCKED |
| 5-1 | SHELVED-RULING |
| 5-2 | SHELVED-RULING |
| 5-3 | CLOSED-FINDING |
| 5-4 | SHELVED-BLOCKED |
| 5-5 | SHELVED-BLOCKED |
| 5-6 | CLOSED-FINDING |
| 5-7 | CLOSED-PASS |

Rulings queue: **13** numbered decisions. Run commits: **11** including this final
packet. Four gates: PASS at every recorded stage boundary; final build 0 warnings /
0 errors, combat-wire PASS, portrait-camera 10,534 / 1,224 / 1,289 / 56, and
move-audit-check PASS.

### Three most important findings

1. The exact server discard predicate cannot be trusted from the deployed mangosd:
   sudo attach works, but its line table is absent; Q1 recommends Build-ID-matched
   debug information without altering the running binary.
2. The fresh-target combat regression protocol is reusable and passes 31/31, while
   the server still provides no player swing; attack-error display is ready for all
   five real server opcodes and intentionally synthesizes nothing.
3. The queued first `IntentOff` reproduces as combat-audit trace-start/setup-cluster
   normalization, not a new production pairing failure; both fresh backpedal traces
   pass mechanics, with visual judgment correctly left to Nico.

## NIGHT_01B 1-1 — amended entry-symbol W1

Status: `SHELVED-BLOCKED` under the item-1-1-only interactive-wait clause; the
autonomous build queue continues.

```text
PREDICTED: one concurrent gated attack reaches entry-symbol discrimination
ACTUAL: symbols/return resolved and armed; W1F was concurrent but fresh-spawn
        observation failed, the mechanical gate refused, and no attack was sent
RESULT: NO CAUSAL VERDICT; no-gdb-hit outcome is not promoted
```

Full attempt and hygiene reconciliation:
`live-runs/N1B-11-entry-symbol-wait-20260801-083000.md`.

Boundary gates: Debug build PASS (0 warnings / 0 errors), combat-wire PASS,
portrait-camera PASS (10,534 / 1,224 / 1,289 / 56), move-audit-check PASS.

## NIGHT_01B 4-1 — gossip

Status: `CLOSED-FINDING`.

```text
PREDICTED: build and live-prove the missing gossip decode/route/text family
ACTUAL: client family and byte checker complete; live vendor + quest hello sends
        received no server gossip/service response in bounded observation
RESULT: client increment accepted; server-silence finding frozen and queued
```

Evidence: `live-runs/N1B-4-1-gossip-20260801-084200.md`. Boundary gates:
Debug build PASS (0 warnings / 0 errors), combat-wire PASS, portrait-camera
PASS (10,534 / 1,224 / 1,289 / 56), move-audit PASS, interface-wire PASS.

## NIGHT_01B 2-1 — vendor

`CLOSED-FINDING`: list/buy/sell/buyback protocol, state, verdicts, runner and
packet-backed merchant UI built; byte gate passes. A 12/12 live runner flushed
the exact vendor list CMSG but the bounded wire log contains no server vendor
response, so no unsafe buy/sell/buyback was attempted. Evidence:
`live-runs/N1B-2-1-vendor-20260801-085100.md`.

## NIGHT_01C 3-1 — cast correctness and spell sweep

`CLOSED-FINDING`: spell pre-send gate, named-result channel, known-spell CSV,
renderer-state/effect STRING columns, roster provisioner, sequence-scoped runner,
and descriptor aura check are built. TEST/Warrior produced 65 non-passive known
spell rows. A GM-off rank-1 Battle Shout flushed exact body
`11 1A 00 00 00 00`, but received no spell response and applied no aura; the
adjacent `.gps` control was delivered. Registered once as
`BLOCKED-BY:F-SILENT-INTERACT`. The account reached its 10-character cap after
creating `Nbwarhuman`; stale-character deletion was not permitted, so the
remaining class representatives were not fabricated around. Evidence:
`live-runs/N1C-3-1-spell-wire-20260801-093000.md`.

## NIGHT_01C 3-5 — channeled spells

`CLOSED-PASS`: the channel verdict/CSV family, cast-bar lifecycle, renderer loop,
periodic tick observation, and movement cancellation are built. A GM-off Drain
Life passed its mechanical pre-send gate and the server returned start, update,
four periodic damage ticks at approximately one-second intervals, and stop.
Evidence: `live-runs/N1C-3-5-channeled-spells-20260801-102000.md`.

## NIGHT_01C 3-6 — auras, buffs, and debuffs

`CLOSED-PASS`: aura apply/remove/stack/duration/cancel verdicts, player countdown,
right-click cancel, target debuff display, and build-5875 aura opcodes are built.
The instrument exposed and the fix handles duration-before-slot-replacement
ordering. A GM-off Fortitude apply/timer/cancel/remove run passed 33/33.
Evidence: `live-runs/N1C-3-6-auras-20260801-103200.md`.

## NIGHT_01C 3-7 — visual-effect presence sweep

`CLOSED-FINDING`: exact DBC stage-kit rendering and a full 67-row TEST known
spell supplier sweep are built. Forty spells resolve model suppliers; twelve
visual chains contain no model and fifteen spells have no visual chain. The
fresh seven-school contact sheet is queued Q18 for perceptual judgment.
Evidence: `live-runs/N1C-3-7-visual-effect-sweep-20260801-104100.md`.

## NIGHT_01C 3-8 — spell errors

`CLOSED-PASS`: server and local cast refusals now share a named, copyable red
error feed and `spell-error` CSV. LOS, power, range, `DONT_REPORT`, and unknown
fallback mappings pass; GM-off live zero-mana and out-of-range Fireball legs
were displayed and mechanically recorded. Evidence:
`live-runs/N1C-3-8-spell-errors-20260801-104600.md`.

## NIGHT_01C 2-2 — trainer

`CLOSED-PASS`: exact trainer list/buy protocol, bounded 38-byte row parser,
availability/cost UI, copyable verdicts, and roster-safe runner support are
built. A GM-off novice-warrior flow passed 28/28: five services decoded, service
6674 purchased for 10 copper, server success arrived, and learned spell 6673
appeared in the client spellbook with an independent `ADDED` delta. Evidence:
`live-runs/N1C-2-2-trainer-20260801-111500.md`.

## NIGHT_01C 2-3 — questing

`CLOSED-FINDING`: the build-5875 quest protocol, bounded parsers, log/objective/
economy state, quest UI, verdicts, runner, and byte gate are built. Four GM-off
acceptance scenarios pass 100/100 checks in aggregate, including accept,
kill/item objectives, completion, exact reward XP/money, reward choice, and
abandon. Quest-giver hello/list is the single
`BLOCKED-BY:F-SILENT-INTERACT` row; direct details and the remaining lifecycle
pass. Evidence: `live-runs/N1C-2-3-questing-20260801-113500.md`.

## NIGHT_01C 2-4 — loot

`CLOSED-PASS`: copyable loot send/response/item/money/economy/release verdicts,
exact bodies, runner actions, empty-state handling, and the expanded byte/state
gate are built. An isolated, GM-provisioned and GM-off live corpse flow passed
36/36: guaranteed item 4951 landed, 5 copper cleared, the purse moved exactly
28→33, and release completed. The independent empty-state runtime replay passed
7/7. Evidence: `live-runs/N1C-2-4-loot-20260801-115500.md`.

## NIGHT_01C 2-12 — character sheet and inventory

`CLOSED-PASS`: exact equip/swap bodies, server-location transitions, character
stat/item STRING verdicts, and complete item tooltip fields are built. The GM-off
live flow passed 25/25: Worn Shortsword matched the read-only DB row and wire
template, retained durability 20/20, moved backpack→main hand→backpack, and the
server-authoritative displayed damage changed from 4.94–6.94 to 4.14–4.14 after
unequip. Evidence: `live-runs/N1C-2-12-character-inventory-20260801-120500.md`.

## NIGHT_01C 2-5 — bank

`CLOSED-PASS`: exact banker activation, deposit/withdraw, DBC price, purchase,
bank state/UI, runner, and byte-gate families are built. The GM-off live flow
passed 31/31: bank open returned, one item moved backpack→bank→backpack with
exact swap bodies, and a 10,000-copper bag-slot purchase changed money and the
server-authoritative slot count from 1→2. Evidence:
`live-runs/N1C-2-5-bank-20260801-121500.md`.

## NIGHT_01C 2-6 — mail

`CLOSED-FINDING`: exact send/list/take/return/delete wire, bounded inbox state,
attachment/COD/money fields, expiry display, mailbox UI, verdicts, runner, and
byte gates are built. The GM-off streamed-mailbox list request was server-silent
with delivered `.gps`, recorded once as `BLOCKED-BY:F-SILENT-INTERACT`; the
runtime replay passed all send, take-money, take-item/COD, return, delete, and
expiry assertions. The scenario passed 24/24. Evidence:
`live-runs/N1C-2-6-mail-20260801-123000.md`.

## NIGHT_01C 2-7 — auction house

`CLOSED-FINDING`: exact browse/filter/page, create, bid/buyout, cancel,
owner/bidder list, result, notification, bounded state, deposit preview, UI,
runner, and byte gates are built. The GM-off live leg opened house 1 and decoded
browse and owner lists; its exact create request received the server's explicit
restricted-account result because this test account retains security status and
the realm disallows GM trades. Runtime replay verified two exact rows,
pagination, lifecycle successes, notifications, and mail-refresh interplay. The
scenario passed 39/39. Evidence:
`live-runs/N1C-2-7-auction-20260801-124300.md`.

## NIGHT_01C 2-8 — crafting and professions

`CLOSED-PASS`: learned tradeskill activation, DBC recipe/reagent/product joins,
live inventory counts, skill-range colors, craft sends, cast-bar linkage,
server skill-up observation, learned-recipe deltas, UI, runner, and gates are
built. The GM-off Alchemy leg decoded three recipes, crafted Elixir of Lion's
Strength through a 3,000 ms server cast, created item 2454, and independently
observed Alchemy increase 1→2. The scenario passed 30/30. Evidence:
`live-runs/N1C-2-8-professions-20260801-125800.md`.

## NIGHT_01C 2-9 — guild

`CLOSED-PASS`: bounded roster/MOTD/member/rank state, exact MOTD/promote/
demote/leave/disband sends, event/results, UI, runner, and byte gates are built.
The isolated GM-created `MSUINight` guild passed GM-off live roster and MOTD
mutation/re-read legs; a two-member runtime replay covered online/offline rows
and all remaining lifecycle results. The scenario passed 30/30 and the guild
was deleted afterward. Evidence: `live-runs/N1C-2-9-guild-20260801-130400.md`.

## NIGHT_01C 2-10 — guild tabard

`CLOSED-FINDING`: exact designer/save/result/economy/UI/renderer/runner families
are built. An isolated GM-created guild and entry-5193 designer completed the
GM-off save with server result 0 and an independent 10-gold delta; item 5976
equipped and six exact shipped MPQ layers rendered on the character. Designer
activation retained the single registered `F-SILENT-INTERACT` row. The final
scenario passed 40/40 and visual judgment is queued Q19. Evidence:
`live-runs/N1C-2-10-tabard-20260801-132000.md`.

## NIGHT_01C 2-11 — talents

`CLOSED-FINDING`: exact Talent/TalentTab DBC catalogs, nine-class three-panel
trees, rank/tier/prerequisite/point gates, spend/reset-removal/wipe-cost wire,
server confirmation, UI, runner, and byte gates are built. TEST/Warrior reset
to 51 points, then a GM-off rank-zero spend learned Improved Heroic Strike and
changed the authoritative point field 51→50. The real trainer unlearn option
retained one `BLOCKED-BY:F-SILENT-INTERACT` row; the server response replay
displayed its exact 10,000-copper cost. The final scenario passed 41/41 before
the added bounded trainer prompt leg and 41/41 in the combined evidence run.
Evidence: `live-runs/N1C-2-11-talents-20260801-134000.md`.
## NIGHT_01C 4-2 — game objects — 2026-08-01 13:50 local

Status: `CLOSED-FINDING`

- Built the missing 1.12 game-object family: exact `CMSG_GAMEOBJ_USE` full-GUID send,
  `SMSG_GAMEOBJECT_CUSTOM_ANIM`, chained page-text query/response, range/type gates,
  STRING verdicts, nearby type/presence inventory, a world-object panel, and runner cells.
- The final live protocol passed 27/27. It observed object entry 151958/type 7, used the
  already-verified `.go xyz X Y Z map` syntax to place TEST 0.013 yd from it, turned GM
  mode OFF, and flushed body `63680096510210F1`. The bounded response leg is the single
  required `BLOCKED-BY:F-SILENT-INTERACT` row; `.gps` controls were delivered on both sides.
- Chest/loot, door, lever/button, spell-focus presence, custom-animation, and readable-page
  paths were byte/state replayed through the runtime. The landed 2-8 prerequisite was proved
  by provisioning Herbalism (`.learn 2366`, `.setskill 182 300 300`) and recording the
  Silverleaf/Copper Vein cells. A fresh `.help go object` returned `There is no help for that
  command`; it was therefore not used in the accepted run.
- Evidence: `live-runs/runner-20260801-134726.csv`,
  `live-runs/verdicts-20260801-134726.txt`,
  `live-runs/N1C-4-2-gameobjects-ui-20260801-134726.png`, and
  `scenarios/world/gameobjects-live.txt`.

## NIGHT_01C 4-3 — resting / XP — 2026-08-01 14:00 local

Status: `CLOSED-PASS`

- Built descriptor-level rest-state/rest-bonus instrumentation, rested-XP bar/tooltip,
  exact `SMSG_LEVELUP_INFO` decode, level/stat/health/talent-point plate verification,
  runtime replay cells for kill rested bonus and quest XP, UI, and runner commands.
- Final protocol passed 20/20. Authorized TEST was changed 60→59 with `.levelup -1` and
  59→60 with `.levelup 1`; the server sent the exact 48-byte level-up packet (health +90,
  stats +2/+1/+2/+0/-1), then descriptors independently confirmed health 2519→2609,
  talent points 49→50, and all five stat changes. `.gps` controls bracketed the run.
- Normal/rested enter/leave, bonus accumulation/consumption, doubled kill XP, and quest XP
  leaving the rested pool untouched were replayed through the same verdict/UI paths.
  Evidence: `live-runs/N1C-4-3-rest-xp-20260801-140000.md`.

## NIGHT_01C 4-4 — death / resurrection completion — 2026-08-01 14:10 local

Status: `CLOSED-PASS`

- Built repop, corpse reclaim and timer, spirit-healer activation, resurrect-request/response,
  durability-loss verdicts, corpse-store discovery, dialog/UI, and runner cells.
- Final protocol passed 26/26. TEST died (health 2609→0), GM was turned OFF, empty-body
  `CMSG_REPOP_REQUEST` was sent, the server created corpse `F101000000010127`, returned an
  exact 30,000 ms reclaim delay, applied ghost aura 8326, and teleported to the graveyard.
  `.revive` then removed ghost state. Delivered `.gps` controls bracketed the flow.
- Spirit healer sickness/durability, timed reclaim, and a named resurrect offer with accept/
  decline exact bodies were replayed through production paths. Evidence:
  `live-runs/N1C-4-4-death-rez-20260801-141000.md`.
## 2026-08-01 14:20 local — item 4-5 innkeeper / hearth

Implemented exact binder activate/confirm and bind-point update protocol, an innkeeper eligibility
gate and home/cooldown UI, and Hearthstone item-to-spell tracking through cast and teleport. The
unattended protocol passed 26/26. On dedicated TEST with GM OFF, item 6948 generated real server
start/go packets for spell 8690 across its 10-second cast and a server home-position update. The
GM-off binder request was wire-correct but retained the one registered `F-SILENT-INTERACT` row;
the distinct teleport-distance and one-hour cooldown displays were exercised through the exact
runtime replay path. Evidence: `live-runs/N1C-4-5-hearth-20260801-142000.md`.
## 2026-08-01 14:30 local — item 4-6 flightmaster

Built the exact taxi status/map/activation/reply family, discovered-node UI, player flying-spline
playback, control lockout, and arrival handoff without touching excluded F3–F6 behavior. The live
GM-off status request to a provisioned flightmaster returned known status 1; the available-node
map request retained the one registered `F-SILENT-INTERACT` row. Production-path replay covered
map, purchase, lockout, flight, and arrival, and the protocol passed 25/25. Evidence:
`live-runs/N1C-4-6-flightmaster-20260801-143000.md`.
## 2026-08-01 14:40 local — item 4-7 environment audit sweeps

Added a read-only unattended environment audit over the current build. The dual-band movement
check passed all 8 baseline/expected sets; 115 portal definitions passed joined-volume and target
validation; 33 instance rows passed arrival-plan/catalog checks; and five resident liquid samples
recorded current renderer state. The live protocol passed 8/8 with zero portal or instance errors
and delivered `.gps` controls. No F3–F6 behavior changed. Evidence:
`live-runs/N1C-4-7-environment-audit-20260801-144000.md`.
## 2026-08-01 14:50 local — item 5-4 Benilla golden traces

Implemented and controlled a 16-metric MSUI-versus-Benilla golden-diff output. The actual Stage B
launch was attempted at the recorded checkout: the recorded PowerShell launcher is absent, `cargo`
is unavailable, and the checkout contains neither scripted input nor a trace dumper. No golden was
fabricated. Existing Q11 therefore remains `SHELVED-RULING`; recommendation is the I14-authorized
eyeball-only downgrade while analytic bands remain primary. Evidence:
`live-runs/N1C-5-4-benilla-goldens-20260801-145000.md`.
