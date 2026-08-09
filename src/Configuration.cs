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

    // --- Aggro training ---

    /// <summary>
    /// Watch every nearby enemy each frame and record the geometry of any pull
    /// onto the player. Off by default — it keeps per-enemy history buffers, so
    /// there is no reason to run it once a mob is solved.
    /// </summary>
    public bool AggroTrainingEnabled { get; set; } = false;

    /// <summary>Draw measured ranges instead of the global slider where available.</summary>
    public bool UseLearnedAggroRanges { get; set; } = true;

    /// <summary>
    /// Echo every training decision to chat. On by default while training,
    /// because otherwise there is no way to tell whether a pull was recorded
    /// without staring at the panel.
    /// </summary>
    public bool AnnounceTrainingInChat { get; set; } = true;

    /// <summary>
    /// Everything measured so far, one entry per mob type. A plain list rather
    /// than a dictionary so it round-trips through config serialization without
    /// depending on non-string dictionary key support.
    /// </summary>
    public List<Tools.AggroProfile> LearnedAggro { get; set; } = new();

    /// <summary>
    /// Mob types the user has marked irrelevant, by <c>BNpcBase</c> id. These
    /// are skipped entirely: no shape drawn, no training samples taken. A list
    /// rather than a set, for the same serialization reason as above.
    /// </summary>
    public List<uint> IgnoredMobBaseIds { get; set; } = new();

    /// <summary>
    /// Ignore anything whose name does not start with
    /// <see cref="TrackedNamePrefix"/>. Every Occult Crescent field mob is
    /// named "Crescent something", so anything else is a summon, an add, or
    /// scenery — noise for both drawing and training.
    ///
    /// This is a live rule rather than entries written into
    /// <see cref="IgnoredMobBaseIds"/>: it stays reversible with one click and
    /// does not bloat the stored list with hundreds of ids.
    /// </summary>
    public bool AutoIgnoreNonMatchingNames { get; set; } = true;

    /// <summary>Name prefix a mob must carry to be tracked. Case-insensitive.</summary>
    public string TrackedNamePrefix { get; set; } = "Crescent";

    public bool IsToolEnabled(string id) =>
        !EnabledTools.TryGetValue(id, out var enabled) || enabled;

    public void SetToolEnabled(string id, bool enabled) =>
        EnabledTools[id] = enabled;
}
