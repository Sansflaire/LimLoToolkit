using System;

using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

using LimLoToolkit.Tools;
using LimLoToolkit.Windows;

namespace LimLoToolkit;

/// <summary>
/// Entry point. Owns the Dalamud services, the tool registry, and the windows.
///
/// STANDALONE: this plugin depends on nothing but Dalamud itself. It must keep
/// working with no other plugin installed — no PanacheUI, no HTTP bridge, no
/// sibling IPC. See CLAUDE.md.
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager         CommandManager  { get; private set; } = null!;
    [PluginService] internal static IPluginLog              Log             { get; private set; } = null!;
    [PluginService] internal static IFramework              Framework       { get; private set; } = null!;
    [PluginService] internal static IClientState            ClientState     { get; private set; } = null!;
    [PluginService] internal static IObjectTable            ObjectTable     { get; private set; } = null!;
    [PluginService] internal static ITargetManager          TargetManager   { get; private set; } = null!;
    [PluginService] internal static IDataManager            DataManager     { get; private set; } = null!;
    [PluginService] internal static ICondition              Condition       { get; private set; } = null!;
    [PluginService] internal static IChatGui                ChatGui         { get; private set; } = null!;
    [PluginService] internal static IGameGui                GameGui         { get; private set; } = null!;

    public const string DisplayName   = "LimLo Toolkit";
    public const string CommandMain   = "/limlo";
    public const string CommandConfig = "/limlocfg";

    public static string VersionString =>
        typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "unknown";

    private readonly Configuration _config;
    private readonly ToolRegistry  _tools;
    private readonly WindowSystem  _windows = new("LimLoToolkit");
    private readonly MainWindow    _mainWindow;
    private readonly ConfigWindow  _configWindow;

    public Plugin()
    {
        _instance = this;

        _config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        _tools  = new ToolRegistry(_config);

        _configWindow = new ConfigWindow(_config, _tools, SaveConfig);
        _mainWindow   = new MainWindow(_config, _tools, SaveConfig, OpenConfigUi);

        _windows.AddWindow(_mainWindow);
        _windows.AddWindow(_configWindow);

        CommandManager.AddHandler(CommandMain, new CommandInfo(OnMainCommand)
        {
            HelpMessage = $"Toggle the {DisplayName} window.",
        });
        CommandManager.AddHandler(CommandConfig, new CommandInfo(OnConfigCommand)
        {
            HelpMessage = $"Open {DisplayName} settings.",
        });

        Framework.Update                       += OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw         += DrawUi;
        PluginInterface.UiBuilder.OpenMainUi   += OpenMainUi;
        PluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;

        if (_config.OpenOnLoad)
            _mainWindow.IsOpen = true;

        Log.Information($"{DisplayName} v{VersionString} loaded with {_tools.All.Count} tool(s).");
    }

    public void Dispose()
    {
        Framework.Update                       -= OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw         -= DrawUi;
        PluginInterface.UiBuilder.OpenMainUi   -= OpenMainUi;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUi;

        CommandManager.RemoveHandler(CommandMain);
        CommandManager.RemoveHandler(CommandConfig);

        _windows.RemoveAllWindows();
        _mainWindow.Dispose();
        _configWindow.Dispose();
        _tools.Dispose();

        _instance = null;

        Log.Information($"{DisplayName} unloaded.");
    }

    private void OnMainCommand(string command, string args)
    {
        if (args.Trim().Equals("config", StringComparison.OrdinalIgnoreCase))
        {
            OpenConfigUi();
            return;
        }

        _mainWindow.Toggle();
    }

    private void OnConfigCommand(string command, string args) => OpenConfigUi();

    private void OpenMainUi()   => _mainWindow.IsOpen   = true;
    private void OpenConfigUi() => _configWindow.IsOpen = true;

    private void OnFrameworkUpdate(IFramework framework)
    {
        // Game-thread tick. Guarded so a misbehaving tool can never crash the
        // game — the registry isolates each tool individually as well.
        try
        {
            _tools.OnFrameworkUpdate();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Unhandled exception in the framework update loop.");
        }
    }

    private void DrawUi()
    {
        try
        {
            _windows.Draw();

            // In-world overlays draw whether or not the toolkit window is open,
            // so this must sit outside the WindowSystem.
            _tools.DrawOverlay();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Unhandled exception while drawing the UI.");
        }
    }

    private void SaveConfig() => PluginInterface.SavePluginConfig(_config);

    /// <summary>
    /// Lets a tool persist its own settings without having a save callback
    /// threaded through its constructor.
    /// </summary>
    public static void SaveConfiguration() => _instance?.SaveConfig();

    private static Plugin? _instance;
}
