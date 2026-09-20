# Sept 8, 26 fixes

Complete recap of the September 7–8 gameplay audit, paused at the owner's request on September 8, 2026. This covers the work that began with spellbook/macro drag sounds and expanded into the live-game checklist.

## State at pause

- **Paused.** The hidden game client and local test process are closed. The last live run, `druid-swim-fixed`, exited normally. No more gameplay or implementation work is queued by this task.
- **82 tracked interaction entries** are reproduced below, including completed fixes, partially verified fixes, open defects, untested leads and disproven leads. An entry is not automatically a completed fix.
- **The full game is not certified. All 28 broad coverage areas remain open.** Individual successful tests retain their exact scope.
- **Latest client Debug and Release builds pass with 10 warnings and 0 errors.** They include the unfinished stealth-pose change, GI-82.
- **The latest expanded BodyDisplay test does not compile.** `BodyDisplayClinicalChecks.cs:90` reports `CS0117: 'WorldEntity' does not contain a definition for 'MoveFlags'`. This happened after the client builds. No stealth regression or post-change live test has passed. Work stopped at the requested pause, without repairing that test.
- **Last live-verified change: GI-81, Aquatic Form swimming animation.** Its earlier BodyDisplay test, client builds, PossessLaw, MountRendering and TacticalFreeze checks passed before GI-82 was added.
- No commits, pushes or Core installation/deployment/restart were performed by this audit. The local GI-60 Core patch remains an undeployed candidate. Existing unrelated workspace changes have been preserved; this recap does not claim authorship of every file in the working tree.

## Reading this report

1. The grouped recap explains the changes in ordinary gameplay terms.
2. The complete GI register retains every tracked entry's evidence and limitations.
3. The supporting engineering register includes diagnostic and harness fixes that do not have GI numbers.
4. The pause notes identify unfinished work, fixture state and evidence corrections.
5. The appendices preserve the detailed historical records inside this file so that earlier small fixes, test failures and scope limitations are not lost. Historical “pending” statements can be superseded by later dated entries and the register above them.

## Changes by gameplay area

### Sounds, action bars and ordinary feedback

Spell and macro dragging now routes one pickup and one placement cue through the actual gesture path. The identified assets are SoundEntries **832/833**, `uSpellIconPickup.wav` and `uSpellIconDrop.wav`. Live spellbook and macro pointer gestures were checked with exact event counts and restored action slots. This is the original requested fix. The evidence establishes routed playback and readable audio assets; subjective listening remains a separate test.

Inventory dragging gained material-specific pickup/put-down sounds through ItemDisplayInfo and ItemGroupSounds. Stack-split confirmation uses the pickup path. Same-slot cancellation and local refusal remain silent, and delayed snapshots do not replay a placement sound. Pet spellbook/action-bar gestures gained the authored **837/838** cues. A pet-drop null dereference was also corrected.

Action-bar swaps now retain the displaced action on the cursor so it can be placed elsewhere. Quest-log opening and closing, target selection, chat scrolling, first/repeated self-whispers, PvP activation and bonus-bar transitions received scoped feedback checks and the recorded corrections below. Remaining mute, queue, neutral-target and timing variants are explicitly open.

### Inventory, targeting and panel interaction

Body changes clear carried items, split dialogs and spell/macro/action cursors. Native drag sources now retain their actor and item identity so a held drag cannot acquire an item from the next possessed body. Newly appearing invisible action-bar input hosts no longer steal focus from inventory drags. The default ImGui drag preview/target rectangle was removed from those gameplay gestures.

Unbound Bind-on-Equip equipment now asks before binding through auto-equip and equipment swaps. The pending confirmation retains actor/item identity and invalidates appropriately after movement or possession. Indestructible items refuse destruction before showing a confirmation or sending a destroy request. Unrestricted item tooltips ignore reserved class/race mask bits instead of displaying redundant restriction lists.

Equipment and Enchanting can remain open together in either relevant opening order. Profession recipe clicks now reach the recipe rows instead of being swallowed by a whole-list invisible button. Ordinary world devices no longer open a diagnostic ImGui fallback window.

Unit-targeted consumables now send their resolved target, including a corpse. Ground-target consumables now arm an aim cursor and send the chosen destination. Their intent retains the acting body/item, clears on possession and tactical freeze, and owns world clicks in Command View. Ground range feedback is actor-relative. Item cooldown gates use the item's authored category and agree with the bag/shelf timers rather than querying only Spell.dbc's category.

### Quest and progression presentation

Quest rewards include the controlled character's max-level money bonus without double-scaling the packet amount. Questgiver objects, including wanted posters, use object-aware acceptance and actor-relative range gates. Object/item quest givers use the appropriate book portrait where verified.

Quest-item progress notices wait for updated inventory state instead of showing the previous count. Long reward names wrap/fit their authored boxes; partially clipped visible reward rows remain selectable without capturing clicks outside the content area. Both NPC and quest-log detail scrollbar thumbs now respond to dragging.

Readable pages expand paragraph, player-name and other supported quest text tokens. Readable titles/page labels use the authored gold styling. Quest panels now show learned spell and trade-skill rewards, their labels and tooltips, alongside item rewards without making the spell row a reward-choice button.

Level-up chat reports level, health/mana, talents and positive stat changes in the mounted reference order. Internal class/racial skill updates are suppressed while legitimate weapon, Defense and profession gains remain visible. Character creation filters invalid name characters in the active editor and limits the displayed/submitted name consistently. Character roster rows reserve space for the Create button.

### Spells, models and movement

Feign Death now holds the fallen pose while health remains positive and returns to standing on cancellation. Shapeshifts use the server replacement model and predicted body movement instead of trying to animate the original character rig. Bandage cast/channel handoff now ends the looping action on channel completion or movement interruption.

Fishing now uses the bobber's authored interaction range and holds the fishing pole in hand, with a line and attachment endpoint during the channel and normal sheath restoration afterward. Ground casts preserve fixed-destination projectile travel, arrival audio and the authored release burst even when the GO packet contains no unit hit list. Instance area fallback now identifies The Stockade instead of retaining Stormwind and suppresses duplicate zone/subzone labels.

The newest verified model fix adds swimming animation selection to the creature renderer used by Aquatic Form: idle **41**, forward **42**, sideways **43/44**, backward **45**, with turn/diagonal precedence and fallbacks. The live seal now visibly strokes through the water instead of retaining ground idle **0**. Straight forward speed measured **7.083333 yd/s** in Aquatic Form and **4.722222 yd/s** after cancellation, confirming the 50% increase for this fixture.

The newest **unfinished** change adds stealth idle **120** and creeping locomotion **119** to creature, streamed-player and local-character renderers, using the existing server descriptor flag. Before the change, Prowl correctly made the cat translucent but still used idle **0** and run **5**. Client builds pass; the new test has the compile failure stated above, and live retesting has not happened.

### Pets, stables and possession

Companion arrival settles the stale “Summoning” footer. Temporary charmed pets now receive their Dismiss menu under the acting-body ownership rule. Pet name caching refreshes when the server name timestamp changes, so a rename updates the frame without reconnecting.

Hunter happiness now has the three-state icon and tooltip, including the displayed damage percentage and loyalty direction. Actual feeding moved the pet through Unhappy, Content and Happy. The displayed damage multipliers have not been measured in combat.

Stable controls now fit the window at the tested scale, close after the acting body moves out of range, display slot prices, disable unaffordable purchases and hide purchasing at the slot cap. These are verified on the directly controlled hunter; possessed stable availability has a separate unresolved Core defect.

Beast Training opens its native panel and shows pet-specific requirements, known state, training costs and the Train action. Training-point decoding now follows Core's signed encoding rather than splitting a raw word into two unsigned halves. Live paid Bite training and an upgrade consumed the correct incremental points. Empty teaching-spell descriptions fall back to the taught ability and substitute the correct rank's damage values.

Possessed character-sheet attack clocks now decode the three snapshot float-bit fields correctly. Mail state resets on both directions of possession and immediately requests the new inbox. Core still returns the wrong body's mail, omits possessed pet owner-only stats, and filters stable availability using the logged-in body's class; those are open defects, not completed server fixes.

### Combat notices

Hidden client auras no longer leak their internal names into floating combat text. Vertical stacking for rapid notices was implemented, but damage numbers still overlap at the upper burst boundary in a reviewed case. GI-69 remains open.

## Complete interaction register

The following entries retain the source ledger's exact scope. “PASS” or “live verified” applies only to the described cases and build; a pending variant remains pending. Priority and reproduction details are retained for follow-up.

### GI-01 — Drag a spell or macro onto an empty or occupied bar: one pickup and one placement cue, no per-frame repetition

**Priority:** P1.

**Evidence / change:** SoundEntries 832/833; `Panels/GameLoop.Spellbook.cs`, `Panels/GameLoop.Macro.cs`, `Hud/GameLoop.ActionBars.cs`

**Status and limits:** Live actual spellbook/macro pointer pickup and placement, exact cue counts and slot restoration verified

### GI-02 — Pick up inventory items: use the item's material sound, once; cover bags, bank, equipment and bag-bar slots

**Priority:** P1.

**Evidence / change:** ItemDisplayInfo field 11 -> ItemGroupSounds pickup column; `PickupOrPlaceItem` previously had no cue

**Status and limits:** Live handler/material matrix and bank transfer verified; pointer/bank-bag variants pending

### GI-03 — Place a carried item: its material put-down cue only after dispatch; same-slot cancellation, local refusal and failed send are silent

**Priority:** P1.

**Evidence / change:** ItemGroupSounds put-down column; `PickupOrPlaceItem` after `sent` gate

**Status and limits:** Live accepted moves and silent original-slot cancellation verified; pointer variants pending

### GI-04 — Confirm a stack split: one pickup cue when the selected count becomes carried; cancelling the dialog does not pick up

**Priority:** P1.

**Evidence / change:** `DrawStackSplit` accepted branch is separate from ordinary pickup

**Status and limits:** Live split Enter/Escape and possession cancellation verified

### GI-05 — Pick up from pet spellbook or pet bar: one ability-icon cue

**Priority:** P1.

**Evidence / change:** SoundEntries 837, `Sound\Interface\uSpellIconPickup.wav`; `PickupPetBookSpell`, `PickupPetAction` had no cue

**Status and limits:** Fixed drag crash; live imp book/bar/token gestures verified; frozen/possessed/pointer variants pending

### GI-06 — Place pet actions: cue on assignment; same-action, passive, illegal token relocation and frozen/read-only refusals stay silent

**Priority:** P1.

**Evidence / change:** SoundEntries 838, `Sound\Interface\uSpellIconDrop.wav`; `PlacePetAction` after `TryAssign`

**Status and limits:** Live passive and same-action refusals verified; remaining gates pending

### GI-07 — Move bar action A onto B, then put B elsewhere: B must remain on the cursor after the first drop

**Priority:** P1.

**Evidence / change:** `MultiActionBarUiLaw.PlaceAction` returns B; `FinishActionDrag` unconditionally cleared that cursor

**Status and limits:** Live production release handler verified: displaced spell retained, placed elsewhere, temporary slots restored

### GI-08 — Open/close the quest log through keybinding, micro-menu, close button and Escape: exactly one matching cue per state transition

**Priority:** P1.

**Evidence / change:** Mounted `QuestLogFrame.lua` lines 87/94 names `igQuestLogOpen/Close`; `Panels/GameLoop.Quest.cs`

**Status and limits:** Live binding/Escape, actual close/Exit buttons and micro-menu verified

### GI-09 — Select friendly NPC, hostile, neutral, and clear target: correct cue on actual selection change

**Priority:** P1.

**Evidence / change:** Mounted `TargetFrame.lua` lines 106-115; `Combat/GameLoop.Targeting.cs`

**Status and limits:** Live self/friendly guard/GM-off hostile/repeat/clear verified; natural neutral NPC variant pending

### GI-10 — Failed item move: cursor/slot locks recover; error once; no duplicate success cue from a later snapshot

**Priority:** P1.

**Evidence / change:** `InventoryFailureClinicalChecks`, `ObserveBagLocks`, inventory error receiver

**Status and limits:** Live invalid equipment placement unlock/cursor recovery verified; other refusals pending

### GI-11 — Press/cancel/repeat a spell or item: pressed state, GCD, own cooldown and finish flash match the actual action

**Priority:** P1.

**Evidence / change:** Mounted ActionButton hooks; `CooldownProtocolClinicalChecks`, action bars and inventory rendering

**Status and limits:** Suspected timing/transition audit; not a confirmed gap

### GI-12 — Change controlled body while holding an item or pet action: old payload clears, purse/feedback owner follows the new body

**Priority:** P1.

**Evidence / change:** `ResetBodySessionUiOnControlChange`, POSSESS_LAW; item feedback now tags `ControlledGuid`

**Status and limits:** Fixed cross-body carried-item/split/spell cursors; live both directions verified; pet variant pending

### GI-13 — Scroll chat up/down/bottom: button cue only on actual input, not new messages/redraw

**Priority:** P2.

**Evidence / change:** Mounted `CombatLog.xml` lines 34/61/89 names `igChatBottom/ScrollDown/ScrollUp`

**Status and limits:** Live actual GUI up/down/bottom buttons and held-button nonrepeat verified

### GI-14 — Coin amount dialog open, selection, OK, cancel: distinct intended cues without duplicate coin-change audio

**Priority:** P2.

**Evidence / change:** Mounted `CoinPickupFrame.lua/xml`, `MONEYFRAMEOPEN/CLOSE`, `igBackPackCoinSelect/OK/Cancel`

**Status and limits:** CoinPickupFrame not used by inline mail/trade denomination inputs; input UX still pending

### GI-15 — Bank bag control feedback

**Priority:** P2.

**Evidence / change:** Mounted `BankFrame.lua` line 194, `BAGMENUBUTTONPRESS` is bag open/shift pickup, not slot purchase

**Status and limits:** Original purchase-cue lead disproven by surrounding source; actual bank gestures remain in GI-02/03

### GI-16 — Bonus/shapeshift bar appears: verify opening feedback and avoid repeats while the bar remains visible

**Priority:** P2.

**Evidence / change:** Mounted `BonusActionBarFrame.lua` line 64, `igBonusBarOpen`; action-bar paging/state

**Status and limits:** Fixed bonus-page wire mapping; live cat/bear/travel and manual-page override verified; body-model defect tracked separately

### GI-17 — Receive a whisper or raid warning: cue once, honor mute/diagnostic settings

**Priority:** P2.

**Evidence / change:** Mounted `ChatFrame.lua` line 1473 (`TellMessage`) and `RaidWarning.lua` line 10; network/chat routes may use numeric kits

**Status and limits:** Live self-whisper first/rapid-repeat verified; mute/raid warning/quiet expiry pending

### GI-18 — Loot, money and quest rewards: feedback corresponds to acquisition without replay after refresh/control switch

**Priority:** P2.

**Evidence / change:** AcquisitionSound checks; `Panels/GameLoop.Loot.cs`, `ObserveMoneySound`, quest-added/completion routes

**Status and limits:** Covered in source; live replay checks pending

### GI-19 — Item right-click equip/use, vendor pickup and buyback: material cue at the correct acceptance point

**Priority:** P2.

**Evidence / change:** ItemGroupSounds includes Use; these bypass ordinary cursor placement and are deliberately not claimed covered by GI-02/03

**Status and limits:** Suspected; native timing/reference review needed

### GI-20 — Hover/leave/disable/toggle: highlight clears, disabled texture agrees with gate, checked state reflects authoritative action

**Priority:** P2.

**Evidence / change:** 891 generated API/visual sites include inherited XML hooks; inspect mapped panel law and renderer

**Status and limits:** Suspected; review one panel family at a time

### GI-21 — Escape with several panels/cursors open: unwind in intended order and play only the closing panel's cue

**Priority:** P2.

**Evidence / change:** `ClearCarriedItemOnEscape`, panel ownership and binding handlers; original UI scripts provide reference, MSUI design may differ

**Status and limits:** Suspected

### GI-22 — PvP state/queue transitions: intended cue only on the transition

**Priority:** P3.

**Evidence / change:** Mounted PlayerFrame/BattlefieldFrame scripts: `igPVPUpdate`, `PVPENTERQUEUE`, `PVPTHROUGHQUEUE`

**Status and limits:** Live clean PvP flag activation and single cue verified; queue and possessed routing pending

### GI-23 — Character roster rows must not overlap Create New Character

**Priority:** P1.

**Evidence / change:** Live greg roster, nine rows, 2560x1369 client area: center click selected row nine; lower edge opened creation. `DrawCharacterSelect` uses fixed 60-unit pitch without reserving the create button

**Status and limits:** Fixed and live GUI verified: first/ninth row, creation, ten-row frame reviewed

### GI-24 — Companion summon status must stop saying Summoning after authoritative arrival

**Priority:** P2.

**Evidence / change:** Screenshot showed completed rows with stale Summoning footer. `ApplyCompanionList` now settles the last accepted summon's status only when its matching row becomes a companion

**Status and limits:** Fixed and live verified by `companion assert-status-summoned Gondolfo` and framebuffer capture

### GI-25 — NPC quest rewards must include the driven body's max-level money bonus

**Priority:** P2.

**Evidence / change:** Live quest 7 displayed 25 copper but awarded 115. NPC detail/offer now uses queried bonus and ControlledGuid level; base amount is not scaled twice

**Status and limits:** Fixed; level-60 quest 5261 shows and pays 60 copper, level-1 quest 7 still shows 25; hidden-reward and possessed variants pending

### GI-26 — Questgiver objects must accept quests and range from the driven body

**Priority:** P1.

**Evidence / change:** Wanted poster47/quest180 opened but failed NPC-only descriptor gate

**Status and limits:** Fixed; real main quest objective/reward and possessed Accept/abandon/range refusal verified

### GI-27 — Item/object quest givers use a book portrait

**Priority:** P2.

**Evidence / change:** Mounted QuestFrame.lua62–68; object incorrectly used generic monster portrait

**Status and limits:** Fixed; possessed poster book reviewed; item variant pending

### GI-28 — `/macro` and `/m` open the Macro Book

**Priority:** P2.

**Evidence / change:** Mounted ChatFrame.lua and GlobalStrings; aliases missing from client dispatcher

**Status and limits:** Fixed; both live commands and resulting frames verified

### GI-29 — Feign Death holds the fallen pose while health remains positive, cancels to standing

**Priority:** P1.

**Evidence / change:** Hunter5384 server aura applied while local character stayed upright

**Status and limits:** Fixed; live held animation1/cancel0 and actual death/repop/reclaim verified; remote variants pending

### GI-30 — Shapeshift uses the server replacement body and its movement animation

**Priority:** P1.

**Evidence / change:** Cat, bear, travel and Ghost Wolf had display changes on the original character rig

**Status and limits:** Fixed; live transformed model/movement/cancel assertions and reviewed cat/bear/travel frames; clearer wolf frame and remaining variants pending

### GI-31 — Ordinary world devices must not open diagnostic widgets or silently consume Escape

**Priority:** P1.

**Evidence / change:** Deadmines cannon opened raw ImGui World Object window

**Status and limits:** Removed fallback; live cannon/menu and readable poster open/Escape cues verified

### GI-32 — Readable title and page labels remain gold on the dark shell

**Priority:** P2.

**Evidence / change:** Live Keeshan poster title was black; mounted ItemTextFrame.xml inherits GameFontNormal and Lua only recolors the body

**Status and limits:** Fixed; both builds/scoped checks pass and rebuilt live gold title reviewed at19:13

### GI-33 — A fishing cast's bobber remains usable at the authored distance

**Priority:** P1.

**Evidence / change:** Live cast placed bobber10.847yards away; client refused limit6 while Core type17 permits100

**Status and limits:** Fixed; live13.175yard bite use, fishing loot, item6291 receipt and skill gains verified; Command View live variant pending

### GI-34 — Fishing holds the pole and draws its line, then restores the normal sheath

**Priority:** P1.

**Evidence / change:** Live channel7620 played134 with sheath0 and zero pole endpoints; pole visibly stayed on back

**Status and limits:** Fixed; live pole/line reviewed, poleTips1 during channel and0 afterward; remote variants pending

### GI-35 — Verify the apparent missing lake surface

**Priority:** P2.

**Evidence / change:** Gray Crystal Lake surface initially mistaken for lake bed

**Status and limits:** Missing-water diagnosis DISPROVEN by same-camera on/off/restored comparison; water is drawn. No appearance change made

### GI-36 — Bandage animation ends on channel completion or movement interruption

**Priority:** P1.

**Evidence / change:** First Aid746 cast and channel both use animation123; the looping cast action outlived the channel hold

**Status and limits:** Fixed matching cast-to-channel handoff in local and streamed renderers; main live completion and movement cancellation return to standing0; streamed variant pending

### GI-37 — Missing reagents and tools identify the item by name

**Priority:** P2.

**Evidence / change:** Campfire818 showed numeric4470/4471 in local cast errors

**Status and limits:** Item template names now used with readable uncached fallback; both builds/UIErrorsFrame PASS; live Stringy Wolf Meat and Skinning Knife messages reviewed

### GI-38 — Switching bodies resets mailbox state and fetches the new body's inbox

**Priority:** P1.

**Evidence / change:** Possessed warrior inherited main's empty inbox and60-second throttle

**Status and limits:** Added ResetMail to both control acknowledgements; both builds/possession checks PASS; live closure and immediate requery verified both directions

### GI-39 — Server inbox and mail operations belong to the driven body

**Priority:** P1.

**Evidence / change:** MailHandler.cpp HandleGetMailList declares GetSuiActor but iterates GetMasterPlayer mail

**Status and limits:** CONFIRMED server defect: normal warrior receives1866; mage session possessing warrior seeszero, warrior session possessing mage sees1866. Core correction required; no server edits/deployment

### GI-40 — Instance entry updates the area label and soundscape identity

**Priority:** P2.

**Evidence / change:** Stockades WMO area ID0 retained Stormwind City; actual map34 has sole area717

**Status and limits:** Map-area fallback and duplicate WMO zone/subzone guard built; live entry/return verified and early212246/212247 captures show one dungeon name

### GI-41 — Unit-targeted consumables send the selected target

**Priority:** P1.

**Evidence / change:** Voodoo Charm8149 failed on Rageclaw's corpse because CMSG_USE_ITEM always sent mask0

**Status and limits:** Added resolved unit GUID and packed target body; both builds/regressions PASS; living target refused, corpse cast10617 accepted and quest2561 credit62.529; reconnect turn-in paid660c and supplied choice item

### GI-42 — Open equipment while Enchanting is open, retaining both panels for item targeting

**Priority:** P1.

**Evidence / change:** Normal opener7411 then C refused because the coordinator accepted only Character/Spellbook replacement shapes

**Status and limits:** Added registered-left transition using existing ownership law and native callbacks; both builds and UiPanelObserver/client+Core possession checks PASS; live214502 side-by-side Character/Enchanting reviewed

### GI-43 — Clicking a profession recipe changes selection and enables its action

**Priority:** P1.

**Evidence / change:** Whole-list invisible button captured input before individual rows; actual recipe clicks did nothing

**Status and limits:** Replaced list button with passive wheel-region hit testing; both builds/ProfessionFrame PASS; actual Craft7418/7428 and TradeSkill3915 selections and crafts verified

### GI-44 — Mail confirmations display the amount before accepting COD, sending money, or deleting money

**Priority:** P2.

**Evidence / change:** Actual COD1868 popup said accepting would cost but omitted the10c amount; mounted StaticPopup.lua explicitly updates its MoneyFrame

**Status and limits:** Restored native coin display; both builds/scoped checks PASS. COD10c accepted/payment collected; separate send12345c succeeded129.823 after interrupted first attempt. Returned1871 delete warning amount reviewed000748 and cancelled, funds collected. GI48 fixes text wrapping

### GI-45 — Two-digit silver/copper values remain fully readable in mail entry fields

**Priority:** P2.

**Evidence / change:** Native send12345c screenshot224014 clips the second digits of23/45; generic border inset plus ImGui padding consumes the30px fields

**Status and limits:** Zero authored text insets/padding for mail controls; both builds/MailFrame/ImGui/client possession PASS. Reviewed224946 displays1/23/45 fully; actual send12345c accepted129.823 and purse29347->16972 includes30c postage

### GI-46 — Ground-target consumables open an aim cursor and send the chosen location

**Priority:** P1.

**Evidence / change:** Actual right-click on crafted4358 immediately sent mask0 and server4054 BAD_TARGETS965.607, count2 unchanged

**Status and limits:** Both builds/scoped checks PASS. Native arm/Escape, same-stack move/current slot commit, SuperUI possession clear verified. Actual world-pointer GO68.757 consumes1; Freeze47-unit snapshot clears intent, Resume preserves2. Command View click defect separately fixedGI49

### GI-47 — Pure ground casts retain destination projectile/arrival sound and authored GO burst

**Priority:** P1.

**Evidence / change:** 4054 empty-hit GO consumes item but local visual handler only iterates unit hit/miss rows

**Status and limits:** Both builds and scoped checks PASS. Live4054 fixed-point movement/arrival sound38 verified; foliage-obscured full flight frame remains a visual limit. Actual worldpointer2120 GO157.616 spawns burst before area loop; burst/burn and later clear frames reviewed. No blanket spell visual pass

### GI-48 — Destructive mail warning remains fully readable above its amount/buttons

**Priority:** P2.

**Evidence / change:** Returned12345c letter1871 DeleteMoney warning clips right edge in235654 capture

**Status and limits:** Fixed wrapping and content-based height with native320/420 widths and GameFontHighlight. Both builds11warnings0errors; MailFrame/UiPanelObserver/ImGui PASS. Reviewed000748 complete delete warning/amount; Cancel preserved funds, collected12345c and deleted emptied letter. Reviewed001648 complete send warning; cancelled repeat. Other locales/scales untested

### GI-49 — Armed ground items own world clicks in Command View

**Priority:** P2.

**Evidence / change:** Live rogue4358 right-click leaves4054 rune/intent active; free-view routing intercepts the gesture

**Status and limits:** Live cursor precedes RTS orders. Both builds and client/Core possession/freeze/item checks PASS. Reviewed003044 actual cancel preserves2; actual left throw GO149.364 consumes1. Possessed variant still untested

### GI-50 — Ground aiming shows native casting cursor and red footprint outside range

**Priority:** P2.

**Evidence / change:** Mage2120 world-click OUT_OF_RANGE while marker remained green; local Benilla reference confirms native range/decal rules

**Status and limits:** Actor-relative hover range, native textures/default/cap/slab/first2 radii. Both builds/radius checks PASS. Rogue reviewed003043 green Cast,003135 small red marker; actual click OUT_OF_RANGE211.133 preserves item. Other terrain/actor/radius variants open

### GI-51 — Item cooldown gate agrees with its authored category and visible swipe

**Priority:** P2.

**Evidence / change:** Successful4054 then repeat342.519 sent despite item24/60000ms; serverNOT_READY342.602; gate queried DBC category0

**Status and limits:** Shared item-category gate for bag/shelf, actor-owned bag timers/swipe. Both builds/scoped checks/client+Core law PASS. Live actual repeat35.974 blocked locally, no cursor/wire,1 preserved; expiry and immediate rearm after range refusal pass. Shelf/inspected-body live variants open

### GI-52 — Character name editor displays exactly the filtered name that Accept will submit

**Priority:** P2.

**Evidence / change:** Live focused name N1@ remains visible while internal filtered N leaves Accept disabled; post-draw byte-buffer cleanup does not update ImGui's active editor

**Status and limits:** Insertion-time CallbackCharFilter and12-character buffer. Both builds/CharCreate/CharSelectCurrent/ImGui PASS. Reviewed005521 filters N1@ to N;005607 caps at12; actual Nbwlkgnome creation0x2E, female Gnome Warlock1 GUIDA2, matching roster/world appearance. Fresh login011742 retains appearance, bars and quest inventory

### GI-53 — Quest-item progress notification agrees with the updated quest tracker

**Priority:** P2.

**Evidence / change:** Native first wolf loot010944 shows center0/8 while tracker1/8; ADD_ITEM arrives before inventory descriptors

**Status and limits:** Both builds and scoped checks PASS. Updated descriptor observer: reviewed0119003/8,0120094/8 and0136058/8 agree with tracker; unrelated loot and fresh-login restoration silent. Other actor/item variants remain open

### GI-54 — Internal class/racial skill changes remain silent when leveling

**Priority:** P2.

**Evidence / change:** Native level2 screenshot012757 prints GENERIC(DND), Demonology, Destruction and Gnome racial increases

**Status and limits:** Added admitting SkillRaceClassInfo 0x402 gate; mounted-data checks retain weapon/Defense/profession notices. Natural level3 live031718/031719 has no internal skill spam; actual Defense7 through10 notices retained earlier in this build

### GI-55 — Level-up chat reports level, health/mana, talent point and positive stat gains in native order

**Priority:** P2.

**Evidence / change:** Level2 packet626.858 parsed +15HP/+38mana/stats but only an invented green center message appeared

**Status and limits:** Restored mounted ChatFrame.lua PLAYER_LEVEL_UP chat sequence via LevelUpChatLaw. Natural level3 packet3129.133 and reviewed031718/031719 show +15HP/+24mana, Strength/Agility/Intellect/Spirit +1 in order, no zero Stamina/talent line, native gold effect. Non-mana and talent-threshold checks remain offline evidence

### GI-56 — Quest reward item names remain inside their authored name boxes

**Priority:** P2.

**Evidence / change:** Sten179 reward014156 Rabbit/Wolf/Boar Handler Gloves overflow adjacent boxes

**Status and limits:** Restored mounted QuestFrameTemplates.xml90x36 GameFontHighlight box, wrapping/height fit and vertical centering. Debug/Release and QuestLog checks PASS; reviewed015428/015905 names contained, long first name ellipsized with full tooltip

### GI-57 — Visible first-column quest rewards can be hovered and selected without offscreen click capture

**Priority:** P1.

**Evidence / change:** Actual Rabbit Handler reward hover/click014259/014411 gives neither tooltip nor selection; full-containment gate rejects native-3px grid inset

**Status and limits:** Intersect item hit region with scroll clip; pure tests cover left inset, partial bottom and wholly hidden row. Debug/Release/QuestLog/client+Core possession/ImGui PASS. Live015904 hover/015905 selection enable Complete; turn-in330.796 receives1079850, consumes8meat, grants80XP. Shared spell-row footer regression live033943: pointer91,602 below content clip shows Complete hover, no tooltip; click186.148 completes1599. Other item layouts remain separate

### GI-58 — Readable item pages expand paragraph and character tokens

**Priority:** P2.

**Evidence / change:** Native right-click9577/page2450 screenshot020250 shows literal $B$B and $n in Tainted Memorandum

**Status and limits:** Reader now expands the shared quest macros before body layout, matching reference feed_item_text. Paragraph/name/class/gender and renderer-seam checks, both builds, ItemTextFrame/ImGui/client+Core possession PASS. Reviewed020724 native right-click: paragraphs and Nbwlkgnome resolved, entire page contained. Other materials, multi-page and possessed identity remain separate cases

### GI-59 — NPC quest and quest-log detail scrollbar thumbs respond to dragging

**Priority:** P2.

**Evidence / change:** Beginnings1599 drag from239 to520 leaves text unchanged021901, arrow click scrolls021945; both quest renderers only draw their thumbs

**Status and limits:** Added native track hit handlers and clamped pointer-to-scroll mapping for both panels. Mounted UIPanelTemplates.xml defines the scrollbar as Slider. Both builds and QuestLog/ImGui/client+Core possession checks PASS. Reviewed native NPC bottom022525/top022527 and quest-log bottom022725/top022726; text and thumb move together

### GI-60 — Followers hold when the driven body ports, as required by supplied standing rule3

**Priority:** P1.

**Evidence / change:** SuperUI Nbprihuman automatically followed a258yd GM port at03:19:50; server explicitly logs party-catchup and frame031951 shows priest in cave before explicit .namego

**Status and limits:** Confirmed Core/standing-rule mismatch. Existing detailed POSSESS_LAW4.3/7.2 still instructs port following. Candidate tools/core-patches/gi60-hold-after-port.patch applies in read-only dry-run; not compiled, installed or live-tested. Detailed law/check reconciliation and owner rollout remain pending

### GI-61 — NPC quest details and reward panels show the spell being taught

**Priority:** P2.

**Evidence / change:** Beginnings1599 reward032437 omits Summon Imp688 and the entire reward section despite parsed RewardSpell

**Status and limits:** Added spell-aware reward visibility, native learn label and non-selectable spell row/tooltip between choices and fixed items. Both builds10warnings0errors; QuestLog/ImGui/client+Core possession PASS. Live reward033727 and partial/scrolled tooltip033832/033834 reviewed. Row click does not complete; actual footer click186.148 completes1599, consumes3charms, grants355XP and teaches688 (book034114). Earth1518 NPC Details041314 and mixed Reward041612 now reviewed; spell8071/item5175 tooltips041726 correct. Sacred Cloth6032 Details044123/Reward044423 use trade-skill label; tooltips reviewed, turn-in515.722 teaches19435 and pays33900c. Recipe appears044501; deeper tooltip content parity remains unverified

### GI-62 — Indestructible items refuse destruction before offering a confirmation

**Priority:** P2.

**Evidence / change:** Earth Totem5175 flags0x20 still offered Yes/No and sent destroy at1374.240; Core refused24 and preserved item

**Status and limits:** Added template-flag gate before popup and send, native ERR_DROP_BOUND_ITEM. Debug/Release10warnings0errors; DeleteItem/QuestLog/ImGui/client+Core possession PASS. Live043205 immediate error/no popup, totem1 and empty cursor, no destroy send. Ordinary Ruined Pelt Cancel preserved5; Yes143.142 removed5. PASS for these actual gestures

### GI-63 — Equipping unbound BoE gear asks before binding

**Priority:** P1.

**Evidence / change:** Native crafted Improved Mooncloth Boots1099092 right-click044955 equips immediately; no confirmation exists on auto-equip or swaps

**Status and limits:** Added shared auto-equip gate and bidirectional equipment-swap gate with actor/item identity retention and vanilla Okay/Cancel popup. Both builds10warnings0errors; EquipBinding/ObserverBody/possess/ImGui/DeleteItem and Corelaw PASS. Live autoequip Cancel/Escape/Okay, bound bypass, forward/reverse click swaps, moved-item invalidation and SuperUI possession cleanup PASS; run0failures. Drag-release behavior remains a separate lead

### GI-64 — Possessed character sheet shows real attack speed

**Priority:** P2.

**Evidence / change:** Mage snapshot051216 shows attack1153958 and ranged1157235 seconds

**Status and limits:** Core SuiPossess1354 sends raw stored float bits; normal Object.cpp752 converts to uint milliseconds. Client normalizes only the three snapshot clocks. Both builds/scoped checks PASS; live mage052644 and repeated possession052722 show 1.60 melee/2.00 ranged, own priest1.90/2.00. Snapshot-time-fixed runner0 failures

### GI-65 — Inventory drag releases into its target without an extra click

**Priority:** P1.

**Evidence / change:** Native equipment and bag presses lose active ID when empty action-bar input hosts appear; payload remains absent

**Status and limits:** NoFocusOnAppearing preserves the source. Live 053525 unequip and 053607 equip PASS; reverse BoE drag Cancel/Okay 053741/45 PASS. Default ImGui preview/target rectangle removed and reviewed054341; final runner054242 zero failures, bag swap/held Escape/closed-bag drag1->2->1 PASS

### GI-66 — A held inventory drag cannot acquire the next possessed body's item

**Priority:** P1.

**Evidence / change:** Native held priest boots drag became mage boots GUID0686 after possession 053754; cursor assertion FAIL

**Status and limits:** Retain pressed actor/container/slot/item and clear press with cursor. Both inventory and bag-bar source gates require the retained press. Both builds/scoped client and Core checks PASS. Live held possession054351 and release054352 cursor0, original boots retained; runner054242 zero failures

### GI-67 — Unrestricted items omit redundant class/race lists

**Priority:** P3.

**Evidence / change:** Taming Rod15911 mask2047 shows all nine playable classes, unnecessarily widening tooltip055857

**Status and limits:** Reference tooltip_item/render.rs429 masks to playable class bits. Client compared the raw mask for equality; now masks reserved bits. Both builds/scoped checks PASS. Live rod15908 tooltip062732 omits class list; restricted16850 tooltip063321 retains Classes: Hunter. Fixture removed afterward

### GI-68 — Temporary charmed pets offer a Dismiss context menu

**Priority:** P2.

**Evidence / change:** Native snow-leopard portrait/name right-click062141/062228 opens nothing; SUMMONEDBY-only gate excludes CHARMEDBY

**Status and limits:** Acting-body charm/summon ownership admitted. Both builds/scoped checks/Corelaw PASS. Live062828 Dismiss/Cancel, actual dismiss133.810 removes charm19597 and062927 shows hostile bear with no pet portrait/bar. Possessed-pet menu remains separate live case

### GI-69 — Rapid combat notices remain readable

**Priority:** P2.

**Evidence / change:** Live061838 overlays Defense and Dazed notices on the same screen area

**Status and limits:** Vertical stacking built and scoped checks PASS. Reviewed065920 separates Dazed/Aspect messages, but damage numbers still overlap at the upper burst boundary. OPEN: investigate the authored offset reset and message lifetime interaction before claiming readability PASS

### GI-70 — Hidden auras remain absent from floating combat text

**Priority:** P2.

**Evidence / change:** Defensive State5302 and Defensive State224948 leak in061838 despite mounted hidden=True and buff icons being filtered

**Status and limits:** HiddenClientSide gate, both builds and scoped checks PASS. Live5302/24948 apply418.757; reviewed065920 retains Dazed/Aspect and omits hidden names, later065925/070413 retain ordinary damage. PASS for these hidden aura cases

### GI-71 — Hunter pet frame displays happiness and its tooltip

**Priority:** P2.

**Evidence / change:** Permanent boar frame063529/063641 has no happiness icon; DrawPetFrame has no happiness branch

**Status and limits:** Both builds/PetMenu/ImGui PASS. Reviewed071107 Unhappy75%/Losing Loyalty,071247 Content100%/Gaining Loyalty,071311 Happy125%/Gaining Loyalty. Actual three food117 feeds consume3 and raw happiness rises through thresholds to1041250. PASS UI/feeding sequence; damage multipliers not empirically measured. Protocol lacks personality selector, mounted fallback row1 used

### GI-72 — Pet rename updates the frame and name cache immediately

**Priority:** P2.

**Evidence / change:** Native Copper rename removes Rename option but frame stays Boar064056; stable shows Copper064412

**Status and limits:** Versioned name refresh on PET_NAME_TIMESTAMP140 built, PetName checks PASS. Actual native rename confirmation071411 then reviewed071445 frame Bristle without reconnect; diagnostic timestamp0->1788866084/nameBristle. PASS same-session rename; stable retrieval retains name

### GI-73 — Stable controls stay inside the window at gameplay scale

**Priority:** P2.

**Evidence / change:** Stable button clips at right064412/064527

**Status and limits:** Logical-width correction built. Reviewed065315 row highlight and065414 complete Stable button remain inside border. PASS at tested scale

### GI-74 — Stable closes outside the acting body interaction range

**Priority:** P2.

**Evidence / change:** Native backward walk to13.617yd leaves stable open064527

**Status and limits:** Both builds/client+Core law PASS. Native backward walk closes at125.953; reviewed065418 closed window and065506 distant request shows You are too far away. PASS own-body case; possessed/delayed-reply variants pending

### GI-75 — Stable slot purchase displays its cost and respects affordability/cap

**Priority:** P2.

**Evidence / change:** Native stable064412 has no price and always enables Buy Slot

**Status and limits:** Both builds/scoped checks/client+Core law PASS. Reviewed070932 red5gold/disabled, native disabled click071021 no purchase, added50000c fixture then071022 enabled/white price. Actual purchase97.2500x0A;071039 slots0/2 hides buy/cost; final purse1270c. PASS second-slot affordability/purchase/cap; first-price mounted-data evidence

### GI-76 — Beast Training opens its native craft panel

**Priority:** P1.

**Evidence / change:** Actual5149 reports LOCAL_ProfessionWindow but reviewed072810 shows no panel; skill261 is absent from craft whitelist

**Status and limits:** Both builds and ProfessionFrame/ImGui PASS. Reviewed073302 panel opens with Growl ranks1/2; actual Create rank1 sends1853 and GO97.379 teaches pet2649. PASS opener/learning; training UI defects trackedGI77

### GI-77 — Beast Training shows pet requirements, costs and Train availability

**Priority:** P2.

**Evidence / change:** Reviewed073302 has false skill0/0, Create, empty Reagents; Rank2 sends14922 and LOWLEVEL100.180 because no pet-level requirement appears

**Status and limits:** PASS own-body level/known/points, native Train, paid learning and upgrade: reviewed075855 zero-total footer hidden;075912 Bite1 learned,7->6 points;075959 Bite2 learned for3 incremental points,7->4. Signed Core training-point encoding corrected. Both builds and PetPaperDoll/ProfessionFrame/PetMenu/PossessLaw/ImGui plus Core law PASS. Possessed variants pending; active-ability cap remains server-authoritative

### GI-78 — Possessed hunter receives pet training points and owner stats

**Priority:** P1.

**Evidence / change:** Reviewed080208 displays0 points after own-body4; pet descriptor raw0 while loyalty2/level8 and learned Bite2 persist

**Status and limits:** OPEN Core visibility gap: Object::GetUpdateFieldFlagsForTarget grants UF_FLAG_OWNER_ONLY only to actual owner/charmer; UNIT_TRAINING_POINTS is owner-only. Possessed pet health also arrives normalized100. No Core deployment; direct pet-info command failed syntax/selection, so exact concurrent server value not independently read

### GI-79 — Beast Training describes Bite's effect

**Priority:** P2.

**Evidence / change:** Reviewed075959 empty detail body; mounted teacher17262 description blank but taught17255 contains damage text

**Status and limits:** Taught-ability description fallback implemented with rank-correct substitution. Debug/Release and mounted PetMenu check PASS; reviewed081042 Bite2 displays16 to18 damage and saved4 points. PASS tested rank

### GI-80 — Hunter stable services remain available when a mage controls the hunter

**Priority:** P1.

**Evidence / change:** Reviewed080423 Erma selected but That creature is not a stablemaster; no list request sent

**Status and limits:** OPEN Core Object.cpp714-717 removes stable bit using target session class, not controlled hunter. Range test could not run; GM go moved mage, hunter remained14.15yd away. No stable/range pass claimed for these captures

### GI-81 — Aquatic Form plays swimming locomotion

**Priority:** P2.

**Evidence / change:** Reviewed084654 moving underwater seal retained animation0; CreatureRenderer had no swimming selector

**Status and limits:** Added flag-driven swim idle/forward/back/strafe/turn selection with mounted seal-model regression. Debug/Release10warnings0errors; BodyDisplay/PossessLaw/MountRendering/TacticalFreeze PASS. Live60372 swim flag confirmed; reviewed085124 temporal forward frames animate flippers with42, stopped41; backward45/strafe43 logged. Land transition and additional variants pending

### GI-82 — Prowl and stealth use crouched idle and creeping locomotion

**Priority:** P2.

**Evidence / change:** Live9913 GO202.575 and aura apply202.643; translucent cat retains idle0 and running5 in085432/085433. Renderers never consume existing UnitIsStealthed pose flag

**Status and limits:** IMPLEMENTED, UNVERIFIED at owner pause. Creature/streamed-player/local-character selectors now use120idle/119creep, retain backward precedence and authored1x creep rate; local UnitState receives actor stealth flag. Both client trays10warnings0errors. Expanded BodyDisplay regression does not compile: CS0117 WorldEntity.MoveFlags at line90. No post-change live run; test repair and retest remain pending



## Supporting engineering and test-harness changes

These changes support the audit and are separate from shipping gameplay fixes.

1. **Archive-driven interaction discovery.** Added `tools/gameplay-interaction-audit` to inspect the actually mounted FrameXML, sound references and material gestures, preserving archive supplier and reference site. Initial discovery found 216 FrameXML files, 101 sound cues, 891 interaction/visual hooks and 72 item-material gestures. All 101 kits resolved to readable nonempty assets. Ten representative cues were independently decoded as non-silent PCM. Repeated unchanged scans produced a stable JSON hash.
2. **Full-game content inventory.** Added `tools/gameplay-coverage` with per-ID spell/quest inventories and grouping for test prioritization. Initial inventory: 22,357 spells, 4,872 candidate spell cohorts, 32,957 stage references, 559 referenced model paths and 4,433 quests from 4,725 exported rows. Eighteen unresolved model references remain reachability/fallback leads. Signed quest-export parsing was corrected to preserve negative object objectives and money requirements.
3. **Attempt-bounded evidence summaries.** `Summarize-Live.ps1` bounds responses to the actual attempt and has regressions rejecting a previous success, another spell's GO and a late GO. It does not turn an emitted command into a gameplay pass. The older whole-session aggregate is not an acceptance oracle.
4. **Hidden native client execution.** Added `--background`, hidden rendering at 30 FPS, isolated test settings, local protocol input, framebuffer captures and journals. Hidden startup ignores normal fullscreen/maximized preferences and does not replace the user's clipboard with capture paths. It does not take desktop mouse/keyboard control. Music startup retains its normal focus gate.
5. **Native input bridge.** Added diagnostic GUI pixel/button input and production key-state control. Optional `glue world-pointer-on/off` feeds the shipping world-press/unprojection route, including GUI capture and drag thresholds, without native cursor capture. Camera-look input remains a separate limitation.
6. **Persistent protocol inbox.** The running client can consume an append-only local inbox and close normally using `inbox-close`, allowing short empirical steps between state inspection and frame review.
7. **Native spawn/roster support.** Added `--native-spawn` to preserve the server's actual starting/saved position instead of the usual diagnostic vantage teleport. Roster inspection can observe the native menu for creation/deletion and appearance checks.
8. **Spell sampler correction.** Replaced the wrong world/portal-particle query with the actual spell renderer and exact instance identity, including child emitters. Early apparent missing Fireball hand effects were diagnostic false negatives. Geometry counters still do not prove visible pixels.
9. **Channel/family evidence.** Added spell-family/stage evidence and temporal sampling for channels, triggered children, aura lifetime and cleanup. The detailed records retain gaps involving stale animation choices, shared model paths and main-body versus possessed-body sampling.
10. **Invalid framebuffer rejection.** A class-ability batch produced 24 priest captures at 1×1 when the hidden framebuffer was unavailable. Capture now rejects unavailable dimensions and reports failure. Those frames remain invalid, and their spells are not marked visually passed.
11. **Pose/support diagnostics.** Added controlled-body model/animation/death/transform inspection and world support/liquid probes. The latest pose log also prints controller position, swimming, grounded state, planar speed, outgoing flags and stealth state. Observing a floor is not a placement/navigation pass.
12. **Bounded combat/escort/pet/fishing helpers.** Added the live scenario and observation helpers represented under `GameLoop/Dev/GameLoop.LiveRun.*`. Their use is logged separately from native pointer paths. Combat helper setup still requires actual range, facing and line of sight; it does not automatically move, face or heal the actor.
13. **Corpse reselection.** Added `select-fight-target` to reselect the retained last-fight GUID after ordinary death clears selection. It checks that the entity still exists and does not synthesize a corpse or objective credit.
14. **Malformed interaction guard.** Guarded the argument count before reading the gossip interaction argument so malformed `interact` input fails without an index exception.
15. **Vendor helper parsing.** Corrected a diagnostic vendor command that treated its three-part split as four arguments. The earlier refusal was a harness error, not a vendor gameplay defect.
16. **Possessed quest inspection.** `quest inspect-log` now reads the controlled character's displayed log/party facts rather than always reading the session character.
17. **Enchant target diagnostics.** An opened enchant item cursor is recorded as `ARMED` rather than falsely labeled `REFUSED`.
18. **Drag inspection.** Added read-only input/active-ID instrumentation that exposed the inventory focus loss and cross-body held-press defects.
19. **Water observation.** Added opt-in liquid/support reporting for the lake investigation. The missing-water hypothesis was disproven with same-camera on/off/restored comparison; no lake appearance/geometry fix was made on that basis.
20. **Network failure diagnostics.** The network worker exception path now logs the full exception before calling the normal failure handler. Two later EOFs remain unresolved; the added logging has not yet captured another failure. An old server process start time prevents claiming that those failures were caused by a restart.
21. **Regression and rule maintenance.** Extended the scoped checks associated with each correction, including item sound joins, target packets, panel ownership, reward geometry, item identity retention, snapshot clocks, pet training and mounted swim poses. Corrected stale checker expectations such as opcode-count 874 versus 876 and an existing quest hit-variable name. A source-check pass is not evidence that an unrelated live behavior works.
22. **Local Core candidate only.** Added `tools/core-patches/gi60-hold-after-port.patch` and its handoff note. The read-only application dry-run passed. It has not been compiled, installed or live-tested, and the detailed law/check wording needs reconciliation with the supplied standing rule before rollout.
23. **Persistent audit records.** Maintained the interaction ledger, full-game coverage map, append-only daily notes, protocol files, hashes, screenshots and journals. `AGENTS.md` lists tracked shared documents. Discovery counts, script dispatch results and presentation review are kept as different evidence layers.

## Open defects and unfinished work at pause

| Entry / issue | Current state | Remaining work |
|---|---|---|
| GI-39: possessed mail | Core uses the wrong body's mail collection despite resolving an actor | Owner-controlled Core correction and actual send/list/take/return/delete retests |
| GI-60: followers after port | Live automatic catch-up contradicts supplied standing rule 3; local patch candidate exists | Reconcile detailed laws/checks, compile and owner rollout, then the documented movement/flight/port matrix |
| GI-69: combat text overlap | Some notice stacking improved; upper damage/notice burst still overlaps | Trace authored offset reset and message lifetime, correct and retest burst readability |
| GI-78: possessed hunter pet stats | Owner-only points absent and pet health normalized | Core visibility and grant/release refresh design, followed by direct/possessed ownership retests |
| GI-80: possessed hunter stable | Stablemaster flag is filtered by the session mage's class | Core actor-aware eligibility and refreshed visible objects; repeat native stable/range tests |
| GI-82: stealth pose | Implementation built; test project currently fails to compile; no live retest | Repair the regression fixture, run tests/builds, then inspect live cat and ordinary/possessed stealth cases |
| Network EOF | Full exception logging added; cause unknown | Inspect the next captured worker stack; do not assume a server restart |
| Aquatic Form on dry land | Live Core accepted it; the correct mounted 1.12 eligibility oracle is unresolved | Establish the appropriate version-specific rule before calling it a defect or changing it |
| Suspected/partial GI rows | GI-11/14/18/19/20/21 and additional variants remain open in the register | Reference-driven checks, valid fixtures and actual success/refusal/cancel/repeat evidence |
| Full game | All 28 broad areas remain open | Continue per-ID/rank/conditional coverage; broad representative tests do not close whole families |

## Empirical coverage and interpretation

The historical appendix includes actual quest acceptance, objective completion, rewards, timed success/expiry, escort failure/success, defense waves, class quests, repeatable donations, object/item starts, corpse-target items and exploration chains. It also includes fishing, cooking, First Aid, gathering, smelting, repair, Enchanting, Tailoring, Alchemy, Engineering, mail/COD, travel, pets and possession tests.

Deadmines and Stockades encounters were swept with recorded GM positioning. These are encounter observations, not an uninterrupted dungeon-run or all-mechanics certification. Spell batches include actual casts, channels, impacts and aura cleanup, but many ranks, race/sex/weapon variants, procs and target geometries remain untested. Appendix details specify the exact IDs and outcomes.

Important corrections retained in this recap:

- `.gm off` alone did not disable the server's login god cheat. The audit discovered that login applied an invincibility threshold of 1 HP. Later normal-combat logins explicitly used `.cheat god off` and verified the reply. Earlier enemy deaths, loot and objectives keep their own evidence, but earlier survivability/difficulty conclusions without that disable are invalid.
- GM teleporting/provisioning/learning/loyalty/leveling is fixture setup, not proof of navigation, gathering, learning through ordinary progression or natural loyalty gain. No `.quest complete` shortcut is treated as objective success.
- GM `.go creature` while possessing moves the session body. Several early service/range failures therefore used the wrong fixture position; those attempts are retained as setup failures.
- Auto-loot closes the loot window here. Extra `take-all` calls after successful auto-loot were harness mistakes, not missing-loot defects.
- A spawn-table row can have an alternate creature entry. Druid shaman rows also admitted defenders; actual spawned identity must be verified before fighting/looting.
- Filenames such as “swim,” “impact” or “recovered” were provisional. Earlier druid “swim” frames were actually on land. One Rageclaw release attempt failed at cast end and later respawned. The first long breath test drowned before its queued surface step. Those names are not pass verdicts.
- The successful fresh Rageclaw corpse-item attempt had actual cast 10617 start 581.116 and GO 590.909. Reviewed full-size frames show hand glow, release burst, smoke/sparkles and cleanup; the quest then paid its recorded reward.
- The short breath retry is valid: server timer start 289.422; native Space ascent; recovery 303.244 at 47,267/60,000 ms with scale 10; stop 304.563. Reviewed `085610` shows the partly depleted bar underwater, and `085618` shows a living surfaced character with the bar gone.
- Audio journals establish routed sound events. Background music focus behavior and subjective listening remain explicit limits.
- Zero runner failures mean the protocol steps and explicit assertions completed. They do not override a recorded server refusal, dead character, occluded frame or unreviewed effect.

## Last fixture and build handoff

The last active character was **Nbdrunelf**, level 60, GUID `0x225`, on greg. The druid was revived and healed after the long underwater test, completed the short breath recovery test, returned to caster form and was moved to a safe recorded point near Crystal Lake `(-9382, -150, 60)`, map 0, before normal exit. The death's recorded durability loss was not claimed repaired.

The hunter fixture **Nbhundwarf** retains Cat pet 94, level 8, loyalty 2, Bite rank 2 and Growl rank 1, with **4 available training points**. Bristle is stabled. The hunter was dismissed from SuperUI and confirmed offline earlier. No new SuperUI companion was summoned during the final druid runs.

Build/test evidence, repository-relative:

- `docs/current/checklist-20260907/build-swim-forms-debug.log`
- `docs/current/checklist-20260907/build-swim-forms-release.log`
- `docs/current/checklist-20260907/build-swim-forms-hashes.json`
- `docs/current/checklist-20260907/check-swim-forms.log`
- `docs/current/checklist-20260907/check-swim-possess.log`
- `docs/current/checklist-20260907/check-swim-mount.log`
- `docs/current/checklist-20260907/check-swim-freeze.log`
- `docs/current/checklist-20260907/druid-swim-fixed.stdout.log` and the `druid-swim-fixed/` run artifacts
- `docs/current/checklist-20260907/build-stealth-pose-debug.log`
- `docs/current/checklist-20260907/build-stealth-pose-release.log`
- `docs/current/checklist-20260907/check-stealth-pose.log` — **compile failure; not a test pass**

The current executable files contain GI-82. The earlier verified swim hashes identify a different build; do not attribute its live results to the newer stealth build. No post-pause gameplay or test run was started to make this report.

## Historical evidence appendices

The following snapshots preserve the detailed recorded work and corrections. Source records sometimes use compact timestamps, abbreviations and provisional capture names. The current status above and later dated corrections govern over earlier provisional statements.

### Appendix A — interaction audit evidence history

Snapshot of the evidence sections from shared_docs/GAMEPLAY_INTERACTION_CHECKLIST.md. The complete current GI register appears above.

#### First batch behavior and limits

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

#### Verification record — 2026-09-07

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

#### Live acceptance sheet

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

#### Background live sweep — 2026-09-07

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

##### Deadmines follow-up, 11:56 local

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

##### Full-checklist continuation — 2026-09-07, afternoon

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

##### Pet and bonus-page defects — 2026-09-07, 14:10 local

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

##### Form pages and scope corrections — 2026-09-07, 14:12 local

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

##### Cursor ownership, captures and spell review — 2026-09-07, 15:12 local

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

##### Wanted poster, pointer input and resume — 2026-09-07, 18:25 local

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

##### Live object portrait and death recovery — 2026-09-07,18:32

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

##### Dungeon encounters and readable UI — 2026-09-07,19:11 local

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

##### Exploration chain and title retest — 2026-09-07,19:21 local

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

##### Fishing interaction, attachment and lake investigation — 2026-09-07 evening

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

##### Fishing visuals passed; water diagnosis corrected — 2026-09-07,19:47 local

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

##### Cooking, First Aid and channel handoff — 2026-09-07,20:01 local

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

##### Skinning, campfire requirements and primary cap — 2026-09-07,20:08 local

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

##### Mail ownership and named requirements — 2026-09-07,20:14 local

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

##### Mail reset retest and server ownership defect — 2026-09-07,20:18 local

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

##### Pointer vendor and gathering round — 2026-09-07,20:26 local

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

##### Gathering retry, smelting and repair — 2026-09-07,20:32 local

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

##### Flight2->4 and insufficient-fare refusal — 2026-09-07,20:39 local

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

##### Possessed flight and acquired-letter chain — 2026-09-07,20:51 local

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

##### Stockades chain, boss observations and area label — 2026-09-07,21:03 local

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

##### Instance fallback live verification — 2026-09-07,21:08 local

instance-area-retry enteredmap34 and reportedarea717/displayzone717 at31.681;
reviewed210419 screenshot shows The Stockade on the minimap. Returnedmap0 reports
area1519 at36.965 and reviewed210424 shows Stormwind City. Original login failure
is retained separately. The zone splash duplicated the same WMO name for zone and
subzone; added equality guard, rebuild/retest pending after the current quest.

Detailed Stockades spell limits: Targorr3391 fired465.608; Dextren11976 hit555.912
and574.694, while19134 twice had resisted targets (fear mechanics not passed).
Kam8242 applied641.148 and removed643.084. Spell target IDs must be checked per
caster; other Defias units also cast9128, so no blanket boss-spell attribution.

##### GI41 targeted consumable wire — 2026-09-07,21:17 local

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

##### Targeted charm live result — 2026-09-07,21:20 local

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

##### Delayed mail and early splash retest — 2026-09-07,21:26 local

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

##### Starter training, scrolling and combat-condition correction — 2026-09-08,02:55 local

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

##### Damage-induced death and native recovery — 2026-09-08,03:03 local

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








### Appendix B — full-game coverage and execution history

Snapshot of shared_docs/FULL_GAME_COVERAGE.md, including the coverage map, ID-specific outcomes and retained failures.

### Full-game coverage

Owner scope, 2026-09-07: the entire game, including different quest types and all
spell animations. The earlier GAMEPLAY_INTERACTION_CHECKLIST is one subsection,
not a claim of game-wide completion. Use greg's authorized characters, hidden
native live protocols, and **SuperUI companions only**. In-game setup is authorized;
Core deployment/restart and direct server database/world-save edits are not.

#### Inventory and evidence rules

`tools/gameplay-coverage` regenerates per-ID inventories from the actual mounted
archives and the live realm's quest export. First output lives in
`docs/current/full-game-20260907/coverage/`. The tool retains every spell ID and
latest quest row at content patch 10; grouping only prioritizes candidate tests.

Initial counts: **22,357 spells**, **4,872 candidate spell cohorts**, **32,957 spell
stage references**, **559 referenced model paths**, and **4,433 quests** from
4,725 exported rows. Eighteen unresolved model references require reachability
and fallback review, not eighteen automatic bugs. The quest classifier finds 18
overlapping families. Counts include internal/legacy spells and disabled quests;
they are not the denominator for player-facing completion until triaged.

Each executed case must record ID/rank, build, data provenance, account character,
actor body, prerequisites, setup commands, action, server response/state delta,
frame sequence, result, cleanup and evidence paths. Use independent columns:

| Layer | What can establish it | What does not establish it |
|---|---|---|
| Data | Row and dependencies resolve from identified archives/export | A name or matching source literal |
| Mechanics | Correct authoritative state change, target and actor | Request sent; GM completion; unrelated state changes |
| Presentation | Reviewed temporal captures plus expected model/animation/attachment/timing | Model loaded; one still image; any particle visible |
| Input | Shipping pointer/keyboard route and hit testing | Harness supplying a slot or NPC GUID |
| Regression | Scoped repeatable assertion with positive and refusal cases | A broad string match against the whole session |

Allowed outcomes: UNTESTED, SETUP_FAILED, EXPECTED_REFUSAL, CONFIRMED_DEFECT,
FIXED_PENDING_RETEST, PASS_WITH_EVIDENCE, NOT_APPLICABLE_WITH_REASON. Offline
checks and historical results retain their own evidence layer/build. Never infer
all ranks, all quests in a family, or all races from one passing representative.

#### Coverage map

Every row below remains open until its required subcases have evidence. This is
the scope map; generated per-ID CSVs supply the content backlog.

| Area | Required cases and observable outcomes |
|---|---|
| Character lifecycle | Every race/class/sex; creation validation; nine-row roster overlap; enter/logout/reconnect; deletion; saved appearance and bars |
| Quest dialogue | NPC/GO/item starts; gossip with multiple offers; accept/decline; available/active/completed markers; text and emotes; remote/range refusal |
| Quest objectives | Kill; creature credit; item drops and delivery; GO interaction; cast on target; exploration; scripted events; multiple objective lanes |
| Quest scripts | Escort, defense/waves, escort death/reset, NPC transforms/dialogue, summoned actors; inspect scripts before selecting representatives |
| Quest lifecycle | Prerequisites, chains, exclusive branches, breadcrumbs; class/race/skill/reputation gates; log full; abandon/reaccept; reconnect persistence |
| Quest outcomes | Timed success/expiry; reward choice; full bags; money cost; reputation; spell/item/mail rewards; repeatable second completion |
| Party quests | Share accept/decline/busy/ineligible; nearby credit; absent member; SuperUI possessed actor's log, reward, purse and markers |
| Class abilities | All nine classes, talents and ranks; racial, pet, item and NPC abilities; passive/proc activation separate from direct casts |
| Cast lifecycle | Instant/cast-time/channel; start/go/miss/impact; cancel/move/damage interrupt; GCD, own cooldown, resources, reagents and tools |
| Spell geometry | Self/friendly/hostile/dead targets; ground area/cone/chain; min/max range, facing and LOS; moving source/target and target death |
| Spell visuals | Precast, release, missile trajectory/contact, impact, channel, aura state/expiry; ground emitters, beams/ribbons, scale, depth and lighting |
| Character animation | Race/sex skeleton, weapon stance, hold/release, locomotion blending, jump/swim/mount/form; missing sequence fallback reviewed |
| Spell mechanics | Damage/heal/absorb/resist/immune; periodic ticks; dispel/steal; stacking/exclusive auras; crowd control; summon/dismiss; teleport/rez |
| Combat | Melee/ranged/wand; swings and queued attacks; ammo; threat/aggro/leash; fleeing/evade; cooldown/resource refusal; combat log and floating text |
| Death | Death animation, ghost/repop, corpse recovery, spirit healer, party resurrection, durability cost and repeated death |
| Inventory | Equip/use/unequip, requirements and two-hand conflicts; drag/swap/split; bags/bank; full/locked slots; ownership after possession |
| Economy | Vendor buy/sell/buyback/repair; bank slots; trade accept/change/cancel; mail attachment/COD/expiry; auction bid/buyout/return on test account |
| Professions | All primary/secondary professions; trainer gates/ranks/unlearn; gathering nodes, fishing and skinning; tools/focus, recipes/reagents, skillups/failures |
| Progression | XP/level gains, stat changes, talent learn/reset, weapon skills, reputation thresholds, faction hostility and PvP toggles |
| Pets/forms | Hunter tame/feed/train/stable; warlock summon; commands/autocast/happiness; shapeshift/stance bars; pet death/rez and owner switches |
| SuperUI party | Summon/dismiss, composition, orders/formation/follow/hold; possession, actor-owned NPC interactions; cross-map/flight hold law |
| CRPG/Command View | Freeze/unfreeze, queued intents, resource reservations, cast cancellation, movement/interaction execution and scene transitions |
| Travel | Walk/run/jump/fall/swim/drown; slopes/doors/collision; mounts; taxi routes/cost/landing; boats/zeppelins, portals, zone/instance transitions |
| Dungeons/raids | Trash, patrols, boss phases/scripts/doors, wipes/reset/lockout, loot rules and full runs; capture each encounter rather than one pack |
| World rendering | Terrain/WMO interiors, time/weather/fog, water, light under units/items, attachments/equipment, LOD/streaming and nameplates |
| UI and audio | Existing GI ledger, all panels/escape/focus; tooltips/scaling; quest/cast errors; combat/ambient/music transitions and muted settings |
| Social/PvP | Party/raid/guild and battleground state, queues/objectives/score/rewards; communication tests confined to authorized test recipients |
| Reliability | Reconnect mid-action; map loading; repeated sessions; stale replies after actor changes; long-session memory/GPU behavior and frame stalls |

#### Execution order and stopping rules

1. Enumerate data and production handlers, link existing clinical checks and old
   captures, and preserve their exact scope. Generated discovery is never a pass.
2. Establish repeatable fixtures on greg's characters. Record setup separately;
   GM teleport/provisioning does not prove travel, gathering, or quest objectives.
3. Exercise one representative of each uncovered behavior first, then every
   remaining player-facing ID/rank/conditional variant. Add a defect regression
   before widening a batch when the harness or acceptance rule is wrong.
4. Record temporal spell frames at precast, release, travel, impact and cleanup.
   Add interruption/target failure and race/sex/weapon variants. Compare rendered
   stages with authored references; review shape/timing as well as draw counters.
5. Quests run from eligible start to actual objective credit and reward, then
   refusal/repeat/abandon/persistence paths. Scripted objectives need source-traced
   triggers and a real encounter, not `.quest complete`.
6. Run appropriate clinical checks and Debug/Release builds for code changes.
   Reproduce failures with valid placement before filing a gameplay defect.
7. Update per-case evidence without replacing previous failures. Leave an explicit
   next queue. A finished batch never means the full game is certified.

Existing spell tools need caution: `spell-matrix-scenario` emits persistent NPC
spawn/delete commands and is not the approved default fixture here.
`spell-matrix-aggregate` groups cast rows by spell across a whole run rather than
by an attempt's time bounds; repeated standing/moving cells can contaminate its
verdict. Do not use its blanket PASS as acceptance. Animation sequence summaries
are diagnostic leads: absent authored stages and stale animation-choice entries
need lifecycle-aware review, and the current sampler reads the main character,
so it does not certify possessed-body animation.

#### First broad pass — 2026-09-07

Inventory generation completed; zero live passes are granted by the generator.
The source quest export hash is recorded in the generated summary. Initial signed
number parsing was corrected for the export's apostrophe prefix, preserving
negative gameobject objectives and money requirements. Live results follow below
as their server responses and temporal captures are reviewed.

##### Reviewed evidence

All paths below are under `docs/current/full-game-20260907/` unless marked dumps.
Native runner success means the steps dispatched and their explicit assertions
passed; the separate results below require server/state/frame evidence as well.

| Case | Result and evidence | Remaining limits |
|---|---|---|
| Quest 783, level 1 Nbpalhuman | `quest-intro/runner-20260907-121831.csv`: accept adds to log, turn-in removes it, server awards 40 XP and offers quest 7 | GM teleport used between NPCs; walking not certified |
| Quest 7 incomplete/abandon | Same run: server completable=false, screenshot shows 0/10 and disabled Continue, abandonment removes log entry | Expiry, reaccept after reconnect, and full-log behavior pending |
| Quest 783, level 60 Nbmaghuman | `spell-confirm-quest-kill/runner-20260907-122647.csv`: zero XP, 30 copper, purse 10000 -> 10030 verified | Separate max-level result, not generalized to other reward types |
| Quest 7 actual kill objectives | Same run: real Arcane Explosion kills on existing vermin; server counters 1..10; completion pays 115 copper; purse 10030 -> 10145 | GM teleport to existing spawns; no `.quest complete`, NPC spawn, or direct world edits; low-level combat balance not certified |
| Eight mage spells | `spells-first/runner-20260907-122243.csv`, 120 temporal frames, 15 per spell; GO received for 133,116,5143,168,1459,10,12051,1953 | Arcane Missiles later received FAILED_OTHER after combat/target lifecycle; no whole-suite visual PASS |
| Spell sampler correction | Old diagnostics queried the world/portal particle system, falsely reporting absent Fireball hand FX visible in frames. Corrected to the actual spell renderer, exact instance ID, including child emitters | Submitted geometry is not visible-pixel proof; mesh/ribbon checks still use shared model paths |
| Fireball corrected retest | `spell-confirm-quest-kill`, 19 samples: precast/cast/moving missile/impact PRESENT; server GO and damage; reviewed hand-glow and impact captures | Fine timing, race/sex/weapon variations and pixel-scale visual comparison still pending |
| Arcane Intellect corrected retest | Same run, 19 samples: expected cast/impact stages PRESENT and server GO | This does not certify all buff/stack/expiry behaviors |
| Quest NPC money discrepancy | `dumps/gameplay-full-game-kill-reward-20260907-122840-714.png` shows 25 copper while server awards 115. Source reads base reward directly; query contains max-level bonus | Client correction adds bonus using the driven body's level, while keeping hidden details hidden; retest below |

The initial eight-spell sampler results are retained, including its false negatives.
Channel and triggered-child visuals, stale animation choices outside active stages,
and per-model draw attribution remain diagnostic gaps. `Summarize-Live.ps1` bounds
wire events to each sampled attempt; its regression rejects a preceding success,
a different spell's GO, and a late GO. It deliberately grants no gameplay PASS.

Offline checks passed: spell animation lifecycle (5,810), missile pipeline (53),
area visual law (100,104). These are named assertions against data/logic, not that
many spells played. Debug/Release builds pass with 11 existing warnings. Core's
required possession-law source check remains blocked at the preexisting expected
NUM_MSG_TYPES 874 versus actual 876 mismatch; no Core code was changed.

##### Next execution queue

1. Quest collection from actual drops with reward choice; item-start/GO-start and
   signed GO credit. Exercise missing items, full reward bags, and repeatable turn-in.
2. Timed success/expiry, escort/defense success/death/reset, and cast-on-objective.
   Classify live script triggers before choosing fixtures; do not infer escort from
   the generic exploration/event flag.
3. Repair channel/triggered-child instrumentation, then sample channels to normal
   finish, cancellation, target death, movement and reagent/resource refusal.
4. Expand across all class, talent, racial, pet and item spell inventories; prioritize
   untouched visual/target/effect groups, preserve per-ID/rank pending states.
5. Possessed SuperUI actor quest rewards/credit, party sharing, repeat possession,
   and full dungeon encounters. Retain the existing wider map as outstanding work.

##### NPC reward correction and final validation

The NPC details and reward panels now request missing static quest data and use
the same max-level calculation as the log, with the offer packet's already scaled
base amount. The level comes from ControlledGuid. Hidden rewards stay hidden in
details and can be revealed in an actual reward offer. No Core packet changed.

`reward-confirm/runner-20260907-123329.csv` passes both display assertions for
quest 5261 at level 60: **60 copper** in details and offer. The reviewed framebuffer
shows 60 copper, and the server subsequently awards 60; purse 10145 -> 10205 is
verified. `reward-low-level` separately asserts **25 copper** for quest 7 on the
level-1 paladin, preserving the base reward. Both runners finish with zero failures.
Live hidden-reward and possessed-level variants remain pending.

Final Debug and Release builds succeed. QuestLog, client PossessLaw, gameplay
ImGui policy, SharedDocs and attempt-boundary regression checks pass. QuestLog's
existing source check expected `_questWatchCollapsed.Remove(line.QuestId)` even
though HEAD already used `hit.QuestId`; the assertion now names the actual hit
record without changing gameplay. The Core 874/876 baseline failure remains.
The final Release assembly SHA256 is recorded in `final-build-sha256.json`.

All test clients exited. The mage returned to Stormwind with earned quest money
and the three completed quests; the level-1 paladin remains at Marshal McBride
with quest 783 completed and quest 7 abandoned. No new SuperUI companions were
needed in this round. Nothing was committed, pushed, deployed or restarted.

##### Expanded live cases — 2026-09-07 afternoon

Evidence root `docs/current/checklist-20260907/`, unless stated otherwise.

| Case | Reviewed result | Limit |
|---|---|---|
| Quest 33 | Actual eight wolf meat drops followed by `collection-timed-channel` request-reward/choice 0; completed and paid 90 copper | Initial loot/select mistakes retained; no GM objective completion |
| Quest 3364 timed success | Mage accepted item delivery, delivered to Durnan, completed; reward frame 1 silver 20 copper at max level | GM travel does not certify navigation |
| Quest 3364 actual expiry | `timed-expiry-confirm`: server 0x0197 at 336.812; later completable=false, timer removed and Continue disabled in reviewed frame; abandon/reaccept/abandon cleanup | Earlier run abandoned four seconds early and is not expiry evidence |
| Evocation 12051 | `collection-timed-channel`: full 8000 ms channel start/stop; 18 temporal samples; channel body vortex and hand effects reviewed | Family-stage CSV corrects prior diagnostic channel absence, not universal visual proof |
| Eight remaining classes | `classes/results.csv`: all eight prepared and known spell rosters captured, 1,241 rows total; mage separately inventoried | Level/learn GM setup, not trainer/progression tests; internal/pet spells need filtering |
| Character create | `roster-proxy`: production pointer selects ninth lower edge correctly, creates Nbwarhuman, ten-row roster fits above Create | Race/sex/validation/delete variants remain open |
| Clinical checks | All 179 flags pass together; Core possession law passes after local checker opcode baseline correction | Offline assertions are not live game certification |

Class fixtures are now level 60 with all_myclass spells/talents: Nbwarhuman,
Nbpalhuman, Nbhundwarf, Nbroghuman, Nbprihuman, Nbshaorc, Nbwlkgnome, Nbdrunelf;
Nbmaghuman remains the main mage fixture. Gondolfo remains the lower-level SuperUI
companion. The roster is ten. Per-ID and per-rank spell coverage is still pending.

Spell family instrumentation records eight stages, triggered descendants and
attempt time bounds in `spell-family-stages.csv`; it grants no automatic PASS.
Caster stage marks are actor-specific. Shared model-path geometry attribution and
same-spell nearby casters still limit visual conclusions.

##### Escort and pet follow-up — 2026-09-07, 14:12 local

Quest945 first run (`escort-success`, name is intent not verdict) produced actual
escort death and server 0x0196 failure. Second `escort-protected` kept Therylune
alive with real Arcane Explosion casts and GM mana/placement; its fixed-duration
loop stopped before waypoint17, so objective success/reward is not certified.
`escort-until-complete` now uses authoritative objective state and a420s bound.
Live script source traces credit to waypoint17, dialogue at19; waypoints exported
read-only into `escort-waypoints.csv`. No NPC spawning or GM quest completion.

Summon Imp688 completed its6-second server cast, pet appeared with server-backed
book/bar and reviewed temporal effects. Live Firebolt pickup exposed a renderer
null dereference on empty pet drop slots; fixed and full gesture sequence passed,
including refusal/relocation and bar restoration. Details in the GI ledger.
Missing-asset triage (`asset-triage.txt`) maps18 references mostly to old/test
spells; Picnic Blanket26846 is another candidate. Reachability remains unproven,
so these are not18 player-visible visual failures.

##### Cursor ownership and capture validity — 2026-09-07, 14:32 local

`ui-inventory-bank` empirically reproduced GI-12: main carried linen at a bag
coordinate, possession of Gondolfo succeeded, and the cursor remained active.
That coordinate could resolve to Gondolfo's different item. Both grant/release
now clear carried items, split dialog and spell/macro/action cursors in the
existing body UI reset. POSSESS_LAW2.3 and its clinical check now require all three.
Live two-direction and split/action-cursor retests are in progress.

The same run passed actual pointer up/down/bottom chat button cues, held-up repeat
silence, cloth-to-head server refusal, inventory error/lock recovery and unchanged
source item. Bank opened with zero items. Its old `deposit-entry` helper searches
only the backpack, so equipped-bag cloth produced a setup refusal; the new test
uses production pickup/place with explicit bank wire coordinates.

The eight-class ability batch observed42 selected-spell GO outcomes out of45
known attempts, with three expected refusals (hunter no pet; two shaman missing
Earth Totem). Five requested spells were absent from the prepared roster. It
created315 PNGs, but24 priest images are1x1 because the hidden framebuffer reported
0x0. Those images are invalid evidence, not presentation passes. Capture now
rejects unavailable dimensions and reports failure to the runner. Recapture is
required for the affected priest spells. Exact per-class counts are in
`class-abilities/observed-summary.csv`. Early effects were visually reviewed for
warrior/paladin/rogue; all spell ranks, durations and conditions remain open.

##### Wanted poster, pointer input and resume — 2026-09-07, 18:25 local

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

##### Live object portrait and death recovery — 2026-09-07,18:32

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

##### Dungeon encounters and readable UI — 2026-09-07,19:11 local

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

##### Exploration chain and title retest — 2026-09-07,19:21 local

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

##### Fishing interaction, attachment and lake investigation — 2026-09-07 evening

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

##### Fishing visuals passed; water diagnosis corrected — 2026-09-07,19:47 local

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

##### Cooking, First Aid and channel handoff — 2026-09-07,20:01 local

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

##### Skinning, campfire requirements and primary cap — 2026-09-07,20:08 local

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

##### Mail ownership and named requirements — 2026-09-07,20:14 local

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

##### Mail reset retest and server ownership defect — 2026-09-07,20:18 local

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

##### Pointer vendor and gathering round — 2026-09-07,20:26 local

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

##### Gathering retry, smelting and repair — 2026-09-07,20:32 local

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

##### Flight2->4 and insufficient-fare refusal — 2026-09-07,20:39 local

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

##### Possessed flight and acquired-letter chain — 2026-09-07,20:51 local

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

##### Stockades chain, boss observations and area label — 2026-09-07,21:03 local

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

##### Instance fallback live verification — 2026-09-07,21:08 local

instance-area-retry enteredmap34 and reportedarea717/displayzone717 at31.681;
reviewed210419 screenshot shows The Stockade on the minimap. Returnedmap0 reports
area1519 at36.965 and reviewed210424 shows Stormwind City. Original login failure
is retained separately. The zone splash duplicated the same WMO name for zone and
subzone; added equality guard, rebuild/retest pending after the current quest.

Detailed Stockades spell limits: Targorr3391 fired465.608; Dextren11976 hit555.912
and574.694, while19134 twice had resisted targets (fear mechanics not passed).
Kam8242 applied641.148 and removed643.084. Spell target IDs must be checked per
caster; other Defias units also cast9128, so no blanket boss-spell attribution.

##### GI41 targeted consumable wire — 2026-09-07,21:17 local

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

##### Targeted charm live result — 2026-09-07,21:20 local

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

##### Delayed mail and early splash retest — 2026-09-07,21:26 local

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
##### Enchanting setup and GI42 — 2026-09-07,21:46 local

cloth-enchant priest learned Tailoring3911 and Enchanting7414 through trainers.
Linen2589x2 was a GM reagent fixture; craft2963 produced2996x1 and Tailoring1->2.
CopperRod6217 bought for124c; purse647->523. Dust/essence vendor purchases correctly
failed for insufficient money. Provisioned only missing dust/essence;7421 produced
RunedCopperRod6218 at246.268 and Enchanting1->2. The first screenshot named
runed-copper-rod-crafted was a failed missing-reagent attempt, not craft proof.
Cast frame camera overlapped the vendor/interior, so no clear world-animation pass.

Diagnostic profession open has null opener provenance and cannot certify native
panel behavior. Closed it fully, reopened with actual Enchanting7411; C still failed
(native-enchant-character214032). Closing Enchanting first allowed Character;
opening Enchanting second retained both. Confirmed asymmetric GI42. Added a separate
registered-left coordinator after bounded pair UnsupportedShape; native close
callbacks, actual final census, pushable seats, menu/fullscreen/death gates retained.
Both builds11warnings0errors; UiPanelObserver and client/CorePossessLaw PASS.
New enchant-target Release screenshot214502 reviewed: C opens Character left and
retains Enchanting center. Equipment enchant and other native panel variants pending.
##### Native profession pointer results — 2026-09-07,21:55 local

GI42 native7411 then C now retains Craft center/Character left (214502).
GI43 actual row pointer clicks reproducibly left the first recipe selected, even
with slow pointer/down/up waits (214741). The whole-list InvisibleButton owned
clicks before rows. Replaced it with window-qualified passive wheel hit testing;
both builds11warnings0errors and ProfessionFrame PASS. Build hash in
build-profession-pointer-sha256.json. Rebuilt actual recipe selection215023 changes
to7418 and enables Create. No diagnostic recipe selector supplied the target.

ThinClothBracers3600 was one explicitly GM-provisioned gear fixture and normally
equipped. In enchant-pointer, Create arms item targeting; actual boots click
refused90.338 without consuming reagent, wrist click started91.759. GO96.585,
dust1->0, health1717->1722, Enchanting2->3, green Health+5 tooltip reviewed215134.
Hand precast and held animation123 then standing cleanup reviewed; short release
not isolated by the sparse frames, so not a full spell-stage pass.

Provisioned missing dust1/essence1 for7428. Actual replacement popup named Health+5
and Defense+1 (215202). No preserved both reagent counts and existing enchant.
Reopened on wrists and actual Yes accepted; GO204.316, both reagents consumed,
Enchanting3->4, health returns1717, Defense+1 tooltip reviewed215322.
Profession craft-send diagnostic incorrectly says REFUSED while an item cursor is
successfully armed; actual cast/wire/item/state evidence establishes success.
No blanket profession journal PASS. Tailoring ordinary recipe pointer next.
##### Tailoring and repeatable donations — 2026-09-07,22:05 local

Native Tailoring3908 tree selection and Create3915 consumed the earlier linen
bolt plus normally purchased2320 thread (10c). Server craft428.245 produced
custom variant1099394, not base4344. The base-count/equip runner failures are
fixture assumptions; actual variant count and equip succeeded481.904/482.120.
Tailoring2->3. No clear shirt-only world view claimed because the robe obscures it.

Before prerequisites,7795 absent from ordinary greeting; diagnostic query returned
details, but Accept refused486.394 (0x018F reason0, readable chat). Donated60 each
2592/4306/4338 via authorized GM materials and normal reward handlers, completing
7791/7793/7794. Early wool completable assertions used diagnostic query/Detail, so
are SETUP_FAILED, not a progress-panel bug. Actual Silk/Mageweave greeting clicks
returned Progress and missing/sufficient-material assertions both passed.
7795 then appeared in actual greeting;100Runecloth fixture,60 consumed; actual
reward676.829 paid39600c,34113->73713. First repeat7796 completed754.992 and second
781.797; each consumed20cloth, both paid0 and remained repeatable. Third with no
cloth returnedcompletableFalse785.484. Reviewed Stormwind hover between repeats
220250 shows1715/6000, after220303 shows1770/6000: +55 with human racial bonus.
Quest collection and navigation are not certified by provision/GM travel.

Enchant-pointer ended normally, all runner failures preserved. GI43 native recipe
selection passes in both Craft and TradeSkill panels. New small diagnostic fix
records an enchant item cursor as ARMED rather than REFUSED; both trays built and
ProfessionFrame PASS. Hash build-profession-target-state-sha256.json.
Next paladin defense uses item6776->1649->1650->1651 with actual existing Duthorian
39536 and Daphne66979. Saved live westfall.cpp excerpt and script_waypoint6182:
three attack waves3/4/5 raiders6180 at waypoints7/8/9, rifle pickup4/return13,
actual quest credit17. No persistent spawn or GM completion commands.
##### Paladin defense and spell reward — 2026-09-07,22:26 local

valor-defense: GM-provisioned actual starting item6776; item quest1649 accepted,
Duthorian turn-in paid240c (1102->1342).1650 accepted and delivered to Daphne,
paid1140 at398.285. Daphne moves on normal waypoint path; old spawn placement was
12.98yards away and correctly refused. Even a2s wait after anchoring allowed her
to move5yards; immediate actual-position interaction succeeded. Keep these fixture
failures distinct from NPC interaction success.

First1651 acceptance398.953 was followed by my invalid `escort` token; actual
protection loop did not run, Daphne died, and quest failure0x0196 arrived434.250.
This is a harness-caused failed encounter, not a server defect. Abandoned normally.
A retry was interrupted before acceptance by network failure; no server restart.
Consecration20924 initially correctly refused as unknown. First GM learn targeted
an NPC and returned Player not found; later self-targeted learn is a spell fixture,
not a talent/progression pass. Retry used already-known rank1 Consecration26573.

valor-retry reconnect retained1650 completion and allowed1651 again after natural
Daphne respawn. Summoned only SuperUI Nbmaghuman and Nbprihuman. Accepted96.163;
normal26573 casts roughly9s apart damaged raiders while companions fought/healed.
Repeated early casts correctly refused cooldown. Script dialogue132.092/169.555/
214.125 marks all three attack rounds;230.672 puts the gun away. Actual quest credit
250.855; bounded observer passed. Turn-in310.320 paid600c,2566->3166.1652 delivery
atDuthorian462.177 paid1560c,3166->4726, received custom reward1084647 (base9607),
and server spell5503 targeted the paladin. New5502 learning needs before/after
known-spell comparison; do not infer it merely from reward-cast5503. Both companions
dismissed and confirmedoffline before mage mail login.1653 not accepted.

Reviewed escort126 capture shows living Daphne, combat/healing and ground effect.
Some later captures show the GM-followed actor intersecting sloped terrain; these
are not proof of ordinary player navigation, and NPC path/terrain presentation
still needs an independent view. No visual path pass or geometry fix claimed.
The apparent bow silhouette was investigated: Core SetVirtualItem takes item ID,
6946 is Monster-Gun,Club and display13455 resolves Firearm_2H_Rifle_A_01.mdx. The
unused enum2511 does not establish an incorrect weapon; no Core change made.
Current build-profession-target-state hash; both trays11warnings0errors and
ProfessionFrame PASS. Mage normal login cod-receive is checking delayed COD now.

##### COD confirmation amount and receipt — 2026-09-07,22:40 local

cod-receive normal mage saw1868,117x5,COD10c and returned letter1867. Actual icon
opened the letter; clicking sender text is outside the reference button and was
not counted as input failure. Actual attachment click showed cost caption but no
amount in223252 capture, while tooltip correctly showed10c. Cancel preserved
29357c and117 count0; zero runner failures. Original cod-receive ended normally.
Mounted patch.MPQ StaticPopup.lua/xml explicitly updates COD, DELETE_MONEY and
SEND_MONEY MoneyFrames. GI44 restores native centered denomination display for
all three; MailFrame check guards amount sources and draw route. Both trays
11warnings0errors, MailFrame/client PossessLaw/ImGui checks PASS.

cod-fixed reviewed223653 popup shows10c. Actual Cancel again preserved item0.
Reopened and accepted: take-item SUCCESS91.826,117x5 received in bag1slot16;
mail1868 refreshed item0/count0/cod0. Reviewed223751 shows purse29347c, exactly
10c deducted. Backpack was full but other bag space existed; no full-bags refusal
claimed. Actual Delete then removed emptied letter. Sender payment receipt and
send/delete-money popup variants remain in progress. Hash build-cod-amount.

Core law initially failed because its stand-state assertion required _player;
read-only source shows Player* actor=GetSuiActor and actor frozen check. Updated
only local law script to require this resolution plus actor freeze gate. Re-run
PASS. No Core source edit/deployment/restart. Other source actor paths were not
certified by this narrow assertion correction.

##### Mail proceeds, amount fields, and lighting comparison — 2026-09-07,22:52 local

cod-seller normal warrior inbox1869 contained10c fromNbmaghuman. Actual attachment
click produced take-money SUCCESS114.406;143->153c. Emptied payment letter deleted.
Zero runner failures. Skin comparison outdoor224351 and inn224552 reviewed: same
body/equipment is moonlit blue outdoors, warm tan inside beside NPCs. Missing skin
texture diagnosis is not supported; no appearance change made. GM placement is
setup, not a navigation pass.

cod-fixed had external NETWORK_FAILED224026 before the queued SendMoney cancel/
accept steps ran. Its reviewed12345c popup is presentation evidence only. No money
was sent in that interrupted run. GI45 amount fields clipped23/45 under generic
text/padding insets. Mounted MoneyInputFrame.xml has zero authored text insets.
Opt-in zero inset/padding on the existing numeric input helper leaves other callers
unchanged. Both trays11warnings0errors; MailFrame,ImGui,clientPossessLaw PASS.

mail-input reviewed224946 shows full1/23/45 values. Actual Send popup then Cancel
preserved the form/balance; actual reopen+Accept sent12345c toNbwarhuman with30c
postage129.706, serverSUCCESS129.823. Screenshot225120 reviewed, purse16972c:
29347-12345-30. Sender and recipient are the authorized test account. Return and
returned-money deletion prompt next. Build-mail-input-sha256.json.

Delivery correction,22:54: mail-return normal warrior inbox empty. Read-only
MailHandler.cpp423 explicitly delays either item OR money mail by configured3600s;
only text-only mail is immediate. The12345c mail sent22:51:18 is due around23:51:18.
No early-delivery defect inferred; return/delete-money retest remains pending that
actual arrival. COD proceeds1869 used the immediate server payment path and were
successfully collected separately. mail-return ended normally.
Next fixtures: rogue Alchemy service2275 (Lilyssia5499guid79832), Engineering4039
(Lilliam5518guid37611), both10c/level5, from read-only live trainer/spawn exports.

##### Alchemy, Engineering, and destination items — 2026-09-07,23:40 local

alchemy-engineering normal Nbroghuman started as a saved ghost; failed initial
trainer selection was a fixture problem. Self .revive restored the test body.
Native trainer purchases learned Alchemy2275 and Engineering4039, with separate
first/second-primary profession confirmations and10c each. Rogue now346c.
Missing recipe materials were GM fixtures, not gathering/vendor evidence.

Alchemy2330 crafted118, skill1->2; full230048 capture reviewed two held bottles.
Second cast was movement-interrupted: CANCEL_SEND399.090/INTERRUPTED399.299,
productcount1 and all three reagents preserved. Retry GO406.004, skill2->3.
Potion118/439 use consumed one, floating +89 reviewed; final HP includes regen
and is not a151-point heal. Immediate retry correctly refused item cooldown.
Late potion bottle cleanup is compatible with its authored6.667s clip; clean
unoccluded full drinking sequence remains open.

Engineering3918 actual CreateAll consumed two RoughStone and created two powders,
actual UI skill1->2->3. Queued skill diagnostic repeats stale before/after1->2;
UI3 is authoritative, diagnostic delta remains a known limitation. Recipe3919
used both powders+one provided linen, created4358x2, skill3->4. Full crafting
frames exist but are not all reviewed. Guessed(-9465,80,56) placement was inside
WMO and excluded. Normal W movement from known road point established clear
(-9465,59.8568,56.1137), used for later item tests.

GI46 original4358 right-click immediately sent mask0 and4054 BAD_TARGETS965.607.
Ground intent now binds actor+itemGUID, arms without sending/starting cooldown,
re-resolves the same copy after a bag move, and emits destination mask0x40 only
at commit. Debug/Release, UseItem, client/Core possession, TacticalFreeze and
CooldownProtocol checks passed. First dynamite-ground run armed correctly but
ended NETWORK_FAILED; retain that failure. dynamite-retest zero runner failures:
actual right-click armed, Escape cleared and preserved2. Rearmed, moved original
stack to backpackslot7, commit sent current wire slot30 at55.893; START55.936,
GO56.954 mask0x40, count2->1. Rearm then SuperUI priest possession cleared both
cursor/intent, bodyrelease restored rogue, priest dismissed and confirmedoffline.
Freeze cancellation still requires live check. Ground commit used castground,
not real world-pointer input; GUI glue only injects ImGui and cannot prove that
world input route. Reviewed aiming circle/hint and wind-up captures.

GI47 uncovered by the empty-ground throw: server GO hits0, no projectile/arrival
sound because ApplySpellGo only iterated unit hits/misses. Upstream Benilla
creature_anim/spell_visual.rs and entities/missile.rs confirm one fixed-point
missile only for an empty unit list; area-kit1704 arrival has sound38 and no
body/effect slots. No unsupported ground explosion is being invented. Separate
no-missile destination GO burst (e.g. Flamestrike33) is also missing locally.
Added normal release/timing fixed destination, positional area sound arrival,
and authored field12 free burst when field6==0. Debug passes11warnings0errors;
scoped runtime checks and Release/live retests in progress. Player-facing entire
spell corpus and all28 coverage areas remain open.

##### Destination visuals and hidden world pointer — 2026-09-07,23:48 local

GI47: both Debug/Release11warnings0errors; missile runtime57 checks and area
lifecycle/distribution100110 checks pass. dynamite-visual zero runner failures:
4054 START74.878/GO75.780; actual fixed-point missile sampled at progression
0.088/0.327/0.724, then disappears, item1->0. Sound journal records area kit38,
Sound/Spells/Immolate.wav. Full flight frame233954 is foliage-obscured: movement
is instrumented but that camera does not certify the projectile's visible shape.
Corrected potion439 repeat: reviewed234105 bottle at mouth/heal+79, reviewed
234112 empty hand/clear effect. Timed samples show Anim61 then Stand, bottle
asset lasts its authored~6.667s and is absent at153.057/154.125. No expiry fix
needed; count1->0. Rogue now no118 or4358, healthy after regeneration.

The optional ClientWindow PointerInputSource bridges supplied pixel/button
states into its normal Begin/EndWorldPress queue, observes GUI capture and drag
thresholds, and never calls native cursor capture. `glue world-pointer-on/off`
is explicit in diagnostic protocols; ordinary play has no source configured.
It shares GUI pixels/buttons, so the shipping ground unprojection and latched
press routing can now be tested. Camera look remains outside this input mode.
Both builds11warnings0errors and clientPossessLaw PASS. Readme documents mode.

world-pointer normal mage: .learn2120 replied alreadyknown, so existing rank1
used. Actual supplied pointer1040,420 produces terrain point and reviewed rune
234527. World right-click cancels; rearm and actual worldleftpress/release starts
2120 at154.712, GO157.616, point(-9469.89,67.64,56.26). Reviewed234619 shows burst
and burn at destination. Ground burst exists before DynamicObject loop in samples;
reviewed234628 is visibly clear after burn ends. This is rank1/outdoor/empty-ground
input/presentation evidence, not all ranks or NPC-damage proof. UI capture and
range refusal checks in progress; mailreturn due23:51 still pending.

##### Returned money warning — 2026-09-08,00:04 local

money-return-live normal warrior received1870 after scheduled delivery,12345c.
Actual Return140.950/SUCCESS140.983; warrior purse153c unchanged. Zero failures.
Read-only Core Mail/Mail.cpp252 confirms money-only return has no delivery delay.
money-delete-check normal mage received1871 plus oldtext1867,12345c intact.
Actual Delete opens warning,235654 reviewed: GI44 amount correct1g23s45c, but
warning sentence clips at right edge (GI48). Cancel preserves letter/funds;
run ended normally. Do not claim money collected or destroyed. Mage16972c and
returned12345c still in mailbox for corrected-warning retest.

Mounted StaticPopup.xml specifies GameFontHighlight,290 textwidth,top16;
StaticPopup.lua widens alert dialogs to420 (ordinary320), wraps FontString text,
and derives popup height/buttons from textheight, adding16 for money. Replaced
fixed360x96 single-line mail geometry with that shared content-height law, wrapped
message, native font color and amount/buttons below text. Updated parity observer
to actual dynamicframe. Initial Debug found one stale observer reference; fixed,
Debug now11warnings0errors. Scoped checks and Release/live retest pending.

##### Mail warning live closure — 2026-09-08,00:18 local

money-wrap-live Release: reviewed000748 complete DeleteMoney sentence, native
alert,12345c coins and readable buttons. Actual Cancel preserved1871. Native
money attachment click sent561.202/SUCCESS561.336; purse16972->29317c reviewed
001549, letter now money0. Native delete then succeeded591.510 on emptied1871.
Ordinary send-money popup reviewed001648: native320 width, two complete lines,
1g23s45c and both buttons fit. Cancel leaves compose amount; no second send needed.
Debug+Release11warnings0errors and MailFrame/UiPanelObserver/ImGui PASS from
build-mail-wrap-sha256. These close GI48 in tested English/current-scale variants;
COD amount was previously live verified, its new wrapped variant not repeated.
Old empty text1867 remains; body-copy letter behavior still untested.

##### Ground pointer, freeze and two new defects — 2026-09-08,00:25 local

Normal rogue ground-item-pointer used GM-provided4358x3 (not crafting coverage).
Actual bag right-click armed4054, world1040,420 clicked production ground point
(-9469.466,67.654625,56.24729). SENT67.774/START67.856/GO68.757, mask0x40,
count3->2. Sequence missile69.358 progress0.792, disappears70.408. Full flight
001913 reviewed but too early in authored release to show the traveling object;
shape/trajectory approval still open. This closes actual world-pointer commit.

Command View Ctrl+F: actual bag use armed the same actor/item, right click failed
to cancel.001955 screenshot reviewed rune still visible; assertion67 FAIL and
no item use. This is GI49: the free-view branch intercepted the live cursor.
Reordered live cursor before RTS click routing, preserving queued cursor path,
actor/item identity and freeze gates. Law assertion added. Live retest pending.
Native Freeze button1088,856: reviewed002029 shows authoritative47-unit lock,
cursor/intent cleared and4358 count2 preserved. Native Resume reviewed later;
count remains2, no delayed throw. Run terminated normally with the single genuine
cancellation failure retained.

GI50: previous world-pointer mage out-of-range389.619 refusal had a green rune.
Local Benilla target/reticle.rs and ui_action/targeting/cursor.rs cite reference
CheckGroundPointInRange/0x4820f0: range from actor, native red Unacceptable texture,
1.3888889 fallback size outside range, +/-2 vertical projection slab,20 radius cap,
first2 radius indices. Added range cursor/decal feedback from actor pose/modifiers;
server still validates clicks. Rendering native cast cursor replaces text hint.
Generic all-effect radius catalog remains for affected-area consumers; native
reticle gets its own first2-slot derivation. Builds/checks/live pending. Initial
build restore couldn't read sandboxed NuGet config; retry uses existing restore.

##### Ground feedback live closure and item-category defect — 2026-09-08,00:37 local

ground-feedback build-ground-feedback-sha256: Debug+Release11warnings0errors,
client/CorePossessLaw,UseItem,TacticalFreeze,ImGui PASS; radius784checks PASS
(includes5 new boundary groups; census counts are not784 independent live cases).
Rogue Command View actual bag arm reviewed003043 green native Cast cursor;
right-click reviewed003044 clears rune/intent, count2 unchanged. Rearm/worldleft
SENT148.530/START148.563/GO149.364, count2->1.003129 reviewed small red projectile
visible near948,422, but a single still does not establish lobbed trajectory.
Diagnostic main mixer reports Fall/Missing because Command View renders actor
from entity stream; do not treat that as proof of actual missing animations.

Main body range pointer1050,260,(-9500.287,78.32033,57.187943): reviewed003135
small red Unacceptable marker; acceptableFalse. Click211.064 -> server
OUT_OF_RANGE211.133, no consumption,1 remains. Green/red input cases closeGI50
for this outdoor main-body item. Other scales, water, indoor/possessed and
spell-radius edge cases remain coverage work. GI49 main-body Command View
cancel/commit passes; previous failed right-cancel run retained.

GI51: immediate use after successful Command View throw was incorrectly armed
154.619. Repeat with GM1item then ordinary own-body throw: GO~340, item2->1;
rearm341.635 and sent342.519 -> serverNOT_READY342.602, count1 unchanged. This
rules out a camera-transition-only defect. Gate queried Spell.dbc category0
while item4358 authors category24/60000ms. Native bag cooldown swipe used24
already. Added IsItemOnCooldown preserving spell GCD/exclusion but querying
item category, wired both inventory and command-shelf gates. Inventory gate,
recovery and swipe now explicitly use ActionsFor(ControlledGuid), consistent
with actor-owned bag display when other bars inspected. Added cross-explosive,
unrelated-potion and exact-expiry checks plus possession assertion. Builds and
live retest pending. Run ended normally, zero runner failures; server refusal
retained as confirmed client-gate defect. Rogue1dynamite remains.

##### Item-category live verification — 2026-09-08,00:43 local

item-category normal rogue build-item-category-sha256: GO34.573 consumes2->1.
Actual second bag right-click35.974 gives LOCAL_ITEM_COOLDOWN; reviewed003954
red readable error, dark cooldown item, no targeting cursor. No repeat use wire.
After recovery expiry, item arms again. Out-of-range attempted144.245 refused
by server and preserves1; immediate re-arm succeeds (no stale local recovery
from rejected cast). Escape clears it. All10companions reported,0summoned.
Run ended normally with zero runner failures. Debug/Release11warnings0errors,
CooldownProtocol/UseItem/ImGui/client+Core possession PASS. GI51 closes for this
main-body bag path; shelf and inspected-other-body variants have shared-query
and source assertions, not separate live passes.

Added explicit --native-spawn diagnostic option to retain server starting/saved
position instead of default arena fixture. Character-select protocols can enter
world without that teleport. README documents it; diagnostic roster inspect now
records GUID and level as well as race/class/sex. Both builds pending for final
harness state. Next native roster/deletion/new female GnomeWarlock.

##### Character lifecycle and focused name filtering — 2026-09-08,00:53 local

character-lifecycle build-native-spawn-sha256, actual10-row roster reviewed004411:
all names/rows fit above Create button. SelectedNbwlkgnome maleGnomeWarlock60,
GUID224. Allcompanionsoffline beforehand. Delete prompt004456 correctidentity,
emptyOkay disabled/click cannot delete; typedDELETE004537 armsOkay; actualCancel
preserves10 and selectedwarlock. Reopened/typed, reviewed004623 sameidentity,
actualOkay -> SMSG_CHAR_DELETE0x39,9rows. This deletion is completed; replacement
has NOT yet been created. No other character was deleted.

Create initialHumanMaleWarrior reviewed004708. NativeGnome/Female clicks004757,
classWarlock and hairstyle/color/earrings next005? reviewed004900: female model,
red robe, brown pigtails, silver earrings; dials1/1/2/2/2 (display1-based).
Name input N1@ stays visiblyN1@ despite filtered internalN,Acceptdisabled. GI52
confirmed focused editor/request mismatch: post-draw WriteBuf doesn't sync active
ImGui input. Local Benilla char_create/mod.rs says shared edit feed filters letters
and12cap on type/paste. Moved validation to CallbackCharFilter; native13-byte
buffer includesterminator. Pure alphabet exclusions added. Build/live pending.

Actual Ctrl+A replacementNbprihuman (existing testpriest) preservedname visibly;
Accept returned0x31, reviewed005104 readable That name is unavailable,9rows,
creation remainsopen. Run endednormally, noactualnewcharacter created yet.
Reopen corrected client, confirm9persistedrows, repeatfemaleappearance/name
filter+12cap, createNbwlkgnome, then native startingarea and savedlook.

##### Female starter creation verified — 2026-09-08,01:06 local

character-create-fixed runs the corrected Release (build-create-name-sha256.json).
Both Debug/Release builds:11 warnings,0 errors; CharCreate,CharSelectCurrent and
ImGui checks pass. Reviewed005521 shows N1@ filtered visibly to N;005607 caps the
active editor at12 characters. Replaced with Nbwlkgnome, actual Accept received
create0x2E. New female Gnome Warlock level1 GUID0xA2 replaces deleted old male60
GUID224. All10 roster rows fit;005746 shows selected new character matching the
creation preview: brown pigtails, silver earrings, red robe, dials0/0/1/1/1.
Natural world entry at(-6240.32,331.033,382.75824),43HP/200mana; no GM leveling,
items or teleport. Native-spawn runner skips its usual fixture teleport. Initial
camera inherited80yd distance and a tree blocked its view; camera90/20/8 gives a
clear reviewed005949 view. Cinematic101 was explicitly acked/skipped by existing
client code; introductory cinematic coverage remains open. Ordinary W movement
3.15s reaches Sten within3.27yd, reviewed010542 greeting with two available quests.
No fresh-session relog yet; all28 full-game areas remain open.

##### Starter quest notification correction — 2026-09-08,01:19 local

character-create-fixed ended normally with one harness failure: loot request ran
while the death transition had cleared selection. Native corpse right-click
010944 successfully looted750x1; second wolf added another. SelectionRingLaw
intentionally clears alive-to-dead targets and permits reselecting corpses; fixed
its contradictory comment only. The combat runner now uses CanActorAttack for
neutral creatures and recognizes observed death despite that normal target clear.

First native meat loot010944 visibly showed center0/8 versus tracker1/8 (GI53).
ADD_ITEM933.834 preceded actual item receipt933.884. Replaced own immediate bag
read with complete-object-slice item progress observation, following local
benilla-app ui_quest_log.rs/net/apply/quests.rs. Loading seeds silently; companion
notices continue from their owned quest facts. Debug/Release11warnings0errors.
QuestLog,PartyQuest,PossessLaw,GameplayImguiPolicy,SelectionRing individually PASS;
Core possess-law-check PASS. Multi-flag interface-wire-check stops at the first
recognized flag, so these were executed separately.

quest-progress Release hash build-quest-progress-sha256.json, PID36708. Native
relogin011742 retained female look, level1, position(-6245.92,363.542,384.56458),
starter bars, quest179 and two meat. No initial item-progress notice. Third normal
wolf kill63.568 gave50XP (100->150); actual corpse right-click89.692 obtained750.
New notice89.991 current3/8 and reviewed011900 center/tracker both3/8. Final
completion/reward and broader item-progress variants remain open. No fixture GM
items/levels/teleports used for this starter progression; all28 areas remain open.

##### Native collection completion and progression defects — 2026-09-08,01:38 local

quest-progress ended normally, zero runner failures. Native wolf kills and corpse
right-clicks brought179 to8/8: notice1114.736, reviewed013605 matches tracker8/8;
minimap quest-source pins disappear and turn-in question mark appears. Ordinary
nonquest drops leave the notice silent. One sixth-kill corpse expired during
inspection before its later click; no loot pass for that attempt. Fresh-session
appearance/bars/quest retention reviewed011742 (GI52 narrow persistence closure).

Natural level2 packet626.858 gains15HP/38mana and stats0/0/1/2/1; descriptor626.925
updates43->58HP, XP350->0,next900. Reviewed012757 gold pillar,012758 fading spark,
012801 clear. Sound journal has one LevelUp.wav kit888 row at57524.687, actorA2;
model-authored audio verified by route, not a human listening claim. At this ding,
chat incorrectly announced GENERIC(DND),Demonology,Destruction,Racial-Gnome to10
(GI54), and omitted native level/health/mana/stat chat (GI55), showing invented
center text instead. Mounted ChatFrame.lua1283 and local Benilla skill watcher
confirm the expected messages and race/class SkillRaceClassInfo flags0x402 gate.

Added SkillLineCatalog.AnnouncesSkillUps with conservative absent-row behavior;
weapon/Defense/profession gains remain allowed. Added LevelUpChatLaw from mounted
GlobalStrings, native order/positive-stat gates and level10 talent point. Removed
invented green center level message; descriptor visual/model sound untouched.
Mounted skill silence/positive controls and mana/nonmana/talent-boundary checks
PASS within SkillFrame. Both Debug/Release10warnings0errors (removed redundant
nullable skill-catalog load warning); separate GameplayImguiPolicy PASS. Live
retest pending in starter-rewards PID45100, build-progression-feedback-sha256.json.
New female warlock is level2,58HP,8meat, quest179 ready, still near wolf field at
(-6245.9053,372.2953,383.70435). All other account characters and Core untouched.

##### Quest reward names and hit clipping — 2026-09-08,01:53 local

starter-rewards native movement5.4s then1.8s returned to Sten,3.01yd. Correct
question mark and greeting reviewed014012,8meat Continue014112, actual Continue
opened Reward014156. First-column Rabbit Handler Gloves hover014259 and click
014411 both fail; Complete remains disabled. GI57: ItemGridInset=-3 places the
native button just outside the scroll's left clip, so full-containment rejects it
entirely. Changed actual InvisibleButton bounds to item/scroll intersection,
including partially visible rows; no offscreen hit region exists to steal Abandon.
GI56: names overflow boxes in same frames. Mounted patch.MPQ QuestFrameTemplates
Name FontString is90x36,GameFontHighlight,left-to-NameFrame+15,vertical center.
Restored that box, shared scalar-aware wrapping, ellipsis height fit and clipping.
Pure checks cover left inset, partial bottom, wholly hidden rows and long words.

Initial QuestLog check failed its renderer structural prohibition on inline
new Vector2; moved the placement to existing name vector with updatedY. Retest
QuestLog PASS. Failed test process45652 was explicitly closed; unrelated older
interface-wire-check36840 was left untouched. Debug/Release builds and separate
client/Core possession and ImGui checks pass (10 existing warnings when compiled).
No reward has yet been selected or received. starter-rewards ended normally with
one invalid harness enum assertion (Offer instead of Reward), corrected later;
actual panels remained functional apart from the identified first-column defect.
GI54/GI55 live level-up verification remains pending after these reward fixes.

##### Starter reward and readable memorandum — 2026-09-08,02:08 local

quest-reward-fixed ended normally,0runner failures. Reviewed015428 boxes contain
all names,015904 hover tooltip,015905 first-column selection enables Complete.
Actual Complete330.613, reward330.796, glove1079850x1 received330.763 and asserted;
meat750x8 removed, XP176->256 (+80), money0 unchanged; quest179 removed330.830.
Follow-up markers/greeting015945 and native acceptance of3115/233 reviewed.
Source items9577/2187 each1 asserted. Right-click glove equipped the custom hand
texture, atlas13regions/0missing. GI57 scroll-hidden actual regression still open.

Right-click memorandum9577/page2450 exposed GI58 in020250: literal $B$B and $n.
Benilla ui_item_text.rs feed_item_text applies the shared NPC text expander before
publishing both page/letter text; mounted patch.MPQ ItemTextFrame.lua wraps that
result. Reader now calls ExpandQuestText before ComposeBlocks, retaining the
controlled-body identity and world-state source already used by quest panels.
Regression covers paragraph/name/class/gender expansion plus the renderer seam.
Debug/Release10warnings0errors; ItemTextFrame, ImGui, client/CorePossessLaw PASS.
New starter-training run uses build-item-text-sha256.json; live retest pending.
The new female warlock remains level2, no GM fixtures, all companions offline.

##### Anvilmar, training introduction and quest scrolling — 2026-09-08,02:25 local

starter-training ended normally,0runner failures. GI58 native read020724 shows
paragraphs and Nbwlkgnome resolved, complete page fits. Natural approach entered
an exterior collision pocket(-6146.702,366.46964,400.1354): back alone stationary,
jump then back recovered to(-6155.673,367.24847,399.95886). Trace
movetrace-anvilmar-wall-snag.csv retained; cause and reproduction remain open.
Further normal movement reached vestibule(-6126.643,386.26425,395.54224), but
safe onward route not established. Recorded first GM position fixture for this
new character: .go xyz -6053 391 399 0, then native Alamar460 greeting4.21yd.
No GM levels, money, items or quest completion used. Interior frame021542 reviewed;
fixture does not establish doorway/navigation correctness.

Tainted Memorandum3115: actual partial item hover021718 works; Continue hover
021719 has no item tooltip and click reaches Reward021720. Complete650.228
consumes9577, grants40XP,256->296 and unchanged0c. These are partial-row/Continue
cases; exact overlapping offscreen click regression remains open. Native1599
Beginnings detail021803 is long. Drag384239->384520 did nothing021901; arrow
worked021945. GI59 confirmed: NPC and quest-log detail thumbs had no input.
Mounted UIPanelTemplates.xml defines Slider+OnValueChanged. Added track handlers
for both, shared inverse pointer mapping with endpoint/midpoint/outside/no-overflow
checks. Debug/Release10warnings0errors, QuestLog/ImGui/client+CorePossessLaw PASS.
quest-scroll-fixed launched with build-quest-scroll-sha256.json; live retest pending.
1599 not yet accepted;233 remains active. Level2 XP296/900, money0, gloves equipped.
GI54/GI55 still require an actual level-up after their fixes.

##### Starter training, scrolling and combat-condition correction — 2026-09-08,02:55 local

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

##### Damage-induced death and native recovery — 2026-09-08,03:03 local

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

##### Delivery chain and natural level-three retest — 2026-09-08,03:19 local

Quest233 native reward2273.016 consumed2187, XP414->604 (+190), purse72 unchanged.
Native follow-up234 acceptance provided2188x1. Walked from Talin(-6222,684) to Grelin
using25.5 seconds of normal W movement, arrived3.824yd; trace talin-grelin-natural.
Quest234 native reward2487.690 consumed2188, XP604->874 (+270), purse72->79 (+7).
Both Progress/Reward panels and source item/reward displays reviewed.

Leaving Grelin's tents hit several walls; eventually ordinary west movement exited
the first tent, and a later south/east attempt entered the neighboring tent. These
captures do not prove a trapping defect. Close camera visibility remains a lead.
Used recorded GM positioning to resume combat. First wolf fixture(-6333,523,387)
overlapped solid object geometry (downward hit388.57, steep normal, terrain386.44);
wolf reset/evaded and no XP was granted. Retain that failed fight and misnamed
level-three-transition/chat frames as SETUP_FAILED, not a level-up. Probed three
alternatives;(-6325,512) has no overhead collision and terrain386.333. Valid frame
031622 reviewed before fighting. GM movement is not navigation evidence.

Normal Shadow Bolt686 killed wolf705 with god mode off and no companions at3129.125.
44XP gave level2->3, XP874->18, next900->1400. Level-up packet3129.133 reports15HP,
24mana, stats1|1|0|1|1. Actual descriptor73HP, stats16|26|21|29|24. Reviewed031718/031719
chat correctly names level3, HP/mana then Strength, Agility, Intellect, Spirit;
no zero Stamina, premature talent, or internal class/racial skill spam. Native gold
LevelUp.m2 effect reviewed and LevelUp.wav route completed. GI54/GI55 pass this
live mana-class/sub-talent-level case; non-mana, level10 and possessed variants
remain separate. Current build quest-scroll hash unchanged. All28 areas open.

##### SuperUI port discrepancy and spell reward UI — 2026-09-08,03:32 local

GI60: verified priest summoned3163.169; main GM port3280.598 from(-6325,512) to cave
(-6540,374). Reviewed031951 already shows priest there. Server03:19:50 explicitly
logs257.7yd separation, re-ground and258yd party-catchup teleport. Contrary to the
capture's provisional name, the helper did NOT hold. He killed novice1479 at3290.740,
XP18->20. The later explicit .namego was sent3298.596 and did not cause the first
relocation. Subsequent nearest-living select picked lower-layer1489 through the floor;
30-second attack attempt had LOS refusals, no kill, and remains failed. Looting the
actual nearby1479 corpse yielded6753x1;3331.022 toast/tracker/count all3/3. This proves
quest loot with a SuperUI helper, not the misselected encounter. Priest dismissed
and offline asserted before return to Alamar. All companions now offline.

Core DoPartyFollow explicitly implements port catch-up. Supplied AGENTS standing
rule3 says ports hold; detailed POSSESS_LAW4.3/7.2 and older Core law check describe
the contrary historical policy. Candidate tools/core-patches/gi60-hold-after-port.patch
follows the current supplied rule, replaces cross-map chase with hold/end-follow,
and makes bossPorted join bossChanged in same-map hold detection. Read-only patch
--dry-run applies. Not compiled/installed/live-tested; detailed law and regression
reconciliation remain pending. Both built/installed Core timestamps23:21:23 Sep7.
Original local copy and normalized hash retained. No Core file/process changed.

GI61: Beginnings Progress032318 correctly displays three charms; actual Continue
opens Reward032437 with no spell row or Rewards title. Packet model carries688,
but NPC DrawQuestRewardSet ignores RewardSpell. Mounted patch.MPQ QuestFrame.lua
414ff renders the learned spell between choices and fixed rewards. Added wire-spell
forwarding for both NPC Details/Reward, spell-aware visibility, mounted learn label,
shared icon/name box and spell tooltip; row returns before any item click/equip action.
Quest remains uncompleted for live retest. quest-scroll-fixed ended normally with
9 runner failures retained (setup/incorrect assertions/unfinished encounters),
not a clean all-pass run. Character3 XP20/1400, charms3, purse80c; no GM levels,
money, items or quest completion. Both builds and scoped checks underway.
##### Spell reward and imp controls — 2026-09-08,03:54 local

Release build-quest-spell-sha256.json: Debug/Release each10 existing warnings,0 errors;
QuestLog, ImGui and client/Core possession checks passed separately. Current Core
check still encodes the older port policy and does not close GI60. Current hidden
quest-spell-fixed run logs god mode off before testing. Previous03:32 purse80c was
incorrect: actual pre/post reward purse79c. This correction preserves that entry.

GI61 Reward live:033727 spell row present; partial/scrolled hover033832/033834 shows
Summon Imp688,85mana,10second cast. Clicking spell row leaves Reward open and charms
intact. Scrolled back up, pointer91,602 lies in unclipped row extent but below content
clip and on Complete.033943 correctly shows Complete hover with no spell tooltip;
actual click186.148 completes1599 at186.315, consumes6753x3, grants355XP(20->375),
keeps79c and learns688(book034114). This also passes GI57's shared-row clipped-footer
case. NPC Details, tradeskill spell and mixed reward cases remain separate.

Actual native book->bar5 drag emitted one pickup832 and one drop833. Two subsequent
sound assertions failed because both cues were inside each category-filtered window;
these are retained test-setup failures, not missing/duplicate audio. Future marks must
separate pickup from drop. Current run therefore is not a clean all-pass runner.

Summon688: first cast322.946, START322.978/10s, movement Cancel325.414 and server
INTERRUPTED325.512; reviewed034236/034237 show precast then cancellation/cleanup,
no pet and no mana spent(262). Second cast GO443.791 after full10s spends85mana;
034427/034432/034435/034436/034440 reviewed, pet bar/portrait and imp Ruirai appear.
Orbit90 frame034541 exposes pet near column. FX roots are the caster's position,
not Alamar despite perspective overlap. Learned-spell chat remains an unconfirmed lead.

Pet autocast captures034704/034705 have REVERSED provisional off/on names: first
right click changes Firebolt3110 word0x81000C26->0xC1000C26(enabled), second disables.
First frame has sparkle, second does not. Outdoor GM fixture(-6325,512,387) relocates
pet with owner; this ordinary summoned pet is not a SuperUI companion hold test.
Native Stay then W1.5s moves owner10.972yd, pet distance2.855->11.354; Follow returns
pet to2.863yd without moving owner. Reviewed035218/035222 buttons/returned model.
Current level3,XP375/1400,79c; all SuperUI companions offline. All28 coverage areas open.
##### Pet combat follow-up — 2026-09-08,04:03 local

Imp Firebolt3110 autocast: GO1010.265,1012.407,1014.268,1016.253,1018.305,
1027.912, final damage1028.396 kills wolf0xF1300002C10003E7. Actual attack button
initiated combat. Reviewed035401 raised-hand fire precast;035405 shows firebolt
impact at low edge with approaching wolf;035433/035444 corpse and settled imp.
Full missile trajectory remains camera-limited. Wolf swings caused pet damage.
No owner attack, no XP/loot; cv-probe1141.272 corpse lootableFalse. Read-only Core
Unit.cpp839ff explicitly replaces a pet tap only on owner/group player damage;
Kill1030 clears player credit for a remaining pet recipient. This is existing
server policy, not a newly diagnosed client defect. No source change made.
Run quest-spell-fixed ended035613 with3 runner failures: two wrongly scoped sound
assertions plus unsupported item-gesture inventory command. Retain all failures.

Next run earth-quest, existing level60 Orc shaman0x222,185known spells; god off
confirmed on login. Quest1516 native Details/Accept, no objective provision/GM
completion. Canaga4532 portrait looked wolf-like in small frame, but magnified
canaga-portrait-detail.png shows the correct red-haired troll face. Mounted DBC
extra2802 race8male/no helmet corroborates identity. No portrait-identity defect.
First cave GM fixture(-163,-4348,67) was outside valid floor with terrain70.509;
void/inside-wall frames035821/035914 are SETUP_FAILED, not a rendering verdict.
Second fixture(-153,-4354,67.2) valid cave floor/model reviewed040001. Ordinary
Lightning Bolt403 kills existing3102, loot6640x1 and tracker1/2 verified. Level60
versus level4 is objective/input evidence, not combat difficulty evidence.

##### Shaman Earth chain and indestructible refusal — 2026-09-08,04:30 local

Run earth-quest, level60 shaman0x222, god off and later gm off. Actual quest1516
collection reached6640x2 after four kills (two no-drops; template quest chance80%).
One separate LOS-blocked fight attempt failed; invalid initial cave placement and
failed count assertions remain recorded. Native turn-in720.179 consumed2hooves,
paid150c; follow-up1517 acceptance763.346 supplied Earth Sapta6635.
Native Sapta use at village805.230 refused REQUIRES_SPELL_FOCUS with readable
Requires Shaman Shrine, item preserved. Valid Spirit Rock use861.492/861.609
consumed Sapta, played drink/purple effect041123, applied20min Sapta Sight8202,
and revealed Lesser Earth Spirit5891 previously absent041031. Clear model041241;
041126 camera was inside giant model, not a rendering defect. Native1517 reward
940.506 paid150c;1518 acceptance1009.728 supplied Rough Quartz6656.
GI61 broadened:1518 Details041314 and mixed Reward041612 render8071 spell plus
5175 item and270c. Correct separate spell/item tooltip captures041726. Earlier
mispositioned tooltip-named captures only show text and are not tooltip evidence.

Existing unique Earth Totem5175 blocked1518 completion1096.183(reason17), preserving
quartz1/totem1/money300 and active quest. Dragging that totem into world incorrectly
offered destroy popup041728. No/Escape preserved it041838. Actual Yes1374.240 sent
CMSG_DESTROYITEM; Core refused24 at1374.341, item retained. Subsequent completion
still failed17. Provisional earth-chain-completed041959 is NOT a completion pass.
Readonly ItemPrototype.h/ItemHandler.cpp and earth-totem-template.csv establish
INDESTRUCTIBLE0x20. GI62 adds client flag checks before popup and send, native
ERR_DROP_BOUND_ITEM. Both builds10warnings0errors, DeleteItem/QuestLog/ImGui and
client/Core possession checks PASS. Live indestructible-fixed retest now pending.
Earth run closed042412 normally with6 runner failures retained; quest1518 active.
No Core or database changes. All28 full-game coverage areas remain open.

##### GI62 live fix and Earth completion — 2026-09-08,04:36 local

Run indestructible-fixed, Release from build-indestructible-release.log. Actual
Earth Totem dragout043205 shows native error, no popup, item1 and empty cursor;
no inventory destroy wire. Ordinary Ruined Pelt4865x5 confirms043239; No preserves5,
retry Yes143.142 sendsdestroy and inventory becomes0. GI62 live regression PASS.
Authenticated .additem5175 -1 removed only the preexisting fixture at144.459;
this is GM cleanup, not successful normal destruction. Actual1518 Continue then
Complete175.946 now succeeds: reward176.085/economy176.118 prove quartz1->0,
totem0->1, money300->570, quest removed. Native gold reward effect043347 reviewed.
Already known8071 means no new-learning pass for this character.
Normal cast8071 at200.725 -> START/GO200.773, authoredanimation53 plays, initial
groundglow043410, actual totemmodel043414. Totem8072 aura applies201.204 with
visible icons. Temporal expiry case pending. All28 full-game areas remain open.
Indestructible-fixed closed043551 with0 runner failures. The frame named
stoneskin-expiry was captured only101seconds after the120second totem cast;
it is a pre-expiry persistence capture, NOT an expiry pass. Schedule an actual
post120second capture before closing a future totem case.

##### Sacred Cloth eligibility setup — 2026-09-08,04:40 local

New hidden sacred-cloth run priest0x221, godoff/gmoff confirmed. GM positioning
7023,-2134,586.8 is visibly valid Timbermaw tunnel floor; terrain verifier height
655.38 is above the interior and not a floor-failure verdict. Starting faction was
hostile; two guards attacked and killed priest37.384 before quest inspection.
043650 greeting-named frame shows combat/no dialog. Retain SETUP_FAILED.
Attempt modifyrep while dead failed Player not found; local dead selection does
not author server selection. Temporary .gm on, named .revive Nbprihuman118.633,
explicit self selection/wait, .modify rep5760 confirmed119.984, .gm off120.652.
This is fixture restoration, not native resurrection evidence. Health879 alive.
At neutral0/Tailoring3, Meilosh offers only vendor043825. Set Tailoring290/300
through GM confirmed; profession list4recipes shows skill290. Neutral0 still no
Sacred Cloth043910. Friendly3000/Tailoring289 offers Runecloth but not Sacred
Cloth043951. Both boundaries tested separately; raise290 next, then nativequest.
GM skills/reputation are setup, not progression earned by gameplay. All28 areasopen.

##### Sacred Cloth reward and craft interruption — 2026-09-08,04:47 local

Friendly3000/Tailoring290 exposes6032 alongside6031 at044043. Native rowclick
opens Details044123, correct trade-skill learn label and19435 Mooncloth Boots row.
Tooltip044212 shows spell name/60sec cast; created-item tooltip parity remains an
unverified reference lead, not a defect claim. Native Accept348.520 adds6032.
Initial hello returns multiquest list; two assertions expecting Progress fail,
retained. Correct actualactivequestclick opens Progress394.190/044258,0/2,
Continue disabled and inert when clicked. GM14342x2fixture updates2/2 at432.972;
white-to-yellow overhead marker and minimap marker reviewed044338.
Actual ProgressContinue477.323 -> Reward478.241, native trade-skill label044423.
Complete515.722 consumes2Mooncloth, learns19435 at515.723(204->205known), removes
6032, money73797->107697(+33900). Prior purse73713->73797 was incidental to earlier
run state; do not attribute that84c to this reward. New recipe044501, product15802,
reagents14048x6/14342x4/7971x2/14341x1. All initiallymissing, Create disabled044550.
Explicit GM supplies exact reagents; native Create610.435, serverSTART610.498 gives
31sec cast despite DBC60sec; animation123 authored/played. Movement612.316 cancels,
serverInterrupted612.401, readable red bar/error044637, allreagentspreserved,
product0. Retry stationary creation pending. GM fixtures do not certify gathering.

##### Improved crafted boots and GI63 — 2026-09-08,05:00 local

Stationary retry19435 START680.821, GO711.798 consumes14048x6/14342x4/7971x2/14341x1.
Server creates1099092 Improved Mooncloth Boots, not base15802; observer records
variant=True, skill290->291, all reagent0 assertionsPASS. Exact-base-item assertion
fails; retain as incorrect oracle, not missing craft. Actual item tooltip044911
shows Improved name,13stam/16int/15spirit,durability40,Required51,Made byNbprihuman.
Native bag rightclick044955 immediately equipped and bound without confirmation;
old Neophyte boots appear inbag, character044956 shows improved boots. GI63 confirmed.
Native StaticPopup.lua612/630 defines EQUIP_BIND/AUTOEQUIP_BIND, EQUIP_NO_DROP,
Okay/Cancel and CancelPendingEquip on bothCancel/Hide. Benilla ui_bind_confirm.rs
and ui_items/drain.rs trace byte-derived gate: unbound,usable,Bonding2,noqualitygate,
exactlyone swap endpoint equip (0..22 or63..68 onwirebag255), elect opposite item.
Implementation now centralizes auto-equip sends and gates both swap directions;
pending capturesactor/source/destinationGUIDs, cancels onbody/session changes,
rechecks ataccept, usesvanilla popup host/buttons. EquipBindingclinicalPASS;
finalDebug/Release and live retestpending. Sacredrunclosed045101 with3runnerfails:
2wrongpanelassertions plus baseproduct-ID assumption. All28areasremainopen.

##### GI63 live regression and snapshot clock defect — 2026-09-08,05:18 local

Run equip-binding-fixed, priest0x221, GMprovided3base15802 at Goldshire. Actual
rightclick createsprompt050443, no send beforeapproval. Cancel050545/Escape050547
preserveBindswhenEquipped; Okay158.605 sends and Soulbound/equipment050549 confirmed.
Alreadybound1099092 bypass236.384 confirmedFalse, actualequipment restored.
Reverse equip->occupiedbag clickplacement prompts327.475; Cancel retains1099092
feet/15802bag9. LaterAccept431.960 swaps and binds, assertionsPASS051023.
Pendingauto sourcebag10 moved throughproduction pickup/place tobag11, promptclears
051025 withoutsend; feetunchanged. SuperUINbmaghuman summoned502.350. Pending
main unbound bag11 question051215 disappears onpossession051216, cursor0. CtrlTab
return051409 retainsunbounditem. Mage dismissed/offline verified. Forwardnative
click-pickup/place question051526, Okay binds; finalimprovedboots restored.
Runclosednormally with0runnerfails. Ordinarydragrelease frompaperdoll->bag failed
twice050726/050932, leavingcursor; extra clickworked. Separate lead, notdragPASS.
NoOSinput/Coremutations. BothGI63builds10warnings0errors; scopedchecksallPASS.

Possessedmage051216 characterattack1153958sec/ranged1157235sec is concreteGI64.
ReadonlySuiPossess1354 sends GetUInt32Value of floatstoredattacktime; Object.cpp752
normally converts floatto uint milliseconds. Client copiedrawbits. Added explicit
ObjectFields.StoredAttackTimeMilliseconds foronly3snapshotfields; ordinaryobject
fieldgettersunchanged. Client/Corewireformatunchanged, noCoredeploymentrequired.
Addedrawfloat regression; builds/retestpending. Readonlyglue drag-inspect diagnostic
addedtoinvestigatecursorrelease. All28fullgameareasremainopen.

##### Snapshot attack-time retest — 2026-09-08, 05:29 local

GI-64 Debug and Release built with 10 existing warnings and zero errors. Raw-float
regression, EquipBinding, ObserverBodyOwnership, GameplayImguiPolicy, DeleteItem,
client PossessLaw and read-only Core possess-law checks pass. Hashes are in
checklist-20260907/build-snapshot-time-hashes.json. Live snapshot-time-fixed run
(runner-20260908-052631.csv) ended normally with zero runner failures. Explicit
server-confirmed god-off and GM-off on login. Reviewed own-body 052637: 1.90-second
attack, 2.00 ranged; possessed Nbmaghuman 052644 and repeated possession 052722:
1.60 attack, 2.00 ranged, replacing the raw-bit million-second values. Native
Ctrl-Tab return restores priest; mage dismissed and roster offline verified.

Inventory probe 052730/052731: paperdoll feet picked up, ImGui recognized drag
movement (1150,353), but payload absent throughout and release retained item.
Following bag test 052844 had no carried item after Escape closed the bag, so it
is SETUP_FAILED, not evidence that bag dragging fails. Additional source-active
observation is being added. All 28 areas remain open.

##### Native inventory drag regressions — 2026-09-08, 05:48 local

GI-65: read-only source instrumentation proved that bag and equipment buttons lost
active mouse ownership when newly visible empty action-bar hit windows appeared.
NoFocusOnAppearing preserves that ownership. The intermediate drag-focus-fixed run
(053517) verified unequip/equip, reverse BoE drag Cancel/Okay, and exposed GI-66:
held priest boots GUID079C were replaced on the cursor by mage boots GUID0686 when
possession changed. Its one failed cursor assertion is retained. The item popup
was cancelled before acceptance; the mage was dismissed and the priest restored.

GI-66: inventory and bag-bar drag sources now retain the pressed actor, slot and
item; clearing the cursor also invalidates that press. Both builds have 10 existing
warnings and zero errors. PossessLaw, ActionBarBindingTail, EquipBinding, DeleteItem,
GameplayImguiPolicy and read-only Core possession checks pass. Final build hashes
are in checklist-20260907/build-drag-ownership-hashes.json.

Final live drag-ownership-fixed run (runner-20260908-054242.csv) ended normally,
zero runner failures. Native equipment swap054251 and bag-to-feet054342 succeed.
Reviewed held preview054341 retains the native icon, with the default ImGui text
and drop rectangle removed. Held possession054351 and release054352 leave cursor0,
mage boots equipped; return retains priest1099092. Bag8->12 swap and held Escape
preserve item identity/count. Provisioned828 equips to bag1, native closed-bag drag
054716 moves it to bag2, then back to bag1; exact equipment slots19/20 asserted.
No OS input or Core mutation. All28 coverage areas remain open.

##### Stoneskin full lifetime — 2026-09-08, 05:52 local

Release build from build-drag-ownership-hashes.json; Nbshaorc60, saved Valley of
Trials position, explicit god-off and GM-off confirmed. Stoneskin8071 server GO
14.449, authored animation53 played; reviewed054856 cast pose and initial totem
glow. Object F1300016F104A829 and Stoneskin8072 aura appeared (APPLY15.079). Reviewed
054900 and055041 show the totem and aura still present. Aura REMOVE134.575, 120.126
seconds after GO. Reviewed055103 after126seconds: totem and its aura icon gone.
This supplies actual expiry evidence that the previous101-second capture lacked.
The5175 casting tool remains1. Same-element replacement is being checked next.
All28 areas remain open; no other totem rank/duration is inferred from this case.
Same-element follow-up: Stoneskin recast, then Strength of Earth8075 replaces it.
Old8072 aura removed223.247; new8076 applied223.781. Reviewed055228 shows one earth
totem and the Strength of Earth icon. Tool5175 remains1. stoneskin-expiry runner
054851 closed normally with zero failures. No actual strength/stat increase or
melee reduction measurement claimed by this presentation/lifecycle case.

##### Dwarf hunter class chain — 2026-09-08, 06:08 local (in progress)

Hunter Nbhundwarf60/0x21F has171 known spells but not Call Pet883; local unknown-
spell refusal055355 is not the planned server no-pet test. Explicit god-off and
GM-off confirmed. GM fixture travel uses known live creature spawns, not navigation
proof. Grif Wildheart1231/spawn222 gives6064; native details/accept055723/25 grants
rod15911 (3charges, spell19674). Native use on friendly Grif refuses Invalid target
locally055857 and leaves3charges. GM moved near Large Crag Boar1126, selected3642.

Native rod use starts20-second channel427.798. Reviewed060033/36 show dwarf casting
and hearts attached above the boar; actual attacks reduce health and train Defense.
Moving at432.261 cancels; server stops, failed-other follows, hearts disappear and
quest flags remain00000000. Rod tooltip060038 has2charges. Second full channel
completes objective543.587, quest flags01000000, applies19677 charm and pet bar for
same creature GUID F130000466000E3A. Reviewed060229 completion/temporary companion.
Native turn-in714.844 consumes rod and gives510c (0->510 verified); next6084 details
open automatically. Accepted Snow Leopard step with rod15913. A far follow attempt
after GM return to Grif has not established movement; no follow pass granted.

GI67 confirmed: rod allowable_class2047 includes all playable bits plus reserved
class bits, but client equality test displays every class. Reference render.rs429
masks playable bits before omitting unrestricted lists. One-line client fix prepared;
build/live retest pending. No tooltip clipping claim: the backdrop grows to fit.
All28 coverage areas remain open.

##### Hunter snow objective and temporary-pet menu — 2026-09-08, 06:25 local

6084 first selection after fixture3643 skipped a guard-killed corpse and chose234
at97.7yd, then cleared; Invalid target was setup failure, not an existing-pet gate.
Second attempt on234 received miss6 (SPELL_MISS_EVADE, Core SpellDefines.h168),
channel stopped after100ms and no credit. Green fading bar061309 is a presentation
lead, not tame success. Valid nearby4110 retry GO1545.198,20second channel and
objective1565.347, charm19676 and pet bar; reviewed061933. Native turn-in1617.824
consumes15913, purse510->1020,6084removed,6085offered. Accepted6085 rod15908.

Valid bear4109 refused with active Snow Leopard: separate pet notice visibly says
You have an active summon already (062116), though CAST_RESULT is DONT_REPORT.
This is a PASS for refusal feedback, correcting the initial log-only commentary.
Native pet portrait and name right-clicks062141/062228 have no context menu.
Confirmed GI68: VisiblePetRows excludes charms with empty SUMMONEDBY. Mounted
UnitPopup.lua402-417 selects Dismiss when PetCanBeAbandoned is false. Fix admits
acting-body CHARMEDBY or SUMMONEDBY; tests, builds and live retest underway.
Earlier boar disappearance (capture061236) was NOT a verified native dismissal;
no menu or dismiss send was established. No natural-expiry timing pass either.

061838 frame shows overlapping Defense/Dazed floating text. Recorded as GI69
visual lead for separate reproduction/source investigation. Combat feedback case
has not passed. All28 coverage areas remain open. Hunter returned to Grif and
client closed normally to rebuild. Runner055345 has1failure: pet inspect after
unsuccessful evade tame, retained rather than rewritten as a pass.


##### Pet-menu fix live — 2026-09-08, 06:32 local
Build-petmenu Debug/Release each10warnings0errors; PetMenu/PossessLaw/ObserverBody/
ImGui/SharedDocs and read-only Corelaw PASS. Live hunter-petmenu-fixed PID54552,
god-off/GM-off confirmed. Rod15908 tooltip062732 omits unrestricted class list,
3charges; restricted-item control still pending. Bear4109 actual tame GO38.020,
channelstop58.050, objective58.151, charm19597. Mid062802 reviewed with native
castbar/hearts; detailed dwarf hold pose not yet approved from this capture.

GI68: actual right-click opens Dismiss/Cancel062828. Native Dismiss133.810 removes
19597;062927 shows portrait/bar gone and bear hostile again. Quest6085 completion
flag retained, actual return/turn-in206.755 consumesrod,1530c (+510),teaches1515,
883,2641,known171->174. Training6086accepted; GM travel to Belia10090/spawn59 in
Ironforge is setup only. Call883 without permanentpet sentGO/whistleanimation81,
no pet or visible error063109; not classified as failure without Core oracle.
All28 areas remain open; no entire hunter-class or pet-lifecycle completion claim.

##### Hunter permanent pet and stable findings — 2026-09-08, 06:49 local
6086 offers Reward directly (no RequestItems). Native complete351.053 teaches
1853,14922,5149,6991;1770c (+240),known174->178. Extra assert-completable was a
harness setup FAIL because panel alreadyReward, not gameplay failure. Restricted
16850 tooltip063321 retains Classes: Hunter; GM provision/removalfixtureonly.
GI67live complete, including unrestricted15908 earlier. Stable-only Erma rejects
quest hello, another setup FAIL; native chat /stable opens actual stable.

Permanent Tame1515 on StonetuskBoar113/spawn80361, GO474.021, full20sec ends494;
new permanent petGUID F14000005C000049,petnumber92,level6;063529 reviewed. Original
world creature's despawn produces failed-other messages but actualpet creation
proves mechanics; those messages alone are not failure oracle. Native bag117x4
pickup then pet-portraitclick casts6991/1539 at567.379/.428,117x3,cursor0,petglow
063641. Feed aura removed587.395 (~20sec);063652 capture at12sec is NOT expiry;
063741 is post-expiry. No measured happiness increase claim yet.

Rename Copper native dialog063842,confirm063946; No064051 preserves name. Repeated
native Yes removes Rename option064138 but frame remainsBoar064056. Stable list
064412 namesCopper, confirming serveraccepted. CorePetHandler381 changes field140
PET_NAME_TIMESTAMP. GI72: client never invalidates name cache from that field;
versioned refresh prepared. GI71: happiness icon/tooltip missing entirely, verified
against mountedPetFrame.lua/xml; implementationpending. GI69verticalspacing and
GI70hiddenaura-name filters prepared, Debugbuildpasses, Release/livepending.

Stable064412 currentCopperlevel6,0slots, Stablebuttonclipped at right (GI73physical/
logicalwidthmixed). BuySlot1089.886 succeeds (firstslot affordable, filename
stable-unaffordable was provisional/incorrect);1slot. Ownerwalked13.617yd away,
064527 stable stillopen(GI74missingrangeleash). ReturnedErma fixture; native Stable
1215.336 succeeds,064728 activeabsent andCopperlistedstabled. Retainedpet92infirst
purchasedslot; clientclosing for rebuild. Source fixes for GI73width and GI74
acting-body request/reply/action/drawrange checks prepared. BuySlot shows no cost
or confirmation; compare mountedframe before classifying further. All28areasopen.

##### 2026-09-08 07:13 local — pet happiness and stable follow-up

Built Debug/Release after GI71 happiness and GI75 stable pricing changes:
10 existing warnings, zero errors; PetMenu, GameplayImguiPolicy, SharedDocs,
client PossessLaw and read-only Core law PASS. Hashes and build logs use
build-pet-happiness prefix. New hidden run hunter-happiness PID39208,
Nbhundwarf; god mode and GM mode explicitly OFF.

GI73 reviewed065315/065414 contained stable controls. GI74 actual walk closed
at125.953 and distant reopen065506 visibly refused. GI70 reviewed065920 keeps
Dazed/Aspect while hidden5302/24948 are absent. GI69 remains open: the same frame
still crowds damage at the burst boundary. Old hunter-stable-fixed ended with
zero runner failures. Copper was retrieved at123.720, destroyed135.762, and absent
from the next login/stable list. Cause unconfirmed; Core loyalty runaway is one
candidate, not a proven verdict.

Stable second-slot cost displayed red/disabled070932; actual disabled click
071021 did not buy. Authorized fixture added50000c;071022 shows enabled/white
5gold cost. Actual purchase97.250 result0x0A;071039 shows slots0/2 and no further
purchase/cost controls. First-slot actual debit was500c, not an assumed1000c.

New permanent boar pet93 GUIDF14000005D00004B tamed1515 at124.168.
071107 reviewed unhappy icon and full tooltip: Unhappy, Causes75% of normal
damage, Losing Loyalty. Raw happiness166500/loyalty1. Actual bag-food117 to pet
sent6991 at167.053, GO167.152, effect1539 at167.185 through187.184. Happiness
122750 before feeding to280250 at mid observation; end and further thresholds
being measured. No blanket damage multiplier or loyalty progression pass.

Two current runner failures are recorded: inspect when Copper was absent and
an unsupported inbox-checkpoint command mistakenly appended by the harness operator.
These are not a zero-failure run. All28 broad coverage areas remain open.

##### 2026-09-08 07:21 local — happiness thresholds, rename, and stable swap

Reviewed071247 Content100%/Gaining Loyalty at577750 and071311 Happy125%/Gaining
Loyalty at901500. Three native food117 feeds consumed3 (assert0), final1041250.
All three icons/tooltips reviewed; actual damage multipliers remain unmeasured.
Bristle rename native Yes after071411 confirmation updated frame071445 immediately,
cache timestamp0->1788866084. GI71/GI72 scoped live UI PASS.

Bristle stabled388.849 result08, retrieved391.418 result09; newGUID...004C,
name/happiness892500 preserved and stillpresent after14sec071548, happiness875000.
Restabled447.679. SnowLeopard1201 permanent tame GO/cast through483.041 creates
pet94...004D, Cat level7/hp137/happiness166500. Actual water159 feed refused
WRONG_PET_FOOD520.805; reviewed071742 explicit text, count2 and cursor0 preserved.
Two food117 feeds then consume10->8 and happiness770250. Fixture10food was provisioned
through authorized in-game GM command, not gathering evidence.

Native stable Swap with active clicked twice:642.672 then645.374 result09.
Reviewed071945 Bristle active/Cat stabled,071947 Cat active/Bristle stabled;
correct visible names/portraits and oneused/twopurchased slots. Current Cat...0052,
health154, happiness691500 after swap. Bothpets remain on greg hunter.
Pet death/revival next: living IceClawBear1196 spawn4109, hunter16.78yd away;
GM positioned only, actual pet command/combat to follow. No death verdict yet.

##### 2026-09-08 07:48 local — pet death/revive and Beast Training

Controlled pet-death fixture used selected Cat plus in-game .damage1000; verified
Core UnitCommands2403 calls ordinary DealDamage. This is a forced-damage fixture,
not a natural-combat death pass. Reviewed072507 pet down/empty frame, health0.
Living-pet982 and dead-present883 returned ALREADY_HAVE_SUMMON with visible text.
Actual982 starts967.734 with server4000ms (DBC10000ms, character has passive
modifiers), GO971.672; reviewed072511 hands/castbar and072514 revival beams/pet
health restoration. The capture named mid is after successful completion.
Second fixture: W interrupts982 at1023.212, health remains0, reviewed072605 red
Interrupted bar/text. RetryGO1028.332,072609 success bar,072610 release aftermath,
health89. Mana2400 was fixture-restored before this cancellation sequence.
Three subsequent117feeds consumed8->5, happiness0->971250, pet health154.

hunter-happiness closed normally with2 recorded setup failures (absent Copper
inspect, unknown inbox-checkpoint), not a zero-failure run. No natural pet death
claim from the earlier bear setup: its fight finished before timed capture.

GI76:5149 Beast Training was silently intercepted but skill261 omitted from
craft whitelist; reviewed072810 no panel. Shared craft-line admission added;
bothbuilds10warnings0errors, ProfessionFrame/ImGui PASS. New hunter-training run
reviewed073302 open with Growl ranks1/2. Native Create1 sends1853, GO97.379,
pet book gains2649. Rank2 sends14922, LOWLEVEL100.180, book unchanged. This exposes
GI77: missing pet-level/cost requirements, false rank0/0, Create caption and empty
Reagents. No full training UI pass granted.

GI77 implementation: SkillLineAbility col14 costs with forward-rank links,
pet-owned known/level/point checks, Train caption, native requirement/cost/point
positions, omitted beast-training skillbar/empty reagent caption. Shared pure law
covers free skills even with negative usable points, lower pet level, unavailable
pet, already-known skills and insufficient points. Mounted cost checks Growl0,
Bite1/Bite2costs1/4 and chain credit PASS. Active-ability cap remains server gate.
Profession structural check initially failed because two coordinates were inline;
moved them to PetTrainingUiLaw, preserving the existing check. Its crashed owned
process49952/child54016 held test DLL; verified PID ancestry/path, stopped local
failed diagnostics, retried. Both final trays10warnings0errors; PetMenu,
ProfessionFrame, PossessLaw, ImGui, SharedDocs and read-only Core law PASS.

Active run hunter-training-ui PID55236, final hashes build-pet-training-ui-hashes.
Same hunter, Cat active, Bristle stabled,2slots,1270c,food117x5. Added owner teaching
spells17254/17262 by authorized GM fixture to test Bite training costs. Pending
live GI77 then SuperUI possession and further full-game coverage. All28 areas open.

##### 2026-09-08 08:01 — GI77 live paid pet training
Corrected UNIT_TRAINING_POINTS decoding against Pet::GetDispTP: signed negative
encoding -(available+1), positive encoding of negative available; not ushort totals.
Debug/Release training-points builds:10 warnings,0 errors. PetPaperDoll,
ProfessionFrame,PetMenu,PossessLaw,ImGui and read-only Core law PASS; hashes saved.
Reviewed075012 Bite2 red level8/cost4,075013 known Growl/disabled,075855 zero-total
footer hidden. Hidden hunter-training-points run55548: god/GM off confirmed.
GM pet loyalty100000 raised Cat loyalty1->2 (fixture, not natural loyalty pass),
rawFFFFFFF8=7 points. Actual Train Bite1 teacher17254 GO48.841; pet gains17253,
rawFFFFFFF9=6;075912 reviewed known row, rank2 incremental3TP and learning effect.
GM selected-pet levelup1 raises Cat7->8/hp175 and available6->7. Actual Train Bite2
teacher17262 GO96.063, rawFFFFFFFB=4;075959 reviewed known ranks and learning beam.
Owner teachers were GM provisions; no wild pet ability-acquisition pass claimed.
Fed Cat three117 items this run (5->2), final happiness inspected before exit.
All28 broad areas stay open. Next: SuperUI possessed hunter ownership/services.

##### 2026-09-08 08:08 — SuperUI pet ownership defects and Bite description
Hidden possessed-hunter-pet58996: mage god/GM off, summoned/possessed hunter21F
successfully; Cat94 petguid...0066 retained Bite2/Growl1 and loyalty2/level8.
Reviewed080256 happy tooltip and080258 pet menu: ownership correct. Reviewed080208
training footer0; owner-only training descriptor0, health normalized100 instead of
175. Last direct-owner available4; no intervening learning. Core Object.cpp1059
owner visibility ignores possessor. GI78 OPEN; .pet info while possessed returned
Incorrect syntax (selected-pet lookup), not an independent live4-point readback.
GI80 OPEN: Core Object.cpp714-717 filters StableMaster bit on target->GetClass(),
which is mage. Reviewed080423 selected Erma rejected locally as not stablemaster.
GM .go creature moved session mage, not hunter; selected NPC14.15yd from actor.
Therefore neither stable opening nor range lifecycle passed. First malformed
select-entry-nearest command is a retained harness FAIL; corrected select syntax
also cannot overcome missing stable flag. No server edits/deployment.
GI79: mounted Bite teachers17254/17262 have empty descriptions; abilities17253/17255
have damage text. Client now falls back only for empty Beast Training descriptions,
substitutes against taught rank. Both trays10warnings0errors; mounted PetMenu,
ProfessionFrame,PossessLaw,ImGui PASS. Live retest pending. All28 areas remain open.

##### 2026-09-08 08:12 — GI79 live closure and retained login failure
First description run51944 failed during bootstrap with network EOF08:08:18;
no gameplay steps ran. Logs/artifact retained. Server process read-only start time
was Aug17, so no server restart attribution. Retry52692 succeeded; reviewed081042
Bite2 now says16 to18 damage, footer4. Direct owner rawFFFFFFFB confirms saved4
points; pet health152,happiness800625,loyalty2. Same build hashes saved. Both local
runs exited; SuperUI hunter had been dismissed and offline assertion passed113.
Next druid57108: actual2541->2561 prerequisite/kill/corpse-item quest path.

##### 2026-09-08 08:20 — sleeping-druid prerequisite setup retained
Druid225,level60; god/GM off. Native Accept2541 at123.137 adds log. First malformed
interact lacked gossip argument and failed; corrected offer081408 reviewed.
Two initial fights selected wrong-floor/distant units (2010range,2009LOS), retained
FAIL. GM grounded positioning48349 then Wrath9912 GO204.319 killed2009.
GM learn9912 replied already known: no new spell acquired. Immediate loot failed
because ordinary alive->dead edge cleared selection. No charm acquired yet.
Added observe-only harness select-fight-target to reselect retained fight GUID;
interact now guards missing argument. Both trays10warnings0errors. First run57108
exited; retry begins48516 and will reselect corpse before loot. No travel pass.

##### 2026-09-08 08:31 — druid corpse spell temporal review
Retry33524 retained initial wrong-floor LOS and facing failures. Actual shaman
identity + anchor4/facing0 produced kills; loot requests autolooted, so extra
TakeAll failures were unnecessary harness calls, not missing loot. Fourth shaman
in retry yielded8363 at199.158. Quest2541 completion314.117 paid420c,38->458.
2561 accepted315.700, item8149 supplied. HealingTouch9889 under actual damage:
server3000ms plus900ms pushback,GO320.639 restored health; no invulnerability.
Rageclaw living8149 use349.530 BAD_TARGETS, actual normal Wrath kill followed.
First clear-corpse cast414.448->424.390 failed OUT_OF_RANGE at end; corpse later
absent and Rageclaw respawned (spawn interval60-65s), so no release visual pass.
Second attempted anchor failed with no selection, retained BAD_TARGETS.
Fresh normal kill followed immediately by corpse reselection/anchor0.5 succeeded:
10617 start581.116,GO590.909, quest completion. Reviewed082935 blue hand glow,
082945 release burst + corpse smoke/sparkles and completion;082945.899 cleanup.
These full-resolution captures improve the earlier mage run's occluded impact.
Frame names from failed attempts do not establish release/completion. Reward
turn-in currently queued; all28 broad areas remain open. No server deployment or direct database edits.

##### 2026-09-08 08:38 — quest reward, form observations and second disconnect
2561 reward688.752 paid660c,458->1118, choice1 selected; quest log empty afterward.
Travel783 indoors694.007 ONLY_OUTDOORS: reviewed083128 explicit refusal.
Aquatic1066 accepted695.125 on the apparently dry barrow floor; reviewed083129
SeaLion2428 and blue morph particles. Land eligibility is an OPEN LEAD, not a
certified bug: live Core has no ONLY_UNDERWATER check usage; mounted/native1.12
restriction still needs an oracle. Early Bear request hitGCD; Cat from Aquatic
WrongForm. These do not establish either form. Native active Aquatic button
canceled aura853.285; DireBear9634 GO854.237, reviewed capture pending.
Connection EOF08:34:58 interrupted before later cat/outdoor steps; run33524
ended11 retained failures (setup/extra autoloot/early reward/absent corpse/network).
Added worker exception stack logging to distinguish socket EOF from packet parsing;
Debug/Release10warnings0errors. New outdoor form run60208 is starting.


##### 2026-09-08 08:53 — GI81 Aquatic Form locomotion
Earlier083409 Dire Bear frame reviewed: bear model/rage. Outdoor083841 Cat and
083845 Travel frames reviewed: cat/energy and cheetah/mana; native active stance
buttons cancel forms. Earlier084057/084101 filenames saying swimming/water were
on land after crossing a shallow strip and confer no swimming pass.
At GM fixture(-9402,-130,56), actual START_SWIM occurred; reviewed084654 moving
seal still animation0. CreatureRenderer omitted the swimming selector altogether.
Added flag-based41/42/43/44/45 selection, turn>strafe>back>forward precedence from
local benilla creature_anim/select.rs byte-verified1.12 reference. Mounted seal
regression invokes the actual renderer selector, including diagonal/turn and dry
states. Both trays10warnings0errors; BodyDisplay/PossessLaw/MountRendering/
TacticalFreeze PASS. Hashes build-swim-forms-hashes.json. Pose diagnostic now logs
controller swimming/grounded/speed/position and outgoing flags.
Previous60208 normal exit0 runner failures; new60372 druid-swim-fixed god/GM off.
Actual flags00200000/1/2/4 and poses41/42/45/43. Reviewed085124.170/.800 successive
forward frames show flipper strokes, underwater camera and body, with prior41idle.
Straight forward7.083333yd/s; native Aquatic cancel starts server breath60000ms at
71.510 and restores native NightElf55/SwimIdle41; caster forward4.722222yd/s.
This proves50% forward speed effect for this body. Breath expiry/recovery is in
progress. Other model/race/form and remote creature swim variants remain open.
All28 coverage areas remain open. No Core edits/deployment.

##### 2026-09-08 09:00 — owner pause; swimming follow-up and GI82 pending
Owner requested pause and a complete recap named Sept 8, 26 fixes. No further
gameplay, implementation or test runs are being started. Hidden60372 exited
normally after inbox-close; process inspection finds no MSUIClient or
interface-wire-check process. No SuperUI companion remains from these runs.
GI81 additional reviewed085126 backward and085127 strafe frames match45/43;
085220 native cancel restores NightElf and visible Breath bar. Forward speed
7.083333 vs4.722222 is a50% change with horizontal camera pitch0.
First breath test was mistimed: server60s timer71.510, environmental damage at
135.716/137.811/139.733 and PLAYER_DEAD139.767. The queued low/surface/recovered
captures085329/085339/085347 show a DEAD body, not recovery. Early cat/prowl
captures085356/085359 likewise show death/refusals, not forms. GM revive194.106
and healing restored1993HP (fixture, not resurrection gameplay proof).
Second short immersion starts timer289.422. Native Space ascent leads server
recovery303.244 (47267/60000ms,scale10) and stop304.563. Captures085610/085612/
085618 record before/surface/recovered; visual review tracked in recap. Final
safe GM placement(-9382,-150,60), caster form; normal exit, zero runner dispatch
failures does not override the failed long-immersion/cat attempts above.
Prowl9913 successful202.575, aura202.643: reviewed085433 translucent Cat but
ordinary Run5; idle0 logged085432. GI82 implemented for all3 body renderers,
using existing UNIT_FIELD_BYTES_1 CREEP flag,120idle/119creep, backward priority,
authored1x stealth rate. Both client builds10warnings0errors. The expanded
BodyDisplay test failed to COMPILE (CS0117 WorldEntity.MoveFlags, line90), so
no new stealth test passed and no post-change live run took place. Paused
without repairing it. Last live-verified binary is the earlier swim build;
latest Debug/Release files contain the unverified stealth change.
All28 broad areas remain open. No commit/push or Core deployment/restart.

