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

    // --- Coffer Lines (Occult Crescent) ---

    /// <summary>Draw a brown line from the player to nearby bronze coffers.</summary>
    public bool DrawLineToBronzeCoffers { get; set; } = true;

    /// <summary>Draw a silver line from the player to nearby silver coffers.</summary>
    public bool DrawLineToSilverCoffers { get; set; } = true;

    /// <summary>
    /// Automatically target and open a coffer once the player walks within
    /// <see cref="AutoOpenDistance"/> of it. Off by default — this is
    /// automation and the user opts in deliberately.
    /// </summary>
    public bool AutoOpenCoffers { get; set; } = false;

    /// <summary>
    /// Range in yalms at which auto-open fires. Clamped to the game's own
    /// interact limit; beyond roughly 2.75y the client refuses the interact.
    /// </summary>
    public float AutoOpenDistance { get; set; } = 2.0f;

    // --- Enemy Vision (Occult Crescent) ---

    /// <summary>Draw the detection shape for sight-based (cone) enemies.</summary>
    public bool ShowSightEnemyVision { get; set; } = true;

    /// <summary>Draw the detection shape for sound-based (omnidirectional) enemies.</summary>
    public bool ShowSoundEnemyVision { get; set; } = true;

    /// <summary>
    /// Assumed detection distance in yalms, measured from the enemy's hitbox
    /// edge. THIS IS AN ESTIMATE — the game does not publish aggro range
    /// anywhere in its data files, so this is a tunable guess, not a fact.
    /// See docs/enemy-vision.md.
    /// </summary>
    public float EnemyVisionRadius { get; set; } = 12.0f;

    /// <summary>Width of the sight cone in degrees. Also an estimate.</summary>
    public float EnemyVisionConeDegrees { get; set; } = 90.0f;

    /// <summary>Recolour a shape when the player is standing inside it.</summary>
    public bool HighlightEnemyVisionWhenInside { get; set; } = true;

    public bool IsToolEnabled(string id) =>
        !EnabledTools.TryGetValue(id, out var enabled) || enabled;

    public void SetToolEnabled(string id, bool enabled) =>
        EnabledTools[id] = enabled;
}
