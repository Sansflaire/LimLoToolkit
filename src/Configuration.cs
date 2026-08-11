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
    /// <summary>
    /// Config schema version. Bumped when a DEFAULT changes in a way that
    /// should also reach people whose file already has the old value written
    /// into it — see <see cref="Migrate"/>. A new field alone does not need a
    /// bump; a missing property already picks up its initializer.
    /// </summary>
    public const int CurrentVersion = 3;

    public int Version { get; set; } = 1;

    /// <summary>Open the toolkit window automatically when the plugin loads.</summary>
    public bool OpenOnLoad { get; set; } = false;

    /// <summary>
    /// Present the plugin exactly as the public build does: no data collection,
    /// no measured values, no lock controls, and only mobs with locked values
    /// shown at all.
    ///
    /// Dev build only — the public build has no trainer compiled in, so it is
    /// unconditionally live. See <see cref="BuildFlavor"/>.
    /// </summary>
    public bool LiveMode { get; set; } = false;

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

    /// <summary>
    /// Lay the detection shapes on the terrain rather than drawing them flat at
    /// the enemy's own height, so a ring on a slope follows the slope.
    ///
    /// Sampled with the game's own background collision, cached per mob and
    /// rebuilt only when one moves — see <see cref="Tools.GroundSampler"/>.
    ///
    /// **Off by default.** Flat is the long-standing look and the one that costs
    /// nothing; ground-following is the addition, and an addition does not get
    /// to change what people already see without being asked for. It shipped on
    /// by default in 0.21.0.0, which was wrong — see <see cref="Migrate"/>.
    /// </summary>
    public bool FollowGroundMesh { get; set; } = false;

    /// <summary>
    /// Outline mobs in the world with the game's own silhouette highlight —
    /// red for anything that can aggro, black for anything that cannot.
    ///
    /// Off by default. It writes to game render state rather than drawing an
    /// overlay, so it stays opt-in.
    /// </summary>
    public bool ShowMobOutlines { get; set; } = false;

    /// <summary>
    /// Line thickness in pixels for every shape THIS PLUGIN draws itself —
    /// detection cones and circles, missing-angle wedges, coffer lines.
    ///
    /// It does NOT affect the mob silhouettes. Those are rendered by the game's
    /// own outline pass and its width is not exposed by any published struct;
    /// the only outline-related knob the game offers is
    /// <c>GraphicsConfig.CharaOutline</c>, which is a bool. See
    /// docs/enemy-vision.md.
    /// </summary>
    public float OverlayThickness { get; set; } = 2.5f;

    // --- Minimap Radar ---

    /// <summary>Draw a dot on the game's minimap for each nearby mob.</summary>
    public bool ShowMinimapRadar { get; set; } = true;

    /// <summary>Dot radius in pixels.</summary>
    public float MinimapDotSize { get; set; } = 4f;

    /// <summary>Pin mobs beyond the minimap's edge to the rim as hollow rings.</summary>
    public bool MinimapShowOffEdge { get; set; } = true;

    /// <summary>Include mobs the player outlevels, drawn dimmer.</summary>
    public bool MinimapShowHarmlessMobs { get; set; } = false;

    /// <summary>How far inside the minimap's edge the rim sits, in pixels.</summary>
    public float MinimapEdgeInset { get; set; } = 8f;

    /// <summary>
    /// Correction for the minimap rotation. The centre and the scale are read
    /// from the game's own marker fields and are certain; the rotation had to be
    /// derived from <c>Camera.DirH</c>, whose convention could not be confirmed
    /// without the game running. See MinimapRadarTool.
    /// </summary>
    public float MinimapRotationOffsetDegrees { get; set; } = 0f;

    /// <summary>Flips east/west, for the same reason as the offset above.</summary>
    public bool MinimapMirror { get; set; } = false;

    // --- Mob Viewer ---

    /// <summary>
    /// Float the live distance to the current target above the player's head,
    /// refreshed every frame. On by default — it only appears while a mob is
    /// actually targeted, so it costs nothing the rest of the time.
    /// </summary>
    public bool ShowTargetDistanceOverHead { get; set; } = true;

    /// <summary>
    /// Show the readout only when the target is the mob selected in Mob Viewer.
    /// On by default — the readout exists to serve the mob being studied, and
    /// firing it at every passing target is noise.
    /// </summary>
    public bool TargetDistanceSelectedMobOnly { get; set; } = true;

    // --- Mob Viewer list filters ---

    /// <summary>Show only mob types currently in the object table.</summary>
    public bool MobViewerNearbyOnly { get; set; } = true;

    /// <summary>Hide ignored and outlevelled mobs from the list.</summary>
    public bool MobViewerHideIrrelevant { get; set; } = true;

    // --- Aggro training ---

    /// <summary>
    /// Master switch for ALL data collection: pulls, non-detections, and mob
    /// locations. Nothing is recorded or updated while this is off — the plugin
    /// only draws what it already knows.
    ///
    /// Off by default. It keeps per-enemy history buffers while running, and
    /// once a mob is solved or its values are locked there is nothing left to
    /// gather.
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
    /// Ignore anything whose name does not contain
    /// <see cref="TrackedNamePrefix"/>. Every Occult Crescent field mob carries
    /// "Crescent" somewhere in its name, so anything else is a summon, an add,
    /// or scenery — noise for both drawing and training.
    ///
    /// This is a live rule rather than entries written into
    /// <see cref="IgnoredMobBaseIds"/>: it stays reversible with one click and
    /// does not bloat the stored list with hundreds of ids.
    /// </summary>
    public bool AutoIgnoreNonMatchingNames { get; set; } = true;

    /// <summary>
    /// Text a mob's name must contain to be tracked. Case-insensitive, and a
    /// CONTAINS test rather than a prefix — "Bird of the Crescent" is a real
    /// Occult Crescent enemy and is the one name of 129 that does not lead with
    /// the word.
    /// </summary>
    public string TrackedNamePrefix { get; set; } = "Crescent";

    /// <summary>
    /// Hide and skip enemies whose Knowledge level is below the player's, since
    /// the Occult Crescent suppresses their aggro entirely.
    ///
    /// This is a data-integrity guard as much as a decluttering one: an enemy
    /// that cannot aggro would otherwise generate an endless stream of "stood
    /// next to it unnoticed" observations, teaching the model that its
    /// detection range is tiny when in truth it is only level-suppressed.
    /// </summary>
    public bool IgnoreOutleveledEnemies { get; set; } = true;

    /// <summary>
    /// How far above an enemy's Knowledge level the player must be before it is
    /// treated as harmless. The Crescent stops aggro at 1 level above, so 1 is
    /// the correct default; raise it to be cautious.
    /// </summary>
    public int OutlevelMargin { get; set; } = 1;

    /// <summary>
    /// Brings a config file written by an older version up to date where a
    /// changed DEFAULT would otherwise be masked by the old value already
    /// sitting in the file.
    ///
    /// Returns true if anything changed, so the caller can save.
    /// </summary>
    public bool Migrate()
    {
        if (Version >= CurrentVersion)
            return false;

        // v1 -> v2: the head readout shipped defaulting to "any target". It is
        // meant to serve the mob being studied, so it now defaults to the
        // selected mob and existing files are moved with it.
        if (Version < 2)
            TargetDistanceSelectedMobOnly = true;

        // v2 -> v3: ground-following shipped switched ON in 0.21.0.0. A new way
        // of drawing does not get to change what people already see without
        // being asked for, so it is opt-in and anyone who picked it up by
        // default is put back. This overrides a stored value deliberately: the
        // setting existed for a matter of minutes and nobody chose `true` — they
        // were given it. Turning it back on afterwards sticks, because the
        // migration runs once and the version is saved immediately.
        if (Version < 3)
            FollowGroundMesh = false;

        Version = CurrentVersion;
        return true;
    }

    public bool IsToolEnabled(string id) =>
        !EnabledTools.TryGetValue(id, out var enabled) || enabled;

    public void SetToolEnabled(string id, bool enabled) =>
        EnabledTools[id] = enabled;
}
