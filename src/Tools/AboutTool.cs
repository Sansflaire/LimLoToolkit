using System;

using Dalamud.Bindings.ImGui;

namespace LimLoToolkit.Tools;

/// <summary>Version info, commands, and a pointer at how to add more tools.</summary>
public sealed class AboutTool : ITool
{
    public string Id          => "about";
    public string Name        => "About";
    public string Description => "Version, commands, and where to find help.";
    public string Category    => "Info";

    public void Draw()
    {
        UiHelpers.SectionHeader(Plugin.DisplayName);
        UiHelpers.Muted($"Version {Plugin.VersionString}");

        ImGui.Spacing();
        ImGui.TextWrapped(
            "A standalone collection of small quality-of-life tools for FFXIV. " +
            "Everything here runs on its own — no other plugins required.");

        ImGui.Spacing();
        UiHelpers.SectionHeader("Commands");
        if (ImGui.BeginTable("##limlo-cmds", 2, ImGuiTableFlags.SizingFixedFit))
        {
            UiHelpers.Row(Plugin.CommandMain,   "Toggle the toolkit window");
            UiHelpers.Row(Plugin.CommandConfig, "Open settings");
            ImGui.EndTable();
        }

        ImGui.Spacing();
        UiHelpers.SectionHeader("Reloading after an update");
        ImGui.TextWrapped(
            "Open /xlplugins, find LimLo Toolkit, and toggle it off and back on. " +
            "There is no need to restart the game.");
    }
}
