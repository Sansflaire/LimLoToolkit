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
| _(none yet)_ | | | |

## How to use this file

- An issue starts in `KNOWN-ISSUES.md`. Once resolved and understood, it moves
  here with its lesson.
- Add the entry **before** moving on to the next task, not later.
- This file grows. It does not shrink — a fix in CI today can regress tomorrow.
