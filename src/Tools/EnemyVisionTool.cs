using System;
using System.Collections.Generic;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;

using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace LimLoToolkit.Tools;

/// <summary>
/// Draws each nearby enemy's detection shape on the ground: a cone for enemies
/// that only see forwards, a circle for those that detect in every direction.
///
/// The SHAPE is real game data (<c>BNpcBase.IsOmnidirectional</c>). The SIZE
/// starts as a guess and becomes measured once training mode has watched enough
/// real pulls — see <see cref="AggroTrainer"/> and docs/enemy-vision.md.
/// </summary>
public sealed class EnemyVisionTool : ITool
{
    public string Id          => "occult-enemy-vision";
    public string Name        => "Enemy Vision";
    public string Description => "Ground shapes showing what nearby Occult Crescent enemies can detect.";
    public string Category    => "Toolkit";

    private const ushort TerritorySouthHorn = 1252;
    private const ushort TerritoryNorthHorn = 1346;

    public const float MinRadius = 3f;
    public const float MaxRadius = 40f;
    public const float MinCone   = 15f;
    public const float MaxCone   = 360f;

    private const float RenderDistance = 70f;
    private const int   CircleSegments = 48;
    /// <summary>Line width for every shape this plugin draws itself. User-set.</summary>
    public const float MinThickness = 1.0f;
    public const float MaxThickness = 8.0f;

    private static readonly Vector4 SightColor  = new(0.98f, 0.75f, 0.25f, 0.85f);
    private static readonly Vector4 SoundColor  = new(0.62f, 0.55f, 0.95f, 0.85f);
    private static readonly Vector4 DangerColor = new(1.00f, 0.30f, 0.28f, 0.95f);

    private readonly struct Shape(
        Vector3         position,
        float           facing,
        float           radius,
        float           coneDegrees,
        bool            omnidirectional,
        bool            playerInside,
        string          name,
        uint            baseId,
        float           distance,
        AggroConfidence confidence,
        bool            measured)
    {
        public Vector3         Position        { get; } = position;
        public float           Facing          { get; } = facing;
        /// <summary>Drawn radius — the hitring gap PLUS the enemy's hitbox.</summary>
        public float           Radius          { get; } = radius;
        /// <summary>Hitring-to-hitring gap, i.e. what the slider and samples mean.</summary>
        public float           Gap             { get; init; }
        public float           HitboxRadius    { get; init; }
        /// <summary>Evidence for this mob, if any. Caps the drawn reach per angle.</summary>
        public AggroProfile?   Profile         { get; init; }
        /// <summary>Classification, when there is enough evidence for one.</summary>
        public DetectionModel  Model           { get; init; }
        public float           ConeDegrees     { get; } = coneDegrees;
        public bool            Omnidirectional { get; } = omnidirectional;
        public bool            PlayerInside    { get; } = playerInside;
        public string          Name            { get; } = name;
        public uint            BaseId          { get; } = baseId;
        public float           Distance        { get; } = distance;
        public AggroConfidence Confidence      { get; } = confidence;
        public bool            Measured        { get; } = measured;

        /// <summary>
        /// The outline dropped onto the terrain, sampled on the game thread.
        /// Null while it is still queued, in which case the shape draws flat -
        /// a frame of the old look beats a frame of nothing.
        /// </summary>
        public Vector3[]?      GroundLoop      { get; init; }

        /// <summary>Wedge sides, ground-sampled. Null for a closed loop.</summary>
        public Vector3[]?      GroundEdgeA     { get; init; }
        public Vector3[]?      GroundEdgeB     { get; init; }
    }

    private readonly Configuration      _config;
    private readonly AggroLearningStore _store;
    private readonly MobOutlines        _outlines = new();
    private readonly GroundSampler      _ground   = new();

#if !PUBLIC_BUILD
    private readonly AggroTrainer _trainer;
#endif

    private ExcelSheet<BNpcBase>? _bnpcSheet;

    private List<Shape> _shapes = new();
    private bool        _inOccultCrescent;
    private int         _threateningCount;
    private int?        _playerForayLevel;

#if !PUBLIC_BUILD
    private bool _trainingWasEnabled;
#endif
    private int         _outleveledCount;

#if PUBLIC_BUILD
    public EnemyVisionTool(Configuration config, AggroLearningStore store)
    {
        _config = config;
        _store  = store;
    }
#else
    public EnemyVisionTool(Configuration config, AggroLearningStore store, AggroTrainer trainer)
    {
        _config  = config;
        _store   = store;
        _trainer = trainer;
    }
#endif

    /// <summary>
    /// Outlines are written into the game's own render state, not drawn as an
    /// overlay, so they survive the plugin going away. Everything we touched
    /// has to be put back or the user is left with permanently outlined mobs
    /// and no way to clear them short of a zone change.
    /// </summary>
    public void Dispose()
    {
        var had = _outlines.OutlinedCount;
        _outlines.ClearAll();
        Plugin.Log.Information($"[EnemyVision] Dispose ran; cleared {had} outline(s).");
    }

    private static bool IsOccultCrescent(ushort territory) =>
        territory is TerritorySouthHorn or TerritoryNorthHorn;

    private static Vector3 FacingVector(float rotation) =>
        new(MathF.Sin(rotation), 0f, MathF.Cos(rotation));

    public void OnFrameworkUpdate()
    {
        var shapes      = new List<Shape>();
        var threatening = 0;

        try
        {
            var territory = (ushort)Plugin.ClientState.TerritoryType;
            _inOccultCrescent = IsOccultCrescent(territory);

#if !PUBLIC_BUILD
            // Training keeps per-enemy history buffers. Drop them the moment it
            // is switched off - or the moment Live Mode hides it - so nothing
            // lingers and no half-observed enemy is waiting when it comes back.
            var trainingOn = _config.AggroTrainingEnabled && !BuildFlavor.IsLive;

            if (_trainingWasEnabled && !trainingOn)
                _trainer.Reset();
            _trainingWasEnabled = trainingOn;
#endif

            if (!_inOccultCrescent)
            {
                _shapes = shapes;
                _threateningCount = 0;
                return;
            }

            var player = Plugin.ObjectTable.LocalPlayer;
            if (player == null)
            {
                _shapes = shapes;
                _threateningCount = 0;
                return;
            }

            var playerPos    = player.Position;
            var playerHitbox = player.HitboxRadius;

            _bnpcSheet ??= Plugin.DataManager.GetExcelSheet<BNpcBase>();

            var fallbackRadius = Math.Clamp(_config.EnemyVisionRadius, MinRadius, MaxRadius);
            var fallbackCone   = Math.Clamp(_config.EnemyVisionConeDegrees, MinCone, MaxCone);

            // Knowledge level. In the Crescent an enemy one level below the
            // player cannot aggro at all, so those are skipped entirely — both
            // to cut clutter and to keep them from feeding the trainer endless
            // meaningless "it did not notice me" evidence.
            if (_config.FollowGroundMesh)
                _ground.BeginTick();
            else
                _ground.Clear();

            var outlined    = new HashSet<ulong>();
            var playerForay = ForayLevel.TryGet(player);

            if (playerForay != _playerForayLevel)
                Plugin.Log.Information($"[EnemyVision] Player Knowledge level read as {playerForay?.ToString() ?? "none"}.");

            _playerForayLevel = playerForay;
            var outleveled  = 0;

#if !PUBLIC_BUILD
            var tracked = new List<TrackedEnemy>();
#endif

            foreach (var obj in Plugin.ObjectTable)
            {
                if (obj.ObjectKind != ObjectKind.BattleNpc)
                    continue;

                if (!obj.IsValid() || obj.IsDead)
                    continue;

                // Combatant (5) is what ordinary field mobs use; the enum has no
                // "Enemy" member. Excludes pets, buddies, race chocobos,
                // minions, party members and BNpc body parts.
                if (obj is not IBattleNpc battleNpc || battleNpc.BattleNpcKind != BattleNpcSubKind.Combatant)
                    continue;

                var distance = Vector3.Distance(playerPos, obj.Position);
                if (distance > RenderDistance)
                    continue;

                var omnidirectional = _bnpcSheet?.GetRowOrDefault(obj.BaseId)?.IsOmnidirectional ?? false;
                var name            = obj.Name.ToString();

                // Marked irrelevant, either explicitly or by the name rule.
                // Still handed to the trainer so a pull from it is reported
                // rather than vanishing — silence here is indistinguishable
                // from the trainer being broken.
                var enemyForay = ForayLevel.TryGet(obj);
                var harmless   = ForayLevel.IsHarmless(playerForay, enemyForay, _config.OutlevelMargin);

                // Outlines mark danger and nothing else. A mob the player
                // outlevels gets none at all — and is left out of the still-
                // present set too, so one that becomes harmless has its outline
                // taken off on the next tick.
                if (_config.ShowMobOutlines && !harmless)
                {
                    outlined.Add(obj.GameObjectId);
                    _outlines.Apply(obj);
                }

                if (_config.IgnoreOutleveledEnemies && harmless)
                {
                    // Skipped completely: no shape, no tracking, no samples.
                    // Its non-detections say nothing about its detection shape.
                    outleveled++;
                    continue;
                }

                var ignored = _store.ShouldSkip(obj.BaseId, name);

#if !PUBLIC_BUILD
                tracked.Add(new TrackedEnemy(
                    obj,
                    obj.BaseId,
                    name,
                    omnidirectional,
                    battleNpc.StatusFlags.HasFlag(StatusFlags.InCombat),
                    battleNpc.Level,
                    battleNpc.MaxHp,
                    obj.HitboxRadius,
                    distance) { Ignored = ignored, CurrentHp = battleNpc.CurrentHp });
#endif

                // Ignored mobs are tracked for logging only — no shape.
                if (ignored)
                    continue;

                // Measured numbers win over the slider when we have them.
                var profile    = _store.Find(obj.BaseId);
                var confidence = _store.ConfidenceOf(profile);

                // Live: only settled values are drawn. A mob without locked
                // numbers has nothing worth stating as fact, and an estimated
                // shape shown without its "this is a guess" apparatus would be
                // a claim the data cannot support.
                if (BuildFlavor.IsLive && profile?.Locked != true)
                    continue;

                var learnedDistance = _config.UseLearnedAggroRanges ? _store.EstimatedDistance(profile) : null;
                var learnedCone     = _config.UseLearnedAggroRanges ? _store.EstimatedConeDegrees(profile) : null;

                // Defensive clamp: a bad measurement must never be able to paint
                // a giant circle across the world.
                var gap  = Math.Clamp(learnedDistance ?? fallbackRadius, 0f, MaxRadius);
                var cone = Math.Clamp(learnedCone     ?? fallbackCone, MinCone, MaxCone);

                // Classify from evidence: the game only does cone or radius, so
                // the job is deciding which, not drawing a free-form outline.
                var model = profile != null && _config.UseLearnedAggroRanges
                    ? AggroLearningStore.Classify(profile)
                    : default;

                var classified = model.Type != AggroLearningStore.UnknownType;

                // Measurements can contradict the sheet: a "sees only forwards"
                // mob that pulled from behind is really omnidirectional.
                var effectiveOmni = omnidirectional || AggroLearningStore.ContradictsSheet(profile);

                var playerAngle = AggroLearningStore.AngleOffFacing(obj.Position, obj.Rotation, playerPos);

                // One path for everything. The classification supplies the model
                // where there is one, evidence caps it per angle either way, and
                // the fallback only fills the gaps that evidence leaves.
                var fallbackAtAngle = !effectiveOmni && cone < 360f && playerAngle > cone * 0.5f
                    ? 0f
                    : gap;

                var reachAtPlayer = AggroLearningStore.ReachForDrawing(
                    profile, model, playerAngle, fallbackAtAngle);

                var inside = distance - playerHitbox - obj.HitboxRadius <= reachAtPlayer;

                // The DRAWN size is a property of the mob, never of where the
                // player happens to be standing. Using the per-angle reach here
                // made the cone visibly grow and shrink as the player circled
                // it — the reach at the player's angle answers "can it see me",
                // which is a different question from "how big is it".
                var drawReach = classified ? model.Range : gap;
                var radius    = Math.Clamp(drawReach, 0f, MaxRadius) + obj.HitboxRadius;
                var drawCone = classified
                    ? (model.Type == AggroLearningStore.RadiusType ? 360f : model.FullConeDegrees)
                    : effectiveOmni ? 360f : cone;

                if (inside)
                    threatening++;

                var (groundLoop, groundEdgeA, groundEdgeB) = SampleGround(
                    obj, profile, model, classified, effectiveOmni, radius, drawCone, cone, gap);

                shapes.Add(new Shape(
                    obj.Position,
                    obj.Rotation,
                    radius,
                    drawCone,
                    drawCone >= 360f,
                    inside,
                    name,
                    obj.BaseId,
                    distance,
                    confidence,
                    classified)
                {
                    Gap          = gap,
                    HitboxRadius = obj.HitboxRadius,
                    Profile      = profile,
                    Model        = model,
                    GroundLoop   = groundLoop,
                    GroundEdgeA  = groundEdgeA,
                    GroundEdgeB  = groundEdgeB,
                });
            }

            shapes.Sort((a, b) => a.Distance.CompareTo(b.Distance));

            _outleveledCount = outleveled;

            // Put back anything that wandered out of range, and drop the lot
            // the moment the feature is switched off.
            if (_config.ShowMobOutlines)
                _outlines.ClearMissing(outlined);
            else
                _outlines.ClearAll();

#if !PUBLIC_BUILD
            if (_config.AggroTrainingEnabled && !BuildFlavor.IsLive)
                _trainer.Tick(player, tracked, territory);
#endif
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "EnemyVisionTool failed during its framework tick.");
        }

        _shapes           = shapes;
        _threateningCount = threatening;
    }

    // ── Overlay ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Asks the sampler for this mob's outline laid on the terrain, matching
    /// whichever of the three shapes the overlay is going to draw.
    ///
    /// Returns nulls when ground-following is off or the sampler's per-tick
    /// budget is spent, and the overlay falls back to drawing flat.
    /// </summary>
    private (Vector3[]? Loop, Vector3[]? EdgeA, Vector3[]? EdgeB) SampleGround(
        IGameObject    obj,
        AggroProfile?  profile,
        DetectionModel model,
        bool           classified,
        bool           effectiveOmni,
        float          radius,
        float          drawCone,
        float          cone,
        float          gap)
    {
        if (!_config.FollowGroundMesh)
            return (null, null, null);

        var sampleCone = drawCone;
        Func<float, float>? radialAt = null;

        // An unclassified mob with evidence draws a closed loop whose reach
        // varies with angle, so the sampler needs the same per-angle function
        // the overlay would have used.
        if (!classified && AggroLearningStore.HasEvidence(profile))
        {
            sampleCone = 360f;

            var capturedProfile = profile;
            var capturedModel   = model;
            var hitbox          = obj.HitboxRadius;
            var omni            = effectiveOmni;

            radialAt = offFacing =>
            {
                var fallback = !omni && cone < 360f && offFacing > cone * 0.5f ? 0f : gap;
                var reach    = AggroLearningStore.ReachForDrawing(
                    capturedProfile, capturedModel, offFacing, fallback);

                return Math.Clamp(reach, 0f, MaxRadius) + hitbox;
            };
        }

        var sampled = _ground.Get(
            obj.GameObjectId, obj.Position, obj.Rotation,
            radius, sampleCone, CircleSegments, radialAt);

        return sampled is { } outline
            ? (outline.Loop, outline.EdgeA, outline.EdgeB)
            : (null, null, null);
    }

    public void DrawOverlay()
    {
        if (!_inOccultCrescent)
            return;

        if (!_config.ShowSightEnemyVision && !_config.ShowSoundEnemyVision)
            return;

        var shapes = _shapes;
        if (shapes.Count == 0)
            return;

        var drawList  = ImGui.GetForegroundDrawList();
        var highlight = _config.HighlightEnemyVisionWhenInside;
        var thickness = Math.Clamp(_config.OverlayThickness, MinThickness, MaxThickness);

        foreach (var shape in shapes)
        {
            if (shape.Omnidirectional && !_config.ShowSoundEnemyVision)
                continue;
            if (!shape.Omnidirectional && !_config.ShowSightEnemyVision)
                continue;

            var colour = ImGui.ColorConvertFloat4ToU32(
                highlight && shape.PlayerInside
                    ? DangerColor
                    : shape.Omnidirectional ? SoundColor : SightColor);

            // Ground-sampled outline wins whenever there is one: it is the
            // same shape, just laid on the terrain instead of held at the mob's
            // own height. Everything below is the flat fallback for a shape
            // whose sampling has not come round yet.
            if (shape.GroundLoop is { Length: > 1 })
            {
                DrawGroundPolyline(drawList, shape.GroundLoop, colour, thickness);

                if (shape.GroundEdgeA is { Length: > 1 })
                    DrawGroundPolyline(drawList, shape.GroundEdgeA, colour, thickness);
                if (shape.GroundEdgeB is { Length: > 1 })
                    DrawGroundPolyline(drawList, shape.GroundEdgeB, colour, thickness);

                continue;
            }

            // Once a mob is classified it is one of the two real shapes, drawn
            // clean — the evidence already went into deciding which and how big.
            // Only an unclassified mob draws from raw bounds, and even then it
            // is smoothed rather than stepped.
            if (shape.Model.Type != AggroLearningStore.UnknownType)
            {
                if (shape.Model.Type == AggroLearningStore.RadiusType)
                    DrawGroundCircle(drawList, shape.Position, shape.Radius, colour, thickness);
                else
                    DrawGroundCone(drawList, shape.Position, shape.Radius, shape.Facing,
                                   shape.Model.FullConeDegrees, colour, thickness);
            }
            else if (AggroLearningStore.HasEvidence(shape.Profile))
                DrawEvidenceShape(drawList, shape, colour, thickness);
            else if (shape.Omnidirectional || shape.ConeDegrees >= 360f)
                DrawGroundCircle(drawList, shape.Position, shape.Radius, colour, thickness);
            else
                DrawGroundCone(drawList, shape.Position, shape.Radius, shape.Facing, shape.ConeDegrees, colour, thickness);
        }
    }

    /// <summary>
    /// Draws the measured detection envelope: a closed loop whose reach varies
    /// with angle, so whatever shape the data describes is what appears — a
    /// forward lobe, an even ring, or a lobe sitting on top of a close core.
    /// No cone-or-circle assumption anywhere.
    /// </summary>
    /// <summary>
    /// Draws the outline angle by angle from the evidence, so a direction you
    /// have proven safe pulls the line in tight against the mob instead of
    /// being covered by an assumed circle.
    ///
    /// A non-pull is evidence. Walking right up behind something and not being
    /// noticed proves that area is safe just as firmly as a pull proves the
    /// opposite, and the drawing has to show that or it is lying about what is
    /// known.
    /// </summary>
    private static void DrawEvidenceShape(ImDrawListPtr drawList, Shape shape, uint colour, float thickness)
    {
        Vector2? previous = null;

        for (var i = 0; i <= CircleSegments; i++)
        {
            var sweep      = i / (float)CircleSegments * MathF.Tau;
            var worldAngle = shape.Facing + sweep;

            // Fold to 0-180 off the facing; detection is left/right symmetric.
            var offFacing = MathF.Abs(WrapPi(sweep)) * 180f / MathF.PI;

            var fallback = !shape.Omnidirectional && shape.ConeDegrees < 360f
                           && offFacing > shape.ConeDegrees * 0.5f
                ? 0f
                : shape.Gap;

            var reach = AggroLearningStore.ReachForDrawing(
                shape.Profile, shape.Model, offFacing, fallback);

            // Never break the outline — a direction proven safe hugs the hitbox
            // rather than leaving a hole, so the shape still reads as one thing.
            var r = Math.Clamp(reach, 0f, MaxRadius) + shape.HitboxRadius;

            var point = new Vector3(
                shape.Position.X + r * MathF.Sin(worldAngle),
                shape.Position.Y,
                shape.Position.Z + r * MathF.Cos(worldAngle));

            if (!Plugin.GameGui.WorldToScreen(point, out var screen))
            {
                previous = null;
                continue;
            }

            if (previous is { } prev)
                drawList.AddLine(prev, screen, colour, thickness);

            previous = screen;
        }
    }

    /// <summary>
    /// Projects a run of world points and joins them up. A point behind the
    /// camera breaks the run rather than joining across the screen, which is
    /// the same rule the flat drawing uses.
    /// </summary>
    private static void DrawGroundPolyline(
        ImDrawListPtr drawList, Vector3[] points, uint colour, float thickness)
    {
        Vector2? previous = null;

        foreach (var point in points)
        {
            if (!Plugin.GameGui.WorldToScreen(point, out var screen))
            {
                previous = null;
                continue;
            }

            if (previous is { } prev)
                drawList.AddLine(prev, screen, colour, thickness);

            previous = screen;
        }
    }

    /// <summary>Wraps an angle to -pi..pi.</summary>
    private static float WrapPi(float radians)
    {
        var wrapped = radians % MathF.Tau;
        if (wrapped > MathF.PI)  wrapped -= MathF.Tau;
        if (wrapped < -MathF.PI) wrapped += MathF.Tau;
        return wrapped;
    }

    private static void DrawGroundCircle(
        ImDrawListPtr drawList, Vector3 centre, float radius, uint colour, float thickness)
    {
        Vector2? previous = null;

        for (var i = 0; i <= CircleSegments; i++)
        {
            var angle = i / (float)CircleSegments * MathF.Tau;
            var point = new Vector3(
                centre.X + radius * MathF.Sin(angle),
                centre.Y,
                centre.Z + radius * MathF.Cos(angle));

            if (!Plugin.GameGui.WorldToScreen(point, out var screen))
            {
                previous = null;
                continue;
            }

            if (previous is { } prev)
                drawList.AddLine(prev, screen, colour, thickness);

            previous = screen;
        }
    }

    private static void DrawGroundCone(
        ImDrawListPtr drawList,
        Vector3       centre,
        float         radius,
        float         facing,
        float         coneDegrees,
        uint          colour,
        float         thickness)
    {
        var half     = coneDegrees * 0.5f * MathF.PI / 180f;
        var segments = Math.Max(8, (int)(CircleSegments * (coneDegrees / 360f)));

        Vector2? previous = null;
        Vector2? arcStart = null;
        Vector2? arcEnd   = null;
        var      centreOnScreen = Plugin.GameGui.WorldToScreen(centre, out var centreScreen);

        for (var i = 0; i <= segments; i++)
        {
            var angle = facing - half + i / (float)segments * (half * 2f);
            var point = new Vector3(
                centre.X + radius * MathF.Sin(angle),
                centre.Y,
                centre.Z + radius * MathF.Cos(angle));

            if (!Plugin.GameGui.WorldToScreen(point, out var screen))
            {
                previous = null;
                continue;
            }

            arcStart ??= screen;
            arcEnd     = screen;

            if (previous is { } prev)
                drawList.AddLine(prev, screen, colour, thickness);

            previous = screen;
        }

        if (!centreOnScreen)
            return;

        if (arcStart is { } start)
            drawList.AddLine(centreScreen, start, colour, thickness);

        if (arcEnd is { } end)
            drawList.AddLine(centreScreen, end, colour, thickness);
    }

    // ── Panel ────────────────────────────────────────────────────────────────

    public void Draw()
    {
        UiHelpers.SectionHeader("Enemy Vision");
        UiHelpers.Muted(
            "Draws what each nearby enemy can detect: a wedge in front of enemies that only " +
            "see forwards, a full circle around ones that detect in all directions. " +
            "Occult Crescent only.");

        ImGui.Spacing();

        var sight = _config.ShowSightEnemyVision;
        if (ImGui.Checkbox("Show forward-facing enemies (cone)", ref sight))
        {
            _config.ShowSightEnemyVision = sight;
            Plugin.SaveConfiguration();
        }

        var sound = _config.ShowSoundEnemyVision;
        if (ImGui.Checkbox("Show all-direction enemies (circle)", ref sound))
        {
            _config.ShowSoundEnemyVision = sound;
            Plugin.SaveConfiguration();
        }

        var highlight = _config.HighlightEnemyVisionWhenInside;
        if (ImGui.Checkbox("Turn red when you are inside", ref highlight))
        {
            _config.HighlightEnemyVisionWhenInside = highlight;
            Plugin.SaveConfiguration();
        }

        ImGui.Spacing();
        UiHelpers.Muted("Draw the shapes:");

        // A two-way choice rather than a checkbox: neither is the "off" state,
        // they are two ways of drawing the same shape, and a tickbox would imply
        // one of them is the absence of the other.
        if (ImGui.RadioButton("Straight outwards", !_config.FollowGroundMesh))
        {
            _config.FollowGroundMesh = false;
            Plugin.SaveConfiguration();
        }
        UiHelpers.HelpMarker(
            "Flat, at the enemy's own height. Clean and cheap, and on level ground it is exactly "
            + "right. On a slope the ring hovers over the downhill side and sinks into the uphill "
            + "one.");

        ImGui.SameLine();

        if (ImGui.RadioButton("On the floor", _config.FollowGroundMesh))
        {
            _config.FollowGroundMesh = true;
            Plugin.SaveConfiguration();
        }
        UiHelpers.HelpMarker(
            "Follows the terrain, so the shape sits on whatever is underneath it. Sampled from the "
            + "game's own collision, cached per enemy and re-sampled only when one moves, so "
            + "standing still costs nothing. Falls back to flat for an enemy not sampled yet, and "
            + "over a hole or unloaded ground.");

        // Mob silhouettes live in Settings alongside the other world overlays.
        // One switch, one home — a second copy here would be a second place to
        // look when it is not doing what was expected.
        DrawSilhouetteStatus();

        var ignoreOutleveled = _config.IgnoreOutleveledEnemies;
        if (ImGui.Checkbox("Ignore enemies you outlevel", ref ignoreOutleveled))
        {
            _config.IgnoreOutleveledEnemies = ignoreOutleveled;
            Plugin.SaveConfiguration();
        }
        UiHelpers.HelpMarker(
            "In the Occult Crescent an enemy stops aggroing once you are just ONE Knowledge level " +
            "above it — far tighter than the overworld's eleven. Those enemies are hidden and, " +
            "importantly, excluded from training: an enemy that cannot aggro would otherwise fill " +
            "its profile with \"it did not notice me\" evidence that reflects level suppression, " +
            "not its real detection range.");

        if (_config.IgnoreOutleveledEnemies)
        {
            ImGui.SetNextItemWidth(120f);
            var margin = _config.OutlevelMargin;
            if (ImGui.InputInt("Levels above to ignore", ref margin))
            {
                _config.OutlevelMargin = Math.Clamp(margin, 1, ForayLevel.MaxKnowledgeLevel);
                Plugin.SaveConfiguration();
            }
            UiHelpers.HelpMarker("1 matches the game's own rule. Raise it to keep borderline enemies visible.");
        }

        var autoIgnore = _config.AutoIgnoreNonMatchingNames;
        if (ImGui.Checkbox("Only track mobs whose name contains a word", ref autoIgnore))
        {
            _config.AutoIgnoreNonMatchingNames = autoIgnore;
            Plugin.SaveConfiguration();
        }
        UiHelpers.HelpMarker(
            "Every Occult Crescent field mob carries \"Crescent\" somewhere in its name. Anything " +
            "else is a summon, an add, or scenery — skipped for both drawing and training. " +
            "It matches anywhere in the name, not just the start, so \"Bird of the Crescent\" counts. " +
            "This is a live rule, not a stored list, so turning it off brings everything straight back.");

        if (_config.AutoIgnoreNonMatchingNames)
        {
            ImGui.SetNextItemWidth(160f);
            var prefix = _config.TrackedNamePrefix ?? string.Empty;
            if (ImGui.InputText("Name must contain", ref prefix, 32))
            {
                _config.TrackedNamePrefix = prefix;
                Plugin.SaveConfiguration();
            }
        }

#if !PUBLIC_BUILD
        // Everything below is measurement apparatus. The estimate sliders exist
        // only to stand in for mobs with no data, and Live shows no such mob.
        //
        // Guarded twice on purpose: the runtime test makes Live Mode preview it
        // correctly in the dev build, the #if keeps it out of the public binary
        // altogether. Neither alone is enough.
        if (!BuildFlavor.IsLive)
        {
            var useLearned = _config.UseLearnedAggroRanges;
            if (ImGui.Checkbox("Use measured ranges where available", ref useLearned))
            {
                _config.UseLearnedAggroRanges = useLearned;
                Plugin.SaveConfiguration();
            }
            UiHelpers.HelpMarker(
                "Mobs with training data use their measured range and cone. " +
                "Everything else falls back to the sliders below.");

            ImGui.Spacing();
            UiHelpers.SectionHeader("Fallback Estimates");
            UiHelpers.Muted("Used for any mob with no measurements yet.");

            var radius = Math.Clamp(_config.EnemyVisionRadius, MinRadius, MaxRadius);
            ImGui.SetNextItemWidth(200f);
            if (ImGui.SliderFloat("Detection range (yalms)", ref radius, MinRadius, MaxRadius, "%.1f"))
            {
                _config.EnemyVisionRadius = Math.Clamp(radius, MinRadius, MaxRadius);
                Plugin.SaveConfiguration();
            }

            var cone = Math.Clamp(_config.EnemyVisionConeDegrees, MinCone, MaxCone);
            ImGui.SetNextItemWidth(200f);
            if (ImGui.SliderFloat("Sight cone (degrees)", ref cone, MinCone, MaxCone, "%.0f"))
            {
                _config.EnemyVisionConeDegrees = Math.Clamp(cone, MinCone, MaxCone);
                Plugin.SaveConfiguration();
            }

            ImGui.Spacing();
            DrawTrainingSection();
        }
#endif

        ImGui.Spacing();
        UiHelpers.SectionHeader("Nearby Enemies");

        if (!_inOccultCrescent)
        {
            UiHelpers.Muted(
                "You are not in the Occult Crescent, so nothing is being drawn. " +
                "Head to South Horn or North Horn.");
            return;
        }

        var shapes = _shapes;
        var live   = BuildFlavor.IsLive;

        if (shapes.Count == 0)
        {
            if (live)
                UiHelpers.Muted(_store.LockedCount == 0
                    ? "No mobs have confirmed values yet, so nothing is drawn."
                    : "No enemies with confirmed values within range.");
#if !PUBLIC_BUILD
            else
                UiHelpers.Muted(_store.IgnoredCount > 0
                    ? $"No enemies within range. ({_store.IgnoredCount} mob type(s) ignored — manage them in Mob Viewer.)"
                    : "No enemies within range.");
#endif
            return;
        }

#if !PUBLIC_BUILD
        if (!live && _store.IgnoredCount > 0)
        {
            UiHelpers.Muted($"{_store.IgnoredCount} mob type(s) ignored and hidden. Manage them in Mob Viewer.");
            ImGui.Spacing();
        }
#endif

        if (_playerForayLevel is { } knowledge)
        {
            UiHelpers.Muted($"Your Knowledge level: {knowledge}"
                            + (_outleveledCount > 0
                                ? $" — {_outleveledCount} nearby enemy/enemies cannot aggro you and are hidden."
                                : "."));
            ImGui.Spacing();
        }

        if (_threateningCount > 0)
        {
            UiHelpers.ColoredWrapped(UiHelpers.Warn, $"{_threateningCount} enemy/enemies could currently detect you.");
            ImGui.Spacing();
        }

        // The legend explains the green/amber/red confidence colouring, which
        // only exists while values are still being established. Live, every row
        // is confirmed, and a legend for one state is noise.
#if !PUBLIC_BUILD
        if (!live)
            DrawLegend();
#endif

        if (ImGui.BeginTable("##limlo-vision", live ? 5 : 6,
                             ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Enemy");
            ImGui.TableSetupColumn("Detects");
            ImGui.TableSetupColumn("Range");
            ImGui.TableSetupColumn("Distance");
            if (!live)
                ImGui.TableSetupColumn("Data");
            ImGui.TableSetupColumn("");
            ImGui.TableHeadersRow();

            foreach (var shape in shapes)
            {
                ImGui.TableNextRow();

                var rowProfile = _store.Find(shape.BaseId);

                ImGui.TableNextColumn();
                UiHelpers.Colored(
                    shape.PlayerInside
                        ? DangerColor
                        : live
                            ? UiHelpers.Official
                            : UiHelpers.StateColor(shape.Confidence, rowProfile?.Locked ?? false),
                    shape.Name);

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(shape.Omnidirectional
                    ? "All directions"
                    : $"In front ({shape.ConeDegrees:F0}°)");

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(live
                    ? $"{shape.Gap:F1}y"
                    : shape.Measured ? $"{shape.Gap:F1}y measured" : $"{shape.Gap:F1}y est.");

                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{shape.Distance:F1}y");

#if !PUBLIC_BUILD
                if (!live)
                {
                    ImGui.TableNextColumn();
                    if (rowProfile?.Locked == true)
                        UiHelpers.Colored(UiHelpers.Official, "Official");
                    else
                        UiHelpers.Colored(
                            UiHelpers.ConfidenceColor(shape.Confidence),
                            DescribeConfidence(shape.Confidence, rowProfile?.Distances.Count ?? 0));
                }
#endif

                ImGui.TableNextColumn();
                if (ImGui.SmallButton((live ? "Hide" : "Ignore") + $"###limlo-ignore-{shape.BaseId}"))
                {
                    _store.SetIgnored(shape.BaseId, true);
                    Plugin.SaveConfiguration();
                }
                if (ImGui.IsItemHovered())
#if PUBLIC_BUILD
                    ImGui.SetTooltip("Stop drawing this mob type. Undo it in Mob Viewer.");
#else
                    ImGui.SetTooltip(live
                        ? "Stop drawing this mob type. Undo it in Mob Viewer."
                        : "Stop drawing and training on this mob type. Undo it in Mob Viewer.");
#endif
            }

            ImGui.EndTable();
        }
    }

    /// <summary>
    /// One line saying whether silhouettes are on, where the switch is, and —
    /// the part worth having — whether the GAME is able to draw them at all. If
    /// the client's character-outline pass is off, nothing this plugin does can
    /// produce an outline, and the user would otherwise have no way to tell that
    /// apart from a broken feature.
    /// </summary>
    private void DrawSilhouetteStatus()
    {
        if (!_config.ShowMobOutlines)
        {
            UiHelpers.Muted("Mob silhouettes are off. Switch them on in Settings, under World Overlays.");
            return;
        }

        if (!MobOutlines.GameOutlinesEnabled)
        {
            UiHelpers.ColoredWrapped(UiHelpers.Bad,
                "Mob silhouettes are on, but the game's own character-outline rendering is switched "
                + "off, so nothing can be drawn. Turn character outlines back on in the game's "
                + "graphics settings.");
            return;
        }

        UiHelpers.Muted(
            $"Mob silhouettes are ON ({_outlines.OutlinedCount} outlined right now). "
            + "Only mobs that can aggro you are outlined. Switch them off in Settings.");
    }

#if !PUBLIC_BUILD
    private void DrawTrainingSection()
    {
        UiHelpers.SectionHeader("Training Mode");

        var training = _config.AggroTrainingEnabled;
        if (ImGui.Checkbox("Collect and update mob data", ref training))
        {
            _config.AggroTrainingEnabled = training;
            if (!training)
                _trainer.Reset();
            Plugin.SaveConfiguration();
        }
        UiHelpers.HelpMarker(
            "The master switch for everything the plugin records: pulls, non-detections, and mob " +
            "locations. With it off nothing is written or changed — the plugin only draws what it " +
            "already knows. Turn it on while you are deliberately measuring, and off once a mob is " +
            "solved or its values are locked.");

        if (!_config.AggroTrainingEnabled)
        {
            UiHelpers.Muted(
                $"Off. {_store.TotalSamples} pull(s) recorded across {_store.All.Count} mob type(s) so far.");
            DrawDataResetControls();
            return;
        }

        UiHelpers.ColoredWrapped(UiHelpers.Good,
            $"Recording. {_trainer.SamplesThisSession} kept / {_trainer.RejectedThisSession} skipped this " +
            $"session, {_store.TotalSamples} total across {_store.All.Count} mob type(s).");

        UiHelpers.Muted(
            $"Saved to aggro-training.json (plus a .bak) the moment each pull lands. Last write: {_store.LastSavedAt}.");

        var announce = _config.AnnounceTrainingInChat;
        if (ImGui.Checkbox("Announce every pull in chat", ref announce))
        {
            _config.AnnounceTrainingInChat = announce;
            Plugin.SaveConfiguration();
        }
        UiHelpers.HelpMarker(
            "Prints a line whenever a pull is recorded or skipped, with the reason. " +
            "Leave this on while training so you can see it working without opening this panel.");

        UiHelpers.ColoredWrapped(UiHelpers.Accent,
            $"{_trainer.SafeObservationsThisSession} non-detection(s) recorded this session.");

        UiHelpers.Muted(
            "Two kinds of evidence are gathered. A pull proves the reach is at least that far. " +
            "Standing near a mob for a moment WITHOUT being noticed proves it is less than that " +
            "— so walking around an idle mob teaches it just as much as pulling does, and the " +
            "estimate can shrink as well as grow.");

        UiHelpers.Muted(
            "A pull is only rejected if the mob was already fighting, or if another mob you are " +
            "engaged with is within 12y of it and could have linked it in. Being in combat " +
            "yourself no longer disqualifies a pull.");

        ImGui.Spacing();
        UiHelpers.SectionHeader("Activity");

        var events = _trainer.RecentEvents;
        if (events.Count == 0)
        {
            UiHelpers.Muted("Nothing yet. Walk into a mob's detection range while out of combat.");
        }
        else
        {
            foreach (var entry in events)
            {
                // Activity lines are full sentences and easily the longest text
                // in the panel — these must wrap.
                UiHelpers.ColoredWrapped(entry.Accepted ? UiHelpers.Good : UiHelpers.Dim,
                    (entry.Accepted ? "+ " : "- ") + entry.Message);
            }
        }

        DrawDataResetControls();
    }

    private void DrawDataResetControls()
    {
        ImGui.Spacing();

        if (ImGui.Button("Clear ALL training data"))
        {
            _store.Clear();
            _trainer.Reset();
            _trainer.ClearEvents();
            Plugin.SaveConfiguration();
        }
        UiHelpers.HelpMarker(
            "Wipes every measured range and cone for every mob and starts over. " +
            "Use this if the data looks wrong — measurements persist across restarts, " +
            "so bad samples stay until they are cleared.");

        if (_store.IgnoredCount == 0)
            return;

        ImGui.SameLine();
        if (ImGui.Button($"Un-ignore all ({_store.IgnoredCount})"))
        {
            _store.ClearIgnored();
            Plugin.SaveConfiguration();
        }
        UiHelpers.HelpMarker(
            "Ignored mobs are never trained on. If a pull seems to go unrecorded, an " +
            "over-eager ignore list is the first thing to check — pulls from ignored mobs " +
            "now say so explicitly instead of passing in silence.");
    }

#endif

#if !PUBLIC_BUILD
    private static void DrawLegend()
    {
        UiHelpers.Colored(UiHelpers.Official, "Purple");
        ImGui.SameLine();
        ImGui.TextUnformatted("official");
        ImGui.SameLine();
        UiHelpers.Colored(UiHelpers.Good, "  Green");
        ImGui.SameLine();
        ImGui.TextUnformatted("solved");
        ImGui.SameLine();
        UiHelpers.Colored(UiHelpers.Warn, "  Yellow");
        ImGui.SameLine();
        ImGui.TextUnformatted("learning");
        ImGui.SameLine();
        UiHelpers.Colored(UiHelpers.Bad, "  Red");
        ImGui.SameLine();
        ImGui.TextUnformatted("none");
        ImGui.Spacing();
    }

    internal static string DescribeConfidence(AggroConfidence confidence, int samples) => confidence switch
    {
        AggroConfidence.Confident => $"Solved ({samples} pulls)",
        AggroConfidence.Learning  => $"Learning ({samples} pull{(samples == 1 ? "" : "s")})",
        _                         => "No data",
    };

    /// <summary>Confidence plus what the mob still needs, for the viewer.</summary>
    internal static string DescribeProgress(AggroLearningStore store, AggroProfile profile)
    {
        var confidence = store.ConfidenceOf(profile);
        return confidence == AggroConfidence.None
            ? "No data yet"
            : $"{DescribeConfidence(confidence, profile.Distances.Count)} — {AggroLearningStore.WhatIsMissing(profile)}";
    }
#endif
}
