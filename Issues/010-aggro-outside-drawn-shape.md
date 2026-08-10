# 010: Aggro occurred outside the drawn detection shape
**Status:** ✅ FIXED
**Date:** 2026-08-09
**Keywords:** invariant, cone, arc, safety, under-draw, classification, aggro

## Symptom

"I am LITERALLY GETTING AGGRO IN AREAS OUTSIDE OF CIRCLES AND CONES." The
drawing claimed an area was safe where the mob demonstrably pulled.

This is the worst failure direction this plugin has. Over-drawing is an
annoyance; under-drawing walks the user into a pull while telling them they are
fine.

## Root Cause

Two compounding causes.

**1. A pull slice could mark itself "outside the arc."** The cone edge was
derived from the narrowest angle holding a safe stand closer than the range.
That scan considered *every* slice — including slices that had themselves
produced pulls. A slice with both a pull and a closer safe stand therefore
pushed the arc boundary inward across its own pull:

```
Crescent Nanka:    pull at 37.5° / 6.4y   arc drawn to 36.9°   <- pull excluded
Crescent Accursed: pull at 37.5° / 6.1y   arc drawn to 36.5°   <- pull excluded
```

**2. Safe stands were briefly allowed to shorten the range** (a change made
minutes earlier, attempting to make shapes shrink as the player walked through
them unnoticed). That capped the range below recorded pulls and was *sticky* — a
new, longer pull could not grow the range back, because the old safe reading
still capped it. That is why fresh aggro appeared "not to register" when the
log showed it recorded correctly.

Both are the same mistake: **evidence of safety was allowed to override evidence
of danger.**

## Attempts

| Date | Attempt | Result |
|------|---------|--------|
| 2026-08-09 | Shrink range to the closest safe stand inside the arc | Under-drew badly; shapes collapsed and could not recover |
| 2026-08-09 | Revert the shrink | Stopped the collapse; the arc bug remained |
| 2026-08-09 | Exclude pull slices from the safe-outside scan, plus a hard floor on the arc | Fixed; verified against real data |

## Resolution / Lesson

- A slice that has produced a pull is inside the arc by definition and is
  skipped entirely when looking for the cone edge.
- The arc has a hard floor at the outer edge of the widest pull slice, so no
  later reasoning can cut a pull out.
- Range remains the maximum recorded pull. Safe stands never shorten it.

**Verified, not asserted.** A script replayed the classifier over the live data
file and checked every slice that had produced a pull against the shape that
would be drawn. Before: 2 violations. After: 0 across all profiles with pulls.
That check is worth re-running whenever the classifier changes.

**Lesson: state the safety invariant explicitly and make it structural.** "If it
caught you at that angle and distance, the drawing covers that angle and
distance" is not a nice-to-have that emerges from good logic — it needs to be a
floor applied last, after every other calculation, precisely so that no clever
reasoning elsewhere can violate it.

**Lesson: asymmetric failure costs demand asymmetric handling.** Danger evidence
and safety evidence are not equal inputs to average together. When they
conflict, danger wins and the conflict is surfaced for a human to judge.
