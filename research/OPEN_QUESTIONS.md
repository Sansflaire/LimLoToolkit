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
| ~~5~~ | ~~Is a per-tool overlay drawn outside the main window worth supporting in `ITool`?~~ | **Answered 2026-08-09: yes.** Coffer Lines needs to draw with the window closed. `ITool.DrawOverlay` added as a defaulted no-op member; `ToolRegistry.DrawOverlay` fans it out from `Plugin.DrawUi`, outside the `WindowSystem`. |
| 6 | Should Coffer Lines also offer "Lines to carrots" (Fortune Carrot `EventObj` BaseId `2010139`), as BOCCHI does in the same section? | It is the third checkbox in BOCCHI's radar group and a small addition. Left out because it was not in the requested screenshot. |
| 7 | Should off-screen coffers get an edge marker instead of no line at all? | Our `WorldToScreen` renderer skips a segment whose endpoint leaves the viewport, where BOCCHI's Pictomancy clips to the screen edge. See `docs/occult-crescent.md`. |
| 8 | Do the Occult Crescent facts survive the next patch? | Territory IDs and coffer SGB ids are patch-sensitive. Re-run the checks in `docs/occult-crescent.md` after a major patch. |
