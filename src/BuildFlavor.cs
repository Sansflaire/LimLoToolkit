namespace LimLoToolkit;

/// <summary>
/// Which of the two builds this DLL is, and whether the UI should currently
/// present itself as the public one.
///
/// **Public build** (Release, what CI ships to the pluginmaster link). The
/// aggro trainer is removed from compilation entirely and every call site is
/// behind <c>#if !PUBLIC_BUILD</c>. There is no data collection, no measured
/// values, no lock controls — only mobs whose values were locked by the author
/// are shown, and they are shown as settled facts.
///
/// **Dev build** (Debug, what lands in devPlugins). Everything compiled, plus
/// <see cref="Configuration.LiveMode"/> — a runtime switch that makes the UI
/// behave exactly like the public build without rebuilding, so the shipped
/// experience can be checked from the dev plugin.
///
/// <see cref="IsLive"/> is the single predicate the UI asks. Never test
/// <c>#if PUBLIC_BUILD</c> in UI code to decide what to *draw* — use this, or
/// Live Mode will not preview correctly. Use <c>#if</c> only to keep code out
/// of the public binary.
/// </summary>
public static class BuildFlavor
{
#if PUBLIC_BUILD
    /// <summary>True when the trainer is compiled into this DLL.</summary>
    public const bool HasTraining = false;

    public const string Name = "public";

    /// <summary>Always live: there is nothing else for the public build to be.</summary>
    public static bool IsLive => true;
#else
    public const bool HasTraining = true;

    public const string Name = "dev";

    /// <summary>
    /// Follows the Live Mode switch. Defaults to off so the dev build opens
    /// with its tools in reach.
    /// </summary>
    public static bool IsLive => Plugin.Config?.LiveMode ?? false;
#endif

    /// <summary>Inverse of <see cref="IsLive"/>, for readability at call sites.</summary>
    public static bool ShowTrainingUi => HasTraining && !IsLive;

    /// <summary>Version suffix so a screenshot says which build it came from.</summary>
    public static string VersionSuffix => HasTraining ? (IsLive ? " (dev, Live Mode)" : " (dev)") : string.Empty;
}
