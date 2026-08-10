using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;

using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace LimLoToolkit.Tools;

/// <summary>
/// A browser over every mob the plugin has data for: what the game's sheets say
/// about it, what was observed live, and what training has measured.
///
/// Master list on the left, detail on the right. Rows are coloured by how solid
/// the measured data is — green solved, amber partial, red nothing yet.
///
/// **On "weakness".** There is no elemental-weakness data reachable from a mob.
/// The only resistance-shaped sheet is <c>BNpcResist</c>: 256 rows of eleven
/// unlabelled booleans, almost certainly status immunities rather than
/// elements — and <c>BNpcBase</c> holds no reference to it, so it cannot be
/// linked to a mob at all. Rather than invent a column, the viewer shows what
/// genuinely resolves. See docs/enemy-vision.md.
/// </summary>
public sealed class MobViewerTool : ITool
{
    public string Id          => "mob-viewer";
    public string Name        => "Mob Viewer";
    public string Description => "Everything known about each mob: detection, ranges, and game data.";
    public string Category    => "Toolkit";

    private const float MasterWidth = 240f;

    private readonly Configuration      _config;
    private readonly AggroLearningStore _store;

    private ExcelSheet<BNpcBase>? _bnpcSheet;

    private uint   _selectedBaseId;
    private string _search = string.Empty;
    private bool   _onlyNearby;
    /// <summary>
    /// Off by default: irrelevant mobs are more useful greyed out at the bottom
    /// of the list than missing entirely.
    /// </summary>
    private bool _hideIgnored;

    private int? _playerForayLevel;

    /// <summary>Base ids currently in the object table, refreshed each tick.</summary>
    private HashSet<uint> _nearby = new();

    /// <summary>
    /// Mobs seen nearby that have no recorded pull yet. Without these, a mob you
    /// have never been detected by would simply not appear — and "no data" is
    /// exactly the state worth showing in red.
    /// </summary>
    private Dictionary<uint, AggroProfile> _seenOnly = new();

    public MobViewerTool(Configuration config, AggroLearningStore store)
    {
        _config = config;
        _store  = store;
    }

    public void OnFrameworkUpdate()
    {
        try
        {
            var nearby = new HashSet<uint>();

            _bnpcSheet ??= Plugin.DataManager.GetExcelSheet<BNpcBase>();
            _playerForayLevel = ForayLevel.TryGet(Plugin.ObjectTable.LocalPlayer);

            foreach (var obj in Plugin.ObjectTable)
            {
                if (obj.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc)
                    continue;

                if (!obj.IsValid() || obj.IsDead)
                    continue;

                if (obj is not IBattleNpc battleNpc || battleNpc.BattleNpcKind != BattleNpcSubKind.Combatant)
                    continue;

                var name = obj.Name.ToString();

                // Names outside the tracked prefix are noise and never get an
                // entry. Explicitly-ignored mobs DO stay listed, otherwise they
                // could never be un-ignored from here.
                if (_store.IsAutoIgnoredByName(name))
                    continue;

                nearby.Add(obj.BaseId);

                var foray = ForayLevel.TryGet(obj) ?? 0;

                // Remember the level on the profile so relevance can still be
                // judged when the mob is nowhere near.
                if (_store.Find(obj.BaseId) is { } known)
                {
                    if (foray > 0 && known.ForayLevel != foray)
                    {
                        known.ForayLevel = foray;
                        _store.MarkDirty();
                    }

                    continue;
                }

                if (_seenOnly.ContainsKey(obj.BaseId))
                    continue;

                _seenOnly[obj.BaseId] = new AggroProfile
                {
                    BaseId               = obj.BaseId,
                    Name                 = name,
                    SheetOmnidirectional = _bnpcSheet?.GetRowOrDefault(obj.BaseId)?.IsOmnidirectional ?? false,
                    TerritoryId          = (ushort)Plugin.ClientState.TerritoryType,
                    Level                = battleNpc.Level,
                    MaxHp                = battleNpc.MaxHp,
                    HitboxRadius         = obj.HitboxRadius,
                    ForayLevel           = foray,
                };
            }

            _nearby = nearby;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "MobViewerTool failed to refresh the nearby set.");
        }
    }

    /// <summary>Measured profiles, plus placeholders for anything only ever seen.</summary>
    private List<AggroProfile> BuildDisplayList()
    {
        var all = new List<AggroProfile>(_store.All);

        foreach (var (baseId, placeholder) in _seenOnly)
        {
            if (_store.Find(baseId) == null)
                all.Add(placeholder);
        }

        // Irrelevant mobs sink to the bottom regardless of how much data they
        // have; among the rest, the best-known come first.
        return all
            .OrderBy(p => IsIrrelevant(p) ? 1 : 0)
            .ThenBy(p => _store.ConfidenceOf(p) switch
            {
                AggroConfidence.Confident => 0,
                AggroConfidence.Learning  => 1,
                _                         => 2,
            })
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Not worth attention: either explicitly ignored, or so far below the
    /// player's Knowledge level that it can never aggro.
    /// </summary>
    private bool IsIrrelevant(AggroProfile profile) =>
        _store.IsIgnored(profile.BaseId) || IsOutleveled(profile);

    private bool IsOutleveled(AggroProfile profile) =>
        _config.IgnoreOutleveledEnemies
        && profile.ForayLevel > 0
        && ForayLevel.IsHarmless(_playerForayLevel, profile.ForayLevel, _config.OutlevelMargin);

    public void Draw()
    {
        var profiles = BuildDisplayList();

        if (profiles.Count == 0)
        {
            UiHelpers.SectionHeader("Mob Viewer");
            UiHelpers.Muted(
                "No mobs recorded yet. Turn on Training Mode in Enemy Vision and go pull " +
                "something in the Occult Crescent — every mob that aggros you gets an entry " +
                "here automatically.");
            return;
        }

        DrawMaster(profiles);

        ImGui.SameLine();

        if (ImGui.BeginChild("##limlo-mob-detail", Vector2.Zero, true))
        {
            var selected = profiles.FirstOrDefault(p => p.BaseId == _selectedBaseId) ?? profiles[0];
            DrawDetail(selected);
        }

        ImGui.EndChild();
    }

    private void DrawMaster(List<AggroProfile> profiles)
    {
        if (ImGui.BeginChild("##limlo-mob-master", new Vector2(MasterWidth, 0), true))
        {
            ImGui.SetNextItemWidth(-1);
            var search = _search;
            if (ImGui.InputTextWithHint("##limlo-mob-search", "Search mobs...", ref search, 64))
                _search = search;

            var onlyNearby = _onlyNearby;
            if (ImGui.Checkbox("Nearby only", ref onlyNearby))
                _onlyNearby = onlyNearby;

            var hideIgnored = _hideIgnored;
            if (ImGui.Checkbox("Hide irrelevant", ref hideIgnored))
                _hideIgnored = hideIgnored;

            ImGui.Separator();

            var solved          = 0;
            var learning        = 0;
            var empty           = 0;
            var irrelevantCount = 0;

            foreach (var profile in profiles)
            {
                // Counts describe the mobs that actually matter; irrelevant ones
                // are tallied separately rather than inflating "empty".
                if (IsIrrelevant(profile))
                {
                    irrelevantCount++;
                    continue;
                }

                switch (_store.ConfidenceOf(profile))
                {
                    case AggroConfidence.Confident: solved++;   break;
                    case AggroConfidence.Learning:  learning++; break;
                    default:                        empty++;    break;
                }
            }

            UiHelpers.Colored(UiHelpers.Good, $"{solved} solved");
            ImGui.SameLine();
            UiHelpers.Colored(UiHelpers.Warn, $"{learning} partial");
            ImGui.SameLine();
            UiHelpers.Colored(UiHelpers.Bad, $"{empty} empty");

            if (irrelevantCount > 0)
                UiHelpers.Muted($"{irrelevantCount} irrelevant (ignored or outlevelled)");

            ImGui.Separator();

            foreach (var profile in profiles)
            {
                var irrelevant = IsIrrelevant(profile);

                if (_hideIgnored && irrelevant)
                    continue;

                if (_onlyNearby && !_nearby.Contains(profile.BaseId))
                    continue;

                if (!string.IsNullOrWhiteSpace(_search)
                    && profile.Name.IndexOf(_search, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                var confidence = _store.ConfidenceOf(profile);

                // Irrelevant mobs stay visible but recede to grey — they are not
                // part of the green/amber/red story any more.
                ImGui.PushStyleColor(ImGuiCol.Text,
                    irrelevant ? UiHelpers.Dim : UiHelpers.ConfidenceColor(confidence));
                var label = string.IsNullOrEmpty(profile.Name) ? $"#{profile.BaseId}" : profile.Name;
                if (ImGui.Selectable($"{label}###limlo-mob-{profile.BaseId}", profile.BaseId == _selectedBaseId))
                    _selectedBaseId = profile.BaseId;
                ImGui.PopStyleColor();

                if (_nearby.Contains(profile.BaseId))
                {
                    ImGui.SameLine();
                    UiHelpers.Colored(UiHelpers.Accent, "*");
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Currently nearby");
                }
            }
        }

        ImGui.EndChild();
    }

    private void DrawDetail(AggroProfile profile)
    {
        _bnpcSheet ??= Plugin.DataManager.GetExcelSheet<BNpcBase>();
        var row = _bnpcSheet?.GetRowOrDefault(profile.BaseId);

        var confidence = _store.ConfidenceOf(profile);

        UiHelpers.SectionHeader(string.IsNullOrEmpty(profile.Name) ? $"#{profile.BaseId}" : profile.Name);

        var ignored = _store.IsIgnored(profile.BaseId);

        if (IsOutleveled(profile))
            UiHelpers.Colored(UiHelpers.Dim,
                $"Harmless — its Knowledge is {profile.ForayLevel} and yours is {_playerForayLevel}, "
                + "so it can never aggro you. Not drawn, not trained on.");
        else if (ignored)
            UiHelpers.Colored(UiHelpers.Dim, "Ignored — not drawn, not trained on.");
        else
            UiHelpers.Colored(
                UiHelpers.ConfidenceColor(confidence),
                EnemyVisionTool.DescribeProgress(_store, profile));

        ImGui.Spacing();

        var ignoreToggle = ignored;
        if (ImGui.Checkbox($"Irrelevant — ignore this mob###limlo-ignore-detail-{profile.BaseId}", ref ignoreToggle))
        {
            _store.SetIgnored(profile.BaseId, ignoreToggle);
            Plugin.SaveConfiguration();
        }
        UiHelpers.HelpMarker(
            "Ignored mobs get no detection shape drawn and contribute no training samples. " +
            "Anything already measured is kept, so un-ignoring restores it.");

        if (AggroLearningStore.ContradictsSheet(profile))
        {
            ImGui.Spacing();
            UiHelpers.Colored(UiHelpers.Warn,
                $"Contradicts the game data: pulled from behind {profile.RearPulls} time(s), " +
                "so it is treated as detecting in all directions.");
        }

        ImGui.Spacing();
        UiHelpers.SectionHeader("Detection");

        var model = AggroLearningStore.Classify(profile);

        var verdictColour = model.Type switch
        {
            DetectionType.Cone   => UiHelpers.Good,
            DetectionType.Radius => UiHelpers.Good,
            _                    => UiHelpers.Warn,
        };

        UiHelpers.Colored(verdictColour, model.Type switch
        {
            DetectionType.Cone   => $"CONE — {model.Range:F1}y out to {model.FullConeDegrees:F0}° wide",
            DetectionType.Radius => $"RADIUS — {model.Range:F1}y in every direction",
            _                    => "NOT YET CLASSIFIED",
        });

        UiHelpers.Muted(model.Reason);

        ImGui.Spacing();

        if (ImGui.BeginTable("##limlo-mob-detect", 2, ImGuiTableFlags.SizingFixedFit))
        {
            UiHelpers.Row("Proven range", model.Range > 0f ? $"{model.Range:F1}y" : "unknown");
            UiHelpers.Row("Arc",
                model.Type == DetectionType.Radius ? "360° (all directions)"
                    : model.Type == DetectionType.Cone ? $"{model.FullConeDegrees:F0}° total"
                    : "undecided");

            UiHelpers.Row("Game data says",
                profile.SheetOmnidirectional ? "All directions" : "Forward only");

            if (model.Type != DetectionType.Unknown)
            {
                var agrees = (model.Type == DetectionType.Radius) == profile.SheetOmnidirectional;
                UiHelpers.Row("Agreement", agrees ? "matches observation" : "CONTRADICTED by observation");
            }

            if (profile.Distances.Count > 0)
            {
                UiHelpers.Row("Pulls recorded", profile.Distances.Count.ToString());
                UiHelpers.Row("Closest pull", $"{profile.Distances.Min():F1}y");
                UiHelpers.Row("Widest detection", $"{profile.MaxAngle:F0}° off its facing");
            }

            ImGui.EndTable();
        }

        ImGui.Spacing();
        UiHelpers.SectionHeader("Measured Envelope");

        UiHelpers.Muted(
            "Furthest pull recorded in each slice around the mob's facing. 0° is dead ahead, " +
            "180° is directly behind. This is the shape as observed — it is not forced to be a " +
            "cone or a circle, so a mob with a close all-round core and a longer forward reach " +
            "shows up as exactly that.");

        ImGui.Spacing();
        UiHelpers.Colored(UiHelpers.Accent, AggroLearningStore.DescribeMeasuredShape(profile));
        ImGui.Spacing();

        AggroLearningStore.EnsureBins(profile);

        if (ImGui.BeginTable("##limlo-envelope", 5, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Angle");
            ImGui.TableSetupColumn("Pulled at");
            ImGui.TableSetupColumn("Safe at");
            ImGui.TableSetupColumn("Best guess");
            ImGui.TableSetupColumn("Certainty");
            ImGui.TableHeadersRow();

            for (var bin = 0; bin < AggroLearningStore.AngleBins; bin++)
            {
                var pulls = profile.BinSamples[bin];
                var safe  = profile.BinMinSafeDistance[bin];

                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(AggroLearningStore.BinLabel(bin));

                // Lower bound: it reached us from here.
                ImGui.TableNextColumn();
                if (pulls > 0) UiHelpers.Colored(UiHelpers.Good, $"{profile.BinMaxDistance[bin]:F1}y");
                else           UiHelpers.Colored(UiHelpers.Dim, "-");

                // Upper bound: we stood here unnoticed.
                ImGui.TableNextColumn();
                if (safe > 0f) UiHelpers.Colored(UiHelpers.Accent, $"<{safe:F1}y");
                else           UiHelpers.Colored(UiHelpers.Dim, "-");

                ImGui.TableNextColumn();
                var guess = AggroLearningStore.RadiusAtAngle(profile, bin * AggroLearningStore.BinWidthDegrees + 1f);
                ImGui.TextUnformatted(guess is { } g ? $"{g:F1}y" : "-");

                ImGui.TableNextColumn();
                if (AggroLearningStore.BinContradicts(profile, bin))
                    UiHelpers.Colored(UiHelpers.Warn, "conflicting");
                else if (AggroLearningStore.BinUncertainty(profile, bin) is { } spread)
                    UiHelpers.Colored(spread <= 1.5f ? UiHelpers.Good : UiHelpers.Warn, $"+/- {spread / 2f:F1}y");
                else if (pulls > 0 || safe > 0f)
                    UiHelpers.Colored(UiHelpers.Warn, "one-sided");
                else
                    UiHelpers.Colored(UiHelpers.Bad, "none");
            }

            ImGui.EndTable();
        }

        UiHelpers.Muted(
            "A slice is pinned down once it has BOTH a pull (lower bound) and a safe stand " +
            "(upper bound) close together. \"Conflicting\" means a pull was recorded further out " +
            "than somewhere you later stood unnoticed — usually an old bad sample.");

        UiHelpers.Muted(
            $"{AggroLearningStore.FilledBins(profile)} of {AggroLearningStore.AngleBins} slices covered " +
            $"({AggroLearningStore.MinFilledBins} needed). Walk in from the sides and from behind to fill the gaps.");

        ImGui.Spacing();
        UiHelpers.SectionHeader("Observed");

        if (ImGui.BeginTable("##limlo-mob-observed", 2, ImGuiTableFlags.SizingFixedFit))
        {
            UiHelpers.Row("Level", profile.Level > 0 ? profile.Level.ToString() : "(unknown)");
            UiHelpers.Row("Max HP", profile.MaxHp > 0 ? profile.MaxHp.ToString("N0") : "(unknown)");
            UiHelpers.Row("Hitbox radius", $"{profile.HitboxRadius:F2}y");
            UiHelpers.Row("Knowledge level", profile.ForayLevel > 0 ? profile.ForayLevel.ToString() : "(unknown)");
            UiHelpers.Row("Seen in", profile.TerritoryId switch
            {
                1252 => "South Horn",
                1346 => "North Horn",
                0    => "(unknown)",
                var t => $"Territory {t}",
            });
            ImGui.EndTable();
        }

        ImGui.Spacing();
        UiHelpers.SectionHeader("Game Data");

        if (row is { } bnpc)
        {
            if (ImGui.BeginTable("##limlo-mob-sheet", 2, ImGuiTableFlags.SizingFixedFit))
            {
                UiHelpers.Row("BNpcBase id", profile.BaseId.ToString());
                UiHelpers.Row("Rank", bnpc.Rank.ToString());
                UiHelpers.Row("Scale", $"{bnpc.Scale:F2}");
                UiHelpers.Row("Battalion", bnpc.Battalion.RowId.ToString());
                UiHelpers.Row("Link race", bnpc.LinkRace.RowId.ToString());
                UiHelpers.Row("Model", bnpc.ModelChara.RowId.ToString());
                UiHelpers.Row("Shows level", bnpc.IsDisplayLevel ? "Yes" : "No");
                UiHelpers.Row("Draws target line", bnpc.IsTargetLine ? "Yes" : "No");
                ImGui.EndTable();
            }
        }
        else
        {
            UiHelpers.Muted($"No BNpcBase row resolved for id {profile.BaseId}.");
        }

        UiHelpers.Muted(
            "No elemental weakness data exists for mobs. The only resistance-shaped sheet is " +
            "BNpcResist (11 unlabelled booleans, likely status immunities) and BNpcBase holds " +
            "no reference to it, so it cannot be linked to a mob.");

        ImGui.Spacing();
        ImGui.Separator();

        if (ImGui.Button($"Forget this mob's data###limlo-forget-{profile.BaseId}"))
        {
            _store.Forget(profile.BaseId);
            Plugin.SaveConfiguration();
        }
        UiHelpers.HelpMarker("Clears every recorded pull for this mob so it can be re-measured from scratch.");

        if (_store.IgnoredCount > 0)
        {
            ImGui.SameLine();
            if (ImGui.Button("Un-ignore all"))
            {
                _store.ClearIgnored();
                Plugin.SaveConfiguration();
            }
        }
    }
}
