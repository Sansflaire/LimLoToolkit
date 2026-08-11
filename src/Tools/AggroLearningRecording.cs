using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace LimLoToolkit.Tools;

/// <summary>
/// The recording half of <see cref="AggroLearningStore"/> — everything that
/// WRITES evidence into a profile.
///
/// **This file is excluded from the public build.** See the
/// <c>Compile Remove</c> in <c>LimLoToolkit.csproj</c>, which drops it and
/// <c>AggroTrainer.cs</c> from the Release configuration. The public plugin
/// reads the shipped dataset and shows confirmed values; it has no code path
/// that could record anything, because the code is not in the binary.
///
/// Split out as a partial class rather than fenced with <c>#if</c> because the
/// read side and the write side are interleaved in the main file — the reading
/// helpers sit between the recording methods, and fencing them cut out
/// classification code the public build genuinely needs.
///
/// Anything added here must be write-side only. If a new method is needed by
/// the drawing or classification path, it belongs in
/// <c>AggroLearning.cs</c> instead, or the public build will not compile.
/// </summary>
public sealed partial class AggroLearningStore
{
    /// <summary>Returns true only when this tightened the known bound.</summary>
    public bool AddSafeObservation(uint baseId, string name, bool sheetOmnidirectional,
                                   ushort territoryId, float hitboxRadius,
                                   float distance, float angleDegrees)
    {
        if (!_byBaseId.TryGetValue(baseId, out var profile))
        {
            profile = new AggroProfile
            {
                BaseId               = baseId,
                Name                 = name,
                SheetOmnidirectional = sheetOmnidirectional,
                TerritoryId          = territoryId,
                HitboxRadius         = hitboxRadius,
            };
            _byBaseId[baseId] = profile;
        }

        EnsureBins(profile);

        var bin      = BinFor(angleDegrees);
        var existing = profile.BinMinSafeDistance[bin];

        // Only a MEANINGFULLY closer safe stand teaches us anything. Without
        // the epsilon this fires on every frame of an approach — 6.8, 6.8, 6.7
        // — each one logging and writing to disk.
        if (existing > 0f && distance >= existing - SafeTightenEpsilon)
            return false;

        profile.BinMinSafeDistance[bin] = distance;
        profile.BinSafeSamples[bin]++;

        // RECENCY WINS. A slice cannot both reach further than this and fail to
        // notice someone standing here, so the older reading is simply wrong
        // and is deleted rather than averaged or out-voted.
        //
        // Deleting is what allows a shape to shrink while still guaranteeing
        // every pull it *keeps* lies inside what is drawn — the contradicting
        // pull no longer exists to be violated.
        if (profile.BinSamples[bin] > 0 && profile.BinMaxDistance[bin] >= distance)
        {
            Plugin.Log.Information(
                $"[Aggro] {profile.Name}: stood {distance:F1}y at {BinLabel(bin)} unnoticed — "
                + $"dropping the older {profile.BinMaxDistance[bin]:F1}y pull there.");

            profile.BinMaxDistance[bin] = 0f;
            profile.BinSamples[bin]     = 0;

            RecomputeAfterPullRemoval(profile);
        }

        return true;
    }

    /// <summary>
    /// Records one pull. <paramref name="angleDegrees"/> is null when the mob
    /// was turning during the sample window and its facing could not be trusted
    /// — the distance is still good, the angle is not.
    /// </summary>
    public void AddSample(
        uint    baseId,
        string  name,
        bool    sheetOmnidirectional,
        ushort  territoryId,
        byte    level,
        uint    maxHp,
        float   hitboxRadius,
        float   distance,
        float?  angleDegrees)
    {
        if (!_byBaseId.TryGetValue(baseId, out var profile))
        {
            profile = new AggroProfile { BaseId = baseId };
            _byBaseId[baseId] = profile;
        }

        profile.Name                 = name;
        profile.SheetOmnidirectional = sheetOmnidirectional;
        profile.TerritoryId          = territoryId;
        profile.Level                = level;
        profile.MaxHp                = maxHp;
        profile.HitboxRadius         = hitboxRadius;

        profile.Distances.Add(distance);
        if (profile.Distances.Count > MaxSamplesPerMob)
            profile.Distances.RemoveAt(0);

        if (distance > profile.MaxDistance + GrowthEpsilon)
        {
            profile.MaxDistance         = distance;
            profile.SamplesSinceMaxGrew = 0;
        }
        else
        {
            profile.MaxDistance = MathF.Max(profile.MaxDistance, distance);
            profile.SamplesSinceMaxGrew++;
        }

        if (angleDegrees is { } angle)
        {
            profile.Angles.Add(angle);
            if (profile.Angles.Count > MaxSamplesPerMob)
                profile.Angles.RemoveAt(0);

            // Fold the sample into the angular envelope.
            EnsureBins(profile);
            var bin = BinFor(angle);
            profile.BinSamples[bin]++;
            if (distance > profile.BinMaxDistance[bin])
                profile.BinMaxDistance[bin] = distance;

            // RECENCY WINS, the other direction. It just noticed us from here,
            // which is the strongest evidence there is — something that happens
            // TO the player in the moment. Any older "I stood here unnoticed"
            // at or inside this distance is stale and goes.
            var staleSafe = profile.BinMinSafeDistance[bin];
            if (staleSafe > 0f && staleSafe <= distance)
            {
                Plugin.Log.Information(
                    $"[Aggro] {profile.Name}: noticed at {distance:F1}y at {BinLabel(bin)} — "
                    + $"dropping the older safe-at-{staleSafe:F1}y reading there.");

                profile.BinMinSafeDistance[bin] = 0f;
                profile.BinSafeSamples[bin]     = 0;
            }

            if (angle > profile.MaxAngle + AngleGrowthEpsilon)
            {
                profile.MaxAngle              = angle;
                profile.SamplesSinceAngleGrew = 0;
            }
            else
            {
                profile.MaxAngle = MathF.Max(profile.MaxAngle, angle);
                profile.SamplesSinceAngleGrew++;
            }

            if (!sheetOmnidirectional && angle > RearPullAngleDegrees)
                profile.RearPulls++;
        }
    }

    /// <summary>
    /// Records where a mob was seen, thinned to one point per grid cell.
    /// Returns true when a genuinely new spot was added, so callers know
    /// whether anything needs saving.
    /// </summary>
    public bool AddSighting(uint baseId, string name, ushort territory, Vector3 position)
    {
        // Create on first sight. A mob you have merely walked past still has a
        // location worth knowing, and it shows up as a red no-data entry, which
        // is exactly the state worth seeing.
        if (!_byBaseId.TryGetValue(baseId, out var profile))
        {
            profile = new AggroProfile
            {
                BaseId      = baseId,
                Name        = name,
                TerritoryId = territory,
            };

            _byBaseId[baseId] = profile;
        }
        else if (string.IsNullOrEmpty(profile.Name) && !string.IsNullOrEmpty(name))
        {
            profile.Name = name;
        }

        foreach (var existing in profile.Sightings)
        {
            if (existing.Territory != territory)
                continue;

            if (MathF.Abs(existing.X - position.X) < SightingGridSize
                && MathF.Abs(existing.Z - position.Z) < SightingGridSize)
                return false;
        }

        if (profile.Sightings.Count >= MaxSightingsPerMob)
            return false;

        profile.Sightings.Add(new Sighting
        {
            Territory = territory,
            X         = position.X,
            Y         = position.Y,
            Z         = position.Z,
        });

        return true;
    }
}
