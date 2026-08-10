# 002: Training data silently discarded on every plugin reload
**Status:** ✅ FIXED
**Date:** 2026-08-09
**Keywords:** persistence, SavePluginConfig, data loss, reload, aggro training, durability

## Symptom

Trist collected aggro measurements, the plugin rebuilt and hot-reloaded, and
every sample was gone. Reported as "You rebuilt and lost the data I just
collected. What the fuck, why?" — entirely justified, since each sample costs a
deliberate clean pull to produce.

## Root Cause

`AggroLearningStore.AddSample` mutated the in-memory `Configuration` object and
nothing ever wrote it out. The record path never called `SavePluginConfig`, and
`Plugin.Dispose` did not save either.

Config was therefore written only when the user happened to toggle an unrelated
checkbox in the UI. Everything collected since that last toggle died on unload.

The failure was invisible while playing: the panel counters read from the same
in-memory object, so the data looked present right up until the reload.

## Attempts

| Date | Attempt | Result |
|------|---------|--------|
| 2026-08-09 | Store samples in `Configuration.LearnedAggro` | Lost on every reload |
| 2026-08-09 | Move to a dedicated `aggro-training.json` with atomic writes, written per sample | Fixed; verified by a sample surviving a rebuild |

## Resolution / Lesson

Training data lives in its own file in the plugin config directory:

- Written the instant an accepted sample lands.
- Atomic: serialize to `.tmp`, copy the previous file to `.bak`, then move into
  place. A crash mid-write costs at most the newest sample.
- Load falls back to `.bak` if the main file is unreadable.
- Flushed again on unload as a backstop.
- Written once on startup if missing, so the storage path is proven working
  before anything depends on it.

**Lesson: data the user spends real effort producing must be written the moment
it exists, to storage it owns.** Never let durability depend on an unrelated
code path deciding to save. The plugin config object is a settings bag, not a
database — settings are cheap to recreate, measurements are not.

**Corollary that made this worse:** the loss was silent and deferred. Nothing
failed at the moment of collection; the data simply was not there later. When
adding a store, verify the round trip — write, reload, read back — rather than
assuming the write happened.
