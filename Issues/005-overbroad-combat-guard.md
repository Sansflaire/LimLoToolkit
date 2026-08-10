# 005: In-combat guard rejected nearly every real pull
**Status:** ✅ FIXED
**Date:** 2026-08-09
**Keywords:** filters, proxy conditions, links, aggro training, sample starvation

## Symptom

Almost nothing was recorded during normal play. The log showed a single `REC`
line against a run of skips:

```
19:56:05  REC  Crescent Oiseau Rare — 4.9y at 3° off its facing. Sample 3/8.
19:56:09  SKIP Crescent Oiseau Rare — you were already in combat...
19:56:13  SKIP Crescent Oiseau Rare — you were already in combat...
```

Reported as "I aggroed three of them" with nothing to show for it.

## Root Cause

The guard rejected any pull where the player was already in combat. Its actual
purpose was to reject **links** — a mob dragged in by a neighbour rather than
noticing the player — and "player is in combat" was used as a proxy for that.

The proxy is reasonable in the open world, where you pull one thing, kill it,
and move on. It collapses in the Occult Crescent, where the player is in combat
almost continuously, so the guard rejected essentially every genuine detection.

The condition was never wrong in the sense of admitting bad data. It was wrong
in that it excluded almost all the good data too.

## Attempts

| Date | Attempt | Result |
|------|---------|--------|
| 2026-08-09 | Reject any pull while the player is in combat | Rejected nearly everything real |
| 2026-08-09 | Reject only when an engaged mob is within link range of the new one | Fixed |

## Resolution / Lesson

A pull is now rejected only when:

- the mob was already in combat (it did not just notice anything), or
- another mob currently engaged with the player is within 12y of it and could
  plausibly have linked it in.

The player's own combat state is no longer disqualifying. The burst debounce
also dropped from 1000ms to 250ms, since walking into a camp can legitimately
trip several mobs at once and each is a real proximity detection.

**Lesson: write the guard against the thing you actually want to exclude, not a
proxy that happens to correlate with it in the environment you tested in.** The
target was links; the test was combat state. In the one zone this plugin is for,
that correlation does not hold at all.

**Corollary:** when a filter's rejection rate is far higher than expected, that
is a signal about the filter, not about the user's behaviour. The skip messages
made this obvious the moment they were visible — see issue 004.
