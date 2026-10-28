using System;
using System.Linq;

using Dalamud.Bindings.ImGui;

namespace LimLoToolkit.Tools;

/// <summary>
/// Plays the Gold Saucer's Keybound Brawler typing minigame: reads the enemy words out of the
/// KTGEWord addon and types them into KTGInput, one verified real key press at a time. See
/// <see cref="KeyboundReader"/> for the addon map, <see cref="KeyboundTyper"/> for the typing
/// loop, and <c>docs/keybound-brawler.md</c> for how every mechanic was established.
///
/// <para>Automation, so it follows CLAUDE.md §5: the tool ships off, and even switched on it
/// only types after Start (panel, in-match window or <c>/limlo brawl</c>) — or on entering a
/// match when the user has opted into <see cref="Configuration.KeyboundAutoStart"/>. The armed
/// state itself is never saved.</para>
/// </summary>
public sealed class KeyboundBrawlerTool : ITool
{
    public string Id          => "keybound-brawler";
    public string Name        => "Keybound Brawler";
    public string Description => "Type the words in the Gold Saucer's Keybound Brawler minigame for you.";
    public string Category    => "Toolkit";

    /// <summary>The standalone plugin this tool was ported from. Both typing at once would fight.</summary>
    private const string StandalonePlugin = "KeyboardBrawlerAutomation";

    /// <summary>The results table is a memory read; twice a second is plenty.</summary>
    private const int ScoreRefreshMs = 500;
    /// <summary>
    /// Keep reading results this long after the minigame leaves the screen — the game writes
    /// them ~24 s after the match UI closes. Outside that window nothing is read.
    /// </summary>
    private const int ScoreWindowMs = 120_000;
    /// <summary>A gap this long between ticks means the tool was switched off in between.</summary>
    private const int TickGapMs = 1000;

    /// <summary>
    /// The match instance. <b>Verified</b> 2026-10-28: ContentFinderCondition 1095 "Keybound
    /// Brawler" (Gold Saucer) has TerritoryType 1359, and the log showed the client entering 1359
    /// at both match starts and returning to 1360 (Keybound Square, the lobby) after "Duty Done".
    /// </summary>
    private const ushort MatchTerritory = 1359;

    private bool _inMatchInstance;

    private readonly Configuration _config;
    private readonly KeyboundTyper _typer;
    private readonly KeyboundScoreboard _scores;

    private long _lastTickAt;
    private long _minigameSeenAt = -ScoreWindowMs;
    private long _nextScoreAt;
    private bool _conflict;
    private long _nextConflictCheckAt;

    public KeyboundBrawlerTool(Configuration config)
    {
        _config = config;
        _typer  = new KeyboundTyper(config);
        _scores = new KeyboundScoreboard(Plugin.PluginInterface.GetPluginConfigDirectory());
    }

    public void OnFrameworkUpdate()
    {
        long now = Environment.TickCount64;

        // The registry stops ticking a disabled tool, so a typer armed before the tool was
        // switched off would come back armed. Coming back is a fresh start.
        if (_lastTickAt != 0 && now - _lastTickAt > TickGapMs && _typer.Armed)
            _typer.Disarm();
        _lastTickAt = now;

        if (now >= _nextConflictCheckAt)
        {
            _nextConflictCheckAt = now + 2000;
            _conflict = StandaloneLoaded();
            if (_conflict && _typer.Armed)
                _typer.Disarm($"the {StandalonePlugin} plugin is also loaded — two typers would fight over the bar");
        }

        bool wasInMatch = _inMatchInstance;
        _inMatchInstance = Plugin.ClientState.TerritoryType == MatchTerritory;

        // "Always enable when minigame starts": arm on the way IN to the match instance, so a
        // Stop pressed during the match still sticks until the next one.
        if (_inMatchInstance && !wasInMatch && _config.KeyboundAutoStart && !_typer.Armed)
        {
            if (_conflict)
                Plugin.ChatGui.PrintError($"[LimLo] {Name}: not auto-starting — the {StandalonePlugin} plugin is also loaded.");
            else
            {
                _typer.Arm();
                Plugin.ChatGui.Print($"[LimLo] {Name}: auto-started — typing begins when the round opens.");
            }
        }

        _typer.Tick();

        var snap = _typer.Last;
        if (snap.WordsVisible || snap.InputVisible) _minigameSeenAt = now;
        if (now - _minigameSeenAt < ScoreWindowMs && now >= _nextScoreAt)
        {
            _nextScoreAt = now + ScoreRefreshMs;
            _scores.Refresh(_typer.WordsSubmitted);
        }
    }

    /// <summary>
    /// <c>/limlo brawl [on|off]</c> — start or stop typing from chat or a macro. No argument
    /// toggles. Prints what happened, so a macro press is never silent.
    /// </summary>
    public void RunCommand(string arg)
    {
        if (!_config.IsToolEnabled(Id))
        {
            Plugin.ChatGui.PrintError($"[LimLo] {Name} is switched off — turn it on in {Plugin.CommandConfig} first.");
            return;
        }

        bool? requested = arg switch
        {
            "on"  => true,
            "off" => false,
            ""    => !_typer.Armed,
            _     => null,
        };
        if (requested is not { } on)
        {
            Plugin.ChatGui.PrintError($"[LimLo] Usage: {Plugin.CommandMain} brawl [on|off]");
            return;
        }

        if (!on)
        {
            _typer.Disarm();
            Plugin.ChatGui.Print($"[LimLo] {Name}: stopped.");
            return;
        }
        if (StandaloneLoaded())
        {
            Plugin.ChatGui.PrintError($"[LimLo] {Name}: not starting — the {StandalonePlugin} plugin is also loaded.");
            return;
        }
        if (!_typer.Armed) _typer.Arm();
        Plugin.ChatGui.Print($"[LimLo] {Name}: typing — starts when a round opens.");
    }

    private static bool StandaloneLoaded()
    {
        try
        {
            return Plugin.PluginInterface.InstalledPlugins.Any(p => p.IsLoaded && p.InternalName == StandalonePlugin);
        }
        catch
        {
            return false;
        }
    }

    public void Draw()
    {
        UiHelpers.SectionHeader("Keybound Brawler");
        UiHelpers.Muted(
            "Reads the enemy words in the Gold Saucer's Keybound Brawler and types them for you, one " +
            "real key press at a time, submitting each word with Enter once the input bar shows all " +
            "of it. It never presses Esc (that quits the minigame).");
        ImGui.Spacing();

        DrawControl();
        DrawResults();
        DrawLive();
        DrawSettings();
    }

    /// <summary>
    /// The in-match window: Start/Stop, targeting and the sliders, shown for the whole time the
    /// player is in the match instance — including the countdown and the 10+ s gaps between
    /// stages, when the minigame UI is hidden. The player can't open menus or type commands
    /// during a match, so this is the only way to reach the controls then (Sansflaire, 2026-10-28).
    ///
    /// <para>It must never take the keyboard — every key the typer sends has to reach the game.
    /// So: it doesn't grab focus when it appears, keyboard navigation is off, and the sliders
    /// can't be Ctrl+clicked into text boxes.</para>
    /// </summary>
    public void DrawOverlay()
    {
        var s = _typer.Last;
        if (!_inMatchInstance && !s.WordsVisible && !s.InputVisible) return;

        const ImGuiWindowFlags Flags = ImGuiWindowFlags.AlwaysAutoResize
                                     | ImGuiWindowFlags.NoFocusOnAppearing
                                     | ImGuiWindowFlags.NoNavInputs
                                     | ImGuiWindowFlags.NoCollapse;

        if (ImGui.Begin("Keybound Brawler###limlo-keybound-play", Flags))
        {
            DrawControl();
            if (_typer.HoldingFor != null)
                UiHelpers.Colored(UiHelpers.Warn, $"Holding — {_typer.HoldingFor}");
            ImGui.Spacing();
            DrawTuning();
        }
        ImGui.End();
    }

    private void DrawControl()
    {
        if (_conflict)
        {
            UiHelpers.ColoredWrapped(UiHelpers.Bad,
                $"The {StandalonePlugin} plugin is loaded. It does the same job, and two typers would " +
                "fight over the input bar — disable one of them.");
            return;
        }

        if (ImGui.Button(_typer.Armed ? "Stop" : "Start typing"))
        {
            if (_typer.Armed) _typer.Disarm();
            else _typer.Arm();
        }
        ImGui.SameLine();
        UiHelpers.Colored(_typer.Armed ? (_typer.Last.InPlay ? UiHelpers.Good : UiHelpers.Warn) : UiHelpers.Dim,
            !_typer.Armed ? "Stopped" : _typer.Last.InPlay ? "Typing" : "Armed — waiting for a round");
        UiHelpers.HelpMarker(
            "Start before or during a match. It types only while a round is open, and stops itself " +
            "if keys stop reaching the input bar. Not saved — every load starts stopped, unless " +
            "\"Always enable when minigame starts\" is on.");

        if (_typer.LastError is { } err)
            UiHelpers.ColoredWrapped(UiHelpers.Bad, $"Last stop: {err}");
        ImGui.Spacing();
    }

    private void DrawResults()
    {
        var board = _scores.Last;
        if (board.Count == 0) return;

        UiHelpers.SectionHeader("Last match results");
        UiHelpers.Muted(
            "The game only shows you your own score. Everyone's is known once a match ends, and is " +
            "shown here until the next match finishes.");
        ImGui.Spacing();

        if (ImGui.BeginTable("##limlo-keybound-scores", 2, ImGuiTableFlags.SizingFixedFit))
        {
            int rank = 0;
            foreach (var e in board)
            {
                rank++;
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                UiHelpers.Colored(e.IsMe ? UiHelpers.Accent : UiHelpers.Dim, $"#{rank}  {e.Name}");
                ImGui.TableNextColumn();
                UiHelpers.Colored(e.IsMe ? UiHelpers.Accent : UiHelpers.Dim, e.Score.ToString("N0"));
            }
            ImGui.EndTable();
        }
        UiHelpers.HelpMarker(
            $"Each finished match is saved to {_scores.HistoryPath} — the newest " +
            $"{KeyboundScoreboard.MaxMatches} are kept ({_scores.SavedThisSession} saved this session).");
        ImGui.Spacing();
    }

    private void DrawLive()
    {
        var s = _typer.Last;
        UiHelpers.SectionHeader("Status");

        if (!s.WordsVisible && !s.InputVisible)
        {
            UiHelpers.Muted("Not in a match. The words and input bar only exist inside Keybound Brawler.");
        }
        else if (ImGui.BeginTable("##limlo-keybound-live", 2, ImGuiTableFlags.SizingFixedFit))
        {
            UiHelpers.Row("Round", s.InputArmed ? "open" : $"countdown {s.Countdown}");
            UiHelpers.Row("Input bar", s.Typed.Length == 0 ? "(empty)" : s.Typed);
            UiHelpers.Row("Target", _typer.Target ?? "—");
            if (s.MyScore >= 0) UiHelpers.Row("Your score", s.MyScore.ToString("N0"));
            if (s.PartyHpMax > 0) UiHelpers.Row("Party HP", $"{s.PartyHp:N0} / {s.PartyHpMax:N0}");
            UiHelpers.Row("Enemy casts", s.Casts.Count == 0 ? "none" : string.Join("  ·  ", s.Casts.Select(c => c.Name)));
            if (_typer.HoldingFor != null) UiHelpers.Row("Holding", $"not typing — {_typer.HoldingFor}");
            UiHelpers.Row("Words", s.Words.Count == 0 ? "none" : string.Join("  ·  ", s.Words.Select(w =>
                w.Kind == PlateKind.Wall ? "[WALL] " + w.Text : w.IsCasting ? "[CAST] " + w.Text : w.Text)));
            ImGui.EndTable();
        }

        if (ImGui.BeginTable("##limlo-keybound-stats", 2, ImGuiTableFlags.SizingFixedFit))
        {
            UiHelpers.Row("This session",
                $"{_typer.WordsSubmitted} words · {_typer.CharsSent} keys · {_typer.BackspacesSent} backspaces · {_typer.EchoFailures} misses");
            if (_typer.TextOnlyChars > 0)
                UiHelpers.Row("Typed as text", $"{_typer.TextOnlyChars} (no key on your layout makes them)");
            ImGui.EndTable();
        }
        ImGui.Spacing();
    }

    private void DrawSettings()
    {
        UiHelpers.SectionHeader("Settings");
        UiHelpers.Muted("These also appear in a small window of their own for the whole time you are in a match.");
        ImGui.Spacing();

        DrawTuning();

        ImGui.Spacing();
        UiHelpers.Muted(
            "Cursed Gaze and Dynamic Sensory Jammer punish text in the bar when they go off. While " +
            "one is casting the tool finishes the word it is on, starts no word it can't finish in " +
            "time, and presses nothing until the stun window has passed.");
    }

    /// <summary>Targeting and the three sliders — shared by the panel and the in-match window.</summary>
    private void DrawTuning()
    {
        var auto = _config.KeyboundAutoStart;
        if (ImGui.Checkbox("Always enable when minigame starts###limlo-keybound-auto", ref auto))
        {
            _config.KeyboundAutoStart = auto;
            Plugin.SaveConfiguration();
        }
        UiHelpers.HelpMarker(
            "Starts typing every time you enter a Keybound Brawler match, whether or not you left " +
            "it on. Pressing Stop during a match still stops it until the next match.");

        if (ImGui.RadioButton("Safety", _config.KeyboundTargeting == KeyboundTargeting.Safety))
        {
            _config.KeyboundTargeting = KeyboundTargeting.Safety;
            Plugin.SaveConfiguration();
        }
        UiHelpers.HelpMarker(
            "Avoid damage: the wall prompt first, then enemies that are casting (interrupts them), " +
            "then the closest enemy. The boss is last.");
        ImGui.SameLine();
        if (ImGui.RadioButton("Boss", _config.KeyboundTargeting == KeyboundTargeting.Boss))
        {
            _config.KeyboundTargeting = KeyboundTargeting.Boss;
            Plugin.SaveConfiguration();
        }
        UiHelpers.HelpMarker("The primary target (the boss, in the boss stage) first, then the closest enemy.");

        IntSlider("Key delay", () => _config.KeyboundCharDelayMs, v => _config.KeyboundCharDelayMs = v, 20, 400);
        UiHelpers.HelpMarker(
            "Time between keys. Each key waits for the bar to show it before the next is sent, so " +
            "the real rate can only be slower than this, never faster.");
        IntSlider("Jitter ±", () => _config.KeyboundJitterMs, v => _config.KeyboundJitterMs = v, 0, 150);
        UiHelpers.HelpMarker("Random spread added to every key delay.");
        IntSlider("Reaction", () => _config.KeyboundReactionMs, v => _config.KeyboundReactionMs = v, 0, 1500);
        UiHelpers.HelpMarker("Pause after picking a new word before its first key.");
    }

    private static void IntSlider(string label, Func<int> get, Action<int> set, int min, int max)
    {
        ImGui.SetNextItemWidth(200f);
        float v = Math.Clamp(get(), min, max);
        // NoInput: Ctrl+click would turn the slider into a text box, which takes the keyboard
        // away from the game mid-match.
        if (ImGui.SliderFloat($"{label}###limlo-keybound-{label}", ref v, min, max, "%.0f ms", ImGuiSliderFlags.NoInput))
        {
            set((int)MathF.Round(v));
            Plugin.SaveConfiguration();
        }
    }

    public void Dispose() => _typer.Disarm();
}
