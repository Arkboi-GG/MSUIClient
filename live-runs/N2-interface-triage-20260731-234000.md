# NIGHT_01 Tier 0-2 — gameplay-interface whole-item triage

The parent explicitly requires whole-item shelving instead of half-building
several flows under time pressure. No interface mutation or transaction was
attempted.

## Actual versus predicted by item

| Item | Predicted complete loop | Actual current-tree readiness | Status |
|---|---|---|---|
| 2-1 Vendor | gossip/list/buy/sell/buyback + errors + DB costs | vendor opcode/state/UI/runner families absent; read-only server sites located in `ItemHandler.cpp:451,619,667,696,701` and `Opcodes.cpp:499-504,748` | `SHELVED-BLOCKED` |
| 2-2 Trainer | list/states/learn/spellbook | trainer client protocol/state/UI/runner absent | `SHELVED-BLOCKED` |
| 2-3 Questing | status/detail/accept/objectives/reward/abandon | quest client protocol/state/UI/runner absent | `SHELVED-BLOCKED` |
| 2-4 Loot | complete corpse/money/release/error live flow | substantial solo-loot client path exists, but kill production is blocked behind the unresolved accepted attack discard; no safe complete live fixture | `SHELVED-BLOCKED` |
| 2-5 Bank | open/deposit/withdraw/bag purchase | bank protocol/state/UI/runner absent | `SHELVED-BLOCKED` |
| 2-6 Mail | inbox/send/take/return/delete | mail protocol/state/UI/runner absent | `SHELVED-BLOCKED` |
| 2-7 Auction | browse/bid/buyout/create/cancel/mail | auction protocol/state/UI/runner absent | `SHELVED-BLOCKED` |
| 2-8 Professions | recipes/reagents/craft/skill/learn | tradeskill protocol/state/UI/runner absent | `SHELVED-BLOCKED` |
| 2-9 Guild | create/roster/ranks/leave/disband | guild protocol/state/UI/runner absent | `SHELVED-BLOCKED` |
| 2-10 Tabard | designer/purchase/render | tabard transaction protocol/state/UI/runner absent; perceptual render ruling also outside authority | `SHELVED-BLOCKED` |
| 2-11 Talents | class panels/spend/confirm/unlearn | only point field display exists; talent protocol/tree/UI/runner absent | `SHELVED-BLOCKED` |
| 2-12 Character/inventory | stats/equip/durability/tooltips cross-check | local sheet/inventory surfaces exist, but no complete scripted transaction/DB cross-check instrument | `SHELVED-BLOCKED` |

Static inventory found no `Vendor`, `Mail`, `Auction`, `Guild`, `TradeSkill`,
`Craft`, `Gossip`, or `Taxi` implementation files/symbol families in the client.
The vendor read-only source probe additionally confirmed the deployed 1.12 row
shape in `ItemHandler.cpp:735-812` and opcode values 414-421/656. This is useful
navigation evidence, not a claim that the client implements it.

Recommendation: issue a separate interfaces work order, priority 2-1 → 2-3 →
2-4, with one complete root cause per flow. Do not merge speculative packet
families into an overnight triage commit.

Boundary gates: Debug build PASS with only established CA2014 and 0 errors;
combat-wire PASS with established CA2014 only; portrait-camera PASS with 10,534
specimens and controls 1,224 / 1,289 / 56; move-audit-check PASS.
