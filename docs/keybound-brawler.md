# Keybound Brawler

The Gold Saucer's FFVII-collab typing minigame (live 2026-10-28). The tool reads the enemy word
plates and types them into the input bar. Everything below was mapped live during real matches on
launch day — FFXIVClientStructs has no definitions for any of these addons. **Re-verify after
every game patch**: node ids are the only thing the tool keys on.

Ported from Sansflaire's standalone `KeyboardBrawlerAutomation` dev plugin, which has the HTTP
probes and per-frame match recorder used to establish all of this. The tool refuses to type
while that plugin is loaded — two typers would fight over the bar.

Paths are NodeId chains: `7/16` means node 16 inside component node 7.

## The match instance

ContentFinderCondition **1095** "Keybound Brawler" (Gold Saucer) → TerritoryType **1359**.
Verified 2026-10-28 from the game's sheet and live: the client entered 1359 at both match starts
(16:34:26, 16:51:41) and returned to **1360** "Keybound Square" (the lobby) after "Duty Done".
The in-match control window shows for the whole time the player is in 1359, because the
minigame UI itself is hidden during the countdown and between stages.

## Addons

| Addon | What the tool reads |
|---|---|
| `KTGEWord` | Each plate is a top-level component node; the word is **Text node 20**. Primary plate = component **2**, extras = **20001+**. Hidden plates keep stale text — always check visibility. Plate child node **25** add-RGB is the tint (below). |
| `KTGInput` | Component **7** is the input; **Text node 16** is the typed text, hidden while empty (and it keeps stale text while hidden, so hidden = empty). AtkValues `[2]` = round open, `[4]` = pre-round countdown 6 → 0. |
| `KTGEnemyCastBar` | One component per cast, visible while casting; **Text node 4** = cast name. **Not slot-aligned with the plates** — a cast bar sits ~80–120 px below its plate, so it is linked by screen position. |
| `KTGHudGaugeHp` | AtkValues `[1]` / `[2]` = party HP / max (40,000). |
| `KTGHudScore` | AtkValues `[1]` = your score, a **UInt**. |
| `KTGPlayGuide` | `[Enter]: Submit Text  [Esc]: Delete Text/Quit` — authoritative. **Esc is never sent.** |

## The bar works like the chat box

Enter opens it, typing fills it, Enter submits and closes it. A key press that opens a closed bar
inserts its letter in lower case, so the echo check is case-sensitive and retypes it. After a wave
break or a stun the bar can keep its text but lose focus: Enter refocuses it, which is why one
missed submit is retried with Enter (never with `WM_CHAR '\r'`, which never submits) and why
ignored backspaces are flushed with Enter before they count as failures.

## Typed characters must be real key events

**Bare `WM_CHAR` typing gets the player expelled from the duty for "extended inactivity"** —
twice, ~2 minutes into rounds where every word was going in fine. The client input timers read 0
throughout, so the kick is not driven by them.

From `ffxiv_dx11.exe` (game 2026.10.16): all keyboard input arrives as window messages (no raw
input, no hooks, no `GetAsyncKeyState`). The window procedure's `WM_KEYDOWN` handler skips
auto-repeats, calls `ToAscii`, and appends to a 64-entry key-event ring; input-bar text arrives
separately as `WM_CHAR`. Measured in the chat box: bare `WM_CHAR` adds **1** ring entry per
character, a physical-style `WM_KEYDOWN` + `WM_CHAR` adds **2**.

`KeyInjector.SendTypedChar` does the second: on the game window's own thread it sets a temporary
keyboard state (so `ToAscii` sees Shift for capitals), sends `WM_KEYDOWN` + `WM_CHAR`, restores
the state, and releases the key on a later frame. With it, a full eight-minute match ran with no
kick. Only a character no key on the layout produces (e.g. `á`) is sent as text alone; any other
failure to send a key event stops the typer instead of degrading.

## Targeting

| Tint (node 25 add-RGB) | Meaning |
|---|---|
| 255,128,0 (orange) | Enemy casting — every orange transition across four matches coincided with a cast. Not every cast tints ("??? Needles" didn't), so a linked cast bar also counts. |
| 0,128,0 (green) | The **wall** prompt: big plate (scale 1.7) at y≈751. Appears as the boss starts Tankbuster (18,000 damage); typing it gives "Barrier reinforced!". |

Plate Y: regular enemies 470–650 (larger = closer), wall ~751, boss and mid-bosses 250–370.

- **Safety** — wall → casting non-boss → closest → boss. Re-chosen at every word start; a wall
  appearing mid-word abandons a word with more than 3 characters left.
- **Boss** — plate 2, then the closest.

## Don't-type casts

**Cursed Gaze** (minion) and **Dynamic Sensory Jammer** (boss): text in the bar or an open input
when the cast goes off disables inputs for ~7 s. Keys during the cast are fine. Both casts last
4.67 s; the stun has landed 1.0, 1.4 and 2.6 s after the cast bar disappeared.

The tool: keeps typing during the cast, finishes the word in progress, starts no word it can't
type and submit before the cast ends, submits whatever is in the bar 450 ms before it ends, then
presses nothing for 4 s after the bar vanishes. Confirmed over 2 gazes + 2 jammers with zero
stuns (10:33–10:40 match), and again 2 gazes with zero stuns at 16:34–16:43.

## Every player's score

The game only shows you your own score, but the last finished match's full results live in an
agent (index 401 on game 2026.10.16 — **located at runtime** by finding your own content ID in a
record, never hard-coded):

```
+0x34  int    player count (4)
+0x40  record[i], 0x40 bytes each
         +0x00 u64  content ID
         +0x30 u32  score
```

It is rewritten ~24 s after the match UI closes, so it is never live. The minigame UI also
disappears for 10+ s between stages, so "UI gone" does not mean "match over" — the results
table changing is the only reliable end-of-match signal. Names are matched from the object table
on `Character.ContentId` while the players are around and cached to
`keybound-player-names.json`. Read through `ReadProcessMemory`, so a wrong guess cannot crash.

The tool reads it only while the minigame is on screen and for two minutes after, and appends
each new result to `keybound-history.jsonl` (newest 50 kept).
