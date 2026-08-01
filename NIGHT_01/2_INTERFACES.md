# NIGHT_01 tier-1 doc 2 — gameplay interfaces

Parent: `TIER0_MASTER.md` item 0-2. All Tier-0 rules apply.

House pattern for EVERY interface item (the "interface loop"):
(a) enumerate the wire protocol involved (opcodes both directions,
cite handler file:line in the deployed vmangos checkout, read-only);
(b) build/extend the client instrument: verdict channel rows for each
protocol step, report=act, shown ⇒ copyable;
(c) scripted live protocol against 192.168.0.2 with the GM test account:
positive control first, then the real flow, run-dated artifacts + manifest;
(d) UI build-out where missing — additive, client-only, within fix
authority; screenshot/contact-sheet evidence for anything visual;
(e) verdicts: wire-correct = your acceptance; looks-right = contact sheet
into the queue for Nico's morning pass. Author a `SUB_2-n_*.md` when an
item needs tier-3 decomposition.

- **2-1 Vendor.** Gossip→vendor list, buy, sell, buyback; item costs vs
  DB (read-only query check); stack handling; error paths (no money, full
  bags).
- **2-2 Trainer.** Trainer list, availability states (level/money),
  learn flow, spell appears in spellbook (ties into 3-1).
- **2-3 Questing.** Giver status flags, gossip/quest detail, accept,
  log update, objective counters (kill/gather via live protocol),
  complete/reward choice, XP/money delta verification, abandon.
- **2-4 Loot.** Corpse loot window, loot roll-free solo flow, money
  loot, empty-corpse handling, loot-release; ties to 1-2 kills.
- **2-5 Bank.** Open, deposit/withdraw, bag slots purchase flow.
- **2-6 Mail.** Send (money/item/COD), inbox list, take attachments,
  return/delete; expiry fields displayed correctly.
- **2-7 Auction house.** Browse/search + pagination, bid, buyout, create
  auction (deposit math vs DB read-only), owner list, cancelled/won/
  outbid mail interplay with 2-6.
- **2-8 Crafting/professions.** Tradeskill window from spellbook,
  recipe list + reagent counts, craft cast (ties to 3-2 cast bar),
  skill-up range colors, learned-recipe delta.
- **2-9 Guild.** Create (charter flow if feasible, GM-create otherwise —
  record which), roster, MOTD, promote/demote, ranks, leave/disband.
- **2-10 Tabard.** Tabard designer flow, purchase, render on character
  (contact sheet → queue for perceptual pass).
- **2-11 Talents.** Panel per class of the test roster, point spend,
  server-side confirm, unlearn cost display.
- **2-12 Character sheet + inventory.** Stats vs server values (read-only
  DB/wire cross-check), equip/unequip flows, durability display, item
  tooltips vs DB fields (STRING columns, not pixels).

Items 2-1, 2-3, 2-4 are priority; if time pressure forces triage, shelve
whole items (SHELVED-BLOCKED "time") rather than half-doing several.
