# docs/ — Index

Every document in this folder, with a one-line description. Add a row whenever
a doc is created.

| Document | Covers |
|----------|--------|
| [architecture.md](architecture.md) | How the toolkit shell, tool registry, and windows fit together |
| [occult-crescent.md](occult-crescent.md) | Verified game data behind the Coffer Lines tool: territory IDs, coffer SGB model ids, gating rules, and how each was checked |
| [enemy-vision.md](enemy-vision.md) | Why enemy detection *shape* is real game data but *range* is not, what every other plugin does about it, and how to make ours accurate. Also the mob-silhouette mechanism and why it has no thickness setting |
| [minimap-radar.md](minimap-radar.md) | **Removed feature, kept for its findings.** The verified `Atk2DNaviMap` transform, why `AgentMap` markers are banned, and the `AtkResNode.ScreenX` rotating-node trap |
| [actually-mute.md](actually-mute.md) | The muted-character placeholder: the `Addon` row it comes from, the on-disk chat-log evidence that the game substitutes it *before* the print hook, and what is deliberately not matched |
| [build-flavours.md](build-flavours.md) | The public/dev build split: what is stripped from the shipped DLL, how `#if PUBLIC_BUILD` and `BuildFlavor.IsLive` differ, Live Mode, and what "confirmed" means |

Project-level docs that live outside this folder:

| Document | Covers |
|----------|--------|
| `../README.md` | User-facing install and tool list |
| `../CLAUDE.md` | Session rules, standalone mandate, key symbols (gitignored) |
| `../BROKEN.md` | Post-mortems |
| `../KNOWN-ISSUES.md` | Active issues |
| `../research/OPEN_QUESTIONS.md` | Unresolved questions |
