# BROKEN.md — Post-Mortems

Resolved issues that shipped or nearly shipped, each with the lesson that
prevents a recurrence. Past tense. This file is an **index** — details live in
`Issues/<ID>-<slug>.md`.

**Read this before every commit, build, or push.** Also read the suite-wide
`devPlugins/BROKEN.md`, which catalogs failures that reached real users across
every plugin (missing `AssemblyVersion` in the bundled manifest, wrong
`DalamudApiLevel`, CI missing `contents: write`, stale dev DLLs, and more).

## Fixed

| ID | Summary | Lesson | Details |
|----|---------|--------|---------|
| 001 | A PowerShell version-bump one-liner turned the em-dash in the plugin `Description` into `â€”` in both manifests | Never round-trip repo files through `Get-Content`/`Set-Content` in PowerShell 5.1 — it reads BOM-less UTF-8 as ANSI, and `-Encoding utf8` on the write does not save you. Use Edit/Write. Grep for `â€` before committing a manifest | [001-powershell-utf8-mangling.md](Issues/001-powershell-utf8-mangling.md) |
| 002 | Every training sample collected was silently discarded on plugin reload | Data the user spends real effort producing must be written the moment it exists, to its own file. Never let durability depend on an unrelated code path happening to save | [002-training-data-lost-on-reload.md](Issues/002-training-data-lost-on-reload.md) |
| 003 | A plugin reload made every already-aggroed mob look like a fresh pull, recording 40y+ ranges and painting enormous shapes that returned after every rebuild | Transient state wiped by a reload must not be indistinguishable from a real event. Require a warm-up window before trusting an edge, and bound values so impossible data cannot enter the store | [003-reload-poisons-aggro-data.md](Issues/003-reload-poisons-aggro-data.md) |
| 004 | Pulls from ignored mobs were dropped with no message, looking exactly like a broken trainer | A filter that silently swallows the thing the user is watching for is indistinguishable from a bug. Every rejection says which rule rejected it and why | [004-silent-filters.md](Issues/004-silent-filters.md) |
| 005 | The in-combat guard rejected nearly every real pull, because Crescent play is continuous combat | Write the guard against the thing you actually want to exclude — a *link* — not a proxy that happens to correlate with it. Proxy guards fail wholesale in the environment they were never tested in | [005-overbroad-combat-guard.md](Issues/005-overbroad-combat-guard.md) |
| 006 | The renderer used only pulls, so mobs with twelve slices of proof they were harmless still drew full-size circles | Evidence of absence is evidence. If the model can only grow, it is not a model — and the drawing must show everything that is known, not just the half that is convenient to compute | [006-negative-evidence-ignored.md](Issues/006-negative-evidence-ignored.md) |
| 007 | Drawing raw per-slice bounds produced a jagged staircase that read as no shape at all | The game implements exactly two detection shapes. Present the classified shape; use raw evidence to choose it and size it, never as the outline itself | [007-jagged-envelope-rendering.md](Issues/007-jagged-envelope-rendering.md) |
| 008 | Trainer diagnostics were logged at Debug, which Dalamud filters out, so a live failure left no trace | Anything needed to diagnose a failure in the field must survive default log settings. Debug is invisible in `dalamud.log` | [004-silent-filters.md](Issues/004-silent-filters.md) |
| 009 | The drawn cone visibly grew and shrank as the player circled a mob | A drawn shape is a property of the thing drawn, never of the observer's position. "How big is it" and "can it see me *here*" are different questions and must not share a value | [009-cone-size-follows-player.md](Issues/009-cone-size-follows-player.md) |
| 012 | Minimap dots sat offset from the player and orbited as he turned | `AtkResNode.ScreenX/Y` is the transformed position of local (0,0), NOT an axis-aligned top-left — so `+ Width/2` on a node that ROTATES (the player arrow does) swings the whole frame in a circle. Prefer an unrotating sibling node (`MapBase`) to clever maths. Two symptoms are usually one bug: I treated "offset" and "shifts as I turn" as separate faults and invented a cause for the second, including wrongly accusing `NorthLockedUp`. Three rounds of inference from screenshots lost to what one live `/debug/structread` answered in minutes | [012-minimap-centre-from-a-rotating-node.md](Issues/012-minimap-centre-from-a-rotating-node.md) |
| 011 | Mob silhouettes were never seen; the only switch was buried in a tool panel rather than Settings. **The first post-mortem for this misdiagnosed the rendering cause and was retracted** | A plausible mechanism is not a diagnosis. A partial probe found a real bitfield detail and a wrong conclusion was built on it — the low nibble of `OutlineFlags` is `LoadState`, not an enable bit, so the direct write was never missing one. Finish the probe before writing the conclusion, and prefer "I don't know" to a tidy story: a wrong post-mortem reads as settled to the next person. The mundane half — a default-off feature whose switch was not where its neighbours live — was sufficient to explain the report and got no scrutiny | [011-outline-bitfield-half-written.md](Issues/011-outline-bitfield-half-written.md) |
| 010 | Aggro happened outside the drawn shape — a slice that had produced a pull was also counted as "safe outside", cutting the arc back across its own proof | **THE INVARIANT: if it caught you at that angle and distance, the drawing covers that angle and distance.** Under-drawing walks the user into a pull; over-drawing is only an annoyance. Never let safety evidence override danger evidence | [010-aggro-outside-drawn-shape.md](Issues/010-aggro-outside-drawn-shape.md) |

## How to use this file

- An issue starts in `KNOWN-ISSUES.md`. Once resolved and understood, it moves
  here with its lesson.
- Add the entry **before** moving on to the next task, not later.
- This file grows. It does not shrink — a fix in CI today can regress tomorrow.

## The recurring theme

Six of the eight entries are the same failure in different clothes: **the
plugin knew something and threw it away silently.** Samples discarded on
reload, pulls dropped by a filter with no message, non-detections never
recorded, diagnostics logged below the visible level. Each one presented to the
user as "it just isn't working" with nothing to go on.

When adding anything that discards, filters, or defers data, the question to
ask is: *if this path is wrong, how would anyone find out?* If the answer is
"they wouldn't", that path needs a message before it ships.
