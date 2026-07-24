# Plan 06 — Per-step template + reference folder

**Foundation build step 6** (`FOUNDATION_PLAN.md` §5, §3.3). Turn the way we planned
Plans 01–05 into a reusable working agreement, and set up the real-client capture
convention that makes "side-by-side with vanilla" (our definition of done) a routine
step instead of an afterthought.

This step is mostly process and files, not code — it's the working agreement that
every *feature* plan after the foundation will follow.

> **Build-order note.** Adoptable anytime, but most useful once the dump (02) and
> vantages (01) exist so the paired artifact it standardizes is real. Order:
> 01 → 03 → 02 → 04 → 05 → **06**.

---

## 1. Problem

We just proved (Plans 01–05) that thinking a step through — problem, class, target,
resources, test, done, fallback — before touching code catches interlocks and
prevents the random walk. But that's only captured as five ad-hoc documents. Without
a template and a convention, the next feature slides back into "try a reasonable
thing." And the real-client comparison that defines "done" for emulation-core work
has no standard capture, so it keeps being improvised.

## 2. Class

Process. Done when the next feature is planned in the template *before* code, and the
first real-client reference is captured.

## 3. Target

Three artifacts in the repo:

1. **`PLAN_TEMPLATE.md`** — the fillable per-step template.
2. **A repo convention** — where plans, vantages, dumps, overrides and references
   live, and how a "paired artifact" is named.
3. **`refs/`** — real-client screenshots keyed by vantage name, the ground truth for
   emulation-core "done."

## 4. The template (`PLAN_TEMPLATE.md`)

Fields, in order (the §5 template, hardened by what Plans 01–05 taught):

1. **Problem** — stated *from a vantage* ("at `sw-front-gate`, the keep is missing").
   Attach the paired artifact.
2. **Class** — emulation-core (measured vs the real 1.12 client) or addition
   (measured vs your intent). Decides the yardstick (`FOUNDATION_PLAN` §2).
3. **Target** — for core: the `refs/<vantage>.png` and what it shows. For additions:
   a written note of what you want.
4. **Hypotheses** — ranked, each *falsifiable by the dump*. (Tooling steps replace
   this with "Key design decisions," as Plans 01–05 did.)
5. **Resources** — the WoWee / SuperUI files and handbook §§ that bear on it (§10 of
   the handbook maps many). Check before writing; the handbook records writing from
   scratch what already existed, twice.
6. **Tools / instrument** — which existing instrument tests it. **If none can
   isolate the suspect, the first task is building the instrument, not the fix.**
7. **Test protocol** — the exact vantage, the dump fields that confirm/refute, the
   visual you'll check — written *before* the change.
8. **Definition of done** — core: matches `refs/<vantage>.png`, dump explains any
   residual. Addition: matches intent by eye, A/B'd from the vantage.
9. **Fallback** — the smallest partial win, or the override DB for visibility, so no
   step can hard-block.

Plus the two rules Plans 01–05 earned:

- **A plan that can't fill field 7 isn't ready** — that's the signal an instrument
  is missing, which is the real next task.
- **Plan the set, not the step, when steps interlock** — Plans 02–05 each changed an
  earlier one; sketch neighbours before committing to sequence.

## 5. Repo conventions

| Artifact | Location | Git |
|---|---|---|
| Plans | `PLAN_NN_NAME.md` at repo root | committed |
| Foundation / handbook | `FOUNDATION_PLAN.md`, `PROJECT_HANDBOOK.md` | committed |
| Vantages | `vantages.json` (root) | committed (shared spots) |
| Overrides | `visibility_overrides.json` (root) | committed (shared truth) |
| Dumps | `dumps/<name>.json` | ignored (transient) |
| Real-client refs | `refs/<vantage>.png` | committed |

**Paired artifact** = a `dump`, a `screenshot`, and (for core) a `ref`, all sharing
the **vantage name**. That shared name is what lets your picture and my data and the
vanilla reference line up without ambiguity.

## 6. Reference capture (the real-client side)

For an emulation-core step: stand in the real 1.12 client at the vantage's position
and facing (its `.gps` shows coordinates; the vantage records `x/y/z/yaw` — get
close, the dump records any residual mismatch), screenshot, save as
`refs/<vantage>.png`. The plan's Target links it. A modest upfront cost per spot that
pays back every revisit. *(Optional later: the client overlays `refs/<vantage>.png`
at reduced opacity for direct alignment — a convenience, not part of this step.)*

## 7. Files touched

| File | Change |
|---|---|
| `PLAN_TEMPLATE.md` *(new)* | the template above |
| `refs/` *(new folder)* | first real-client captures for our initial vantages |
| `FOUNDATION_PLAN.md` | add a plan index + the adopted build order (reconciliation) |
| `.gitignore` | `dumps/` ignored; `refs/` committed |

## 8. Reconciliation with other plans

- Standardizes the artifacts Plans 01/02/04 produce. No code dependency; it's the
  agreement that governs every plan *after* the foundation.
- Its first real use is the building/visibility problem — the `FOUNDATION_PLAN` §8
  worked example — now runnable end to end once 01–05 exist.

## 9. Test protocol and definition of done

- The next feature after the foundation (the missing building) is written in
  `PLAN_TEMPLATE.md` form, with a `refs/` capture, before any code.
- A newcomer (or a cold session) can read `PLAN_TEMPLATE.md` + one filled plan and
  run the loop without further explanation.
- **Done:** template exists, conventions are documented, `refs/` has its first
  capture, and the working agreement is the default.

## 10. Fallback

None needed — it's process. The minimum viable version is `PLAN_TEMPLATE.md` + the
conventions table; the `refs/` overlay and index are additive.
