# 006: Renderer used only pulls, so proven-safe mobs still drew full-size shapes
**Status:** ✅ FIXED
**Date:** 2026-08-09
**Keywords:** negative evidence, rendering, classification, aggro, false danger

## Symptom

"This Crescent Nanka enemy is recording nothing when I get into range. It
continues to show a large circle around it when I'm CLEARLY safe when behind it,
even when close." A screenshot showed the player standing on top of an
unaggroed Lv44 Nanka inside a roughly 20y red circle.

## Root Cause

Two separate things, and the first one masked the second.

**It *was* recording.** The log showed twelve angular slices of safe stands for
Nanka, including 3.5y at 134° and 3.9y at 116°. The data was fine.

**The renderer ignored all of it.** `Classify()` returns `Unknown` unless there
is at least one *pull*, because the range is defined as the furthest distance
the player was ever noticed from. A mob with `pulls = 0` therefore fell through
to the fallback path — the `BNpcBase` sight/sound flag plus the global slider —
and drew a full-size circle regardless of how much evidence existed that its
reach was small.

Several mobs were in that state at once: Banemite, Belladonna, Bile, Soblyn and
others all had rich safe data and zero pulls, so all of them drew wrong.

The user's framing was the correct one: *"We need to record when I DO NOT pull
an enemy because that proves a SAFE AREA."*

## Attempts

| Date | Attempt | Result |
|------|---------|--------|
| 2026-08-09 | Record safe stands into the per-angle envelope | Recorded correctly, but had no effect on what was drawn |
| 2026-08-09 | Cap the drawn reach per angle by the safe bound, including with zero pulls | Fixed the over-drawing |

## Resolution / Lesson

Drawing and the can-it-see-me test share one path: classification supplies the
model where there is one, safe stands cap the reach per angle either way, and
the fallback only fills the gaps evidence leaves.

**Lesson: evidence of absence is evidence.** A model that can only ever grow is
not a model — it is a high-water mark. Walking up behind something and not being
noticed proves that area is safe exactly as firmly as a pull proves the
opposite, and a system that records one but not the other will drift
permanently in one direction.

**Lesson: recording data and *using* it are separate features.** The samples
were captured perfectly and the failure was entirely downstream. When a user
reports "it isn't recording", confirm whether the data exists before touching
the capture path — the log settled this in one query and the capture code was
never at fault.
