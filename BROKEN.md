# BROKEN.md — Post-Mortems

Resolved issues that shipped or nearly shipped, each with the lesson that
prevents a recurrence. Past tense. This file is an **index** — details live in
`Issues/<ID>-<slug>.md`.

**Read this before every commit, build, or push.** Also read the suite-wide
`devPlugins/BROKEN.md`, which catalogs failures that reached real users across
every plugin (missing `AssemblyVersion` in the bundled manifest, wrong
`DalamudApiLevel`, CI missing `contents: write`, stale dev DLLs, and more).

## Fixed

| ID | Summary | Lesson | Details |
|----|---------|--------|---------|
| 001 | A PowerShell version-bump one-liner turned the em-dash in the plugin `Description` into `â€”` in both manifests | Never round-trip repo files through `Get-Content`/`Set-Content` in PowerShell 5.1 — it reads BOM-less UTF-8 as ANSI, and `-Encoding utf8` on the write does not save you. Use Edit/Write. Grep for `â€` before committing a manifest | [001-powershell-utf8-mangling.md](Issues/001-powershell-utf8-mangling.md) |

## How to use this file

- An issue starts in `KNOWN-ISSUES.md`. Once resolved and understood, it moves
  here with its lesson.
- Add the entry **before** moving on to the next task, not later.
- This file grows. It does not shrink — a fix in CI today can regress tomorrow.
