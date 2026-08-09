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

    /// <summary>Keep the table bounded; a mob needs nowhere near this many.</summary>
    private const int MaxSamplesPerMob = 100;

    /// <summary>Padding added to the widest measured angle when sizing a cone.</summary>
    private const float ConeMarginDegrees = 10f;

    /// <summary>Beyond this, a "sees only forwards" mob is contradicting the sheet.</summary>
    public const float RearPullAngleDegrees = 100f;

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

            if (!sheetOmnidirectional && angle > RearPullAngleDegrees)
                profile.RearPulls++;
        }
    }

    public AggroConfidence ConfidenceOf(AggroProfile? profile)
    {
        if (profile == null || profile.Distances.Count == 0)
            return AggroConfidence.None;

        return profile.Distances.Count >= MinSamplesForConfident
               && profile.SamplesSinceMaxGrew >= StableSamplesForConfident
            ? AggroConfidence.Confident
            : AggroConfidence.Learning;
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
