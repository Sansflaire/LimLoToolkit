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

## If you want it accurate

The honest options, in increasing effort:

1. **Tune the one number** until the shape matches where you actually get
   pulled. Cheap, and good enough for "don't walk into that".
2. **Per-enemy overrides** — a small table keyed by `BNpcBase` id, filled in as
   you measure them. The panel already shows each enemy's `BNpcBase` id for
   exactly this reason.
3. **Learn it automatically** — watch for the moment an enemy switches to
   in-combat with you, record the distance at that instant, and build the table
   from real pulls. This is the only route to real numbers without hand
   measurement, and it is how a community dataset would get built.

## Re-verify after a patch

`IsOmnidirectional` is a reverse-engineered column name. It has been stable,
but if the shapes ever look systematically wrong, re-check the schema before
assuming the drawing code broke.

Sources for the community-practice claims:
[RadarPlugin](https://github.com/KangasZ/RadarPlugin),
[Distance](https://github.com/PunishedPineapple/Distance),
[NecroLens](https://github.com/Jukkales/NecroLens).
