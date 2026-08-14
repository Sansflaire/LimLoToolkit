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

**Every tool starts switched off.** A fresh install opens on an empty window —
go to Settings, turn on the tools you want, and they appear in the sidebar.
Nothing runs until you ask for it. Switching a tool off again removes it from
the sidebar and stops it completely.

If you already have the plugin, this changes nothing for you: whatever you had
on stays on.

## Tools

**Toolkit**

| Tool | What it does |
|------|--------------|
| Coffer Lines | Draws a line from you to each nearby treasure coffer in the Occult Crescent — brown for bronze, silver for silver. Each grade toggles separately. Optionally targets and opens a coffer once you walk up to it. Everything pauses in combat, and the tool does nothing outside South Horn and North Horn. |

| Enemy Vision | Draws each nearby enemy's detection area on the ground — a wedge in front of enemies that only see forwards, a full circle around ones that detect in all directions — and turns it red when you are standing inside it. Includes a training mode that measures each mob's real range by watching pulls. Occult Crescent only. |
| Mob Viewer | Browse every mob with confirmed detection values: how it detects you, its range and cone, level, HP, hitbox, where it lives, and the underlying game data. Any mob can be hidden. |
| Actually Mute | Muting someone in-game does not silence them — the game still prints their name and swaps the text for "(This message is from a muted character.)". This deletes that line completely, so a muted character leaves no trace in chat. Only lines whose entire text is that placeholder are removed; nothing else in chat is touched and your mute list is never modified. |

**World overlays** (Settings → World Overlays)

| Overlay | What it does |
|---------|--------------|
| Mob silhouettes | Outlines nearby mobs that **can aggro you** in red, using the game's own targeting silhouette, so it traces the real model rather than a box. Mobs you outlevel get no outline at all — an outline means danger. Off by default; it writes to the game's render state and clears itself when switched off or unloaded. |
| Overlay line thickness | Width of every line the plugin draws itself: detection cones and circles, coffer lines, missing-angle wedges. It does not change the silhouettes — the game's own outline pass has no width setting to change. |
| Distance above your head | Floats the live distance to your current target over your character, refreshed every frame. The big number is the hitbox-to-hitbox gap, which is what every range in this plugin means; the raw centre-to-centre distance sits underneath. The name turns blue when the target is the mob selected in Mob Viewer, and it can be narrowed to that mob only. |

> **Auto-open is off by default and is opt-in.** It targets and opens a coffer
> for you once *you* have walked within about 2 yalms of it — it never moves
> your character. Square Enix does not permit third-party automation, so enable
> it at your own risk.

> **Enemy Vision: the shape is exact, and so is every range you are shown.**
> Whether an enemy detects in a cone or in all directions is read from the
> game's own per-enemy data. The *distance* is not published by the game
> anywhere, so each one is measured by hand and confirmed before it ships. See
> [docs/enemy-vision.md](docs/enemy-vision.md).

### Mob knowledge ships with the plugin

You do not start from nothing. Confirmed detection values and sighting locations
for the Occult Crescent are **built into the plugin**, so mobs are already
mapped on first launch, and each update brings newly confirmed ones with it.

Nothing is guessed at you. A mob whose detection range has not been pinned down
does not appear, rather than appearing with a number that might be wrong — so
what the overlay draws is what somebody actually walked into and measured.

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
dotnet build src/LimLoToolkit.csproj -c Debug     # dev build
dotnet build src/LimLoToolkit.csproj -c Release   # public build
```

A Debug build copies `LimLoToolkit.dll` into
`%APPDATA%\XIVLauncher\devPlugins\LimLoToolkit\` so Dalamud picks it up.

The two configurations produce **different plugins**: Release is what CI ships,
and it has the measurement and data-collection code removed from the binary
entirely. See [docs/build-flavours.md](docs/build-flavours.md).

## License

See the repository for license details.
