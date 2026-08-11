using System;
using System.Collections.Generic;

using Dalamud.Game.ClientState.Objects.Types;

using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace LimLoToolkit.Tools;

/// <summary>
/// Puts the game's own silhouette outline around mobs — the same effect it uses
/// for targeting, so it traces the actual model rather than approximating it
/// with a box.
///
/// **Mechanism.** <c>GameObject.Highlight(ObjectHighlightColor, includeMount)</c>,
/// virtual function 26 on the object's vtable. The palette is fixed by the game
/// and offers exactly eight values: None, Red, Green, Blue, Yellow, Orange,
/// Magenta, Black. There is no grey, so Black is used for "won't aggro" — it
/// reads as a dark rim against most terrain and is unmistakably not the red one.
///
/// **Why not write <c>DrawObject-&gt;OutlineColor</c> directly.** That property is
/// a bitfield occupying only the HIGH nibble of <c>DrawObject.OutlineFlags</c>
/// (confirmed from the IL of its getter: <c>ldfld OutlineFlags; ldc.i4.4;
/// ldc.i4.4; call GetBitfield</c>). Setting it leaves the low nibble untouched
/// and nothing renders. ClientStructs' own summary on the property says to use
/// <c>Highlight</c> instead, which also covers the mob's weapon and mount. This
/// plugin did write the property directly at first and the outlines never
/// appeared — see BROKEN.md.
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
    /// Applies an outline, remembering the object so it can be cleared.
    ///
    /// Re-asserted every tick rather than cached: the game drives this same
    /// field for its own target highlight, so a cached "already red" would go
    /// stale the moment the player targets and untargets the mob.
    /// </summary>
    public unsafe void Apply(IGameObject obj, bool canAggro)
    {
        try
        {
            var native = (GameObject*)obj.Address;
            if (native == null || native->DrawObject == null)
                return;

            native->Highlight(
                canAggro ? ObjectHighlightColor.Red : ObjectHighlightColor.Black,
                includeMount: true);

            _outlined.Add(obj.GameObjectId);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Failed to apply a mob outline.");
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
