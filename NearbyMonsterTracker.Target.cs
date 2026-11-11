using System;
using System.Reflection;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.TargetLens;

/// <summary>
/// Write-side of <see cref="NearbyMonsterTracker"/>: set the player's real in-game combat target from a Nearby
/// Monsters row click, plus the current-target uuid the window reads to highlight the matching row. Split out of
/// <c>NearbyMonsterTracker.cs</c> to keep that file under the 500-LoC gate.
///
/// Setter: <c>Panda.ZGame.TargetComp.ForceSetTarget(long uuid)</c> — a per-entity <c>ZComponent</c> on the LOCAL
/// PLAYER <c>ZEntity</c> (there is NO singleton/manager for it), resolved via <c>ZEntity.GetComponent&lt;TargetComp&gt;()</c>
/// with the same base-type-walk generic-reflection pattern as <c>CastPatch.GetSkillComp</c>. Confirmed non-inlined,
/// no <c>in</c>/<c>ref</c> struct param (no IL2CPP trampoline crash), and client-authoritative — a local call takes
/// effect with no server request. The component only exists once <c>GamePhase == World</c> and the player entity is
/// in-scene, so every step is null-guarded and a miss no-ops (never throws into the click handler).
///
/// ⚠️ Stickiness: <c>AttrTargetId</c> is a server-broadcast attr and this is action-combat with continuous auto-target
/// selection, so a forced SOFT target gets re-selected away within ~1s. So a row click engages the game's real
/// HARD lock (the in-game lock-on button): <c>ForceSetTarget</c> to select the monster, then <c>LockTargetOrCancel</c>
/// to pin it. <c>LockTargetOrCancel</c> is a TOGGLE on the current selection, so the sequence is state-managed off the
/// live <c>ZWorld.LockTargetUuid</c> (0 = nothing locked): re-clicking the locked monster releases it; switching
/// clears the old lock first. This activates the game's lock-on mode (camera) — intended, that's what was asked.
/// </summary>
internal sealed partial class NearbyMonsterTracker
{
    // Live soft/lock target uuid off the local player (EAttrType.AttrTargetId=30) — refreshed each Poll (see the main
    // file), read by the window to highlight the matching row. 0 when unread / no current target.
    public long CurrentTargetUuid { get; private set; }

    private const int AttrTargetIdId = 30;    // EAttrType.AttrTargetId
    private object?   _attrTargetIdBox;        // lazy Enum box, resolved in EnsureApi alongside the HP attr boxes

    // ── ZEntity.GetComponent<TargetComp>() + the TargetComp target/lock methods — resolved once, invoked per click. ──
    private MethodInfo? _miGetTargetComp;      // closed ZEntity.GetComponent<TargetComp>()
    private MethodInfo? _miForceSetTarget;     // TargetComp.ForceSetTarget(long) — soft-select the current target
    private MethodInfo? _miLockTargetOrCancel; // TargetComp.LockTargetOrCancel() — TOGGLES the hard lock on the current selection
    private MethodInfo? _miCancelLock;         // TargetComp.CancelLock() — releases the hard lock
    private bool        _targetApiResolved;
    private bool        _lockFailLogged;       // rate-limit: warn on the FIRST lock failure only (clicks can repeat fast)

    // ── ZWorld.LockTargetUuid — the current HARD-locked uuid (0 = nothing locked); drives the toggle state above AND
    //    the row padlock. Resolved in EnsureApi (ResolveLockApi) so the read side has it without a prior click. ──
    private Type?         _zworldType;         // Panda.ZGame.ZWorld singleton
    private PropertyInfo? _piLockTargetUuid;   // ZWorld.LockTargetUuid getter (fallback path)
    private Func<object?>?      _getZWorld;    // bound static ZSingleton<ZWorld>.Instance getter (null → GetSingleton)
    private Func<object, long>? _getLockUuid;  // bound open-instance LockTargetUuid getter (null → GetValue)

    /// <summary>
    /// The game's HARD-locked target uuid (<c>ZWorld.LockTargetUuid</c>; 0 = none) — the lock-on from either the
    /// game's lock key or a row click. Read ONCE per Poll (≤~6.7 Hz, see <c>PollIntervalMs</c>), never per row/frame;
    /// the window compares each row against it to show the gold padlock.
    /// </summary>
    public long LockedTargetUuid { get; private set; }

    /// <summary>
    /// HARD-lock the player's in-game combat target to <paramref name="uuid"/> (the game's lock-on, so it sticks
    /// against continuous auto-target selection). Called directly from a <c>SelectableElement.OnClick</c> (already on
    /// the Unity main thread → no <c>IFramework.Post</c> needed). No-op if not in world, or if the local entity /
    /// <c>TargetComp</c> / the target+lock methods can't be resolved.
    /// </summary>
    public void SetGameTarget(long uuid)
    {
        try
        {
            if (uuid == 0) return;
            if (!_services.ClientState.IsLoggedIn) return;
            if (!EnsureApi()) return;   // shares the read-side reflection resolve (ZEntityMgr / GetEntity / GetAttr)

            long localUuid = _services.CombatSnapshot.LocalEntityId.Value;
            if (localUuid == 0) return;

            var mgr = _piEntMgrStaticInstance?.GetValue(null) ?? StellarInterop.GetSingleton(_entMgrType);
            if (mgr == null) return;
            var localEnt = GetEntityObj(mgr, localUuid);
            if (localEnt == null) return;

            var comp = GetTargetComp(localEnt);   // also resolves the lock methods (ZWorld.LockTargetUuid: EnsureApi)
            if (comp == null || _miForceSetTarget == null || _miLockTargetOrCancel == null || _miCancelLock == null)
            {
                if (!_lockFailLogged)
                {
                    _lockFailLogged = true;
                    _services.Log.Warning($"[Nearby] lock unavailable (comp={comp != null} setter={_miForceSetTarget != null} " +
                        $"lock={_miLockTargetOrCancel != null} unlock={_miCancelLock != null})");
                }
                return;
            }

            ApplyHardLock(comp, uuid);
        }
        catch (Exception ex)
        {
            if (!_lockFailLogged)
            {
                _lockFailLogged = true;
                _services.Log.Warning($"[Nearby] lock uuid={uuid} error: {ex.InnerException?.Message ?? ex.Message}");
            }
        }
    }

    // Engage the game's HARD lock on uuid. LockTargetOrCancel is a TOGGLE on the current selection, so the sequence is
    // state-managed off the live ZWorld.LockTargetUuid: re-clicking the already-locked monster releases it; otherwise
    // drop any existing lock first (the toggle would cancel that instead of locking the new one), select this monster,
    // then toggle the now-unlocked selection ON so it sticks.
    // comp + all three methods are guaranteed non-null by the caller.
    private void ApplyHardLock(object comp, long uuid)
    {
        long lockedWas = ReadLockTargetUuid();
        if (lockedWas == uuid)
        {
            _miCancelLock!.Invoke(comp, null);                       // clicked the already-locked monster → toggle lock OFF
            LockedTargetUuid = 0;       // optimistic padlock — the next Poll reconciles against ZWorld.LockTargetUuid
        }
        else
        {
            if (lockedWas != 0) _miCancelLock!.Invoke(comp, null);   // release the previous lock first
            _miForceSetTarget!.Invoke(comp, new object[] { uuid });  // make this monster the current selection
            _miLockTargetOrCancel!.Invoke(comp, null);               // now-unlocked selection → this hard-locks it
            CurrentTargetUuid = uuid;   // optimistic — the next Poll reconciles against the live AttrTargetId read
            LockedTargetUuid  = uuid;   // optimistic padlock — likewise reconciled against ZWorld.LockTargetUuid
        }
    }

    // ZWorld.LockTargetUuid (0 = nothing hard-locked). Runs every Poll, so it goes through the bound delegates (no
    // PropertyInfo.GetValue object[]/long box per read — Plugin-Performance-Rules §2); the reflection path is only the
    // fallback if binding failed. Same value TargetInfoTracker.ReadLockTargetUuid reads.
    private long ReadLockTargetUuid()
    {
        if (_zworldType == null || _piLockTargetUuid == null) return 0;
        try
        {
            var inst = _getZWorld != null ? _getZWorld() : StellarInterop.GetSingleton(_zworldType);
            if (inst == null) return 0;
            return _getLockUuid != null ? _getLockUuid(inst) : Convert.ToInt64(_piLockTargetUuid.GetValue(inst) ?? 0L);
        }
        catch { return 0; }
    }

    // Resolve ZWorld + LockTargetUuid once (from EnsureApi) and bind both getters into delegates. Fail-open: any miss
    // just leaves the padlock off and the click toggle treating "nothing locked".
    private void ResolveLockApi()
    {
        try
        {
            var zworld = StellarInterop.FindType("Panda.ZGame.ZWorld");
            if (zworld == null) return;
            _zworldType = zworld;
            _piLockTargetUuid = zworld.GetProperty("LockTargetUuid", BindingFlags.Public | BindingFlags.Instance);

            // Static Instance lives on the generic base ZSingleton<ZWorld> → FlattenHierarchy. Func<object> binds by
            // reference-return covariance; a static getter needs no target.
            var inst = zworld.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetGetMethod();
            if (inst != null && !inst.ReturnType.IsValueType)
                try { _getZWorld = (Func<object>)Delegate.CreateDelegate(typeof(Func<object>), inst); } catch { _getZWorld = null; }

            var g = _piLockTargetUuid?.GetGetMethod();
            if (g != null && !g.IsStatic && g.ReturnType == typeof(long) && !zworld.IsValueType)
                _getLockUuid = (Func<object, long>?)typeof(NearbyMonsterTracker)
                    .GetMethod(nameof(MakeLongGetter), BindingFlags.NonPublic | BindingFlags.Static)!
                    .MakeGenericMethod(g.DeclaringType!).Invoke(null, new object[] { g });
        }
        catch (Exception ex) { _services.Log.Warning($"[Nearby] lock api resolve error: {ex.Message}"); }
    }

    // Open-instance delegate over an interop long getter, wrapped once as Func<object, long> (one cast + one call per
    // read). Mirrors RaidManager MechanicCalloutTracker.MakeTypedGetter.
    private static Func<object, long> MakeLongGetter<T>(MethodInfo getter) where T : class
    {
        var d = (Func<T, long>)Delegate.CreateDelegate(typeof(Func<T, long>), getter);
        return o => d((T)o);
    }

    // Resolve ZEntity.GetComponent<TargetComp>() (closed generic) + TargetComp.ForceSetTarget(long) once off the live
    // local entity, then invoke the closed generic per click. Mirrors CastPatch.GetSkillComp: GetComponent<T>() is
    // declared on a ZEntity base type and FlattenHierarchy doesn't surface generic-method defs reliably, so walk up
    // from the concrete runtime type to find the generic definition.
    private object? GetTargetComp(object localEnt)
    {
        if (!_targetApiResolved)
        {
            _targetApiResolved = true;
            try
            {
                var tcType = StellarInterop.FindType("Panda.ZGame.TargetComp");
                MethodInfo? gen = null;
                var cur = localEnt.GetType();
                while (cur != null && gen == null)
                {
                    foreach (var mi in cur.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                        if (mi.Name == "GetComponent" && mi.IsGenericMethodDefinition && mi.GetParameters().Length == 0)
                        { gen = mi; break; }
                    cur = cur.BaseType;
                }
                if (tcType != null && gen != null) _miGetTargetComp = gen.MakeGenericMethod(tcType);
                if (tcType != null)
                    foreach (var m in tcType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                    {
                        var ps = m.GetParameters();
                        if (m.Name == "ForceSetTarget" && ps.Length == 1 && ps[0].ParameterType == typeof(long)) _miForceSetTarget = m;
                        else if (m.Name == "LockTargetOrCancel" && ps.Length == 0) _miLockTargetOrCancel = m;
                        else if (m.Name == "CancelLock" && ps.Length == 0) _miCancelLock = m;
                    }
                // ZWorld.LockTargetUuid (the toggle state) is resolved by EnsureApi → ResolveLockApi, which ran first.
            }
            catch (Exception ex) { _services.Log.Warning($"[Nearby] target api resolve error: {ex.Message}"); }
        }
        if (_miGetTargetComp == null) return null;
        try { return _miGetTargetComp.Invoke(localEnt, null); } catch { return null; }
    }
}
