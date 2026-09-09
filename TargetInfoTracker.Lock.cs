namespace Stellar.TargetLens;

// HUD-target LOCK state + control API (display-only). Splitting this off the big TargetInfoTracker.cs keeps that
// file under the 500-LoC cap. The lock is purely a READ-SIDE override: when _lockedUuid is set, Poll() reads that
// entity for every overlay instead of the live game target. It NEVER calls a game targeting API — the player's real
// target / auto-attack / lock-on stay entirely the game's.
internal sealed partial class TargetInfoTracker
{
    private long  _lockedUuid;              // 0 = not locked; else the pinned display target
    private float _lockMissingSince = -1f;  // realtime when the locked entity first went missing; -1 = present
    private const float LockMissingGraceSec = 4f; // release the lock if it stays unresolvable this long (despawn)

    public bool IsLocked => _lockedUuid != 0;

    internal enum LockResult { NoTarget, Locked, Unlocked }

    // Called by the hotkey. Toggles the lock and returns the result + the locked target's display name.
    internal (LockResult result, string name) ToggleLock()
    {
        if (_lockedUuid != 0) { _lockedUuid = 0; _lockMissingSince = -1f; return (LockResult.Unlocked, ""); }
        long live = ReadLiveTargetUuidSafe();
        if (live == 0) return (LockResult.NoTarget, "");
        _lockedUuid = live;
        _lockMissingSince = -1f;
        _cacheFrame = -1;                // invalidate the frame cache so Current re-polls WITH the lock
        var snap = Current;              // resolves the name via the normal path (now using _lockedUuid)
        return (LockResult.Locked, snap.Name ?? "");
    }

    // Reads the LIVE game target uuid right now, ignoring the lock (used only when engaging a new lock).
    private long ReadLiveTargetUuidSafe()
    {
        try
        {
            if (!_services.ClientState.IsLoggedIn || !EnsureApi()) return 0;
            long local = _services.CombatSnapshot.LocalEntityId.Value;
            if (local == 0) return 0;
            long t = ReadTargetUuid(local);
            return (t != 0 && t != local) ? t : 0;
        }
        catch { return 0; }
    }
}
