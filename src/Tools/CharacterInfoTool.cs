using System;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;

using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace LimLoToolkit.Tools;

/// <summary>
/// Live readout of the local character and the current zone.
///
/// This is also the reference implementation of the tool pattern: all game
/// memory is read on the framework thread into <see cref="_snapshot"/>, and
/// <see cref="Draw"/> only ever renders that plain-old-data struct. Copy this
/// shape for any tool that touches the game.
/// </summary>
public sealed class CharacterInfoTool : ITool
{
    public string Id          => "character-info";
    public string Name        => "Character";
    public string Description => "Live readout of your character, zone, and current target.";
    public string Category    => "Info";

    private ExcelSheet<ClassJob>?      _classJobSheet;
    private ExcelSheet<TerritoryType>? _territorySheet;

    private Snapshot _snapshot;

    private struct Snapshot
    {
        public bool    LoggedIn;
        public string  PlayerName;
        public string  HomeWorld;
        public string  CurrentWorld;
        public string  JobName;
        public string  JobAbbreviation;
        public byte    Level;
        public uint    CurrentHp;
        public uint    MaxHp;
        public uint    CurrentMp;
        public uint    MaxMp;
        public string  ZoneName;
        public ushort  TerritoryId;
        public Vector3 Position;
        public float   Rotation;
        public bool    InCombat;
        public bool    Mounted;
        public string  TargetName;
    }

    public void OnFrameworkUpdate()
    {
        var snap = new Snapshot
        {
            PlayerName   = string.Empty,
            HomeWorld    = string.Empty,
            CurrentWorld = string.Empty,
            JobName      = string.Empty,
            JobAbbreviation = string.Empty,
            ZoneName     = string.Empty,
            TargetName   = string.Empty,
        };

        try
        {
            snap.LoggedIn = Plugin.ClientState.IsLoggedIn;

            var player = Plugin.ObjectTable.LocalPlayer;
            if (snap.LoggedIn && player != null)
            {
                snap.PlayerName = player.Name.ToString();
                snap.Level      = player.Level;
                snap.CurrentHp  = player.CurrentHp;
                snap.MaxHp      = player.MaxHp;
                snap.CurrentMp  = player.CurrentMp;
                snap.MaxMp      = player.MaxMp;
                snap.Position   = player.Position;
                snap.Rotation   = player.Rotation;

                if (player is IPlayerCharacter pc)
                {
                    snap.HomeWorld    = pc.HomeWorld.ValueNullable?.Name.ToString()    ?? string.Empty;
                    snap.CurrentWorld = pc.CurrentWorld.ValueNullable?.Name.ToString() ?? string.Empty;
                }

                _classJobSheet ??= Plugin.DataManager.GetExcelSheet<ClassJob>();
                var jobRow = _classJobSheet?.GetRowOrDefault(player.ClassJob.RowId);
                snap.JobName         = jobRow?.Name.ToString()         ?? string.Empty;
                snap.JobAbbreviation = jobRow?.Abbreviation.ToString() ?? string.Empty;
            }

            snap.TerritoryId = (ushort)Plugin.ClientState.TerritoryType;
            _territorySheet ??= Plugin.DataManager.GetExcelSheet<TerritoryType>();
            snap.ZoneName = _territorySheet?.GetRowOrDefault(snap.TerritoryId)?
                                .PlaceName.ValueNullable?.Name.ToString() ?? string.Empty;

            snap.InCombat = Plugin.Condition[ConditionFlag.InCombat];
            snap.Mounted  = Plugin.Condition[ConditionFlag.Mounted];

            snap.TargetName = Plugin.TargetManager.Target?.Name.ToString() ?? string.Empty;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "CharacterInfoTool failed to build its snapshot.");
        }

        _snapshot = snap;
    }

    public void Draw()
    {
        var s = _snapshot;

        if (!s.LoggedIn || string.IsNullOrEmpty(s.PlayerName))
        {
            UiHelpers.Muted("Not logged in. Log into a character to see live data here.");
            return;
        }

        UiHelpers.SectionHeader("Character");
        if (ImGui.BeginTable("##limlo-char", 2, ImGuiTableFlags.SizingFixedFit))
        {
            UiHelpers.Row("Name",  s.PlayerName);
            UiHelpers.Row("World", FormatWorld(s.HomeWorld, s.CurrentWorld));
            UiHelpers.Row("Job",   string.IsNullOrEmpty(s.JobAbbreviation)
                                       ? $"Level {s.Level}"
                                       : $"{s.JobName} ({s.JobAbbreviation}) — Level {s.Level}");
            UiHelpers.Row("HP",    $"{s.CurrentHp:N0} / {s.MaxHp:N0}");
            if (s.MaxMp > 0)
                UiHelpers.Row("MP", $"{s.CurrentMp:N0} / {s.MaxMp:N0}");
            ImGui.EndTable();
        }

        ImGui.Spacing();
        UiHelpers.SectionHeader("Location");
        if (ImGui.BeginTable("##limlo-loc", 2, ImGuiTableFlags.SizingFixedFit))
        {
            UiHelpers.Row("Zone",     string.IsNullOrEmpty(s.ZoneName) ? "(unknown)" : s.ZoneName);
            UiHelpers.Row("Zone ID",  s.TerritoryId.ToString());
            UiHelpers.Row("Position", $"X {s.Position.X:F2}   Y {s.Position.Y:F2}   Z {s.Position.Z:F2}");
            UiHelpers.Row("Facing",   $"{s.Rotation * 180f / MathF.PI:F1}°");
            ImGui.EndTable();
        }

        if (ImGui.Button("Copy position"))
            ImGui.SetClipboardText($"{s.Position.X:F3}, {s.Position.Y:F3}, {s.Position.Z:F3}");

        ImGui.Spacing();
        UiHelpers.SectionHeader("Status");
        if (ImGui.BeginTable("##limlo-status", 2, ImGuiTableFlags.SizingFixedFit))
        {
            UiHelpers.Row("In combat", s.InCombat ? "Yes" : "No");
            UiHelpers.Row("Mounted",   s.Mounted  ? "Yes" : "No");
            UiHelpers.Row("Target",    string.IsNullOrEmpty(s.TargetName) ? "(none)" : s.TargetName);
            ImGui.EndTable();
        }
    }

    private static string FormatWorld(string home, string current)
    {
        if (string.IsNullOrEmpty(home) && string.IsNullOrEmpty(current))
            return "(unknown)";
        if (string.IsNullOrEmpty(current) || home == current)
            return home;
        return $"{current} (visiting from {home})";
    }
}
