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
north-up, so with `NorthLockedUp` the offset is simply `(dx, dz) * scale` — no
negation, no swap.

With the map free to turn, it turns so the camera's direction points up. Writing
the camera yaw as ψ and using the plugin's established facing convention
(`facing = (sin r, cos r)`, from `EnemyVisionTool.FacingVector`), we need the
rotation φ with

```
R(φ) · (sin ψ, cos ψ) = (0, -1)      // camera direction maps to screen "up"
⇒ sin(ψ - φ) = 0 and cos(ψ - φ) = -1
⇒ φ = ψ - π
```

Check: ψ = 0 is the camera looking toward +Z. Something at +dz should appear
*above* the player. `R(-π)·(0, dz) = (0, -dz)` — up. ✓

ψ comes from `CameraManager.Instance()->GetActiveCamera()->DirH` (`+0x140`).

## The one unverified step

**Whether `Camera.DirH` is the direction the camera looks toward or the
direction from the target to the camera** cannot be settled from the struct
definitions — the two differ by π, and its zero point and sign are likewise
unconfirmed. Everything else above is certain.

So the tool does not hide the guess:

- **Rotation offset** (−180°…180°) and **Mirror left/right** in the Orientation
  panel correct it in one move.
- While that panel is open the overlay draws the frame it is using: a crosshair
  on the computed centre, a ring on the computed edge, and a red tick where it
  believes north is. The crosshair should sit on the player arrow and the tick
  should agree with the minimap's compass.
- The panel lists the live values read — scaling, north-lock state, cone
  rotation, `DirH`, and the rotation actually applied.

**Test north-lock first.** With north locked there is no rotation term at all,
so it isolates the scale and the mirror. If it is right with north locked and
wrong when unlocked, the fault is in the `DirH` convention and the rotation
offset is the knob — try 180 first.

Once confirmed in game, bake the correct value into the defaults in
`Configuration` and reduce this section to a note.

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
