# Occult Crescent — verified reference

Everything the Coffer Lines tool depends on, with how each fact was established.
Re-verify after a major patch; SE can add territories or new coffer models.

Source of the original approach: [OhKannaDuh/BOCCHI](https://github.com/OhKannaDuh/BOCCHI)
(`BOCCHI.Treasure/Services/TreasureCoffer.cs`, `TreasureRadarDrawer.cs`,
`BOCCHI.Common/Data/Zones/ZoneId.cs`). Our implementation is independent — see
"Differences from BOCCHI" below — but the game-data facts came from reading it,
and each was then checked against the game itself.

## Territories

| Territory ID | Internal name | Place name |
|--------------|---------------|-----------|
| 1252 | `o6b1` | South Horn |
| 1346 | `o6b2` | North Horn |

**Verified** against live game data via `GET /game/territory/{id}` on 2026-08-09.
These two IDs are the entire Occult Crescent. The tool is inert everywhere else.

## Coffer identification

Coffers are object-table entries with `ObjectKind.Treasure`. Grade comes from
the `Treasure` Excel sheet row addressed by the object's `BaseId`; that row's
`SGB` column is a `RowRef<ExportedSG>` whose row id identifies the model:

| `SGB` row id | Grade | `ExportedSG.SgbPath` | Treasure rows using it |
|--------------|-------|----------------------|------------------------|
| 1596 | Bronze | `bgcommon/world/tbx/shared/for_bg/sgbg_w_tbx_001_01a.sgb` | 655 |
| 1597 | Silver | `bgcommon/world/tbx/shared/for_bg/sgbg_w_tbx_002_01a.sgb` | 348 |
| 1598 | Gold   | `bgcommon/world/tbx/shared/for_bg/sgbg_w_tbx_003_01a.sgb` | 148 |

**Verified** by reading the `Treasure` and `ExportedSG` sheets straight out of
the game's sqpack with Lumina on 2026-08-09 (2076 `Treasure` rows, 52 distinct
non-zero SGB values). The bronze/silver/gold assignment rests on three
consistent signals: `tbx` is "treasure box"; the assets are numbered
`001`/`002`/`003`, i.e. ascending tier; and the row counts fall in exactly the
rarity order you would expect (655 > 348 > 148). BOCCHI ships the same 1596/1597
mapping in working code.

> **These SGB ids are game-wide, not Occult-Crescent-specific.** All 1003
> bronze+silver `Treasure` rows span the whole game — these are the generic
> chest models. That is precisely why the territory gate is load-bearing rather
> than a nicety: without it the tool would draw lines to ordinary treasure
> coffers everywhere in the game.

Gold coffers (1598) are deliberately not drawn — BOCCHI does not offer them
either. The Happy Bunny gold coffer from a Fortune Carrot is a different thing
again: an `EventObj` with BaseId `2012936`, not a `Treasure` object.

## Validity and gating

- A coffer counts as live while `IsValid() && !IsDead && IsTargetable`. An
  opened or despawning coffer stops being targetable, which is how it leaves
  the list. BOCCHI additionally reads the `Treasure` struct's `TreasureFlags`
  for an `Opened` edge, but only to decrement its own survey counter — the
  targetable check is what drives its radar.
- Lines are suppressed while `ConditionFlag.InCombat`, matching BOCCHI's
  documented "while out of combat" behaviour.

## Colours

Taken from BOCCHI's `TreasureColors` so the two plugins look the same:

| Grade | RGBA |
|-------|------|
| Bronze | `0.72, 0.45, 0.20, 1.0` |
| Silver | `0.82, 0.84, 0.88, 1.0` |

## Differences from BOCCHI

| | BOCCHI | LimLoToolkit |
|---|--------|--------------|
| Renderer | Pictomancy (true 3D, depth-aware, clips at the viewport edge) | Dalamud `WorldToScreen` + ImGui foreground draw list |
| Dependency cost | External overlay library | None — plugin stays a single DLL |
| Off-screen endpoint | Clipped to the screen edge | Segment skipped |
| Coffer collection | Stateful tracker keyed by `BaseId`, plus a `_WideText` parser for the Treasure Sight survey counts | Stateless per-frame scan of the object table |

The renderer swap is the reason for the standalone mandate in `CLAUDE.md` §2.
The one visible consequence: a coffer behind the camera draws no line at all,
where BOCCHI would draw one running to the screen edge.

## Not implemented

BOCCHI's config group is titled "Coffer lines on the map **and Treasure Hunt
routes**". Only the coffer lines are implemented here. The route half is a large
automation feature (`HuntRoutePlanner`, pathfinding, Treasure Sight casting,
auto-mount, Ninja Hide) that depends on vnavmesh and would pull in exactly the
cross-plugin dependencies this plugin exists to avoid.

BOCCHI also offers "Lines to carrots" in the same section — Fortune Carrot
`EventObj` BaseId `2010139`. Not implemented; it would be a small addition to
`OccultCofferLinesTool` if wanted.
