using System;
using System.Collections.Generic;
using System.Linq;
using Stellar.Abstractions.Services;

namespace Stellar.TargetLens;

/// <summary>How the checked list for a type is interpreted.</summary>
internal enum TrackMode
{
    /// <summary>Show only items that are checked.</summary>
    IncludeOnly  = 0,
    /// <summary>Show every active item EXCEPT the ones that are checked.</summary>
    ExcludeBelow = 1,
}

/// <summary>
/// Persisted user selection for Target Lens: which buffs and which debuffs to show on the target HUD.
/// Unity-free — unit-testable. Stored as int[] arrays in the "select" config section.
/// Both modes default to <see cref="TrackMode.ExcludeBelow"/> so an empty selection shows EVERYTHING
/// (the HUD is unchanged until the user configures a selection).
/// </summary>
internal sealed class TargetEffectSelection
{
    private readonly HashSet<int> _buffs   = new();
    private readonly HashSet<int> _debuffs = new();

    public TrackMode BuffMode   { get; private set; } = TrackMode.ExcludeBelow;
    public TrackMode DebuffMode { get; private set; } = TrackMode.ExcludeBelow;

    public void SetBuffMode(TrackMode m)   => BuffMode   = m;
    public void SetDebuffMode(TrackMode m) => DebuffMode = m;

    /// <summary>Returns true when the buff with <paramref name="baseId"/> is in the tracked set.</summary>
    public bool IsBuffTracked(int baseId) => _buffs.Contains(baseId);

    /// <summary>Returns true when the debuff with <paramref name="baseId"/> is in the tracked set.</summary>
    public bool IsDebuffTracked(int baseId) => _debuffs.Contains(baseId);

    /// <summary>Adds or removes <paramref name="baseId"/> from the tracked-buffs set.</summary>
    public void SetBuff(int baseId, bool on)
    {
        if (on) _buffs.Add(baseId); else _buffs.Remove(baseId);
    }

    /// <summary>Adds or removes <paramref name="baseId"/> from the tracked-debuffs set.</summary>
    public void SetDebuff(int baseId, bool on)
    {
        if (on) _debuffs.Add(baseId); else _debuffs.Remove(baseId);
    }

    /// <summary>True when an effect should be shown given its tracked-state + the type's mode.</summary>
    public bool ShouldShow(int baseId, bool isDebuff)
    {
        var mode = isDebuff ? DebuffMode : BuffMode;
        bool tracked = isDebuff ? IsDebuffTracked(baseId) : IsBuffTracked(baseId);
        return mode == TrackMode.IncludeOnly ? tracked : !tracked;   // ExcludeBelow = show all except tracked
    }

    /// <summary>Populates a new <see cref="TargetEffectSelection"/> from a persisted config section.</summary>
    public static TargetEffectSelection Load(IConfigSection cfg)
    {
        var sel = new TargetEffectSelection();
        foreach (var id in cfg.Get("track.buffs",   Array.Empty<int>()) ?? Array.Empty<int>()) sel._buffs.Add(id);
        foreach (var id in cfg.Get("track.debuffs", Array.Empty<int>()) ?? Array.Empty<int>()) sel._debuffs.Add(id);
        sel.BuffMode   = (TrackMode)cfg.Get("mode.buffs",   (int)TrackMode.ExcludeBelow);
        sel.DebuffMode = (TrackMode)cfg.Get("mode.debuffs", (int)TrackMode.ExcludeBelow);
        return sel;
    }

    /// <summary>Persists both sets to <paramref name="cfg"/> and calls <see cref="IConfigSection.Save"/>.</summary>
    public void Save(IConfigSection cfg)
    {
        cfg.Set("track.buffs",   _buffs.ToArray());
        cfg.Set("track.debuffs", _debuffs.ToArray());
        cfg.Set("mode.buffs",   (int)BuffMode);
        cfg.Set("mode.debuffs", (int)DebuffMode);
        cfg.Save();
    }
}
