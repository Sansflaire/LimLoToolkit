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
/// centre = addon->MapBase node's ScreenX/Y      // the map circle, NOT the pin
/// scale  = map.MarkerPositionScaling * (nodeWidth / map.Width) * addonScale
/// north  = map.NorthLockedUp                    // is the map fixed north-up
/// </code>
///
/// With the map locked north, world +X is screen right and world +Z is screen
/// down, so the offset is simply <c>(dx, dz) * scale</c>. With it unlocked the
/// map turns so the direction the CHARACTER faces points up, which is a
/// rotation of <c>playerRotation - pi</c> applied to that same offset.
///
/// **Two bugs worth not repeating**, both found by reading live memory rather
/// than by staring at screenshots:
///
/// 1. The centre came from <c>PlayerPin</c>. That node is the player ARROW and
///    it rotates with the character, so the axis-aligned half-size term used to
///    find its middle swung the whole frame in a circle as the character turned.
///    Reported as "the dots are offset from where I am" and "they shift as I
///    turn" — one fault, two symptoms. <c>MapBase</c> does not rotate.
/// 2. The rotation came from <c>Camera.DirH</c>. The minimap follows the
///    CHARACTER. Confirmed live: player at -135 degrees, PlayerPinRotation 45,
///    i.e. exactly <c>facing + 180</c>.
///
/// Because the centre comes from a node's post-transform screen position, HUD
/// layout position, HUD scale and addon scale are all already applied. Moving
/// the minimap in the HUD editor needs no configuration at all.
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

    public const float MinScaleTrim = 0.25f;
    public const float MaxScaleTrim = 4.0f;

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
        float   pinRotation,
        float   cameraDirH,
        float   playerRotation,
        float   appliedRotation)
    {
        public bool    Found           { get; } = found;
        public Vector2 Centre          { get; } = centre;
        public float   Radius          { get; } = radius;
        public float   Scale           { get; } = scale;
        public bool    NorthLocked     { get; } = northLocked;
        public float   ConeRotation    { get; } = coneRotation;
        /// <summary>The player arrow's rotation. The key to automating this.</summary>
        public float   PinRotation     { get; } = pinRotation;
        public float   CameraDirH      { get; } = cameraDirH;
        public float   PlayerRotation  { get; } = playerRotation;
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

    /// <summary>Rate limit for <see cref="LogRawValues"/>.</summary>
    private long _lastLogAt;

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

            _state = ReadMinimap(player.Rotation);
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
    private unsafe MinimapState ReadMinimap(float playerRotation)
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

        // The MAP node, not the player pin. The pin is the player arrow and it
        // ROTATES with the character; see the note on the centre below.
        var mapBase = addon->MapBase;
        if (mapBase == null)
            return default;

        // CENTRE. Taken from the map node's screen position, which has already
        // been through every transform the UI applies — so the HUD layout
        // position, the HUD scale and the addon's own scale are all accounted
        // for and none of them need configuring. Move the minimap in the HUD
        // editor and this follows it.
        //
        // NOT from PlayerPin, which is what the first version did and is the
        // bug reported as "the dots are offset from my actual location" and
        // "they shift around as I turn". AtkResNode.ScreenX/Y is where the
        // node's local (0,0) lands AFTER its transform, and the pin is the
        // player ARROW — it rotates with the character. Adding half its size
        // axis-aligned therefore swung the computed centre in a circle around
        // the true one as the character turned.
        //
        // Verified against live memory 2026-08-11: pin ScreenX/Y (2484.06,
        // 93.62), rotation -4.9218 rad, origin (16,16), addon scale 0.8.
        // Undoing the rotation properly —
        //     centre = screen + scale * R(rotation) . origin
        // — lands on (2474.20, 108.80), which is MapBase's centre to three
        // decimal places. MapBase does not rotate, so it needs none of that.
        var node    = &mapBase->AtkResNode;
        var uiScale = handle.Scale <= 0f ? 1f : handle.Scale;

        var centre = new Vector2(
            node->ScreenX + node->Width * 0.5f * uiScale,
            node->ScreenY + node->Height * 0.5f * uiScale);

        var radius = MathF.Min(node->Width, node->Height) * 0.5f * uiScale - _config.MinimapEdgeInset;

        // SCALE. MarkerPositionScaling converts yalms into the marker coordinate
        // space, in which the map spans Atk2DNaviMap.Width. The map NODE is
        // node->Width pixels wide, so the ratio between the two converts to
        // pixels, and the addon scale converts to screen. Every term is read
        // from the game, so zoom and HUD scale come along for free.
        //
        // Measured live 2026-08-11: MarkerPositionScaling 0.5, map.Width 88,
        // node width 176, addon scale 0.8 — 0.8 px per yalm, an 88 yalm visible
        // radius. The Width-to-node ratio is the one step not confirmed against
        // a second source, which is what the trim in the Orientation panel is
        // for.
        var spanUnits = map.Width > 0 ? map.Width : (ushort)88;
        var scale     = map.MarkerPositionScaling
                        * (node->Width / (float)spanUnits)
                        * uiScale
                        * Math.Clamp(_config.MinimapScaleTrim, MinScaleTrim, MaxScaleTrim);

        var dirH = 0f;
        var cameraManager = CameraManager.Instance();
        if (cameraManager != null)
        {
            var camera = cameraManager->GetActiveCamera();
            if (camera != null)
                dirH = camera->DirH;
        }

        // Locked north: the map never turns, so world offsets go straight on.
        //
        // Otherwise the map turns so the direction the CHARACTER faces points
        // up. Not the camera — that was the original bug. Swinging the camera
        // around a stationary character left the map still while the dots
        // rotated, which is what "the dots shift around as I turn" was.
        //
        // Wanting the facing f = (sin r, cos r) to land on screen "up" (0,-1):
        //     R(phi) . (sin r, cos r) = (sin(r - phi), cos(r - phi)) = (0, -1)
        //  => r - phi = pi
        //  => phi = r - pi
        //
        // NorthLockedUp IS trusted, having been read out of live memory on
        // 2026-08-11: it was true on a minimap whose terrain demonstrably did
        // not turn through a full circle of character facings. An earlier note
        // here claimed the opposite and was wrong — the dots moving as the
        // character turned was the PIN-derived centre orbiting, not a rotation
        // being applied. One bug wearing two symptoms.
        //
        // The character-facing form is confirmed too: with the player at -135
        // degrees, PlayerPinRotation read 45, which is `facing + 180` — the same
        // angle as `facing - pi`. So when the map does turn, this is the right
        // expression for it.
        var rotation = map.NorthLockedUp ? 0f : playerRotation - MathF.PI;

        rotation += _config.MinimapRotationOffsetDegrees * MathF.PI / 180f;

        return new MinimapState(
            found:           true,
            centre:          centre,
            radius:          MathF.Max(8f, radius),
            scale:           scale,
            northLocked:     map.NorthLockedUp,
            coneRotation:    map.PlayerConeRotation,
            pinRotation:     map.PlayerPinRotation,
            cameraDirH:      dirH,
            playerRotation:  playerRotation,
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

    /// <summary>
    /// Writes the raw minimap numbers to the Dalamud log once a second while
    /// the Orientation panel is open.
    ///
    /// Deliberately at Information, not Debug: Dalamud filters Debug out by
    /// default, so a diagnostic logged there is invisible in the field exactly
    /// when it is needed. That lesson is already in BROKEN.md (008).
    ///
    /// It exists to settle how PlayerPinRotation relates to the character's
    /// facing, which is what would let the north-up question be answered
    /// automatically instead of asked.
    /// </summary>
    private void LogRawValues()
    {
        if (!_state.Found)
            return;

        var now = Environment.TickCount64;
        if (now - _lastLogAt < 1000)
            return;

        _lastLogAt = now;

        const float toDegrees = 180f / MathF.PI;

        Plugin.Log.Information(
            "[MinimapRadar] facing={0:F1} pin={1:F1} cone={2:F1} dirH={3:F1} "
            + "northLocked={4} trim={5:F2} applied={6:F1} scale={7:F2} centre=({8:F0},{9:F0}) r={10:F0}",
            _state.PlayerRotation  * toDegrees,
            _state.PinRotation     * toDegrees,
            _state.ConeRotation    * toDegrees,
            _state.CameraDirH      * toDegrees,
            _state.NorthLocked,
            _config.MinimapScaleTrim,
            _state.AppliedRotation * toDegrees,
            _state.Scale,
            _state.Centre.X, _state.Centre.Y,
            _state.Radius);
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

        LogRawValues();

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

        ImGui.SetNextItemWidth(200f);
        var trim = Math.Clamp(_config.MinimapScaleTrim, MinScaleTrim, MaxScaleTrim);
        if (ImGui.SliderFloat("Distance trim", ref trim, MinScaleTrim, MaxScaleTrim, "%.2fx"))
        {
            _config.MinimapScaleTrim = Math.Clamp(trim, MinScaleTrim, MaxScaleTrim);
            Plugin.SaveConfiguration();
        }
        UiHelpers.HelpMarker(
            "Multiplies how far out the dots sit. Leave it at 1.00 unless they are consistently "
            + "too near or too far — the scale is computed from the game's own marker fields and "
            + "follows your zoom, so it should not need touching.");

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
            UiHelpers.Row("North-locked",       _state.NorthLocked ? "Yes" : "No");
            UiHelpers.Row("Player pin",         $"{_state.PinRotation * 180f / MathF.PI:F1}°");
            UiHelpers.Row("Player cone",        $"{_state.ConeRotation * 180f / MathF.PI:F1}°");
            UiHelpers.Row("Your facing",        $"{_state.PlayerRotation * 180f / MathF.PI:F1}°");
            UiHelpers.Row("Camera DirH",        $"{_state.CameraDirH * 180f / MathF.PI:F1}° (not used)");
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
