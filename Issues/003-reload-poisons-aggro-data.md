# 003: Plugin reload recorded every already-aggroed mob as a fresh pull
**Status:** ✅ FIXED
**Date:** 2026-08-09
**Keywords:** transient state, edge detection, reload, aggro training, data poisoning, first-sight

## Symptom

"When it rebuilds and I open it again the radiuses for enemies are massive and
wrong." Detection shapes ballooned to 40y+ and came back after every rebuild,
because the bad data persisted.

## Root Cause

The trainer detects a pull as an *edge*: a mob's `TargetObjectId` was not the
player last frame and is this frame. That edge is computed against a
`Dictionary<ulong, bool>` of previous-frame state — which is transient and wiped
by a plugin reload.

On the first frame after a reload the dictionary is empty, so `wasTargeting`
read false for every mob. Any mob already chasing the player therefore looked
like a brand-new pull and was recorded at whatever range it happened to be —
frequently 40y or more, since it had been chasing for a while.

Reloading was the trigger, which is exactly what made it show up on every
rebuild during development.

## Attempts

| Date | Attempt | Result |
|------|---------|--------|
| 2026-08-09 | Guard on player-not-in-combat | Insufficient; the mob arrives aggroed while the player is out of combat |
| 2026-08-09 | Require a warm-up window of observation before trusting the edge | Fixed |

## Resolution / Lesson

Three layers:

1. A mob must have been under observation for at least the rotation-lookback
   window before any pull from it counts. Anything that arrives already aggroed
   is ignored and logged as such.
2. The plausible sample bound tightened from 50y to 25y — real aggro is well
   inside that, so impossible values cannot enter the store at all.
3. Profiles already holding an impossible sample are reset on load, so existing
   poisoned data heals itself without the user cleaning up.

**Lesson: transient state wiped by a reload must not be indistinguishable from
a real event.** An edge detector whose "previous" state is empty will fire on
everything the first time it runs. Any state-transition detector needs either a
warm-up period or an explicit "no previous observation" case that is treated as
*unknown* rather than as *false*.

**Second lesson: bound the values that can enter a store.** Even with the edge
bug present, a 25y sanity limit would have kept the damage to something
plausible rather than something that painted the whole screen.
