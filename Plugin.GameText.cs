using System;
using Stellar.Abstractions.Services;

namespace Stellar.TargetLens;

// English effect names / descriptions from the framework's shared game-text table (IPluginServices.GameText, fw ≥2.33)
// — replaces TargetLens's own 8.6 MB BuffTable.json + its loader (issue #8). The table loads lazily in the background;
// every lookup is O(1) and allocation-free, and returns null until it is ready (callers then fall back to the game's
// own name, exactly as before when an id was missing). The game's live text is Chinese, so — as with the old JSON —
// the English name wins over it; ResolveDisplayName is NOT used because it prefers the game's name first.
//
// Ready (raised once per handler on the main thread, also when subscribed after the load) invalidates what was
// resolved early: the Select Effects table (names are its grouping keys) and the List window's per-slot name cache.
// HUD tile labels and the tooltip resolve on demand, so they pick the names up on the next refresh by themselves.
public sealed partial class Plugin
{
    private void InitGameText()
    {
        try { _services.GameText.Ready += OnGameTextReady; }
        catch (Exception ex) { _services.Log.Warning($"[TargetLens] GameText unavailable: {ex.Message}"); }
    }

    private void DisposeGameText()
    {
        try { _services.GameText.Ready -= OnGameTextReady; } catch { }
    }

    private void OnGameTextReady()
    {
        _buffTabLoaded = false;                                  // Select Effects: regroup with the real names on next use
        _selVersion++;
        Array.Clear(_blNameRow, 0, _blNameRow.Length);           // List window: re-resolve each slot's name
    }

    // Label: the English name, else the English design label (rows with no real name); null = not known / not ready.
    private string? GameTextName(int buffId)
    {
        var t = _services.GameText;
        var n = t.GetName(GameTextKind.Buff, buffId);
        return !string.IsNullOrEmpty(n) ? n : t.GetDesignName(GameTextKind.Buff, buffId);
    }

    // Tooltip text: the English description (or design note); null = not known / not ready.
    private string? GameTextDescription(int buffId) => _services.GameText.GetDescription(GameTextKind.Buff, buffId);
}
