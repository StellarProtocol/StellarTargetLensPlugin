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
/// selection, so a forced SOFT target may get re-selected away. This is the plain soft-set only — no hard-lock
/// (<c>LockTargetOrCancel</c>) — by design; hard-lock is a deliberate fast-follow if in-game testing shows it won't hold.
/// </summary>
internal sealed partial class NearbyMonsterTracker
{
    // Live soft/lock target uuid off the local player (EAttrType.AttrTargetId=30) — refreshed each Poll (see the main
    // file), read by the window to highlight the matching row. 0 when unread / no current target.
    public long CurrentTargetUuid { get; private set; }

    private const int AttrTargetIdId = 30;    // EAttrType.AttrTargetId
    private object?   _attrTargetIdBox;        // lazy Enum box, resolved in EnsureApi alongside the HP attr boxes

    // ── ZEntity.GetComponent<TargetComp>() + TargetComp.ForceSetTarget(long) — resolved once, invoked per click. ──
    private MethodInfo? _miGetTargetComp;      // closed ZEntity.GetComponent<TargetComp>()
    private MethodInfo? _miForceSetTarget;     // TargetComp.ForceSetTarget(long)
    private bool        _targetApiResolved;
    private bool        _targetSetLogged;      // one-line diagnostic on first use / first failure (matches the other logs)

    /// <summary>
    /// Set the player's in-game combat target to <paramref name="uuid"/>. Called directly from a
    /// <c>SelectableElement.OnClick</c> (already on the Unity main thread → no <c>IFramework.Post</c> needed).
    /// No-op if not in world, or if the local entity / <c>TargetComp</c> / setter can't be resolved.
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

            var comp = GetTargetComp(localEnt);
            if (comp == null || _miForceSetTarget == null)
            {
                if (!_targetSetLogged)
                {
                    _targetSetLogged = true;
                    _services.Log.Info($"[Nearby] set target uuid={uuid} ok=false (comp={comp != null} setter={_miForceSetTarget != null})");
                }
                return;
            }

            _miForceSetTarget.Invoke(comp, new object[] { uuid });
            CurrentTargetUuid = uuid;   // optimistic — the next Poll reconciles against the live AttrTargetId read
            if (!_targetSetLogged) { _targetSetLogged = true; _services.Log.Info($"[Nearby] set target uuid={uuid} ok=true"); }
        }
        catch (Exception ex)
        {
            if (!_targetSetLogged)
            {
                _targetSetLogged = true;
                _services.Log.Warning($"[Nearby] set target uuid={uuid} error: {ex.InnerException?.Message ?? ex.Message}");
            }
        }
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
                        if (m.Name == "ForceSetTarget" && m.GetParameters() is { Length: 1 } ps && ps[0].ParameterType == typeof(long))
                        { _miForceSetTarget = m; break; }
                _services.Log.Info($"[Nearby] target api tc={tcType != null} getComp={_miGetTargetComp != null} setter={_miForceSetTarget != null}");
            }
            catch (Exception ex) { _services.Log.Warning($"[Nearby] target api resolve error: {ex.Message}"); }
        }
        if (_miGetTargetComp == null) return null;
        try { return _miGetTargetComp.Invoke(localEnt, null); } catch { return null; }
    }
}
