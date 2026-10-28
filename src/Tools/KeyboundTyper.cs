using System;
using System.Collections.Generic;
using System.Linq;

using FFXIVClientStructs.FFXIV.Client.UI;

namespace LimLoToolkit.Tools;

/// <summary>Which plate the Keybound Brawler typer goes for.</summary>
public enum KeyboundTargeting
{
    /// <summary>
    /// Avoid damage: the wall (green plate) first, then casting enemies, then the closest enemy;
    /// the boss last.
    /// </summary>
    Safety = 0,

    /// <summary>The primary plate (component 2 — the boss in the boss stage) first.</summary>
    Boss = 1,
}

/// <summary>
/// Types the Keybound Brawler words, one verified key at a time.
///
/// <para><b>Every key is checked before the next is sent.</b> After a character is posted the
/// typer waits until the input bar reads back exactly what it expects. That makes the rate
/// self-limiting (no queue of posted keys can run ahead of the game), turns a field that is not
/// listening into a detected failure on the first character, and means a stray key from the
/// player is noticed and backspaced instead of compounding.</para>
///
/// <para><b>Words are submitted with Enter</b> — the game's own KTGPlayGuide addon reads
/// "[Enter]: Submit Text  [Esc]: Delete Text/Quit". Enter is the one key that could land
/// somewhere other than the minigame (chat), so it is only ever sent when the input bar reads
/// back the complete word: proof that the bar, not chat, has received every character.
/// Esc is never sent — it doubles as Quit.</para>
///
/// <para>Give-up rule (CLAUDE.md §5): <see cref="MaxConsecutiveFailures"/> keys in a row that
/// never reach the bar stand the typer down, and a word that keeps failing is skipped for
/// <see cref="BlacklistMs"/>.</para>
/// </summary>
internal sealed class KeyboundTyper
{
    private enum PendingKind { Char, Backspace, Submit, }

    private sealed record Pending(PendingKind Kind, string Expected, int PrevLen, long Deadline);

    /// <summary>
    /// Echo misses in a row before the typer stands itself down (circuit breaker). 12, not 6:
    /// stopping mid-match costs far more than a few wasted keys, and the common cause (an
    /// unfocused bar after a stun) is handled by an Enter flush before it counts.
    /// </summary>
    private const int MaxConsecutiveFailures = 12;
    /// <summary>Echo misses on one word before that word is abandoned.</summary>
    private const int MaxFailuresPerWord = 2;
    private const int EchoTimeoutMs = 600;
    /// <summary>
    /// The bar works like the chat box: <b>Enter opens it</b>, typing fills it, Enter submits
    /// and closes it again. When a first letter does not land, press Enter to open the bar and
    /// retry.
    /// </summary>
    private const int FirstCharRetryMs = 200;
    private const int MaxFirstCharTries = 6;
    private const int OpenBarSettleMs = 350;
    private const int SubmitTimeoutMs = 900;
    private const int BlacklistMs = 5000;
    private const int MaxOpenAttempts = 1;

    /// <summary>Seconds of input idle before a harmless LControl tap. The duty kick is well beyond this.</summary>
    private const float ActivityLimitSeconds = 20f;

    /// <summary>
    /// "Don't type" casts: text in the bar or an open input when they go off disables inputs for
    /// ~7 s. Cursed Gaze (a minion) and Dynamic Sensory Jammer (the boss's version).
    /// </summary>
    private static readonly string[] HoldDuringCasts = { "Cursed Gaze", "Dynamic Sensory Jammer" };

    /// <summary>
    /// Quiet time after a don't-type cast bar vanishes. The stun has landed 1.0 s, 1.4 s and 2.6 s
    /// after the bar went, so it is a window, not an instant. 4 s covers the worst seen + margin.
    /// </summary>
    private const int HoldAfterCastMs = 4000;
    /// <summary>Don't-type cast length: 4.67 s for both casts, every time recorded.</summary>
    private const int GazeCastMs = 4670;
    /// <summary>The bar must be empty and closed this long before the gaze fires.</summary>
    private const int GazeLastCallMs = 450;

    private readonly Configuration _config;
    private readonly KeyInjector _keys = new();
    private readonly Random _rng = new();
    private readonly Dictionary<string, long> _blacklist = new(StringComparer.OrdinalIgnoreCase);

    private Pending? _pending;
    private long _nextActionAt;
    private bool _backspaceViaKeyDown;
    private bool _submitRetried;
    private int _consecutiveFailures;
    private bool _flushTried;
    private int _wordFailures;
    private int _firstCharTries;
    private int _openAttempts;
    private long _nextActivityCheckAt;

    private bool _holdLogged;
    private long _holdUntil;
    private long _gazeSeenAt = -1;
    private long _gazeFiresAt;

    public bool Armed { get; private set; }
    /// <summary>Name of the cast the typer is currently holding for, or null.</summary>
    public string? HoldingFor { get; private set; }
    public string? Target { get; private set; }
    public string? LastError { get; private set; }
    public KeyboundSnapshot Last { get; private set; } = new();

    public int WordsSubmitted { get; private set; }
    public int CharsSent { get; private set; }
    public int EchoFailures { get; private set; }
    public int BackspacesSent { get; private set; }
    /// <summary>Characters that had to go as bare text (no key on this layout produces them).</summary>
    public int TextOnlyChars { get; private set; }

    public KeyboundTyper(Configuration config) => _config = config;

    /// <summary>Generous time to type and submit <paramref name="word"/> at the current settings.</summary>
    private int EstimateWordMs(string word) =>
        _config.KeyboundReactionMs + 350 + word.Length * (_config.KeyboundCharDelayMs + _config.KeyboundJitterMs + 25) + 300;

    public void Arm()
    {
        Armed = true;
        LastError = null;
        _consecutiveFailures = 0; _flushTried = false;
        ResetWord();
        Plugin.Log.Information("[Keybound] armed");
    }

    public void Disarm(string? reason = null)
    {
        Armed = false;
        _keys.ReleaseAll();
        ResetWord();
        if (reason != null)
        {
            LastError = reason;
            Plugin.Log.Warning($"[Keybound] stood down: {reason}");
            Plugin.ChatGui.PrintError($"[LimLo] Keybound Brawler stopped — {reason}");
        }
    }

    private void ResetWord()
    {
        Target = null;
        _pending = null;
        _wordFailures = 0;
        _firstCharTries = 0;
        _openAttempts = 0;
        _submitRetried = false;
    }

    /// <summary>Framework tick. Reads the UI every frame (cheap) and acts only while armed and in play.</summary>
    public void Tick()
    {
        _keys.Pump();
        var snap = KeyboundReader.Read();
        Last = snap;

        if (!Armed) return;
        long now = Environment.TickCount64;
        if (snap.WordsVisible || snap.InputVisible) KeepActive(now);
        if (!snap.InPlay)
        {
            ResetWord();
            return;
        }

        string typed = snap.Typed;

        if (_pending is { } p && !ResolvePending(p, typed, now)) return;
        if (now < _nextActionAt || _keys.AnyHeld) return;

        // Don't-type casts punish text in the bar / an open input when they GO OFF, not keys
        // during the cast. So during the cast: keep typing; finish (and submit) a word already
        // started; don't START a word that can't be finished before it fires; and after the cast
        // bar is gone, press nothing at all until the stun window has passed.
        var gaze = snap.Casts.FirstOrDefault(c => HoldDuringCasts.Contains(c.Name));
        if (gaze != null)
        {
            if (_gazeSeenAt < 0) { _gazeSeenAt = now; Plugin.Log.Information($"[Keybound] {gaze.Name} started"); }
            HoldingFor = gaze.Name;
            _holdUntil = now + HoldAfterCastMs;
            _gazeFiresAt = _gazeSeenAt + GazeCastMs;
        }
        else _gazeSeenAt = -1;

        if (gaze == null && now < _holdUntil)
        {
            if (!_holdLogged) { _holdLogged = true; Plugin.Log.Information($"[Keybound] {HoldingFor} going off — not touching anything"); }
            return;
        }
        if (gaze == null && _holdLogged) { Plugin.Log.Information($"[Keybound] {HoldingFor} over — resuming"); _holdLogged = false; HoldingFor = null; }

        // Last resort: a word still unfinished right before the gaze fires gets submitted as-is
        // so the bar is closed when it lands. A half-typed submit is harmless.
        if (gaze != null && typed.Length > 0 && now >= _gazeFiresAt - GazeLastCallMs)
        {
            if (_pending?.Kind != PendingKind.Submit)
            {
                Plugin.Log.Information($"[Keybound] {gaze.Name} about to fire — submitting '{typed}' as-is");
                Submit(typed, now);
            }
            return;
        }

        var words = snap.Words.Where(w => !IsBlacklisted(w.Text, now)).ToList();

        if (typed.Length > 0)
        {
            // Case-sensitive on purpose: a key press that opens a closed bar inserts its letter in
            // lower case, which the game would not take. A wrong-case letter is no prefix, so it is
            // backspaced and retyped into the now-open bar.
            var match = Pick(words.Where(w => w.Text.StartsWith(typed, StringComparison.Ordinal)));
            if (match == null)
            {
                // Wrong text in the bar: a stray key, a plate that died under us, or a word we
                // gave up on. Clear it rather than submit it.
                Backspace(typed, now);
                return;
            }

            if (!string.Equals(Target, match.Text, StringComparison.Ordinal)) { Target = match.Text; _wordFailures = 0; _firstCharTries = 0; }

            if (_config.KeyboundTargeting == KeyboundTargeting.Safety && Pick(words) is { } best && best.Text != match.Text && ShouldSwitch(match, best, typed.Length))
            {
                Plugin.Log.Information($"[Keybound] wall up — dropping '{match.Text}' for '{best.Text}'");
                _blacklist[match.Text] = now + 1500;   // so the backspaced prefix isn't re-matched
                Backspace(typed, now);
                return;
            }

            if (typed.Length >= match.Text.Length)
            {
                Submit(typed, now);
                return;
            }
        }
        else
        {
            // Re-choose at every word start — priorities change between words (a wall appears,
            // an enemy starts casting, a closer one walks up).
            var next = Pick(words);
            if (next == null) { ResetWord(); return; }

            // During a gaze, only start a word that can be typed and submitted before it fires.
            if (gaze != null && now + EstimateWordMs(next.Text) > _gazeFiresAt - GazeLastCallMs)
            {
                if (!_holdLogged) { _holdLogged = true; Plugin.Log.Information($"[Keybound] {gaze.Name}: no time for '{next.Text}' — bar stays closed"); }
                ResetWord();
                return;
            }
            if (next.Text != Target)
            {
                ResetWord();
                Target = next.Text;
                _nextActionAt = now + _config.KeyboundReactionMs;
                return;
            }
        }

        char c = Target![typed.Length];
        bool first = typed.Length == 0;

        // Letters go as real key presses, which would fire keybinds (W = walk forward) if no text
        // input had focus. So open the bar first rather than probing with a letter.
        if (first && !IsTextInputActive() && _openAttempts < MaxOpenAttempts)
        {
            _openAttempts++;
            Plugin.Log.Debug($"[Keybound] bar not focused before '{Target}' — Enter to open");
            _keys.SendEnter(false);
            _nextActionAt = now + OpenBarSettleMs;
            return;
        }
        if (first) _firstCharTries++;
        if (!TypeChar(c)) return;
        CharsSent++;
        _pending = new Pending(PendingKind.Char, typed + c, typed.Length, now + (first ? FirstCharRetryMs : EchoTimeoutMs));
        _nextActionAt = now + Delay();
    }

    /// <summary>
    /// REQUIREMENT: a character is a real key event (WM_KEYDOWN + WM_CHAR). Bare WM_CHAR got the
    /// player expelled for inactivity twice at ~2 min; key events ran a full 8-minute match with no
    /// kick. Bare text is allowed only for a character no key on the layout produces (e.g. "á");
    /// any other failure to send a key event stops the typer rather than degrade.
    /// </summary>
    private bool TypeChar(char c)
    {
        if (_keys.SendTypedChar(c, out var why)) return true;

        bool perCharacter = why != null && (why.StartsWith("no key") || why.Contains("Ctrl/Alt"));
        if (!perCharacter)
        {
            Disarm($"can't send real key events ({why}) — text-only typing gets you expelled for inactivity");
            return false;
        }
        Plugin.Log.Warning($"[Keybound] {why} — sending it as text only");
        if (!_keys.SendChar(c)) { Disarm("could not find the game window to type into"); return false; }
        TextOnlyChars++;
        return true;
    }

    /// <summary>Returns true when the pending key is settled and the typer may act this tick.</summary>
    private bool ResolvePending(Pending p, string typed, long now)
    {
        switch (p.Kind)
        {
            case PendingKind.Submit when typed.Length == 0:
                _pending = null;
                _consecutiveFailures = 0; _flushTried = false;
                WordsSubmitted++;
                ResetWord();
                _nextActionAt = now + _config.KeyboundReactionMs;
                return false;

            case PendingKind.Char when string.Equals(typed, p.Expected, StringComparison.Ordinal):
                _firstCharTries = 0;
                _pending = null;
                _consecutiveFailures = 0; _flushTried = false;
                return true;

            case PendingKind.Backspace when typed.Length < p.PrevLen:
                _pending = null;
                _consecutiveFailures = 0; _flushTried = false;
                return true;
        }

        bool expectedShape = p.Kind == PendingKind.Char
            ? typed.Length == p.PrevLen || typed.Length == p.PrevLen + 1
            : typed.Length == p.PrevLen;
        if (!expectedShape)
        {
            // The bar changed some other way (the player typed, a plate died) — re-evaluate.
            _pending = null;
            return true;
        }

        if (now < p.Deadline) return false;
        _pending = null;

        if (p.Kind == PendingKind.Char && p.PrevLen == 0 && typed.Length == 0 && _firstCharTries < MaxFirstCharTries)
        {
            // Bar not open. Open it with Enter, let it settle, then the next tick retries the letter.
            Plugin.Log.Debug($"[Keybound] '{p.Expected}' dropped (try {_firstCharTries}) — pressing Enter to open the bar");
            _keys.SendEnter(false);
            _nextActionAt = now + OpenBarSettleMs;
            return true;
        }

        // Backspace has two plausible Win32 forms; try the key form once before calling it a
        // failure, and keep whichever works for the rest of the session.
        if (p.Kind == PendingKind.Backspace && !_backspaceViaKeyDown)
        {
            _backspaceViaKeyDown = true;
            Plugin.Log.Information("[Keybound] WM_CHAR backspace ignored — switching to VK_BACK key messages");
            return true;
        }
        // Enter after a wave break only refocuses the bar; press it once more before calling it a
        // failure. Never switch Enter to another form: VK_RETURN is the one that submits, and a
        // permanent switch on one miss broke every later word.
        if (p.Kind == PendingKind.Submit && !_submitRetried)
        {
            _submitRetried = true;
            Plugin.Log.Information($"[Keybound] Enter didn't submit '{Target}' — bar probably unfocused, pressing again");
            return true;
        }

        // Leftover text in an unfocused bar: after a stun the bar keeps what was typed but no
        // longer has focus, so backspaces go nowhere. Enter refocuses / flushes it instead (a junk
        // submit is harmless), so do that before counting a failure.
        if (p.Kind == PendingKind.Backspace && typed.Length > 0 && !_flushTried)
        {
            _flushTried = true;
            Plugin.Log.Information($"[Keybound] backspace ignored with '{typed}' in the bar — Enter to flush it");
            _keys.SendEnter(false);
            _nextActionAt = now + OpenBarSettleMs;
            return true;
        }

        EchoFailures++;
        _consecutiveFailures++;
        _wordFailures++;
        string what = p.Kind switch
        {
            PendingKind.Char => $"'{p.Expected[^1]}'",
            PendingKind.Backspace => "backspace",
            _ => "Enter",
        };
        Plugin.Log.Warning($"[Keybound] no effect from {what} (bar reads '{typed}')");

        if (_consecutiveFailures >= MaxConsecutiveFailures)
        {
            Disarm($"{MaxConsecutiveFailures} keys in a row never reached the input bar — is the bar focused?");
            return false;
        }
        if ((_wordFailures >= MaxFailuresPerWord || p.Kind == PendingKind.Submit) && Target != null)
        {
            Plugin.Log.Warning($"[Keybound] giving up on '{Target}' for {BlacklistMs / 1000}s");
            _blacklist[Target] = now + BlacklistMs;
            Target = null;
            _wordFailures = 0;
        }
        return true;
    }

    /// <summary>
    /// Belt and braces against the duty's "extended inactivity" expulsion: when any of the game's
    /// input timers passes <see cref="ActivityLimitSeconds"/>, tap LControl, which does nothing in
    /// game but resets them. Real key events are what actually fixed the kick.
    /// </summary>
    private unsafe void KeepActive(long now)
    {
        if (now < _nextActivityCheckAt) return;
        _nextActivityCheckAt = now + 2000;
        try
        {
            var ui = UIModule.Instance();
            if (ui == null) return;
            var itm = ui->GetInputTimerModule();
            if (itm == null) return;
            float idle = MathF.Max(itm->AfkTimer, MathF.Max(itm->ContentInputTimer, itm->InputTimer));
            if (idle > ActivityLimitSeconds && _keys.TapControl())
                Plugin.Log.Information($"[Keybound] input timer at {idle:0}s — tapped LControl to stay active");
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug($"[Keybound] activity check failed: {ex.Message}");
        }
    }

    /// <summary>True while any game text input (chat, the minigame bar, …) has keyboard focus.</summary>
    private static unsafe bool IsTextInputActive()
    {
        try
        {
            var m = RaptureAtkModule.Instance();
            return m != null && m->IsTextInputActive();
        }
        catch { return false; }
    }

    private void Submit(string typed, long now)
    {
        if (!_keys.SendEnter(false)) { Disarm("could not find the game window to type into"); return; }
        _pending = new Pending(PendingKind.Submit, string.Empty, typed.Length, now + SubmitTimeoutMs);
        _nextActionAt = now + Delay();
    }

    private void Backspace(string typed, long now)
    {
        if (!_keys.SendBackspace(_backspaceViaKeyDown)) { Disarm("could not find the game window to type into"); return; }
        BackspacesSent++;
        _pending = new Pending(PendingKind.Backspace, string.Empty, typed.Length, now + EchoTimeoutMs);
        _nextActionAt = now + Math.Max(15, _config.KeyboundCharDelayMs / 2);
    }

    private KeyboundWord? Pick(IEnumerable<KeyboundWord> candidates)
    {
        var list = candidates.ToList();
        if (list.Count == 0) return null;

        if (_config.KeyboundTargeting == KeyboundTargeting.Boss)
            return list.FirstOrDefault(w => w.Slot == 2) ?? Closest(list);

        // Safety: wall first, then enemies that are casting, then the closest enemy; the boss last.
        // The boss is excluded from "casting" because it always is.
        var wall = list.Where(w => w.Kind == PlateKind.Wall).ToList();
        if (wall.Count > 0) return Closest(wall);
        var casters = list.Where(w => w.IsCasting && !w.IsBossLike).ToList();
        if (casters.Count > 0) return Closest(casters);
        var minions = list.Where(w => !w.IsBossLike).ToList();
        return Closest(minions.Count > 0 ? minions : list);
    }

    /// <summary>Lowest plate on screen = closest enemy.</summary>
    private static KeyboundWord Closest(List<KeyboundWord> list) =>
        list.OrderByDescending(w => w.ScreenY).First();

    /// <summary>
    /// Whether to abandon the word in progress for <paramref name="best"/>. Only for the wall
    /// (it mitigates an 18,000-HP Tankbuster) and only when the current word has more than a
    /// few characters left — finishing a nearly done word is cheaper than backspacing it.
    /// </summary>
    private static bool ShouldSwitch(KeyboundWord current, KeyboundWord best, int typedLength) =>
        best.Kind == PlateKind.Wall && current.Kind != PlateKind.Wall && current.Text.Length - typedLength > 3;

    private bool IsBlacklisted(string word, long now)
    {
        if (!_blacklist.TryGetValue(word, out var until)) return false;
        if (now < until) return true;
        _blacklist.Remove(word);
        return false;
    }

    private int Delay()
    {
        int j = _config.KeyboundJitterMs <= 0 ? 0 : _rng.Next(-_config.KeyboundJitterMs, _config.KeyboundJitterMs + 1);
        return Math.Max(15, _config.KeyboundCharDelayMs + j);
    }
}
