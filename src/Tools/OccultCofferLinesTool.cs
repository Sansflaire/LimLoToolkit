using System;
using System.Collections.Generic;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;

using Lumina.Excel;

using XIVTreasure = Lumina.Excel.Sheets.Treasure;

namespace LimLoToolkit.Tools;

/// <summary>
/// Draws a line from the player to nearby treasure coffers inside the Occult
/// Crescent, colour-coded by coffer grade.
///
/// Mechanisms (verified against OhKannaDuh/BOCCHI's implementation, which is
/// the reference for this behaviour — see docs/occult-crescent.md):
///
///  - Coffers are <see cref="ObjectKind.Treasure"/> entries in the object table.
///  - Grade comes from the <c>Treasure</c> Excel sheet row addressed by the
///    object's <c>BaseId</c>: its <c>SGB</c> row id is 1596 for bronze and
///    1597 for silver. Anything else is an unrelated chest and is ignored.
///  - Lines are suppressed in combat, matching BOCCHI ("while out of combat").
///  - The whole tool is inert outside the Occult Crescent territories.
///
/// This is an independent implementation. BOCCHI renders through Pictomancy;
/// this uses Dalamud's own <c>WorldToScreen</c> plus an ImGui foreground draw
/// list, so the plugin stays a single DLL with no extra dependencies. The
/// tradeoff is that a segment whose endpoint leaves the screen is skipped
/// rather than clipped to the viewport edge.
/// </summary>
public sealed class OccultCofferLinesTool : ITool
{
    public string Id          => "occult-coffer-lines";
    public string Name        => "Coffer Lines";
    public string Description => "Lines to nearby bronze and silver coffers in the Occult Crescent.";
    public string Category    => "Toolkit";

    /// <summary>Occult Crescent territories. The tool does nothing anywhere else.</summary>
    private const ushort TerritorySouthHorn = 1252;
    private const ushort TerritoryNorthHorn = 1346;

    /// <summary>`Treasure.SGB` row ids that identify coffer grade.</summary>
    private const uint SgbBronzeCoffer = 1596;
    private const uint SgbSilverCoffer = 1597;

    private static readonly Vector4 BronzeColor = new(0.72f, 0.45f, 0.20f, 1.00f);
    private static readonly Vector4 SilverColor = new(0.82f, 0.84f, 0.88f, 1.00f);

    private const float LineThickness = 3f;

    private enum CofferGrade
    {
        Bronze,
        Silver,
    }

    private readonly struct Coffer(Vector3 position, CofferGrade grade, float distance)
    {
        public Vector3     Position { get; } = position;
        public CofferGrade Grade    { get; } = grade;
        public float       Distance { get; } = distance;
    }

    private readonly Configuration _config;

    private ExcelSheet<XIVTreasure>? _treasureSheet;

    // Written on the framework thread, read on the UI thread. Reference
    // assignment is atomic, so the overlay always sees a complete list.
    private List<Coffer> _coffers = new();
    private Vector3      _origin;
    private bool         _inOccultCrescent;
    private bool         _inCombat;

    public OccultCofferLinesTool(Configuration config) => _config = config;

    private static bool IsOccultCrescent(ushort territory) =>
        territory is TerritorySouthHorn or TerritoryNorthHorn;

    public void OnFrameworkUpdate()
    {
        var found = new List<Coffer>();

        try
        {
            _inOccultCrescent = IsOccultCrescent((ushort)Plugin.ClientState.TerritoryType);
            _inCombat         = Plugin.Condition[ConditionFlag.InCombat];

            if (!_inOccultCrescent)
            {
                _coffers = found;
                return;
            }

            var player = Plugin.ObjectTable.LocalPlayer;
            if (player == null)
            {
                _coffers = found;
                return;
            }

            _origin = player.Position;

            _treasureSheet ??= Plugin.DataManager.GetExcelSheet<XIVTreasure>();

            foreach (var obj in Plugin.ObjectTable)
            {
                if (obj.ObjectKind != ObjectKind.Treasure)
                    continue;

                // An opened or despawning coffer stops being targetable — that is
                // how BOCCHI drops it from the list, and it is the only signal
                // available without reading the Treasure struct's flags.
                if (!obj.IsValid() || obj.IsDead || !obj.IsTargetable)
                    continue;

                var sgb = _treasureSheet?.GetRowOrDefault(obj.BaseId)?.SGB.RowId ?? 0u;

                CofferGrade grade;
                switch (sgb)
                {
                    case SgbBronzeCoffer: grade = CofferGrade.Bronze; break;
                    case SgbSilverCoffer: grade = CofferGrade.Silver; break;
                    default: continue;
                }

                found.Add(new Coffer(obj.Position, grade, Vector3.Distance(_origin, obj.Position)));
            }

            found.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "OccultCofferLinesTool failed to scan for coffers.");
        }

        _coffers = found;
    }

    public void DrawOverlay()
    {
        if (!_inOccultCrescent || _inCombat)
            return;

        if (!_config.DrawLineToBronzeCoffers && !_config.DrawLineToSilverCoffers)
            return;

        var coffers = _coffers;
        if (coffers.Count == 0)
            return;

        // If the player themselves is not on screen there is nothing to anchor
        // the lines to — WorldToScreen would hand back a garbage origin.
        if (!Plugin.GameGui.WorldToScreen(_origin, out var originScreen))
            return;

        var drawList = ImGui.GetForegroundDrawList();
        var bronze   = ImGui.ColorConvertFloat4ToU32(BronzeColor);
        var silver   = ImGui.ColorConvertFloat4ToU32(SilverColor);

        foreach (var coffer in coffers)
        {
            if (!IsGradeEnabled(coffer.Grade))
                continue;

            if (!Plugin.GameGui.WorldToScreen(coffer.Position, out var cofferScreen))
                continue;

            drawList.AddLine(
                originScreen,
                cofferScreen,
                coffer.Grade == CofferGrade.Bronze ? bronze : silver,
                LineThickness);
        }
    }

    private bool IsGradeEnabled(CofferGrade grade) => grade switch
    {
        CofferGrade.Bronze => _config.DrawLineToBronzeCoffers,
        CofferGrade.Silver => _config.DrawLineToSilverCoffers,
        _                  => false,
    };

    public void Draw()
    {
        UiHelpers.SectionHeader("Coffer Lines");
        UiHelpers.Muted(
            "While out of combat, draws a line from you to each nearby treasure coffer. " +
            "Only active inside the Occult Crescent.");

        ImGui.Spacing();

        var bronze = _config.DrawLineToBronzeCoffers;
        if (ImGui.Checkbox("Lines to bronze coffers", ref bronze))
        {
            _config.DrawLineToBronzeCoffers = bronze;
            Plugin.SaveConfiguration();
        }
        UiHelpers.HelpMarker("Draw a brown line from you to nearby bronze coffers.");

        var silver = _config.DrawLineToSilverCoffers;
        if (ImGui.Checkbox("Lines to silver coffers", ref silver))
        {
            _config.DrawLineToSilverCoffers = silver;
            Plugin.SaveConfiguration();
        }
        UiHelpers.HelpMarker("Draw a silver line from you to nearby silver coffers.");

        ImGui.Spacing();
        UiHelpers.SectionHeader("Status");

        if (!_inOccultCrescent)
        {
            UiHelpers.Muted(
                "You are not in the Occult Crescent, so nothing is being drawn. " +
                "Head to South Horn or North Horn.");
            return;
        }

        var bronzeCount = 0;
        var silverCount = 0;
        var coffers     = _coffers;

        foreach (var coffer in coffers)
        {
            if (coffer.Grade == CofferGrade.Bronze) bronzeCount++;
            else                                    silverCount++;
        }

        if (ImGui.BeginTable("##limlo-coffers", 2, ImGuiTableFlags.SizingFixedFit))
        {
            UiHelpers.Row("Bronze in range", bronzeCount.ToString());
            UiHelpers.Row("Silver in range", silverCount.ToString());
            UiHelpers.Row("Nearest",
                coffers.Count > 0 ? $"{coffers[0].Distance:F1} yalms ({coffers[0].Grade})" : "(none)");
            ImGui.EndTable();
        }

        if (_inCombat)
        {
            ImGui.Spacing();
            ImGui.PushStyleColor(ImGuiCol.Text, UiHelpers.Warn);
            ImGui.TextWrapped("Lines are hidden while you are in combat.");
            ImGui.PopStyleColor();
        }
    }
}
