using System;
using System.Collections.Generic;
using System.Linq;

namespace LimLoToolkit.Tools;

/// <summary>
/// Owns every <see cref="ITool"/> instance and fans frame callbacks out to them.
///
/// TO ADD A TOOL: write a class implementing <see cref="ITool"/> under
/// <c>src/Tools/</c>, then add one line to the constructor below. That is the
/// whole registration story — the sidebar, settings list, and framework tick
/// all read from this registry.
/// </summary>
public sealed class ToolRegistry : IDisposable
{
    private readonly List<ITool> _tools = new();
    private readonly Configuration _config;

    /// <summary>Held so training data can be flushed on unload.</summary>
    private readonly AggroLearningStore _aggroStore;

    public ToolRegistry(Configuration config)
    {
        _config = config;

        // The learned-aggro table and its capture side are shared: the vision
        // tool feeds the trainer and reads the store, the viewer browses it.
        var store = new AggroLearningStore(config, Plugin.PluginInterface.GetPluginConfigDirectory());

        _aggroStore = store;

        // Registration order is display order. Real tools first, info last.
        Register(new OccultCofferLinesTool(config));
#if PUBLIC_BUILD
        Register(new EnemyVisionTool(config, store));
#else
        Register(new EnemyVisionTool(config, store, new AggroTrainer(store, config)));
#endif
        Register(new MinimapRadarTool(config, store));
        Register(new MobViewerTool(config, store));

        Register(new CharacterInfoTool());
        Register(new EorzeaClockTool());
        Register(new AboutTool());
    }

    private void Register(ITool tool)
    {
        if (_tools.Any(t => t.Id == tool.Id))
        {
            Plugin.Log.Error($"Duplicate tool id '{tool.Id}' — ignoring the second registration.");
            tool.Dispose();
            return;
        }

        _tools.Add(tool);
    }

    /// <summary>Every registered tool, including disabled ones.</summary>
    public IReadOnlyList<ITool> All => _tools;

    /// <summary>Tools the user has left switched on, in registration order.</summary>
    public IEnumerable<ITool> Enabled => _tools.Where(t => _config.IsToolEnabled(t.Id));

    public ITool? FindById(string id) => _tools.FirstOrDefault(t => t.Id == id);

    /// <summary>
    /// Ticks every enabled tool on the game thread. Each tool is isolated —
    /// one throwing does not stop the others or take the game down.
    /// </summary>
    public void OnFrameworkUpdate()
    {
        foreach (var tool in _tools)
        {
            if (!_config.IsToolEnabled(tool.Id))
                continue;

            try
            {
                tool.OnFrameworkUpdate();
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, $"Tool '{tool.Id}' threw during framework update.");
            }
        }
    }

    /// <summary>
    /// Draws every enabled tool's in-world overlay on the UI thread, whether or
    /// not the toolkit window is open. Isolated per tool, same as the tick.
    /// </summary>
    public void DrawOverlay()
    {
        foreach (var tool in _tools)
        {
            if (!_config.IsToolEnabled(tool.Id))
                continue;

            try
            {
                tool.DrawOverlay();
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, $"Tool '{tool.Id}' threw while drawing its overlay.");
            }
        }
    }

    public void Dispose()
    {
        // Belt and braces: samples are already written as they land, but a
        // reload must never be the thing that loses measurements.
        try
        {
            _aggroStore.Save();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Failed to flush aggro training data on unload.");
        }

        foreach (var tool in _tools)
        {
            try
            {
                tool.Dispose();
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, $"Tool '{tool.Id}' threw during dispose.");
            }
        }

        _tools.Clear();
    }
}
