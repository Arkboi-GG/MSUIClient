# NIGHT_01C 2-12 — character sheet and inventory

Run time: 2026-08-01 12:05 local

Status: `CLOSED-PASS`

## Prediction / actual / result

```text
PREDICTED: prove server-authoritative character stats, exact equip/unequip wire,
           durability, and item-template STRING fields against read-only DB values
ACTUAL:    expanded parser/verdict/runner family passes; live GM-off run is 25/25
RESULT:    accepted — equip, unequip, damage delta, stats, durability, and strings pass
```

## Protocol and parser increment

Deployed read-only vmangos source was checked before implementation:

- `Handlers/ItemHandler.cpp:61`: inventory swap handler.
- `Handlers/ItemHandler.cpp:138`: auto-equip handler.
- `Handlers/ItemHandler.cpp:295-390`: exact item-query response order, including
  names, inventory type, item/required level, ten stat pairs, five damage rows,
  armor/resistances, spells, bonding, and description.

The client now retains those previously skipped STRING fields and renders damage,
armor, stats, binding, durability, levels, and description in tooltips. Exact
autoequip/swap body builders and byte checks were added. Copyable inventory
verdicts expose item location, template strings, durability, send bodies, and the
independent server-authoritative location transition. Character verdicts expose
the update-field stats used by the visible paper doll.

## Autonomous provisioning and cross-check

Before first use, `.additem <item> [count]` was verified in deployed
`CharacterCommands.cpp:3267-3335`. The read-only world DB row for item 25 was:

```text
entry=25 name=Worn Shortsword class=2 subclass=7 quality=1 inventoryType=21
itemLevel=2 requiredLevel=1 damage=1..3 school=0 delay=1900
armor=0 bonding=0 material=1 sheath=3 maxDurability=20
```

The TEST character was GM-provisioned one item, then GM was disabled. The live
runner passed 25/25. The decoded server template matched the DB STRING row:
`Worn Shortsword`, quality 1, inventory type 21, item level 2, required level 1,
and durability 20/20.

The exact live transitions were:

```text
equip send:    bag=255 slot=27 body=FF1B
equip result:  item GUID moved to equipment slot 15
unequip send:  source=15 destination=28 body=0F1C
unequip result:item GUID moved to backpack slot 5
```

The update-field snapshot remained server-authoritative throughout. Unequipping
the weapon changed displayed melee damage from `4.9357142–6.9357142` to
`4.142857–4.142857`, while the other character fields remained coherent:
level 1, health 60/60, stats 23/20/22/20/21, armor 47, attack power 29.

Evidence:

- `live-runs/runner-20260801-115855.csv`
- `live-runs/verdicts-20260801-115855.txt`
- `live-runs/N1C-2-12-character-inventory-ui-20260801-120000.png`

## Boundary gates

- Debug build: PASS, 0 warnings / 0 errors
- combat-wire-check: PASS
- interface-wire-check: PASS
- portrait-camera-check: PASS, 10,534 / 1,224 / 1,289 / 56
- move-audit-check: PASS

Manifest: `live-runs/manifests/N1C-2-12-20260801-120500.sha256`.
