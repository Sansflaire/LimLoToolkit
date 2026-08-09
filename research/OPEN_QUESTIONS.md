# Open Questions

Unresolved questions and decisions still to make. Pick from here when choosing
the next piece of work. Move an entry out (delete it, or fold the answer into
`CLAUDE.md` / `docs/`) as soon as it is answered.

## Product

| # | Question | Why it matters |
|---|----------|----------------|
| 1 | What does "LimLo" stand for, and should the display name or punchline reflect it? | Affects the user-facing manifest text that friends see in `/xlplugins`. Currently generic. |
| 2 | Which tools go in next? | The shell is built; the tool list is the whole product. |
| 3 | Should there be an icon? | `IconUrl` is currently omitted from both manifests because no `images/icon.png` exists yet. A broken icon URL looks worse than none. |

## Technical

| # | Question | Why it matters |
|---|----------|----------------|
| 4 | Do any planned tools need optional IPC (Penumbra, Glamourer, vnavmesh)? | Allowed only as a graceful-degradation extra — never a hard dependency. See `CLAUDE.md` §2. |
| 5 | Is a per-tool overlay window (drawn outside the main window) worth supporting in `ITool`? | Some tools want a HUD element rather than a panel. Would mean widening the interface. |
