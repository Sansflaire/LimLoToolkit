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

**Toolkit**

| Tool | What it does |
|------|--------------|
| Coffer Lines | Draws a line from you to each nearby treasure coffer in the Occult Crescent — brown for bronze, silver for silver. Each grade toggles separately. Optionally targets and opens a coffer once you walk up to it. Everything pauses in combat, and the tool does nothing outside South Horn and North Horn. |

| Enemy Vision | Draws each nearby enemy's detection area on the ground — a wedge in front of sight-based enemies, a full circle around sound-based ones — and turns it red when you are standing inside it. Occult Crescent only. |

> **Auto-open is off by default and is opt-in.** It targets and opens a coffer
> for you once *you* have walked within about 2 yalms of it — it never moves
> your character. Square Enix does not permit third-party automation, so enable
> it at your own risk.

> **Enemy Vision: the shape is exact, the size is a guess.** Whether an enemy
> detects in a cone or in all directions is read from the game's own per-enemy
> data. The *distance* is not published by the game anywhere, so it is a slider
> you tune by eye. See [docs/enemy-vision.md](docs/enemy-vision.md).

**Info**

| Tool | What it shows |
|------|---------------|
| Character | Live name, world, job, level, HP/MP, zone, position, target, combat and mount state. Includes a "copy position" button. |
| Eorzea Clock | Current Eorzea time and day, plus how long until the next weather window. |
| About | Version, commands, and reload instructions. |

More tools are on the way.

### Credit

The Coffer Lines tool reproduces a feature from
[BOCCHI](https://github.com/OhKannaDuh/BOCCHI) by OhKannaDuh. It is an
independent implementation — no BOCCHI code is used and BOCCHI is not required —
but the approach and the colour choices come from there. If you want the full
Occult Crescent automation suite, including Treasure Hunt route planning, go
install BOCCHI; it does far more than this one panel.

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
