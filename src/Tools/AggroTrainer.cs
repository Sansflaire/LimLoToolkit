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

    /// <summary>Samples beyond this are nonsense and get dropped.</summary>
    private const float MaxPlausibleDistance = 50f;

    private const long HistoryWindowMs = 600;

    private readonly struct Snapshot(long tick, Vector3 position, float rotation)
    {
        public long    Tick     { get; } = tick;
        public Vector3 Position { get; } = position;
        public float   Rotation { get; } = rotation;
    }

    private readonly AggroLearningStore _store;

    private readonly List<Snapshot>                   _playerHistory = new();
    private readonly Dictionary<ulong, List<Snapshot>> _enemyHistory  = new();
    private readonly Dictionary<ulong, bool>           _wasTargetingMe = new();
    private readonly Dictionary<ulong, bool>           _wasInCombat    = new();

    private bool _playerWasInCombat;
    private long _lastSampleAt;

    public AggroTrainer(AggroLearningStore store) => _store = store;

    /// <summary>Samples recorded since the plugin loaded, for the UI.</summary>
    public int SamplesThisSession { get; private set; }

    public string LastEvent { get; private set; } = string.Empty;

    /// <summary>Drops all transient history. Called when training is switched off.</summary>
    public void Reset()
    {
        _playerHistory.Clear();
        _enemyHistory.Clear();
        _wasTargetingMe.Clear();
        _wasInCombat.Clear();
        _playerWasInCombat = false;
    }

    /// <summary>
    /// Called once per frame on the game thread with the enemies already
    /// gathered by the vision scan, so the object table is only walked once.
    /// </summary>
    public void Tick(IGameObject player, IReadOnlyList<TrackedEnemy> enemies, ushort territoryId)
    {
        var now = Environment.TickCount64;

        Append(_playerHistory, new Snapshot(now, player.Position, player.Rotation), now);

        var playerId       = player.GameObjectId;
        var playerHitbox   = player.HitboxRadius;
        var playerInCombat = Plugin.Condition[ConditionFlag.InCombat];

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

            var targetingMe = enemy.TargetObjectId == playerId;
            var wasTargeting = _wasTargetingMe.TryGetValue(id, out var prevTarget) && prevTarget;
            var wasFighting  = _wasInCombat.TryGetValue(id, out var prevCombat) && prevCombat;

            if (targetingMe && !wasTargeting)
                TryRecord(tracked, history, playerHitbox, playerInCombat, wasFighting, territoryId, now);

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
            LastEvent = $"Skipped {tracked.Name}: already in combat (link or target switch).";
            return;
        }

        if (now - _lastSampleAt < BurstWindowMs)
        {
            LastEvent = $"Skipped {tracked.Name}: within {BurstWindowMs} ms of the last pull.";
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
            LastEvent = $"Skipped {tracked.Name}: implausible gap of {gap:F1}y.";
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
        SamplesThisSession++;

        LastEvent = angle is { } recorded
            ? $"{tracked.Name}: pulled at {gap:F1}y, {recorded:F0} degrees off its facing."
            : $"{tracked.Name}: pulled at {gap:F1}y (it was turning, angle discarded).";

        Plugin.Log.Debug($"Aggro sample: {tracked.Name} ({tracked.BaseId}) gap={gap:F2} angle={angle?.ToString("F1") ?? "n/a"}");
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
    public string      Name                 { get; } = name;
    public bool        SheetOmnidirectional { get; } = sheetOmnidirectional;
    public bool        InCombat             { get; } = inCombat;
    public byte        Level                { get; } = level;
    public uint        MaxHp                { get; } = maxHp;
    public float       HitboxRadius         { get; } = hitboxRadius;
    public float       Distance             { get; } = distance;
}
