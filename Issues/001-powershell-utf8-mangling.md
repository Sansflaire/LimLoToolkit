# 001: PowerShell 5.1 round-trip mangles non-ASCII in manifests
**Status:** ✅ FIXED
**Date:** 2026-08-09
**Keywords:** powershell, encoding, utf-8, mojibake, em dash, manifest, version bump, Set-Content, Get-Content

## Symptom

After bumping the version with a PowerShell one-liner, the em-dash in the
plugin `Description` became `â€”` in both `LimLoToolkit.json` and
`pluginmaster.json`, and in a `.csproj` comment:

> The plugin is fully standalone â€” it needs no other plugins installed

Caught before commit. Had it shipped, every user browsing `/xlplugins` would
have seen the mojibake in the plugin's description, and it would have been
baked into the published `pluginmaster.json` that friends' clients read.

## Root Cause

The bump was done as:

```powershell
(Get-Content $f -Raw) -replace '0\.2\.0\.0','0.3.0.0' | Set-Content $f -Encoding utf8
```

Windows PowerShell 5.1 (`powershell.exe`, not `pwsh`) defaults `Get-Content`
to the **system ANSI codepage** when a file has no BOM. Our manifests are
BOM-less UTF-8. So the em-dash `—` (UTF-8 `E2 80 94`) was read as three
separate CP-1252 characters `â€”`, and `Set-Content -Encoding utf8` then
faithfully re-encoded those three characters as UTF-8. The `-Encoding utf8` on
the write side looks like it makes the operation safe; it does not, because the
corruption already happened on the **read**.

Silent: no error, no warning, and the file still parses as valid JSON.

## Attempts

| Date | Attempt | Result |
|------|---------|--------|
| 2026-08-09 | `Get-Content -Raw` + `-replace` + `Set-Content -Encoding utf8` | Mangled every non-ASCII character |
| 2026-08-09 | Rewrote the affected files with the Write tool | Fixed; verified valid UTF-8 JSON, no BOM |

## Resolution / Lesson

**Do not edit repo files with `Get-Content`/`Set-Content` round-trips.** Use the
Edit or Write tools, which are UTF-8 correct.

If a shell edit is genuinely unavoidable, force the read encoding too
(`Get-Content -Encoding utf8`), and note that `Set-Content -Encoding utf8` in
5.1 may add a BOM — which Dalamud's manifest parsing does not want.

**Verification that catches it**, run before any commit touching a manifest:

```bash
grep -rn "â€\|Â\|ï»¿" --include=*.json --include=*.csproj --include=*.cs --include=*.md .
```

Broader lesson: prefer plain ASCII (`-`) over typographic characters (`—`) in
manifest text that gets machine-processed. The visual gain is not worth a class
of silent corruption in strings that end up in front of users.
