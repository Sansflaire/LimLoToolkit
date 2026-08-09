using System;
using System.Collections.Generic;
using System.Numerics;

using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;

namespace LimLoToolkit.Tools;

/// <summary>
/// Watches nearby enemies and records the geometry of the moment one pulls onto
/// the player, so real detection ranges can be measured instead of guessed.
///
/// **Detecting a pull.** An enemy's <c>TargetObjectId</c> flipping to the
/// player is the signal. It is precise and needs no hooks.
///
/// **Two timing problems, and what is done about them.**
///
/// 1. *Position lag.* By the time the pull reaches us, the player has walked
///    closer than they were when they actually crossed the line. So the player
///    position used is the one from <see cref="AggroLatencyMs"/> ago, not the
///    current one. Residual error still biases every sample LOW, which is why
///    the estimator tracks the upper end of the distribution.
///
/// 2. *The mob turns to face you.* A mob rotates toward its new target
///    immediately, so its facing at detection time is nearly always pointing
///    straight at the player — measuring the angle then would report ~0 degrees
///    for every mob and collapse every cone. The facing used is therefore from
///    <see cref="RotationLookbackMs"/> ago, before it turned. If the mob rotated
///    more than <see cref="MaxRotationDriftDegrees"/> across that window it was
///    already turning, and the angle is discarded as untrustworthy while the
///    distance is still kept.
///
/// **Rejecting pulls that were not proximity detection.** A mob can target you
/// because you hit it, because it linked off a neighbour, or because it was
/// already fighting. Guards: the player must have been out of combat on the
/// previous frame, the enemy must not already have been in combat, and only the
/// first pull inside <see cref="BurstWindowMs"/> is recorded so a chain of
/// linked adds contributes one sample rather than five bad ones.
/// </summary>
public sealed class AggroTrainer
{
    /// <summary>Assumed round trip before a pull becomes visible to us.</summary>
    private const long AggroLatencyMs = 150;

    /// <summary>How far back to read the mob's facing, to beat its turn-to-face.</summary>
    private const long RotationLookbackMs = 400;

    /// <summary>Rotation change across the window that invalidates the angle.</summary>
    private const float MaxRotationDriftDegrees = 30f;

    /// <summary>Only the first pull in this window is recorded.</summary>
    private const long BurstWindowMs = 1000;

    /// <summary>
    /// Samples beyond this are nonsense and get dropped. Real FFXIV aggro is
    /// well inside 25y; anything larger is a mob that was already chasing us,
    /// not a fresh detection.
    /// </summary>
    private const float MaxPlausibleDistance = AggroLearningStore.MaxPlausibleSampleDistance;

    private const long HistoryWindowMs = 600;

    /// <summary>Entries kept in the visible activity log.</summary>
    private const int MaxLoggedEvents = 8;

    private readonly struct Snapshot(long tick, Vector3 position, float rotation)
    {
        public long    Tick     { get; } = tick;
        public Vector3 Position { get; } = position;
        public float   Rotation { get; } = rotation;
    }

    /// <summary>One line of training activity, accepted or rejected.</summary>
    public readonly struct TrainingEvent(string message, bool accepted)
    {
        public string Message  { get; } = message;
        public bool   Accepted { get; } = accepted;
    }

    private readonly AggroLearningStore _store;
    private readonly Configuration      _config;
    private readonly List<TrainingEvent> _events = new();

    private readonly List<Snapshot>                   _playerHistory = new();
    private readonly Dictionary<ulong, List<Snapshot>> _enemyHistory  = new();
    private readonly Dictionary<ulong, bool>           _wasTargetingMe = new();
    private readonly Dictionary<ulong, bool>           _wasInCombat    = new();

    private bool _playerWasInCombat;
    private long _lastSampleAt;

    public AggroTrainer(AggroLearningStore store, Configuration config)
    {
        _store  = store;
        _config = config;
    }

    /// <summary>Samples recorded since the plugin loaded, for the UI.</summary>
    public int SamplesThisSession { get; private set; }

    /// <summary>Pulls seen but rejected this session, for the UI.</summary>
    public int RejectedThisSession { get; private set; }

    /// <summary>Most recent activity, newest first.</summary>
    public IReadOnlyList<TrainingEvent> RecentEvents => _events;

    /// <summary>Drops all transient history. Called when training is switched off.</summary>
    public void Reset()
    {
        _playerHistory.Clear();
        _enemyHistory.Clear();
        _wasTargetingMe.Clear();
        _wasInCombat.Clear();
        _playerWasInCombat = false;
    }

    public void ClearEvents() => _events.Clear();

    /// <summary>
    /// Records one line of activity and, when enabled, echoes it to chat so a
    /// pull is visible without the toolkit window being open on this panel.
    /// </summary>
    private void Log(string message, bool accepted)
    {
        if (accepted)
            SamplesThisSession++;
        else
            RejectedThisSession++;

        _events.Insert(0, new TrainingEvent(message, accepted));
        while (_events.Count > MaxLoggedEvents)
            _events.RemoveAt(_events.Count - 1);

        if (_config.AnnounceTrainingInChat)
        {
            try
            {
                Plugin.ChatGui.Print($"[LimLo] {(accepted ? "Recorded" : "Skipped")}: {message}");
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Failed to print a training message to chat.");
            }
        }

        // Information, not Debug: Dalamud's log filters Debug out by default,
        // which made "is it even running?" impossible to answer from the log.
        Plugin.Log.Information($"[AggroTrainer] {(accepted ? "REC" : "SKIP")} {message}");
    }

    /// <summary>
    /// Called once per frame on the game thread with the enemies already
    /// gathered by the vision scan, so the object table is only walked once.
    /// </summary>
    public void Tick(IGameObject player, IReadOnlyList<TrackedEnemy> enemies, ushort territoryId)
    {
        var now = Environment.TickCount64;

        Append(_playerHistory, new Snapshot(now, player.Position, player.Rotation), now);

        // Match on both id forms. A mob's TargetObjectId is not guaranteed to
        // be expressed the same way as the local player's GameObjectId, and if
        // it is not, a single comparison silently never fires and NOTHING is
        // ever recorded — with no error to show for it.
        var playerGameObjectId = player.GameObjectId;
        var playerEntityId     = (ulong)player.EntityId;
        var playerHitbox       = player.HitboxRadius;
        var playerInCombat     = Plugin.Condition[ConditionFlag.InCombat];

        var seen = new HashSet<ulong>();

        foreach (var tracked in enemies)
        {
            var enemy = tracked.Object;
            var id    = enemy.GameObjectId;
            seen.Add(id);

            if (!_enemyHistory.TryGetValue(id, out var history))
            {
                history = new List<Snapshot>();
                _enemyHistory[id] = history;
            }

            Append(history, new Snapshot(now, enemy.Position, enemy.Rotation), now);

            var targetingMe = enemy.TargetObjectId == playerGameObjectId
                              || enemy.TargetObjectId == playerEntityId;

            var wasTargeting = _wasTargetingMe.TryGetValue(id, out var prevTarget) && prevTarget;
            var wasFighting  = _wasInCombat.TryGetValue(id, out var prevCombat) && prevCombat;

            // Secondary signal. If the target id comparison ever fails us, a mob
            // flipping into combat while we were peaceful is still a pull.
            var justEnteredCombat = tracked.InCombat && !wasFighting && !playerInCombat;
            var pulled            = (targetingMe && !wasTargeting) || justEnteredCombat;

            // We must have been watching this mob long enough to have real
            // pre-aggro geometry. Without this check, the first frame a mob
            // appears counts as a pull — so anything already chasing us gets
            // recorded at whatever range it walked in at, and a plugin reload
            // (which wipes the tables below) poisons every mob on screen at
            // once. That was the "ranges are suddenly enormous" bug.
            var watchedLongEnough = history.Count > 0 && now - history[0].Tick >= RotationLookbackMs;

            if (pulled)
            {
                if (tracked.Ignored)
                {
                    // Never swallow this silently. A mob on the ignore list
                    // dropping its pull with no message looks identical to the
                    // trainer being broken.
                    Log($"{tracked.Name} pulled, but it is on your ignore list — not recorded.", false);
                }
                else if (!watchedLongEnough)
                {
                    Log($"{tracked.Name} — it was already chasing you when it came into view, not a clean pull.", false);
                }
                else
                {
                    TryRecord(tracked, history, playerHitbox, playerInCombat, wasFighting, territoryId, now);
                }
            }

            _wasTargetingMe[id] = targetingMe;
            _wasInCombat[id]    = tracked.InCombat;
        }

        // Drop history for anything that left, so the buffers stay bounded.
        if (_enemyHistory.Count > seen.Count)
        {
            var gone = new List<ulong>();
            foreach (var id in _enemyHistory.Keys)
                if (!seen.Contains(id))
                    gone.Add(id);

            foreach (var id in gone)
            {
                _enemyHistory.Remove(id);
                _wasTargetingMe.Remove(id);
                _wasInCombat.Remove(id);
            }
        }

        _playerWasInCombat = playerInCombat;
    }

    private void TryRecord(
        TrackedEnemy   tracked,
        List<Snapshot> enemyHistory,
        float          playerHitbox,
        bool           playerInCombat,
        bool           enemyWasInCombat,
        ushort         territoryId,
        long           now)
    {
        // Already fighting? Then this is a target switch or a link, not a fresh
        // detection. _playerWasInCombat is the previous frame's value on purpose:
        // the pull itself flips the player into combat on the very same frame.
        if (_playerWasInCombat || enemyWasInCombat)
        {
            Log($"{tracked.Name} — you were already in combat, so this is a link or a target switch, not a detection.", false);
            return;
        }

        if (now - _lastSampleAt < BurstWindowMs)
        {
            Log($"{tracked.Name} — arrived within a second of the last pull, treated as an add.", false);
            return;
        }

        var playerThen = SampleAt(_playerHistory, now - AggroLatencyMs);
        var enemyThen  = SampleAt(enemyHistory, now - AggroLatencyMs);
        if (playerThen is not { } playerSnapshot || enemyThen is not { } enemySnapshot)
            return;

        // Gap between hitrings — the same quantity the range slider means.
        var centreDistance = Vector3.Distance(playerSnapshot.Position, enemySnapshot.Position);
        var gap            = centreDistance - tracked.HitboxRadius - playerHitbox;

        if (gap < 0f || gap > MaxPlausibleDistance)
        {
            Log($"{tracked.Name} — {gap:F1}y is too far to be a real detection, discarded.", false);
            return;
        }

        // Facing from before the mob turned toward us, and only if it was not
        // already rotating during the window.
        float? angle = null;
        var    facingThen = SampleAt(enemyHistory, now - RotationLookbackMs);

        if (facingThen is { } facingSnapshot)
        {
            var drift = AngleDifferenceDegrees(facingSnapshot.Rotation, enemySnapshot.Rotation);
            if (drift <= MaxRotationDriftDegrees)
            {
                angle = AggroLearningStore.AngleOffFacing(
                    facingSnapshot.Position, facingSnapshot.Rotation, playerSnapshot.Position);
            }
        }

        _store.AddSample(
            tracked.BaseId,
            tracked.Name,
            tracked.SheetOmnidirectional,
            territoryId,
            tracked.Level,
            tracked.MaxHp,
            tracked.HitboxRadius,
            gap,
            angle);

        _lastSampleAt = now;

        // Write to disk NOW. Measurements are expensive to gather and a reload
        // must never be able to discard them.
        _store.Save();

        var profile = _store.Find(tracked.BaseId);
        var count   = profile?.Distances.Count ?? 1;
        var solved  = _store.ConfidenceOf(profile) == AggroConfidence.Confident;

        var detail = angle is { } recorded
            ? $"{gap:F1}y at {recorded:F0}° off its facing"
            : $"{gap:F1}y (it was turning, angle discarded)";

        Log($"{tracked.Name} — {detail}. Sample {count}/{AggroLearningStore.MinSamplesForConfident}"
            + (solved ? ", SOLVED." : "."), true);
    }

    private static void Append(List<Snapshot> history, Snapshot snapshot, long now)
    {
        history.Add(snapshot);

        while (history.Count > 0 && now - history[0].Tick > HistoryWindowMs)
            history.RemoveAt(0);
    }

    /// <summary>Buffered snapshot closest to the requested time, if any.</summary>
    private static Snapshot? SampleAt(List<Snapshot> history, long targetTick)
    {
        if (history.Count == 0)
            return null;

        var      bestDelta = long.MaxValue;
        Snapshot best      = history[0];

        foreach (var snapshot in history)
        {
            var delta = Math.Abs(snapshot.Tick - targetTick);
            if (delta >= bestDelta)
                continue;

            bestDelta = delta;
            best      = snapshot;
        }

        return best;
    }

    /// <summary>Smallest absolute difference between two headings, in degrees.</summary>
    private static float AngleDifferenceDegrees(float a, float b)
    {
        var delta = MathF.Abs(a - b) % MathF.Tau;
        if (delta > MathF.PI)
            delta = MathF.Tau - delta;

        return delta * 180f / MathF.PI;
    }
}

/// <summary>One live enemy, gathered once per frame and shared by both tools.</summary>
public readonly struct TrackedEnemy(
    IGameObject obj,
    uint        baseId,
    string      name,
    bool        sheetOmnidirectional,
    bool        inCombat,
    byte        level,
    uint        maxHp,
    float       hitboxRadius,
    float       distance)
{
    public IGameObject Object               { get; } = obj;
    public uint        BaseId               { get; } = baseId;
    /// <summary>On the user's ignore list: tracked for logging, never recorded.</summary>
    public bool        Ignored              { get; init; }
    public string      Name                 { get; } = name;
    public bool        SheetOmnidirectional { get; } = sheetOmnidirectional;
    public bool        InCombat             { get; } = inCombat;
    public byte        Level                { get; } = level;
    public uint        MaxHp                { get; } = maxHp;
    public float       HitboxRadius         { get; } = hitboxRadius;
    public float       Distance             { get; } = distance;
}
