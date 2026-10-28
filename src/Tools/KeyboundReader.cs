using System;
using System.Collections.Generic;

using Dalamud.Game.Text.SeStringHandling;

using FFXIVClientStructs.FFXIV.Component.GUI;

namespace LimLoToolkit.Tools;

/// <summary>What a plate is, from its tint (KTGEWord plate child node 25, add-RGB).</summary>
internal enum PlateKind
{
    Normal,
    /// <summary>Orange (255,128,0): the enemy is casting. Matched a cast in progress every time
    /// across four recorded matches — this is the yellow/red "flash" seen in game.</summary>
    Casting,
    /// <summary>Green (0,128,0): the barrier ("wall") prompt. Big plate (scale 1.7) near the
    /// bottom; typing it gives "Barrier reinforced!". Appears when the boss casts Tankbuster.</summary>
    Wall,
}

/// <summary>One enemy word plate. <see cref="Slot"/> is the plate's component node id.</summary>
/// <remarks><see cref="ScreenY"/>: the plate's on-screen Y. Larger = lower on screen = closer to
/// the players. Recorded 2026-10-28: regular enemies 470–650, the wall ~750, the boss and
/// mid-bosses 250–370 (far back).</remarks>
internal sealed record KeyboundWord(uint Slot, string Text, float ScreenY = 0, PlateKind Kind = PlateKind.Normal, float CenterX = 0)
{
    /// <summary>Set when a cast bar sits under this plate (see KeyboundReader.LinkCasts).</summary>
    public string? CastName { get; set; }

    /// <summary>Casting by either signal: orange plate tint, or a cast bar under the plate.</summary>
    public bool IsCasting => Kind == PlateKind.Casting || CastName != null;

    /// <summary>Far back row — the boss or a mid-boss. Its casts are not ours to interrupt.</summary>
    public bool IsBossLike => ScreenY > 0 && ScreenY < 420;
}

/// <summary>An enemy cast in progress. <see cref="Slot"/> matches the word plate's slot.</summary>
internal sealed record KeyboundCast(uint Slot, string Name, float CenterX = 0, float ScreenY = 0);

/// <summary>Everything the typer needs from one frame of the Keybound Brawler UI.</summary>
internal sealed class KeyboundSnapshot
{
    public bool WordsVisible;
    public bool InputVisible;
    /// <summary>KTGInput AtkValues[2]. False during the pre-round countdown, true while words can be typed.</summary>
    public bool InputArmed;
    /// <summary>KTGInput AtkValues[4] — counts 6, 4, 2, 0 through the pre-round countdown.</summary>
    public int Countdown;
    /// <summary>What is in the input bar right now. Empty when the bar is empty.</summary>
    public string Typed = string.Empty;
    public readonly List<KeyboundWord> Words = new();
    /// <summary>Casts in progress (KTGEnemyCastBar component visible; node 4 = cast name).</summary>
    public readonly List<KeyboundCast> Casts = new();
    /// <summary>Party HP (KTGHudGaugeHp AtkValues[1] / [2]); -1 when not shown.</summary>
    public int PartyHp = -1, PartyHpMax = -1;
    /// <summary>Our live score (KTGHudScore AtkValues[1], a UInt); -1 when not shown.</summary>
    public int MyScore = -1;

    public bool InPlay => WordsVisible && InputVisible && InputArmed;
}

/// <summary>
/// Reads the Keybound Brawler addons. FFXIVClientStructs has no definitions for these
/// (the minigame shipped 2026-10-28), so the layout below was mapped live from node dumps taken
/// during real matches. Full map in <c>docs/keybound-brawler.md</c>:
///
/// <list type="bullet">
/// <item><c>KTGEWord</c> — every enemy plate is a component node in the addon's top-level
/// node list; the plate's word is Text node <b>20</b> inside the component. The primary plate
/// is component <b>2</b>, extra plates are components <b>20001+</b>.</item>
/// <item><c>KTGInput</c> — the input bar is component node <b>7</b>; its Text node <b>16</b>
/// holds what has been typed (hidden while empty). AtkValues[2] flips true when typing opens.</item>
/// </list>
///
/// Pointers are re-resolved every call and never cached — an addon can be torn down between
/// frames — and every dereference sits inside the try.
/// </summary>
internal static unsafe class KeyboundReader
{
    public const string WordAddon = "KTGEWord";
    public const string InputAddon = "KTGInput";

    private const uint PlateWordNodeId = 20;
    private const uint InputComponentNodeId = 7;
    private const uint InputTextNodeId = 16;

    public static KeyboundSnapshot Read()
    {
        var snap = new KeyboundSnapshot();
        try
        {
            ReadWords(snap);
            ReadInput(snap);
            ReadCasts(snap);
            LinkCasts(snap);
            ReadHud(snap);
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug($"[Keybound] read failed: {ex.Message}");
        }
        return snap;
    }

    private static void ReadWords(KeyboundSnapshot snap)
    {
        var unit = (AtkUnitBase*)(nint)Plugin.GameGui.GetAddonByName(WordAddon, 1);
        if (unit == null || !unit->IsVisible) return;
        snap.WordsVisible = true;

        var mgr = &unit->UldManager;
        for (int i = 0; i < mgr->NodeListCount; i++)
        {
            var node = mgr->NodeList[i];
            if (node == null || (int)node->Type < 1000 || !node->IsVisible()) continue;

            var comp = ((AtkComponentNode*)node)->Component;
            if (comp == null) continue;

            var text = FindText(&comp->UldManager, PlateWordNodeId, requireVisible: true);
            if (!string.IsNullOrWhiteSpace(text))
                snap.Words.Add(new KeyboundWord(node->NodeId, text, node->ScreenY, ReadKind(&comp->UldManager), CenterX(node)));
        }
    }

    /// <summary>
    /// KTGEnemyCastBar has one component per plate slot (2, 20001…). Visible = casting; Text node
    /// 4 is the cast name. Mapped from the 2026-10-28 08:06 match recording: Self-destruct,
    /// Cursed Gaze, Cometeor, Burst, Charybdis, Ecliptic Meteor, Tankbuster, Ballistic Missile,
    /// Dynamic Sensory Jammer, Obstruction.
    /// </summary>
    private static void ReadCasts(KeyboundSnapshot snap)
    {
        var unit = (AtkUnitBase*)(nint)Plugin.GameGui.GetAddonByName("KTGEnemyCastBar", 1);
        if (unit == null || !unit->IsVisible) return;
        var mgr = &unit->UldManager;
        for (int i = 0; i < mgr->NodeListCount; i++)
        {
            var node = mgr->NodeList[i];
            if (node == null || (int)node->Type < 1000 || !node->IsVisible() || node->NodeId < 2) continue;
            var comp = ((AtkComponentNode*)node)->Component;
            if (comp == null) continue;
            var name = FindText(&comp->UldManager, 4, requireVisible: false);
            if (!string.IsNullOrWhiteSpace(name)) snap.Casts.Add(new KeyboundCast(node->NodeId, name, CenterX(node), node->ScreenY));
        }
    }

    /// <summary>Horizontal centre of a node on screen (ScreenX is the left edge).</summary>
    private static float CenterX(AtkResNode* node) => node->ScreenX + node->Width * node->ScaleX / 2f;

    /// <summary>
    /// Pairs each cast bar with the plate it sits under. NOT by slot id: in the 08:21 match the
    /// Burr Burrow cast was on cast slot 20004 while plate 20004 was elsewhere, and the cactuar's
    /// "??? Needles" never tinted its plate orange. A cast bar sits ~80–120 px below its plate
    /// (recorded), so the nearest plate by x and by (y − 100) wins, within a sanity limit.
    /// </summary>
    private static void LinkCasts(KeyboundSnapshot snap)
    {
        foreach (var cast in snap.Casts)
        {
            KeyboundWord? best = null;
            float bestDist = float.MaxValue;
            foreach (var w in snap.Words)
            {
                float d = Math.Abs(cast.CenterX - w.CenterX) + Math.Abs(cast.ScreenY - w.ScreenY - 100f);
                if (d < bestDist) { bestDist = d; best = w; }
            }
            if (best != null && bestDist < 260f) best.CastName = cast.Name;
        }
    }

    private static void ReadHud(KeyboundSnapshot snap)
    {
        var hp = (AtkUnitBase*)(nint)Plugin.GameGui.GetAddonByName("KTGHudGaugeHp", 1);
        if (hp != null && hp->IsVisible && hp->AtkValuesCount > 2)
        {
            snap.PartyHp = AsInt(hp->AtkValues[1]);
            snap.PartyHpMax = AsInt(hp->AtkValues[2]);
        }
        var sc = (AtkUnitBase*)(nint)Plugin.GameGui.GetAddonByName("KTGHudScore", 1);
        if (sc != null && sc->IsVisible && sc->AtkValuesCount > 1) snap.MyScore = AsInt(sc->AtkValues[1]);
    }

    private static int AsInt(AtkValue v) => v.Type switch
    {
        AtkValueType.Int => v.Int,
        AtkValueType.UInt => (int)v.UInt,
        _ => -1,
    };

    private static void ReadInput(KeyboundSnapshot snap)
    {
        var unit = (AtkUnitBase*)(nint)Plugin.GameGui.GetAddonByName(InputAddon, 1);
        if (unit == null || !unit->IsVisible) return;
        snap.InputVisible = true;

        if (unit->AtkValuesCount > 4)
        {
            snap.InputArmed = unit->AtkValues[2].Type == AtkValueType.Bool && unit->AtkValues[2].Byte != 0;
            if (unit->AtkValues[4].Type == AtkValueType.Int) snap.Countdown = unit->AtkValues[4].Int;
        }

        var mgr = &unit->UldManager;
        for (int i = 0; i < mgr->NodeListCount; i++)
        {
            var node = mgr->NodeList[i];
            if (node == null || node->NodeId != InputComponentNodeId || (int)node->Type < 1000) continue;
            var comp = ((AtkComponentNode*)node)->Component;
            if (comp == null) return;
            // The typed-text node is hidden while the bar is empty, and keeps its last
            // contents while hidden — so a hidden node means "empty", not "unknown".
            snap.Typed = FindText(&comp->UldManager, InputTextNodeId, requireVisible: true) ?? string.Empty;
            return;
        }
    }

    private const uint PlateTintNodeId = 25;

    private static PlateKind ReadKind(AtkUldManager* mgr)
    {
        for (int j = 0; j < mgr->NodeListCount; j++)
        {
            var n = mgr->NodeList[j];
            if (n == null || n->NodeId != PlateTintNodeId) continue;
            if (n->AddRed == 255 && n->AddGreen == 128 && n->AddBlue == 0) return PlateKind.Casting;
            if (n->AddRed == 0 && n->AddGreen == 128 && n->AddBlue == 0) return PlateKind.Wall;
            return PlateKind.Normal;
        }
        return PlateKind.Normal;
    }

    private static string? FindText(AtkUldManager* mgr, uint nodeId, bool requireVisible)
    {
        for (int j = 0; j < mgr->NodeListCount; j++)
        {
            var n = mgr->NodeList[j];
            if (n == null || n->NodeId != nodeId || n->Type != NodeType.Text) continue;
            if (requireVisible && !n->IsVisible()) return null;
            return ReadText((AtkTextNode*)n);
        }
        return null;
    }

    private static string ReadText(AtkTextNode* t)
    {
        byte* s = t->NodeText.StringPtr.Value;
        if (s == null) return string.Empty;
        int len = 0;
        while (s[len] != 0 && len < 1024) len++;
        return SeString.Parse(s, len).TextValue;
    }
}
