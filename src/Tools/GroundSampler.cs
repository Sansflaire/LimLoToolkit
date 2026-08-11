using System;
using System.Collections.Generic;
using System.Numerics;

using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;

using CsFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

namespace LimLoToolkit.Tools;

/// <summary>
/// Drops a shape's outline onto the terrain, so a detection ring on a hillside
/// follows the hillside instead of hovering at the mob's own height.
///
/// **Mechanism.** <c>Framework.Instance()-&gt;BGCollisionModule</c> and its
/// <c>RaycastMaterialFilter(Vector3 origin, Vector3 direction, out RaycastHit
/// hit, float maxDistance)</c> overload — the game's own background collision.
/// A ray is fired straight down over each point of the outline and the hit
/// height is used. Nothing else is involved: no vnavmesh, no sibling plugin, no
/// IPC, so this stays inside the standalone mandate.
///
/// **Why it is cached rather than sampled every frame.** A 64-segment ring is
/// 64 raycasts. Twenty mobs on screen at sixty frames a second is eighty
/// thousand raycasts a second, which is not a thing to do to somebody's game.
/// Instead each mob's outline is sampled once and kept until the mob actually
/// moves or turns, and only <see cref="RebuildsPerTick"/> outlines may be built
/// in any one tick. Mobs mostly stand still, so in the steady state this costs
/// nothing at all.
///
/// **Nothing here blocks drawing.** A shape with no outline yet is simply drawn
/// flat for a frame or two, exactly as it was before, until its turn comes up.
/// A stall in sampling must never make a mob's detection range disappear.
/// </summary>
public sealed class GroundSampler
{
    /// <summary>How many outlines may be (re)sampled in a single tick.</summary>
    private const int RebuildsPerTick = 2;

    /// <summary>Movement, in yalms, that invalidates a cached outline.</summary>
    private const float MoveTolerance = 0.35f;

    /// <summary>Turn, in radians, that invalidates a cached cone. About 3°.</summary>
    private const float TurnTolerance = 0.05f;

    /// <summary>Radius or arc change, past which the outline is stale.</summary>
    private const float ShapeTolerance = 0.15f;

    /// <summary>
    /// Cached outlines are dropped after this long untouched, so mobs that
    /// despawn do not accumulate. Nothing depends on the timing.
    /// </summary>
    private const long EvictAfterMs = 30_000;

    /// <summary>Start the ray this far above the mob's feet.</summary>
    private const float RayStartAbove = 4f;

    /// <summary>
    /// How far down to look. Generous enough for a cliff edge inside the shape,
    /// short enough that a ring over a chasm gives up rather than snapping to
    /// whatever is at the bottom.
    /// </summary>
    private const float RayLength = 25f;

    private sealed class Entry
    {
        public Vector3    Origin;
        public float      Facing;
        public float      Radius;
        public float      ConeDegrees;
        public Vector3[]  Loop = Array.Empty<Vector3>();
        public Vector3[]? EdgeA;
        public Vector3[]? EdgeB;
        public long       TouchedAt;
    }

    private readonly Dictionary<ulong, Entry> _cache = new();

    private int  _rebuildsThisTick;
    private long _lastEvictAt;

    /// <summary>Sampled outlines currently held, for the panel to report.</summary>
    public int CachedCount => _cache.Count;

    /// <summary>Raycasts fired on the most recent tick, for the panel to report.</summary>
    public int RaycastsLastTick { get; private set; }

    /// <summary>Call once per game-thread tick, before any <see cref="Get"/>.</summary>
    public void BeginTick()
    {
        _rebuildsThisTick = 0;
        RaycastsLastTick  = 0;

        var now = Environment.TickCount64;
        if (now - _lastEvictAt < EvictAfterMs)
            return;

        _lastEvictAt = now;

        List<ulong>? stale = null;
        foreach (var (id, entry) in _cache)
            if (now - entry.TouchedAt > EvictAfterMs)
                (stale ??= new List<ulong>()).Add(id);

        if (stale == null)
            return;

        foreach (var id in stale)
            _cache.Remove(id);
    }

    /// <summary>Forgets everything. Used when the feature is switched off.</summary>
    public void Clear() => _cache.Clear();

    /// <summary>
    /// The ground-following outline for one shape, or null if it has not been
    /// sampled yet and the per-tick budget is spent. Callers draw flat when this
    /// returns null rather than drawing nothing.
    /// </summary>
    /// <param name="id">Game object id — the cache key.</param>
    /// <param name="centre">Mob position.</param>
    /// <param name="facing">Mob rotation in radians.</param>
    /// <param name="radius">Outline radius in yalms.</param>
    /// <param name="coneDegrees">360 for a full ring, less for a wedge.</param>
    /// <param name="segments">Points around a full circle.</param>
    /// <param name="radialAt">
    /// Per-angle reach, for an outline whose radius varies with angle. Given an
    /// angle in degrees off the facing (0-180), returns the reach there. Null
    /// for a plain circle or cone.
    /// </param>
    public (Vector3[] Loop, Vector3[]? EdgeA, Vector3[]? EdgeB)? Get(
        ulong           id,
        Vector3         centre,
        float           facing,
        float           radius,
        float           coneDegrees,
        int             segments,
        Func<float, float>? radialAt = null)
    {
        if (_cache.TryGetValue(id, out var entry) && !IsStale(entry, centre, facing, radius, coneDegrees))
        {
            entry.TouchedAt = Environment.TickCount64;
            return (entry.Loop, entry.EdgeA, entry.EdgeB);
        }

        if (_rebuildsThisTick >= RebuildsPerTick)
            return entry is { Loop.Length: > 0 }
                ? (entry.Loop, entry.EdgeA, entry.EdgeB)   // stale but better than nothing
                : null;

        _rebuildsThisTick++;

        entry ??= new Entry();
        Build(entry, centre, facing, radius, coneDegrees, segments, radialAt);

        entry.Origin      = centre;
        entry.Facing      = facing;
        entry.Radius      = radius;
        entry.ConeDegrees = coneDegrees;
        entry.TouchedAt   = Environment.TickCount64;

        _cache[id] = entry;
        return (entry.Loop, entry.EdgeA, entry.EdgeB);
    }

    private static bool IsStale(Entry entry, Vector3 centre, float facing, float radius, float cone)
    {
        if (entry.Loop.Length == 0)
            return true;

        if (Vector3.DistanceSquared(entry.Origin, centre) > MoveTolerance * MoveTolerance)
            return true;

        if (MathF.Abs(entry.Radius - radius) > ShapeTolerance)
            return true;

        if (MathF.Abs(entry.ConeDegrees - cone) > 1f)
            return true;

        // A full ring looks the same whichever way the mob is facing, so a mob
        // spinning on the spot must not rebuild it sixty times a second.
        if (cone < 360f && MathF.Abs(WrapPi(entry.Facing - facing)) > TurnTolerance)
            return true;

        return false;
    }

    private void Build(
        Entry               entry,
        Vector3             centre,
        float               facing,
        float               radius,
        float               coneDegrees,
        int                 segments,
        Func<float, float>? radialAt)
    {
        var full = coneDegrees >= 360f;

        if (full)
        {
            var loop = new Vector3[segments + 1];
            for (var i = 0; i <= segments; i++)
            {
                var sweep = i / (float)segments * MathF.Tau;
                var r     = radialAt == null ? radius : radialAt(OffFacingDegrees(sweep));
                loop[i]   = OnGround(centre, facing + sweep, r);
            }

            entry.Loop  = loop;
            entry.EdgeA = null;
            entry.EdgeB = null;
            return;
        }

        // A wedge: the arc, plus a line back to the mob down each side. Those
        // radial edges get sampled too — on a slope a straight line from the mob
        // to the arc cuts through the hill just as visibly as the arc would.
        var half     = coneDegrees * 0.5f * MathF.PI / 180f;
        var arcSteps = Math.Max(6, (int)(segments * (coneDegrees / 360f)));

        var arc = new Vector3[arcSteps + 1];
        for (var i = 0; i <= arcSteps; i++)
        {
            var angle = -half + i / (float)arcSteps * (half * 2f);
            var r     = radialAt == null ? radius : radialAt(MathF.Abs(angle) * 180f / MathF.PI);
            arc[i]    = OnGround(centre, facing + angle, r);
        }

        entry.Loop  = arc;
        entry.EdgeA = RadialEdge(centre, facing - half, radialAt == null ? radius : radialAt(half * 180f / MathF.PI));
        entry.EdgeB = RadialEdge(centre, facing + half, radialAt == null ? radius : radialAt(half * 180f / MathF.PI));
    }

    /// <summary>A sampled line from the mob out to the arc along one edge.</summary>
    private Vector3[] RadialEdge(Vector3 centre, float angle, float radius)
    {
        // One point every couple of yalms is enough to read as following the
        // ground without spending raycasts on a straight line.
        var steps  = Math.Clamp((int)(radius / 2f), 2, 10);
        var points = new Vector3[steps + 1];

        for (var i = 0; i <= steps; i++)
            points[i] = OnGround(centre, angle, radius * i / steps);

        return points;
    }

    /// <summary>Angle off the facing, folded to 0-180.</summary>
    private static float OffFacingDegrees(float sweep) =>
        MathF.Abs(WrapPi(sweep)) * 180f / MathF.PI;

    private static float WrapPi(float radians)
    {
        var wrapped = radians % MathF.Tau;
        if (wrapped > MathF.PI)  wrapped -= MathF.Tau;
        if (wrapped < -MathF.PI) wrapped += MathF.Tau;
        return wrapped;
    }

    /// <summary>
    /// One point of the outline, dropped onto whatever the collision mesh says
    /// is underneath it. Falls back to the mob's own height when the ray finds
    /// nothing — over a hole, off the edge of the world, or on unloaded terrain
    /// — because a point at the wrong height is still better than a gap in the
    /// shape.
    /// </summary>
    private unsafe Vector3 OnGround(Vector3 centre, float worldAngle, float distance)
    {
        var point = new Vector3(
            centre.X + distance * MathF.Sin(worldAngle),
            centre.Y,
            centre.Z + distance * MathF.Cos(worldAngle));

        try
        {
            var framework = CsFramework.Instance();
            if (framework == null)
                return point;

            var collision = framework->BGCollisionModule;
            if (collision == null)
                return point;

            RaycastsLastTick++;

            var origin    = new Vector3(point.X, point.Y + RayStartAbove, point.Z);
            var direction = new Vector3(0f, -1f, 0f);

            // The four-argument overload is a static convenience wrapper, not an
            // instance call — the module pointer above is only fetched to check
            // the world is actually loaded before firing rays at it.
            if (BGCollisionModule.RaycastMaterialFilter(origin, direction, out var hit, RayLength))
                point.Y = hit.Point.Y;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Ground raycast failed; falling back to the mob's height.");
        }

        return point;
    }
}
