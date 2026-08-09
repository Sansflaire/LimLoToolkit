using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

using LimLoToolkit.Tools;

namespace LimLoToolkit.Windows;

/// <summary>
/// The toolkit shell: a sidebar listing every enabled tool, and a content pane
/// that draws whichever one is selected. Tools never know about this class —
/// they just implement <see cref="ITool"/>.
/// </summary>
public sealed class MainWindow : Window, IDisposable
{
    private const float SidebarWidth = 168f;

    private readonly Configuration _config;
    private readonly ToolRegistry  _tools;
    private readonly Action        _saveConfig;
    private readonly Action        _openSettings;

    private string _selectedId;

    public MainWindow(Configuration config, ToolRegistry tools, Action saveConfig, Action openSettings)
        : base($"{Plugin.DisplayName}###LimLoToolkitMain")
    {
        _config       = config;
        _tools        = tools;
        _saveConfig   = saveConfig;
        _openSettings = openSettings;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(520, 340),
            MaximumSize = new Vector2(1600, 1200),
        };
        Size          = new Vector2(660, 460);
        SizeCondition = ImGuiCond.FirstUseEver;

        _selectedId = _config.LastToolId;
    }

    public void Dispose() { }

    public override void Draw()
    {
        var enabled = _tools.Enabled.ToList();

        if (enabled.Count == 0)
        {
            UiHelpers.Muted("Every tool is switched off. Turn one back on in Settings.");
            ImGui.Spacing();
            if (ImGui.Button("Open Settings"))
                _openSettings();
            return;
        }

        // Keep the selection valid across config changes and tool removals.
        var selected = enabled.FirstOrDefault(t => t.Id == _selectedId) ?? enabled[0];
        if (selected.Id != _selectedId)
            SelectTool(selected.Id);

        DrawSidebar(enabled, selected);

        ImGui.SameLine();

        if (ImGui.BeginChild("##limlo-content", Vector2.Zero, true))
        {
            try
            {
                selected.Draw();
            }
            catch (Exception ex)
            {
                // A broken panel must not take the window — or the game — down.
                Plugin.Log.Error(ex, $"Tool '{selected.Id}' threw while drawing.");
                ImGui.PushStyleColor(ImGuiCol.Text, UiHelpers.Warn);
                ImGui.TextWrapped($"This tool hit an error and stopped drawing:\n{ex.Message}");
                ImGui.PopStyleColor();
            }
        }

        ImGui.EndChild();
    }

    private void DrawSidebar(IReadOnlyList<ITool> enabled, ITool selected)
    {
        if (ImGui.BeginChild("##limlo-sidebar", new Vector2(SidebarWidth, 0), true))
        {
            // GroupBy keeps categories in first-registration order and tools in
            // registration order within each. Grouping rather than tracking the
            // previous category means an out-of-order registration can never
            // emit the same header twice.
            var first = true;

            foreach (var category in enabled.GroupBy(t => t.Category))
            {
                if (!first)
                    ImGui.Spacing();
                first = false;

                ImGui.PushStyleColor(ImGuiCol.Text, UiHelpers.Dim);
                ImGui.TextUnformatted(category.Key.ToUpperInvariant());
                ImGui.PopStyleColor();
                ImGui.Separator();

                foreach (var tool in category)
                {
                    if (ImGui.Selectable($"{tool.Name}###limlo-nav-{tool.Id}", tool.Id == selected.Id))
                        SelectTool(tool.Id);

                    if (ImGui.IsItemHovered() && !string.IsNullOrEmpty(tool.Description))
                        ImGui.SetTooltip(tool.Description);
                }
            }

            // Settings sits at the bottom of the sidebar, out of the tool flow.
            var buttonHeight = ImGui.GetFrameHeight();
            var remaining    = ImGui.GetContentRegionAvail().Y - buttonHeight;
            if (remaining > 0)
                ImGui.Dummy(new Vector2(0, remaining));

            if (ImGui.Button("Settings", new Vector2(-1, buttonHeight)))
                _openSettings();
        }

        ImGui.EndChild();
    }

    private void SelectTool(string id)
    {
        _selectedId        = id;
        _config.LastToolId = id;
        _saveConfig();
    }
}
