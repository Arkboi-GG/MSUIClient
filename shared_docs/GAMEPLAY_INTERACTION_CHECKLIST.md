# Gameplay interaction checklist

Owner request, 2026-09-07: find the subtle feedback and interaction omissions that
make ordinary gameplay feel incomplete. Begin with inventory and pet-bar dragging,
then maintain an evidence-backed list rather than relying on remembered vanilla behavior.
This is an audit ledger, not a replacement for POSSESS_LAW, the freeze rules, or
intentional MSUI designs such as the Macro Book.

The wider owner-requested game audit now lives in `FULL_GAME_COVERAGE.md`: per-ID
quest/spell discovery, temporal captures, gameplay families and remaining queue.

## Repeatable discovery

Run `dotnet run --project tools/gameplay-interaction-audit`. The ignored output is
`docs/current/gameplay-interaction-audit.json`; the tracked implementation and usage
notes are in `tools/gameplay-interaction-audit/`. It uses the client's actual MPQ
priority, DBC readers and file extraction. Archive supplier and line accompany each
reference site, so patches cannot silently change which UI is being audited.

The first scan found **216 FrameXML files, 101 distinct sound cues, 891 interaction
or visual hook sites, and 72 item-material gestures**. All 101 sound kits resolved
to nonempty, readable assets. These are discovery counts, not counts of defects or
verified features. In particular, item cues resolve through numeric DBC joins and
often have no C# sound-name literal at all.

For each lead: inspect the original trigger and surrounding conditions; trace our
entry point, acceptance gate and feedback call; check success, refusal, cancellation
and repeat input; run the relevant offline checks; finally record live evidence.
Do not add sounds just because an unused kit exists. Native-API cues cannot be fully
specified by a FrameXML `PlaySound` search; distinguish a data-supported UX choice
from a proven reproduction of native call timing.

Status vocabulary:

- **Suspected**: reference/search lead; production path has not been fully traced.
- **Confirmed missing**: traced handler omits the required behavior.
- **Implemented**: code changed; validation still pending.
- **Offline verified**: named static/data checks passed; live behavior remains unverified.
- **Live verified**: a dated reproduction passed with identified build and actor.
- **Covered in source**: a route exists; not a claim that all paths or timing work.

## Prioritized ledger

Paths below are repository relative. `Panels/`, `Hud/` and `Combat/` refer to
`MSUIClient/GameLoop/`; UI law classes live in `MSUIClient/Engine/UI/`.
Rows marked suspected remain audit work, not permission to blindly implement a cue.

| ID | Priority | Interaction and expected check | Evidence / production entry | Status |
|---|---|---|---|---|
| GI-01 | P1 | Drag a spell or macro onto an empty or occupied bar: one pickup and one placement cue, no per-frame repetition | SoundEntries 832/833; `Panels/GameLoop.Spellbook.cs`, `Panels/GameLoop.Macro.cs`, `Hud/GameLoop.ActionBars.cs` | Live actual spellbook/macro pointer pickup and placement, exact cue counts and slot restoration verified |
| GI-02 | P1 | Pick up inventory items: use the item's material sound, once; cover bags, bank, equipment and bag-bar slots | ItemDisplayInfo field 11 -> ItemGroupSounds pickup column; `PickupOrPlaceItem` previously had no cue | Live handler/material matrix and bank transfer verified; pointer/bank-bag variants pending |
| GI-03 | P1 | Place a carried item: its material put-down cue only after dispatch; same-slot cancellation, local refusal and failed send are silent | ItemGroupSounds put-down column; `PickupOrPlaceItem` after `sent` gate | Live accepted moves and silent original-slot cancellation verified; pointer variants pending |
| GI-04 | P1 | Confirm a stack split: one pickup cue when the selected count becomes carried; cancelling the dialog does not pick up | `DrawStackSplit` accepted branch is separate from ordinary pickup | Live split Enter/Escape and possession cancellation verified |
| GI-05 | P1 | Pick up from pet spellbook or pet bar: one ability-icon cue | SoundEntries 837, `Sound\Interface\uSpellIconPickup.wav`; `PickupPetBookSpell`, `PickupPetAction` had no cue | Fixed drag crash; live imp book/bar/token gestures verified; frozen/possessed/pointer variants pending |
| GI-06 | P1 | Place pet actions: cue on assignment; same-action, passive, illegal token relocation and frozen/read-only refusals stay silent | SoundEntries 838, `Sound\Interface\uSpellIconDrop.wav`; `PlacePetAction` after `TryAssign` | Live passive and same-action refusals verified; remaining gates pending |
| GI-07 | P1 | Move bar action A onto B, then put B elsewhere: B must remain on the cursor after the first drop | `MultiActionBarUiLaw.PlaceAction` returns B; `FinishActionDrag` unconditionally cleared that cursor | Live production release handler verified: displaced spell retained, placed elsewhere, temporary slots restored |
| GI-08 | P1 | Open/close the quest log through keybinding, micro-menu, close button and Escape: exactly one matching cue per state transition | Mounted `QuestLogFrame.lua` lines 87/94 names `igQuestLogOpen/Close`; `Panels/GameLoop.Quest.cs` | Live binding/Escape, actual close/Exit buttons and micro-menu verified |
| GI-09 | P1 | Select friendly NPC, hostile, neutral, and clear target: correct cue on actual selection change | Mounted `TargetFrame.lua` lines 106-115; `Combat/GameLoop.Targeting.cs` | Live self/friendly guard/GM-off hostile/repeat/clear verified; natural neutral NPC variant pending |
| GI-10 | P1 | Failed item move: cursor/slot locks recover; error once; no duplicate success cue from a later snapshot | `InventoryFailureClinicalChecks`, `ObserveBagLocks`, inventory error receiver | Live invalid equipment placement unlock/cursor recovery verified; other refusals pending |
| GI-11 | P1 | Press/cancel/repeat a spell or item: pressed state, GCD, own cooldown and finish flash match the actual action | Mounted ActionButton hooks; `CooldownProtocolClinicalChecks`, action bars and inventory rendering | Suspected timing/transition audit; not a confirmed gap |
| GI-12 | P1 | Change controlled body while holding an item or pet action: old payload clears, purse/feedback owner follows the new body | `ResetBodySessionUiOnControlChange`, POSSESS_LAW; item feedback now tags `ControlledGuid` | Fixed cross-body carried-item/split/spell cursors; live both directions verified; pet variant pending |
| GI-13 | P2 | Scroll chat up/down/bottom: button cue only on actual input, not new messages/redraw | Mounted `CombatLog.xml` lines 34/61/89 names `igChatBottom/ScrollDown/ScrollUp` | Live actual GUI up/down/bottom buttons and held-button nonrepeat verified |
| GI-14 | P2 | Coin amount dialog open, selection, OK, cancel: distinct intended cues without duplicate coin-change audio | Mounted `CoinPickupFrame.lua/xml`, `MONEYFRAMEOPEN/CLOSE`, `igBackPackCoinSelect/OK/Cancel` | CoinPickupFrame not used by inline mail/trade denomination inputs; input UX still pending |
| GI-15 | P2 | Bank bag control feedback | Mounted `BankFrame.lua` line 194, `BAGMENUBUTTONPRESS` is bag open/shift pickup, not slot purchase | Original purchase-cue lead disproven by surrounding source; actual bank gestures remain in GI-02/03 |
| GI-16 | P2 | Bonus/shapeshift bar appears: verify opening feedback and avoid repeats while the bar remains visible | Mounted `BonusActionBarFrame.lua` line 64, `igBonusBarOpen`; action-bar paging/state | Fixed bonus-page wire mapping; live cat/bear/travel and manual-page override verified; body-model defect tracked separately |
| GI-17 | P2 | Receive a whisper or raid warning: cue once, honor mute/diagnostic settings | Mounted `ChatFrame.lua` line 1473 (`TellMessage`) and `RaidWarning.lua` line 10; network/chat routes may use numeric kits | Live self-whisper first/rapid-repeat verified; mute/raid warning/quiet expiry pending |
| GI-18 | P2 | Loot, money and quest rewards: feedback corresponds to acquisition without replay after refresh/control switch | AcquisitionSound checks; `Panels/GameLoop.Loot.cs`, `ObserveMoneySound`, quest-added/completion routes | Covered in source; live replay checks pending |
| GI-19 | P2 | Item right-click equip/use, vendor pickup and buyback: material cue at the correct acceptance point | ItemGroupSounds includes Use; these bypass ordinary cursor placement and are deliberately not claimed covered by GI-02/03 | Suspected; native timing/reference review needed |
| GI-20 | P2 | Hover/leave/disable/toggle: highlight clears, disabled texture agrees with gate, checked state reflects authoritative action | 891 generated API/visual sites include inherited XML hooks; inspect mapped panel law and renderer | Suspected; review one panel family at a time |
| GI-21 | P2 | Escape with several panels/cursors open: unwind in intended order and play only the closing panel's cue | `ClearCarriedItemOnEscape`, panel ownership and binding handlers; original UI scripts provide reference, MSUI design may differ | Suspected |
| GI-22 | P3 | PvP state/queue transitions: intended cue only on the transition | Mounted PlayerFrame/BattlefieldFrame scripts: `igPVPUpdate`, `PVPENTERQUEUE`, `PVPTHROUGHQUEUE` | Live clean PvP flag activation and single cue verified; queue and possessed routing pending |
| GI-23 | P1 | Character roster rows must not overlap Create New Character | Live greg roster, nine rows, 2560x1369 client area: center click selected row nine; lower edge opened creation. `DrawCharacterSelect` uses fixed 60-unit pitch without reserving the create button | Fixed and live GUI verified: first/ninth row, creation, ten-row frame reviewed |
| GI-24 | P2 | Companion summon status must stop saying Summoning after authoritative arrival | Screenshot showed completed rows with stale Summoning footer. `ApplyCompanionList` now settles the last accepted summon's status only when its matching row becomes a companion | Fixed and live verified by `companion assert-status-summoned Gondolfo` and framebuffer capture |
| GI-25 | P2 | NPC quest rewards must include the driven body's max-level money bonus | Live quest 7 displayed 25 copper but awarded 115. NPC detail/offer now uses queried bonus and ControlledGuid level; base amount is not scaled twice | Fixed; level-60 quest 5261 shows and pays 60 copper, level-1 quest 7 still shows 25; hidden-reward and possessed variants pending |
| GI-26 | P1 | Questgiver objects must accept quests and range from the driven body | Wanted poster47/quest180 opened but failed NPC-only descriptor gate | Fixed; real main quest objective/reward and possessed Accept/abandon/range refusal verified |
| GI-27 | P2 | Item/object quest givers use a book portrait | Mounted QuestFrame.lua62–68; object incorrectly used generic monster portrait | Fixed; possessed poster book reviewed; item variant pending |
| GI-28 | P2 | `/macro` and `/m` open the Macro Book | Mounted ChatFrame.lua and GlobalStrings; aliases missing from client dispatcher | Fixed; both live commands and resulting frames verified |
| GI-29 | P1 | Feign Death holds the fallen pose while health remains positive, cancels to standing | Hunter5384 server aura applied while local character stayed upright | Fixed; live held animation1/cancel0 and actual death/repop/reclaim verified; remote variants pending |
| GI-30 | P1 | Shapeshift uses the server replacement body and its movement animation | Cat, bear, travel and Ghost Wolf had display changes on the original character rig | Fixed; live transformed model/movement/cancel assertions and reviewed cat/bear/travel frames; clearer wolf frame and remaining variants pending |
| GI-31 | P1 | Ordinary world devices must not open diagnostic widgets or silently consume Escape | Deadmines cannon opened raw ImGui World Object window | Removed fallback; live cannon/menu and readable poster open/Escape cues verified |
| GI-32 | P2 | Readable title and page labels remain gold on the dark shell | Live Keeshan poster title was black; mounted ItemTextFrame.xml inherits GameFontNormal and Lua only recolors the body | Fixed; both builds/scoped checks pass and rebuilt live gold title reviewed at19:13 |
| GI-33 | P1 | A fishing cast's bobber remains usable at the authored distance | Live cast placed bobber10.847yards away; client refused limit6 while Core type17 permits100 | Fixed; live13.175yard bite use, fishing loot, item6291 receipt and skill gains verified; Command View live variant pending |
| GI-34 | P1 | Fishing holds the pole and draws its line, then restores the normal sheath | Live channel7620 played134 with sheath0 and zero pole endpoints; pole visibly stayed on back | Fixed; live pole/line reviewed, poleTips1 during channel and0 afterward; remote variants pending |
| GI-35 | P2 | Verify the apparent missing lake surface | Gray Crystal Lake surface initially mistaken for lake bed | Missing-water diagnosis DISPROVEN by same-camera on/off/restored comparison; water is drawn. No appearance change made |
| GI-36 | P1 | Bandage animation ends on channel completion or movement interruption | First Aid746 cast and channel both use animation123; the looping cast action outlived the channel hold | Fixed matching cast-to-channel handoff in local and streamed renderers; main live completion and movement cancellation return to standing0; streamed variant pending |
| GI-37 | P2 | Missing reagents and tools identify the item by name | Campfire818 showed numeric4470/4471 in local cast errors | Item template names now used with readable uncached fallback; both builds/UIErrorsFrame PASS; live Stringy Wolf Meat and Skinning Knife messages reviewed |
| GI-38 | P1 | Switching bodies resets mailbox state and fetches the new body's inbox | Possessed warrior inherited main's empty inbox and60-second throttle | Added ResetMail to both control acknowledgements; both builds/possession checks PASS; live closure and immediate requery verified both directions |
| GI-39 | P1 | Server inbox and mail operations belong to the driven body | MailHandler.cpp HandleGetMailList declares GetSuiActor but iterates GetMasterPlayer mail | CONFIRMED server defect: normal warrior receives1866; mage session possessing warrior seeszero, warrior session possessing mage sees1866. Core correction required; no server edits/deployment |
| GI-40 | P2 | Instance entry updates the area label and soundscape identity | Stockades WMO area ID0 retained Stormwind City; actual map34 has sole area717 | Map-area fallback and duplicate WMO zone/subzone guard built; live entry/return verified and early212246/212247 captures show one dungeon name |
| GI-41 | P1 | Unit-targeted consumables send the selected target | Voodoo Charm8149 failed on Rageclaw's corpse because CMSG_USE_ITEM always sent mask0 | Added resolved unit GUID and packed target body; both builds/regressions PASS; living target refused, corpse cast10617 accepted and quest2561 credit62.529; reconnect turn-in paid660c and supplied choice item |
| GI-42 | P1 | Open equipment while Enchanting is open, retaining both panels for item targeting | Normal opener7411 then C refused because the coordinator accepted only Character/Spellbook replacement shapes | Added registered-left transition using existing ownership law and native callbacks; both builds and UiPanelObserver/client+Core possession checks PASS; live214502 side-by-side Character/Enchanting reviewed |
| GI-43 | P1 | Clicking a profession recipe changes selection and enables its action | Whole-list invisible button captured input before individual rows; actual recipe clicks did nothing | Replaced list button with passive wheel-region hit testing; both builds/ProfessionFrame PASS; actual Craft7418/7428 and TradeSkill3915 selections and crafts verified |
| GI-44 | P2 | Mail confirmations display the amount before accepting COD, sending money, or deleting money | Actual COD1868 popup said accepting would cost but omitted the10c amount; mounted StaticPopup.lua explicitly updates its MoneyFrame | Restored native coin display; both builds/scoped checks PASS. COD10c accepted/payment collected; separate send12345c succeeded129.823 after interrupted first attempt. Returned1871 delete warning amount reviewed000748 and cancelled, funds collected. GI48 fixes text wrapping |
| GI-45 | P2 | Two-digit silver/copper values remain fully readable in mail entry fields | Native send12345c screenshot224014 clips the second digits of23/45; generic border inset plus ImGui padding consumes the30px fields | Zero authored text insets/padding for mail controls; both builds/MailFrame/ImGui/client possession PASS. Reviewed224946 displays1/23/45 fully; actual send12345c accepted129.823 and purse29347->16972 includes30c postage |
| GI-46 | P1 | Ground-target consumables open an aim cursor and send the chosen location | Actual right-click on crafted4358 immediately sent mask0 and server4054 BAD_TARGETS965.607, count2 unchanged | Both builds/scoped checks PASS. Native arm/Escape, same-stack move/current slot commit, SuperUI possession clear verified. Actual world-pointer GO68.757 consumes1; Freeze47-unit snapshot clears intent, Resume preserves2. Command View click defect separately fixedGI49 |
| GI-47 | P1 | Pure ground casts retain destination projectile/arrival sound and authored GO burst | 4054 empty-hit GO consumes item but local visual handler only iterates unit hit/miss rows | Both builds and scoped checks PASS. Live4054 fixed-point movement/arrival sound38 verified; foliage-obscured full flight frame remains a visual limit. Actual worldpointer2120 GO157.616 spawns burst before area loop; burst/burn and later clear frames reviewed. No blanket spell visual pass |
| GI-48 | P2 | Destructive mail warning remains fully readable above its amount/buttons | Returned12345c letter1871 DeleteMoney warning clips right edge in235654 capture | Fixed wrapping and content-based height with native320/420 widths and GameFontHighlight. Both builds11warnings0errors; MailFrame/UiPanelObserver/ImGui PASS. Reviewed000748 complete delete warning/amount; Cancel preserved funds, collected12345c and deleted emptied letter. Reviewed001648 complete send warning; cancelled repeat. Other locales/scales untested |
| GI-49 | P2 | Armed ground items own world clicks in Command View | Live rogue4358 right-click leaves4054 rune/intent active; free-view routing intercepts the gesture | Live cursor precedes RTS orders. Both builds and client/Core possession/freeze/item checks PASS. Reviewed003044 actual cancel preserves2; actual left throw GO149.364 consumes1. Possessed variant still untested |
| GI-50 | P2 | Ground aiming shows native casting cursor and red footprint outside range | Mage2120 world-click OUT_OF_RANGE while marker remained green; local Benilla reference confirms native range/decal rules | Actor-relative hover range, native textures/default/cap/slab/first2 radii. Both builds/radius checks PASS. Rogue reviewed003043 green Cast,003135 small red marker; actual click OUT_OF_RANGE211.133 preserves item. Other terrain/actor/radius variants open |
| GI-51 | P2 | Item cooldown gate agrees with its authored category and visible swipe | Successful4054 then repeat342.519 sent despite item24/60000ms; serverNOT_READY342.602; gate queried DBC category0 | Shared item-category gate for bag/shelf, actor-owned bag timers/swipe. Both builds/scoped checks/client+Core law PASS. Live actual repeat35.974 blocked locally, no cursor/wire,1 preserved; expiry and immediate rearm after range refusal pass. Shelf/inspected-body live variants open |
| GI-52 | P2 | Character name editor displays exactly the filtered name that Accept will submit | Live focused name N1@ remains visible while internal filtered N leaves Accept disabled; post-draw byte-buffer cleanup does not update ImGui's active editor | Insertion-time CallbackCharFilter and12-character buffer. Both builds/CharCreate/CharSelectCurrent/ImGui PASS. Reviewed005521 filters N1@ to N;005607 caps at12; actual Nbwlkgnome creation0x2E, female Gnome Warlock1 GUIDA2, matching roster/world appearance. Fresh login011742 retains appearance, bars and quest inventory |
| GI-53 | P2 | Quest-item progress notification agrees with the updated quest tracker | Native first wolf loot010944 shows center0/8 while tracker1/8; ADD_ITEM arrives before inventory descriptors | Both builds and scoped checks PASS. Updated descriptor observer: reviewed0119003/8,0120094/8 and0136058/8 agree with tracker; unrelated loot and fresh-login restoration silent. Other actor/item variants remain open |
| GI-54 | P2 | Internal class/racial skill changes remain silent when leveling | Native level2 screenshot012757 prints GENERIC(DND), Demonology, Destruction and Gnome racial increases | Added admitting SkillRaceClassInfo 0x402 gate; mounted-data checks retain weapon/Defense/profession notices. Natural level3 live031718/031719 has no internal skill spam; actual Defense7 through10 notices retained earlier in this build |
| GI-55 | P2 | Level-up chat reports level, health/mana, talent point and positive stat gains in native order | Level2 packet626.858 parsed +15HP/+38mana/stats but only an invented green center message appeared | Restored mounted ChatFrame.lua PLAYER_LEVEL_UP chat sequence via LevelUpChatLaw. Natural level3 packet3129.133 and reviewed031718/031719 show +15HP/+24mana, Strength/Agility/Intellect/Spirit +1 in order, no zero Stamina/talent line, native gold effect. Non-mana and talent-threshold checks remain offline evidence |
| GI-56 | P2 | Quest reward item names remain inside their authored name boxes | Sten179 reward014156 Rabbit/Wolf/Boar Handler Gloves overflow adjacent boxes | Restored mounted QuestFrameTemplates.xml90x36 GameFontHighlight box, wrapping/height fit and vertical centering. Debug/Release and QuestLog checks PASS; reviewed015428/015905 names contained, long first name ellipsized with full tooltip |
| GI-57 | P1 | Visible first-column quest rewards can be hovered and selected without offscreen click capture | Actual Rabbit Handler reward hover/click014259/014411 gives neither tooltip nor selection; full-containment gate rejects native-3px grid inset | Intersect item hit region with scroll clip; pure tests cover left inset, partial bottom and wholly hidden row. Debug/Release/QuestLog/client+Core possession/ImGui PASS. Live015904 hover/015905 selection enable Complete; turn-in330.796 receives1079850, consumes8meat, grants80XP. Shared spell-row footer regression live033943: pointer91,602 below content clip shows Complete hover, no tooltip; click186.148 completes1599. Other item layouts remain separate |
| GI-58 | P2 | Readable item pages expand paragraph and character tokens | Native right-click9577/page2450 screenshot020250 shows literal $B$B and $n in Tainted Memorandum | Reader now expands the shared quest macros before body layout, matching reference feed_item_text. Paragraph/name/class/gender and renderer-seam checks, both builds, ItemTextFrame/ImGui/client+Core possession PASS. Reviewed020724 native right-click: paragraphs and Nbwlkgnome resolved, entire page contained. Other materials, multi-page and possessed identity remain separate cases |
| GI-59 | P2 | NPC quest and quest-log detail scrollbar thumbs respond to dragging | Beginnings1599 drag from239 to520 leaves text unchanged021901, arrow click scrolls021945; both quest renderers only draw their thumbs | Added native track hit handlers and clamped pointer-to-scroll mapping for both panels. Mounted UIPanelTemplates.xml defines the scrollbar as Slider. Both builds and QuestLog/ImGui/client+Core possession checks PASS. Reviewed native NPC bottom022525/top022527 and quest-log bottom022725/top022726; text and thumb move together |
| GI-60 | P1 | Followers hold when the driven body ports, as required by supplied standing rule3 | SuperUI Nbprihuman automatically followed a258yd GM port at03:19:50; server explicitly logs party-catchup and frame031951 shows priest in cave before explicit .namego | Confirmed Core/standing-rule mismatch. Existing detailed POSSESS_LAW4.3/7.2 still instructs port following. Candidate tools/core-patches/gi60-hold-after-port.patch applies in read-only dry-run; not compiled, installed or live-tested. Detailed law/check reconciliation and owner rollout remain pending |
| GI-61 | P2 | NPC quest details and reward panels show the spell being taught | Beginnings1599 reward032437 omits Summon Imp688 and the entire reward section despite parsed RewardSpell | Added spell-aware reward visibility, native learn label and non-selectable spell row/tooltip between choices and fixed items. Both builds10warnings0errors; QuestLog/ImGui/client+Core possession PASS. Live reward033727 and partial/scrolled tooltip033832/033834 reviewed. Row click does not complete; actual footer click186.148 completes1599, consumes3charms, grants355XP and teaches688 (book034114). Earth1518 NPC Details041314 and mixed Reward041612 now reviewed; spell8071/item5175 tooltips041726 correct. Sacred Cloth6032 Details044123/Reward044423 use trade-skill label; tooltips reviewed, turn-in515.722 teaches19435 and pays33900c. Recipe appears044501; deeper tooltip content parity remains unverified |
| GI-62 | P2 | Indestructible items refuse destruction before offering a confirmation | Earth Totem5175 flags0x20 still offered Yes/No and sent destroy at1374.240; Core refused24 and preserved item | Added template-flag gate before popup and send, native ERR_DROP_BOUND_ITEM. Debug/Release10warnings0errors; DeleteItem/QuestLog/ImGui/client+Core possession PASS. Live043205 immediate error/no popup, totem1 and empty cursor, no destroy send. Ordinary Ruined Pelt Cancel preserved5; Yes143.142 removed5. PASS for these actual gestures |
| GI-63 | P1 | Equipping unbound BoE gear asks before binding | Native crafted Improved Mooncloth Boots1099092 right-click044955 equips immediately; no confirmation exists on auto-equip or swaps | Added shared auto-equip gate and bidirectional equipment-swap gate with actor/item identity retention and vanilla Okay/Cancel popup. Both builds10warnings0errors; EquipBinding/ObserverBody/possess/ImGui/DeleteItem and Corelaw PASS. Live autoequip Cancel/Escape/Okay, bound bypass, forward/reverse click swaps, moved-item invalidation and SuperUI possession cleanup PASS; run0failures. Drag-release behavior remains a separate lead |
| GI-64 | P2 | Possessed character sheet shows real attack speed | Mage snapshot051216 shows attack1153958 and ranged1157235 seconds | Core SuiPossess1354 sends raw stored float bits; normal Object.cpp752 converts to uint milliseconds. Client normalizes only the three snapshot clocks. Both builds/scoped checks PASS; live mage052644 and repeated possession052722 show 1.60 melee/2.00 ranged, own priest1.90/2.00. Snapshot-time-fixed runner0 failures |
| GI-65 | P1 | Inventory drag releases into its target without an extra click | Native equipment and bag presses lose active ID when empty action-bar input hosts appear; payload remains absent | NoFocusOnAppearing preserves the source. Live 053525 unequip and 053607 equip PASS; reverse BoE drag Cancel/Okay 053741/45 PASS. Default ImGui preview/target rectangle removed and reviewed054341; final runner054242 zero failures, bag swap/held Escape/closed-bag drag1->2->1 PASS |
| GI-66 | P1 | A held inventory drag cannot acquire the next possessed body's item | Native held priest boots drag became mage boots GUID0686 after possession 053754; cursor assertion FAIL | Retain pressed actor/container/slot/item and clear press with cursor. Both inventory and bag-bar source gates require the retained press. Both builds/scoped client and Core checks PASS. Live held possession054351 and release054352 cursor0, original boots retained; runner054242 zero failures |
| GI-67 | P3 | Unrestricted items omit redundant class/race lists | Taming Rod15911 mask2047 shows all nine playable classes, unnecessarily widening tooltip055857 | Reference tooltip_item/render.rs429 masks to playable class bits. Client compared the raw mask for equality; now masks reserved bits. Both builds/scoped checks PASS. Live rod15908 tooltip062732 omits class list; restricted16850 tooltip063321 retains Classes: Hunter. Fixture removed afterward |

| GI-68 | P2 | Temporary charmed pets offer a Dismiss context menu | Native snow-leopard portrait/name right-click062141/062228 opens nothing; SUMMONEDBY-only gate excludes CHARMEDBY | Acting-body charm/summon ownership admitted. Both builds/scoped checks/Corelaw PASS. Live062828 Dismiss/Cancel, actual dismiss133.810 removes charm19597 and062927 shows hostile bear with no pet portrait/bar. Possessed-pet menu remains separate live case |
| GI-69 | P2 | Rapid combat notices remain readable | Live061838 overlays Defense and Dazed notices on the same screen area | Vertical stacking built and scoped checks PASS. Reviewed065920 separates Dazed/Aspect messages, but damage numbers still overlap at the upper burst boundary. OPEN: investigate the authored offset reset and message lifetime interaction before claiming readability PASS |

| GI-70 | P2 | Hidden auras remain absent from floating combat text | Defensive State5302 and Defensive State224948 leak in061838 despite mounted hidden=True and buff icons being filtered | HiddenClientSide gate, both builds and scoped checks PASS. Live5302/24948 apply418.757; reviewed065920 retains Dazed/Aspect and omits hidden names, later065925/070413 retain ordinary damage. PASS for these hidden aura cases |
| GI-71 | P2 | Hunter pet frame displays happiness and its tooltip | Permanent boar frame063529/063641 has no happiness icon; DrawPetFrame has no happiness branch | Both builds/PetMenu/ImGui PASS. Reviewed071107 Unhappy75%/Losing Loyalty,071247 Content100%/Gaining Loyalty,071311 Happy125%/Gaining Loyalty. Actual three food117 feeds consume3 and raw happiness rises through thresholds to1041250. PASS UI/feeding sequence; damage multipliers not empirically measured. Protocol lacks personality selector, mounted fallback row1 used |

| GI-72 | P2 | Pet rename updates the frame and name cache immediately | Native Copper rename removes Rename option but frame stays Boar064056; stable shows Copper064412 | Versioned name refresh on PET_NAME_TIMESTAMP140 built, PetName checks PASS. Actual native rename confirmation071411 then reviewed071445 frame Bristle without reconnect; diagnostic timestamp0->1788866084/nameBristle. PASS same-session rename; stable retrieval retains name |
| GI-73 | P2 | Stable controls stay inside the window at gameplay scale | Stable button clips at right064412/064527 | Logical-width correction built. Reviewed065315 row highlight and065414 complete Stable button remain inside border. PASS at tested scale |
| GI-74 | P2 | Stable closes outside the acting body interaction range | Native backward walk to13.617yd leaves stable open064527 | Both builds/client+Core law PASS. Native backward walk closes at125.953; reviewed065418 closed window and065506 distant request shows You are too far away. PASS own-body case; possessed/delayed-reply variants pending |
| GI-75 | P2 | Stable slot purchase displays its cost and respects affordability/cap | Native stable064412 has no price and always enables Buy Slot | Both builds/scoped checks/client+Core law PASS. Reviewed070932 red5gold/disabled, native disabled click071021 no purchase, added50000c fixture then071022 enabled/white price. Actual purchase97.2500x0A;071039 slots0/2 hides buy/cost; final purse1270c. PASS second-slot affordability/purchase/cap; first-price mounted-data evidence |

| GI-76 | P1 | Beast Training opens its native craft panel | Actual5149 reports LOCAL_ProfessionWindow but reviewed072810 shows no panel; skill261 is absent from craft whitelist | Both builds and ProfessionFrame/ImGui PASS. Reviewed073302 panel opens with Growl ranks1/2; actual Create rank1 sends1853 and GO97.379 teaches pet2649. PASS opener/learning; training UI defects trackedGI77 |
| GI-77 | P2 | Beast Training shows pet requirements, costs and Train availability | Reviewed073302 has false skill0/0, Create, empty Reagents; Rank2 sends14922 and LOWLEVEL100.180 because no pet-level requirement appears | PASS own-body level/known/points, native Train, paid learning and upgrade: reviewed075855 zero-total footer hidden;075912 Bite1 learned,7->6 points;075959 Bite2 learned for3 incremental points,7->4. Signed Core training-point encoding corrected. Both builds and PetPaperDoll/ProfessionFrame/PetMenu/PossessLaw/ImGui plus Core law PASS. Possessed variants pending; active-ability cap remains server-authoritative |

| GI-78 | P1 | Possessed hunter receives pet training points and owner stats | Reviewed080208 displays0 points after own-body4; pet descriptor raw0 while loyalty2/level8 and learned Bite2 persist | OPEN Core visibility gap: Object::GetUpdateFieldFlagsForTarget grants UF_FLAG_OWNER_ONLY only to actual owner/charmer; UNIT_TRAINING_POINTS is owner-only. Possessed pet health also arrives normalized100. No Core deployment; direct pet-info command failed syntax/selection, so exact concurrent server value not independently read |
| GI-79 | P2 | Beast Training describes Bite's effect | Reviewed075959 empty detail body; mounted teacher17262 description blank but taught17255 contains damage text | Taught-ability description fallback implemented with rank-correct substitution. Debug/Release and mounted PetMenu check PASS; reviewed081042 Bite2 displays16 to18 damage and saved4 points. PASS tested rank |
| GI-80 | P1 | Hunter stable services remain available when a mage controls the hunter | Reviewed080423 Erma selected but That creature is not a stablemaster; no list request sent | OPEN Core Object.cpp714-717 removes stable bit using target session class, not controlled hunter. Range test could not run; GM go moved mage, hunter remained14.15yd away. No stable/range pass claimed for these captures |
| GI-81 | P2 | Aquatic Form plays swimming locomotion | Reviewed084654 moving underwater seal retained animation0; CreatureRenderer had no swimming selector | Added flag-driven swim idle/forward/back/strafe/turn selection with mounted seal-model regression. Debug/Release10warnings0errors; BodyDisplay/PossessLaw/MountRendering/TacticalFreeze PASS. Live60372 swim flag confirmed; reviewed085124 temporal forward frames animate flippers with42, stopped41; backward45/strafe43 logged. Land transition and additional variants pending |
| GI-82 | P2 | Prowl and stealth use crouched idle and creeping locomotion | Live9913 GO202.575 and aura apply202.643; translucent cat retains idle0 and running5 in085432/085433. Renderers never consume existing UnitIsStealthed pose flag | IMPLEMENTED, UNVERIFIED at owner pause. Creature/streamed-player/local-character selectors now use120idle/119creep, retain backward precedence and authored1x creep rate; local UnitState receives actor stealth flag. Both client trays10warnings0errors. Expanded BodyDisplay regression does not compile: CS0117 WorldEntity.MoveFlags at line90. No post-change live run; test repair and retest remain pending |

## First batch behavior and limits

Inventory cues join the cached item template's display id to the material group and
its pickup/put-down kit. Unknown templates/displays or authored zero kits stay silent;
there is no generic fallback and no late cue on a future query response. A confirmed
stack split uses the same pickup path. These UI gestures obey sound/master/effects
volume, the expanded-audio kill switch and `MSUI_UI_AUDIO_OFF`.

Put-down audio is **cursor dispatch feedback**, not a promise that the server accepted
an inventory mutation. It plays after the network send succeeds. A later server
refusal still uses the existing inventory error path; a snapshot does not replay the
put-down cue. Right-click auto-equip, purchases and other separate routes are GI-19.

Pet cues use the authored ability-icon pair (837/838), at 0.75 catalog volume, through
the ordinary one-shot UI audio path. Assignment sound occurs below `TryAssign`; the
existing possession, frozen actor, passive and token rules remain authoritative.

The bar swap correction preserves `PlaceActionPayload`'s displaced cursor. Dropping
off the bars still clears it. This needs a live A -> B -> empty-slot reproduction;
the existing pure placement check alone could not catch the old later cursor clear.

## Verification record — 2026-09-07

Debug and Release solution builds pass, each with 11 warnings on existing code.
All eleven local checks pass:

- `tools/interface-wire-check`: `--acquisition-sound-only`, `--stack-split-only`,
  `--inventory-failure-only`, `--pet-spellbook-only`, `--pet-action-bar-only`,
  `--multi-bars-only`, `--spellbook-visual-only`, `--possess-law-only`,
  `--imgui-policy-only`, and `--shared-docs-only`.
- `dotnet run --project tools/inventory-parity-check --no-build`.

The acquisition check now exercises the display -> material -> gesture join, including
different pickup/put-down ids, explicit silence, missing tables and an unknown display
that must not borrow group zero's sound. The existing pet checks cover passive/no-op
and token-relocation refusal cases; source review verifies the cue follows assignment.

Ten representative cues (pet pickup/drop plus bag, cloth/leather, paper and small metal
pairs) were independently extracted from their reported archives and decoded as non-silent
PCM WAVs. The 101 scanned kits all have readable, nonempty assets. Two runs with unchanged
inputs produced the same JSON hash. The final report has 45 cues with a literal C# match
and 56 without; many of the latter are intentionally data-driven item sounds.

No live client interaction or listening proof was collected in this round. Rows GI-02
through GI-07 are offline verified only; the live acceptance sheet remains pending.

Core source check baseline issue found before any server edit: `tools/possess-law-check.sh`
expects `NUM_MSG_TYPES = 874`; the development server's
`src/game/Server/Protocol/Opcodes_1_12_1.h` defines commander-raid messages at 874/875
and `NUM_MSG_TYPES = 876`. The check stops at that mismatch, so later Core assertions
have not been certified. This round does not modify Core source or its deployment.

## Live acceptance sheet

Use the owner's normal Release launch; record date, build and actor for each pass.

1. Pick up cloth/leather, paper, metal, and a bag. Hold each drag still for a second:
   one pickup sound, never a loop. Drop onto empty slots and swap occupied slots.
2. Drop on the original slot, cancel with Escape, attempt an invalid split destination,
   and exercise a server-refused move. Distinguish dispatch sound from rejection feedback.
3. Split a stack via OK and Enter; cancel via the button and Escape. Only accepting
   the split creates a carried item and pickup cue.
4. Drag a pet spell book -> bar, bar -> empty, bar -> occupied; try passive and same-action
   placement. Exercise tokens with/without a valid relocation home.
5. Pick up action A from a bar, drop on B, move the carried B into an empty slot.
   Verify both actions remain usable, then repeat across bottom bars and with a macro.
6. Repeat relevant inventory and pet cases on a controlled companion, then switch bodies
   mid-drag. Check read-only and frozen states do not produce an assignment cue.
7. Repeat with master/effects muted and diagnostic audio suppressed; restore preferences.

## Background live sweep — 2026-09-07

Owner authorized greg account gameplay and later explicitly granted all in-game
character actions. Owner requires SuperUI companions, not VMaNGOS partybots, and
background control so the PC remains usable. No Core deployment, restart, process
control or direct database edits were performed.

The native client now supports `--background` with live protocols: hidden rendering
at 30 FPS, internal actions, framebuffer PNG/JSON captures and protocol/audio logs.
The test settings and credentials live only in ignored local files. Gameplay captures
no longer replace the desktop clipboard in background mode. See `tools/live-run/README.md`.

Evidence is under `docs/current/live-gameplay-20260907/` and `dumps/` on this machine:

| Check | Actual evidence | Limit |
|---|---|---|
| Hidden execution | greg / Nbmaghuman authenticated and entered the live world; process had no visible main window; 1600x900 and 2560x1369 framebuffer captures rendered | Uses GPU/CPU; music startup still has its normal focus gate |
| Bag controls and cues | Production B binding opened/closed backpack; one inventory cue each; `actions-confirm/runner-20260907-113036.csv` has 55 PASS steps, zero failures | Sound journal proves dispatch, not subjective listening |
| Spell and macro pickup/drop | Kits 832/833 resolved to uSpellIconPickup.wav / uSpellIconDrop.wav; one cue while holding pickup for a second, one on accepted drop | Harness supplies destination slot; desktop pointer hit testing is not certified |
| Occupied bar swap | Fireball displaced Frost Armor onto the cursor, which placed it in the next slot; temporary slots restored to empty | Production pickup/release and state checked; drag geometry still pending |
| Macro cleanup | Blank character macro created with shipping handler, placed on bar, picked up, removed, then deleted by its recorded id | Did not execute a chat macro or change existing macro bodies |
| Crafting | Spell 2881 completed; `SMSG_ITEM_PUSH_RESULT` increased Light Leather (2318) by one | Used authorized GM reagent provisioning; this was not resource gathering |
| Trainer learning | Simon Tanner list decoded; profession confirmation accepted; service 2155 succeeded, spell 2108 added, skill became 1/75; 10 copper cost | Explicitly provisioned test money after full in-game permission |
| SuperUI companions | Summon results OK for Gondolfo and Nbpalhuman; live roster showed both present; possession changed actor to Gondolfo (0x92) | Outdoor summon prerequisite enforced by server |
| Possessed bags | Gondolfo's inventory and gold rendered; bag sound owner was 0x92, not main 0x223 | Item dragging on companions remains pending |
| Companion cleanup | Both dismissed with OK replies and offline roster assertions; revive commands issued after dungeon deaths | Two mistaken VMaNGOS partybots from an earlier run were absent after logout; subsequent tests used SuperUI companions only |
| Summon status fix | Footer now says Gondolfo summoned; live status assertion passed and screenshot confirmed it | Arrival toast may still be fading from the earlier accepted request |

Failed attempts are retained rather than counted as passes. The initial sound profile
was muted; all-events sound assertions also counted NPC footsteps until a category
assertion was added. Rapid same-kit tests were spaced by a second to avoid overlapping
voices. Trainer selection initially used an unsupported numeric flag instead of the
harness's symbolic `trainer` selector. Dungeon summon rejection matched the server's
non-instance requirement. Seated casting produced the correct server refusal. An
offset teleport went outside the dungeon floor and produced an evade; it is invalid
combat setup, not a certified combat regression. No full dungeon clear is claimed.

Debug and Release build successfully with 11 existing warnings. MacroBook,
MultiBars, AcquisitionSound, Companions and client PossessLaw checks pass. The Core
PossessLaw check still stops at the preexisting NUM_MSG_TYPES 874 vs 876 mismatch.
GI-02 through GI-06 and the remaining suspected rows retain their pending status.

### Deadmines follow-up, 11:56 local

`dungeon-pack/runner-20260907-115604.csv` records the final pack attempt. Normal
Arcane Explosion casts killed the selected Defias Miner; the death assertion and
corpse loot request passed. The server opened loot with 21 copper and two item slots,
cleared the money, and delivered one Wool Cloth (2592) to the player. Linen Cloth
remained in the loot window when inventory filled; the captured UI displayed
Inventory is full. This covers one live inventory-capacity refusal and partial loot
acquisition, not full-bag recovery or acquisition replay after possession.

This runner still has one failed companion attack step (no eligible live subject at
that moment), so the entire scenario is not marked passed. SuperUI companions were
used; Gondolfo's automatic combat and death were observed in the earlier passes.
Summon, possession, actor-owned inventory/audio, dismiss, and summon-status completion
have separate passing evidence. No boss fight or full dungeon clear was completed.

The main returned to Stormwind; Gondolfo was dismissed and the roster confirmed him
offline. In-game revive replies confirmed Gondolfo and Nbpalhuman were revived after
testing. Temporary bar entries and the blank test macro were removed. The authorized
character retains the learned profession, crafted/looted items, remaining test money,
and gameplay skill gains. The harness did not restore all prior character stats.
Final Debug/Release builds passed with 11 existing warnings each; gameplay ImGui and
shared-doc checks also passed. The ordinary visible client's default frame rates are
preserved; the 30 FPS cap is specific to background runs.

### Full-checklist continuation — 2026-09-07, afternoon

Evidence root: `docs/current/checklist-20260907/`. Historical failures above remain
historical; the results here supersede their current status only where specified.

- GI-08: quest-log state transitions now emit 844/845. `timed-expiry-confirm`
  verifies held L emits one open and production Escape emits one close. Other inputs pending.
- GI-09: target-frame visibility now follows mounted OnShow/OnHide semantics.
  Self shows 867 once, same target repeats silently, clear emits 900. Changing one
  already visible target to another does not replay OnShow. NPC variants pending.
- GI-13/GI-17: chat scroll cues and reference whisper quiet timer implemented.
  `inventory-feedback` and `split-confirm` verify first self-whisper TellMessage
  once and rapid repeat silent. Raid warning, mute and pointer scroll remain pending.
- GI-23: roster pitch reserves Create space and scales row text/highlight together.
  `roster-proxy` selected first and ninth lower edge, opened creation, created
  Nbwarhuman using actual GUI input, and verified ten rows. Reviewed framebuffer.
  A hidden input-context proxy supplies GUI pointer/buttons without using the desktop.
- GI-02/03: `inventory-feedback` moved bag 14155, leather 2318, cloth 2589,
  paper 2598 and metal 2901 through production pickup/place. Journals resolve to
  1183/1185/1191/1192 pickup and 1200/1202/1209/1213 placement; same-slot drops
  produce no putdown. These are handler tests, not pointer hit-testing proof.
- GI-04: `split-confirm/runner-20260907-135106.csv` has zero failures. Source
  bag open, two RightArrow presses then Enter, carried cloth and server destination
  nonempty, one 1185 pickup/1202 putdown, next split Escape has no pickup. Prior
  closed-bag attempt cancelled its dialog as designed and remains a setup failure.

All **179 clinical flags** passed together (`clinical/full-final-results.csv`).
Twenty-four initial check failures were investigated and stale fixtures/assertions
corrected; this is not a claim of 24 gameplay fixes. Two malformed-packet tests
previously caught their own failure throws and now truly reject accepted bad data.
The local Core checker now pins existing commander-raid opcodes 874/875 and count
876; read-only execution passes (`core-possess-law.log`). No Core source changed.
Debug/Release builds pass with 11 warnings; hashes are in `live-build-sha256.json`.
The live per-case and full-game backlog remains open.

### Pet and bonus-page defects — 2026-09-07, 14:10 local

`pet-gestures` crashed in DrawPetActionBar when picking up Firebolt 3110. Dragging
revealed blank grid slots whose icon array entries were null; their ring renderer
read `.Length`. Blank icons now normalize to an empty path. `pet-gestures-fixed`
completed all steps with zero failures and restored the original bar. It covers
book/bar pickup, same-action refusal, passive 18728 refusal, empty placement and
command-token relocation, with 837/838 cue assertions. Screenshot reviewed.
The reusable fixture is `tools/live-run/scenarios/pet-item-cursor-regression.txt`;
it requires a normal imp with its initial Firebolt bar. Possessed/frozen and
no-relocation-home variants remain pending. The initial crash logs are retained.

GI-16 traced a functional gap: ActionWireSlot ignored already-loaded
SpellShapeshiftForm.BonusActionBar. Main page one now maps to the authored extra
pages, with controlled-body ownership and explicit ordinary-page overrides.
One igBonusBarOpen cue occurs on showing the bonus page, not per redraw. Cat
768 verified wire slot72, page-two override12, return72, cue once/hold silence
in `form-pages`; bear fixture initially used replaced spell5487 and then failed
to cancel cat. These expected refusal/setup failures do not count as bear passes.
Follow-up uses learned Dire Bear9634 and cancels the current form first.

### Form pages and scope corrections — 2026-09-07, 14:12 local

`form-bear-travel/runner-20260907-141107.csv` has zero failures: cancel persisted
cat, cast learned Dire Bear9634, aura verified and first main button maps to96;
one bonus-open cue; cancel restores0. Travel783 verified with normal slot0 and
no bonus cue, then cancelled. Cat and manual page override have separate passing
assertions in `form-pages`. Wrong-form and unknown replaced-rank refusals remain
in the failed setup logs. StanceBar, MultiBars, gameplay ImGui and client/Core
PossessLaw checks pass after the changes; both trays build with11 warnings.

GI-14 source scope: trade/mail use inline denomination inputs. There is no
CoinPickupFrame popup lifecycle on those routes, so its open/OK/cancel sounds are
not applicable there. This does not certify money-input pointer/keyboard behavior.
GI-15's original bank-purchase sound lead remains disproven; the shipping bank
purchase button already uses igMainMenuOption. Bank-bag gestures still need live QA.

### Cursor ownership, captures and spell review — 2026-09-07, 15:12 local

Evidence root is `docs/current/checklist-20260907/`. `body-cursors-bank` has zero
failures: held main linen clears on possession; held Gondolfo wool clears on release;
a main split dialog cannot produce an item after possession/Enter; main spell cursor
clears on possession. Both control acknowledgements now clear carried item, split and
action cursor. POSSESS_LAW 2.3 and its clinical enforcement were updated. Client/Core
possession checks pass. Real bank transfer moved linen from bag4/12 to bank0 and back,
verified both server slots, and dispatched the material cues once each. The earlier
`ui-inventory-bank` reproduction is retained. Its invalid head-slot move left the
source item intact and both slots unlocked; actual GUI chat scroll buttons passed.

The class batch requested 50 representative abilities: 45 known attempts, 42 selected
spell GO events, three refusals and five absent spells. This is not 42 visual passes.
The Horde shaman's original Stormwind fixture was under guard attack and is invalid
for isolated presentation review. `shaman-safe` repeats at Rwag's Valley of Trials
spawn; both totems succeed after supplying Earth Totem5175. Priest recapture repeats
139/2050/2054/15473 after cancelling Shadowform. All 28 recaptured frames are valid;
24 original priest images were 1x1 from a zero-sized framebuffer. The capture helpers
now reject invalid dimensions and propagate sample failure instead of logging PASS.
Original files remain unchanged. Cause of the zero framebuffer is still open.

`hunter-feign-fixed/runner-20260907-145809.csv` has zero failures. Live 5384 aura,
positive health, ReadsDead, animation1 held through five seconds, aura cancellation
and return to animation0 all passed. Full-size fallen/rise/standing frames reviewed.
The original hunter stayed upright: the local renderer ignored ReadsDead entirely,
and streamed renderers considered health death alone. Presentation now honors the
dynamic dead bit and stand-state7, excludes ghosts, and prevents late casts/wounds
from overriding the corpse pose. Feign cancellation does not trigger creature respawn
fade. An intermediate immediate recast was rejected by the server; the successful
fixture waits35 seconds after cancellation. Remote and real-death variants remain.

`item-pvp-escort/runner-20260907-145912.csv`: quest337 started through actual item
2794 use, accepted, turned in to NPC1440, removed the book/log entry and paid2300 copper
(10415 -> 12715). Its item was GM provisioned; acquisition/drop is not certified.
The PvP fixture explicitly disabled the preference, waited330 seconds, verified the
visible flag cleared, enabled it and verified one igPVPUpdate/PVPWARNING.wav cue;
redraw and disabling the preference emitted none. Battleground queues remain pending.
A source-traced Core limitation remains: HandleTogglePvP uses GetPlayer rather than
GetSuiActor. No Core changes or possessed PvP mutation were performed.

The same run completed real escort945 using normal Arcane Explosion combat alongside
the moving NPC, with GM placement and temporary no-power-cost setup. Server objective
completion, reward completion, log removal and1840 copper (12715 -> 14555) are verified.
A redundant request-reward step after the Offer window opened failed; the turn-in
succeeded. Earlier NPC deaths, reset wait and mana exhaustion are retained failures.
The off command was sent before leaving; its response needs explicit cleanup retest.
Travel, pathfinding and level-appropriate combat balance are not certified by this
fixture. Both builds pass11 warnings; StandState, DeathFrame, ObserverBodyOwnership,
BodyDisplay, gameplay ImGui and client PossessLaw scoped checks pass as applicable.

Visual review also confirmed Ghost Wolf changed the server display to4613 while the
local avatar stayed an orc. Transformed-body world/portrait rendering is now wired
through the existing display-model renderer, with a separate predicted render pose,
no network-entity mutation, and no original player gear on replacement models.
Ghost Wolf/cat/bear/travel live model tests are currently pending. Their earlier bar
mapping passes do not certify that the body transformed. Build-specific hashes now
use unique names; `build-body-display-sha256.json` identifies this pending-live build.

### Wanted poster, pointer input and resume — 2026-09-07, 18:25 local

`quest-live-retry.stdout.log` records quest180 accepted via the actual Accept button,
Fangore killed with normal Arcane Explosion damage, paw3632 received from his corpse,
and Magistrate Solomon completing the quest. Server reward5620 copper at282.354,
purse14595 before reward, log empty at285.374. GM travel was fixture setup; fleeing
initially left the corpse out of range, correctly refused at8.73yards. Moving to the
corpse produced real loot40 copper and the paw. Deliberate quest query20.53yards
from the poster was refused by the fixed6yard gate. The user paused the client at
step116 before final artifact flush; stdout and framebuffer evidence are preserved.

The object-giver gate and its lifecycle now accept questgiver gameobjects using
controlled-body position. New client possession checks and read-only Core law pass.
Mounted QuestFrame.lua62–68 requires the book icon when no NPC unit exists. Object
and item quest givers now use it instead of a monster portrait. Unit target frames
also reject non-unit selections (normal object right-click already avoids selecting
the object; the diagnostic selector exposed this defensive omission). Debug and
Release builds pass,11warnings/0errors. Live portrait/possessed-giver retest underway
on `build-resume-object-portrait-sha256.json`.

`ui-live-retry` also passed actual pointer Fire Blast and blank macro drags into bar
slot3, exactly one832 pickup and one833 drop per gesture; slot3 restored empty and
test macro deleted. Quest-log close button, bottom Exit and micro-menu sound cues
passed, as did the visible100quest tooltip. Friendly guard selection867 and normal
GM-off hostile Hogger selection873 passed; same-target repeat silent. Earlier wrong
neutral expectations and the invalid `any` wildcard assertion are retained as
fixture failures. `/macro` and `/m` aliases were missing and now call OpenMacros.

Full scope remains open; individual results never certify all quests/spells/ranks.

### Live object portrait and death recovery — 2026-09-07,18:32

`resume-live` used the Release hash in `build-resume-object-portrait-sha256.json`.
The book icon is visually confirmed in `dumps/gameplay-checklist-possessed-object-portrait-retry-20260907-182703-282.png`; no invalid creature target frame remains.
Paladin-controlled Accept was sent at239.161; party-facts showed quest180 present,
and the subsequent actor-targeted abandon returnedok1/refused0. Moving the paladin
18.08yards away causedREFUSED_RANGE while the main remained near the poster.
Initial setup failures used a companion left behind in Stormwind, not a nearby
actor, and are retained. A3second dismiss assertion ran during unavailable state;
a later fresh list after8seconds confirmed offline. GM teleport commands move the
session main, as verified in Core TeleportCommands.cpp, so never assume otherwise.
The diagnostic `quest inspect-log` read the main even while possessing; it now
reads ControlledGuid's displayed log and party-facts projection.

Self-targeted authenticated `.die` established actual health0 (this is death/recovery
setup, not fall-damage proof). Fallen animation1 persisted and Release Spirit was
visible. Repop produced corpse0xF101000000000001,30second reclaim delay, ghost aura
and translucent blue character/Spirit Healer. Reclaim failed during delay, then
outside corpse range. At the actual corpse location it succeeded at344.517;
server unghost at344.714, health851, animation0. Reviewed full framebuffers before
release, at graveyard and after recovery. No durability-loss pass is inferred from
GM-induced death. Ghost-world color treatment remains a visual-reference lead.
All companions dismissed, main alive, run exited normally. Raw runner failures
include deliberate refusal tests and earlier setup mistakes, not a blanket pass.

### Dungeon encounters and readable UI — 2026-09-07,19:11 local

The hidden native harness observed actual deaths of Rhahk'Zor, Sneed's Shredder,
Sneed, Gilnid, Mr. Smite, Captain Greenskin, VanCleef and Cookie across sessions.
Rhahk'Zor/Sneed/Gilnid opened their corresponding doors. Powder5397 came from the
real chest; cannon16398 drove ironclad door16397 state1->2/animation150. Smite had
one observed6432 stomp and ten-second stun; later weapon phases remain unverified.
This is an encounter sweep using GM travel/resources, not one continuous dungeon
run or a balance pass. Initial Shredder footage was invalid: a5yard GM offset put
the actor outside the room/floor. Corrected grounded placement supported Sneed;
that initial visual phase still needs recapture. No Core control was performed.

`dungeon-final/runner-20260907-185852.csv` and matching verdicts record VanCleef and
Cookie deaths and server-confirmed loot:303 and80 copper, purse20581->20884->20964,
plus four item receipts. Redundant take-all commands failed after autoloot had
already acquired everything. Reviewed their final full framebuffers. External
network failures interrupted three sessions; the new interruption path preserved
partial CSVs on the last two instead of losing the run's observations.

Cannon interaction exposed a raw World Object ImGui popup. Removed that fallback;
readable item text remains, and hidden object context no longer consumes Escape.
Gameplay ImGui policy now includes GameLoop.GameObjects.cs. Both builds passed
11warnings/0errors; relevant UI and client/Core possession checks passed. The
19:01 cannon/menu captures verify the fix. `exploration` then opened real poster
1726/page245 and Escape-closed it with exactly one850/851 item-text cue each.
The title was visibly black on the dark header; mounted ItemTextFrame.xml118 and
Fonts.xml70-74 establish gold. Title correction is built Debug; Release/live pending.

`exploration` restored no-power-cost OFF at30.588 and HP1640/1640 at32.706 with
server acknowledgments. Accepted quest62, placed outside trigger197, observed
incomplete state, then normal forward movement entered it at152.553 and produced
server objective COMPLETE. Reward/chain checks are underway. Full scope stays open.

### Exploration chain and title retest — 2026-09-07,19:21 local

`exploration-retry` visually confirms the readable poster gold title in
`dumps/gameplay-checklist-readable-poster-gold-title-20260907-191329-571.png`.
Debug/Release:11warnings,0errors; ItemTextFrame and GameplayImguiPolicy PASS.
Build identity: `build-item-title-sha256.json`. Quest62 turn-in displayed/paid395c;
purse20964->21359. Follow-up76 accepted normally. An arbitrary outside-trigger
coordinate had no floor and was discarded. At the known miner spawn(-9087.93,
-589.59,58.3875), normal walking crossed trigger87 at219.188 and server completed
quest76. Its reward displayed/paid860c, purse21359->22219. No GM quest credit was
used. Fargodeep/Jasperlode now have real exploration and reward evidence; travel
setup does not certify a complete overland journey.

Lee Brown's actual Fishing7733 purchase succeeded and learned7620/7738; the
Journeyman row visibly lists skill50/Apprentice prerequisites and is disabled.
The diagnostic vendor command incorrectly parsed its3-part split as4arguments;
fixed with a local full split, both builds11warnings/0errors. No production vendor
bug is inferred from that parser failure. Server disconnect at19:19 preserved
partial artifacts. The subsequent `fishing` run is testing the real pole purchase
and bobber path. Full scope remains open.

### Fishing interaction, attachment and lake investigation — 2026-09-07 evening

`fishing` bought6256 from vendor66 through the fixed diagnostic parser, received
it in bag19/slot6 and equipped it in mainhand15. Cast7620 created a bobber10.847yd
away; UseGameObject refused its generic6yd limit. Core GameObjectDefines.h760-780
permits100yd for type17; the existing cursor already used100yd. Direct use and
Command View now share GameObjectUseDistance. Both builds11warnings/0errors;
client/Core PossessLaw, GameObjectCast, FishingLine, ImGui and shared-docs PASS.

`fishing-retry` on `build-fishing-range-sha256.json` observed waiting1->active0,
accepted use13.175yd away, received fishing lootType3 and skill1->2->3. Later
TakeAll produced actual item6291 receipts, including156.223/bag19slot8. The first
fish was only displayed, not taken before the next cast; do not count display
as acquisition. FishBite/FishReelIn resolved and played in the audio journal.
An earlier screenshot had the game menu over it due an unnecessary Escape;
subsequent unobscured captures supersede it for visual review.

Those captures show a stowed pole and no line. `water-pose` reports sheath0,
visualSheath0, channel7620/owned bobber, poleTips0 while animation134 runs.
FishingLineLaw now overrides attachment sheath during133/134 only, preserving
persistent server/user state. Local and streamed renderers use it; cancellation
restores normal sheath. Both builds/FishingLine pass, live retest is `liquid-gpu`.

Crystal Lake also lacks a visible surface: settings Enabled=true, runtime has
surface57.631035/type4 over terrain55.843792,9tiles/4drawn/12740triangles,
30watertexture frames and river alphas0.65/0.5. This is not an unloaded-asset or
settings-off conclusion. `MSUI_LIQUID_PROBE=1` now records GPU errors/state around
the liquid pass every5seconds; normally disabled. No liquid rendering fix yet.

### Fishing visuals passed; water diagnosis corrected — 2026-09-07,19:47 local

`liquid-gpu` shows the rebuilt pole in hand and a visible line in
`gameplay-checklist-fishing-held-pole-retest-20260907-194151-195.png`.
During7620 channel the diagnostic reports poleTips1; afterward channel0/poleTips0
and the normal stowed pose returns. Both builds11warnings/0errors; FishingLine
PASS. The override only affects rendered attachments, not the server sheath byte.
Remote/possessed variants remain pending. The session later disconnected; partial
artifacts were preserved.

The earlier missing-water diagnosis is DISPROVEN. In `liquid-ab`, same-camera
water-visible-a194543 versus water-hidden-b194545 shows the gray textured surface
removed to expose the brown lake bed. Water was restored afterward. GPU draws
reported NoError, with no stencil/scissor/raster-discard gate. Unreferenced
sentinel-height bounds were noticed but were not established as a visible defect.
No water appearance/geometry correction was made; only opt-in diagnostics exist.
Keep this correction alongside the earlier observations rather than treating the
initial visual impression as a confirmed bug.

### Cooking, First Aid and channel handoff — 2026-09-07,20:01 local

`liquid-ab` bought Cooking2551 from Tomas1430 and First Aid3279 from
Thamner Pol2326; both learned the profession and initial recipes. Real craft2538
consumed provisioned wolf meat2672 and produced Charred Wolf Meat2679, skill1->2.
Real craft3275 consumed existing linen2589 and produced bandage1251, skill1->2->3.
The reagent provision was a GM fixture, not gathering evidence. Eating the cooked
food sits the character; normal movement removes the eating state.

With self health deliberately set1000/1640, First Aid746 channeled six seconds
and delivered six11-point heals; natural regeneration is separate. Recently
Bandaged11196 blocked a repeat with SPELL_FAILED_TARGET_AURASTATE. A defect left
animation123 looping after the channel ended: both the cast action and channel
hold used123, and only the hold was cleared. Both renderers now retire the matching
cast action when its channel hold takes over, preserving nonmatching release
animations. Debug/Release11warnings/0errors; TacticalFreeze, StandState and
BodyDisplay checks PASS. Build identity `build-channel-loop-sha256.json`.

`channel-loop` live asserts123 during healing and0 afterward; reviewed standing
frame195512. After the debuff expired normally, movement stopped a second bandage
at115.717; standing0 assertion and reviewed frame195618 passed. Health cleanup
1640/1640 acknowledged118.583. Streamed and possessed variants remain open.

### Skinning, campfire requirements and primary cap — 2026-09-07,20:08 local

`channel-loop` retained failed setup attempts: Young Wolf299 is neutral, so the
hostile-only fight observer refused; Fireball133 was rejected for facing; normal
Arcane Explosion10202 killed it, but the corpse correctly returned UNSKINNABLE.
Prowler118 has skinning_loot_id118 in the live template. Actual AE death, ordinary
corpse loot, two-second8613 cast, skin lootType2 and TakeAll delivered Ruined
Leather Scraps2934 at533.553. Reviewed loot frame200313. No GM kill/loot credit.

Cooking initially lacked both reagent and fire; that refusal does not isolate the
fire requirement. With meat2672 present, craft2538 refused MissingSpellFocus and
showed Requires Cooking Fire. Campfire818 first refused missing wood4470, then
with wood present refused missing flint4471. Provisioned materials let its real
10-second cast create a visible campfire; focusAvailable changedfalse->true and
actual cooking2538 produced2679, skill2->3. Reviewed fire frame200440. The temporary
fire expires naturally; it was not spawned by a GM worldobject command.

Gelman5513's Apprentice Miner2581 purchase correctly refused with zero free primary
slots. Actual pointer selection showed the two-profession limit and disabled Train
button in frame200629. The unselected row remains green despite the purchase gate;
reference review pending, no color change inferred yet. The diagnostic refusal
reason mentions money despite affordable cost10; the gate is the profession cap.

Missing-material feedback displayed numeric IDs4470/4471. Local casts now resolve
cached item names and request absent templates, with readable generic fallback.
Both builds11warnings/0errors and UIErrorsFrame PASS; live retest is reagent-names.

### Mail ownership and named requirements — 2026-09-07,20:14 local

`reagent-names` first campfire retest hit its legitimate cooldown, so it does not
verify material feedback. Cooking2538 and Skinning8613 then showed Missing Stringy
Wolf Meat and Requires Skinning Knife; frames200912/200915 reviewed. All temporarily
removed knives/flint and temporary wood restored/removed as intended.

Main sent Tough Jerky117 plus123c to own Nbwarhuman, accepted86.036; sent an empty
text-only letter accepted142.364. Core configuration MailDeliveryDelay3600 applies
to money/attachments: delivery is pending until approximately21:09 local. Do not
count accepted send as receipt. Test recipients are greg's own characters only.

Summoned SuperUI warrior died immediately after arrival (Core20:10:22), ghosted and
self-recovered30seconds later. Cause unresolved; initial possession assertion failed
and those frames were still main. Later actual control A1 succeeded198.943. Its
mailbox reused main's empty inbox and60-second query throttle. ResetMail now runs
on both control acknowledgements, clearing inbox/compose/confirmation/throttle;
POSSESS_LAW2.3 and its client check enforce it. Both builds11warnings/0errors and
client/CorePossessLaw PASS; build-mail-reset-sha256.json.

Reopening the possessed mailbox forced a request but still returned zero rows.
Core MailHandler.cpp770+ declares pActor=GetSuiActor yet iterates the session's
GetMasterPlayer mail; WorldSession.h968 returns m_masterPlayer directly. Normal
warrior login comparison is mail-owner. No Core edits/deployment/restart performed.
External disconnect20:12:40 preserved partial artifacts; queued release/dismiss
had not run. Confirm companion state after reconnect rather than claiming cleanup.

### Mail reset retest and server ownership defect — 2026-09-07,20:18 local

Normal Nbwarhuman login in mail-owner received the immediate empty letter1866;
normal mage and possessed warrior had received zero rows. In the reverse direction,
warrior session possessing mage still received warrior letter1866. Thus server
inbox ownership is CONFIRMED_DEFECT, not merely client caching or delivery delay.
HandleGetMailList uses session GetMasterPlayer despite resolving unused pActor.
Other mail operations also use GetMasterPlayer and require a Core ownership audit;
no possessed deletion/take/return was attempted on the wrong mailbox. No Core edit
or deployment occurred. The broad Core law check remains green because it only
requires a GetSuiActor occurrence, not use of that actor for the mail store.

Client ResetMail retest passed both directions: frames201628/201634 show mailbox
closed on grant/release; immediate reopens sent force=True queries102.951/108.537,
within60seconds. Both images reviewed. This closes GI38's observed client defect;
GI39 remains a server correction requirement. Mage companion dismissed and roster
confirmedstate0/inWorldFalse. Reading letter1866 changed its displayed expiry from
29days to2days on subsequent refresh, consistent with the server's read-mail expiry.
Money/attachment mail remains pending until21:09; no acquisition claim yet.

### Pointer vendor and gathering round — 2026-09-07,20:26 local

Hidden GUI proxy now supports right-down/right-up; normal desktop cursor remains
untouched. Both builds11warnings/0errors; build-right-input-sha256.json. Actual
backpack right-click sold4ToughJerky117 for4c, purse268->272. Buyback tab right-click
restored4 and purse268. Actual vendor right-click boughtMiningPick2901 for81c,
receipt93.992 and count assertion1. Trainer2581 confirmation/purchase cost10,
learnedMining/Smelting/FindMinerals, then second-primary2372 Herbalism confirmation
and purchase succeeded. These are real trainer changes, not GM skill provisioning.

Existing CopperVein1731/guid26226 used Mining2575,3.2-secondcast/animation62,
loot and skill1->2. TakeAll delivered2RoughStone2835 and1CopperOre2770. Its first
still is partly obscured by the rock and is not a clean mining-pose pass. Normal
movement interrupted a second cast and standing0 assertion passed.

Smelt2657 correctly refused RequiresForge with ore present. Diagnostic book name
Smelting failed because OpenProfessionNamed matches the Mining skill line; using
Mining opened it. Gelman trainer position is outside the actual forge focus, so
craft refused despite reagent1/1. The diagnostic reason missing-reagents is too
broad here; no successful bar creation claimed. Next fixture uses the Goldshire
smith/forge. Herbalism2366 at existingPeacebloom1618/guid43847 returned two real
TRY_AGAIN attempts; the node remained and retry is continuing. Do not treat normal
gathering chance failures as automatic defects.

### Gathering retry, smelting and repair — 2026-09-07,20:32 local

Peacebloom1618 returned five TRY_AGAIN outcomes before the sixth cast succeeded
at621.500; skill182 increased1->2 and TakeAll delivered3Peacebloom2447 at622.967.
Core Spell.cpp6114-6123 explicitly rolls orange gathering failure, using
reqSkillValue > irand(skillValue-25,skillValue+37). These attempts establish retry
and failure feedback, not a gathering defect. Four herb frame samples from the
third (failed) attempt were reviewed: animation123 during gathering, return to
standing after refusal, no stray spell particles required by kit64.

At SmithArgus514's actual Goldshire forge area, SmeltCopper2657 consumed the
previously gathered1ore2770 and produced1bar2840 at389.129; Mining2->3. Gelman's
trainer seat had no nearby forge focus, which explains the earlier refusal. Smith
Argus is a trainer, not the repair merchant; its vendor request was a fixture
failure. CorinaSteele54/guid80338 is the actual merchant/repair service.

Actual RepairAll pointer click restored pants21/25->25/25, sword16/20->20/20 and
shield16/20->20/20. ITEM_REPAIR kit7994/ui.vendor was exactlyone; clicking the now
disabled repair button produced no extra cue (both sound assertions PASS).
Mining's clearer four-frame recapture is continuing near the same existing vein.

### Flight2->4 and insufficient-fare refusal — 2026-09-07,20:39 local

Flight run, same right-input build: Thor523 first interaction discovered node4;
Stormwind352 returned known2,4. Taxi2->4 fare110 accepted71.505, 28points/73614ms.
Warrior purse160->50. Takeoff screenshot203557 reviewed with gryphon/rider; input
W during flight produced axes0/jumpfalse LOCKED_OUT. Arrival frame203719 reviewed,
standing0 and normal movement afterward. Return4->2 correctly rejectedcode3 with
50c versus110fare and displayed You don't have enough money; controlLockedFalse.
No GM money or flight unlock was used. GM travel only established flightmaster
visits before the actual ride. Possessed mage separately discovered node4 and its
return-flight/left-behind-body test is now running.

The clearer CopperMining sequence was reviewed: animation62 swings the authored
MiningPick_SpellObject; completion returns to standing and removes the pick.
Second mining loot succeeded. Summarize-Live produced attempt-evidence for both
four-frame cells, preserving falseGO for the failed herb attempt and trueGO for
mining; no success was borrowed from the later herb retry. External disconnect
20:33:44 occurred after gathering; artifacts flushed and flight started in a fresh
session. All companions were offline before this flight round's mage summon.

### Possessed flight and acquired-letter chain — 2026-09-07,20:51 local

Flight4->2 accepted312.467 for the possessed mage, fare110 paid from21713->21603.
Control assertions passed at start, middle and after landing. Reviewed mid-flight
204029 and landed204131 frames show gryphon ride then the mage standing normally
at Stormwind. Read-only server GPS for the left-behind warrior at311.416,347.829,
409.595 and433.148 remained(-10627.152344,1036.680054,34.217575), Sentinel Hill.
Release did not chase/teleport the warrior; mage dismissed and roster offline.
Flight protocol finished with zero runner failures. This covers this route/body
pair, not every taxi route or party configuration.

Stockades-chain uses the same right-input build. Letter2874 was actually looted
from VanCleef in dungeon-final at81.271. Item use opened quest373 with book portrait;
204446 frame reviewed. Accept added373, normal Baros turn-in consumed the letter
and completed363.674: purse21603->22813,1210c verified. Follow-up389 appeared and
was accepted. GM travel to quest NPCs is setup, not travel proof. Chain continues.

### Stockades chain, boss observations and area label — 2026-09-07,21:03 local

Stockades-chain completed373->389->391 through ordinary acceptance and rewards;
389 paid270c,22813->23083. Bazil1716 died763.942 (bounded observer passed), actual
head2926 received766.834 and count1 confirmed. Warden turn-in391 completed859.427,
3940c,24185->28125. Both SuperUI companions dismissed and roster offline before
normal exit. GM travel was fixture setup; no GM quest completion/resource refill.

Targorr1696 died to the paladin before the fight observer began; the observer's
refusal is preserved, not a passing recorded fight. Enrage8599 applied471.628,
matching the exported30%-health event169603. Actual corpse loot1797+3Wool2592.
Dextren1663, Kam1666 and Hamhock1717 bounded observers saw death. Dextren combat
frames were off-camera and do not certify his visual sequence. Hamhock Bloodlust
6742 applied; ChainLightning421 had resisted attempts and a two-target hit708.822.
Kam ShieldSlam8242 appeared. Selected GUID clears on death; early anchor/loot
steps failed until reselecting the actual corpse. Bruegal1720 was absent; no rare
spawn was created. Fourteen runner failures remain with this attempt, mainly those
fixture/refusal cases and a redundant389 reward-request after an offer already
opened. Priest arrived dead, then recovered his own body; saved/login cause remains
unresolved and no new combat defect is inferred.

Area label stayed Stormwind City throughout map34. WMO area lookup returned0;
actual AreaTable.dbc has sole top-level area717 The Stockade for map34. Added an
unambiguous map-area fallback to the minimap/session-area and soundscape resolution;
continents and maps with multiple root areas still return0. Both builds11warnings,
0errors, Minimap/MinimapBinding/ZoneText/client+CorePossessLaw checks PASS. Actual
archive regression coversStockades717,SFK209,ambiguousmaps0/36 and unknownmap.
GI40 live retest pending: first new-client login failed with end-of-stream before
world entry; server remains online and a fresh login is running. No Core control.

Correction to a fixture concern: _config.Start.Map is updated on NEW_WORLD in
GameLoop.Net.cs; actual anchor commands in this run usedmap34 correctly.

Evidence correction,21:04: Bazil's death observer completed immediately after
764.132 attack-stop replies; the preceding entry's763.942 timestamp was a recording
error. Use the preserved step130/764.132 evidence. No result change.

### Instance fallback live verification — 2026-09-07,21:08 local

instance-area-retry enteredmap34 and reportedarea717/displayzone717 at31.681;
reviewed210419 screenshot shows The Stockade on the minimap. Returnedmap0 reports
area1519 at36.965 and reviewed210424 shows Stormwind City. Original login failure
is retained separately. The zone splash duplicated the same WMO name for zone and
subzone; added equality guard, rebuild/retest pending after the current quest.

Detailed Stockades spell limits: Targorr3391 fired465.608; Dextren11976 hit555.912
and574.694, while19134 twice had resisted targets (fear mechanics not passed).
Kam8242 applied641.148 and removed643.084. Spell target IDs must be checked per
caster; other Defias units also cast9128, so no blanket boss-spell attribution.

### GI41 targeted consumable wire — 2026-09-07,21:17 local

The Sleeping Druid2541: sixth shaman kill yielded8363 at284.130; turn-in396.206
paid420c,28193->28613. Additional already-queued kills were unnecessary; later
selection failures are fixture failures. Follow-up2561 supplied8149 and persisted
through reconnect. Original alive and corpse uses both returnedBAD_TARGETS because
WorldSession.UseItem always wrote targetmask0, omitting the selected unit entirely.
The old alive refusal therefore did not independently certify the intended gate.

Added optional packed unit target to CMSG_USE_ITEM, shared normal spell target
resolution in SendItemUse, and a wire/target regression including a corpse GUID and
friendly fallback to the actor. Both builds11warnings/0errors. UseItem,Minimap,
ZoneText,CooldownProtocol,client+CorePossessLaw PASS. New live retry is running;
now the alive-target use actually sends Rageclaw's GUID and getsBAD_TARGETS.
Corpse cast, credit and reward remain pending. Ground/item/GO consumable cursor
variants are outside this unit-target fix and remain open in the coverage map.

### Targeted charm live result — 2026-09-07,21:20 local

item-target build: explicit livingRageclaw refused41.462; after normal kill, explicit
corpse use started52.586, quest2561 objective completed62.529 and spellGO62.530.
Reviewed1s/9s/postcast frames show casting hand glows and cleanup; camera/wall partly
obscures the corpse, so its full impact transformation remains unreviewed. External
disconnect21:17:06 interrupted before turn-in; partial artifacts retained.
item-target-finish reconnect retained completion, reward49.912 paid660c,
28613->29273 and choice item1085227 received. One runner failure is the inappropriate
inbox-close token in a standalone replay; requested game steps completed normally.
Minimap again shows The Stockade. Splash screenshot was too late (already faded),
so the duplicate-name visual regression still needs an earlier temporal capture.
Mail recipient normal-login is now running. Spell-sweep's resolvedGUID field is not
item-use-aware; use inventory/use-target and wire evidence for this targeted item.

### Delayed mail and early splash retest — 2026-09-07,21:26 local

mail-delivery normal warrior login, item-target build, zero runner failures.
Inbox45.787 contained1866(emptytext) and1865(item117x1,money123). TakeMoney1865
succeeded93.221; purse50->173 visible in collection capture. TakeItem succeeded95.272,
jerky count4->5 asserted. Return1866 succeeded97.457 and disappeared from list.
Delete emptied1865 also succeeded. COD mail to ownmage with117 stack5 and10c price
accepted161.226 (about21:22:38), postage30c; expected delivery around22:22:38.
No COD receipt/payment claimed yet. Returned text receipt still needs mage inbox.

Early Stockades212246/212247 screenshots reviewed: one Stormwind Stockade line and
Alliance Territory, with no duplicate subzone. Minimap The Stockade and return to
Stormwind also verified. GI40 closes for this observed main-body transition.
Mailbox initial GM placement was inside its model and ordinary movement was blocked;
that view is only UI evidence. Repositioned outside with explicit GM setup before
COD send. No world-camera/collision pass inferred from the mailbox-center fixture.

### Starter training, scrolling and combat-condition correction — 2026-09-08,02:55 local

Current quest-scroll-fixed run uses build-quest-scroll-sha256.json. GI59 live NPC
thumb drags022525/022527 and quest-log detail022725/022726 visibly scroll both ways.
Native trainer showed the unaffordable10c Immolate gate. Selling five ordinary
junk stacks through actual vendor right-clicks raised0->80c; native training554.008
sent service1374, learned348 at554.025 and success554.027, purse70c. Training visual
023424 reviewed. Native right-click equips looted Small Blue Pouch828 in container1,
12 slots at627.284. Spellbook Destruction lists Immolate; actual drag to slot4
asserted exactly one pickup832/uSpellIconPickup.wav and drop833/uSpellIconDrop.wav
in ui.actionbar at766.097/766.814. These are routed-audio assertions, not listening.

Beginnings1599 has2/3 Feather Charms6753 after two real Novice946 kills and autoloot;
XP296->354->414. Immolate348 cast, aura and periodic damage observed; overlapping
Shadow Bolt/hostile effects limit isolated visual approval. Two extra whelps then
kept attacking at player health1. Returned to Alamar using recorded GM positioning.
Read-only Core investigation: Player.cpp login applies CONFIG_BOOL_GM_CHEAT_GOD,
SetCheatGod uses invincibility threshold1, and run/etc/mangosd.conf has GM.CheatGod=1.
This is independent of .gm OFF. Sent .cheat god off; server confirmed at1607.745.
Future normal-combat logins must explicitly disable and verify it.

EVIDENCE CORRECTION: earlier greg-account combat without a recorded god-mode disable
cannot establish ordinary survivability, combat difficulty or damage-induced death.
Recorded spell effects, actual enemy deaths, quest counters and loot remain their
own evidence. Prior .die/repop/reclaim tests establish only the documented induced
recovery path. Companion states require their own verification. No server setting
was changed. Current female warlock2 retains XP414/900, two charms, source2187,
trained Immolate and equipped pouch/gloves. GI54/GI55 natural level-up is pending.
Cave fixture(-6514,372,392) gave poor geometry visibility; nearby valid interior
(-6540,374,396) rendered the cave. Camera/fixture investigation remains open.
Two runner failures were invalid extra take-all after automatic loot closed and a
Progress-panel assertion with no NPC panel; retain these as harness setup failures.
All28 coverage areas remain open.

### Damage-induced death and native recovery — 2026-09-08,03:03 local

After confirmed .cheat god off, novice946 reduced health58->35->8->4->0.
PLAYER_DEAD2021.697 and animation1 observed. Four equipment durability decrements
35->32,25->23,14->13,16->15 accompanied the10% warning. Actual Release Spirit
button2062.045 created corpseF101000000000079 and ghost,30-second reclaim delay.
Reviewed025933 graveyard/ghost/Spirit Healer. Recorded GM return to corpse location
(not a corpse-run navigation pass); actual Accept2149.533 reclaimed, health33,
ghostFalse, living animation0. Returned safely to Anvilmar by GM positioning.

Companion setup failed: immediate resummon after dismissal saw unavailable state4;
later explicit roster refresh showed state0. No helper was present during the death.
A separate summon attempt while ghost was rejected by server code3 with explicit
alive/outdoors guidance; dispatch PASS did not mean the summon succeeded. The failed
helper attack, unfinished fight, expected third-charm count and summoned assertion
remain failures. Both charms retained; no quest advancement claimed from that fight.







### 2026-09-08 — Core source follow-up, owner rebuild/install pending

GI-39, GI-60, GI-78 and GI-80 now have owner-authorized C++ fixes applied directly
in `~/vmangos`. Five translation units pass syntax-only compilation; expanded Core
possession laws and new `tools/core-patches/check-actor-fixes.py` regressions pass.
Full Core rebuild/link, installation and all post-change live tests remain pending.
See the latest entry in `FULL_GAME_COVERAGE.md` for exact scope, files and evidence.
Historical rows above retain their original audit outcomes; they are not live passes.

### 2026-09-08 — GI-82 resumed production and live verification

Fixed missing stealth 119/120 in CharacterRenderer's actual bake list and Walk
fallback rate in all three renderers. Clinical tests now use the production list
and actual mounted model clip availability; DruidCat intentionally falls back 0/4.
Red regression reproduced missing preload; corrected test and both client builds
pass. Direct human rogue idle/forward/back/strafe/cancel passed with server aura
replies, pose assertions and reviewed frames in rogue-stealth-retest (11:50).
Possessed/streamed humanoid and post-fix cat movement remain pending. See the latest
FULL_GAME_COVERAGE entry for evidence, interrupted login and failed earlier clicks.

### 2026-09-08 12:00 — GI-78/80 live follow-up

Possessed hunter now displays Cat's true health and 4 training points and opens Erma's
stable. Swap still failed (server 0x06): four pet save/load calls retained _player.
Corrected them to pActor directly in Core NPCHandler.cpp; added red/green regression
and law checks, syntax-only PASS. Owner rebuild/install and live mutation retest
pending. Exact evidence and limits in the latest FULL_GAME_COVERAGE entry.

### 2026-09-08 12:08 — GI-82 Cat and Shadowmeld follow-up

Post-fix Cat Prowl live poses 0/4, backward13, strafe4; cancellation restores run5.
Server aura apply/remove confirmed. Shadowmeld humanoid120 and movement cancellation0
have reviewed before/after frames. Cat frames reviewed with visibility limits retained;
see FULL_GAME_COVERAGE for details. Broader animation coverage remains open.

### 2026-09-08 12:17 — GI-39 empirical actor mail operations

Possessed warrior sends100c with correct actor purse deduction; delivery due around
13:11:51 under configured one-hour delay. Druid instant text reaches warrior inbox;
possessed read, permanent copy to warrior inventory and return all verified, followed
by returned-letter deletion on druid. Historical mage letter is empty, no COD pass.
Exact IDs, failed input/confirmation attempts and pending matrix: FULL_GAME_COVERAGE.

### 2026-09-08 12:20 — GI-60 live port follow-up

Same-map207.7yd and Goldshire->Darnassus ports left Nbmaghuman in place; returning
re-observed exact pre-port coordinates in both cases. Ordinary main walking resumes
normal nearby follow. Evidence and limitations: FULL_GAME_COVERAGE12:20 and
checklist-20260908/cat-stealth-final. No flight/instance/distant-hop pass inferred.

### 2026-09-08 12:35 — GI-69 overflow follow-up

Real self-damage burst reproduces occupied-row reset with correct lifetimes. New
queue retires only colliding older rows when adding newer feedback; optional loot
uses the same stack. Production regression red at message7, green20-message burst.
Both builds and scoped checks PASS; reviewed live12:34:06/07 now separates numbers
and expires correctly. This is an intentional overflow improvement over mounted
CombatText.lua; critical/mixed and loot-enabled live cases remain open. Full evidence
and exact limitations in FULL_GAME_COVERAGE12:35, checklist-20260908/combat-burst-*.

### 2026-09-08 12:43 — GI-60 flight and far-hop matrix

Main hunter2->4 native taxi ride left mage at exact Stormwind coordinates. After
landing, same-map distant possession moved control to mage; release re-observed
hunter at exact Sentinel Hill landing coordinates. Fare110 paid by hunter. Mage
dismissed/offline before clean exit. Full evidence/fixture failures in coverage12:43.

### 2026-09-08 13:23 — GI39 delivery and GI78/GI80 stable retest

One-hour warrior-to-druid100c mail arrived; native collection increases only druid's
purse1088->1188, empty-letter deletion succeeds. New installed Core b41f985a...bc834d
passes possessed hunter Cat/Bristle swaps, Stable and Unstable through all four
corrected ownership calls, with health/training fields and native frames reviewed.
Range exit closes; attempted return remains out of range and refuses. Hunter restored
Cat active/Bristle stabled, dismissed/offline; normal exit. Exact cases and fixture
failures in FULL_GAME_COVERAGE13:22/13:23. Remaining matrix stays open.

### 2026-09-08 15:01 — quest script evidence and diagnostic classification

Mist938 now has real successful mage escort/arrival credit and reward, with declared
mana fixture and prior deaths/disconnect preserved. Plagued Lands2118 real supplied
trap transforms/captures bear on open ground, credit1/1 and native reward succeed;
obstructed placements/dead-character retry remain setup failures. Full exact cases in
FULL_GAME_COVERAGE13:37/14:49. No broad quest/script pass inferred.

Audit-only CastClassification incorrectly treated channel-interrupt flags as channel
identity. Corrected to authored AttributesEx channel bits, with mounted red/green
regression, both builds and live ordinary9437/true-channel12051 proof. Discovery74rows
reclassified,4872->4867 candidate cohorts, no gameplay passes inferred. This is a
harness/catalog diagnostic correction, not a newly claimed player-facing GI defect.

### 2026-09-08 15:38 — next-ten audit progress

GI39 same-frame send/release proves wrong sender/purse before handler entry: recipient
gets mage223 sender for druid225 request. Send-mail MAP queue changed to WORLD alongside
control/freeze. Send/freeze also reproducibly leaves pending after Resume+20s; frozen
entry now returns mail failure. Both Core source fixes syntax/regression/law green;
owner rebuild/install and live retest pending. Range callback race still inconclusive.
GI60 actual Stockades map34 entry, two-follower world hold, and selective explicit
follow relink pass the bounded cases; see FULL_GAME_COVERAGE15:38 for exact positions,
authoritative chain states, GM placement limitations and cleanup. Item/COD deliveries
and GI69 final results remain in progress; no full-game completion claim.

### 2026-09-08 16:07 — next-ten retests

New owner-installed Core live retest closes GI39 actor-switch and freeze cancellations:
FAILED-6, pending=False, no recipient letter. Eight successful race letters reconciled
and empty fixtures cleaned; warrior coin23 unchanged. Range cancellation remains
inconclusive despite port/walking trials; do not infer callback ordering from dispatch.
GI60 native chain unlink/relink and normal Stockades doorway entry also pass, supplementing
the earlier GM setup tests. GI69 mixed critical/scrolling fix passes real critical retest
and temporal production regression; loot-enabled real corpse item+damage text passes.
Both client trays and full interface checker green. Item/COD receipt pending16:12.
Exact evidence and limits in FULL_GAME_COVERAGE15:43,15:48,16:06.

### 2026-09-08 16:11 — owner-requested pause

Next-ten batch stopped:7 PASS,1 INCONCLUSIVE (range callback cancellation),2 UNFINISHED
(item/COD receipt). The final idle receipt run disconnected16:09:16 before cleanup;
cleanup-only reconnect follows. No attachments/payment collected. Fixture identities,
verified purses/inventory and expected next steps are recorded in FULL_GAME_COVERAGE
16:11. Seven passes are bounded cases, not closure of the full-game matrix.
