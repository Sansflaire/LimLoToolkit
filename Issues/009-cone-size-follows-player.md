# 009: Drawn cone changed size as the player moved around the mob
**Status:** ✅ FIXED
**Date:** 2026-08-09
**Keywords:** rendering, cone, radius, observer dependence, per-angle reach

## Symptom

"The cone keeps changing between small and larger sizes. Why?" Screenshots of
the same Crescent Nanka moments apart showed the cone at clearly different
lengths.

## Root Cause

When the per-angle evidence model was introduced, one value ended up doing two
jobs:

```csharp
var reachAtPlayer = AggroLearningStore.ReachForDrawing(profile, model, playerAngle, fallback);
var inside = distance - playerHitbox - obj.HitboxRadius <= reachAtPlayer;
var radius = Math.Clamp(reachAtPlayer, 0f, MaxRadius) + obj.HitboxRadius;   // <- wrong
```

`reachAtPlayer` is the reach **at the angle the player is standing at**, which
is exactly right for the "can it see me" test and exactly wrong for the drawn
size. As the player circled the mob, the angle changed, so the reach changed, so
the whole cone was redrawn longer or shorter every frame.

## Attempts

| Date | Attempt | Result |
|------|---------|--------|
| 2026-08-09 | Share one reach value between the inside test and the drawn radius | Cone size followed the player around |
| 2026-08-09 | Draw from the model's range; keep the per-angle reach for the inside test only | Fixed |

## Resolution / Lesson

The drawn radius comes from `model.Range` for a classified mob, or the fallback
for an unclassified one. The per-angle reach is used solely to decide whether
the player is currently detectable.

**Lesson: a drawn shape is a property of the thing drawn, never of the
observer's position.** Any time a render value is computed from the viewer's
state, that is a bug unless the shape is genuinely viewer-relative.

**Lesson: two questions that happen to share a formula still need two
variables.** "How far does this reach at angle θ" and "how big is this" were
collapsed into one expression because at the moment it was written the player's
angle was the only one being asked about. Naming the value `reachAtPlayer` and
then using it as a general radius was the tell, and the name was right there.
