# Enemy vision — what is real and what is guessed

Short answer: **the shape is real game data, the size is not.** That split is
the whole story, and the tool's UI says so out loud so the drawing never looks
more authoritative than it is.

## The shape IS in the game's data

`BNpcBase.IsOmnidirectional` is a boolean column on the `BNpcBase` sheet, one
row per enemy type.

| Value | Meaning | Rows |
|-------|---------|------|
| `true` | Detects in every direction — "sound" aggro | 4,216 |
| `false` | Only detects in front of itself — "sight" aggro | 16,186 |

**Verified 2026-08-09** by reading `BNpcBase` out of sqpack with Lumina:
20,402 rows total. Reached at runtime from a live enemy via its `BaseId`, which
for a BattleNpc is the `BNpcBase` row id.

So cone-versus-circle is a fact, per enemy, straight from the game. That is
better than the hardcoded per-zone tables most plugins use for this part.

## The radius is NOT in the game's data

There is no aggro range anywhere in the sheets. Checked and confirmed absent:

- **No aggro/sight/detection/vision sheet exists.** Every sheet name containing
  `Aggro`, `Sight`, `Detect`, `Vision`, `Enemy`, or `BNpc` was enumerated; the
  matches are all unrelated (`BNpcName`, `BNpcResist`, `BNpcCustomize`,
  `BNpcParts`, `DynamicEventEnemyType`, …).
- **`BNpcBase` carries no distance column.** Its full schema is `Scale`,
  `ArrayEventHandler`, `Behavior`, `ModelChara`, `BNpcCustomize`, `NpcEquip`,
  `Special`, `Battalion`, `LinkRace`, `Rank`, `SEPack`, `BNpcParts`,
  `IsOmnidirectional`, `IsTargetLine`, `IsDisplayLevel`, and a handful of
  unknown bytes. Nothing range-shaped.
- `Rank` was checked as a possible aggro class and is not one — it does not
  correlate with `IsOmnidirectional` in any usable way (Rank 0 alone holds
  16,628 rows, 3,364 of them omnidirectional).

This matches how every other plugin handles it. From the community tooling:

- **RadarPlugin** ships an aggro-radius viewer for Deep Dungeons with
  *preconfigured* aggro types — hardcoded, not read from game files.
- **Distance** asks contributors to submit boss aggro ranges by hand, noting
  "instance/zone name, boss name, BNpc ID, TerritoryType, and distance" — i.e.
  players measure it and the numbers get baked into the plugin.
- **NecroLens** does the same for Deep Dungeon with its own dataset.

Nobody reads aggro range from the client because it is not there. It is
server-side behaviour.

## What this tool does instead

One tunable distance, applied to every enemy, drawn in the correct shape:

| Setting | Default | Basis |
|---------|---------|-------|
| Detection range | 12y | A starting guess. **Tune it by eye.** |
| Sight cone | 90° | The figure the community uses for Deep Dungeon sight mobs |

Two details that make it more accurate than a naive circle:

- **Measured from the hitbox edge.** The drawn radius is the configured
  distance plus the enemy's `HitboxRadius`, because FFXIV measures range
  hitring-to-hitring, not centre-to-centre. A big enemy therefore gets a
  visibly bigger shape from the same setting.
- **The "inside" test respects the cone.** Standing behind a sight enemy does
  not trigger the red highlight, because the check is a dot product against the
  enemy's facing, not a plain distance test.

Facing is `(sin r, 0, cos r)`, the inverse of the `Atan2(dx, dz)` used by this
repo's own working walk-to routine in `EasterEvent/src/Walker.cs`.

Enemies are filtered to `BattleNpcSubKind.Combatant` — the enum has no `Enemy`
member, and `Combatant` (5) is what ordinary field mobs use. This excludes
pets, buddies, race chocobos, minions, party members, and BNpc body parts,
which all share `ObjectKind.BattleNpc`.

## Training mode — measuring it for real

Opt-in, off by default (it keeps per-enemy history buffers while running). When
on, every pull onto the player is recorded and the drawn shapes switch from the
slider to measured numbers per mob.

**The signal.** An enemy's `TargetObjectId` flipping to the player. Precise, no
hooks needed.

### Two timing problems, and what is done about them

**1. Position lag.** By the time the pull reaches the client, you have walked
closer than you were when you actually crossed the line. The player position
used is therefore the one from **150 ms ago**, not the current one. Residual
error still biases every sample *low* — which is why the estimator tracks the
**upper end** of the distribution rather than the mean. Nothing biases a sample
high except a misattributed pull, and those are filtered out (below).

**2. The mob turns to face you.** This one silently ruins the cone if you miss
it. A mob rotates toward its new target immediately, so its facing at detection
time points almost straight at you — measuring the angle then would report ~0°
for every mob and collapse every cone to nothing. The facing used is therefore
from **400 ms ago**, before it turned. If the mob rotated more than **30°**
across that window it was already turning, so the angle is discarded as
untrustworthy while the distance is still kept.

### Rejecting pulls that were not proximity detection

A mob can target you because you hit it, because it linked off a neighbour, or
because it was already fighting. All three would poison the data:

- the player must have been **out of combat on the previous frame** (the pull
  itself flips the player into combat on the same frame, so the previous frame's
  value is the one that matters)
- the enemy must **not already have been in combat**
- only the **first pull in a 1000 ms window** is recorded, so a chain of linked
  adds contributes one clean sample instead of five bad ones
- the gap must be within 0–50 y, or it is discarded as nonsense

### Turning samples into numbers

| Quantity | Rule |
|----------|------|
| Range | 90th percentile once there are ≥10 samples, plain maximum before that |
| Cone | (widest observed angle + 10° margin) × 2, since the cone is symmetric |
| Confidence | **Green/solved** at ≥8 samples *and* ≥4 consecutive samples that did not grow the maximum. **Amber** with any samples. **Red** with none. |

The "stopped growing" half of the green condition is the important one: a plain
sample count would call a mob solved while its measured range was still
climbing.

All measurements are hitring-to-hitring gaps, matching what the slider means.

### It also verifies the sight/sound flag

If a mob the sheet calls forward-only ever pulls from more than **100°** off its
facing, that is recorded as a rear pull. Any rear pull flips the mob to
omnidirectional for drawing and raises a contradiction warning in the Mob
Viewer. So the `IsOmnidirectional` reading is not merely trusted — it gets
checked against real behaviour for every mob you meet, which is the empirical
answer to "how accurate is sight vs sound".

## Ignoring irrelevant mobs

Mobs can be marked irrelevant by `BNpcBase` id, from the Enemy Vision table or
the Mob Viewer. Ignored mobs get no shape drawn and contribute no samples.
Anything already measured is kept, so un-ignoring restores it.

## Re-verify after a patch

`IsOmnidirectional` is a reverse-engineered column name. It has been stable,
but if the shapes ever look systematically wrong, re-check the schema before
assuming the drawing code broke.

Sources for the community-practice claims:
[RadarPlugin](https://github.com/KangasZ/RadarPlugin),
[Distance](https://github.com/PunishedPineapple/Distance),
[NecroLens](https://github.com/Jukkales/NecroLens).
