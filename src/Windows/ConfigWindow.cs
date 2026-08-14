using System;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

using LimLoToolkit.Tools;

namespace LimLoToolkit.Windows;

/// <summary>Global options plus a per-tool on/off list.</summary>
public sealed class ConfigWindow : Window, IDisposable
{
    private readonly Configuration _config;
    private readonly ToolRegistry  _tools;
    private readonly Action        _saveConfig;

    public ConfigWindow(Configuration config, ToolRegistry tools, Action saveConfig)
        : base($"{Plugin.DisplayName} — Settings###LimLoToolkitConfig")
    {
        _config     = config;
        _tools      = tools;
        _saveConfig = saveConfig;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(400, 280),
            MaximumSize = new Vector2(900, 900),
        };
        Size          = new Vector2(460, 380);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose() { }

#if !PUBLIC_BUILD
    /// <summary>
    /// The Live Mode switch. Dev build only — the public build has no trainer
    /// compiled into it and is unconditionally live, so a switch there would be
    /// a control with one position.
    ///
    /// Deliberately the first thing in Settings and deliberately loud: the whole
    /// value of it is knowing at a glance which plugin you are looking at, and a
    /// quiet checkbox halfway down a list would leave you wondering why your
    /// training panel had vanished.
    /// </summary>
    private void DrawLiveModeSwitch()
    {
        var live = _config.LiveMode;

        var accent = live ? UiHelpers.Official : UiHelpers.Dim;
        var origin = ImGui.GetCursorScreenPos();
        var width  = ImGui.GetContentRegionAvail().X;

        ImGui.PushStyleColor(ImGuiCol.ChildBg,
            live ? new Vector4(0.16f, 0.11f, 0.24f, 1f) : new Vector4(0.11f, 0.12f, 0.14f, 1f));

        if (ImGui.BeginChild("##limlo-livemode", new Vector2(width, 76f), true))
        {
            ImGui.GetWindowDrawList().AddRectFilled(
                origin, origin + new Vector2(4f, 76f), ImGui.ColorConvertFloat4ToU32(accent));

            ImGui.Indent(6f);

            if (ImGui.Checkbox("LIVE MODE — see the plugin exactly as everyone else does", ref live))
            {
                _config.LiveMode = live;
                _saveConfig();
            }

            UiHelpers.Muted(live
                ? "ON. Data collection is stopped, the measurement panels are hidden, and only mobs "
                  + "with confirmed values are listed or drawn — the public build, from your dev copy."
                : "OFF. This is the dev build: data collection, measured values and lock controls are "
                  + "all available. Switch this on to check what your friends actually see.");

            ImGui.Unindent(6f);
        }

        ImGui.EndChild();
        ImGui.PopStyleColor();

        ImGui.Spacing();
    }
#endif

    public override void Draw()
    {
#if !PUBLIC_BUILD
        DrawLiveModeSwitch();
#endif

        UiHelpers.SectionHeader("General");

        var openOnLoad = _config.OpenOnLoad;
        if (ImGui.Checkbox("Open the toolkit window when the plugin loads", ref openOnLoad))
        {
            _config.OpenOnLoad = openOnLoad;
            _saveConfig();
        }

        ImGui.Spacing();
        UiHelpers.SectionHeader("World Overlays");
        UiHelpers.Muted("Things drawn on the world itself rather than in this window.");
        ImGui.Spacing();

        var outlines = _config.ShowMobOutlines;
        if (ImGui.Checkbox("Outline mobs (silhouettes)", ref outlines))
        {
            _config.ShowMobOutlines = outlines;
            _saveConfig();
        }
        UiHelpers.HelpMarker(
            "Outlines every nearby mob that CAN aggro you, in red, using the game's own silhouette " +
            "highlight — so it traces the actual model rather than a box. Mobs you outlevel get no " +
            "outline at all, because an outline means danger. This writes to the game's render " +
            "state rather than drawing an overlay, and every outline is removed again when you " +
            "switch it off or unload the plugin. Requires the Enemy Vision tool to be enabled, and " +
            "only applies in the Occult Crescent.");

        ImGui.SetNextItemWidth(200f);
        var thickness = Math.Clamp(_config.OverlayThickness,
                                   EnemyVisionTool.MinThickness, EnemyVisionTool.MaxThickness);
        if (ImGui.SliderFloat("Overlay line thickness", ref thickness,
                              EnemyVisionTool.MinThickness, EnemyVisionTool.MaxThickness, "%.1f px"))
        {
            _config.OverlayThickness = Math.Clamp(thickness,
                                                  EnemyVisionTool.MinThickness, EnemyVisionTool.MaxThickness);
            _saveConfig();
        }
        UiHelpers.HelpMarker(
            "Width of every line this plugin draws itself: detection cones and circles, coffer " +
            "lines, and the missing-angle wedges.\n\n" +
            "It does NOT change the mob silhouettes. Those are drawn by the game's own outline " +
            "pass and its width is not exposed anywhere — the only outline setting the game " +
            "publishes is an on/off for character outlines, not a thickness.");

        var headDistance = _config.ShowTargetDistanceOverHead;
        if (ImGui.Checkbox("Show distance to target above your head", ref headDistance))
        {
            _config.ShowTargetDistanceOverHead = headDistance;
            _saveConfig();
        }
        UiHelpers.HelpMarker(
            "Floats the live distance to whatever mob you have targeted over your character, " +
            "updated every frame. The big number is the hitbox-to-hitbox gap — the same thing every " +
            "range in this plugin means — with the raw centre-to-centre distance underneath.");

        if (_config.ShowTargetDistanceOverHead)
        {
            ImGui.Indent();
            var selectedOnly = _config.TargetDistanceSelectedMobOnly;
            if (ImGui.Checkbox("Only for the mob selected in Mob Viewer", ref selectedOnly))
            {
                _config.TargetDistanceSelectedMobOnly = selectedOnly;
                _saveConfig();
            }
            UiHelpers.HelpMarker(
                "Off, the readout appears for any mob you target. On, it appears only when the " +
                "targeted mob is the one highlighted in the Mob Viewer list. Either way the name " +
                "turns blue when the two match.");
            ImGui.Unindent();
        }

        ImGui.Spacing();
        UiHelpers.SectionHeader("Mob Viewer");
        UiHelpers.Muted("What the mob list shows. Both on keeps it to what is in front of you.");
        ImGui.Spacing();

        var nearbyOnly = _config.MobViewerNearbyOnly;
        if (ImGui.Checkbox("Nearby only", ref nearbyOnly))
        {
            _config.MobViewerNearbyOnly = nearbyOnly;
            _saveConfig();
        }
        UiHelpers.HelpMarker("List only mob types currently loaded around you.");

        var hideIrrelevant = _config.MobViewerHideIrrelevant;
        if (ImGui.Checkbox("Hide irrelevant", ref hideIrrelevant))
        {
            _config.MobViewerHideIrrelevant = hideIrrelevant;
            _saveConfig();
        }
        UiHelpers.HelpMarker("Hide mobs you have ignored, and ones too far below your Knowledge level to aggro.");

        ImGui.Spacing();
        UiHelpers.SectionHeader("Tools");
        UiHelpers.Muted(
            "Everything starts switched off. Turn a tool on to have it appear in the sidebar and "
            + "start running; switch it off again to stop it completely.");
        ImGui.Spacing();

        foreach (var tool in _tools.All)
        {
            var isEnabled = _config.IsToolEnabled(tool.Id);
            if (ImGui.Checkbox($"{tool.Name}###limlo-toggle-{tool.Id}", ref isEnabled))
            {
                _config.SetToolEnabled(tool.Id, isEnabled);
                _saveConfig();
            }

            if (!string.IsNullOrEmpty(tool.Description))
            {
                ImGui.Indent();
                UiHelpers.Muted(tool.Description);
                ImGui.Unindent();
            }

            ImGui.Spacing();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.PushStyleColor(ImGuiCol.Text, UiHelpers.Dim);
        ImGui.TextUnformatted($"{Plugin.DisplayName} v{Plugin.VersionString}");
        ImGui.PopStyleColor();
    }
}
