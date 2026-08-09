# Architecture

## Shape

```
Plugin (src/Plugin.cs)
├── Configuration          persisted to pluginConfigs/LimLoToolkit.json
├── ToolRegistry           owns every ITool, fans out the game-thread tick
│   ├── CharacterInfoTool
│   ├── EorzeaClockTool
│   └── AboutTool
└── WindowSystem
    ├── MainWindow         sidebar + content pane
    └── ConfigWindow       global options + per-tool on/off
```

## Threading contract

Two callbacks drive everything:

| Callback | Thread | What may happen there |
|----------|--------|----------------------|
| `Framework.Update` → `ToolRegistry.OnFrameworkUpdate` | Game thread | Read game memory, FFXIVClientStructs, Excel sheets. Write results into a snapshot field. |
| `UiBuilder.Draw` → `WindowSystem.Draw` → `ITool.Draw` | UI thread | ImGui calls only. Render the snapshot. |

A tool that reads game state from `Draw` is a bug waiting to happen. Follow
`CharacterInfoTool`: build a `Snapshot` struct on the framework tick, render it
in `Draw`.

## Failure isolation

Three nested guards, so one bad tool can never take the game down:

1. `Plugin.OnFrameworkUpdate` / `Plugin.DrawUi` wrap the whole pass in try/catch.
2. `ToolRegistry.OnFrameworkUpdate` wraps **each tool individually** — one
   throwing tool does not stop the others from ticking.
3. `MainWindow.Draw` wraps the selected tool's `Draw` and renders the exception
   message in the content pane instead of propagating it.

Every guard logs through `Plugin.Log` so failures are visible in `dalamud.log`
rather than silent.

## Adding a tool

See `CLAUDE.md` §3. Short version: implement `ITool`, add one `Register(...)`
line to the `ToolRegistry` constructor, never change an existing `Id`.

## Why no UI framework

This plugin ships to people who have no other plugins installed. Raw ImGui plus
the handful of helpers in `src/UiHelpers.cs` keeps the artifact to a single DLL
with zero runtime dependencies beyond Dalamud itself. See `CLAUDE.md` §2.
