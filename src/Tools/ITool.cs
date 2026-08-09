using System;

namespace LimLoToolkit.Tools;

/// <summary>
/// One panel in the toolkit. Adding a feature means writing one of these and
/// registering it in <see cref="ToolRegistry"/> — nothing else in the plugin
/// needs to change.
///
/// Contract:
///  - <see cref="Draw"/> runs inside the main window's content pane, on the UI
///    thread, every frame the tool is selected. Do not block.
///  - <see cref="OnFrameworkUpdate"/> runs on the game thread every frame,
///    whether or not the tool is selected or the window is open. This is the
///    only safe place to touch game memory.
///  - <see cref="DrawOverlay"/> runs on the UI thread every frame regardless of
///    whether the window is open or this tool is selected. Use it for in-world
///    drawing that must keep working with the toolkit window closed. It must
///    check its own enabled/zone conditions — the host does not gate it.
///  - All three are called inside a try/catch by the host, but each tool should
///    still guard its own unsafe reads. A throwing tool gets logged, not
///    crashed — the game must never go down because a panel misbehaved.
/// </summary>
public interface ITool : IDisposable
{
    /// <summary>Stable identifier. Used as a config key — never rename it.</summary>
    string Id { get; }

    /// <summary>Display name shown in the sidebar.</summary>
    string Name { get; }

    /// <summary>One-line summary shown under the name in the settings list.</summary>
    string Description { get; }

    /// <summary>Sidebar grouping header.</summary>
    string Category { get; }

    /// <summary>Draw the tool's panel. Called only when this tool is selected.</summary>
    void Draw();

    /// <summary>Per-frame game-thread tick. Default: do nothing.</summary>
    void OnFrameworkUpdate() { }

    /// <summary>
    /// Per-frame in-world drawing, independent of window state. Default: do nothing.
    /// </summary>
    void DrawOverlay() { }

    /// <summary>Default: nothing to release.</summary>
    void IDisposable.Dispose() { }
}
