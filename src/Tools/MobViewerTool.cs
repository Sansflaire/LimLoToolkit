using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface;

using Lumina.Excel;
using Lumina.Excel.Sheets;

using CsGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace LimLoToolkit.Tools;

/// <summary>
/// A browser over every mob the plugin has data for: what the game's sheets say
/// about it, what was observed live, and what training has measured.
///
/// Master list on the left, detail on the right. Rows are coloured by how solid
/// the measured data is — green solved, amber partial, red nothing yet.
///
/// **In Live Mode** (and therefore in the public build) the list is confirmed
/// mobs only, every row is purple, and the measurement apparatus — lock
/// controls, missing-angle guide, envelope table, forget button — is gone.
/// See <see cref="BuildFlavor"/>.
///
/// **On "weakness".** There is no elemental-weakness data reachable from a mob.
/// The only resistance-shaped sheet is <c>BNpcResist</c>: 256 rows of eleven
/// unlabelled booleans, almost certainly status immunities rather than
/// elements — and <c>BNpcBase</c> holds no reference to it, so it cannot be
/// linked to a mob at all. Rather than invent a column, the viewer shows what
/// genuinely resolves. See docs/enemy-vision.md.
/// </summary>
public sealed class MobViewerTool : ITool
{
    public string Id          => "mob-viewer";
    public string Name        => "Mob Viewer";
    public string Description => "Everything known about each mob: detection, ranges, and game data.";
    public string Category    => "Toolkit";

    private const float MasterWidth = 240f;

    private readonly Configuration      _config;
    private readonly AggroLearningStore _store;

    private ExcelSheet<BNpcBase>? _bnpcSheet;

    private uint   _selectedBaseId;
    private string _search = string.Empty;

    private int?    _playerForayLevel;
    private ushort  _territory;
    private Vector3 _playerPos;

    // NOTE: this tool used to place markers on the game's own map, through
    // AgentMap. It was removed at the user's request — they rendered as large
    // red flags and covered the map. Nothing here writes to the game's UI.
    // Sightings are shown only on the plugin's own plot below.
    //
    // Minimap dots came back in 0.20.0 as MinimapRadarTool, but by DRAWING OVER
    // the minimap rather than adding real markers, so the size is ours to choose
    // and there is nothing to clean up. Do not reintroduce AgentMap markers.

    private static readonly Vector4 SightingColor = new(0.35f, 0.85f, 1.00f, 0.90f);

    /// <summary>Wedges marking approaches still needed on the selected mob.</summary>
    private static readonly Vector4 MissingAngleColor = new(1.00f, 0.35f, 0.80f, 0.95f);

    /// <summary>
    /// Show the missing approach angles in the world for the SELECTED mob only.
    /// Deliberately scoped to one mob: drawing this for everything nearby would
    /// be exactly the screen clutter that got the sighting rings removed.
    /// </summary>
    private bool _showMissingAngles = true;

    /// <summary>Live instances of the selected mob, for the wedge overlay.</summary>
    private List<(Vector3 Position, float Rotation)> _selectedInstances = new();

    /// <summary>
    /// The live distance-to-target readout that floats over the player's head.
    ///
    /// Built on the game thread every tick and drawn from the snapshot, per the
    /// tool contract in CLAUDE.md — the framework update runs once per frame, so
    /// this is frame-fresh without reading game memory during Draw.
    /// </summary>
    private readonly record struct HeadReadout(
        Vector3 Anchor,
        float   Gap,
        float   Centre,
        string  TargetName,
        bool    IsSelectedMob);

    private HeadReadout? _headReadout;

    /// <summary>
    /// How long after a fight a mob is assumed to still be walking home. Its
    /// position during that window is on the return path, not its spawn.
    /// </summary>
    private const long ReturnGraceMs = 8000;

    private readonly Dictionary<ulong, long> _lastInCombatAt = new();

    /// <summary>Base ids currently in the object table, refreshed each tick.</summary>
    private HashSet<uint> _nearby = new();

    /// <summary>
    /// Mobs seen nearby that have no recorded pull yet. Without these, a mob you
    /// have never been detected by would simply not appear — and "no data" is
    /// exactly the state worth showing in red.
    /// </summary>
    private Dictionary<uint, AggroProfile> _seenOnly = new();

    public MobViewerTool(Configuration config, AggroLearningStore store)
    {
        _config = config;
        _store  = store;
    }

    public void OnFrameworkUpdate()
    {
        try
        {
            var nearby    = new HashSet<uint>();
            var instances = new List<(Vector3, float)>();

            _bnpcSheet ??= Plugin.DataManager.GetExcelSheet<BNpcBase>();

            var player = Plugin.ObjectTable.LocalPlayer;
            _playerForayLevel = ForayLevel.TryGet(player);

            var territory = (ushort)Plugin.ClientState.TerritoryType;
            _territory    = territory;
            _playerPos    = player?.Position ?? Vector3.Zero;

            _headReadout = BuildHeadReadout(player);


            foreach (var obj in Plugin.ObjectTable)
            {
                if (obj.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc)
                    continue;

                if (!obj.IsValid() || obj.IsDead)
                    continue;

                if (obj is not IBattleNpc battleNpc || battleNpc.BattleNpcKind != BattleNpcSubKind.Combatant)
                    continue;

                if (obj.BaseId == _selectedBaseId && Vector3.Distance(_playerPos, obj.Position) <= 60f)
                    instances.Add((obj.Position, obj.Rotation));

                var name = obj.Name.ToString();

                // Names outside the tracked prefix are noise and never get an
                // entry. Explicitly-ignored mobs DO stay listed, otherwise they
                // could never be un-ignored from here.
                if (_store.IsAutoIgnoredByName(name))
                    continue;

                nearby.Add(obj.BaseId);

                // Only record where a mob LIVES, which means catching it truly
                // idle. A mob in combat is wherever the fight dragged it, and a
                // mob that just lost aggro is walking home — neither position
                // says anything about its spawn. So it must be out of combat
                // AND clear of the return grace since it last fought.
                var id  = obj.GameObjectId;
                var now = Environment.TickCount64;

                if (battleNpc.StatusFlags.HasFlag(StatusFlags.InCombat))
                {
                    // Tracked even with collection off, so switching it on
                    // mid-fight does not immediately record a combat position.
                    _lastInCombatAt[id] = now;
                }
#if !PUBLIC_BUILD
                else if (_config.AggroTrainingEnabled && !BuildFlavor.IsLive)
                {
                    var settled = !_lastInCombatAt.TryGetValue(id, out var lastFight)
                                  || now - lastFight > ReturnGraceMs;

                    if (settled && _store.AddSighting(obj.BaseId, name, territory, obj.Position))
                        _store.MarkDirty();
                }
#endif

                var foray = ForayLevel.TryGet(obj) ?? 0;

                // Remember the level on the profile so relevance can still be
                // judged when the mob is nowhere near.
                if (_store.Find(obj.BaseId) is { } known)
                {
                    if (foray > 0 && known.ForayLevel != foray)
                    {
                        known.ForayLevel = foray;
                        _store.MarkDirty();
                    }

                    continue;
                }

                if (_seenOnly.ContainsKey(obj.BaseId))
                    continue;

                _seenOnly[obj.BaseId] = new AggroProfile
                {
                    BaseId               = obj.BaseId,
                    Name                 = name,
                    SheetOmnidirectional = _bnpcSheet?.GetRowOrDefault(obj.BaseId)?.IsOmnidirectional ?? false,
                    TerritoryId          = (ushort)Plugin.ClientState.TerritoryType,
                    Level                = battleNpc.Level,
                    MaxHp                = battleNpc.MaxHp,
                    HitboxRadius         = obj.HitboxRadius,
                    ForayLevel           = foray,
                };
            }

            _nearby            = nearby;
            _selectedInstances = instances;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "MobViewerTool failed to refresh the nearby set.");
        }
    }

    /// <summary>
    /// Works out what, if anything, should float over the player's head this
    /// frame: the live distance to the mob currently targeted.
    ///
    /// The headline number is the hitbox-to-hitbox GAP rather than the raw
    /// centre-to-centre distance, because every range this plugin records,
    /// draws, and lets you type in by hand is a gap. Showing centre distance as
    /// the big number would invite reading it straight off against a measured
    /// range that does not mean the same thing.
    /// </summary>
    private unsafe HeadReadout? BuildHeadReadout(IGameObject? player)
    {
        if (!_config.ShowTargetDistanceOverHead || player == null)
            return null;

        if (Plugin.TargetManager.Target is not IBattleNpc target)
            return null;

        if (target.BattleNpcKind != BattleNpcSubKind.Combatant || !target.IsValid() || target.IsDead)
            return null;

        var isSelected = _selectedBaseId != 0 && target.BaseId == _selectedBaseId;

        if (_config.TargetDistanceSelectedMobOnly && !isSelected)
            return null;

        var centre = Vector3.Distance(player.Position, target.Position);
        var gap    = MathF.Max(0f, centre - player.HitboxRadius - target.HitboxRadius);

        // GameObject.Height is the model's own height in yalms — a lalafell and
        // a roegadyn need different anchors or the text sits on the face of one
        // and a yard over the other. Fall back to a sane constant if it reads
        // as zero, which happens for a frame or two around a redraw.
        var height = 2.0f;
        var native = (CsGameObject*)player.Address;
        if (native != null && native->Height > 0.1f)
            height = native->Height;

        var anchor = new Vector3(
            player.Position.X,
            player.Position.Y + height + 0.55f,
            player.Position.Z);

        return new HeadReadout(anchor, gap, centre, target.Name.ToString(), isSelected);
    }

    /// <summary>
    /// Draws the head readout: the gap large, the raw distance and the mob's
    /// name small underneath, on a dark plate so it stays legible over bright
    /// ground.
    /// </summary>
    private void DrawHeadReadout()
    {
        if (_headReadout is not { } readout)
            return;

        if (!Plugin.GameGui.WorldToScreen(readout.Anchor, out var anchor))
            return;

        var draw     = ImGui.GetForegroundDrawList();
        var font     = ImGui.GetFont();
        var baseSize = ImGui.GetFontSize();
        var bigSize  = baseSize * 1.75f;

        var headline = $"{readout.Gap:F1}y";
        var detail   = $"gap  ·  {readout.Centre:F1}y centre";
        var name     = readout.TargetName;

        var headlineSize = ImGui.CalcTextSize(headline) * (bigSize / baseSize);
        var detailSize   = ImGui.CalcTextSize(detail);
        var nameSize     = ImGui.CalcTextSize(name);

        const float lineGap = 2f;
        const float padX    = 8f;
        const float padY    = 5f;

        var width  = MathF.Max(headlineSize.X, MathF.Max(detailSize.X, nameSize.X));
        var height = headlineSize.Y + detailSize.Y + nameSize.Y + lineGap * 2f;

        // Anchored by its BOTTOM centre, so the block grows upwards away from
        // the character rather than creeping down over the head.
        var topLeft = new Vector2(anchor.X - width * 0.5f, anchor.Y - height);

        draw.AddRectFilled(
            topLeft - new Vector2(padX, padY),
            topLeft + new Vector2(width + padX, height + padY),
            ImGui.ColorConvertFloat4ToU32(new Vector4(0.05f, 0.06f, 0.08f, 0.72f)),
            5f);

        var accent = ImGui.ColorConvertFloat4ToU32(UiHelpers.Accent);

        draw.AddRect(
            topLeft - new Vector2(padX, padY),
            topLeft + new Vector2(width + padX, height + padY),
            readout.IsSelectedMob ? accent : ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.55f)),
            5f, ImDrawFlags.None, 1.5f);

        var y = topLeft.Y;

        draw.AddText(
            font, bigSize,
            new Vector2(anchor.X - headlineSize.X * 0.5f, y),
            ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.98f)),
            headline);
        y += headlineSize.Y + lineGap;

        draw.AddText(
            new Vector2(anchor.X - detailSize.X * 0.5f, y),
            ImGui.ColorConvertFloat4ToU32(UiHelpers.Dim),
            detail);
        y += detailSize.Y + lineGap;

        draw.AddText(
            new Vector2(anchor.X - nameSize.X * 0.5f, y),
            ImGui.ColorConvertFloat4ToU32(readout.IsSelectedMob ? UiHelpers.Accent : UiHelpers.Dim),
            name);
    }

    /// <summary>
    /// Draws the approach angles still missing for the selected mob, as pink
    /// wedges around each live instance of it. Walk into one and it fills in.
    ///
    /// Turns "needs 5 more angles" from a number to interpret into a target to
    /// walk at. Only the selected mob is drawn — doing this for everything
    /// nearby would be the same screen clutter that got the sighting rings
    /// deleted.
    ///
    /// Slices are folded left/right (detection is symmetric), so each missing
    /// slice is drawn on both sides of the mob's facing. Standing in either
    /// satisfies it.
    /// </summary>
    public void DrawOverlay()
    {
        // Independent of the wedges below — the readout is wanted whether or not
        // a mob is selected or has angles left to fill.
        DrawHeadReadout();

        // Missing-angle wedges are a measurement aid — they mark evidence still
        // to be gathered, which is not a thing the live plugin does.
        if (BuildFlavor.IsLive || !_showMissingAngles || _selectedBaseId == 0)
            return;

        var instances = _selectedInstances;
        if (instances.Count == 0)
            return;

        var profile = _store.Find(_selectedBaseId);
        if (profile == null || _store.IsIgnored(_selectedBaseId))
            return;

        var missing = AggroLearningStore.MissingBins(profile);
        if (missing.Count == 0)
            return;

        var drawList  = ImGui.GetForegroundDrawList();
        var colour    = ImGui.ColorConvertFloat4ToU32(MissingAngleColor);
        var thickness = Math.Clamp(_config.OverlayThickness,
                                   EnemyVisionTool.MinThickness, EnemyVisionTool.MaxThickness);

        // Far enough out to stand in comfortably, without implying a measured
        // distance — this marks a direction, not a range.
        const float wedgeRadius = 10f;

        foreach (var (position, rotation) in instances)
        {
            foreach (var bin in missing)
            {
                var (start, end) = AggroLearningStore.BinRange(bin);

                DrawWedge(drawList, position, rotation, start, end, wedgeRadius, thickness, colour);
                DrawWedge(drawList, position, rotation, -end, -start, wedgeRadius, thickness, colour);
            }
        }
    }

    /// <summary>Outlines one angular wedge on the ground, relative to a facing.</summary>
    private static void DrawWedge(
        ImDrawListPtr drawList,
        Vector3       centre,
        float         facing,
        float         startDegrees,
        float         endDegrees,
        float         radius,
        float         thickness,
        uint          colour)
    {
        const int segments = 6;

        var startRad = facing + startDegrees * MathF.PI / 180f;
        var endRad   = facing + endDegrees   * MathF.PI / 180f;

        Vector2? previous = null;
        Vector2? arcStart = null;
        Vector2? arcEnd   = null;

        var centreOnScreen = Plugin.GameGui.WorldToScreen(centre, out var centreScreen);

        for (var i = 0; i <= segments; i++)
        {
            var angle = startRad + (endRad - startRad) * (i / (float)segments);
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

        if (arcStart is { } s) drawList.AddLine(centreScreen, s, colour, thickness);
        if (arcEnd   is { } e) drawList.AddLine(centreScreen, e, colour, thickness);
    }

    /// <summary>Measured profiles, plus placeholders for anything only ever seen.</summary>
    private List<AggroProfile> BuildDisplayList()
    {
        // Live: confirmed mobs only. A profile that is merely "seen" or
        // part-measured has nothing to state as fact, and listing it in red
        // would be advertising the measuring apparatus this build does without.
        if (BuildFlavor.IsLive)
            return _store.All
                .Where(p => p.Locked)
                .OrderBy(p => IsIrrelevant(p) ? 1 : 0)
                .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

        var all = new List<AggroProfile>(_store.All);

        foreach (var (baseId, placeholder) in _seenOnly)
        {
            if (_store.Find(baseId) == null)
                all.Add(placeholder);
        }

        // Irrelevant mobs sink to the bottom regardless of how much data they
        // have; among the rest, the best-known come first.
        return all
            .OrderBy(p => IsIrrelevant(p) ? 1 : 0)
            .ThenBy(p => _store.ConfidenceOf(p) switch
            {
                AggroConfidence.Confident => 0,
                AggroConfidence.Learning  => 1,
                _                         => 2,
            })
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Not worth attention: either explicitly ignored, or so far below the
    /// player's Knowledge level that it can never aggro.
    /// </summary>
    private bool IsIrrelevant(AggroProfile profile) =>
        _store.IsIgnored(profile.BaseId) || IsOutleveled(profile);

    private bool IsOutleveled(AggroProfile profile) =>
        _config.IgnoreOutleveledEnemies
        && profile.ForayLevel > 0
        && ForayLevel.IsHarmless(_playerForayLevel, profile.ForayLevel, _config.OutlevelMargin);

    public void Draw()
    {
        var profiles = BuildDisplayList();

        if (profiles.Count == 0)
        {
            UiHelpers.SectionHeader("Mob Viewer");
#if PUBLIC_BUILD
            UiHelpers.Muted("No mobs with confirmed values are available yet.");
#else
            UiHelpers.Muted(BuildFlavor.IsLive
                ? "No mobs with confirmed values are available yet."
                : "No mobs recorded yet. Turn on Training Mode in Enemy Vision and go pull " +
                  "something in the Occult Crescent — every mob that aggros you gets an entry " +
                  "here automatically.");
#endif
            return;
        }

        DrawMaster(profiles);

        ImGui.SameLine();

        if (ImGui.BeginChild("##limlo-mob-detail", Vector2.Zero, true))
        {
            DrawCounters(profiles);

            var selected = profiles.FirstOrDefault(p => p.BaseId == _selectedBaseId) ?? profiles[0];
            DrawDetail(selected);
        }

        ImGui.EndChild();
    }

    /// <summary>
    /// One-line tally across the top of the detail pane.
    ///
    /// Deliberately never wraps: five short counts reading left to right are
    /// scannable, and the same five broken across two lines are not. If the
    /// panel is too narrow the row runs off the edge, which is the better
    /// failure — the leading counts stay in place and the window can be
    /// widened.
    /// </summary>
    private void DrawCounters(List<AggroProfile> profiles)
    {
        // Live: every listed mob is confirmed, so the five-way breakdown of how
        // solid the evidence is has nothing left to distinguish.
        if (BuildFlavor.IsLive)
        {
            UiHelpers.Colored(UiHelpers.Official, $"{profiles.Count} mob(s) with confirmed values");
            ImGui.Separator();
            ImGui.Spacing();
            return;
        }

#if !PUBLIC_BUILD
        var official   = 0;
        var solved     = 0;
        var learning   = 0;
        var empty      = 0;
        var irrelevant = 0;

        foreach (var profile in profiles)
        {
            if (IsIrrelevant(profile)) { irrelevant++; continue; }
            if (profile.Locked)        { official++;   continue; }

            switch (_store.ConfidenceOf(profile))
            {
                case AggroConfidence.Confident: solved++;   break;
                case AggroConfidence.Learning:  learning++; break;
                default:                        empty++;    break;
            }
        }

        UiHelpers.Colored(UiHelpers.Official, $"{official} official");
        ImGui.SameLine();
        UiHelpers.Colored(UiHelpers.Good, $"{solved} solved");
        ImGui.SameLine();
        UiHelpers.Colored(UiHelpers.Warn, $"{learning} partial");
        ImGui.SameLine();
        UiHelpers.Colored(UiHelpers.Bad, $"{empty} empty");
        ImGui.SameLine();
        UiHelpers.Colored(UiHelpers.Dim, $"{irrelevant} skipped");

        ImGui.Separator();
        ImGui.Spacing();
#endif
    }

    /// <summary>Padlock glyph from the icon font, for a locked mob.</summary>
    private static void DrawLockIcon()
    {
        using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
        {
            ImGui.PushStyleColor(ImGuiCol.Text, UiHelpers.Official);
            ImGui.TextUnformatted(((char)FontAwesomeIcon.Lock).ToString());
            ImGui.PopStyleColor();
        }

        ImGui.SameLine(0f, 4f);
    }

    private void DrawMaster(List<AggroProfile> profiles)
    {
        if (ImGui.BeginChild("##limlo-mob-master", new Vector2(MasterWidth, 0), true))
        {
            ImGui.SetNextItemWidth(-1);
            var search = _search;
            if (ImGui.InputTextWithHint("##limlo-mob-search", "Search mobs...", ref search, 64))
                _search = search;

            ImGui.Separator();

            foreach (var profile in profiles)
            {
                var irrelevant = IsIrrelevant(profile);

                if (_config.MobViewerHideIrrelevant && irrelevant)
                    continue;

                if (_config.MobViewerNearbyOnly && !_nearby.Contains(profile.BaseId))
                    continue;

                if (!string.IsNullOrWhiteSpace(_search)
                    && profile.Name.IndexOf(_search, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                var confidence = _store.ConfidenceOf(profile);

                // Irrelevant mobs stay visible but recede to grey — they are not
                // part of the green/amber/red story any more.
                // Padlock sits to the LEFT of the name, so locked mobs are
                // identifiable by shape as well as by colour.
                if (profile.Locked)
                    DrawLockIcon();

                ImGui.PushStyleColor(ImGuiCol.Text,
                    irrelevant
                        ? UiHelpers.Dim
                        : BuildFlavor.IsLive
                            ? UiHelpers.Official
                            : UiHelpers.StateColor(confidence, profile.Locked));
                var label = string.IsNullOrEmpty(profile.Name) ? $"#{profile.BaseId}" : profile.Name;
                if (ImGui.Selectable($"{label}###limlo-mob-{profile.BaseId}", profile.BaseId == _selectedBaseId))
                    _selectedBaseId = profile.BaseId;
                ImGui.PopStyleColor();

                if (profile.Locked && ImGui.IsItemHovered())
                    ImGui.SetTooltip("Official — values locked by you");

                if (_nearby.Contains(profile.BaseId))
                {
                    ImGui.SameLine();
                    UiHelpers.Colored(UiHelpers.Accent, "*");
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Currently nearby");
                }
            }
        }

        ImGui.EndChild();
    }

    /// <summary>
    /// Top-down plot of everywhere this mob has been seen, with the player
    /// marked. Scaled to fit whatever ground the mob actually covers, so it
    /// reads the same for a mob confined to one camp and one spread across the
    /// zone — the axis labels carry the true extent.
    /// </summary>
    /// <summary>
    /// Scale diagram of the detection shape: the mob at the origin as a red
    /// dot, the reach drawn to scale, and the measurements labelled on the
    /// drawing itself. A picture of "8.4y over 90° in front" is read instantly
    /// where the same thing as two table rows is not.
    ///
    /// The mob always faces UP, so the wedge orientation reads the same for
    /// every mob regardless of where it happens to be looking in the world.
    /// </summary>
    private static void DrawShapeDiagram(DetectionModel model)
    {
        const float canvas = 200f;
        const float pad    = 18f;

        var origin = ImGui.GetCursorScreenPos();
        var draw   = ImGui.GetWindowDrawList();
        var centre = origin + new Vector2(canvas * 0.5f, canvas * 0.5f);

        draw.AddRectFilled(origin, origin + new Vector2(canvas, canvas),
            ImGui.ColorConvertFloat4ToU32(new Vector4(0.08f, 0.09f, 0.11f, 1f)), 4f);
        draw.AddRect(origin, origin + new Vector2(canvas, canvas),
            ImGui.ColorConvertFloat4ToU32(UiHelpers.Dim), 4f);

        if (model.Type == DetectionType.Unknown || model.Range <= 0f)
        {
            ImGui.Dummy(new Vector2(canvas, canvas));
            UiHelpers.Muted("No shape to draw yet.");
            return;
        }

        // Scale so the reach fills the canvas with a margin for labels.
        var pixels = canvas * 0.5f - pad;
        var scale  = pixels / model.Range;

        var shapeColour = ImGui.ColorConvertFloat4ToU32(
            model.Type == DetectionType.Radius
                ? new Vector4(0.62f, 0.55f, 0.95f, 0.95f)
                : new Vector4(0.98f, 0.75f, 0.25f, 0.95f));

        var radius = model.Range * scale;

        // Screen Y grows downwards, so "in front" is negative Y.
        Vector2 At(float degreesFromFacing, float distance)
        {
            var rad = degreesFromFacing * MathF.PI / 180f;
            return centre + new Vector2(MathF.Sin(rad) * distance, -MathF.Cos(rad) * distance);
        }

        if (model.Type == DetectionType.Radius)
        {
            draw.AddCircle(centre, radius, shapeColour, 48, 2.5f);
        }
        else
        {
            var half     = model.HalfAngleDegrees;
            var segments = Math.Max(8, (int)(half / 4f));

            Vector2? previous = null;
            for (var i = 0; i <= segments; i++)
            {
                var angle = -half + (half * 2f) * (i / (float)segments);
                var point = At(angle, radius);

                if (previous is { } prev)
                    draw.AddLine(prev, point, shapeColour, 2.5f);

                previous = point;
            }

            draw.AddLine(centre, At(-half, radius), shapeColour, 2.5f);
            draw.AddLine(centre, At(half, radius), shapeColour, 2.5f);

            // Angle marker: a small arc near the origin, labelled.
            var markerRadius = MathF.Min(34f, radius * 0.45f);
            Vector2? prevMark = null;
            for (var i = 0; i <= segments; i++)
            {
                var angle = -half + (half * 2f) * (i / (float)segments);
                var point = At(angle, markerRadius);

                if (prevMark is { } pm)
                    draw.AddLine(pm, point, ImGui.ColorConvertFloat4ToU32(UiHelpers.Dim), 1.5f);

                prevMark = point;
            }

            var angleLabel = $"{model.FullConeDegrees:F0}°";
            draw.AddText(At(0f, markerRadius + 4f) - new Vector2(ImGui.CalcTextSize(angleLabel).X * 0.5f, 0f),
                ImGui.ColorConvertFloat4ToU32(UiHelpers.Dim), angleLabel);
        }

        // Radius line, drawn along a direction that is inside the shape.
        var radiusEnd = At(model.Type == DetectionType.Radius ? 90f : 0f, radius);
        draw.AddLine(centre, radiusEnd, ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.75f)), 1.5f);

        var rangeLabel = $"{model.Range:F1}y";
        var labelPos   = (centre + radiusEnd) * 0.5f + new Vector2(4f, -14f);
        draw.AddText(labelPos, ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.95f)), rangeLabel);

        // The mob itself, last so nothing covers it.
        draw.AddCircleFilled(centre, 4.5f, ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.25f, 0.25f, 1f)));

        ImGui.Dummy(new Vector2(canvas, canvas));
        UiHelpers.Muted(model.Type == DetectionType.Radius
            ? "Red dot is the mob. It detects equally in every direction."
            : "Red dot is the mob, facing up the screen.");
    }

    /// <summary>
    /// Lets the user pin a mob's values by hand.
    ///
    /// The learner is deliberately conservative and self-correcting, which
    /// means it can walk a value back — a closer safe stand deletes a longer
    /// pull, because newer evidence is meant to win. That is right when the old
    /// reading was noise and wrong when the user simply knows they have been
    /// caught from further away. A lock settles it: their value stands and no
    /// amount of sampling moves it.
    ///
    /// Samples keep accumulating underneath, so unlocking returns the mob to
    /// whatever the evidence says.
    /// </summary>
#if !PUBLIC_BUILD
    private void DrawLockControls(AggroProfile profile)
    {
        ImGui.Spacing();

        if (!ImGui.CollapsingHeader($"Set values by hand###limlo-manual-{profile.BaseId}"))
            return;

        ImGui.Indent();

        // Fields are ALWAYS editable, lock or no lock. Type the values first,
        // check they look right on the diagram, then lock them — rather than
        // having to lock before you are allowed to enter anything.
        if (profile.LockedRange <= 0f)
        {
            var current = AggroLearningStore.Classify(profile);
            profile.LockedRange      = current.Range > 0f ? current.Range : _config.EnemyVisionRadius;
            profile.LockedArcDegrees = current.Type == DetectionType.Radius
                ? 90f
                : Math.Clamp(current.FullConeDegrees, 5f, 360f);
            profile.LockedIsSound    = current.Type == DetectionType.Radius;
        }

        var isSound = profile.LockedIsSound;
        if (ImGui.RadioButton($"Sight (cone)###limlo-sight-{profile.BaseId}", !isSound))
            Apply(profile, profile.LockedRange, profile.LockedArcDegrees, false);
        ImGui.SameLine();
        if (ImGui.RadioButton($"Sound (all round)###limlo-sound-{profile.BaseId}", isSound))
            Apply(profile, profile.LockedRange, profile.LockedArcDegrees, true);

        var range = profile.LockedRange;
        ImGui.SetNextItemWidth(150f);
        if (ImGui.InputFloat($"Range (yalms)###limlo-range-{profile.BaseId}", ref range, 0.5f, 1f, "%.1f"))
            Apply(profile, Math.Clamp(range, 0f, EnemyVisionTool.MaxRadius),
                  profile.LockedArcDegrees, profile.LockedIsSound);

        if (!profile.LockedIsSound)
        {
            var arc = profile.LockedArcDegrees;
            ImGui.SetNextItemWidth(150f);
            if (ImGui.InputFloat($"Cone angle (deg)###limlo-arc-{profile.BaseId}", ref arc, 5f, 15f, "%.0f"))
                Apply(profile, profile.LockedRange, Math.Clamp(arc, 5f, 360f), profile.LockedIsSound);

            UiHelpers.HelpMarker("Total width of the wedge, centred on the mob's facing.");
        }

        if (ImGui.SmallButton($"Copy from measurement###limlo-frommeas-{profile.BaseId}"))
        {
            var wasLocked  = profile.Locked;
            profile.Locked = false;
            var measured   = AggroLearningStore.Classify(profile);
            profile.Locked = wasLocked;

            if (measured.Type != DetectionType.Unknown)
            {
                Apply(profile,
                      measured.Range,
                      measured.Type == DetectionType.Radius ? profile.LockedArcDegrees : measured.FullConeDegrees,
                      measured.Type == DetectionType.Radius);
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Fill the fields from what the samples currently say, then adjust by hand.");

        ImGui.Spacing();

        var locked = profile.Locked;
        if (ImGui.Checkbox($"Lock — use these values and stop the learner changing them###limlo-lock-{profile.BaseId}",
                           ref locked))
        {
            _store.SetLock(profile.BaseId, locked,
                profile.LockedRange, profile.LockedArcDegrees, profile.LockedIsSound);
        }
        UiHelpers.HelpMarker(
            "While locked, these exact values are drawn and no sampling moves them. Use it when you " +
            "know you get caught from further out than the measurements say. Recorded samples are " +
            "kept, so unlocking hands the mob back to the evidence.");

        if (!profile.Locked)
            UiHelpers.Muted("Not locked — the drawing still follows the measurements above.");

        ImGui.Unindent();
    }

    /// <summary>Writes edited values through, preserving the current lock state.</summary>
    private void Apply(AggroProfile profile, float range, float arcDegrees, bool isSound) =>
        _store.SetLock(profile.BaseId, profile.Locked, range, arcDegrees, isSound);

    /// <summary>
    /// Lists the approaches still needed and offers to show them in the world.
    /// </summary>
    private void DrawMissingAngleGuide(AggroProfile profile)
    {
        var missing = AggroLearningStore.MissingBins(profile);
        if (missing.Count == 0)
            return;

        ImGui.Spacing();

        var show = _showMissingAngles;
        if (ImGui.Checkbox($"Show the {missing.Count} missing approach angle(s) in pink", ref show))
            _showMissingAngles = show;
        UiHelpers.HelpMarker(
            "Draws a pink wedge on this mob for every angle with no data yet. Walk into one and it " +
            "fills in — either it notices you, or it does not, and both answer the question. Drawn " +
            "for the selected mob only.");

        var labels = missing
            .Select(bin => AggroLearningStore.BinLabel(bin))
            .ToList();

        UiHelpers.ColoredWrapped(MissingAngleColor,
            "Still needed: " + string.Join(", ", labels) + " off its facing.");

        if (_selectedInstances.Count == 0)
            UiHelpers.Muted("None of these are nearby right now, so nothing is drawn.");
    }

#endif

    private void DrawSightingMap(AggroProfile profile)
    {
        UiHelpers.SectionHeader("Where It Lives");

        var points = profile.Sightings.Where(s => s.Territory == _territory).ToList();

        if (points.Count == 0)
        {
#if PUBLIC_BUILD
            UiHelpers.Muted("No known locations for this mob in this zone.");
#else
            if (BuildFlavor.IsLive)
                UiHelpers.Muted("No known locations for this mob in this zone.");
            else if (!_config.AggroTrainingEnabled)
                UiHelpers.ColoredWrapped(UiHelpers.Warn,
                    "Data collection is off, so no locations are being recorded. Turn on "
                    + "\"Collect and update mob data\" in Enemy Vision.");
            else
                UiHelpers.Muted(profile.Sightings.Count > 0
                    ? "Seen only in another zone. Nothing to plot here."
                    : "Not seen anywhere yet — walk past one while it is idle and it will appear here.");
#endif

            return;
        }

#if !PUBLIC_BUILD
        if (!BuildFlavor.IsLive && !_config.AggroTrainingEnabled)
            UiHelpers.Muted("Data collection is off — this shows what was already recorded.");
#endif


        // Fit to the spread of sightings plus the player, with a floor so a
        // single point does not blow up to fill the canvas.
        var minX = points.Min(p => p.X);
        var maxX = points.Max(p => p.X);
        var minZ = points.Min(p => p.Z);
        var maxZ = points.Max(p => p.Z);

        minX = MathF.Min(minX, _playerPos.X);
        maxX = MathF.Max(maxX, _playerPos.X);
        minZ = MathF.Min(minZ, _playerPos.Z);
        maxZ = MathF.Max(maxZ, _playerPos.Z);

        var spanX = MathF.Max(maxX - minX, 20f);
        var spanZ = MathF.Max(maxZ - minZ, 20f);
        var span  = MathF.Max(spanX, spanZ) * 1.15f;

        var centreX = (minX + maxX) * 0.5f;
        var centreZ = (minZ + maxZ) * 0.5f;

        const float canvas = 240f;
        var origin = ImGui.GetCursorScreenPos();
        var draw   = ImGui.GetWindowDrawList();

        draw.AddRectFilled(origin, origin + new Vector2(canvas, canvas),
            ImGui.ColorConvertFloat4ToU32(new Vector4(0.08f, 0.09f, 0.11f, 1f)), 4f);
        draw.AddRect(origin, origin + new Vector2(canvas, canvas),
            ImGui.ColorConvertFloat4ToU32(UiHelpers.Dim), 4f);

        Vector2 ToCanvas(float x, float z) => origin + new Vector2(
            (x - centreX) / span * canvas + canvas * 0.5f,
            (z - centreZ) / span * canvas + canvas * 0.5f);

        var spotColour = ImGui.ColorConvertFloat4ToU32(SightingColor);
        foreach (var point in points)
            draw.AddCircleFilled(ToCanvas(point.X, point.Z), 3.5f, spotColour);

        // Player marker last, so it is never hidden under a spot.
        var player = ToCanvas(_playerPos.X, _playerPos.Z);
        draw.AddCircleFilled(player, 4f, ImGui.ColorConvertFloat4ToU32(UiHelpers.Good));
        draw.AddCircle(player, 6.5f, ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.85f)), 0, 1.5f);

        ImGui.Dummy(new Vector2(canvas, canvas));

        UiHelpers.Muted($"{points.Count} spot(s) across roughly {span:F0} yalms. Green is you.");
    }

    private void DrawDetail(AggroProfile profile)
    {
        _bnpcSheet ??= Plugin.DataManager.GetExcelSheet<BNpcBase>();
        var row = _bnpcSheet?.GetRowOrDefault(profile.BaseId);

        var confidence = _store.ConfidenceOf(profile);

        UiHelpers.SectionHeader(string.IsNullOrEmpty(profile.Name) ? $"#{profile.BaseId}" : profile.Name);

        var ignored = _store.IsIgnored(profile.BaseId);

        var live = BuildFlavor.IsLive;

        if (IsOutleveled(profile))
            UiHelpers.ColoredWrapped(UiHelpers.Dim,
                $"Harmless — its Knowledge is {profile.ForayLevel} and yours is {_playerForayLevel}, "
                + "so it can never aggro you." + (live ? " Not drawn." : " Not drawn, not trained on."));
        else if (ignored)
            UiHelpers.ColoredWrapped(UiHelpers.Dim,
                live ? "Hidden — not drawn." : "Ignored — not drawn, not trained on.");
        else if (live)
            UiHelpers.ColoredWrapped(UiHelpers.Official, "Confirmed values.");
#if !PUBLIC_BUILD
        else if (profile.Locked)
            UiHelpers.ColoredWrapped(UiHelpers.Official,
                "OFFICIAL — values locked by you. The learner will not change them.");
        else
            UiHelpers.ColoredWrapped(
                UiHelpers.ConfidenceColor(confidence),
                EnemyVisionTool.DescribeProgress(_store, profile));

        if (!live)
        {
            DrawLockControls(profile);
            DrawMissingAngleGuide(profile);
        }
#endif

        ImGui.Spacing();

        var ignoreToggle = ignored;
        if (ImGui.Checkbox(
                (live ? "Hide this mob" : "Irrelevant — ignore this mob")
                + $"###limlo-ignore-detail-{profile.BaseId}", ref ignoreToggle))
        {
            _store.SetIgnored(profile.BaseId, ignoreToggle);
            Plugin.SaveConfiguration();
        }
#if PUBLIC_BUILD
        UiHelpers.HelpMarker(
            "Hidden mobs get no detection shape drawn. Everything known about them is kept.");
#else
        UiHelpers.HelpMarker(live
            ? "Hidden mobs get no detection shape drawn. Everything known about them is kept."
            : "Ignored mobs get no detection shape drawn and contribute no training samples. " +
              "Anything already measured is kept, so un-ignoring restores it.");
#endif

#if !PUBLIC_BUILD
        if (!live && AggroLearningStore.ContradictsSheet(profile))
        {
            ImGui.Spacing();
            UiHelpers.ColoredWrapped(UiHelpers.Warn,
                $"Contradicts the game data: pulled from behind {profile.RearPulls} time(s), " +
                "so it is treated as detecting in all directions.");
        }
#endif

        ImGui.Spacing();
        UiHelpers.SectionHeader("Detection");

        var model = AggroLearningStore.Classify(profile);

        var verdictColour = profile.Locked
            ? UiHelpers.Official
            : model.Type switch
            {
                DetectionType.Cone   => UiHelpers.Good,
                DetectionType.Radius => UiHelpers.Good,
                _                    => UiHelpers.Warn,
            };

        UiHelpers.ColoredWrapped(verdictColour, model.Type switch
        {
            DetectionType.Cone   => $"CONE — {model.Range:F1}y out to {model.FullConeDegrees:F0}° wide",
            DetectionType.Radius => $"RADIUS — {model.Range:F1}y in every direction",
            _                    => "NOT YET CLASSIFIED",
        });

        UiHelpers.Muted(model.Reason);

        ImGui.Spacing();
        DrawShapeDiagram(model);
        ImGui.Spacing();

#if !PUBLIC_BUILD
        // Collapsed by default — the diagram and the verdict answer the usual
        // question, and the numbers behind them are for when they do not.
        if (!live
            && ImGui.CollapsingHeader($"Detection details###limlo-detect-{profile.BaseId}")
            && ImGui.BeginTable("##limlo-mob-detect", 2, ImGuiTableFlags.SizingFixedFit))
        {
            UiHelpers.Row("Proven range", model.Range > 0f ? $"{model.Range:F1}y" : "unknown");
            UiHelpers.Row("Arc",
                model.Type == DetectionType.Radius ? "360° (all directions)"
                    : model.Type == DetectionType.Cone ? $"{model.FullConeDegrees:F0}° total"
                    : "undecided");

            UiHelpers.Row("Game data says",
                profile.SheetOmnidirectional ? "All directions" : "Forward only");

            if (model.Type != DetectionType.Unknown)
            {
                var agrees = (model.Type == DetectionType.Radius) == profile.SheetOmnidirectional;
                UiHelpers.Row("Agreement", agrees ? "matches observation" : "CONTRADICTED by observation");
            }

            if (profile.Distances.Count > 0)
            {
                UiHelpers.Row("Pulls recorded", profile.Distances.Count.ToString());
                UiHelpers.Row("Closest pull", $"{profile.Distances.Min():F1}y");
                UiHelpers.Row("Widest detection", $"{profile.MaxAngle:F0}° off its facing");
            }

            ImGui.EndTable();
        }
#endif

        ImGui.Spacing();
        DrawSightingMap(profile);

#if !PUBLIC_BUILD
        // The envelope table is the raw evidence behind the verdict — the
        // apparatus, not the answer. Live shows the answer only.
        if (!live)
        {
            ImGui.Spacing();
            UiHelpers.SectionHeader("Measured Envelope");

            UiHelpers.Muted(
                "Furthest pull recorded in each slice around the mob's facing. 0° is dead ahead, " +
                "180° is directly behind. This is the shape as observed — it is not forced to be a " +
                "cone or a circle, so a mob with a close all-round core and a longer forward reach " +
                "shows up as exactly that.");

            ImGui.Spacing();
            UiHelpers.ColoredWrapped(UiHelpers.Accent, AggroLearningStore.DescribeMeasuredShape(profile));
            ImGui.Spacing();

            AggroLearningStore.EnsureBins(profile);

            if (ImGui.BeginTable("##limlo-envelope", 5, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg))
            {
                ImGui.TableSetupColumn("Angle");
                ImGui.TableSetupColumn("Pulled at");
                ImGui.TableSetupColumn("Safe at");
                ImGui.TableSetupColumn("Best guess");
                ImGui.TableSetupColumn("Certainty");
                ImGui.TableHeadersRow();

                for (var bin = 0; bin < AggroLearningStore.AngleBins; bin++)
                {
                    var pulls = profile.BinSamples[bin];
                    var safe  = profile.BinMinSafeDistance[bin];

                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(AggroLearningStore.BinLabel(bin));

                    // Lower bound: it reached us from here.
                    ImGui.TableNextColumn();
                    if (pulls > 0) UiHelpers.Colored(UiHelpers.Good, $"{profile.BinMaxDistance[bin]:F1}y");
                    else           UiHelpers.Colored(UiHelpers.Dim, "-");

                    // Upper bound: we stood here unnoticed.
                    ImGui.TableNextColumn();
                    if (safe > 0f) UiHelpers.Colored(UiHelpers.Accent, $"<{safe:F1}y");
                    else           UiHelpers.Colored(UiHelpers.Dim, "-");

                    ImGui.TableNextColumn();
                    var guess = AggroLearningStore.RadiusAtAngle(profile, bin * AggroLearningStore.BinWidthDegrees + 1f);
                    ImGui.TextUnformatted(guess is { } g ? $"{g:F1}y" : "-");

                    ImGui.TableNextColumn();
                    if (AggroLearningStore.BinContradicts(profile, bin))
                        UiHelpers.Colored(UiHelpers.Warn, "conflicting");
                    else if (AggroLearningStore.BinUncertainty(profile, bin) is { } spread)
                        UiHelpers.Colored(spread <= 1.5f ? UiHelpers.Good : UiHelpers.Warn, $"+/- {spread / 2f:F1}y");
                    else if (pulls > 0 || safe > 0f)
                        UiHelpers.Colored(UiHelpers.Warn, "one-sided");
                    else
                        UiHelpers.Colored(UiHelpers.Bad, "none");
                }

                ImGui.EndTable();
            }

            UiHelpers.Muted(
                "A slice is pinned down once it has BOTH a pull (lower bound) and a safe stand " +
                "(upper bound) close together. \"Conflicting\" means a pull was recorded further out " +
                "than somewhere you later stood unnoticed — usually an old bad sample.");

            UiHelpers.Muted(
                $"{AggroLearningStore.FilledBins(profile)} of {AggroLearningStore.AngleBins} slices covered " +
                $"({AggroLearningStore.MinFilledBins} needed). Walk in from the sides and from behind to fill the gaps.");
        }
#endif

        ImGui.Spacing();
        UiHelpers.SectionHeader("Observed");

        if (ImGui.BeginTable("##limlo-mob-observed", 2, ImGuiTableFlags.SizingFixedFit))
        {
            UiHelpers.Row("Level", profile.Level > 0 ? profile.Level.ToString() : "(unknown)");
            UiHelpers.Row("Max HP", profile.MaxHp > 0 ? profile.MaxHp.ToString("N0") : "(unknown)");
            UiHelpers.Row("Hitbox radius", $"{profile.HitboxRadius:F2}y");
            UiHelpers.Row("Knowledge level", profile.ForayLevel > 0 ? profile.ForayLevel.ToString() : "(unknown)");
            UiHelpers.Row("Seen in", profile.TerritoryId switch
            {
                1252 => "South Horn",
                1346 => "North Horn",
                0    => "(unknown)",
                var t => $"Territory {t}",
            });
            ImGui.EndTable();
        }

        ImGui.Spacing();
        UiHelpers.SectionHeader("Game Data");

        if (row is { } bnpc)
        {
            if (ImGui.BeginTable("##limlo-mob-sheet", 2, ImGuiTableFlags.SizingFixedFit))
            {
                UiHelpers.Row("BNpcBase id", profile.BaseId.ToString());
                UiHelpers.Row("Rank", bnpc.Rank.ToString());
                UiHelpers.Row("Scale", $"{bnpc.Scale:F2}");
                UiHelpers.Row("Battalion", bnpc.Battalion.RowId.ToString());
                UiHelpers.Row("Link race", bnpc.LinkRace.RowId.ToString());
                UiHelpers.Row("Model", bnpc.ModelChara.RowId.ToString());
                UiHelpers.Row("Shows level", bnpc.IsDisplayLevel ? "Yes" : "No");
                UiHelpers.Row("Draws target line", bnpc.IsTargetLine ? "Yes" : "No");
                ImGui.EndTable();
            }
        }
        else
        {
            UiHelpers.Muted($"No BNpcBase row resolved for id {profile.BaseId}.");
        }

        UiHelpers.Muted(
            "No elemental weakness data exists for mobs. The only resistance-shaped sheet is " +
            "BNpcResist (11 unlabelled booleans, likely status immunities) and BNpcBase holds " +
            "no reference to it, so it cannot be linked to a mob.");

        ImGui.Spacing();
        ImGui.Separator();

#if !PUBLIC_BUILD
        if (!live && ImGui.Button($"Forget this mob's data###limlo-forget-{profile.BaseId}"))
        {
            _store.Forget(profile.BaseId);
            Plugin.SaveConfiguration();
        }
        if (!live)
            UiHelpers.HelpMarker("Clears every recorded pull for this mob so it can be re-measured from scratch.");
#endif

        if (_store.IgnoredCount > 0)
        {
            if (!live)
                ImGui.SameLine();
            if (ImGui.Button(live ? $"Unhide all ({_store.IgnoredCount})" : "Un-ignore all"))
            {
                _store.ClearIgnored();
                Plugin.SaveConfiguration();
            }
        }
    }
}
