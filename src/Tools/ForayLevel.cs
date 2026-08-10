using System;

using Dalamud.Game.ClientState.Objects.Types;

using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace LimLoToolkit.Tools;

/// <summary>
/// Reads the "foray" level — Knowledge in the Occult Crescent, Elemental level
/// in Eureka, Resistance rank in Bozja. It lives on <c>BattleChara.ForayInfo</c>
/// and is populated for both the player and field enemies, which is what makes
/// the outlevel check possible.
///
/// **Why this matters here.** Aggro suppression by level is far tighter in a
/// foray zone than in the overworld. The overworld stops aggro once the player
/// is 11+ levels above a mob; the Occult Crescent stops it at just **1**
/// Knowledge level above. At the Knowledge 40 cap that makes most of the zone
/// permanently harmless, and drawing detection shapes for enemies that cannot
/// possibly react is noise.
///
/// It is also a correctness issue, not only a tidiness one. An enemy that
/// cannot aggro would otherwise feed the trainer an unbroken run of "stood
/// beside it unnoticed" observations. Those look like evidence of a tiny
/// detection radius and are nothing of the sort — the enemy is muzzled by
/// level, not blind. Left unchecked they would quietly poison the profile of
/// every low-level mob in the zone.
///
/// Sources for the level rules:
/// https://ffxiv.consolegameswiki.com/wiki/Aggression
/// </summary>
public static class ForayLevel
{
    /// <summary>Occult Crescent Knowledge cap as of the North Horn patch.</summary>
    public const int MaxKnowledgeLevel = 40;

    /// <summary>
    /// Foray level for any object, or null when it has none — a non-foray zone,
    /// or an object that is not a BattleChara.
    /// </summary>
    public static unsafe int? TryGet(IGameObject? obj)
    {
        if (obj == null || obj.Address == IntPtr.Zero)
            return null;

        try
        {
            var level = ((BattleChara*)obj.Address)->ForayInfo.Level;
            return level > 0 ? level : null;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Failed to read ForayInfo.Level.");
            return null;
        }
    }

    /// <summary>
    /// True when the player is far enough above this enemy's Knowledge level
    /// that it can never aggro. Unknown levels answer false — never assume
    /// safety from missing data.
    /// </summary>
    public static bool IsHarmless(int? playerLevel, int? enemyLevel, int margin)
    {
        if (playerLevel is not { } player || enemyLevel is not { } enemy)
            return false;

        return player - enemy >= Math.Max(1, margin);
    }
}
