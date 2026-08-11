# Minimap Radar — how a world position becomes a minimap dot

Every number below was read out of `FFXIVClientStructs.dll` in the Dalamud dev
folder with the probe pattern from CLAUDE.md §3. None of it is measured off a
screenshot or copied from another plugin.

## Why we draw over the minimap instead of adding markers

The game has a real API for this: `AgentMap.AddMiniMapMarker(Vector3 position,
uint icon, int scale)`, with `_miniMapMarkers` as a `FixedSizeArray100` and
`ResetMiniMapMarkers()` to clear.

We do not use it.

An earlier version of the Mob Viewer added markers through `AgentMap` and they
came out as large red flags that covered the map; the feature was pulled at the
user's request. Beyond the look, the API has three properties we do not want:

- **The icon is a game icon.** Size and appearance come from the icon sheet, not
  from us. A small neutral dot is not on the menu.
- **It is shared state.** `ResetMiniMapMarkers()` clears the game's markers too,
  so cleaning up after ourselves means rebuilding quest and aetheryte markers
  with `CreateMiniMapMarkers`. Getting that wrong breaks the player's minimap.
- **It survives us.** A crash or a plugin reload mid-frame leaves markers on the
  map with nothing left to remove them.

Drawing our own dots on top has none of those problems: any size, any colour,
nothing written, nothing to clean up.

## The transform

```
handle = GameGui.GetAddonByName("_NaviMap", 1)   // Dalamud AtkUnitBasePtr
addon  = (AddonNaviMap*)(nint)handle
map    = addon->NaviMap                          // Atk2DNaviMap, @0x238
```

`Atk2DNaviMap` inherits `Atk2DMap`, and between them they carry everything:

| Field | Offset | What it is |
|-------|--------|-----------|
| `PlayerPin` | `+0x08` | `AtkComponentNode*` — the player arrow, i.e. the middle of the minimap |
| `MarkerPositionScaling` | `+0x2C` | **yalms → minimap pixels.** The game's own marker scale |
| `MarkerRadiusScale` | `+0x28` | Scale used for marker radius rings |
| `PlayerPinRotation` | `+0x30` | |
| `PlayerConeRotation` | `+0x34` | The view cone's rotation |
| `Width` / `Height` | `+0x40` / `+0x42` | Minimap size in pixels |
| `NorthLockedUp` | `+0x134C` | Is the map pinned north-up |

**Centre.** `PlayerPin`'s `AtkResNode.ScreenX/ScreenY` is the node's top-left
after every UI transform, so the centre is that plus half the node's scaled
size. Taking it from the node rather than reconstructing the addon rectangle
means HUD scale, HUD layout position and window movement are all already
accounted for.

**Scale.** `MarkerPositionScaling * addon scale`. This is the same number the
game uses to place its own minimap markers, so distances match the map exactly
at every zoom level.

**Rotation.** Screen Y grows downward and so does world +Z when the map is
north-up, so with a north-up minimap the offset is simply `(dx, dz) * scale` —
no negation, no swap. This is the default and the case verified against a real
client.

With the map free to turn, it turns so the direction the **character** faces
points up. Writing the character's facing as r and using the plugin's
established convention (`facing = (sin r, cos r)`, from
`EnemyVisionTool.FacingVector`), we need the rotation phi with

```
R(phi) . (sin r, cos r) = (sin(r - phi), cos(r - phi)) = (0, -1)
=> r - phi = pi
=> phi = r - pi
```

Check: r = 0 is the character facing +Z. Something at +dz (in front) should
appear above the player. `R(-pi).(0, dz) = (0, -dz)` — up. ✓

### Two things this got wrong, and why

**It used the camera, not the character.** The first version took the angle from
`Camera.DirH`. Swinging the camera around a stationary character then rotated
every dot while the minimap itself stayed put — reported as "as I turn my
camera, the dots shift around". The minimap follows the character.

**`NorthLockedUp` is not trusted.** On 2026-08-11, four screenshots with the
character facing north, south, east and west showed a minimap whose terrain did
not move at all — plainly north-up — while `Atk2DNaviMap.NorthLockedUp`
(`+0x134C`) read false and a rotation was applied anyway. Either the field means
something other than its name, or it is not the whole story.

So the plugin **asks** instead: "My minimap turns with my character", off by
default. An auto-detection that cannot be validated is worse than a setting.
The field's value is still shown in the Orientation table, labelled
`(not trusted)`, so the discrepancy stays visible.

This is the same trap as the Glamour Dresser `GlamourDresserItemSetUnlockBits`
polarity noted in `devPlugins/CLAUDE.md`: a plausibly-named boolean whose
meaning must be verified empirically before anything is gated on it.

### Settling it properly

`Atk2DNaviMap.PlayerPinRotation` is the rotation the game applies to the player
arrow, which is the character's facing expressed in minimap space. That makes

```
map rotation = PlayerPinRotation - characterFacing
```

fall out for both kinds of minimap with no boolean involved at all: north-up
gives a constant, character-up gives -facing. The unknown is the constant offset
and sign between `AtkResNode.Rotation`'s convention and the world angle.

While the Orientation panel is open the tool logs, once a second at
**Information** (not Debug — Dalamud filters Debug out, see BROKEN 008):

```
[MinimapRadar] facing= pin= cone= dirH= northLocked= rotatesSetting= applied= scale= centre= r=
```

Two samples at known facings resolve the constant and the sign. Once confirmed,
the setting can go away and this can decide for itself.

## Colours

| Colour | Meaning |
|--------|---------|
| Red | Can detect you right now. Only claimed for mobs with a confirmed shape |
| Amber | Can aggro, range not settled |
| Grey | Never measured |
| Dim grey | Cannot aggro you at all (off by default) |

A dot is a *position*, so it is drawn for any tracked mob whether or not its
range is confirmed. Only the red state is a claim about detection, and that is
gated on confirmed data — which is why the radar behaves the same in the public
build as in the dev one, unlike the ground shapes.

Mobs beyond the minimap's edge are pinned to the rim as small hollow rings, so
"something is out that way" is still answerable without implying it is at the
edge.
