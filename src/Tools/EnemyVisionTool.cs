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
/// Draws each nearby enemy's detection shape on the ground in the Occult
/// Crescent: a cone for sight-based enemies, a full circle for sound-based
/// ones.
///
/// WHAT IS REAL AND WHAT IS ESTIMATED — read this before trusting the drawing:
///
///  - The SHAPE is real game data. <c>BNpcBase.IsOmnidirectional</c> is a per-
///    enemy flag in the game's own sheets (4216 of 20402 rows have it set).
///    Set means the enemy detects in every direction — the "sound" aggro
///    everyone knows. Clear means it only detects in front of itself.
///  - The RADIUS and CONE ANGLE are NOT real game data. Aggro range appears
///    nowhere in the game's sheets — there is no aggro/sight/detection sheet,
///    and BNpcBase carries no distance column. Every plugin that draws these
///    (RadarPlugin, Distance, NecroLens) ships hand-measured community tables
///    or hardcoded per-zone constants. Ours is a single tunable number.
///
/// So: trust the cone-versus-circle. Treat the size as a configurable guess.
/// The panel says so too — this must never look more authoritative than it is.
///
/// Geometry notes:
///  - Facing is <c>(sin r, 0, cos r)</c>, the inverse of the
///    <c>Atan2(dx, dz)</c> used by this repo's own working walk-to routine in
///    EasterEvent's Walker.
///  - Radius is measured from the hitbox edge, so the drawn radius is the
///    configured distance plus the enemy's <c>HitboxRadius</c>. FFXIV measures
///    ranges hitring-to-hitring, not centre-to-centre.
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

    /// <summary>Beyond this the shapes are clutter and cost projection work.</summary>
    private const float RenderDistance = 70f;

    private const int   CircleSegments = 48;
    private const float ShapeThickness = 2.5f;

    private static readonly Vector4 SightColor   = new(0.98f, 0.75f, 0.25f, 0.85f);
    private static readonly Vector4 SoundColor   = new(0.62f, 0.55f, 0.95f, 0.85f);
    private static readonly Vector4 DangerColor  = new(1.00f, 0.30f, 0.28f, 0.95f);

    private readonly struct Enemy(
        Vector3 position,
        float   facing,
        float   radius,
        bool    omnidirectional,
        bool    playerInside,
        string  name,
        uint    baseId,
        float   distance)
    {
        public Vector3 Position        { get; } = position;
        public float   Facing          { get; } = facing;
        /// <summary>Already includes the hitbox radius.</summary>
        public float   Radius          { get; } = radius;
        public bool    Omnidirectional { get; } = omnidirectional;
        public bool    PlayerInside    { get; } = playerInside;
        public string  Name            { get; } = name;
        public uint    BaseId          { get; } = baseId;
        public float   Distance        { get; } = distance;
    }

    private readonly Configuration _config;

    private ExcelSheet<BNpcBase>? _bnpcSheet;

    private List<Enemy> _enemies = new();
    private bool        _inOccultCrescent;
    private int         _threateningCount;

    public EnemyVisionTool(Configuration config) => _config = config;

    private static bool IsOccultCrescent(ushort territory) =>
        territory is TerritorySouthHorn or TerritoryNorthHorn;

    /// <summary>Forward vector for a FFXIV rotation, in the XZ plane.</summary>
    private static Vector3 FacingVector(float rotation) =>
        new(MathF.Sin(rotation), 0f, MathF.Cos(rotation));

    public void OnFrameworkUpdate()
    {
        var found       = new List<Enemy>();
        var threatening = 0;

        try
        {
            _inOccultCrescent = IsOccultCrescent((ushort)Plugin.ClientState.TerritoryType);
            if (!_inOccultCrescent)
            {
                _enemies = found;
                _threateningCount = 0;
                return;
            }

            var player = Plugin.ObjectTable.LocalPlayer;
            if (player == null)
            {
                _enemies = found;
                _threateningCount = 0;
                return;
            }

            var playerPos    = player.Position;
            var playerHitbox = player.HitboxRadius;

            _bnpcSheet ??= Plugin.DataManager.GetExcelSheet<BNpcBase>();

            var configuredRadius = Math.Clamp(_config.EnemyVisionRadius, MinRadius, MaxRadius);
            var halfConeRadians  = Math.Clamp(_config.EnemyVisionConeDegrees, MinCone, MaxCone)
                                   * 0.5f * MathF.PI / 180f;

            foreach (var obj in Plugin.ObjectTable)
            {
                if (obj.ObjectKind != ObjectKind.BattleNpc)
                    continue;

                if (!obj.IsValid() || obj.IsDead)
                    continue;

                // Combatant (5) is the sub-kind ordinary field mobs use. This
                // filters out pets, buddies, race chocobos, minions, party
                // members, and BNpc body-parts, which all share ObjectKind
                // BattleNpc. (The enum has no "Enemy" member — Combatant is it.)
                if (obj is not IBattleNpc battleNpc || battleNpc.BattleNpcKind != BattleNpcSubKind.Combatant)
                    continue;

                var distance = Vector3.Distance(playerPos, obj.Position);
                if (distance > RenderDistance)
                    continue;

                var omnidirectional = _bnpcSheet?.GetRowOrDefault(obj.BaseId)?.IsOmnidirectional ?? false;

                // FFXIV measures range hitring to hitring, so the shape starts
                // at the enemy's hitbox edge rather than its centre.
                var radius = configuredRadius + obj.HitboxRadius;

                // The player is "inside" when their own hitring crosses the
                // shape — and, for a sight enemy, when they are also within the
                // cone's arc.
                var inside = distance - playerHitbox <= radius;
                if (inside && !omnidirectional)
                {
                    var toPlayer = playerPos - obj.Position;
                    toPlayer.Y = 0f;

                    if (toPlayer.LengthSquared() > 0.0001f)
                    {
                        var forward = FacingVector(obj.Rotation);
                        var cos     = Vector3.Dot(Vector3.Normalize(toPlayer), forward);
                        inside = MathF.Acos(Math.Clamp(cos, -1f, 1f)) <= halfConeRadians;
                    }
                }

                if (inside)
                    threatening++;

                found.Add(new Enemy(
                    obj.Position,
                    obj.Rotation,
                    radius,
                    omnidirectional,
                    inside,
                    obj.Name.ToString(),
                    obj.BaseId,
                    distance));
            }

            found.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "EnemyVisionTool failed to scan for enemies.");
        }

        _enemies          = found;
        _threateningCount = threatening;
    }

    // ── Overlay ──────────────────────────────────────────────────────────────

    public void DrawOverlay()
    {
        if (!_inOccultCrescent)
            return;

        if (!_config.ShowSightEnemyVision && !_config.ShowSoundEnemyVision)
            return;

        var enemies = _enemies;
        if (enemies.Count == 0)
            return;

        var drawList   = ImGui.GetForegroundDrawList();
        var coneDegs   = Math.Clamp(_config.EnemyVisionConeDegrees, MinCone, MaxCone);
        var highlight  = _config.HighlightEnemyVisionWhenInside;

        foreach (var enemy in enemies)
        {
            if (enemy.Omnidirectional && !_config.ShowSoundEnemyVision)
                continue;
            if (!enemy.Omnidirectional && !_config.ShowSightEnemyVision)
                continue;

            var colour = ImGui.ColorConvertFloat4ToU32(
                highlight && enemy.PlayerInside
                    ? DangerColor
                    : enemy.Omnidirectional ? SoundColor : SightColor);

            // A sound enemy, or a cone opened up to a full turn, is just a circle.
            if (enemy.Omnidirectional || coneDegs >= 360f)
                DrawGroundCircle(drawList, enemy.Position, enemy.Radius, colour);
            else
                DrawGroundCone(drawList, enemy.Position, enemy.Radius, enemy.Facing, coneDegs, colour);
        }
    }

    /// <summary>
    /// Ground circle in the XZ plane. Segments whose endpoints leave the screen
    /// are dropped rather than drawn to a garbage projection.
    /// </summary>
    private static void DrawGroundCircle(ImDrawListPtr drawList, Vector3 centre, float radius, uint colour)
    {
        Vector2? previous = null;
        Vector2? first    = null;

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

            first ??= screen;

            if (previous is { } prev)
                drawList.AddLine(prev, screen, colour, ShapeThickness);

            previous = screen;
        }
    }

    /// <summary>
    /// Ground cone: the arc, plus the two edges back to the enemy, so it reads
    /// as a wedge rather than a floating curve.
    /// </summary>
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

        Vector2? previous     = null;
        Vector2? arcStart     = null;
        Vector2? arcEnd       = null;
        var      centreOnScreen = Plugin.GameGui.WorldToScreen(centre, out var centreScreen);

        for (var i = 0; i <= segments; i++)
        {
            // Sweep from one cone edge to the other, around the facing angle.
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
            "Draws what each nearby enemy can detect: a wedge in front of sight-based " +
            "enemies, a full circle around sound-based ones. Occult Crescent only.");

        ImGui.Spacing();
        ImGui.PushStyleColor(ImGuiCol.Text, UiHelpers.Warn);
        ImGui.TextWrapped(
            "The shape is real: whether an enemy sees in a cone or hears in all directions " +
            "comes straight from the game's own data, per enemy. The SIZE is not — the game " +
            "never publishes aggro range, so the distance below is an estimate you tune by eye.");
        ImGui.PopStyleColor();

        ImGui.Spacing();

        var sight = _config.ShowSightEnemyVision;
        if (ImGui.Checkbox("Show sight enemies (cone)", ref sight))
        {
            _config.ShowSightEnemyVision = sight;
            Plugin.SaveConfiguration();
        }

        var sound = _config.ShowSoundEnemyVision;
        if (ImGui.Checkbox("Show sound enemies (circle)", ref sound))
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

        var radius = Math.Clamp(_config.EnemyVisionRadius, MinRadius, MaxRadius);
        ImGui.SetNextItemWidth(200f);
        if (ImGui.SliderFloat("Detection range (yalms)", ref radius, MinRadius, MaxRadius, "%.1f"))
        {
            _config.EnemyVisionRadius = Math.Clamp(radius, MinRadius, MaxRadius);
            Plugin.SaveConfiguration();
        }
        UiHelpers.HelpMarker(
            "Measured from the enemy's hitbox edge, the way the game measures range. " +
            "Tune it until the shape matches where you actually get pulled.");

        var cone = Math.Clamp(_config.EnemyVisionConeDegrees, MinCone, MaxCone);
        ImGui.SetNextItemWidth(200f);
        if (ImGui.SliderFloat("Sight cone (degrees)", ref cone, MinCone, MaxCone, "%.0f"))
        {
            _config.EnemyVisionConeDegrees = Math.Clamp(cone, MinCone, MaxCone);
            Plugin.SaveConfiguration();
        }
        UiHelpers.HelpMarker("90 degrees is the figure the community uses for Deep Dungeon sight mobs.");

        ImGui.Spacing();
        UiHelpers.SectionHeader("Nearby Enemies");

        if (!_inOccultCrescent)
        {
            UiHelpers.Muted(
                "You are not in the Occult Crescent, so nothing is being drawn. " +
                "Head to South Horn or North Horn.");
            return;
        }

        var enemies = _enemies;
        if (enemies.Count == 0)
        {
            UiHelpers.Muted("No enemies within range.");
            return;
        }

        if (_threateningCount > 0)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, UiHelpers.Warn);
            ImGui.TextWrapped($"{_threateningCount} enemy/enemies could currently detect you.");
            ImGui.PopStyleColor();
            ImGui.Spacing();
        }

        // This table doubles as the verification surface: it shows the aggro
        // type read straight from the sheet, per enemy, so a wrong reading is
        // visible rather than silently drawn.
        if (ImGui.BeginTable("##limlo-vision", 4, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Enemy");
            ImGui.TableSetupColumn("Detects");
            ImGui.TableSetupColumn("Distance");
            ImGui.TableSetupColumn("BNpcBase");
            ImGui.TableHeadersRow();

            foreach (var enemy in enemies)
            {
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                if (enemy.PlayerInside)
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, DangerColor);
                    ImGui.TextUnformatted(enemy.Name);
                    ImGui.PopStyleColor();
                }
                else
                {
                    ImGui.TextUnformatted(enemy.Name);
                }

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(enemy.Omnidirectional ? "All directions" : "In front");

                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{enemy.Distance:F1}y");

                ImGui.TableNextColumn();
                ImGui.PushStyleColor(ImGuiCol.Text, UiHelpers.Dim);
                ImGui.TextUnformatted(enemy.BaseId.ToString());
                ImGui.PopStyleColor();
            }

            ImGui.EndTable();
        }
    }
}
