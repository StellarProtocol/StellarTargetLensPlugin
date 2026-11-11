using System;
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

    // Boss > Elite > Normal, guarded (mirrors the previous inline resolve in AccumulateDict). Drives the row's
    // rank (boss/elite colour) and the bosses-first sort.
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
