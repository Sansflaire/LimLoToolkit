using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace LimLoToolkit.Tools;

/// <summary>How much we trust a mob's measured numbers.</summary>
public enum AggroConfidence
{
    /// <summary>Never seen this mob pull. Nothing measured.</summary>
    None,

    /// <summary>Some pulls recorded, but the range is still growing.</summary>
    Learning,

    /// <summary>Enough pulls, and the measured range has stopped growing.</summary>
    Confident,
}

/// <summary>
/// Everything learned about one mob type, keyed by its <c>BNpcBase</c> row id.
/// Serialized into the plugin config, so it survives restarts.
/// </summary>
[Serializable]
public sealed class AggroProfile
{
    public uint   BaseId               { get; set; }
    public string Name                 { get; set; } = string.Empty;
    public bool   SheetOmnidirectional { get; set; }
    public ushort TerritoryId          { get; set; }
    public byte   Level                { get; set; }
    public uint   MaxHp                { get; set; }
    public float  HitboxRadius         { get; set; }

    /// <summary>Hitring-to-hitring gap at each recorded pull, in yalms.</summary>
    public List<float> Distances { get; set; } = new();

    /// <summary>Absolute angle off the mob's facing at each pull, 0-180 degrees.</summary>
    public List<float> Angles { get; set; } = new();

    /// <summary>Pulls from behind a mob the sheet claims only sees forwards.</summary>
    public int RearPulls { get; set; }

    /// <summary>Largest gap seen, and how many samples since it last grew.</summary>
    public float MaxDistance        { get; set; }
    public int   SamplesSinceMaxGrew { get; set; }

    /// <summary>
    /// Same idea for the cone. Tracked separately because the two converge at
    /// very different rates: range settles from any approach, but the cone only
    /// grows when you happen to approach from a wide angle.
    /// </summary>
    public float MaxAngle             { get; set; }
    public int   SamplesSinceAngleGrew { get; set; }

    /// <summary>
    /// The detection envelope: furthest pull seen in each angular slice around
    /// the mob's facing, and how many samples landed in each.
    ///
    /// This is what lets the shape emerge from data instead of being assumed.
    /// A pure sight mob fills the front bins and leaves the rear ones short; a
    /// pure sound mob fills every bin equally; a mob with BOTH a short
    /// all-directions core and a longer forward lobe — which is what several
    /// of these actually look like — produces exactly that profile, and no
    /// cone-or-circle model could represent it.
    /// </summary>
    public List<float> BinMaxDistance { get; set; } = new();
    public List<int>   BinSamples     { get; set; } = new();
}

/// <summary>
/// The learned-aggro table plus the estimator that turns raw pull samples into
/// a radius and cone.
///
/// **Why the maximum matters.** Every measurement is biased LOW: you cross the
/// real trigger line, the server notices, the packet arrives, and only then do
/// we see it — by which point you have walked closer. Nothing biases a sample
/// high except a misattributed pull. So the estimate tracks the upper end of
/// the distribution, not the mean, and the capture side works hard to reject
/// misattributed pulls (see EnemyVisionTool's sampling guards).
/// </summary>
public sealed class AggroLearningStore
{
    /// <summary>Samples before a mob can be called solved.</summary>
    public const int MinSamplesForConfident = 8;

    /// <summary>Consecutive samples that must fail to grow the max.</summary>
    public const int StableSamplesForConfident = 4;

    /// <summary>A new max must beat the old by this much to count as growth.</summary>
    private const float GrowthEpsilon = 0.25f;

    /// <summary>Angle samples needed before a cone can be called solved.</summary>
    public const int MinAngleSamplesForConfident = 6;

    /// <summary>Consecutive angle samples that must fail to widen the cone.</summary>
    public const int StableAngleSamplesForConfident = 3;

    private const float AngleGrowthEpsilon = 4f;

    /// <summary>Keep the table bounded; a mob needs nowhere near this many.</summary>
    private const int MaxSamplesPerMob = 100;

    /// <summary>Padding added to the widest measured angle when sizing a cone.</summary>
    private const float ConeMarginDegrees = 10f;

    /// <summary>Beyond this, a "sees only forwards" mob is contradicting the sheet.</summary>
    public const float RearPullAngleDegrees = 100f;

    /// <summary>
    /// Angular slices of the detection envelope, over 0-180 degrees off the
    /// mob's facing. Detection is symmetric left/right, so folding to an
    /// absolute angle halves how much walking is needed to fill the profile.
    /// </summary>
    public const int   AngleBins       = 12;
    public const float BinWidthDegrees = 180f / AngleBins;

    /// <summary>Filled slices needed before the envelope is trustworthy.</summary>
    public const int MinFilledBins = 6;

    public static int BinFor(float angleDegrees) =>
        Math.Clamp((int)(angleDegrees / BinWidthDegrees), 0, AngleBins - 1);

    public static string BinLabel(int bin) =>
        $"{bin * BinWidthDegrees:F0}-{(bin + 1) * BinWidthDegrees:F0}°";

    /// <summary>Old configs predate the bins; make sure they are the right size.</summary>
    public static void EnsureBins(AggroProfile profile)
    {
        while (profile.BinMaxDistance.Count < AngleBins) profile.BinMaxDistance.Add(0f);
        while (profile.BinSamples.Count     < AngleBins) profile.BinSamples.Add(0);
    }

    public static int FilledBins(AggroProfile profile)
    {
        EnsureBins(profile);

        var filled = 0;
        for (var i = 0; i < AngleBins; i++)
            if (profile.BinSamples[i] > 0)
                filled++;

        return filled;
    }

    /// <summary>
    /// Measured detection distance at a given angle off the mob's facing.
    /// Falls back to the nearest slice that does have data, so a partly-filled
    /// envelope still draws something sensible rather than collapsing to zero.
    /// Returns null when the mob has no angular data at all.
    /// </summary>
    public static float? RadiusAtAngle(AggroProfile profile, float angleDegrees)
    {
        EnsureBins(profile);

        var bin = BinFor(Math.Clamp(angleDegrees, 0f, 180f));
        if (profile.BinSamples[bin] > 0)
            return profile.BinMaxDistance[bin];

        // Walk outwards to the closest filled slice on either side.
        for (var offset = 1; offset < AngleBins; offset++)
        {
            var low  = bin - offset;
            var high = bin + offset;

            if (low >= 0 && profile.BinSamples[low] > 0)
                return profile.BinMaxDistance[low];

            if (high < AngleBins && profile.BinSamples[high] > 0)
                return profile.BinMaxDistance[high];
        }

        return null;
    }

    private readonly Configuration _config;
    private readonly Dictionary<uint, AggroProfile> _byBaseId = new();
    private readonly HashSet<uint> _ignored = new();

    public AggroLearningStore(Configuration config)
    {
        _config = config;

        // The config stores a plain list (bulletproof to serialize); the
        // dictionary is just an index over the very same instances.
        foreach (var profile in _config.LearnedAggro)
            _byBaseId[profile.BaseId] = profile;

        foreach (var baseId in _config.IgnoredMobBaseIds)
            _ignored.Add(baseId);

        PurgeImplausibleProfiles();
    }

    /// <summary>Samples past this are not real detections. Shared with the trainer.</summary>
    public const float MaxPlausibleSampleDistance = 25f;

    /// <summary>
    /// Heals data recorded before the first-sight guard existed. A plugin reload
    /// used to wipe the trainer's "was this mob targeting me" table, so on the
    /// next frame every mob already chasing the player looked like a fresh pull
    /// and got recorded at whatever range it walked in at — 40y and up. Those
    /// profiles paint enormous shapes and survive restarts, so any profile
    /// holding an impossible sample is reset rather than left to mislead.
    ///
    /// A whole-profile reset rather than dropping the bad samples: the angular
    /// bins keep maxima only, so a poisoned bin cannot be unmixed.
    /// </summary>
    private void PurgeImplausibleProfiles()
    {
        var purged = 0;

        foreach (var profile in _byBaseId.Values)
        {
            var bad = profile.Distances.Any(d => d > MaxPlausibleSampleDistance)
                      || profile.BinMaxDistance.Any(d => d > MaxPlausibleSampleDistance);

            if (!bad)
                continue;

            profile.Distances.Clear();
            profile.Angles.Clear();
            profile.BinMaxDistance.Clear();
            profile.BinSamples.Clear();
            profile.MaxDistance           = 0f;
            profile.SamplesSinceMaxGrew   = 0;
            profile.MaxAngle              = 0f;
            profile.SamplesSinceAngleGrew = 0;
            profile.RearPulls             = 0;
            purged++;
        }

        if (purged > 0)
            Plugin.Log.Warning($"Reset {purged} aggro profile(s) holding impossible samples (>{MaxPlausibleSampleDistance}y).");
    }

    // ── Ignore list ──────────────────────────────────────────────────────────

    public int IgnoredCount => _ignored.Count;

    /// <summary>Mobs marked irrelevant get no shape drawn and no samples taken.</summary>
    public bool IsIgnored(uint baseId) => _ignored.Contains(baseId);

    public void SetIgnored(uint baseId, bool ignored)
    {
        if (ignored)
        {
            if (_ignored.Add(baseId))
                _config.IgnoredMobBaseIds.Add(baseId);
        }
        else if (_ignored.Remove(baseId))
        {
            _config.IgnoredMobBaseIds.RemoveAll(id => id == baseId);
        }
    }

    public void ClearIgnored()
    {
        _ignored.Clear();
        _config.IgnoredMobBaseIds.Clear();
    }

    public IReadOnlyCollection<AggroProfile> All => _byBaseId.Values;

    public AggroProfile? Find(uint baseId) =>
        _byBaseId.TryGetValue(baseId, out var profile) ? profile : null;

    public int TotalSamples => _byBaseId.Values.Sum(p => p.Distances.Count);

    public void Clear()
    {
        _byBaseId.Clear();
        _config.LearnedAggro.Clear();
    }

    public void Forget(uint baseId)
    {
        if (!_byBaseId.Remove(baseId))
            return;

        _config.LearnedAggro.RemoveAll(p => p.BaseId == baseId);
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
            _config.LearnedAggro.Add(profile);
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

    /// <summary>Is the RANGE settled? Converges from any approach.</summary>
    public static bool RangeSolved(AggroProfile profile) =>
        profile.Distances.Count >= MinSamplesForConfident
        && profile.SamplesSinceMaxGrew >= StableSamplesForConfident;

    /// <summary>
    /// Is the SHAPE settled? This no longer trusts the sheet's sight/sound flag
    /// as a shortcut — a mob the sheet calls forward-only can still have an
    /// all-directions core, and a mob it calls omnidirectional can still reach
    /// further forwards. The only way to know is to have walked in from enough
    /// different angles, so the test is envelope coverage.
    /// </summary>
    public static bool ShapeSolved(AggroProfile profile) =>
        FilledBins(profile) >= MinFilledBins
        && profile.Angles.Count >= MinAngleSamplesForConfident
        && profile.SamplesSinceAngleGrew >= StableAngleSamplesForConfident;

    /// <summary>Green only when both halves are settled.</summary>
    public AggroConfidence ConfidenceOf(AggroProfile? profile)
    {
        if (profile == null || profile.Distances.Count == 0)
            return AggroConfidence.None;

        return RangeSolved(profile) && ShapeSolved(profile)
            ? AggroConfidence.Confident
            : AggroConfidence.Learning;
    }

    /// <summary>Short human explanation of what a mob still needs.</summary>
    public static string WhatIsMissing(AggroProfile profile)
    {
        var rangeOk = RangeSolved(profile);
        var shapeOk = ShapeSolved(profile);

        if (rangeOk && shapeOk)
            return "solved";

        var filled = FilledBins(profile);

        if (!rangeOk && !shapeOk)
            return $"needs more pulls ({profile.Distances.Count}/{MinSamplesForConfident}) "
                   + $"and more approach angles ({filled}/{MinFilledBins} covered)";

        if (!rangeOk)
            return $"needs more pulls ({profile.Distances.Count}/{MinSamplesForConfident})";

        return $"needs approaches from more angles ({filled}/{MinFilledBins} covered)";
    }

    /// <summary>
    /// How the measured envelope actually behaves, in plain words. This is the
    /// answer to "is it sight or sound" for this specific mob, derived from
    /// what happened rather than from the sheet.
    /// </summary>
    public static string DescribeMeasuredShape(AggroProfile profile)
    {
        EnsureBins(profile);

        if (FilledBins(profile) < 3)
            return "not enough angles measured yet";

        float front = 0f, rear = 0f;
        var frontBins = 0; var rearBins = 0;

        for (var i = 0; i < AngleBins; i++)
        {
            if (profile.BinSamples[i] == 0)
                continue;

            // Front third versus rear third of the sweep.
            if (i < AngleBins / 3)      { front += profile.BinMaxDistance[i]; frontBins++; }
            else if (i >= AngleBins * 2 / 3) { rear += profile.BinMaxDistance[i];  rearBins++;  }
        }

        if (frontBins == 0 || rearBins == 0)
            return "front and rear not both measured yet";

        front /= frontBins;
        rear  /= rearBins;

        if (rear <= 0.1f)
            return $"forward only — {front:F1}y ahead, nothing behind";

        var ratio = front / MathF.Max(rear, 0.1f);

        return ratio switch
        {
            < 1.25f => $"all directions — about {front:F1}y everywhere",
            < 2.0f  => $"mostly even — {front:F1}y ahead, {rear:F1}y behind",
            _       => $"forward lobe plus a close core — {front:F1}y ahead, {rear:F1}y behind",
        };
    }

    /// <summary>
    /// Measured detection gap in yalms, or null when nothing is recorded.
    /// Uses the 90th percentile once there are enough samples to have outliers
    /// worth trimming, and the plain maximum before that.
    /// </summary>
    public float? EstimatedDistance(AggroProfile? profile)
    {
        if (profile == null || profile.Distances.Count == 0)
            return null;

        return profile.Distances.Count >= 10
            ? Percentile(profile.Distances, 0.90f)
            : profile.Distances.Max();
    }

    /// <summary>
    /// Measured cone width in degrees, or null when unknown. Omnidirectional
    /// mobs report a full turn. Sight mobs get the widest observed angle plus a
    /// margin, doubled, because the cone is symmetric about the facing.
    /// </summary>
    public float? EstimatedConeDegrees(AggroProfile? profile)
    {
        if (profile == null)
            return null;

        if (profile.SheetOmnidirectional || profile.RearPulls > 0)
            return 360f;

        if (profile.Angles.Count == 0)
            return null;

        var widest = profile.Angles.Count >= 10
            ? Percentile(profile.Angles, 0.90f)
            : profile.Angles.Max();

        return Math.Clamp((widest + ConeMarginDegrees) * 2f, 15f, 360f);
    }

    /// <summary>True when measurements contradict the sheet's sight/sound flag.</summary>
    public static bool ContradictsSheet(AggroProfile? profile) =>
        profile is { SheetOmnidirectional: false, RearPulls: > 0 };

    private static float Percentile(List<float> values, float percentile)
    {
        var sorted = values.ToArray();
        Array.Sort(sorted);
        var index = (int)MathF.Round(percentile * (sorted.Length - 1));
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    /// <summary>Absolute angle in degrees between a mob's facing and the player.</summary>
    public static float AngleOffFacing(Vector3 mobPosition, float mobRotation, Vector3 playerPosition)
    {
        var toPlayer = playerPosition - mobPosition;
        toPlayer.Y = 0f;

        if (toPlayer.LengthSquared() < 0.0001f)
            return 0f;

        var forward = new Vector3(MathF.Sin(mobRotation), 0f, MathF.Cos(mobRotation));
        var cos     = Vector3.Dot(Vector3.Normalize(toPlayer), forward);

        return MathF.Acos(Math.Clamp(cos, -1f, 1f)) * 180f / MathF.PI;
    }
}
