using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

using Dalamud.Game.ClientState.Objects.SubKinds;

using FFXIVClientStructs.FFXIV.Client.UI.Agent;

using Newtonsoft.Json;

using CsCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace LimLoToolkit.Tools;

internal sealed record KeyboundScore(ulong ContentId, string Name, int Score, bool IsMe);

/// <summary>
/// Every player's score from the last finished Keybound Brawler match, which the game keeps
/// client-side but only shows you your own — plus a small history file of those results.
///
/// <para>Layout, found 2026-10-28 by scanning agents for our own content ID (agent index 401 on
/// game 2026.10.16; FFXIVClientStructs has no definition for it):</para>
/// <code>
/// +0x34  int    player count (4)
/// +0x40  record[0..count), 0x40 bytes each:
///          +0x00 u64  content ID
///          +0x30 u32  score
/// </code>
/// <para>It is the LAST FINISHED MATCH's results, not a live scoreboard: it is rewritten ~24 s
/// after the match UI closes. The agent index is not trusted — it is located by finding our own
/// content ID at a record position and re-validated on every read. Names come from the object
/// table (Character.ContentId); a player never seen there shows as "Player N". Every read goes
/// through ReadProcessMemory, so a wrong guess cannot crash the game.</para>
/// </summary>
internal sealed unsafe class KeyboundScoreboard
{
    [DllImport("kernel32.dll")]
    private static extern bool ReadProcessMemory(nint process, nint address, byte[] buffer, nint size, out nint read);

    private const int CountOffset = 0x34;
    private const int FirstRecord = 0x40;
    private const int RecordSize = 0x40;
    private const int ScoreInRecord = 0x30;
    private const int MaxPlayers = 8;

    /// <summary>Matches kept in the history file; at 51 the oldest goes.</summary>
    public const int MaxMatches = 50;

    private readonly byte[] _buf = new byte[FirstRecord + RecordSize * MaxPlayers];
    private readonly Dictionary<ulong, string> _names = new();
    private readonly string _namesPath;
    private readonly string _historyPath;

    private int _agentIndex = -1;
    private string _lastSignature = string.Empty;
    private bool _baselineTaken;

    /// <summary>The last match's results, highest first. Empty until one has been seen.</summary>
    public List<KeyboundScore> Last { get; private set; } = new();

    public int SavedThisSession { get; private set; }
    public string HistoryPath => _historyPath;

    public KeyboundScoreboard(string configDirectory)
    {
        _namesPath = Path.Combine(configDirectory, "keybound-player-names.json");
        _historyPath = Path.Combine(configDirectory, "keybound-history.jsonl");
        LoadNames();
    }

    /// <summary>
    /// Re-reads the table. Returns true when it now holds a NEW match's results, which are also
    /// appended to the history file. The first read only takes a baseline, so results that were
    /// already there when reading started (the previous session's match) are never saved twice.
    /// </summary>
    public bool Refresh(int wordsSubmitted)
    {
        var live = ReadLive();
        bool first = !_baselineTaken;
        _baselineTaken = true;
        if (live.Count == 0) return false;

        string sig = string.Join(";", live.OrderBy(e => e.ContentId).Select(e => $"{e.ContentId}:{e.Score}"));
        if (sig != _lastSignature)
        {
            _lastSignature = sig;
            Last = live;
            if (!first)
            {
                Plugin.Log.Information("[Keybound] new results: " + string.Join(", ", live.Select(e => $"{e.Name} {e.Score}")));
                Save(live, wordsSubmitted);
                return true;
            }
            return false;
        }

        // Same results as before: refresh names only (players resolved later keep their name).
        Last = live.Select(e => e with { Name = Better(e) }).ToList();
        return false;
    }

    private string Better(KeyboundScore e)
    {
        var old = Last.FirstOrDefault(o => o.ContentId == e.ContentId);
        return old != null && e.Name.StartsWith("Player ") && !old.Name.StartsWith("Player ") ? old.Name : e.Name;
    }

    private List<KeyboundScore> ReadLive()
    {
        var result = new List<KeyboundScore>();
        try
        {
            ulong me = Plugin.PlayerState.ContentId;
            if (me == 0) return result;
            if (_agentIndex < 0 || !Load(_agentIndex, me)) _agentIndex = Locate(me);
            if (_agentIndex < 0) return result;

            int count = Math.Clamp(BitConverter.ToInt32(_buf, CountOffset), 0, MaxPlayers);
            RefreshNames();
            for (int i = 0; i < count; i++)
            {
                int r = FirstRecord + i * RecordSize;
                ulong cid = BitConverter.ToUInt64(_buf, r);
                if (cid == 0) continue;
                int score = BitConverter.ToInt32(_buf, r + ScoreInRecord);
                string name = cid == me ? (Plugin.ObjectTable.LocalPlayer?.Name.TextValue ?? "You")
                            : _names.TryGetValue(cid, out var n) ? n : $"Player {i + 1}";
                result.Add(new KeyboundScore(cid, name, score, cid == me));
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug($"[Keybound] score read failed: {ex.Message}");
        }
        return result.OrderByDescending(e => e.Score).ToList();
    }

    /// <summary>Reads agent <paramref name="index"/> into the buffer and checks our content ID is one of its records.</summary>
    private bool Load(int index, ulong me)
    {
        var module = AgentModule.Instance();
        if (module == null || index < 0 || index >= module->Agents.Length) return false;
        nint agent = (nint)module->Agents[index].Value;
        if (agent == 0) return false;
        if (!ReadProcessMemory(-1, agent, _buf, _buf.Length, out var read) || read != _buf.Length) return false;
        int count = BitConverter.ToInt32(_buf, CountOffset);
        if (count is < 1 or > MaxPlayers) return false;
        for (int i = 0; i < count; i++)
            if (BitConverter.ToUInt64(_buf, FirstRecord + i * RecordSize) == me) return true;
        return false;
    }

    private int Locate(ulong me)
    {
        var module = AgentModule.Instance();
        if (module == null) return -1;
        for (int i = 0; i < module->Agents.Length; i++)
            if (Load(i, me))
            {
                Plugin.Log.Information($"[Keybound] results table found in agent {i}");
                return i;
            }
        return -1;
    }

    // ── Names ────────────────────────────────────────────────────────────────
    // Kept on disk (content ID → name) so a plugin reload doesn't turn the last match's players
    // back into "Player N".

    private void LoadNames()
    {
        try
        {
            if (File.Exists(_namesPath))
                foreach (var kv in JsonConvert.DeserializeObject<Dictionary<ulong, string>>(File.ReadAllText(_namesPath)) ?? new())
                    _names[kv.Key] = kv.Value;
        }
        catch (Exception ex) { Plugin.Log.Warning($"[Keybound] could not read {_namesPath}: {ex.Message}"); }
    }

    private void RefreshNames()
    {
        bool added = false;
        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj is not IPlayerCharacter pc) continue;
            var ch = (CsCharacter*)pc.Address;
            if (ch == null || ch->ContentId == 0) continue;
            if (_names.TryGetValue(ch->ContentId, out var known) && known == pc.Name.TextValue) continue;
            _names[ch->ContentId] = pc.Name.TextValue;
            added = true;
        }
        if (!added) return;
        try { File.WriteAllText(_namesPath, JsonConvert.SerializeObject(_names, Formatting.Indented)); }
        catch (Exception ex) { Plugin.Log.Warning($"[Keybound] could not save names: {ex.Message}"); }
    }

    // ── History ──────────────────────────────────────────────────────────────

    /// <summary>One line per finished match: when, the four players and their scores, your rank.</summary>
    private void Save(List<KeyboundScore> board, int wordsSubmitted)
    {
        try
        {
            int myRank = board.FindIndex(e => e.IsMe) + 1;
            var entry = new
            {
                endedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                myRank,
                wordsSubmittedThisSession = wordsSubmitted,
                players = board.Select((e, i) => new { rank = i + 1, name = e.Name, score = e.Score, me = e.IsMe }),
            };
            Directory.CreateDirectory(Path.GetDirectoryName(_historyPath)!);
            File.AppendAllText(_historyPath, JsonConvert.SerializeObject(entry) + "\n");
            Trim();
            SavedThisSession++;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[Keybound] could not save the match: {ex.Message}");
        }
    }

    /// <summary>
    /// Drops the oldest lines so at most <see cref="MaxMatches"/> remain. The file is tiny (one
    /// ~300-byte line per match), so read-all is fine; it's rewritten atomically.
    /// </summary>
    private void Trim()
    {
        var lines = File.ReadAllLines(_historyPath).Where(l => l.StartsWith('{')).ToList();
        if (lines.Count <= MaxMatches) return;
        int drop = lines.Count - MaxMatches;
        var tmp = _historyPath + ".tmp";
        File.WriteAllLines(tmp, lines.Skip(drop));
        File.Move(tmp, _historyPath, overwrite: true);
        Plugin.Log.Information($"[Keybound] trimmed {drop} oldest match(es) — {MaxMatches} kept");
    }
}
