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

| Enemy Vision | Draws each nearby enemy's detection area on the ground — a wedge in front of enemies that only see forwards, a full circle around ones that detect in all directions — and turns it red when you are standing inside it. Includes a training mode that measures each mob's real range by watching pulls. Occult Crescent only. |
| Mob Viewer | Browse every mob you have met: how it detects you, its measured range and cone, level, HP, hitbox, and the underlying game data. Colour-coded green / amber / red by how solid the measurements are, and any mob can be marked irrelevant to hide it. |

**World overlays** (Settings → World Overlays)

| Overlay | What it does |
|---------|--------------|
| Mob silhouettes | Outlines nearby mobs using the game's own targeting silhouette, so it traces the real model rather than a box — red for a mob that can aggro you, black for one you outlevel. Off by default; it writes to the game's render state and clears itself when switched off or unloaded. |
| Distance above your head | Floats the live distance to your current target over your character, refreshed every frame. The big number is the hitbox-to-hitbox gap, which is what every range in this plugin means; the raw centre-to-centre distance sits underneath. The name turns blue when the target is the mob selected in Mob Viewer, and it can be narrowed to that mob only. |

> **Auto-open is off by default and is opt-in.** It targets and opens a coffer
> for you once *you* have walked within about 2 yalms of it — it never moves
> your character. Square Enix does not permit third-party automation, so enable
> it at your own risk.

> **Enemy Vision: the shape is exact, the size starts as a guess.** Whether an
> enemy detects in a cone or in all directions is read from the game's own
> per-enemy data. The *distance* is not published by the game anywhere — so it
> begins as a slider and becomes a real measurement once training mode has
> watched enough pulls. See [docs/enemy-vision.md](docs/enemy-vision.md).

### Mob knowledge ships with the plugin

You do not start from nothing. Detection data and sighting locations for the
Occult Crescent are **built into the plugin**, so mobs are already mapped on
first launch.

Anything you measure yourself always wins — the shipped data only fills gaps.
A mob you have never met is taken from it wholesale; a mob you have partly
measured keeps everything you recorded and takes shipped data only for the
angles you have no evidence in. Your own observations are of the mobs in front
of you right now, so they outrank anything baked in at build time.

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
