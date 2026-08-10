# 007: Free-form envelope drew a jagged staircase instead of a shape
**Status:** ✅ FIXED
**Date:** 2026-08-09
**Keywords:** rendering, envelope, cone, radius, domain model, readability

## Symptom

"You've stopped drawing the cone shape...what shape even is that" — with a
screenshot of a blocky, stepped polygon fragment sitting beside the mob,
unrecognisable as any kind of detection area.

## Root Cause

The renderer walked the per-angle envelope and drew each slice's bound
literally. Each of the twelve slices holds whatever distance the player happened
to stand at when the evidence was gathered, so neighbouring slices differ
arbitrarily — 3.5y here, 12.8y next door — and the outline came out as a
staircase. Slices proven safe to the hitbox produced gaps, breaking the outline
into disconnected fragments.

The deeper mistake was modelling. Trist had already stated the domain rule:
**the game implements exactly two detection shapes, cone and radius.** Drawing a
free-form outline was inventing a third that does not exist, so no amount of
smoothing would have made it correct — it was answering the wrong question.

## Attempts

| Date | Attempt | Result |
|------|---------|--------|
| 2026-08-09 | Draw the per-angle envelope directly | Jagged, broken, unrecognisable |
| 2026-08-09 | Draw the classified cone or circle; keep raw evidence for choosing and sizing it | Fixed |

## Resolution / Lesson

- A classified mob draws a clean cone or circle from its model. The evidence
  already went into deciding which shape and how big.
- An unclassified mob with evidence draws a smoothed bound — interpolated
  between slice centres rather than stepped, and never broken by gaps.
- Raw slice values remain visible in the Mob Viewer's envelope table, which is
  where per-slice detail actually belongs.

**Lesson: present the domain's shapes, not the data's shape.** Measurements are
noisy samples of an underlying model. The user should see the model; the samples
belong in a table. Drawing the raw measurement outline exposed sampling noise as
if it were geometry.

**Lesson: when the domain expert states a constraint — "only cone or radius
exist" — that is a modelling instruction, not a description.** It should have
collapsed the free-form envelope into a classifier immediately rather than being
treated as background colour.
