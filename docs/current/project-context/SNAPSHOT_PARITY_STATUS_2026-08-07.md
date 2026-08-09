# Benilla versus MSUI snapshot parity — pause handoff

Last updated: 2026-08-08 19:40 UTC (SpellBookFrame promotion, dependency-packet sweep, sound port,
and first accepted live SpellBook evidence folded in; see the dated section below). Resume from this
document together with [`SNAPSHOT_PARITY_WORKFLOW.md`](SNAPSHOT_PARITY_WORKFLOW.md). The workflow is
authoritative for classification, overwrite protection, evidence, acceptance, and verification. This
page records the current working-tree state and the exact next boundary.

## Frozen comparison

- Pair: `pair-de1462d2f83d2f33`
- Benilla snapshot: `benilla-5089066fea6adbc3`
- MSUI target snapshot: `msui-88444818e8c4b271`
- Frozen Benilla source ZIP SHA-256:
  `267cea4f181dea34bd1fee84d5387d4f1d570bba524b231853d4864e436af108`
- Review queue: 1,021 bounded packets.
- Verification boundary: **0 packets are verified**.

The latest fresh workspace validation is
`parity/reports/packet-workspace-validation.json`. It reports 1,021 packets, 984
blocked/unreviewed, 0 verified, and **0 dossier errors**. Canonical ledgers stand at **168 traces /
166 claims** (`parity/traces/current.jsonl`, `parity/claims/current.jsonl`); the queue has 36
implementation-phase and 985 review-phase packets, and **every implementation-phase packet has full
claim coverage of its facts** (zero unclaimed facts remain anywhere in the implementation phase). A
clean dossier means its implemented/unverified clinical record is internally complete; it does not
mean the behavior is verified or terminal in the queue.

## Non-negotiable clinical rule

Apply the rule independently to every observable trace:

- absent behavior may be ported;
- reproduced broken behavior may be repaired;
- functional equivalent, different, or intentional MSUI behavior is preserved;
- unclear behavior stays blocked.

A packet-wide `missing` or `broken` label never authorizes a wholesale Benilla replacement. Each
implemented packet must show MSUI before, the full frozen reference dependency closure, the atomic
decision, MSUI after, changed and protected `path:symbol-or-hunk` mappings, all 14 checkpoints, and a
current evidence seal.

## Clean implemented/unverified dossiers

These 37 packet folders have packet-local before/reference/after evidence, all 14 acceptance rows,
trace dispositions where linked, empty verification artifact IDs, current seals, and zero packet-local
validator errors:

| Surface | Packet ID |
|---|---|
| BagFrame part 1 | `packet-d2bff763aea84ed4` |
| BagFrame part 2 | `packet-2d8bf71c6c222663` |
| BagFrame part 3 | `packet-83a66892bf2db372` |
| BuffFrame | `packet-55843a21670272b5` |
| CastingBar | `packet-3d775bf40e48fa14` |
| CharacterFrame part 1 | `packet-df4fb76448d44ed4` |
| CharacterFrame part 2 | `packet-848c09ce5805caea` |
| EnchantConfirm | `packet-a1bc988c58ac3967` |
| GameMenuFrame | `packet-baa61a2c1366b658` |
| InspectFrame | `packet-a0942138a48e8b4b` |
| MailFrame part 1 | `packet-2d926b3532181004` |
| MailFrame part 2 | `packet-72a7fda1a26d33fc` |
| MailFrame part 3 | `packet-f8d9e5749f34692d` |
| MultiBars | `packet-c5f64e6e922cc2b1` |
| PartyFrame | `packet-be7dc064ffb215ce` |
| PetActionBar | `packet-1bc2add72d6d062b` |
| QuestFrame part 1 | `packet-772ed3a05a4e61db` |
| QuestFrame part 2 | `packet-3c1eb663c7772bd9` |
| QuestFrame part 3 | `packet-ff0104d615ec3afa` |
| SkillFrame | `packet-f1bdcf7c64cbc4be` |
| Group session boundary | `packet-231ad7e7968d59c3` |
| Group retained state | `packet-6b19d8133b06b28b` |
| Group direct feed | `packet-b27811a95376641a` |
| Group protocol messages | `packet-237cf77997c38a7a` |
| Group protocol writer | `packet-62a4001bbc1ec5ca` |
| Group protocol tests | `packet-bc12fc3b0ad08810` |
| GameTooltip | `packet-9a1c649d1b69d3d6` |
| UIParent | `packet-073056dde50fbcf7` |
| Fonts | `packet-251df25123373c98` |
| MerchantFrame | `packet-44d93b17a73aa49b` |
| Tooltip verbs (script) | `packet-7b0da864ffea148e` |
| Tooltip mod (script) | `packet-29839257df34f3d1` |
| Tooltip unit (script) | `packet-ee94abd30f2d6882` |
| UiPanels | `packet-def2f87d85e3064d` |
| SpellBookFrame | `packet-14517ee3cf1caf5f` |
| Script button (engine) | `packet-df3f076dec39a1e1` |
| Widget kinds (engine) | `packet-12fbf9d7b17bc33f` |

All remain **implemented/unverified**. Their remaining live/visual/input/wire/audio/user gates are
recorded in their own dossiers. The user's observed BagFrame result is valuable visual feedback, but
it does not waive the formal remaining gates.

Important current boundaries:

- QuestFrame was clinically reopened after the earlier shallow capture. Its current dossier is clean,
  but no real NPC plus item-giver end-to-end run or complete visual/state/audio matrix exists.
- SkillFrame has a clean packet-local implementation dossier, but its frozen queue entry has no global
  claims and its 169 facts remain nonterminal. Do not infer queue completion from dossier integrity.
- PetActionBar now rejects staged player-as-pet capture. Its real-pet protocol is packet-local and has
  not been run.

## Current boundary: sealed group dependencies and shared UI foundations

PartyFrame `packet-be7dc064ffb215ce` was clinically re-audited and repaired. Its old staged 10/10
capture, high-resolution-portrait statement, broad popup-exactness statement, and synthetic live-proof
wording were removed. The current packet has eight sealed evidence files, all 14 checkpoints, four
exact mixed trace dispositions, empty verification IDs, and an independent runtime/ledger review.
It remains **implemented/unverified**.

Re-pinning the four Party traces to their complete frozen dependency closure correctly promoted 14
previously hidden dependency packets ahead of new surfaces. They cover session teardown, group state
and protocol, GameTooltip/UIParent management, StaticPopup behavior, button ownership, and tooltip
runtime. They are not licensed for a dossier-only rubber stamp: each packet contains broader unreviewed
facts beyond the narrow Party trace and must receive a full atomic audit before any write.

The first six promoted group/session/protocol packets have now received their bounded runtime and
protocol implementation. Ten new traces cover session boundaries, roster/system-line state, complete
member stats, raid-target state, direct-feed equivalence, outbound builders, parsed-but-unused
minimap/ready-check support, and the deliberate absence of synthetic Party staging. Four nonterminal
claims were added: three are `implementedUnverified`; the synthetic sandbox claim is `divergent` under
`user-2026-08-08-party-observational-evidence-only`. All verification ID lists are empty.

The exact ledger audit is clean: the six frozen sources contain 456 facts; 425 are assigned exactly
once to the ten group traces; the remaining 31 are the co-resident, non-Party facts in
`net/apply/session.rs` and are explicitly preserved/out of this implementation. There are no missing
fact IDs, stale file/evidence hashes, duplicate trace IDs, or intra-partition overlaps. The queue and
the six packet `reference.md` files were regenerated from those ledgers.

The six group/session/protocol dependency dossiers are now sealed and independently reviewed:

- `packet-231ad7e7968d59c3`
- `packet-6b19d8133b06b28b`
- `packet-237cf77997c38a7a`
- `packet-62a4001bbc1ec5ca`
- `packet-bc12fc3b0ad08810`
- `packet-b27811a95376641a`

Each folder has packet-local before/reference/decision/after evidence, all 14 checkpoints, exact
changed and preserved symbol/hunk mappings, empty verification IDs, a current seal, and zero
packet-local validator errors. The writer dossier preserves the invite, accept, decline, and member
stats request methods and wrappers already present in the frozen MSUI target; only genuinely absent
builders and administration support are classified as ported. Protocol builder presence is not UI
reachability or server proof, and the frozen group protocol tests are deterministic evidence rather
than shipped runtime. Two direct checker cases remain explicitly unreviewed instead of being
overclaimed: the LEAVE/empty-name/IGNORING_YOU command-result body and the 39/40 roster boundary.

Two additive shared foundations are prepared behind that boundary:

- Button/tab release ownership and exact pressed/highlight/font state are wired through the existing
  `Program.VanillaUi` renderers, with their geometry/assets/callbacks preserved.
- GameTooltip owner generations, stale-owner rejection, complete same-owner content clearing, fade,
  money/newbie/unit laws, and future-only UIParent offsets are implemented and deterministically
  checked. The frame coordinator now advances fade, resolves at the tooltip stratum before dialogs,
  applies each retained owner's explicit hide/fade policy through the fullscreen WorldMap return,
  and rejects stale generations. Party is the first routed producer: the original renderer is
  deferred unchanged, ownership follows fixed PartyMemberFrame1..4 controls, and live health follows
  the separate party1..4 tokens. Spell/action/pet B2 is now green: fixed physical button owners,
  immutable prepared spell/string snapshots, explicit immediate-hide policy, and the original
  renderers all pass focused and full checks. Item and ShoppingTooltip B3 is also green: every rich
  item producer prepares immutable paint operations before the tooltip stratum, ownership follows
  fixed physical controls (including container buttons across bag-size changes), comparison ordinals
  survive empty first arms, and deferred comparison evidence completes only after shared arbitration.
  Aura and minimap B4/B5 is green as well: aura ownership follows fixed BuffButton ordinals with
  immediate hide, while resource-dot ownership follows exact game-object GUIDs and retains an exact
  0.5-second fade without changing the established `SetTooltip` cursor renderer. World-unit B6 is
  now independently approved: strict creature-query hit/miss retention, one coalesced query per
  template entry, immutable typed rows, rendered-static rebuilds, health-only pushes while hovered,
  frozen retained health during the 0.5-second loss fade, managed default anchoring, ordered-edge
  screen clamping, and exact backdrop / `$parentThicken` / content layering all pass. Faction-name
  gating, per-pet given names, and world-gameobject hover ingress remain explicit later dependencies.
  Exact shared money/newbie presentation is now implemented and deterministically green: visible
  denominations collapse zero slots, zero money retains Copper 0, the immutable plate uses the
  frozen NumberFontNormal and denomination UVs, and detailed newbie text uses the exact 260px
  source-backed wrapper. No existing surface opts into either capability, so current Mail, item,
  minimap, action, and binding presentation remains unchanged. Independent frozen-source review is
  approved after pinning whitespace-only wrapped rows; the promoted dependency-packet audit remains
  open, so this is not yet a terminal ledger or dossier claim.

The UIPanel/StaticPopup laws are pure and renderer-neutral. The first UIPanel adapter phase is now
landed and green as an observation-only shadow: it samples the exact 21 registered predicates after
quest lifecycle, records only unambiguous single-edge advisory transitions, latches unknown state on
legacy conflicts, and recovers authoritatively at an all-closed census. It dispatches no effect and
changes no panel flag, pixel, callback, sound, bag/keyring state, wire, or telemetry. The profession
provenance prerequisite is also landed: only spell effect 47 is intercepted, signed misc-value zero
selects TradeSkill, nonzero selects Craft, missing provenance remains unresolved, and manual
diagnostic opens never guess from skill line or name. The cast path performs that interception before
passive/known-spell gates, so a profession opener cannot fall through to a cast wire. No panel caller
has been made authoritative yet; the next bounded authority slice is the host-confirmed
Character/SpellBook replacement pair.

## 2026-08-08 later session — SpellBookFrame, engine-dependency sweep, sound port, first live SpellBook evidence

This session (after the 10:07 UTC update) completed the following, moving the canonical ledgers
from 141/139 to **168/166** and the blocked/unreviewed count from 995 to **984** with workspace
validation green throughout:

1. **SpellBookFrame `packet-14517ee3cf1caf5f` promoted and dossiered.** A SpellBook-only
   noncanonical plan (`parity/reports/promotion-plan-spellbookframe-only-2026-08-08.json`) with a
   temp-ledger preflight (`preflight-promotion-plan-spellbookframe-only-2026-08-08.ps1`) proved the
   170-fact exact-once partition, zero ID collisions, exact claim unions, current hashes, and zero
   borrowed facts from the seven deferred dependency packets (472 facts) before the guarded apply
   (`apply-spellbookframe-ledger-records-2026-08-08.ps1`) added 14 traces and 15 claims (one direct
   notRuntime). The dossier is sealed with all 14 checkpoints; the rich-tooltip preserved
   difference is recorded under `user-2026-08-06-preserve-present-differences`; the Party and
   UiPanels canonical records were byte-preserved.
2. **SpellBook sound gap ported** (its only zero-dependency gap): `SpellbookLaw` now owns
   `OpenSound`/`CloseSound`/`PageTurnSound` cue constants; `SetSpellbookOpen` plays
   igSpellBookOpen/igSpellBookClose on real state transitions and `DrawPageButton` plays
   igAbiliityPageTurn on enabled page clicks via the shared `PlayUiSound` path. The claim moved
   gap -> implementedUnverified.
3. **Engine dependency packets reviewed to full closure.** `packet-df3f076dec39a1e1`
   (script/button.rs, 13 facts) and `packet-12fbf9d7b17bc33f` (widget/kinds/mod.rs, 22 facts)
   received full atomic dispositions: engine machinery is terminal `internalSupport` under the
   workflow's helper rule with exact MSUI per-surface preserved anchors; the Party-owned
   `wants_click`, `region_visible`, and `TooltipState` facts remain solely with their existing
   Party claims. Both dossiers are sealed.
4. **Unclaimed-fact debt cleared across the implementation phase.** The 31 co-resident session.rs
   facts (`packet-231ad7e7968d59c3`) got 12 atomic traces (login, character management, cinematic,
   logout, mail-time, teleport/transfer, reputation, pong equivalents), and the BagFrame chunks
   1-3 got their 23+97+122 structural presentation facts attached to their owning traces after
   verifying MSUI's implementation symbol by symbol. **Four honest new gaps were recorded instead
   of papered over:** `claim-sessionrs-transfer-aborted-gap-001` (SMSG_TRANSFER_ABORTED defined
   but unhandled), `claim-sessionrs-time-speed-gap-001` (SMSG_LOGIN_SETTIMESPEED defined but
   unhandled), `claim-bagframe-keyring-state-art-gap-001` (keyring pushed/hover state art not
   rendered), and `claim-bagframe-slot-depress-art-gap-001` (UI-Quickslot-Depress absent from bag
   item slots). Zero unclaimed facts remain in any implementation packet.
5. **First accepted live SpellBook evidence** (user-authorized client launches; four runs, all
   `PROTOCOL_DONE failures=0`; run D accepted at
   `live-runs/spellbookframe-live-20260808d`, artifacts copied into the packet's
   `evidence/live/run-20260808/`): real P-binding open/close through the host route with parsed
   `inWorld=true`/`networkState=InWorld` and `fixtureStaged=false`; the live sound journal
   contains exactly one igSpellBookOpen (SoundEntries 829, iAbilitiesOpenA.wav) and one
   igSpellBookClose (830, iAbilitiesCloseA.wav) with zero extra sounds; a clean unobstructed
   open-book screenshot plus draw CSV and hash-pinned manifest. The affected checkpoints record
   the live evidence but remain formally open (page-turn cue needs a multi-page book; pointer
   input, the mechanical UI diff, and the per-state screenshot matrix are still missing; the
   recorded human session confirmation is still outstanding). Protocols:
   `parity/reports/spellbookframe-live-protocol.txt` and `-v2.txt` (v2 closes the F6 calibration
   overlay, which defaults open in DevTools mode). A one-line dev-tools fix makes
   `UpdateSpellbookInput` honor the live-held F6 key like the P binding.
6. Runtime/dev-tools code touched this session: `MSUIClient/Program.Spellbook.cs` (sound cues,
   F6 live-key fix), `MSUIClient/Engine/UI/SpellbookLaw.cs` (cue constants). A pre-existing CA2014
   warning in `MSUIClient/Engine/UI/GlueAdditive.cs` was flagged as a separate task and left
   untouched.

Open SpellBook gaps blocked on unpromoted dependency packets (unchanged): cooldown child
(Cooldown.xml `packet-43a9e606883fa13f`), active checked overlay (script spellbook.rs
`packet-aa931af7882d4593`), shift-click pickup/receive-drag (ActionBar `packet-4880737ac18ad66c`
/ `packet-1c078e3a73352cb3` and cursor packets), tabs five through eight (ui_spellbook.rs
`packet-23def27e08c1de65`).

## Evidence that must remain invalid or non-verifying

1. The first CastingBar “live” attempt used `MSUIClient/client-config.json` with networking disabled
   and opened the offline synthetic world viewer. It is invalid.
2. A separate enabled-config CastingBar run reached an internal `InWorld` state, but it was not
   accepted as the intended user-visible session, predates the final same-render-frame scenario fix,
   and has a CSV/manifest fraction mismatch. It is observational history, not verification.
3. The historical PetActionBar “4/4 live” result used `StagePetActionBarProof` to make the logged-in
   player look like a pet and sent no pet mutations. It is invalid. Staged PetActionBar capture is now
   rejected in production.
4. The historical QuestFrame “17/17” run mostly staged/waited/armed/captured. It did not prove clicks,
   transitions, wires, sounds, errors, lifecycle, item givers, or real accept/turn-in behavior.
5. A build, source-constant assertion, screenshot, or runner step count is never a substitute for the
   relevant typed checkpoint evidence.

Some global Pet claim/trace prose still contains historical “live capture” or blanket-usability wording.
Those rows are nonterminal and have no verification IDs; do not treat that prose as current proof. The
Pet packet's sealed clinical matrix is the current re-audit record until a new target snapshot and
ledger refresh deliberately re-pin the global claims.

## Last deterministic state

At the pause boundary:

- `dotnet build MSUIClient.sln --no-restore`: PASS, 0 warnings, 0 errors.
- Focused GroupProtocol checker: PASS.
- Focused PartyFrame interface checker: PASS.
- Focused UiFoundation checker: PASS.
- Focused GameTooltip checker: PASS, including same-owner stale money/comparison/live-state clearing.
- GameTooltip B0 frame-coordinator checks: PASS for fade tick, one-callback resolution, stale-owner
  rejection, cross-frame cleanup, fullscreen cleanup, and explicit unseen-owner departure.
- GameTooltip B1 Party adapter checks: PASS for fixed-slot ownership, same-slot occupant rebinding,
  health-only token pushes, retained disconnect fade, deferred capture completion, and unchanged
  Party tooltip presentation.
- GameTooltip B2 spell/action/pet adapter checks: PASS for fixed physical owner identities,
  prepared immutable callbacks, same-frame last-owner arbitration, complete channel clearing, and
  unchanged spell/simple-tooltip presentation.
- GameTooltip B3 item/comparison adapter checks: PASS for deep immutable body snapshots, physical
  container-seat rebinding, fixed bank/mail/loot/quest/vendor/action owners, stable ShoppingTooltip
  ordinals and anchors, stale-owner suppression, deferred parity completion, and removal of every
  production `DrawItemTooltip` / `DrawItemTooltipBody` entry point.
- GameTooltip B4/B5 aura/minimap adapter checks: PASS for fixed aura-button and resource-GUID owners,
  immutable aura content, right-click preservation, explicit immediate/fade policies, retained
  minimap requeue and alpha, fullscreen renderer-free departure, stale-owner rejection, and terminal
  cleanup. Frozen minimap last-pointer-seat retention remains explicitly unverified; no replacement
  cursor positioning was invented.
- GameTooltip B6 world-unit adapter checks: PASS for strict creature-query hit/miss parsing,
  negative-cache query suppression, typed line/rank/reaction/flag laws, rendered-static rebuilding,
  hover-only health refresh, retained health through fade, managed placement, exact span-preserving
  screen clamp, immutable skin/scale/texture capture, navy backdrop, `$parentThicken`, and stale-owner
  rejection. The external player-level read is deliberately included in the rebuild signature as a
  repair of the frozen same-hover stale-line bug; faction/reputation, per-pet given-name, and world-GO
  ingress remain unimplemented boundaries.
- GameTooltip B7/B8 money/newbie checks: PASS for visible-denomination zero collapse, Copper 0,
  verbose money-string separation, NumberFontNormal measurement, denomination UVs, icon-before-number
  layering, exact 13/4/4 row geometry, 260px wrapping with `.25` tolerance, repeated and
  whitespace-only row preservation, Unicode force-breaks, logical-row gaps, guarded publication,
  same-owner clearing, and the no-current-producer boundary. Independent review found no remaining
  runtime blocker.
- Focused UIPanel observer checker: PASS for the exact 21-row registry, ambiguity/refusal detection,
  immediate all-closed recovery, unresolved profession provenance, locked-Taxi non-mutation, and
  source fences proving that no host state or effect is dispatched.
- Profession opener prerequisite checks: PASS for effect-47 interception, signed zero/nonzero
  TradeSkill/Craft routing, missing-provenance ambiguity, diagnostic-null preservation, and the
  no-cast-fallthrough source order.
- Full interface checker: PASS with 15 mounted archives.
- `tools/ui-parity self-test`: PASS for extended schema, strict diff, and containment.
- Party runtime and the four corrected Party trace/claim rows passed an independent frozen-source
  review. Three claims are `implementedUnverified`; the preserved presentation claim is nonterminal
  `divergent`. Every verification ID remains empty.
- Focused/full checks recorded inside the other 25 clean dossiers remain packet-local diagnostics.
- No client was launched for Party closure, and no deterministic result is represented as live proof.

## Exact resume order

1. Read this page and the workflow. Preserve the dirty shared working tree; do not reset or regenerate
   it from the frozen target.
2. Run workspace validation against the active pair. The expected dossier baseline is 0 errors across
   1,021 packets, with 984 still blocked/unreviewed and 0 verified. Validate against
   `parity/reports/review-queue.json`; `live-runs/review-queue-party-preview.json` is stale historical
   preview data and must not drive a dossier.
3. All 36 implementation-phase packets now have full claim coverage and clean implemented dossiers;
   their remaining work is (a) live verification per the real-session gate below, and (b) porting
   the recorded gaps. The unblocked gaps with no dependency-packet prerequisite are the two session
   wire gaps (SMSG_TRANSFER_ABORTED, SMSG_LOGIN_SETTIMESPEED at `Program.Net.cs:PumpNet`) and the
   two bag presentation gaps (keyring pushed/hover art, bag item-slot depress art in
   `Program.Inventory.cs`). The SpellBook cooldown/checked-overlay/input/tabs-5-8 gaps require
   promoting their fenced dependency packets first (Cooldown.xml, script spellbook.rs, ActionBar
   chunks, ui_spellbook.rs — 472 facts total).
4. Live verification continues from the SpellBook precedent: reuse the protocol pattern in
   `parity/reports/spellbookframe-live-protocol-v2.txt` (real bindings via `key press/release`,
   `sound mark`/`sound assert`, `ui-parity <panel>` captures) and fold artifacts into each packet's
   `evidence/live/` with a reseal. The igAbiliityPageTurn cue needs a character with a multi-page
   skill line. Every run still requires the full real-session gate below, including the user's
   recorded confirmation of the visible session.
5. Reconcile the already-clean SkillFrame packet `packet-f1bdcf7c64cbc4be` with reviewed global
   traces/claims; its packet-local dossier alone does not terminally cover its 169 frozen facts.
6. Then continue the review phase in queue order once implementation packets are terminal or
   deliberately parked: TradeFrame `packet-aa4f987e593f5fbb` / `packet-e59d683733db84ba`, UnitPopup
   `packet-b415285024f24214`, and the SpellBook dependency promotions from step 3 when their gaps
   are scheduled.

Every dependency packet is a fresh atomic audit boundary. A narrow Party claim never authorizes
marking the rest of that source packet implemented.

Do not start a later review packet while an earlier implementation packet remains clinically open.

## Real-session gate for future live work

Before any artifact can be called live:

- use an explicit ignored configuration with `server.enabled=true`;
- require parsed `inWorld=true` and `networkState=InWorld`;
- have the user confirm that the visible window is the intended authenticated game session;
- use production input and real server-authored actors/data, not `ui-parity-stage` fixtures;
- arm ordinary observational capture only after the state is reached naturally;
- capture same-scenario input, wire, sound, state, PNG, strong CSV and manifest evidence;
- run the frozen-reference selection/diff and record exact preserved-deviation rows;
- obtain explicit screenshot/user review.

An actor-dependent surface such as PetActionBar cannot be proven by substituting the player, and an
internal `InWorld` flag alone cannot override the user's observation that the visible window is wrong.

## Source-control boundary

The working tree intentionally contains many coordinated runtime/tool/doc changes. Do not discard,
reset, or normalize unrelated hunks. No commit or staging operation was performed at this pause.

The large trees below are local, content-addressed/reproducible working data and should not be added to
Git wholesale:

- `parity/packets/`
- `parity/snapshots/`
- `parity/reports/`
- `live-runs/`

The workflow/status documents, reviewed ledgers, runtime changes, and verification tools are the
source-controlled project record. Before committing, inspect the existing ignore rules and stage only
the intended tracked source/doc/tool files.

## Key paths

- Workflow: `docs/current/project-context/SNAPSHOT_PARITY_WORKFLOW.md`
- This handoff: `docs/current/project-context/SNAPSHOT_PARITY_STATUS_2026-08-07.md`
- Original project context: `docs/current/project-context/SPELLS_CHARACTER_HANDOFF_2026-08-06.md`
- Reviewed traces: `parity/traces/current.jsonl`
- Comparison claims: `parity/claims/current.jsonl`
- Packet workspaces: `parity/packets/pair-de1462d2f83d2f33/`
- Machine dossier report: `parity/reports/packet-workspace-validation.json` (current: 0 errors,
  984 blocked/unreviewed, 0 verified, 1,021 packets)
- Frozen pair: `parity/snapshots/current/pair.json`
- SpellBook promotion scripts: `parity/reports/promotion-plan-spellbookframe-only-2026-08-08.json`
  plus its preflight/apply `.ps1` pair (git-ignored, local-only)
- SpellBook live evidence: `parity/packets/pair-de1462d2f83d2f33/spellbookframe-part-01-packet-14517ee3cf1caf5f/evidence/live/run-20260808/`
  (accepted run D; superseded runs A-C under `live-runs/spellbookframe-live-20260808*`)
