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

    public ToolRegistry(Configuration config)
    {
        _config = config;

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

    public void Dispose()
    {
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
