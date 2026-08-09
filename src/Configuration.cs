using System;
using System.Collections.Generic;

using Dalamud.Configuration;

namespace LimLoToolkit;

/// <summary>
/// Persisted settings. Written to
/// <c>%APPDATA%\XIVLauncher\pluginConfigs\LimLoToolkit.json</c> by Dalamud.
///
/// Keep every field defaulted so an older config file deserializes cleanly —
/// a missing property just keeps its initializer value.
/// </summary>
[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>Open the toolkit window automatically when the plugin loads.</summary>
    public bool OpenOnLoad { get; set; } = false;

    /// <summary>Id of the tool the sidebar should select on open.</summary>
    public string LastToolId { get; set; } = string.Empty;

    /// <summary>Per-tool enabled state, keyed by <see cref="Tools.ITool.Id"/>.</summary>
    public Dictionary<string, bool> EnabledTools { get; set; } = new();

    public bool IsToolEnabled(string id) =>
        !EnabledTools.TryGetValue(id, out var enabled) || enabled;

    public void SetToolEnabled(string id, bool enabled) =>
        EnabledTools[id] = enabled;
}
