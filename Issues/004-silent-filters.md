# 004: Silent filters and invisible logs made a working trainer look broken
**Status:** ✅ FIXED
**Date:** 2026-08-09
**Keywords:** diagnostics, silent failure, ignore list, log level, Debug, observability

## Symptom

"I just pulled a Crescent Oiseau Rare and it registered nothing." Later: "this
enemy I'm trying now just IS NOT being recorded at all. I aggroed three of
them." The trainer appeared completely dead.

It was not. It was rejecting input for reasons it never mentioned.

## Root Cause

Two independent causes, both silence:

1. **The ignore list swallowed pulls.** `EnemyVisionTool` skipped ignored mobs
   with a bare `continue` before the trainer ever saw them. Trist had 22 mob
   types ignored via the one-click Ignore button, and a pull from any of them
   produced no message whatsoever — identical to the trainer being broken.

2. **Diagnostics were logged at Debug.** Dalamud filters Debug out of
   `dalamud.log` by default, so when the failure was investigated there was no
   trace of it at all. The one recorded sample that would have proved the
   pipeline worked was invisible.

The actual diagnosis only became possible after raising the log level, at which
point the log immediately showed both `REC` and `SKIP` lines with reasons and
the cause was obvious in seconds.

## Attempts

| Date | Attempt | Result |
|------|---------|--------|
| 2026-08-09 | Reasoned about possible causes from the code | Inconclusive; several plausible candidates |
| 2026-08-09 | Read the live config and found 22 ignored mob types | Identified the swallowing filter |
| 2026-08-09 | Raised trainer logging Debug → Information | Root cause visible immediately in `dalamud.log` |

## Resolution / Lesson

- Ignored mobs are still tracked, purely so their pulls can be reported:
  *"X pulled, but it is on your ignore list — not recorded."*
- Every rejection path names the rule that rejected it and why.
- Trainer logging is Information, so it survives default log settings.
- The panel gained a live activity log and an "Un-ignore all" button.

**Lesson: a filter that silently swallows the thing the user is watching for is
indistinguishable from a bug.** The user cannot see the difference between "the
code decided not to record this" and "the code is broken", and will reasonably
assume the latter.

**Lesson: diagnostics that do not survive default log settings do not exist.**
Debug-level logging is fine for noise, but anything needed to answer "why did
nothing happen?" in the field must be at Information or above.

The general test when writing any skip, filter, or early return on a path the
user is actively watching: *if this fires when it should not, how would anyone
find out?*
