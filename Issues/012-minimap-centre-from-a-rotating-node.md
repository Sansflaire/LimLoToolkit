# 012 — Minimap dots offset from the player, and orbiting as he turned

**Status:** Largely fixed in 0.22.1.0. A small residual rotation remains — see
"Still open" below.
**Found:** 2026-08-11 — "minimap radar is very inaccurate, as I turn my camera
the minimap dots shift around", then "it's like the enemy dots are based off of
a location for the player that is offset from my actual location".

## Cause

The minimap centre was taken from `Atk2DNaviMap.PlayerPin`:

```csharp
var node = &pin->AtkResNode;
centre   = (node->ScreenX + node->Width  * 0.5f * uiScale,
            node->ScreenY + node->Height * 0.5f * uiScale);
```

**The player pin is the player ARROW, and it rotates with the character.**
`AtkResNode.ScreenX/ScreenY` is where the node's local `(0,0)` lands *after* its
transform, so for a rotating node the axis-aligned half-size term does not find
its middle — it finds a point that swings in a circle as the node turns.

The whole dot frame therefore orbited the true centre, radius ~23px, once per
character rotation. That is both reported symptoms at once: an offset that also
moved as he turned.

Verified from live memory rather than inferred:

| | value |
|---|---|
| pin `ScreenX/Y` | (2484.06, 93.62) |
| pin `Rotation` | −4.9218 rad |
| pin `OriginX/Y` | (16, 16) |
| addon `Scale` | 0.8 |

Undoing the rotation properly —

```
centre = screen + scale * R(rotation) . origin
```

— gives **(2474.20, 108.80)**, which is `MapBase`'s centre to three decimal
places. The old formula gave (2496.86, 106.42).

## Fix

Take the centre, the radius and the scale from **`AddonNaviMap.MapBase`**, whose
rotation is 0 and whose origin is its own centre (88, 88 of a 176×176 node).
Nothing about it moves when the character turns.

Because it is a node's *post-transform* screen position, HUD layout position,
HUD scale and addon scale are all already applied — moving the minimap in the
HUD editor needs no configuration, which was a separate thing the user asked
about.

Scale became `MarkerPositionScaling * (nodeWidth / Atk2DNaviMap.Width) *
addonScale` = `0.5 * (176/88) * 0.8` = **0.8 px per yalm**, a ~78 yalm visible
radius.

## Two wrong turns before this, both from guessing

1. **`Camera.DirH`.** The rotation was taken from the camera. The minimap
   follows the CHARACTER. Confirmed live: player at −135°, `PlayerPinRotation`
   45° — exactly `facing + 180`, the same angle as `facing − pi`.
2. **Blaming `NorthLockedUp`.** From four screenshots I concluded the field was
   lying, and shipped a manual "my minimap turns with my character" setting to
   route around it. Live memory says the field reads **true** and is correct.
   The dots moving as he turned was the pin-derived centre orbiting all along —
   one fault wearing two symptoms — and I had invented a second, imaginary fault
   to explain the second symptom. The setting was removed the same day.

## Lessons

**`AtkResNode.ScreenX/ScreenY` is the transformed position of local (0,0), not
an axis-aligned top-left.** For any node that rotates, `+ Width/2` is wrong.
Either undo the rotation about `OriginX/OriginY`, or — much better — take your
reference from a node that does not rotate.

**Prefer an unrotating sibling to clever maths.** `MapBase` was sitting there the
whole time with rotation 0 and its origin at its centre.

**Two symptoms are usually one bug.** I treated "offset from my position" and
"shifts as I turn" as separate faults and started inventing causes for the
second. An orbit is exactly what one wrong centre on a rotating node produces.

**Stop guessing after the first miss — go and read the memory.** Three rounds
went by on inference from screenshots. One session with
`GET /debug/structread` on the running client produced the exact numbers and the
fault fell out in minutes. See CLAUDE.md §3 for the technique.

## Still open

Dots "still rotate around their position slightly". The centre is now provably
stable (`MapBase` rotation is 0), and with a north-locked map the applied
rotation is 0, so neither can be producing it. Leads for next session, in order:

1. **`Atk2DNaviMap.X` / `Y`** (read live as −857.47, −694.94) look like the map's
   current offset in marker space. The game may place its own markers relative
   to *that* rather than assuming the player sits exactly at the node centre. If
   it lags or interpolates, our dots drift against the terrain while the game's
   own markers do not.
2. **`MarkerRadiusScale`** (1.0) and the `Width`→node-width ratio (2.0) are the
   two least-confirmed terms in the scale. A scale error would look like dots
   sitting at slightly wrong distances, not rotating, but it is worth pinning.
3. Correlate against a game marker that is genuinely on-map. The first attempt
   failed because every marker sampled was edge-clamped — non-zero node
   rotation and a screen delta unrelated to its map delta. Scan the full
   101-entry `_naviMapMarkers` array for one with `Rotation == 0` and a small
   map delta first.
