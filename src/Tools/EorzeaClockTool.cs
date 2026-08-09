using System;

using Dalamud.Bindings.ImGui;

namespace LimLoToolkit.Tools;

/// <summary>
/// Eorzea clock, read from the game's own timekeeper rather than derived from
/// the wall clock.
///
/// <c>Framework.Instance()->ClientTime.EorzeaTime</c> is the number of Eorzea
/// seconds since the Eorzea epoch. From it:
///   hour   = (t / 3600) % 24
///   minute = (t / 60)   % 60
///   day    = (t / 86400) % 8   — the Eorzea week has eight days
/// One Eorzea hour is 175 real seconds, so weather windows (8 Eorzea hours)
/// are 1400 real seconds / ~23m20s long.
/// </summary>
public sealed class EorzeaClockTool : ITool
{
    public string Id          => "eorzea-clock";
    public string Name        => "Eorzea Clock";
    public string Description => "Current Eorzea time, day, and time until the next weather window.";
    public string Category    => "Info";

    private static readonly string[] EorzeanDays =
    [
        "Firesday", "Watersday", "Lightsday", "Windsday",
        "Iceday", "Lightningday", "Earthday", "Darksday",
    ];

    private long _eorzeaTimestamp;
    private bool _valid;

    public unsafe void OnFrameworkUpdate()
    {
        try
        {
            var fw = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance();
            if (fw == null)
            {
                _valid = false;
                return;
            }

            _eorzeaTimestamp = fw->ClientTime.EorzeaTime;
            _valid           = true;
        }
        catch (Exception ex)
        {
            _valid = false;
            Plugin.Log.Error(ex, "EorzeaClockTool failed to read the game clock.");
        }
    }

    public void Draw()
    {
        if (!_valid)
        {
            UiHelpers.Muted("The game clock is not available yet — this fills in once you are in-game.");
            return;
        }

        var ts     = _eorzeaTimestamp;
        var hour   = (int)(ts / 3600 % 24);
        var minute = (int)(ts / 60 % 60);
        var day    = EorzeanDays[(int)(ts / 86400 % 8)];

        UiHelpers.SectionHeader("Eorzea Time");

        ImGui.SetWindowFontScale(2.0f);
        ImGui.TextUnformatted($"{hour:00}:{minute:00}");
        ImGui.SetWindowFontScale(1.0f);

        UiHelpers.Muted(day);

        ImGui.Spacing();
        UiHelpers.SectionHeader("Weather Window");

        // Weather rolls over every 8 Eorzea hours: at 00:00, 08:00 and 16:00.
        var eorzeaSecondsIntoWindow = ts % (8 * 3600);
        var eorzeaSecondsRemaining  = 8 * 3600 - eorzeaSecondsIntoWindow;
        var realSecondsRemaining    = eorzeaSecondsRemaining * 175 / 3600;

        var windowStartHour = hour / 8 * 8;

        if (ImGui.BeginTable("##limlo-clock", 2, ImGuiTableFlags.SizingFixedFit))
        {
            UiHelpers.Row("Current window", $"{windowStartHour:00}:00 – {windowStartHour + 8:00}:00");
            UiHelpers.Row("Changes in",     $"{realSecondsRemaining / 60}m {realSecondsRemaining % 60}s (real time)");
            ImGui.EndTable();
        }

        ImGui.Spacing();
        UiHelpers.Muted("One Eorzea hour is 175 real seconds. A full Eorzea day is 70 real minutes.");
    }
}
