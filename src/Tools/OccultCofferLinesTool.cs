using System;
using System.Collections.Generic;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;

using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

using Lumina.Excel;

using NativeObject   = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using NativeTreasure = FFXIVClientStructs.FFXIV.Client.Game.Object.Treasure;
using TreasureFlags  = FFXIVClientStructs.FFXIV.Client.Game.Object.Treasure.TreasureFlags;
using TreasureState  = FFXIVClientStructs.FFXIV.Client.Game.Object.Treasure.TreasureState;
using XIVTreasure    = Lumina.Excel.Sheets.Treasure;

namespace LimLoToolkit.Tools;

/// <summary>
/// Draws a line from the player to nearby treasure coffers inside the Occult
/// Crescent, and optionally targets and opens one the player walks up to.
///
/// Mechanisms (verified against OhKannaDuh/BOCCHI's implementation and against
/// the game itself — see docs/occult-crescent.md):
///
///  - Coffers are <see cref="ObjectKind.Treasure"/> entries in the object table.
///  - Grade comes from the <c>Treasure</c> Excel sheet row addressed by the
///    object's <c>BaseId</c>: its <c>SGB</c> row id is 1596 for bronze and
///    1597 for silver. Anything else is an unrelated chest and is ignored.
///  - Opening is <c>TargetSystem.InteractWithObject</c>, the same primitive
///    BOCCHI and Pandora's AutoOpenChests use, throttled to 200 ms.
///  - Open state is read from the native <c>Treasure</c> struct's flags/state,
///    plus the loot window, so an already-open coffer is never re-poked.
///  - Lines and auto-open are both suppressed in combat, and the whole tool is
///    inert outside the Occult Crescent territories.
///
/// This is an independent implementation. BOCCHI renders through Pictomancy and
/// paths to coffers with vnavmesh; this uses Dalamud's own <c>WorldToScreen</c>
/// plus an ImGui foreground draw list and never moves the player, so the plugin
/// stays a single DLL with no extra dependencies.
/// </summary>
public sealed class OccultCofferLinesTool : ITool
{
    public string Id          => "occult-coffer-lines";
    public string Name        => "Coffer Lines";
    public string Description => "Lines to nearby bronze and silver coffers in the Occult Crescent, with optional auto-open.";
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

    /// <summary>Past roughly this range the client refuses the interact outright.</summary>
    public const float MinOpenDistance = 1.0f;
    public const float MaxOpenDistance = 2.75f;

    /// <summary>Pandora's ChestThrottle cadence, which BOCCHI also matches.</summary>
    private const long InteractThrottleMs = 200;

    /// <summary>Breathing room after a coffer actually opens, for the loot window.</summary>
    private const long PostOpenCooldownMs = 700;

    /// <summary>
    /// Circuit breaker. A coffer that will not open — blocked line of sight,
    /// another party's chest, a level gate — must not be poked forever at five
    /// attempts a second. After this many tries it is benched.
    /// </summary>
    private const int  MaxAttemptsPerCoffer = 8;
    private const long BenchDurationMs      = 15_000;

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

    private sealed class AttemptRecord
    {
        public int  Attempts;
        public long BenchedUntil;
    }

    private readonly Configuration _config;

    private ExcelSheet<XIVTreasure>? _treasureSheet;

    // Written on the framework thread, read on the UI thread. Reference
    // assignment is atomic, so the overlay always sees a complete list.
    private List<Coffer> _coffers = new();
    private Vector3      _origin;
    private bool         _inOccultCrescent;
    private bool         _inCombat;

    // Auto-open state. Framework thread only, except for the read-only status
    // fields the panel displays.
    private readonly Dictionary<ulong, AttemptRecord> _attempts = new();
    private long   _nextInteractAllowedAt;
    private int    _openedThisSession;
    private string _lastAction = string.Empty;

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
                // Leaving the zone invalidates every object id we were tracking.
                if (_attempts.Count > 0)
                    _attempts.Clear();

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

            IGameObject? openCandidate     = null;
            var          openCandidateDist = float.MaxValue;
            var          openRange         = Math.Clamp(_config.AutoOpenDistance, MinOpenDistance, MaxOpenDistance);

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

                var distance = Vector3.Distance(_origin, obj.Position);
                found.Add(new Coffer(obj.Position, grade, distance));

                if (distance <= openRange && distance < openCandidateDist && !IsBenched(obj.GameObjectId))
                {
                    openCandidate     = obj;
                    openCandidateDist = distance;
                }
            }

            found.Sort((a, b) => a.Distance.CompareTo(b.Distance));

            if (openCandidate != null)
                TryAutoOpen(openCandidate);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "OccultCofferLinesTool failed during its framework tick.");
        }

        _coffers = found;
    }

    // ── Auto-open ────────────────────────────────────────────────────────────

    /// <summary>
    /// Conditions under which the plugin must keep its hands off. Anything that
    /// means "the player is busy, mid-transition, or not in control" belongs
    /// here — firing an interact through one of these is how you desync a
    /// cutscene or eat an input during a zone change.
    /// </summary>
    private static bool IsBlockedByCondition()
    {
        var c = Plugin.Condition;

        return c[ConditionFlag.InCombat]
            || c[ConditionFlag.BetweenAreas]
            || c[ConditionFlag.BetweenAreas51]
            || c[ConditionFlag.Casting]
            || c[ConditionFlag.Occupied]
            || c[ConditionFlag.Occupied30]
            || c[ConditionFlag.Occupied33]
            || c[ConditionFlag.Occupied38]
            || c[ConditionFlag.Occupied39]
            || c[ConditionFlag.OccupiedInEvent]
            || c[ConditionFlag.OccupiedInQuestEvent]
            || c[ConditionFlag.OccupiedInCutSceneEvent]
            || c[ConditionFlag.OccupiedSummoningBell]
            || c[ConditionFlag.WatchingCutscene]
            || c[ConditionFlag.WatchingCutscene78]
            || c[ConditionFlag.Unconscious]
            || c[ConditionFlag.LoggingOut];
    }

    private bool IsBenched(ulong gameObjectId) =>
        _attempts.TryGetValue(gameObjectId, out var record)
        && record.BenchedUntil > Environment.TickCount64;

    private unsafe void TryAutoOpen(IGameObject coffer)
    {
        if (!_config.AutoOpenCoffers)
            return;

        var now = Environment.TickCount64;
        if (now < _nextInteractAllowedAt)
            return;

        if (IsBlockedByCondition())
            return;

        var native = (NativeObject*)coffer.Address;
        if (native == null)
            return;

        var treasure = (NativeTreasure*)native;

        // Already open, already looted, or mid-open animation: never re-poke.
        if (IsOpenedOrLooted(coffer, treasure))
        {
            _attempts.Remove(coffer.GameObjectId);
            return;
        }

        if (treasure->State is TreasureState.Opening)
        {
            _nextInteractAllowedAt = now + PostOpenCooldownMs;
            return;
        }

        if (!native->GetIsTargetable())
            return;

        if (!_attempts.TryGetValue(coffer.GameObjectId, out var record))
        {
            record = new AttemptRecord();
            _attempts[coffer.GameObjectId] = record;
        }

        record.Attempts++;
        if (record.Attempts > MaxAttemptsPerCoffer)
        {
            record.BenchedUntil = now + BenchDurationMs;
            record.Attempts     = 0;
            _lastAction         = $"Gave up on a coffer after {MaxAttemptsPerCoffer} tries — retrying in {BenchDurationMs / 1000}s.";
            Plugin.Log.Debug($"Benching coffer {coffer.GameObjectId:X} after {MaxAttemptsPerCoffer} failed interacts.");
            return;
        }

        _nextInteractAllowedAt = now + InteractThrottleMs;

        var targetSystem = TargetSystem.Instance();
        if (targetSystem == null)
            return;

        // Target first — the user asked for target-and-open, and it also makes
        // what the plugin is doing visible in the game's own UI.
        Plugin.TargetManager.Target = coffer;
        targetSystem->InteractWithObject(native, true);

        if (IsOpenedOrLooted(coffer, treasure))
        {
            _openedThisSession++;
            _attempts.Remove(coffer.GameObjectId);
            _nextInteractAllowedAt = now + PostOpenCooldownMs;
            _lastAction            = $"Opened a coffer ({_openedThisSession} this session).";
        }
    }

    /// <summary>
    /// True once a coffer is spent. Flags and state cover the normal open, and
    /// the loot window covers one opened by someone else in the party.
    /// </summary>
    private static unsafe bool IsOpenedOrLooted(IGameObject coffer, NativeTreasure* treasure)
    {
        if (treasure->Flags.HasFlag(TreasureFlags.Opened)
            || treasure->Flags.HasFlag(TreasureFlags.FadedOut)
            || treasure->State is TreasureState.Opened or TreasureState.FadingOut or TreasureState.FadedOut)
        {
            return true;
        }

        var loot = Loot.Instance();
        if (loot == null)
            return false;

        foreach (var item in loot->Items)
        {
            if (item.ChestObjectId == coffer.GameObjectId)
                return true;
        }

        return false;
    }

    // ── Overlay ──────────────────────────────────────────────────────────────

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

    // ── Panel ────────────────────────────────────────────────────────────────

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
        UiHelpers.SectionHeader("Auto-Open");

        var autoOpen = _config.AutoOpenCoffers;
        if (ImGui.Checkbox("Target and open coffers automatically", ref autoOpen))
        {
            _config.AutoOpenCoffers = autoOpen;
            Plugin.SaveConfiguration();
        }
        UiHelpers.HelpMarker(
            "When you walk within range of a coffer, target it and open it for you. " +
            "It never moves your character — you still walk there yourself.");

        if (_config.AutoOpenCoffers)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, UiHelpers.Warn);
            ImGui.TextWrapped(
                "This acts on the game for you. Square Enix does not permit third-party " +
                "automation, so use it at your own risk.");
            ImGui.PopStyleColor();

            ImGui.Spacing();

            var range = Math.Clamp(_config.AutoOpenDistance, MinOpenDistance, MaxOpenDistance);
            ImGui.SetNextItemWidth(200f);
            if (ImGui.SliderFloat("Open within (yalms)", ref range, MinOpenDistance, MaxOpenDistance, "%.2f"))
            {
                _config.AutoOpenDistance = Math.Clamp(range, MinOpenDistance, MaxOpenDistance);
                Plugin.SaveConfiguration();
            }
            UiHelpers.HelpMarker(
                $"The game itself refuses to interact past roughly {MaxOpenDistance:F2} yalms, " +
                "so this cannot be raised beyond that. 2.00 matches what other plugins use.");

            UiHelpers.Muted(
                "Paused in combat, during cutscenes and zone changes, and any time you are " +
                "otherwise occupied. A coffer that refuses to open is dropped after " +
                $"{MaxAttemptsPerCoffer} tries and retried in {BenchDurationMs / 1000} seconds.");
        }

        ImGui.Spacing();
        UiHelpers.SectionHeader("Status");

        if (!_inOccultCrescent)
        {
            UiHelpers.Muted(
                "You are not in the Occult Crescent, so nothing is being drawn or opened. " +
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

            if (_config.AutoOpenCoffers)
                UiHelpers.Row("Opened this session", _openedThisSession.ToString());

            ImGui.EndTable();
        }

        if (_config.AutoOpenCoffers && !string.IsNullOrEmpty(_lastAction))
        {
            ImGui.Spacing();
            UiHelpers.Muted(_lastAction);
        }

        if (_inCombat)
        {
            ImGui.Spacing();
            ImGui.PushStyleColor(ImGuiCol.Text, UiHelpers.Warn);
            ImGui.TextWrapped("Paused while you are in combat — no lines, no auto-open.");
            ImGui.PopStyleColor();
        }
    }
}
