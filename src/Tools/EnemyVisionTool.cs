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
    private const float ShapeThickness = 2.5f;

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
        /// <summary>Measured envelope, when this mob has one. Overrides the shape.</summary>
        public AggroProfile?   Envelope        { get; init; }
        public float           HitboxRadius    { get; init; }
        public float           ConeDegrees     { get; } = coneDegrees;
        public bool            Omnidirectional { get; } = omnidirectional;
        public bool            PlayerInside    { get; } = playerInside;
        public string          Name            { get; } = name;
        public uint            BaseId          { get; } = baseId;
        public float           Distance        { get; } = distance;
        public AggroConfidence Confidence      { get; } = confidence;
        public bool            Measured        { get; } = measured;
    }

    private readonly Configuration      _config;
    private readonly AggroLearningStore _store;
    private readonly AggroTrainer       _trainer;

    private ExcelSheet<BNpcBase>? _bnpcSheet;

    private List<Shape> _shapes = new();
    private bool        _inOccultCrescent;
    private int         _threateningCount;
    private bool        _trainingWasEnabled;

    public EnemyVisionTool(Configuration config, AggroLearningStore store, AggroTrainer trainer)
    {
        _config  = config;
        _store   = store;
        _trainer = trainer;
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

            // Training keeps per-enemy history buffers. Drop them the moment it
            // is switched off so nothing lingers.
            if (_trainingWasEnabled && !_config.AggroTrainingEnabled)
                _trainer.Reset();
            _trainingWasEnabled = _config.AggroTrainingEnabled;

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

            var tracked = new List<TrackedEnemy>();

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

                // Marked irrelevant: no shape, no training sample, no row.
                if (_store.IsIgnored(obj.BaseId))
                    continue;

                var omnidirectional = _bnpcSheet?.GetRowOrDefault(obj.BaseId)?.IsOmnidirectional ?? false;
                var name            = obj.Name.ToString();

                tracked.Add(new TrackedEnemy(
                    obj,
                    obj.BaseId,
                    name,
                    omnidirectional,
                    battleNpc.StatusFlags.HasFlag(StatusFlags.InCombat),
                    battleNpc.Level,
                    battleNpc.MaxHp,
                    obj.HitboxRadius,
                    distance));

                // Measured numbers win over the slider when we have them.
                var profile    = _store.Find(obj.BaseId);
                var confidence = _store.ConfidenceOf(profile);

                var learnedDistance = _config.UseLearnedAggroRanges ? _store.EstimatedDistance(profile) : null;
                var learnedCone     = _config.UseLearnedAggroRanges ? _store.EstimatedConeDegrees(profile) : null;

                // Defensive clamp: a bad measurement must never be able to paint
                // a giant circle across the world.
                var gap  = Math.Clamp(learnedDistance ?? fallbackRadius, 0f, MaxRadius);
                var cone = Math.Clamp(learnedCone     ?? fallbackCone, MinCone, MaxCone);

                // When the mob has a measured envelope, that replaces the
                // cone-or-circle guess entirely.
                var envelope = profile != null && _config.UseLearnedAggroRanges
                               && AggroLearningStore.FilledBins(profile) > 0
                    ? profile
                    : null;

                // Measurements can contradict the sheet: a "sees only forwards"
                // mob that pulled from behind is really omnidirectional.
                var effectiveOmni = omnidirectional || AggroLearningStore.ContradictsSheet(profile);

                var playerAngle = AggroLearningStore.AngleOffFacing(obj.Position, obj.Rotation, playerPos);

                bool  inside;
                float radius;

                if (envelope != null)
                {
                    // Measured: the reach at the angle the player actually
                    // stands at, no assumed shape involved.
                    var measured = Math.Clamp(
                        AggroLearningStore.RadiusAtAngle(envelope, playerAngle) ?? gap, 0f, MaxRadius);

                    radius = measured + obj.HitboxRadius;
                    inside = distance - playerHitbox - obj.HitboxRadius <= measured;
                }
                else
                {
                    radius = gap + obj.HitboxRadius;
                    inside = distance - playerHitbox <= radius;

                    if (inside && !effectiveOmni && cone < 360f)
                        inside = playerAngle <= cone * 0.5f;
                }

                if (inside)
                    threatening++;

                shapes.Add(new Shape(
                    obj.Position,
                    obj.Rotation,
                    radius,
                    effectiveOmni ? 360f : cone,
                    effectiveOmni,
                    inside,
                    name,
                    obj.BaseId,
                    distance,
                    confidence,
                    learnedDistance.HasValue) { Gap = gap, Envelope = envelope, HitboxRadius = obj.HitboxRadius });
            }

            shapes.Sort((a, b) => a.Distance.CompareTo(b.Distance));

            if (_config.AggroTrainingEnabled)
                _trainer.Tick(player, tracked, territory);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "EnemyVisionTool failed during its framework tick.");
        }

        _shapes           = shapes;
        _threateningCount = threatening;
    }

    // ── Overlay ──────────────────────────────────────────────────────────────

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

            if (shape.Envelope is { } envelope)
                DrawEnvelope(drawList, shape, envelope, colour);
            else if (shape.Omnidirectional || shape.ConeDegrees >= 360f)
                DrawGroundCircle(drawList, shape.Position, shape.Radius, colour);
            else
                DrawGroundCone(drawList, shape.Position, shape.Radius, shape.Facing, shape.ConeDegrees, colour);
        }
    }

    /// <summary>
    /// Draws the measured detection envelope: a closed loop whose reach varies
    /// with angle, so whatever shape the data describes is what appears — a
    /// forward lobe, an even ring, or a lobe sitting on top of a close core.
    /// No cone-or-circle assumption anywhere.
    /// </summary>
    private static void DrawEnvelope(ImDrawListPtr drawList, Shape shape, AggroProfile envelope, uint colour)
    {
        Vector2? previous = null;
        Vector2? first    = null;

        for (var i = 0; i <= CircleSegments; i++)
        {
            var sweep      = i / (float)CircleSegments * MathF.Tau;
            var worldAngle = shape.Facing + sweep;

            // Fold to 0-180 off the facing; the envelope is left/right symmetric.
            var offFacing = MathF.Abs(WrapPi(sweep)) * 180f / MathF.PI;

            var reach = AggroLearningStore.RadiusAtAngle(envelope, offFacing) ?? shape.Gap;
            var r     = Math.Clamp(reach, 0f, MaxRadius) + shape.HitboxRadius;

            var point = new Vector3(
                shape.Position.X + r * MathF.Sin(worldAngle),
                shape.Position.Y,
                shape.Position.Z + r * MathF.Cos(worldAngle));

            if (!Plugin.GameGui.WorldToScreen(point, out var screen))
            {
                previous = null;
                continue;
            }

            first ??= screen;

            if (previous is { } prev)
                drawList.AddLine(prev, screen, colour, ShapeThickness);

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

    private static void DrawGroundCircle(ImDrawListPtr drawList, Vector3 centre, float radius, uint colour)
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
                drawList.AddLine(prev, screen, colour, ShapeThickness);

            previous = screen;
        }
    }

    private static void DrawGroundCone(
        ImDrawListPtr drawList,
        Vector3       centre,
        float         radius,
        float         facing,
        float         coneDegrees,
        uint          colour)
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
                drawList.AddLine(prev, screen, colour, ShapeThickness);

            previous = screen;
        }

        if (!centreOnScreen)
            return;

        if (arcStart is { } start)
            drawList.AddLine(centreScreen, start, colour, ShapeThickness);

        if (arcEnd is { } end)
            drawList.AddLine(centreScreen, end, colour, ShapeThickness);
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
        if (shapes.Count == 0)
        {
            UiHelpers.Muted(_store.IgnoredCount > 0
                ? $"No enemies within range. ({_store.IgnoredCount} mob type(s) ignored — manage them in Mob Viewer.)"
                : "No enemies within range.");
            return;
        }

        if (_store.IgnoredCount > 0)
        {
            UiHelpers.Muted($"{_store.IgnoredCount} mob type(s) ignored and hidden. Manage them in Mob Viewer.");
            ImGui.Spacing();
        }

        if (_threateningCount > 0)
        {
            UiHelpers.Colored(UiHelpers.Warn, $"{_threateningCount} enemy/enemies could currently detect you.");
            ImGui.Spacing();
        }

        DrawLegend();

        if (ImGui.BeginTable("##limlo-vision", 6, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Enemy");
            ImGui.TableSetupColumn("Detects");
            ImGui.TableSetupColumn("Range");
            ImGui.TableSetupColumn("Distance");
            ImGui.TableSetupColumn("Data");
            ImGui.TableSetupColumn("");
            ImGui.TableHeadersRow();

            foreach (var shape in shapes)
            {
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                UiHelpers.Colored(
                    shape.PlayerInside ? DangerColor : UiHelpers.ConfidenceColor(shape.Confidence),
                    shape.Name);

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(shape.Omnidirectional
                    ? "All directions"
                    : $"In front ({shape.ConeDegrees:F0}°)");

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(shape.Measured ? $"{shape.Gap:F1}y measured" : $"{shape.Gap:F1}y est.");

                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{shape.Distance:F1}y");

                ImGui.TableNextColumn();
                var profile = _store.Find(shape.BaseId);
                UiHelpers.Colored(
                    UiHelpers.ConfidenceColor(shape.Confidence),
                    DescribeConfidence(shape.Confidence, profile?.Distances.Count ?? 0));

                ImGui.TableNextColumn();
                if (ImGui.SmallButton($"Ignore###limlo-ignore-{shape.BaseId}"))
                {
                    _store.SetIgnored(shape.BaseId, true);
                    Plugin.SaveConfiguration();
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Stop drawing and training on this mob type. Undo it in Mob Viewer.");
            }

            ImGui.EndTable();
        }
    }

    private void DrawTrainingSection()
    {
        UiHelpers.SectionHeader("Training Mode");

        var training = _config.AggroTrainingEnabled;
        if (ImGui.Checkbox("Learn real ranges by watching pulls", ref training))
        {
            _config.AggroTrainingEnabled = training;
            if (!training)
                _trainer.Reset();
            Plugin.SaveConfiguration();
        }
        UiHelpers.HelpMarker(
            "Watches every nearby enemy each frame. When one pulls onto you, it records how " +
            "far away you were and what angle you were at relative to its facing. Leave it " +
            "off once a mob is solved — it keeps per-enemy history buffers while running.");

        if (!_config.AggroTrainingEnabled)
        {
            UiHelpers.Muted(
                $"Off. {_store.TotalSamples} pull(s) recorded across {_store.All.Count} mob type(s) so far.");
            DrawDataResetControls();
            return;
        }

        UiHelpers.Colored(UiHelpers.Good,
            $"Recording. {_trainer.SamplesThisSession} kept / {_trainer.RejectedThisSession} skipped this " +
            $"session, {_store.TotalSamples} total across {_store.All.Count} mob type(s).");

        var announce = _config.AnnounceTrainingInChat;
        if (ImGui.Checkbox("Announce every pull in chat", ref announce))
        {
            _config.AnnounceTrainingInChat = announce;
            Plugin.SaveConfiguration();
        }
        UiHelpers.HelpMarker(
            "Prints a line whenever a pull is recorded or skipped, with the reason. " +
            "Leave this on while training so you can see it working without opening this panel.");

        UiHelpers.Muted(
            "Only fresh pulls count: you must be OUT OF COMBAT, the mob must not already be " +
            "fighting, and only the first pull per second is kept. So pull one mob at a time " +
            "and disengage between pulls — running a whole train records one sample, not ten.");

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
                UiHelpers.Colored(entry.Accepted ? UiHelpers.Good : UiHelpers.Dim,
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
    }

    private static void DrawLegend()
    {
        UiHelpers.Colored(UiHelpers.Good, "Green");
        ImGui.SameLine();
        ImGui.TextUnformatted("solved");
        ImGui.SameLine();
        UiHelpers.Colored(UiHelpers.Warn, "  Yellow");
        ImGui.SameLine();
        ImGui.TextUnformatted("some data");
        ImGui.SameLine();
        UiHelpers.Colored(UiHelpers.Bad, "  Red");
        ImGui.SameLine();
        ImGui.TextUnformatted("no data");
        ImGui.Spacing();
    }

    internal static string DescribeConfidence(AggroConfidence confidence, int samples) => confidence switch
    {
        AggroConfidence.Confident => $"Solved ({samples})",
        AggroConfidence.Learning  => $"Learning ({samples}/{AggroLearningStore.MinSamplesForConfident})",
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
}
