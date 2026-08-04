# Plan NN — <short title>

> Copy this to `PLAN_NN_NAME.md` and fill it in **before** writing code. This is the
> working agreement (`FOUNDATION_PLAN.md` §5, §11–12). Two hard rules the foundation
> earned:
>
> - **If you can't fill field 7 (Test protocol), stop** — you're missing an
>   instrument, and building it is the real next task.
> - **When steps interlock, plan the set before fixing the sequence** — each of Plans
>   02–05 changed an earlier one.
>
> Delete this quote block and the parenthetical hints once the plan is written.

## 1. Problem
State it **from a vantage**: "at `<vantage>`, `<what looks/feels wrong>`." Attach the
paired artifact — your screenshot + `dumps/<vantage>.json`, and for emulation-core
work the `refs/<vantage>.png` real-client capture.

## 2. Class
**Emulation-core** (measured against the real 1.12 client) or **addition** (measured
against your intent)? This picks the yardstick (§2). First question on every item,
because it decides whether you go capture a real-client reference or write down an
intent.

## 3. Target
Core: the `refs/<vantage>.png` and what it shows. Addition: a written note of what you
want.

## 4. Hypotheses  *(tooling steps: rename to "Key design decisions")*
Ranked, each **falsifiable by the dump**. "It's the impostor classifier" is a
hypothesis a reason code confirms or kills in one dump.

## 5. Resources
The WoWee / SuperUI files and handbook §§ that bear on it (handbook §10 maps many).
**Check before writing** — the handbook records writing from scratch what already
existed, twice. Note exact source files + line ranges.

## 6. Tools / instrument
Which existing instrument tests it — vantage, scene dump, reason codes, group picker,
override DB, collision draw, solo / bind-pose. **If none can isolate the suspect,
building the instrument is task one.**

## 7. Test protocol
The exact vantage, the dump fields that confirm/refute, and the visual you'll check —
written **before** the change so we can't fool ourselves after. Diff two dumps from
the same vantage; the changed reason code is the effect.

## 8. Definition of done
Core: matches `refs/<vantage>.png`; the dump explains any residual difference.
Addition: matches intent by eye, A/B'd from the vantage.

## 9. Fallback
The smallest partial win, or the override DB for a visibility case, so no step can
hard-block.

## 10. Reconciliation
What this changes in earlier plans, and the build-order impact.
