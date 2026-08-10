using System;
using System.Collections.Generic;
using System.Numerics;

using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace LimLoToolkit.Tools;

/// <summary>
/// Puts markers for a mob's known locations on the game's own map.
///
/// **Mechanism.** `AgentMap.AddMapMarker(Vector3 worldPosition, uint icon,
/// int scale, ...)` takes a WORLD position and does the map projection itself,
/// so there is no map-coordinate arithmetic here to get wrong — no size factor,
/// no offsets, no 2048-texture conversion. The markers land in the agent's
/// temporary marker list, alongside the ones the game and other plugins add.
///
/// **What is deliberately NOT called.** `ResetMapMarkers()` clears every
/// temporary marker on the map, including coffers and anything another plugin
/// has placed. Wiping someone else's markers to tidy up our own is not a
/// trade worth making, so ours are only refreshed when something actually
/// changed, and the game clears its temporary list on zone change anyway.
///
/// **Refresh policy.** Markers are (re)placed when the selection changes, the
/// territory changes, or the map is opened after being closed. Not on a timer,
/// and never per frame — `AddMapMarker` appends, so repeatedly calling it would
/// pile up thousands of duplicates.
/// </summary>
public sealed class MapMarkers
{
    /// <summary>Cap so a widespread mob cannot bury the map.</summary>
    public const int MaxMarkers = 100;

    /// <summary>Generic quest/objective style dot.</summary>
    private const uint MarkerIcon = 60561;

    private const int MarkerScale = 1;

    private uint   _placedForBaseId;
    private ushort _placedForTerritory;
    private bool   _mapWasOpen;
    private int    _placedCount;

    public int PlacedCount => _placedCount;

    /// <summary>True when the game map is currently up.</summary>
    public static unsafe bool IsMapOpen()
    {
        try
        {
            var agent = AgentMap.Instance();
            return agent != null && agent->IsAgentActive();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Failed to query map state.");
            return false;
        }
    }

    /// <summary>
    /// Ensures the map is showing markers for <paramref name="sightings"/>.
    /// Cheap to call every frame — it only acts when something changed.
    /// </summary>
    public unsafe void Sync(uint baseId, ushort territory, IReadOnlyList<Sighting> sightings, bool enabled)
    {
        var mapOpen = IsMapOpen();

        try
        {
            if (!enabled || baseId == 0)
            {
                _placedForBaseId = 0;
                _placedCount     = 0;
                _mapWasOpen      = mapOpen;
                return;
            }

            // Only place while the map is actually up. The agent discards its
            // temporary markers when the map closes, so placing them otherwise
            // achieves nothing but churn.
            if (!mapOpen)
            {
                _mapWasOpen      = false;
                _placedForBaseId = 0;
                return;
            }

            var justOpened = !_mapWasOpen;
            _mapWasOpen = true;

            if (!justOpened && _placedForBaseId == baseId && _placedForTerritory == territory)
                return;

            Place(territory, sightings);

            _placedForBaseId    = baseId;
            _placedForTerritory = territory;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Failed to sync map markers.");
        }
    }

    private unsafe void Place(ushort territory, IReadOnlyList<Sighting> sightings)
    {
        var agent = AgentMap.Instance();
        if (agent == null)
            return;

        var placed = 0;

        foreach (var sighting in sightings)
        {
            if (sighting.Territory != territory)
                continue;

            if (placed >= MaxMarkers)
                break;

            var position = new Vector3(sighting.X, sighting.Y, sighting.Z);

            if (agent->AddMapMarker(position, MarkerIcon, MarkerScale, null, 0, 0))
                placed++;
        }

        _placedCount = placed;
        Plugin.Log.Information($"[MapMarkers] Placed {placed} marker(s) for territory {territory}.");
    }

    /// <summary>Forces the next <see cref="Sync"/> to re-place markers.</summary>
    public void Invalidate() => _placedForBaseId = 0;
}
