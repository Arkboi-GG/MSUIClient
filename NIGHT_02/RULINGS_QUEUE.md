# NIGHT_02 rulings queue (append-only)

No rulings queued as of 2026-08-01 15:35 local.

## Q1 — 2-1 — unit-frame NOT-DRAWN worklist and perceptual review

- Corrected full-reference coverage: player 6/30 with 24 NOT-DRAWN; target 7/35 with 28 NOT-DRAWN. Exact sets are the `coverage` DELTA rows in `live-runs/N2-0-2-9P-instrument-truth-20260801-183000/player-frame-final-diff.csv` and `target-frame-final-diff.csv`.
- Work: implement the NOT-DRAWN chrome/text/state elements; close all remaining draw-derived geometry/anchor/font/color/layer deltas.
- Review: `player-frame-contact-isolated.png` and `target-frame-contact-isolated.png` contain nonblank shipped-art references and clean actual captures.

## Q2 — 2-2 — party-frame NOT-DRAWN worklist and perceptual review

- Corrected coverage: 0/39; 33 NOT-DRAWN; six DRAWN-NOT-INSTRUMENTED. Exact set: `party-frame-corrected-diff.csv` in the pilot packet.
- Work: derive evidence for the six real draws, then implement the 33 absent reference elements.
- Review: `live-runs/N2-0-2-2-party-frames-20260801-160500/party-frame-contact.png`.

## Q3 — 2-3 — action-bar NOT-DRAWN worklist and perceptual review

- Corrected coverage: main 0/21 (7 NOT-DRAWN), button 0/10 (6 NOT-DRAWN), multibar 0/121 (116 NOT-DRAWN). Exact sets: the three `*-corrected-diff.csv` files in the pilot packet.
- Work: replace legacy declarations with draw-derived evidence and implement every NOT-DRAWN element.
- Review: the three contact sheets in `live-runs/N2-0-2-3-action-bars-20260801-162500/`.

## Q4 — 2-4 — cast-bar NOT-DRAWN worklist and perceptual review

- Corrected coverage: 0/6; 2 NOT-DRAWN; four DRAWN-NOT-INSTRUMENTED. Exact set: `cast-bar-corrected-diff.csv`.
- Review: `live-runs/N2-0-2-4-cast-bar-20260801-164000/cast-bar-contact.png`.

## Q5 — 2-5 — buff-frame NOT-DRAWN worklist and blocked reference render

- Corrected coverage: 0/105; 101 NOT-DRAWN; four DRAWN-NOT-INSTRUMENTED. Exact set: `buff-frame-corrected-diff.csv`.
- SHELVED-BLOCKED render only: the harness lacks the runtime-state hook `render --state aura-spell=<id>` needed to join the aura spell to `SpellIcon.dbc` and select its shipped icon. No art is missing and none may be invented. Build that hook before claiming a buff reference contact sheet.

## Q6 — 2-6 — minimap NOT-DRAWN worklist and perceptual review

- Corrected coverage: 0/21; 8 NOT-DRAWN; 13 DRAWN-NOT-INSTRUMENTED. Exact set: `minimap-corrected-diff.csv`.
- Review: `live-runs/N2-0-2-6-minimap-20260801-171500/minimap-contact.png`.

## Q7 — 2-7 — chat-frame NOT-DRAWN worklist and perceptual review

- Corrected coverage: 0/34; 25 NOT-DRAWN; nine DRAWN-NOT-INSTRUMENTED. Exact set: `chat-frame-corrected-diff.csv`.
- Review: `live-runs/N2-0-2-7-chat-frame-20260801-173500/chat-frame-contact.png`.

## Q8 — 2-8 — XP/reputation NOT-DRAWN worklist and perceptual review

- Corrected coverage: reputation 0/12 with zero NOT-DRAWN and 12 DRAWN-NOT-INSTRUMENTED; XP 0/9 with four NOT-DRAWN and five DRAWN-NOT-INSTRUMENTED. Exact sets: `reputation-bar-corrected-diff.csv` and `xp-bar-corrected-diff.csv`.
- Review: both contact sheets in `live-runs/N2-0-2-8-xp-reputation-20260801-175000/`.

## Q9 — 3-1 — character-sheet NOT-DRAWN worklist and perceptual review

- Corrected coverage: CharacterFrame shell 1/13 with 12 NOT-DRAWN; PaperDollFrame 5/127 with 122 NOT-DRAWN. Exact sets: `character-frame-diff.csv` and `paperdoll-diff2.csv`.
- Work: instrument and implement the absent header, slot, stat, model, resistance, and tab elements using the existing functional draw path.
- Review: `live-runs/N2-0-3-1-character-frame-20260801-190000/paperdoll-contact2.png`.

## Q10 — 3-2 — spellbook NOT-DRAWN worklist and perceptual review

- Corrected coverage: 6/160 with 154 NOT-DRAWN. Exact set: `live-runs/N2-0-3-2-spellbook-20260801-191500/spellbook-diff.csv`.
- Work: derive the spell-button/tab/page evidence and implement every absent default/conditional reference element.
- Review: `live-runs/N2-0-3-2-spellbook-20260801-191500/spellbook-contact.png`.

## Q11 — 3-3 — talent-frame NOT-DRAWN worklist and perceptual review

- Corrected coverage: 9/31 with 22 NOT-DRAWN. Exact set: `live-runs/N2-0-3-3-talent-frame-20260801-193000/talent-frame-diff.csv`.
- Work: instrument/implement portrait, point header, buttons, tabs, and the runtime talent buttons/arrows omitted by the static shell inventory.
- Review: `live-runs/N2-0-3-3-talent-frame-20260801-193000/talent-frame-contact.png`.

## Q12 — 3-4 — quest-log NOT-DRAWN worklist and perceptual review

- Corrected coverage: 6/43 with 37 NOT-DRAWN. Exact set: `live-runs/N2-0-3-4-quest-log-20260801-194500/quest-log-diff.csv`.
- Work: build live quest-log population and derive list/detail/objective/reward/button evidence for the shipped shell.
- Review: `live-runs/N2-0-3-4-quest-log-20260801-194500/quest-log-contact.png`.

## Q13 — 3-5 — merchant NOT-DRAWN worklist and perceptual review

- Corrected coverage: 5/124 with 119 NOT-DRAWN. Exact set: `live-runs/N2-0-3-5-merchant-20260801-200000/merchant-diff.csv`.
- Work: derive item-grid, buyback, currency, paging, tab, and button evidence from the populated vendor draw path.
- Review: `live-runs/N2-0-3-5-merchant-20260801-200000/merchant-contact.png`.

## Q14 — 3-6 — trainer NOT-DRAWN worklist and perceptual review

- Corrected coverage: 5/111 with 106 NOT-DRAWN. Exact set: `live-runs/N2-0-3-6-trainer-20260801-201500/trainer-diff.csv`.
- Work: derive service rows/filter/cost/button evidence and implement every absent reference element.
- Review: `live-runs/N2-0-3-6-trainer-20260801-201500/trainer-contact.png`.

## Q15 — 3-7 — bank NOT-DRAWN worklist and perceptual review

- Corrected coverage: 5/59 with 54 NOT-DRAWN. Exact set: `live-runs/N2-0-3-7-bank-20260801-203000/bank-diff.csv`.
- Work: derive slot-grid, item, bag-slot, purchase, money, title, portrait, and button evidence and implement every absent reference element.
- Review: `live-runs/N2-0-3-7-bank-20260801-203000/bank-contact.png`.

## Q16 — 3-8 — mail NOT-DRAWN worklist and perceptual review

- Corrected coverage: 6/76 with 70 NOT-DRAWN. Exact set: `live-runs/N2-0-3-8-mail-20260801-204500/mail-diff.csv`.
- Work: derive inbox rows, tabs, pagination, detail/compose states, attachments, money, fonts, and buttons and implement every absent reference element.
- Review: `live-runs/N2-0-3-8-mail-20260801-204500/mail-contact.png`.

## Q17 — 3-9 — auction NOT-DRAWN worklist and perceptual review

- Corrected coverage: 7/225 with 218 NOT-DRAWN. Exact set: `live-runs/N2-0-3-9-auction-20260801-210000/auction-diff.csv`.
- Work: derive browse/bid/auctions tabs, search, filters, list columns/rows, pagination, money, and buttons and implement every absent reference element.
- Review: `live-runs/N2-0-3-9-auction-20260801-210000/auction-contact.png`.

## Q18 — 3-10 — loot NOT-DRAWN worklist and perceptual review

- Corrected coverage: 4/20 with 16 NOT-DRAWN. Exact set: `live-runs/N2-0-3-10-loot-20260801-211500/loot-diff.csv`.
- Work: derive title, pager states, close states, and all populated item/coin row evidence and implement every absent reference element.
- Review: `live-runs/N2-0-3-10-loot-20260801-211500/loot-contact.png`.

## Q19 — 3-11 — guild NOT-DRAWN worklist and perceptual review

- Corrected coverage: 1/63 with 62 NOT-DRAWN. Exact set: `live-runs/N2-0-3-11-guild-20260801-213000/guild-diff.csv`.
- Work: derive roster columns/rows, totals, MOTD/info tabs, rank controls, notes, filters, and every absent embedded GuildFrame reference element.
- Review: `live-runs/N2-0-3-11-guild-20260801-213000/guild-contact.png`.

## Q20 — 3-12 — gossip NOT-DRAWN worklist and perceptual review

- Corrected coverage: 6/23 with 17 NOT-DRAWN. Exact set: `live-runs/N2-0-3-12-gossip-20260801-214500/gossip-diff.csv`.
- Work: derive portrait/name/greeting material, option icons/rows, greeting text, close/goodbye states, and every absent reference element.
- Review: `live-runs/N2-0-3-12-gossip-20260801-214500/gossip-contact.png`.

## Q21 — 3-13 — taxi NOT-DRAWN worklist and perceptual review

- Corrected coverage: 5/12 with 7 NOT-DRAWN. Exact set: `live-runs/N2-0-3-13-taxi-20260801-220000/taxi-diff.csv`.
- Work: derive portrait/title/dynamic continent map, real node buttons/route line, close states, and resolve an authoritative TEST-realm flight-master descriptor.
- Review: `live-runs/N2-0-3-13-taxi-20260801-220000/taxi-contact.png`.

## Q22 — 3-14 — trade NOT-DRAWN worklist and perceptual review

- Corrected coverage: 5/129 with 124 NOT-DRAWN. Exact set: `live-runs/N2-0-3-14-trade-20260801-221500/trade-diff.csv`.
- Work: implement authoritative trade wire/state, player/target slots, enchant slots, portraits/names, money, accept/cancel states, highlights, and every absent reference element.
- Review: `live-runs/N2-0-3-14-trade-20260801-221500/trade-contact.png`.

## Q23 — 4-1 — game-menu NOT-DRAWN worklist and perceptual review

- Corrected coverage: 10/43 with 33 NOT-DRAWN. Exact set: `live-runs/N2-0-4-1-game-menu-20260801-223000/game-menu-diff.csv`.
- Work: derive title font, backdrop nine-slice properties, every button texture state/font/color/layer, and implement every absent reference element.
- Review: `live-runs/N2-0-4-1-game-menu-20260801-223000/game-menu-contact.png`.

## Q24 — 4-2 — options NOT-DRAWN worklist and perceptual review

- Corrected coverage: 2/180 with 178 NOT-DRAWN. Exact set: `live-runs/N2-0-4-2-options-20260801-224500/options-diff.csv`.
- Work: rebuild the vanilla 450x665 video/sound/interface tabs, group boxes, dropdowns, sliders, checks, labels, reset/default/okay/cancel buttons, and instrument all real control draws.
- Review: `live-runs/N2-0-4-2-options-20260801-224500/options-contact.png`.

## Q25 — 4-3 — keybindings NOT-DRAWN worklist and perceptual review

- Corrected coverage: 7/249 with 242 NOT-DRAWN. Exact set: `live-runs/N2-0-4-3-keybindings-20260801-230000/keybindings-diff.csv`.
- Work: implement editable binding state/persistence, categories, every binding row/label/button state, defaults/account-character scope, okay/cancel, header/font, and remaining vanilla elements.
- Review: `live-runs/N2-0-4-3-keybindings-20260801-230000/keybindings-contact.png`.

## Q26 — 4-4 — macro NOT-DRAWN worklist and perceptual review

- Corrected coverage: 6/158 with 152 NOT-DRAWN. Exact set: `live-runs/N2-0-4-4-macro-20260801-231500/macro-diff.csv`.
- Work: persist and execute macros, implement account/character tabs, complete icon selector/grid, selected icon/name/body/limit, buttons, fonts, separators, and every absent reference element.
- Review: `live-runs/N2-0-4-4-macro-20260801-231500/macro-contact.png`.

## Q27 — 4-5 — tooltip mechanical findings and perceptual review

- Coverage: 1/1; exact verdicts: `live-runs/N2-0-4-5-tooltip-20260801-233000/tooltip-diff.csv`.
- Work: route ubiquitous tooltip call sites through the dedicated shipped-art path and extend draw evidence to carry backdrop paths plus TOOLTIP strata without copying reference declarations.
- Review: `live-runs/N2-0-4-5-tooltip-20260801-233000/tooltip-contact.png`.

## Q28 — 4-6 — UIErrorsFrame mechanical findings and perceptual review

- Coverage: 2/2; exact verdicts: `live-runs/N2-0-4-6-ui-errors-20260801-234500/ui-errors-diff.csv`.
- Work: expose ErrorFont object identity and HIGH strata in draw evidence, remove the empty child anchor metadata, and reconcile the gold FrameXML font-object default with the red error-feed runtime color law.
- Review: `live-runs/N2-0-4-6-ui-errors-20260801-234500/ui-errors-contact.png`.

## Q29 — 4-7 — StaticPopup NOT-DRAWN worklist and perceptual review

- Coverage: 5/17 with 12 NOT-DRAWN; exact set: `live-runs/N2-0-4-7-static-popup-20260802-000000/static-popup-diff.csv`.
- Work: instrument close-button texture states and all dialog-button texture states, capture dynamic Lua button placement, expose backdrop metadata/strata, and add an item-cursor reference specification when one is locatable in shipped reference data.
- Review: `live-runs/N2-0-4-7-static-popup-20260802-000000/static-popup-contact.png`.

## Q30 — 5-1 — font-object audit findings

- Exact audit: `live-runs/N2-0-5-1-font-audit-20260802-001500/font-object-audit.csv`.
- Counts: 375 rows; 3 PASS, 11 DELTA, 290 NOT-DRAWN, 71 NO-VERDICT.
- Work: implement every absent text draw, expose named font-object identity from the real draw path, then resolve each remaining size/object delta without reference-metadata copying.

## Q31 — 5-2 — color-constant audit findings

- Exact audit: `live-runs/N2-0-5-2-color-audit-20260802-003000/color-constant-audit.csv`.
- Counts: 30 constants; 14 PASS, 2 DELTA, 14 NOT-DRAWN after correcting mana, rage, energy, and happiness.
- Work: implement class and quest-difficulty color consumers; resolve the epic rounding and dead-NPC gray byte deltas.
