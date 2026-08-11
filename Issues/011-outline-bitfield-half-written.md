# 011 — Mob silhouettes were never seen, and had no switch where anyone would look

**Status:** Fixed (discoverability). Rendering cause **corrected on 2026-08-10** —
see the retraction below.
**Found:** 2026-08-10 — reported as "the silhouettes aren't working, I do not see
them and they are not in the settings to turn on/off"
**Shipped in:** 0.17.0.0 (the feature), 0.18.0.0 (first fix attempt, wrong
diagnosis), 0.19.0.0 (corrected)

## Symptom

Nothing was ever drawn, and the only switch for the feature lived inside the
Enemy Vision tool panel. The previous release had moved the Mob Viewer filters
into Settings, so Settings was where a toggle was expected — and it was not
there. From the outside, "the switch is missing" and "the feature is broken" are
the same report.

## RETRACTION — the first diagnosis was wrong

The 0.18.0.0 post-mortem claimed the cause was this line:

```csharp
native->DrawObject->OutlineColor = ObjectHighlightColor.Red;
```

writing "only the high nibble of `OutlineFlags`, leaving the low nibble at zero
so the outline pass never picked the object up."

**The first half is true and the conclusion does not follow.** A fuller probe of
`DrawObject`'s property accessors shows what the low nibble actually is:

| Property | Getter IL | Bits of `OutlineFlags` (`+0x89`) |
|----------|-----------|----------------------------------|
| `LoadState` | `ldfld OutlineFlags; ldc.i4.0; ldc.i4.4; call GetBitfieldValue` | 0–3 |
| `OutlineColor` | `ldfld OutlineFlags; ldc.i4.4; ldc.i4.4; call GetBitfieldValue` | 4–7 |

The low nibble is `LoadState`. It is not an enable bit, and the direct write was
not missing one. `OutlineColor` occupies the whole of its own field. The claimed
mechanism was invented to fit the symptom.

FFXIVClientStructs' own wording should have been the tell. It says to prefer
`GameObject.Highlight` **"as it makes sure that it also highlights a character's
weapon(s), mount and ornament"** — a completeness argument, not a correctness
one. If the direct write did not work at all, that is not how it would be
phrased.

**What the cause most likely was:** `ShowMobOutlines` defaults to `false`, and
its only checkbox was buried in a tool panel the user had no reason to scroll
to. The reported half of the bug — "not in the settings to turn on/off" — is
very probably the whole of it. This has not been confirmed by observation, and
saying so is the honest position.

## Fix

- The toggle moved to **Settings → World Overlays**, next to the other world
  overlays. The Enemy Vision panel keeps a status line, not a second switch.
- `MobOutlines` now calls `GameObject.Highlight(colour, includeMount: true)`
  (`VirtualFunction` index 26). This is kept — it is the game's own routine and
  it covers weapon, mount and ornament — but it is a *correctness-by-default*
  change, **not** a proven fix for the invisibility.
- Added a real diagnostic instead of a guess: the panel now reads
  `GraphicsConfig.CharaOutline` (a `bool` at `+0x16`) and says so plainly if the
  game's own character-outline pass is switched off, because in that state
  nothing this plugin does can produce an outline.
- 0.19.0.0: only mobs that can actually aggro are outlined, in red. The black
  "harmless" outline is gone — an outline means danger, and a second colour
  saying "not danger" still reads as a warning.

## Lessons

**A plausible mechanism is not a diagnosis.** The bitfield finding was real, the
IL was read correctly, and the conclusion drawn from it was still wrong — the
half of the layout that mattered (`LoadState` in the low nibble) had not been
looked up. Finding *a* discrepancy in the neighbourhood of a bug is not the same
as finding *the* cause, and a partial probe is the easiest way to talk yourself
into one.

**Finish the probe before writing the conclusion.** Dumping every property
accessor on `DrawObject` — five extra lines of script — would have shown the low
nibble immediately. The first probe stopped as soon as it found something that
fit.

**Prefer "I don't know" to a tidy story.** The user was told a specific mechanism
with specific evidence. It was wrong, and a wrong post-mortem in the repo is
worse than none: the next person reads it as settled.

**When two faults are reported together, fix and verify them separately.** The
missing switch was real, verifiable, and sufficient to explain the report on its
own. It got no scrutiny because a more interesting cause had already been found.

**A feature with no visible switch does not exist.** Put a setting where its
neighbours are. When toggles are consolidated into Settings, sweep for
stragglers left behind in tool panels in the same change.

## How it was probed

A throwaway `net10.0-windows` console app referencing
`addon/Hooks/dev/FFXIVClientStructs.dll` reflected the enum, dumped
`DrawObject`'s field offsets, printed the IL of every property accessor on
`DrawObject`, and searched the assembly for anything named outline/highlight.
That last sweep is what turned up both `GraphicsConfig.CharaOutline` and
`TargetSystem.OutlineInfo`. Same probe pattern as CLAUDE.md §3. Windows
PowerShell 5.1 cannot load these assemblies; do not try.

**No thickness parameter exists.** The sweep found exactly two outline-related
things outside `DrawObject`: `GraphicsConfig.CharaOutline` (a bool) and
`TargetSystem.OutlineInfo` (two `GameObject*` — mouseover and soft target).
Neither is a width. The silhouette's thickness belongs to the game's render pass
and is not reachable from any published struct.
