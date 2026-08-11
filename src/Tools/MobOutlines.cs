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
/// **Mechanism.** <c>GameObject-&gt;DrawObject-&gt;OutlineColor</c>, an
/// <c>ObjectHighlightColor</c>. The palette is fixed by the game and offers
/// exactly eight values: None, Red, Green, Blue, Yellow, Orange, Magenta,
/// Black. There is no grey, so Black is used for "won't aggro" — it reads as a
/// dark rim against most terrain and is unmistakably not the red one.
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

    /// <summary>Applies an outline, remembering the object so it can be cleared.</summary>
    public unsafe void Apply(IGameObject obj, bool canAggro)
    {
        try
        {
            var native = (GameObject*)obj.Address;
            if (native == null || native->DrawObject == null)
                return;

            native->DrawObject->OutlineColor = canAggro
                ? ObjectHighlightColor.Red
                : ObjectHighlightColor.Black;

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
                    native->DrawObject->OutlineColor = ObjectHighlightColor.None;

                return;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Failed to clear a mob outline.");
        }
    }
}

