# LimLo Toolkit

A single window holding small quality-of-life tools for FFXIV.

**Standalone.** No other plugins required — install it and it works.

## Install

Add this repository URL in Dalamud (`/xlsettings` → Experimental → Custom Plugin Repositories):

```
https://raw.githubusercontent.com/Sansflaire/LimLoToolkit/main/pluginmaster.json
```

Then find **LimLo Toolkit** in `/xlplugins` and install it.

## Use

| Command | What it does |
|---------|--------------|
| `/limlo` | Toggle the toolkit window |
| `/limlocfg` | Open settings |

Pick a tool from the sidebar. Any tool you do not want can be switched off in
Settings — it then disappears from the sidebar and stops running.

## Tools

| Tool | What it shows |
|------|---------------|
| Character | Live name, world, job, level, HP/MP, zone, position, target, combat and mount state. Includes a "copy position" button. |
| Eorzea Clock | Current Eorzea time and day, plus how long until the next weather window. |
| About | Version, commands, and reload instructions. |

More tools are on the way.

## Building

Requires the .NET 10 SDK and a Dalamud install (the project references the
Dalamud DLLs from `%APPDATA%\XIVLauncher\addon\Hooks\dev\`).

```
dotnet build src/LimLoToolkit.csproj -c Debug
```

A Debug build copies `LimLoToolkit.dll` into
`%APPDATA%\XIVLauncher\devPlugins\LimLoToolkit\` so Dalamud picks it up.

## License

See the repository for license details.
