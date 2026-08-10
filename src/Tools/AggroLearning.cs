using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

using Newtonsoft.Json;

namespace LimLoToolkit.Tools;

/// <summary>One place a mob type was observed.</summary>
[Serializable]
public sealed class Sighting
{
    public ushort Territory { get; set; }
    public float  X         { get; set; }
    public float  Y         { get; set; }
    public float  Z         { get; set; }
}

/// <summary>On-disk shape of the training data file.</summary>
[Serializable]
public sealed class AggroDataFile
{
    public int                Version  { get; set; } = 1;
    public string             SavedAt  { get; set; } = string.Empty;
    public List<AggroProfile> Profiles { get; set; } = new();
}

/// <summary>
/// The only two detection shapes the game actually implements. Everything the
/// trainer gathers is evidence toward deciding which of these a mob is, and
/// with what numbers — the per-angle envelope is the raw evidence, this is the
/// conclusion drawn from it.
/// </summary>
public enum DetectionType
{
    /// <summary>Not enough evidence to say yet.</summary>
    Unknown,

    /// <summary>Forward arc only. Approaching from outside the arc is safe at any range.</summary>
    Cone,

    /// <summary>All directions. Any approach inside the range is detected.</summary>
    Radius,
}

/// <summary>A mob's detection, as classified from the evidence.</summary>
public readonly struct DetectionModel(
    DetectionType type,
    float         range,
    float         halfAngleDegrees,
    string        reason)
{
    public DetectionType Type { get; } = type;

    /// <summary>Proven maximum reach: the furthest distance we were ever noticed from.</summary>
    public float Range { get; } = range;

    /// <summary>Half-width of the arc. 180 for a radius mob.</summary>
    public float HalfAngleDegrees { get; } = halfAngleDegrees;

    /// <summary>Plain-language justification, shown in the UI.</summary>
    public string Reason { get; } = reason;

    public float FullConeDegrees => MathF.Min(360f, HalfAngleDegrees * 2f);
}

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

    /// <summary>Knowledge level, so relevance survives without the mob nearby.</summary>
    public int    ForayLevel           { get; set; }

    /// <summary>
    /// Where this mob type has been seen, thinned to one point per grid cell so
    /// the list stays small however long you play. Enough to show the ground it
    /// occupies rather than every individual spawn.
    /// </summary>
    public List<Sighting> Sightings { get; set; } = new();

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

    /// <summary>
    /// NEGATIVE evidence: the closest we have stood in each slice, for long
    /// enough to be noticed, and were NOT detected. 0 means none recorded.
    ///
    /// This is what lets the model shrink. Pulls alone can only ever push the
    /// estimate outwards, so one bad sample inflates a mob forever. A pull at
    /// distance p proves the reach is at least p; standing unnoticed at
    /// distance s proves it is less than s. Together they bracket the true
    /// boundary between them, which is both a better estimate and an honest
    /// measure of how well we actually know it.
    /// </summary>
    public List<float> BinMinSafeDistance { get; set; } = new();
    public List<int>   BinSafeSamples     { get; set; } = new();
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

    /// <summary>A safe stand must beat the known bound by this much to count.</summary>
    private const float SafeTightenEpsilon = 0.5f;

    /// <summary>Minimum gap between debounced writes.</summary>
    private const long SaveDebounceMs = 2000;

    private bool _dirty;
    private long _lastSaveAt;

    /// <summary>
    /// Marks the table changed without writing immediately. Used for frequent,
    /// individually cheap updates such as safe observations. Pulls call
    /// <see cref="Save"/> directly — they are rare and expensive to re-gather.
    /// </summary>
    public void MarkDirty() => _dirty = true;

    /// <summary>Writes a pending change once the debounce window has passed.</summary>
    public void FlushIfDirty()
    {
        if (!_dirty || Environment.TickCount64 - _lastSaveAt < SaveDebounceMs)
            return;

        Save();
    }

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

    // Short aliases so call sites read cleanly.
    public const DetectionType UnknownType = DetectionType.Unknown;
    public const DetectionType ConeType    = DetectionType.Cone;
    public const DetectionType RadiusType  = DetectionType.Radius;

    public static int BinFor(float angleDegrees) =>
        Math.Clamp((int)(angleDegrees / BinWidthDegrees), 0, AngleBins - 1);

    public static string BinLabel(int bin) =>
        $"{bin * BinWidthDegrees:F0}-{(bin + 1) * BinWidthDegrees:F0}°";

    /// <summary>Old configs predate the bins; make sure they are the right size.</summary>
    public static void EnsureBins(AggroProfile profile)
    {
        while (profile.BinMaxDistance.Count     < AngleBins) profile.BinMaxDistance.Add(0f);
        while (profile.BinSamples.Count         < AngleBins) profile.BinSamples.Add(0);
        while (profile.BinMinSafeDistance.Count < AngleBins) profile.BinMinSafeDistance.Add(0f);
        while (profile.BinSafeSamples.Count     < AngleBins) profile.BinSafeSamples.Add(0);
    }

    /// <summary>
    /// Records standing unnoticed at <paramref name="distance"/> in the slice
    /// covering <paramref name="angleDegrees"/>. Proves the reach there is less
    /// than that.
    /// </summary>
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

        // NOTE: an earlier version deleted pull evidence in this slice whenever
        // a closer safe stand contradicted it. Simulating that over the real
        // data first showed it wiping every pull on 8 of 13 mobs, because the
        // contradictions are pervasive rather than occasional. Pervasive
        // contradiction means one SOURCE is systematically wrong, and the
        // answer is to fix that source — not to let the two sides annihilate
        // each other. See the player-initiated-pull guard in AggroTrainer.
        return true;
    }

    /// <summary>
    /// Rebuilds the summary values after pull evidence is discarded, so a
    /// deleted pull cannot keep influencing the classification through a stale
    /// maximum.
    /// </summary>
    private static void RecomputeAfterPullRemoval(AggroProfile profile)
    {
        var maxDistance = 0f;
        var maxAngle    = 0f;

        for (var i = 0; i < AngleBins; i++)
        {
            if (profile.BinSamples[i] == 0)
                continue;

            maxDistance = MathF.Max(maxDistance, profile.BinMaxDistance[i]);
            maxAngle    = MathF.Max(maxAngle, (i + 0.5f) * BinWidthDegrees);
        }

        profile.MaxDistance = maxDistance;
        profile.MaxAngle    = maxAngle;
    }

    /// <summary>
    /// True when a slice's positive and negative evidence disagree — a pull
    /// recorded further out than a distance we later stood at unnoticed. Means
    /// one of the two is noise, usually an old bad pull.
    /// </summary>
    public static bool BinContradicts(AggroProfile profile, int bin)
    {
        EnsureBins(profile);

        var safe = profile.BinMinSafeDistance[bin];
        return safe > 0f && profile.BinSamples[bin] > 0 && safe <= profile.BinMaxDistance[bin];
    }

    /// <summary>
    /// How tightly a slice is pinned down: the gap between the furthest pull
    /// and the closest safe stand. Null when either side is missing.
    /// </summary>
    public static float? BinUncertainty(AggroProfile profile, int bin)
    {
        EnsureBins(profile);

        var safe = profile.BinMinSafeDistance[bin];
        if (safe <= 0f || profile.BinSamples[bin] == 0)
            return null;

        return MathF.Max(0f, safe - profile.BinMaxDistance[bin]);
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

        var direct = ReachIn(profile, bin);
        if (direct.HasValue)
            return direct;

        // Walk outwards to the closest slice that has any evidence at all.
        for (var offset = 1; offset < AngleBins; offset++)
        {
            if (bin - offset >= 0 && ReachIn(profile, bin - offset) is { } low)
                return low;

            if (bin + offset < AngleBins && ReachIn(profile, bin + offset) is { } high)
                return high;
        }

        return null;
    }

    /// <summary>
    /// Best estimate of the reach in one slice, from both kinds of evidence.
    ///
    /// Pulls give a lower bound, safe stands give an upper bound, so the truth
    /// sits between them and the midpoint is the best single guess. With only
    /// pulls we can just report the furthest one. With only safe stands we know
    /// the reach is under that, so we report just inside it rather than
    /// pretending to know nothing.
    /// </summary>
    private static float? ReachIn(AggroProfile profile, int bin)
    {
        var pulled = profile.BinSamples[bin] > 0 ? profile.BinMaxDistance[bin] : (float?)null;
        var safe   = profile.BinMinSafeDistance[bin] > 0f ? profile.BinMinSafeDistance[bin] : (float?)null;

        return (pulled, safe) switch
        {
            ({ } p, { } s) when s > p => (p + s) * 0.5f,   // bracketed: split the difference
            ({ } p, { } s)            => MathF.Min(p, s),  // contradictory: take the cautious one
            ({ } p, null)             => p,
            (null, { } s)             => MathF.Max(0f, s - 0.5f),
            _                         => null,
        };
    }

    private readonly Configuration _config;
    private readonly Dictionary<uint, AggroProfile> _byBaseId = new();
    private readonly HashSet<uint> _ignored = new();

    /// <summary>
    /// Training data lives in its OWN file, not in the plugin config.
    ///
    /// It used to live in the config object, which was only ever written when
    /// the user happened to toggle a checkbox — so a plugin reload silently
    /// discarded every sample collected since the last toggle. Measurements are
    /// expensive to gather (one clean pull at a time) and must never depend on
    /// something unrelated deciding to save.
    ///
    /// Writes are atomic: serialize to .tmp, keep the previous file as .bak,
    /// then move into place. A crash mid-write can therefore cost at most the
    /// newest sample, never the whole table.
    /// </summary>
    private readonly string _filePath;
    private readonly string _backupPath;

    public AggroLearningStore(Configuration config, string directory)
    {
        _config     = config;
        _filePath   = Path.Combine(directory, "aggro-training.json");
        _backupPath = Path.Combine(directory, "aggro-training.bak.json");

        LoadFromDisk();
        MigrateFromConfig();

        foreach (var baseId in _config.IgnoredMobBaseIds)
            _ignored.Add(baseId);

        PurgeImplausibleProfiles();

        // Write once on startup if the file is missing, so the storage path is
        // proven working before any measurement depends on it — rather than
        // discovering it was broken after losing a session's data.
        if (!File.Exists(_filePath))
            Save();
    }

    public string FilePath => _filePath;

    /// <summary>When the table was last written, for the UI to show.</summary>
    public string LastSavedAt { get; private set; } = "never";

    private void LoadFromDisk()
    {
        foreach (var path in new[] { _filePath, _backupPath })
        {
            try
            {
                if (!File.Exists(path))
                    continue;

                var data = JsonConvert.DeserializeObject<AggroDataFile>(File.ReadAllText(path));
                if (data?.Profiles == null)
                    continue;

                foreach (var profile in data.Profiles)
                    _byBaseId[profile.BaseId] = profile;

                LastSavedAt = string.IsNullOrEmpty(data.SavedAt) ? "unknown" : data.SavedAt;
                Plugin.Log.Information(
                    $"Loaded {data.Profiles.Count} aggro profile(s) from {Path.GetFileName(path)}.");
                return;
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, $"Failed to read aggro training data from {path}; trying the backup.");
            }
        }
    }

    /// <summary>One-time lift of anything still sitting in the old config list.</summary>
    private void MigrateFromConfig()
    {
        if (_config.LearnedAggro.Count == 0)
            return;

        var moved = 0;
        foreach (var profile in _config.LearnedAggro)
        {
            if (_byBaseId.ContainsKey(profile.BaseId))
                continue;

            _byBaseId[profile.BaseId] = profile;
            moved++;
        }

        _config.LearnedAggro.Clear();

        if (moved > 0)
        {
            Plugin.Log.Information($"Migrated {moved} aggro profile(s) out of the plugin config into their own file.");
            Save();
        }
    }

    /// <summary>
    /// Persists the table. Called after every accepted sample, so a reload or a
    /// crash cannot lose measurements.
    /// </summary>
    public void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var data = new AggroDataFile
            {
                SavedAt  = DateTime.UtcNow.ToString("o"),
                Profiles = _byBaseId.Values.ToList(),
            };

            var tempPath = _filePath + ".tmp";
            File.WriteAllText(tempPath, JsonConvert.SerializeObject(data, Formatting.Indented));

            // Previous good copy becomes the backup, then the new file lands.
            if (File.Exists(_filePath))
                File.Copy(_filePath, _backupPath, true);

            File.Move(tempPath, _filePath, true);

            LastSavedAt = data.SavedAt;
            _dirty      = false;
            _lastSaveAt = Environment.TickCount64;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Failed to save aggro training data.");
        }
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

    /// <summary>
    /// True when a mob is filtered out by the name rule rather than by an
    /// explicit ignore. Kept separate so the UI can explain WHICH kind of
    /// ignore is in play — "you ignored this" and "it is not a Crescent mob"
    /// are very different messages.
    /// </summary>
    public bool IsAutoIgnoredByName(string name)
    {
        if (!_config.AutoIgnoreNonMatchingNames)
            return false;

        var prefix = _config.TrackedNamePrefix?.Trim();
        if (string.IsNullOrEmpty(prefix))
            return false;

        return string.IsNullOrEmpty(name)
               || !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Either kind of ignore.</summary>
    public bool ShouldSkip(uint baseId, string name) =>
        IsIgnored(baseId) || IsAutoIgnoredByName(name);

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
        Save();
    }

    public void Forget(uint baseId)
    {
        if (!_byBaseId.Remove(baseId))
            return;

        _config.LearnedAggro.RemoveAll(p => p.BaseId == baseId);
        Save();
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

    /// <summary>
    /// Decides whether a mob is a cone or a radius, and with what numbers.
    ///
    /// The rules, which follow directly from how the game behaves:
    ///  - Being noticed at distance d proves the reach is AT LEAST d. The
    ///    furthest such distance is therefore the mob's range.
    ///  - Getting close from some angle and NOT being noticed, when we are
    ///    already known to be noticed from further away at another angle, can
    ///    only mean that angle lies outside a forward arc. That mob is a CONE.
    ///  - Being noticed from behind means there is no arc to be outside of.
    ///    That mob is a RADIUS.
    ///
    /// The cone's width is bracketed the same way its range is: the widest
    /// angle we were ever noticed at is inside the arc, the narrowest angle we
    /// safely closed in from is outside it, and the edge lies between them.
    /// </summary>
    public static DetectionModel Classify(AggroProfile profile)
    {
        EnsureBins(profile);

        var range = 0f;
        for (var i = 0; i < AngleBins; i++)
            if (profile.BinSamples[i] > 0)
                range = MathF.Max(range, profile.BinMaxDistance[i]);

        if (range <= 0f)
            return new DetectionModel(DetectionType.Unknown, 0f, 180f, "no pulls recorded yet");

        // Derived from the slices that still hold pulls, never from a stored
        // maximum — a pull discarded as contradicted must stop counting
        // immediately, and a stale summary value would keep it alive.
        var widestPullAngle = 0f;
        for (var i = 0; i < AngleBins; i++)
            if (profile.BinSamples[i] > 0)
                widestPullAngle = MathF.Max(widestPullAngle, (i + 0.5f) * BinWidthDegrees);

        // The narrowest angle at which we got closer than the proven range and
        // still went unnoticed. That angle must sit outside a forward arc.
        // Widest slice that has ever produced a pull. Everything up to here is
        // inside the arc by definition and no later reasoning may exclude it.
        var widestPullBinEdge = 0f;
        for (var i = 0; i < AngleBins; i++)
            if (profile.BinSamples[i] > 0)
                widestPullBinEdge = MathF.Max(widestPullBinEdge, (i + 1) * BinWidthDegrees);

        var    safeOutsideAngle = float.MaxValue;
        for (var i = 0; i < AngleBins; i++)
        {
            // A slice that has produced a pull IS inside the arc. Letting its
            // own safe reading mark it "outside" was cutting the arc back
            // across the very pull that proves it belongs — the cause of aggro
            // happening beyond the drawn cone.
            if (profile.BinSamples[i] > 0)
                continue;

            var safe = profile.BinMinSafeDistance[i];
            if (safe <= 0f || safe >= range - SafeTightenEpsilon)
                continue;

            var binCentre = (i + 0.5f) * BinWidthDegrees;
            if (binCentre <= widestPullAngle)
                continue;   // same arc, just a closer stand — not a cone signal

            safeOutsideAngle = MathF.Min(safeOutsideAngle, binCentre);
        }

        if (safeOutsideAngle < float.MaxValue)
        {
            // Edge lies between the widest detection and the closest safe angle,
            // but never inside a slice that has pulled. THE INVARIANT: if it
            // caught you at that angle and distance, the drawing covers that
            // angle and distance. Nothing here is allowed to violate it.
            var half = Math.Clamp((widestPullAngle + safeOutsideAngle) * 0.5f, 5f, 180f);
            half = MathF.Max(half, widestPullBinEdge);

            var bounded = BoundRangeBySafeStands(profile, range, half, out var shrunk);

            return new DetectionModel(
                DetectionType.Cone, bounded, half,
                shrunk
                    ? $"passed through the arc at {bounded:F1}y unnoticed, so its reach is under that; "
                      + $"blind past {safeOutsideAngle:F0}° — a forward arc"
                    : $"noticed out to {bounded:F1}y and as wide as {widestPullAngle:F0}°, but slipped inside "
                      + $"at {safeOutsideAngle:F0}° — a forward arc");
        }

        // Noticed from behind, with nothing suggesting a blind side.
        if (widestPullAngle >= 135f)
        {
            var bounded = BoundRangeBySafeStands(profile, range, 180f, out var shrunk);

            return new DetectionModel(
                DetectionType.Radius, bounded, 180f,
                shrunk
                    ? $"noticed from {widestPullAngle:F0}° off its facing, but walked past at "
                      + $"{bounded:F1}y unnoticed — all directions, and closer than first thought"
                    : $"noticed from {widestPullAngle:F0}° off its facing — all directions");
        }

        var unknownHalf     = MathF.Max(MathF.Max(widestPullAngle, 45f), widestPullBinEdge);
        var unknownBounded  = BoundRangeBySafeStands(profile, range, unknownHalf, out _);

        return new DetectionModel(
            DetectionType.Unknown, unknownBounded, unknownHalf,
            $"noticed out to {unknownBounded:F1}y, but only within {widestPullAngle:F0}° so far — "
            + "walk in from the side and from behind to settle cone versus radius");
    }

    /// <summary>
    /// Shrinks a range using safe stands taken INSIDE the detection arc.
    ///
    /// Pulls give the range a floor and nothing else, so without this a mob can
    /// only ever be judged bigger. Running straight through the front of a cone
    /// at 3y without being noticed proves the reach there is under 3y, however
    /// far away it once managed to catch you — and the drawing has to follow
    /// that or the user is walking through an area the plugin still paints as
    /// dangerous.
    ///
    /// Only stands within the arc count. A safe stand BEHIND a cone says
    /// nothing about how far it reaches forwards; that evidence is what defines
    /// the arc in the first place.
    /// </summary>
    private static float BoundRangeBySafeStands(
        AggroProfile profile,
        float        range,
        float        halfAngleDegrees,
        out bool     shrunk)
    {
        // DISABLED — kept for the reasoning, not the behaviour.
        //
        // This shrank the range to the closest safe stand inside the arc, which
        // was wrong in the one direction that matters. Where a slice holds both
        // a pull and a closer safe stand, the two contradict: a cone has ONE
        // range, so it cannot both reach 9.5y and fail to notice someone at
        // 3.2y at the same angle. One reading is noise.
        //
        // Taking the minimum resolved that in favour of "safe", which made the
        // shape under-draw and put the player in real aggro range outside the
        // drawing. Worse, it was sticky: a new pull further out could not grow
        // the range back, because the old safe stand still capped it.
        //
        // Over-drawing is an annoyance. Under-drawing walks you into a pull. So
        // pulls win, and contradictions are surfaced in the Mob Viewer for the
        // user to judge rather than silently resolved here.
        //
        // The correct treatment is almost certainly that a safe stand closer
        // than the range means that ANGLE is outside the arc, not that the
        // range is shorter — which narrows the cone instead of shortening it.
        // That needs the contradictory data cleaned up first.
        shrunk = false;
        return range;
    }

    /// <summary>
    /// Reach to DRAW at a given angle off the mob's facing.
    ///
    /// Classification supplies the model, but safe stands cap it regardless —
    /// including when there are no pulls at all and therefore no classification.
    /// That last case is the important one: a mob you have walked all around
    /// without ever being noticed has masses of evidence that its reach is
    /// small, and drawing the fallback circle over the top of that evidence is
    /// simply wrong. Proof of safety is proof, whether or not it has ever
    /// managed to catch you.
    /// </summary>
    public static float ReachForDrawing(
        AggroProfile?  profile,
        DetectionModel model,
        float          angleOffFacing,
        float          fallback)
    {
        var reach = model.Type switch
        {
            DetectionType.Cone   => angleOffFacing <= model.HalfAngleDegrees ? model.Range : 0f,
            DetectionType.Radius => model.Range,
            _                    => fallback,
        };

        if (profile == null)
            return reach;

        var safe = SafeCapAt(profile, angleOffFacing);
        if (safe is { } cap)
            reach = MathF.Min(reach, MathF.Max(0f, cap - 0.25f));

        return reach;
    }

    /// <summary>
    /// Smoothly interpolated upper bound at an angle, or null where nothing is
    /// known nearby.
    ///
    /// Reading raw slice values gives a staircase: each slice was measured at
    /// whatever distance the player happened to stand at, so neighbouring
    /// values jump around and the outline comes out as ragged steps. Blending
    /// between slice centres keeps the same information but renders as a curve.
    /// </summary>
    private static float? SafeCapAt(AggroProfile profile, float angleOffFacing)
    {
        EnsureBins(profile);

        var angle = Math.Clamp(angleOffFacing, 0f, 180f);

        // Position along the slice centres, which sit at (i + 0.5) * width.
        var t     = angle / BinWidthDegrees - 0.5f;
        var lower = (int)MathF.Floor(t);
        var frac  = t - lower;

        var a = NearestSafe(profile, lower);
        var b = NearestSafe(profile, lower + 1);

        return (a, b) switch
        {
            ({ } lo, { } hi) => lo + (hi - lo) * frac,
            ({ } lo, null)   => lo,
            (null, { } hi)   => hi,
            _                => null,
        };
    }

    /// <summary>Safe bound in a slice, or the closest slice that has one.</summary>
    private static float? NearestSafe(AggroProfile profile, int bin)
    {
        for (var offset = 0; offset < AngleBins; offset++)
        {
            var low  = bin - offset;
            var high = bin + offset;

            if (low >= 0 && low < AngleBins && profile.BinMinSafeDistance[low] > 0f)
                return profile.BinMinSafeDistance[low];

            if (high >= 0 && high < AngleBins && profile.BinMinSafeDistance[high] > 0f)
                return profile.BinMinSafeDistance[high];
        }

        return null;
    }

    /// <summary>Grid size for thinning sightings, in yalms.</summary>
    private const float SightingGridSize = 6f;

    /// <summary>Upper bound on stored sightings per mob type.</summary>
    private const int MaxSightingsPerMob = 400;

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

    /// <summary>True when a mob has any evidence worth drawing from.</summary>
    public static bool HasEvidence(AggroProfile? profile)
    {
        if (profile == null)
            return false;

        EnsureBins(profile);

        for (var i = 0; i < AngleBins; i++)
            if (profile.BinSamples[i] > 0 || profile.BinMinSafeDistance[i] > 0f)
                return true;

        return false;
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
