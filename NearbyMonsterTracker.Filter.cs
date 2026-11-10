using System;
using System.Collections.Generic;
using System.Reflection;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.TargetLens;

/// <summary>
/// Camp-relation ally filter for <see cref="NearbyMonsterTracker"/>: keeps only entities that are enemies of the
/// LOCAL player (Hostile / Neutral) and drops allied/friendly/team entities plus non-hostile ally summons. Split out
/// of <c>NearbyMonsterTracker.cs</c> to keep that file under the 500-LoC gate.
///
/// Primary source: the static extension <c>Panda.ZGame.EntityAttrExtensions.GetCampWithPlayer(ZPureEntity)</c> →
/// <c>ECampRelation { None=0, Hostile=1, Neutral=2, Friendly=3, Team=4 }</c> — the relation to the local player is
/// PRECOMPUTED, so we invoke the static method with the monster <c>ZEntity</c> as arg 0 (<c>ZEntity : ZPureEntity</c>,
/// so the entity is a valid arg) and read the returned enum as its int value. Note there are THREE
/// <c>EntityAttrExtensions</c> classes in the dump (<c>Panda.ZGame</c>, <c>Panda.ZGame.Pure</c>,
/// <c>Panda.ZGame.Home</c>); only the <c>Panda.ZGame</c> one declares <c>GetCampWithPlayer</c>.
///
/// Fallback when the primary reads <c>None</c> (unpopulated) — <c>ZCampMgr.IsHostile(ZEntity a, ZEntity b)</c> off the
/// <c>ZSingleton&lt;ZCampMgr&gt;.Instance</c> (resolved with <c>FlattenHierarchy</c> per the project ZSingleton gotcha),
/// comparing the monster to the local player entity. All params are plain <c>ZEntity</c> (no <c>in</c>/<c>ref</c> → no
/// IL2CPP trampoline crash).
///
/// FAIL OPEN, always: if NEITHER path binds, no filtering happens (show all monsters) — losing ally-exclusion beats
/// shipping an empty HUD if the API doesn't bind on this build. Every read is try/catch-guarded and a per-entity miss
/// defaults that entity to KEEP so a transient failure never blanks the list.
/// </summary>
internal sealed partial class NearbyMonsterTracker
{
    // Panda.ZGame.ECampRelation values (see class summary).
    private const int CampNone = 0, CampHostile = 1, CampNeutral = 2, CampFriendly = 3, CampTeam = 4;

    private bool          _campResolved;
    private bool          _campFilterActive;      // true iff at least one camp path bound → filtering is enabled
    private MethodInfo?   _miGetCampWithPlayer;   // static EntityAttrExtensions.GetCampWithPlayer(ZPureEntity) → ECampRelation
    private Type?         _campMgrType;           // Panda.ZGame.ZCampMgr
    private PropertyInfo? _piCampMgrInstance;     // ZCampMgr.Instance (static ZSingleton accessor — FlattenHierarchy)
    private MethodInfo?   _miIsHostile;           // ZCampMgr.IsHostile(ZEntity, ZEntity) → bool
    private PropertyInfo? _piIsSummon;            // ZEntity.IsSummon (bool)

    // Resolved once from EnsureApi (guarded). Sets _campFilterActive only when the primary OR the fallback is usable;
    // otherwise the filter stays inactive and PassesCampFilter fails open (shows everything).
    private void ResolveCampApi(Type entType)
    {
        if (_campResolved) return;
        _campResolved = true;
        try
        {
            var extType = StellarInterop.FindType("Panda.ZGame.EntityAttrExtensions");
            if (extType != null)
                foreach (var m in extType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    if (m.Name == "GetCampWithPlayer" && m.GetParameters().Length == 1)
                    { _miGetCampWithPlayer = m; break; }

            _campMgrType = StellarInterop.FindType("Panda.ZGame.ZCampMgr");
            if (_campMgrType != null)
            {
                _piCampMgrInstance = _campMgrType.GetProperty("Instance",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                foreach (var m in _campMgrType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                    if (m.Name == "IsHostile" && m.GetParameters().Length == 2)
                    { _miIsHostile = m; break; }
            }

            _piIsSummon = entType.GetProperty("IsSummon", BindingFlags.Public | BindingFlags.Instance);

            bool fallbackOk = _miIsHostile != null && _piCampMgrInstance != null;
            _campFilterActive = _miGetCampWithPlayer != null || fallbackOk;
            if (!_campFilterActive)
                _services.Log.Warning("[Nearby] camp filter unresolved — showing ALL monsters (no ally exclusion)");
            _services.Log.Info($"[Nearby] camp filter resolved getCampWithPlayer={_miGetCampWithPlayer != null} " +
                $"campMgr={fallbackOk} isSummon={_piIsSummon != null} active={_campFilterActive}");
        }
        catch (Exception ex) { _services.Log.Warning($"[Nearby] camp filter resolve error: {ex.Message}"); }
    }

    /// <summary>
    /// True = keep this entity in the Nearby list; false = drop it as an ally/friendly/team/non-hostile-summon.
    /// Fail-open: an inactive filter or any read miss defaults to KEEP so a transient failure never blanks the HUD.
    /// </summary>
    private bool PassesCampFilter(object ent, object? localEnt)
    {
        if (!_campFilterActive) return true;   // neither camp path bound → no filtering (show all)
        try
        {
            int rel = ReadCampRelation(ent);   // ECampRelation as int; CampNone if primary is missing / returns None / throws

            // None = unpopulated. Resolve via the ZCampMgr.IsHostile fallback: keep only if it confirms hostility.
            if (rel == CampNone)
            {
                if (TryIsHostile(ent, localEnt, out bool hostile))
                    rel = hostile ? CampHostile : CampFriendly;   // false → non-enemy bucket (drop); true → hostile (keep)
                else
                    return true;   // truly undeterminable → fail open, keep
            }

            // Ally-summon guard: a non-hostile summon is an ally pet/escort → drop. A HOSTILE summon is an enemy add → keep.
            if (rel != CampHostile && IsSummon(ent)) return false;

            return rel switch
            {
                CampHostile => true,   // enemy
                CampNeutral => true,   // attackable non-ally
                _           => false,  // Friendly / Team (and the collapsed non-hostile None) → drop
            };
        }
        catch { return true; }   // per-entity fail-open
    }

    // Invoke the static extension with the monster as arg 0. CampNone on missing method / null / non-numeric / throw.
    private int ReadCampRelation(object ent)
    {
        if (_miGetCampWithPlayer == null) return CampNone;
        try
        {
            var r = _miGetCampWithPlayer.Invoke(null, new[] { ent });
            return r == null ? CampNone : Convert.ToInt32(r);
        }
        catch { return CampNone; }
    }

    // ZCampMgr.IsHostile(ent, localEnt). Returns false (could-not-determine) when the singleton, setter, or local
    // entity is unavailable — the caller distinguishes "returned false" (drop) from "couldn't determine" (fail open).
    private bool TryIsHostile(object ent, object? localEnt, out bool hostile)
    {
        hostile = false;
        if (_miIsHostile == null || _piCampMgrInstance == null || localEnt == null) return false;
        try
        {
            var mgr = _piCampMgrInstance.GetValue(null);
            if (mgr == null) return false;
            if (_miIsHostile.Invoke(mgr, new[] { ent, localEnt }) is bool b) { hostile = b; return true; }
            return false;
        }
        catch { return false; }
    }

    private bool IsSummon(object ent) => ReadBool(ent, _piIsSummon);

    // ── Targetability gate (AttrCantHit=723) + validation diagnostic ─────────────────────────────────────────────
    // EAttrType ids (release_3.7). 723 is an INVERSE flag ON PURPOSE: non-zero = cannot be hit/targeted, so an
    // absent/default attr reads 0 → treated as hittable → KEEP. That makes the gate fail-open — a normal mob that
    // never carries the attr is never wrongly dropped. 720/724 are read for the LOG ONLY (reading 0 when absent would
    // wrongly drop normal mobs, so they must NOT be filtered on).
    private const int AttrCantHitId   = 723;  // EAttrType.AttrCantHit — non-zero = immune/untargetable (the gate)
    private const int AttrCanBeHitId  = 720;  // EAttrType.AttrCanBeHit — diagnostic only
    private const int AttrCanHatredId = 724;  // EAttrType.AttrCanBeHatredTarget — diagnostic only

    private object? _attrCantHitBox;   // lazy Enum boxes, resolved in EnsureApi via ResolveTargetabilityApi
    private object? _attrCanBeHitBox;
    private object? _attrCanHatredBox;

    // 723/720/724 are stored by the game as INT32 scalar flags, NOT Int64. Reading them through the GetAttr<long>
    // closure makes the game emit `arr type err, type=Int64, enum=AttrCantHit` (Debug.LogError WITH a stack capture)
    // on EVERY read and return default(long)=0 — a Player.log flood on the per-mob Nearby poll AND a silently fail-open
    // gate (723 always read 0 = "hittable", so nothing was ever dropped).
    // ⚠️ GetAttr<object> is NOT a fix: System.Object has no IL2CPP class pointer, so closing GetAttr<T> over
    // typeof(object) makes Il2CppInterop resolve Class::FromIl2CppType(0x20) — every call threw (so this gate stayed
    // fail-open anyway), and behind the framework's native fast-path stub it crashed the game. The closure was removed,
    // so _miGetAttrObject stays null and ReadAttrObjToInt64 returns 0 (keep) until these reads move to a real
    // instantiation (GetAttr<int>, as TargetInfoTracker's _miGetAttrInt already uses for the break gauge).
    // HP (AttrHp/AttrMaxHp) is legitimately Int64 and STAYS on _miGetAttrLong (ReadAttr) — do not route it here.
    private MethodInfo? _miGetAttrObject = null;   // unset — see above; targetability int32 reads fail-open

    // Dedupe for the validation diagnostic: log the raw 723/720/724 readings once per distinct monster NAME (names
    // are few, so an unbounded set is fine). Cleared on each resolve/scene change (the first-poll-after-resolve latch
    // in Poll) so the readings re-emit per scene. NOT used for filtering.
    private readonly HashSet<string> _targetabilitySeenNames = new();

    // Sticky targetability latch: uuids that have read targetable (cantHit==0) at least once this encounter.
    // AttrCantHit(723) is a LIVE flag — a boss/mob that goes invincible mid-fight (mechanic/immune phase) would
    // otherwise drop out of the list and flicker. So once a monster has EVER been targetable we keep it even when it
    // later reads untargetable; only monsters that are NEVER targetable (decorative/gimmick) stay excluded. Cleared on
    // the SAME first-poll-after-resolve latch as _targetabilitySeenNames so it never carries stale uuids across
    // encounters or grows unbounded.
    private readonly HashSet<long> _everTargetable = new();

    // Resolve the targetability attr boxes. Called from EnsureApi; fail-soft — a null box just makes ReadAttr return 0
    // (keep), so this never gates the main _apiOk and a resolve miss degrades to "show all" rather than blanking.
    private void ResolveTargetabilityApi(Type attrEnum)
    {
        try
        {
            _attrCantHitBox   = Enum.ToObject(attrEnum, AttrCantHitId);
            _attrCanBeHitBox  = Enum.ToObject(attrEnum, AttrCanBeHitId);
            _attrCanHatredBox = Enum.ToObject(attrEnum, AttrCanHatredId);
            // No GetAttr<object> closure here — see _miGetAttrObject (it crashed the game).
        }
        catch (Exception ex) { _services.Log.Warning($"[Nearby] targetability api resolve error: {ex.Message}"); }
    }

    /// <summary>
    /// Targetability gate for monsters that already passed the camp + alive checks. Reads the inverse immunity attr
    /// <c>AttrCantHit</c> (723): non-zero means the monster currently cannot be hit/targeted
    /// (invulnerable/decorative/gimmick). Absent/0 — the common case — or any read failure → true (keep): fail-open,
    /// so a normal mob that never carries the attr is never wrongly dropped.
    ///
    /// STICKY latch (see <see cref="_everTargetable"/>): 723 is a LIVE flag, so a boss/mob that goes invincible
    /// mid-fight would otherwise flicker out of the list. So the gate keeps any monster that has read targetable at
    /// least once this encounter: <c>cantHit==0</c> → latch the uuid and keep; <c>cantHit!=0</c> → keep ONLY if the
    /// uuid was latched before (transient immunity on something we already saw hittable), otherwise drop (never been
    /// targetable → decorative/gimmick).
    ///
    /// ⚠️ UNVALIDATED: the IL2CPP dump can't confirm the server actually sets 723 on the untargetable monsters the
    /// user sees, so this also emits a rate-limited diagnostic — once per distinct monster NAME (cached in
    /// <see cref="_targetabilitySeenNames"/> so it never spams per-frame) it logs the raw 723/720/724 readings.
    /// <c>AttrCanBeHit</c> (720) and <c>AttrCanBeHatredTarget</c> (724) are read for the LOG ONLY and are NOT filtered
    /// on. Checking the log against a known untargetable monster confirms the gate or tells us to pivot (e.g. to
    /// MonsterTable <c>BkCanBeHit</c> / invincible buff 521001).
    /// </summary>
    private bool PassesTargetabilityGate(object ent, long uuid, string name, string rank, long hp, long maxHp)
    {
        long cantHit = ReadAttrObjToInt64(ent, _attrCantHitBox);

        if (!string.IsNullOrWhiteSpace(name) && _targetabilitySeenNames.Add(name))
        {
            long canBeHit  = ReadAttrObjToInt64(ent, _attrCanBeHitBox);
            long canHatred = ReadAttrObjToInt64(ent, _attrCanHatredBox);
            _services.Log.Info($"[Nearby] targetability name='{name}' cantHit(723)={cantHit} canBeHit(720)={canBeHit} " +
                $"canHatred(724)={canHatred} hp={hp}/{maxHp} rank={rank}");
        }

        // 0 / absent / read-miss = currently targetable → latch and keep (fail-open). Non-zero = currently immune →
        // keep only if this uuid was EVER targetable (transient invincibility), else drop (never targetable → gimmick).
        if (cantHit == 0) { _everTargetable.Add(uuid); return true; }
        return _everTargetable.Contains(uuid);
    }

    // Type-agnostic scalar read for the int32 targetability flags (723/720/724) through _miGetAttrObject (currently
    // unset → always 0; see its comment) so the game does NOT log the Int64 mismatch error the long path triggered; converts
    // the boxed value to Int64. Null → 0 and any Convert failure → 0, so a missing/unconvertible attr degrades to
    // "hittable" → fail-open keep, matching ReadAttr's behaviour. NOT for HP — that stays Int64 on ReadAttr.
    private long ReadAttrObjToInt64(object ent, object? attrBox)
    {
        if (attrBox == null || _miGetAttrObject == null) return 0;
        object? boxed;
        try { boxed = _miGetAttrObject.Invoke(ent, new object[] { attrBox, true }); }
        catch { return 0; }
        if (boxed == null) return 0;
        try { return Convert.ToInt64(boxed); }
        catch { return 0; }
    }

    // Boss > Elite > Normal, guarded (mirrors the previous inline resolve in AccumulateDict). Used by the row entry
    // and the targetability diagnostic so both agree.
    private string ResolveRank(object ent)
    {
        try
        {
            if (ReadBool(ent, _piIsBoss)) return "Boss";
            if (ReadBool(ent, _piIsElite)) return "Elite";
        }
        catch { /* fall through to Normal */ }
        return "Normal";
    }
}
