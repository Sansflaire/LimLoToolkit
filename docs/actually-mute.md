# Actually Mute — verified reference

Everything the Actually Mute tool depends on, and how each fact was
established. Re-verify after a major patch: SE can renumber Addon rows.

## The problem

Muting a character in-game does not remove their messages. The client keeps
printing the line — sender name, world, channel colour and all — and replaces
only the body text with a placeholder:

```
Velocious Sanooc  Diabolos: (This message is from a muted character.)
```

The game's own help text says as much (`Addon` row 14839: "Messages from a muted
character will not be displayed."), but what it does is substitute, not delete.
The tool deletes the line.

## The placeholder string

| Sheet | Row | Text |
|-------|-----|------|
| `Addon` | **14832** | `(This message is from a muted character.)` |

**Verified** on 2026-08-14 by reading the `Addon` and `LogMessage` sheets
straight out of sqpack with Lumina, with no game running, and scanning every row
of both for `mut`. Row 14832 is the only row in either sheet whose text is
exactly this placeholder. Neighbouring rows confirm it is the mute feature's own
block of strings rather than a coincidence:

| Row | Text |
|-----|------|
| 14832 | `(This message is from a muted character.)` |
| 14833 | `Register to Mute List` |
| 14834 | `Remove from Mute List` |
| 14835 | `… will be registered to your mute list. Messages from this character will no longer be displayed. …` |
| 14836 | `Remove  from your mute list?` |
| 14837 | `Mute List` |
| 14839 | `Messages from a muted character will not be displayed.` |

The tool reads row 14832 from the game at load rather than hard-coding the
English sentence, so it matches on a French, German or Japanese client too. The
English string is kept only as a fallback for the case where the sheet read
fails, and the panel says plainly which of the two is in use.

## Where the substitution happens, and why the hook can see it

This is the load-bearing question: Dalamud's `IChatGui.ChatMessage` hook sits on
the message-print path, so it only works if the client has *already* swapped the
text by the time the line is printed. If the swap happened later — at render
time in the chat log addon — the hook would see the real message and matching on
the placeholder would silently never fire.

**Verified** on 2026-08-14 against the game's own on-disk chat log
(`Documents/My Games/FINAL FANTASY XIV - A Realm Reborn/FFXIV_CHR<id>/log/*.log`),
which is written from the stored log entries. Those files contain the
placeholder, not the original message:

```
… ^C Momo Zzzzz ^B'^G … ^C Zalera ^B^S^BM-l^C ^_ (This message is from a muted character.) …
                                                 ^ 0x1F field separator
```

Two things follow from that byte layout, and both matter:

1. **The substitution is upstream of message storage.** The stored entry already
   holds the placeholder, so the swap happened at or before `PrintMessage` —
   which is where Dalamud's hook is. The hook sees the placeholder.
2. **The sender is a separate field.** `Momo Zzzzz` and the world `Zalera` sit
   in the sender payload, before the `0x1F` separator; the message body after it
   is *exactly* the placeholder with nothing appended or prepended. So an exact
   match on the body is the correct test.

`IHandleableChatMessage.PreventOriginal()` marks the message handled and stops
the game processing it further, so a suppressed line never reaches the chat log
and is never written to the log file on disk.

## Matching rule

Exact match on the trimmed body, plus an ends-with case as a belt-and-braces
fallback for any channel that bakes the sender into the message text. Nothing
else legitimately ends with that sentence, and if a player types it by hand then
removing it is still the requested behaviour.

Deliberately **not** matched:

- `LogMessage` 9719 `One or more muted characters have joined your party.` — a
  system notice about your own mute list, not a muted character speaking.
- `LogMessage` 9743 `Unable to send /tell. This character is muted.` — feedback
  on your own action.

Neither is noise from a muted character, so neither is removed.

## Cost

The handler runs on the game thread for every chat line, including combat log
spam. Both enabled checks are dictionary/bool reads and come before any string
work; the string comparison only runs on lines that survive them.
