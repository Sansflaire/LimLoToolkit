# 011 — Mob silhouettes never rendered, and had no switch where anyone would look

**Status:** Fixed
**Found:** 2026-08-10 — reported as "the silhouettes aren't working, I do not see
them and they are not in the settings to turn on/off"
**Shipped in:** 0.17.0.0 (the feature), broken from the moment it landed

## Symptom

Two failures wearing one coat, which is why the report reads as one bug.

1. Nothing was ever drawn. The feature had never worked, not once.
2. The only switch for it lived inside the Enemy Vision tool panel. The
   previous release had moved the Mob Viewer filters into Settings, so Settings
   was now where a toggle was expected to be — and it was not there. From the
   outside that is indistinguishable from "the feature does not exist".

## Cause — the rendering half

`MobOutlines.Apply` set the outline by writing the property directly:

```csharp
native->DrawObject->OutlineColor = ObjectHighlightColor.Red;
```

That looks like a plain field assignment and is not. `DrawObject.OutlineColor`
is a **bitfield occupying only the high nibble** of `DrawObject.OutlineFlags`
(byte at `+0x89`). Decompiling the accessors out of the shipped
`FFXIVClientStructs.dll` makes it explicit — the getter's IL is:

```
ldarg.0
ldfld    OutlineFlags        // 7B 65 77 00 04
ldc.i4.4                     // 1A   bit offset
ldc.i4.4                     // 1A   bit width
call     GetBitfieldValue
```

So the assignment set bits 4–7 and left bits 0–3 as it found them: zero. The
outline pass never picked the object up.

FFXIVClientStructs says so in its own summary on that property, which nobody
read before writing the line:

> Used to highlight potential targets and housing object outlines.
> To set the color it is recommended to use `GameObject.Highlight(...)`, as it
> makes sure that it also highlights a character's weapon(s), mount and
> ornament, if available.

`GameObject.Highlight(ObjectHighlightColor, bool includeMount)` is
`VirtualFunction` index 26 — the game's own routine. It sets whatever else the
outline pass requires and walks the attached objects.

## Cause — the discoverability half

The toggle was placed next to the code that consumed it rather than next to the
other toggles the user had just been taught to look for. A setting is findable
where its *neighbours* are, not where its *implementation* is.

## Fix

- `MobOutlines` now calls `native->Highlight(colour, includeMount: true)` for
  both applying and clearing, and re-asserts every tick rather than caching —
  the game drives the same field for its own target highlight, so a cached
  "already red" goes stale the moment the mob is targeted and untargeted.
- The toggle moved to **Settings → World Overlays**. The Enemy Vision panel
  keeps a one-line status instead of a second copy of the switch, so there is
  exactly one place to change it and the tool panel still says whether it is on
  and how many mobs are currently outlined.

## Lessons

**A property in FFXIVClientStructs is not necessarily a field.** Bitfields,
computed properties, and packed flags all present as `x->Foo = bar`. Before
writing through one, read its accessor — the `.xml` doc file shipped beside the
DLL, or the IL. Half a bitfield writes without complaint and does nothing.

**When ClientStructs names a preferred API in its own docs, use it.** The
`Highlight` recommendation was sitting in `FFXIVClientStructs.xml` in the
Dalamud dev folder the whole time. The direct write was a guess that compiled.

**A feature with no visible switch is a feature that does not exist.** Put a
setting where its neighbours live. When toggles get consolidated into Settings,
sweep for stragglers left behind in tool panels in the same change.

## How it was verified

A throwaway `net10.0-windows` console app referencing
`addon/Hooks/dev/FFXIVClientStructs.dll` reflected the enum, dumped
`DrawObject`'s field offsets, and printed the IL of `get_OutlineColor` /
`set_OutlineColor`. This is the same probe pattern used for the coffer SGB ids
and `BNpcBase.IsOmnidirectional` — see CLAUDE.md §3. Windows PowerShell 5.1
cannot load these assemblies; do not try.
