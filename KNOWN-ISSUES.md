# KNOWN-ISSUES.md — Active, Unresolved

Present tense. Things that are broken or annoying right now, with workarounds
where they exist. This file is an **index** — details live in
`Issues/<ID>-<slug>.md`.

When an issue is resolved and understood, move it to `BROKEN.md` with its lesson.

## Active

| ID | Summary | Workaround | Details |
|----|---------|-----------|---------|
| _(none yet)_ | | | |
| Minimap Radar | Dots still rotate slightly around their true positions, after the centre fix in 0.22.1.0. Centre is provably stable and applied rotation is 0 on a north-locked map, so the cause is elsewhere — prime suspect is `Atk2DNaviMap.X`/`Y`, the map's own marker-space offset, which the game may place its markers against instead of assuming the player is exactly centred. Leads in [Issues/012](Issues/012-minimap-centre-from-a-rotating-node.md) §Still open |
