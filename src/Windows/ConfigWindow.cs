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

    public override void Draw()
    {
        UiHelpers.SectionHeader("General");

        var openOnLoad = _config.OpenOnLoad;
        if (ImGui.Checkbox("Open the toolkit window when the plugin loads", ref openOnLoad))
        {
            _config.OpenOnLoad = openOnLoad;
            _saveConfig();
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
        UiHelpers.Muted("Switch a tool off to hide it from the sidebar and stop it from running.");
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
