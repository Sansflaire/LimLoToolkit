using System;
using System.Collections.Generic;

using Dalamud.Game.ClientState.Objects.Types;

using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;

namespace LimLoToolkit.Tools;

/// <summary>
/// Puts the game's own silhouette outline around mobs — the same effect it uses
/// for targeting, so it traces the actual model rather than approximating it
/// with a box.
///
/// **Only mobs that can actually aggro are outlined.** Anything the player
/// outlevels gets no outline at all rather than a second colour — the point of
/// the overlay is "these can touch you", and a harmless mob wearing a rim reads
/// as a warning no matter which colour it is.
///
/// **Mechanism.** <c>GameObject.Highlight(ObjectHighlightColor, includeMount)</c>,
/// virtual function 26 on the object's vtable. It is the game's own routine, and
/// it also covers the mob's weapon, mount and ornament — which is exactly why
/// FFXIVClientStructs' summary on <c>DrawObject.OutlineColor</c> tells you to
/// call it rather than assign the property. The palette is fixed by the game at
/// eight values: None, Red, Green, Blue, Yellow, Orange, Magenta, Black.
///
/// **There is no thickness parameter.** The outline width belongs to the game's
/// render pass. The only outline-related setting in any published struct is
/// <c>GraphicsConfig.CharaOutline</c>, a bool at <c>+0x16</c> — a master on/off
/// for character outlines, not a width. If it is false, nothing here can draw.
/// See docs/enemy-vision.md.
///
/// **Restoring.** This writes to game state, so anything it touches must be put
/// back. Every outlined object is tracked and reset to None when it leaves
/// range, when the feature is switched off, and on plugin unload. Leaving mobs
/// permanently outlined after a reload would be a mess the user could not
/// clear without a zone change.
/// </summary>
public sealed class MobOutlines
{
    /// <summary>Objects currently carrying an outline we applied.</summary>
    private readonly HashSet<ulong> _outlined = new();

    public int OutlinedCount => _outlined.Count;

    /// <summary>
    /// Outlines a mob in red, remembering it so the outline can be taken off
    /// again. Call only for mobs that can actually aggro — this no longer takes
    /// a colour, because a harmless mob is meant to have no outline at all.
    ///
    /// Re-asserted every tick rather than cached: the game drives this same
    /// field for its own target highlight, so a cached "already red" would go
    /// stale the moment the player targets and untargets the mob.
    /// </summary>
    public unsafe void Apply(IGameObject obj)
    {
        try
        {
            var native = (GameObject*)obj.Address;
            if (native == null || native->DrawObject == null)
                return;

            native->Highlight(ObjectHighlightColor.Red, includeMount: true);

            _outlined.Add(obj.GameObjectId);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Failed to apply a mob outline.");
        }
    }

    /// <summary>
    /// Whether the game's own character-outline render pass is switched on. If
    /// this is false, nothing this class does can produce a visible outline —
    /// which is worth saying out loud in the UI rather than leaving the user to
    /// conclude the feature is broken.
    /// </summary>
    public static unsafe bool GameOutlinesEnabled
    {
        get
        {
            try
            {
                var config = GraphicsConfig.Instance();
                return config == null || config->CharaOutline;
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Failed to read GraphicsConfig.CharaOutline.");
                return true;
            }
        }
    }

    /// <summary>
    /// Clears outlines from anything we touched that is no longer in
    /// <paramref name="stillPresent"/>.
    /// </summary>
    public void ClearMissing(HashSet<ulong> stillPresent)
    {
        if (_outlined.Count == 0)
            return;

        List<ulong>? gone = null;

        foreach (var id in _outlined)
        {
            if (stillPresent.Contains(id))
                continue;

            (gone ??= new List<ulong>()).Add(id);
        }

        if (gone == null)
            return;

        foreach (var id in gone)
        {
            ClearOne(id);
            _outlined.Remove(id);
        }
    }

    /// <summary>Removes every outline we applied. Safe to call repeatedly.</summary>
    public void ClearAll()
    {
        if (_outlined.Count == 0)
            return;

        foreach (var id in _outlined)
            ClearOne(id);

        _outlined.Clear();
    }

    /// <summary>
    /// Resolves the id through the live object table before touching it — an
    /// object that despawned while outlined must not be written through a
    /// remembered address.
    /// </summary>
    private unsafe void ClearOne(ulong gameObjectId)
    {
        try
        {
            foreach (var obj in Plugin.ObjectTable)
            {
                if (obj.GameObjectId != gameObjectId)
                    continue;

                var native = (GameObject*)obj.Address;
                if (native != null && native->DrawObject != null)
                    native->Highlight(ObjectHighlightColor.None, includeMount: true);

                return;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Failed to clear a mob outline.");
        }
    }
}
