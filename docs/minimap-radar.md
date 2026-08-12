# Minimap Radar — REMOVED, kept for its findings

**The Minimap Radar tool was removed in 0.23.0.0** at the user's request, after
a day of it never quite sitting still. This document survives it because the
transform below was verified against live memory and would otherwise have to be
worked out from scratch by whoever tries this next.

**If you are about to add minimap dots again, read this whole file first, and
read [`../Issues/012-minimap-centre-from-a-rotating-node.md`](../Issues/012-minimap-centre-from-a-rotating-node.md).**

## Why NOT to use AgentMap

The game has a real API: `AgentMap.AddMiniMapMarker(Vector3 position, uint icon,
int scale)`, with `_miniMapMarkers` as a `FixedSizeArray100` and
`ResetMiniMapMarkers()` to clear. **Do not use it.**

An early Mob Viewer added markers through `AgentMap` and they came out as large
red flags covering the map; the feature was pulled at the user's request. Beyond
the look:

- **The icon is a game icon.** Size and appearance come from the icon sheet. A
  small neutral dot is not on the menu.
- **It is shared state.** `ResetMiniMapMarkers()` clears the game's own markers
  too, so cleaning up means rebuilding quest and aetheryte markers with
  `CreateMiniMapMarkers`. Getting that wrong breaks the player's minimap.
- **It survives us.** A crash or reload mid-frame leaves markers behind with
  nothing left to remove them.

Drawing over the addon has none of those problems.

## The verified transform

```
handle = GameGui.GetAddonByName("_NaviMap", 1)   // Dalamud AtkUnitBasePtr
addon  = (AddonNaviMap*)(nint)handle
map    = addon->NaviMap                          // Atk2DNaviMap, @0x238
```

| Field | Offset | What it is |
|-------|--------|-----------|
| `PlayerPin` | `+0x08` | `AtkComponentNode*` — the player ARROW. **Rotates. Not a usable anchor.** |
| `MarkerRadiusScale` | `+0x28` | Scale used for marker radius rings |
| `MarkerPositionScaling` | `+0x2C` | yalms → marker coordinate space |
| `PlayerPinRotation` | `+0x30` | Character facing in minimap space, **in degrees** |
| `PlayerConeRotation` | `+0x34` | The view cone's rotation, degrees |
| `X` / `Y` | `+0x20` / `+0x24` | Map offset in marker space (live: −857.47, −694.94) |
| `Width` / `Height` | `+0x40` / `+0x42` | Map span in marker units (live: 88) |
| `NorthLockedUp` | `+0x134C` | Map is pinned north-up. **Correct — verified true on a map that did not turn** |

Live values, read 2026-08-11 from a running client via the brain's
`/debug/structread` (see CLAUDE.md §3):

| | |
|---|---|
| `_NaviMap` addon | `0x1AEB74960D0`, position (2387, 24), size 218×218, scale **0.8** |
| `MapBase` / `Mask` node | ScreenX/Y (2403.80, 38.40), size **176×176**, rotation **0**, origin (88, 88) |
| `PlayerPin` node | ScreenX/Y (2484.06, 93.62), size 32×32, rotation **−4.9218 rad**, origin (16, 16) |
| `MarkerPositionScaling` | 0.5 |
| `Atk2DNaviMap.Width` | 88 |

**Centre — use `MapBase`, never `PlayerPin`.**

```
centre = MapBase.ScreenX + MapBase.Width  / 2 * addonScale
         MapBase.ScreenY + MapBase.Height / 2 * addonScale
       = (2474.20, 108.80)
```

`AtkResNode.ScreenX/ScreenY` is where the node's local (0,0) lands **after** its
transform. `MapBase` has rotation 0, so the axis-aligned half-size term is valid
for it. The player pin does not: it rotates with the character, and the same
formula on it gives (2496.86, 106.42) — 23px off, and *orbiting* as the
character turns. Undoing the rotation properly,

```
centre = screen + scale * R(rotation) . origin
```

reproduces (2474.20, 108.80) from the pin exactly, which is how the fault was
proved. Anchoring to the unrotating node is simpler and needs no maths.

Because this is a node's post-transform screen position, **HUD layout position,
HUD scale and addon scale are all already applied** — moving the minimap in the
HUD editor needs no configuration at all.

**Scale.**

```
scale = MarkerPositionScaling * (MapBase.Width / Atk2DNaviMap.Width) * addonScale
      = 0.5 * (176 / 88) * 0.8
      = 0.8 px per yalm      // ~78 yalm visible radius
```

The `Width`→node-width ratio of 2 is the one term never confirmed against a
second source. An attempt to measure it from the game's own `_naviMapMarkers`
failed because every marker sampled was edge-clamped — non-zero node rotation
and a screen delta unrelated to its map delta. **Scan the full 101-entry array
for one with `Rotation == 0` and a small map delta first.**

**Rotation.** Screen Y grows downward and so does world +Z on a north-up map, so
with `NorthLockedUp` the offset is simply `(dx, dz) * scale` — no negation, no
swap.

When the map does turn, it turns so the direction the **character** faces points
up — not the camera. Writing the facing as r, with this plugin's convention
`facing = (sin r, cos r)`:

```
R(phi) . (sin r, cos r) = (sin(r - phi), cos(r - phi)) = (0, -1)
=> phi = r - pi
```

Confirmed live: player at −135°, `PlayerPinRotation` 45° — exactly `r + 180`,
the same angle as `r − pi`.

## What was never solved

With the centre fixed, the dots still rotated slightly around their true
positions. The centre is provably stable (`MapBase` rotation 0) and applied
rotation is 0 on a north-locked map, so it is neither of those. The untested
lead: **`Atk2DNaviMap.X`/`Y`** is the map's own offset in marker space, and the
game may place its markers relative to that rather than assuming the player sits
exactly at the node centre. If it lags or interpolates, dots anchored to the
true player position drift against the terrain.

That is where to start.
