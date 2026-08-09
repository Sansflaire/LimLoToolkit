# Issues/

One file per issue: `Issues/<ID>-<short-slug>.md`. `../BROKEN.md` and
`../KNOWN-ISSUES.md` are indexes that link here.

Resolved issues with no further diagnostic value move to `archive/`.

**Do not read this folder at session start.** Open a file only when the task at
hand touches a similar problem.

## Format

```markdown
# <ID>: <Title>
**Status:** ✅ FIXED | ⚠️ ACTIVE | 🔄 INVESTIGATING
**Date:** YYYY-MM-DD
**Keywords:** comma, separated, terms

## Symptom
What was observed.

## Root Cause
What actually caused it.

## Attempts
| Date | Attempt | Result |
|------|---------|--------|

## Resolution / Lesson
What fixed it and what to never do again.
```

## When to create one

The bug (a) took more than one attempt to resolve, (b) could plausibly recur,
or (c) produced a non-obvious lesson. Trivial one-shot fixes get an inline note
in the index instead.
