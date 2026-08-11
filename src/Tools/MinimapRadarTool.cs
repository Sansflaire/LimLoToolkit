using System;
using System.Collections.Generic;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;

using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI;

using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace LimLoToolkit.Tools;

/// <summary>
/// Puts a dot on the game's own minimap for every nearby mob, so "something can
/// aggro me from over there" is answerable without opening a panel.
///
/// **Nothing is written to the game's UI.** This reads the minimap's transform
/// and draws its own dots on top with ImGui. An earlier version of the Mob
/// Viewer added real markers through <c>AgentMap</c> and they came out as large
/// red flags covering the map; that was removed at the user's request and the
/// lesson stuck. Drawing over the addon means the dots can be any size we like,
/// nothing has to be cleaned up, and a crash or reload cannot leave marks behind.
///
/// **How a world position becomes a screen position.** Every number below is
/// read from the game rather than guessed — see docs/minimap-radar.md.
///
/// <code>
/// addon  = GameGui.GetAddonByName("_NaviMap")   -> AddonNaviMap*
/// map    = addon->NaviMap                       -> Atk2DNaviMap
///
/// centre = map.PlayerPin node's ScreenX/Y       // the player dot, i.e. the middle
/// scale  = map.MarkerPositionScaling            // yalms -> minimap pixels
/// north  = map.NorthLockedUp                    // is the map fixed north-up
/// </code>
///
/// With the map locked north, world +X is screen right and world +Z is screen
/// down, so the offset is simply <c>(dx, dz) * scale</c>. With it unlocked the
/// map turns so the camera's direction points up, which is a rotation of
/// <c>DirH - pi</c> applied to that same offset (derivation in the doc).
///
/// **The rotation sign is the one thing that could not be verified without the
/// game running**, because it depends on the convention of
/// <c>Camera.DirH</c>. Hence the calibration controls: if the dots orbit the
/// wrong way, one toggle fixes it. The centre and the scale come straight from
/// the game's own marker fields and are not in doubt.
/// </summary>
public sealed class MinimapRadarTool : ITool
{
    public string Id          => "minimap-radar";
    public string Name        => "Minimap Radar";
    public string Description => "Dots on the game's minimap for nearby mobs, with the ones that can see you in red.";
    public string Category    => "Toolkit";

    private const ushort TerritorySouthHorn = 1252;
    private const ushort TerritoryNorthHorn = 1346;

    /// <summary>Mobs further out than this are never considered, minimap or not.</summary>
    private const float ScanDistance = 90f;

    public const float MinDotSize = 2f;
    public const float MaxDotSize = 10f;

    private static readonly Vector4 SeenByColor   = new(1.00f, 0.30f, 0.28f, 0.95f);
    private static readonly Vector4 KnownColor    = new(0.98f, 0.75f, 0.25f, 0.90f);
    private static readonly Vector4 UnknownColor  = new(0.72f, 0.74f, 0.78f, 0.80f);
    private static readonly Vector4 HarmlessColor = new(0.45f, 0.48f, 0.52f, 0.65f);

    /// <summary>One mob, already reduced to where its dot goes on screen.</summary>
    private readonly struct Blip(Vector2 screen, Vector4 colour, bool onRim, string name, float distance)
    {
        public Vector2 Screen   { get; } = screen;
        public Vector4 Colour   { get; } = colour;
        /// <summary>Clamped to the minimap edge because the mob is off it.</summary>
        public bool    OnRim    { get; } = onRim;
        public string  Name     { get; } = name;
        public float   Distance { get; } = distance;
    }

    /// <summary>
    /// The live minimap numbers, kept so the panel can show what was actually
    /// read. When the derivation is wrong, guessing from a screenshot is much
    /// harder than reading the inputs.
    /// </summary>
    private readonly struct MinimapState(
        bool    found,
        Vector2 centre,
        float   radius,
        float   scale,
        bool    northLocked,
        float   coneRotation,
        float   cameraDirH,
        float   appliedRotation)
    {
        public bool    Found           { get; } = found;
        public Vector2 Centre          { get; } = centre;
        public float   Radius          { get; } = radius;
        public float   Scale           { get; } = scale;
        public bool    NorthLocked     { get; } = northLocked;
        public float   ConeRotation    { get; } = coneRotation;
        public float   CameraDirH      { get; } = cameraDirH;
        public float   AppliedRotation { get; } = appliedRotation;
    }

    private readonly Configuration      _config;
    private readonly AggroLearningStore _store;

    private ExcelSheet<BNpcBase>? _bnpcSheet;

    private List<Blip>   _blips = new();
    private MinimapState _state;
    private bool         _inOccultCrescent;
    private int          _seenByCount;

    /// <summary>
    /// True while the Orientation panel is expanded. Drives the guide rings the
    /// overlay draws, so the frame being used is visible instead of inferred.
    /// </summary>
    private bool _showCalibrationOverlay;

    public MinimapRadarTool(Configuration config, AggroLearningStore store)
    {
        _config = config;
        _store  = store;
    }

    private static bool IsOccultCrescent(ushort territory) =>
        territory is TerritorySouthHorn or TerritoryNorthHorn;

    // ── Tick ─────────────────────────────────────────────────────────────────

    public unsafe void OnFrameworkUpdate()
    {
        var blips  = new List<Blip>();
        var seenBy = 0;

        // Cleared every tick and set again by Draw when the panel is open, so
        // the guide rings vanish the moment another tool is selected rather
        // than being left on by a panel that is no longer being drawn.
        _showCalibrationOverlay = false;

        try
        {
            _inOccultCrescent = IsOccultCrescent((ushort)Plugin.ClientState.TerritoryType);

            if (!_config.ShowMinimapRadar || !_inOccultCrescent)
            {
                _blips = blips;
                _state = default;
                return;
            }

            var player = Plugin.ObjectTable.LocalPlayer;
            if (player == null)
            {
                _blips = blips;
                _state = default;
                return;
            }

            _state = ReadMinimap();
            if (!_state.Found)
            {
                _blips = blips;
                return;
            }

            _bnpcSheet ??= Plugin.DataManager.GetExcelSheet<BNpcBase>();

            var playerPos    = player.Position;
            var playerHitbox = player.HitboxRadius;
            var playerForay  = ForayLevel.TryGet(player);

            var sin = MathF.Sin(_state.AppliedRotation);
            var cos = MathF.Cos(_state.AppliedRotation);

            foreach (var obj in Plugin.ObjectTable)
            {
                if (obj.ObjectKind != ObjectKind.BattleNpc || !obj.IsValid() || obj.IsDead)
                    continue;

                if (obj is not IBattleNpc battleNpc || battleNpc.BattleNpcKind != BattleNpcSubKind.Combatant)
                    continue;

                var distance = Vector3.Distance(playerPos, obj.Position);
                if (distance > ScanDistance)
                    continue;

                var name = obj.Name.ToString();
                if (_store.ShouldSkip(obj.BaseId, name))
                    continue;

                var harmless = ForayLevel.IsHarmless(playerForay, ForayLevel.TryGet(obj), _config.OutlevelMargin);

                if (harmless && !_config.MinimapShowHarmlessMobs)
                    continue;

                // World offset -> minimap pixels, then rotated into the map's
                // frame. Screen Y grows downwards and so does world +Z on a
                // north-up map, which is why neither term is negated here.
                var dx = (obj.Position.X - playerPos.X) * _state.Scale;
                var dz = (obj.Position.Z - playerPos.Z) * _state.Scale;

                if (_config.MinimapMirror)
                    dx = -dx;

                var px = dx * cos - dz * sin;
                var py = dx * sin + dz * cos;

                var offset = new Vector2(px, py);
                var length = offset.Length();
                var onRim  = length > _state.Radius;

                if (onRim)
                {
                    if (!_config.MinimapShowOffEdge)
                        continue;

                    // Pin it to the edge rather than dropping it: "there is one
                    // out that way" is the answer a detector exists to give.
                    offset = length > 0.001f ? offset / length * _state.Radius : Vector2.Zero;
                }

                var colour = ColourFor(obj, battleNpc, playerPos, playerHitbox, distance, harmless, ref seenBy);

                blips.Add(new Blip(_state.Centre + offset, colour, onRim, name, distance));
            }

            // Nearest last so the closest dot is drawn on top of the rest.
            blips.Sort((a, b) => b.Distance.CompareTo(a.Distance));
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "MinimapRadarTool failed during its framework tick.");
        }

        _blips       = blips;
        _seenByCount = seenBy;
    }

    /// <summary>
    /// Red when this mob can currently detect the player, which is only claimed
    /// where there is a confirmed shape to claim it from. Amber for a known mob
    /// with no settled range, grey for one never measured, dimmer still for one
    /// that cannot aggro at all.
    /// </summary>
    private Vector4 ColourFor(
        IGameObject obj,
        IBattleNpc  battleNpc,
        Vector3     playerPos,
        float       playerHitbox,
        float       distance,
        bool        harmless,
        ref int     seenBy)
    {
        if (harmless)
            return HarmlessColor;

        var profile = _store.Find(obj.BaseId);
        if (profile == null)
            return UnknownColor;

        var model = AggroLearningStore.Classify(profile);
        if (model.Type == DetectionType.Unknown)
            return KnownColor;

        var angle = AggroLearningStore.AngleOffFacing(obj.Position, obj.Rotation, playerPos);
        var reach = AggroLearningStore.ReachForDrawing(profile, model, angle, model.Range);
        var gap   = distance - playerHitbox - obj.HitboxRadius;

        if (gap > reach)
            return KnownColor;

        seenBy++;
        return SeenByColor;
    }

    /// <summary>
    /// Reads the minimap's own transform. Every value comes from
    /// <c>Atk2DNaviMap</c> or the player-pin node — nothing here is a constant
    /// somebody measured off a screenshot.
    /// </summary>
    private unsafe MinimapState ReadMinimap()
    {
        // Dalamud hands back a wrapper rather than a raw pointer; it carries the
        // null and visibility checks, and the address converts implicitly for
        // the fields only the ClientStructs layout knows about.
        var handle = Plugin.GameGui.GetAddonByName("_NaviMap", 1);
        if (handle.IsNull || !handle.IsVisible)
            return default;

        var addon = (AddonNaviMap*)(nint)handle;
        if (addon == null)
            return default;

        ref var map = ref addon->NaviMap;

        var pin = map.PlayerPin;
        if (pin == null)
            return default;

        // The player pin IS the centre of the minimap, so there is no need to
        // reconstruct the addon's rectangle — the node has already been through
        // every transform the UI applies. ScreenX/Y is the node's TOP-LEFT, so
        // half its size gets us to the middle of the pin.
        //
        // If that half-size term turns out to be wrong the dots sit a few pixels
        // off, which is why the Orientation panel can draw the computed centre:
        // it is easier to look at than to argue about.
        var node    = &pin->AtkResNode;
        var uiScale = handle.Scale <= 0f ? 1f : handle.Scale;

        var centre = new Vector2(
            node->ScreenX + node->Width * 0.5f * uiScale,
            node->ScreenY + node->Height * 0.5f * uiScale);
        var radius  = (MathF.Min(map.Width, map.Height) * 0.5f - _config.MinimapEdgeInset) * uiScale;

        var dirH = 0f;
        var cameraManager = CameraManager.Instance();
        if (cameraManager != null)
        {
            var camera = cameraManager->GetActiveCamera();
            if (camera != null)
                dirH = camera->DirH;
        }

        // Locked north: the map never turns, so world offsets go straight on.
        // Otherwise the map turns so the camera direction points up, which is a
        // rotation of DirH - pi. See the class summary.
        var rotation = map.NorthLockedUp
            ? 0f
            : dirH - MathF.PI;

        rotation += _config.MinimapRotationOffsetDegrees * MathF.PI / 180f;

        return new MinimapState(
            found:           true,
            centre:          centre,
            radius:          MathF.Max(8f, radius),
            scale:           map.MarkerPositionScaling * uiScale,
            northLocked:     map.NorthLockedUp,
            coneRotation:    map.PlayerConeRotation,
            cameraDirH:      dirH,
            appliedRotation: rotation);
    }

    // ── Overlay ──────────────────────────────────────────────────────────────

    public void DrawOverlay()
    {
        if (!_config.ShowMinimapRadar)
            return;

        var draw = ImGui.GetForegroundDrawList();

        // With the Orientation panel open, show the frame the dots are being
        // placed in: a ring on the computed centre and one on the rim. If the
        // centre ring does not sit on the player pin, or the rim ring does not
        // follow the minimap's edge, the fault is visible rather than inferred.
        if (_showCalibrationOverlay && _state.Found)
        {
            var guide = ImGui.ColorConvertFloat4ToU32(new Vector4(0.35f, 0.95f, 0.85f, 0.85f));
            draw.AddCircle(_state.Centre, 6f, guide, 0, 1.5f);
            draw.AddLine(_state.Centre - new Vector2(9f, 0f), _state.Centre + new Vector2(9f, 0f), guide, 1.2f);
            draw.AddLine(_state.Centre - new Vector2(0f, 9f), _state.Centre + new Vector2(0f, 9f), guide, 1.2f);
            draw.AddCircle(_state.Centre, _state.Radius, guide, 0, 1.2f);

            // North tick: where the radar thinks north is. Compare it against
            // the minimap's own compass.
            var north = new Vector2(-MathF.Sin(_state.AppliedRotation), -MathF.Cos(_state.AppliedRotation));
            draw.AddLine(_state.Centre + north * (_state.Radius - 12f),
                         _state.Centre + north * _state.Radius,
                         ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.4f, 0.4f, 0.95f)), 2.5f);
        }

        var blips = _blips;
        if (blips.Count == 0)
            return;

        var size = Math.Clamp(_config.MinimapDotSize, MinDotSize, MaxDotSize);

        foreach (var blip in blips)
        {
            var colour = ImGui.ColorConvertFloat4ToU32(blip.Colour);

            // Edge blips are drawn smaller and hollow so "out that way,
            // somewhere" never reads as "right there".
            if (blip.OnRim)
            {
                draw.AddCircle(blip.Screen, size * 0.7f, colour, 0, 1.6f);
                continue;
            }

            // A dark rim under every dot, so a red mob over red terrain still
            // reads. The minimap is busy and un-outlined dots vanish into it.
            draw.AddCircleFilled(blip.Screen, size + 1f,
                ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.65f)));
            draw.AddCircleFilled(blip.Screen, size, colour);
        }
    }

    // ── Panel ────────────────────────────────────────────────────────────────

    public void Draw()
    {
        UiHelpers.SectionHeader("Minimap Radar");
        UiHelpers.Muted(
            "Puts a dot on the game's minimap for every nearby mob. Red means it can see you "
            + "right now, amber means it can aggro but its range is not settled, grey means it is "
            + "not measured. Occult Crescent only.");

        UiHelpers.Muted(
            "Nothing is written to the game's UI — the dots are drawn on top of the minimap, so "
            + "there is nothing to clean up and nothing that can be left behind.");

        ImGui.Spacing();

        var enabled = _config.ShowMinimapRadar;
        if (ImGui.Checkbox("Show mobs on the minimap", ref enabled))
        {
            _config.ShowMinimapRadar = enabled;
            Plugin.SaveConfiguration();
        }

        ImGui.SetNextItemWidth(200f);
        var dot = Math.Clamp(_config.MinimapDotSize, MinDotSize, MaxDotSize);
        if (ImGui.SliderFloat("Dot size", ref dot, MinDotSize, MaxDotSize, "%.1f px"))
        {
            _config.MinimapDotSize = Math.Clamp(dot, MinDotSize, MaxDotSize);
            Plugin.SaveConfiguration();
        }

        var offEdge = _config.MinimapShowOffEdge;
        if (ImGui.Checkbox("Pin mobs beyond the minimap to its edge", ref offEdge))
        {
            _config.MinimapShowOffEdge = offEdge;
            Plugin.SaveConfiguration();
        }
        UiHelpers.HelpMarker(
            "A mob further out than the minimap shows as a small hollow ring on the rim, in its "
            + "direction. It tells you something is that way without pretending to know it is "
            + "at the edge.");

        var harmless = _config.MinimapShowHarmlessMobs;
        if (ImGui.Checkbox("Include mobs that cannot aggro you", ref harmless))
        {
            _config.MinimapShowHarmlessMobs = harmless;
            Plugin.SaveConfiguration();
        }
        UiHelpers.HelpMarker("Off by default. They are drawn dimmer when shown.");

        ImGui.Spacing();
        DrawStatus();
        DrawCalibration();
    }

    private void DrawStatus()
    {
        UiHelpers.SectionHeader("Status");

        if (!_config.ShowMinimapRadar)
        {
            UiHelpers.Muted("Switched off.");
            return;
        }

        if (!_inOccultCrescent)
        {
            UiHelpers.Muted("You are not in the Occult Crescent, so nothing is drawn.");
            return;
        }

        if (!_state.Found)
        {
            UiHelpers.ColoredWrapped(UiHelpers.Warn,
                "The minimap is not on screen, so there is nothing to draw on. It is hidden in "
                + "some duties and can be switched off in the HUD layout.");
            return;
        }

        UiHelpers.Colored(UiHelpers.Good, $"Drawing {_blips.Count} mob(s) on the minimap.");

        if (_seenByCount > 0)
            UiHelpers.ColoredWrapped(SeenByColor, $"{_seenByCount} of them can see you right now.");
    }

    /// <summary>
    /// Live readout of what was read from the minimap, plus the two corrections
    /// that fix an orientation that came out wrong.
    ///
    /// This is here rather than hidden behind a debug flag on purpose. The
    /// centre and the scale are the game's own marker numbers and are certain;
    /// the ROTATION depends on the convention of <c>Camera.DirH</c>, which
    /// cannot be established without the game running. Rather than ship a guess
    /// with no way to correct it, the guess is visible and adjustable.
    /// </summary>
    private void DrawCalibration()
    {
        _showCalibrationOverlay = ImGui.CollapsingHeader("Orientation###limlo-minimap-calibration");

        if (!_showCalibrationOverlay)
            return;

        ImGui.Indent();

        UiHelpers.Muted(
            "If the dots sit at the right distance but the wrong way round, fix it here. "
            + "Distance and centring are read from the game's own minimap fields; the rotation "
            + "is the one part that had to be derived, so it gets a correction.");

        UiHelpers.ColoredWrapped(UiHelpers.Accent,
            "While this section is open, guide rings are drawn on the minimap: a crosshair on "
            + "where the radar thinks the centre is, a ring on where it thinks the edge is, and a "
            + "red tick pointing at where it thinks north is. The crosshair should sit on your "
            + "player arrow and the red tick should agree with the minimap's own compass.");

        ImGui.Spacing();

        var mirror = _config.MinimapMirror;
        if (ImGui.Checkbox("Mirror left/right", ref mirror))
        {
            _config.MinimapMirror = mirror;
            Plugin.SaveConfiguration();
        }
        UiHelpers.HelpMarker(
            "Use this if a mob to your east shows up west. Test it with the map's north lock ON, "
            + "which removes rotation from the picture entirely.");

        ImGui.SetNextItemWidth(200f);
        var offset = _config.MinimapRotationOffsetDegrees;
        if (ImGui.SliderFloat("Rotation offset", ref offset, -180f, 180f, "%.0f°"))
        {
            _config.MinimapRotationOffsetDegrees = offset;
            Plugin.SaveConfiguration();
        }
        UiHelpers.HelpMarker(
            "Only bites while the minimap is free to turn. If everything is correct with north "
            + "locked but rotated by a constant when unlocked, this is the knob — try 180 first.");

        ImGui.SetNextItemWidth(200f);
        var inset = _config.MinimapEdgeInset;
        if (ImGui.SliderFloat("Edge inset", ref inset, 0f, 40f, "%.0f px"))
        {
            _config.MinimapEdgeInset = inset;
            Plugin.SaveConfiguration();
        }
        UiHelpers.HelpMarker("Pulls the rim in, if edge dots sit outside the minimap's frame.");

        ImGui.Spacing();

        if (_state.Found && ImGui.BeginTable("##limlo-minimap-diag", 2, ImGuiTableFlags.SizingFixedFit))
        {
            UiHelpers.Row("Centre on screen",   $"{_state.Centre.X:F0}, {_state.Centre.Y:F0}");
            UiHelpers.Row("Radius",             $"{_state.Radius:F0} px");
            UiHelpers.Row("Yalms to pixels",    $"{_state.Scale:F2}");
            UiHelpers.Row("North locked",       _state.NorthLocked ? "Yes" : "No");
            UiHelpers.Row("Player cone",        $"{_state.ConeRotation * 180f / MathF.PI:F1}°");
            UiHelpers.Row("Camera DirH",        $"{_state.CameraDirH * 180f / MathF.PI:F1}°");
            UiHelpers.Row("Rotation applied",   $"{_state.AppliedRotation * 180f / MathF.PI:F1}°");
            ImGui.EndTable();
        }
        else if (!_state.Found)
        {
            UiHelpers.Muted("Nothing to report — the minimap is not on screen.");
        }

        ImGui.Unindent();
    }
}
