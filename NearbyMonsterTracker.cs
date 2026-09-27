using System;
using System.Collections.Generic;
using System.Reflection;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;

namespace Stellar.TargetLens;

/// <summary>
/// Enumerates the nearby (AOI / streamed-in) monster set and produces a distance-sorted, alive-only list for the
/// standalone "Nearby Monsters" overlay (see <c>Plugin.NearbyWindow.cs</c>). Self-contained: it resolves its own
/// reflection in <see cref="EnsureApi"/> and the result is frame-gated (<see cref="Current"/>) so the window getters
/// share a single dict-walk per rendered frame.
///
/// Source: <c>Panda.ZGame.ZEntityMgr</c> (a <c>ZSingleton</c>, read via its static <c>Instance</c> accessor — the
/// same path <see cref="TargetInfoTracker"/> uses, which resolves ECS monster entities in-game). Its internal
/// <c>MonsterDict</c> (<c>Dictionary&lt;long, ZEntity&gt;</c>, key = uuid) is UNIONed with the separate <c>BossDict</c>
/// so bosses (which may live only in the boss dict) appear. The raw <c>Dictionary.GetEnumerator()</c> is unusable via
/// reflection (Ambiguous), so we iterate the <c>.Keys</c> KeyCollection and resolve each entity through the manager's
/// own <c>GetEntity(long)</c> — the proven per-uuid resolve. Dict membership is NOT authoritative death (removal
/// lags): alive/dead is decided by live HP only.
/// </summary>
internal sealed partial class NearbyMonsterTracker
{
    // A single nearby-monster row. Distance is metres (target root vs local-player root); Rank is "Boss"/"Elite"/"Normal".
    public readonly struct NearbyEntry
    {
        public readonly long   Uuid;
        public readonly string Name;
        public readonly long   Hp;
        public readonly long   MaxHp;
        public readonly float  Distance;
        public readonly string Rank;
        public NearbyEntry(long uuid, string name, long hp, long maxHp, float distance, string rank)
        {
            Uuid = uuid; Name = name; Hp = hp; MaxHp = maxHp; Distance = distance; Rank = rank;
        }
    }

    private const int AttrHpId    = 11310; // EAttrType.AttrHp
    private const int AttrMaxHpId  = 11320; // EAttrType.AttrMaxHp
    private const int AttrIdId     = 10;    // EAttrType.AttrId — MonsterTable config id (last-ditch, usually 0)

    private const int   MaxRows      = 8;    // top-N cap (fixed row pool in the window)
    private const float MaxDistanceM = 60f;  // hide anything beyond ~60 m; unknown distance is excluded entirely

    private readonly IPluginServices _services;
    public NearbyMonsterTracker(IPluginServices services) => _services = services;

    // Frame-gated cache: multiple window getters resolve to one dict-walk per frame.
    private int                    _cacheFrame = -1;
    private readonly List<NearbyEntry> _cache  = new();

    public IReadOnlyList<NearbyEntry> Current
    {
        get
        {
            int f = Time.frameCount;
            if (f == _cacheFrame) return _cache;
            _cacheFrame = f;
            Poll();
            return _cache;
        }
    }

    // Scratch reused across frames so a busy AOI doesn't churn the GC every frame.
    private readonly List<NearbyEntry> _scratch = new();
    private readonly HashSet<long>     _seen    = new();

    private void Poll()
    {
        _cache.Clear();
        try
        {
            if (!_services.ClientState.IsLoggedIn) return;
            if (!EnsureApi()) return;

            long localUuid = _services.CombatSnapshot.LocalEntityId.Value;
            if (localUuid == 0) return;

            var mgr = _piEntMgrStaticInstance?.GetValue(null) ?? StellarInterop.GetSingleton(_entMgrType);
            if (mgr == null) return;

            // Local player position — the distance origin. If it isn't loaded, every distance is unknown and the
            // whole (distance-capped) list stays empty, which is the intended behaviour.
            var localEnt = GetEntityObj(mgr, localUuid);
            // Live current-target uuid (EAttrType.AttrTargetId=30) off the local player — read here (we already hold
            // the local entity) so the window's row highlight self-syncs to the ACTUAL target, whether it was set by
            // our click or by the game's auto-target. Guarded read: 0 on miss / no target. (Write side: SetGameTarget.)
            CurrentTargetUuid = localEnt != null ? ReadAttr(localEnt, _attrTargetIdBox) : 0;
            if (localEnt == null || !TryGetPosition(localEnt, out var localPos)) return;

            _scratch.Clear();
            _seen.Clear();
            AccumulateDict(mgr, _dictReader, localUuid, localPos);   // MonsterDict
            AccumulateDict(mgr, _bossReader, localUuid, localPos);   // BossDict (union — a boss may not be in MonsterDict)

            // Bosses first, then elites, then normal; within a tier by ascending distance. Then cap at MaxRows.
            _scratch.Sort((a, b) =>
            {
                int ra = RankOrder(a.Rank), rb = RankOrder(b.Rank);
                if (ra != rb) return ra.CompareTo(rb);
                return a.Distance.CompareTo(b.Distance);
            });
            int n = Math.Min(MaxRows, _scratch.Count);
            for (int i = 0; i < n; i++) _cache.Add(_scratch[i]);
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[Nearby] poll error: {ex.InnerException?.Message ?? ex.Message}");
        }
    }

    private static int RankOrder(string rank) => rank switch { "Boss" => 0, "Elite" => 1, _ => 2 };

    // Walk one dict's .Keys, resolve each entity, filter to alive monsters within range, and append to _scratch.
    // Fully guarded — a missing/absent dict just contributes nothing.
    private void AccumulateDict(object mgr, Func<object, object?>? reader, long localUuid, Vector3 localPos)
    {
        if (reader == null) return;
        object? dict;
        try { dict = reader(mgr); } catch { return; }
        if (dict == null) return;

        object? keys;
        try { keys = dict.GetType().GetProperty("Keys", BindingFlags.Public | BindingFlags.Instance)?.GetValue(dict); }
        catch { return; }
        if (keys == null) return;

        foreach (var keyObj in ReflectEnumerate(keys))
        {
            if (keyObj == null) continue;
            long uuid;
            try { uuid = Convert.ToInt64(keyObj); } catch { continue; }
            if (uuid == 0 || uuid == localUuid) continue;
            if (!_seen.Add(uuid)) continue;   // dedupe across the two dicts

            // Monster filter via the uuid bit layout (entType = (uuid >> 6) & 31; EntMonster = 1). Cheap and needs
            // no per-entity reflection — the dicts already hold only monsters, so this is a belt-and-suspenders guard.
            if (((uuid >> 6) & 31) != 1) continue;

            var ent = GetEntityObj(mgr, uuid);
            if (ent == null) continue;

            long hp    = ReadAttr(ent, _attrHpBox);
            long maxHp = ReadAttr(ent, _attrMaxHpBox);
            if (maxHp <= 0 || hp <= 0) continue;   // alive only (dict membership is NOT authoritative death)

            if (!TryGetPosition(ent, out var pos)) continue;   // unknown distance → excluded from the capped list
            float dist = Vector3.Distance(pos, localPos);
            if (dist > MaxDistanceM) continue;

            string rank = "Normal";
            try
            {
                if (ReadBool(ent, _piIsBoss)) rank = "Boss";
                else if (ReadBool(ent, _piIsElite)) rank = "Elite";
            }
            catch { rank = "Normal"; }

            _scratch.Add(new NearbyEntry(uuid, ResolveMonsterName(ent), hp, maxHp, dist, rank));
        }
    }

    // Duck-typed enumerator walk (mirrors Plugin.SelectWindow.Tables.cs). The KeyCollection's single public
    // GetEnumerator avoids the Ambiguous raw-Dictionary enumerator. Struct enumerators mutate in-box across
    // reflective MoveNext invokes, which is the established working pattern here.
    private static IEnumerable<object?> ReflectEnumerate(object collection)
    {
        object? enumerator;
        MethodInfo? miNext; PropertyInfo? piCur;
        try
        {
            var mi = collection.GetType().GetMethod("GetEnumerator", BindingFlags.Public | BindingFlags.Instance);
            if (mi == null) yield break;
            enumerator = mi.Invoke(collection, null);
            if (enumerator == null) yield break;
            var et = enumerator.GetType();
            miNext = et.GetMethod("MoveNext",  BindingFlags.Public | BindingFlags.Instance);
            piCur  = et.GetProperty("Current", BindingFlags.Public | BindingFlags.Instance);
        }
        catch { yield break; }
        if (miNext == null || piCur == null) yield break;
        while (true)
        {
            bool moved;
            object? cur;
            try
            {
                moved = (bool)(miNext.Invoke(enumerator, null) ?? false);
                if (!moved) break;
                cur = piCur.GetValue(enumerator);
            }
            catch { yield break; }
            yield return cur;
        }
    }

    // ── Name resolution (monsters only — CombatLookup carries player names, so it is skipped here) ──
    // entRow_ bound row → _base.Name is the reliable source; falls back to the MonsterTableBase lookup keyed by the
    // config id (entRow_.Id → BaseId → AttrId(10)). Every miss degrades to "" then to a "#entId" placeholder.
    private string ResolveMonsterName(object ent)
    {
        try
        {
            long baseId = 0;
            try { baseId = _piBaseId?.GetValue(ent) is int bi ? bi : 0; } catch { baseId = 0; }
            var (rowId, rowName) = ReadEntConfig(ent);
            if (!string.IsNullOrEmpty(rowName)) return rowName;

            long attrId  = ReadAttr(ent, _attrIdBox);
            long configId = rowId > 0 ? rowId : (baseId > 0 ? baseId : attrId);
            string tblName = ResolveTableName(configId);
            return string.IsNullOrEmpty(tblName) ? "" : tblName;
        }
        catch { return ""; }
    }

    private (long id, string name) ReadEntConfig(object ent)
    {
        if (_fiEntRow == null) return (0, "");
        try
        {
            var cfg = _fiEntRow.GetValue(ent);
            if (cfg == null) return (0, "");
            var t = cfg.GetType();
            if (t != _entRowType)
            {
                _entRowType   = t;
                _piEntRowId   = t.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
                _fiEntRowBase = t.GetField("_base", BindingFlags.NonPublic | BindingFlags.Instance);
            }
            long id = _piEntRowId?.GetValue(cfg) is int i ? i : 0;
            var baseRow = _fiEntRowBase?.GetValue(cfg);
            string nm = (baseRow != null && _piMonName?.GetValue(baseRow) is string s) ? (s ?? "") : "";
            return (id, nm);
        }
        catch { return (0, ""); }
    }

    private string ResolveTableName(long configId)
    {
        if (configId <= 0 || _miMonGetTable == null || _piMonName == null) return "";
        try
        {
            _monTblObj ??= _miMonGetTable.Invoke(null, new object[] { false });
            var tableObj = _monTblObj;
            if (tableObj == null) return "";
            if (!_monTryGetResolved)
            {
                foreach (var m in tableObj.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                    if (m.Name == "TryGetValue" && m.GetParameters().Length == 3) { _miMonTryGetValue = m; break; }
                _monTryGetResolved = true;
            }
            if (_miMonTryGetValue == null) return "";
            var args = new object?[] { (int)configId, null, false };   // errorWhenNotFound = false — a miss must not log
            bool ok = _miMonTryGetValue.Invoke(tableObj, args) is bool b && b;
            var row = ok ? args[1] : null;
            if (row != null && _piMonName.GetValue(row) is string s) return s ?? "";
            return "";
        }
        catch { return ""; }
    }

    // ── Reflection resolve ───────────────────────────────────────────────────────
    private bool          _apiResolved;
    private bool          _apiOk;
    private Type?         _entMgrType;
    private PropertyInfo? _piEntMgrStaticInstance;   // ZEntityMgr.Instance (static) — the accessor that resolves entities in-game
    private MethodInfo?   _miGetEntity;              // ZEntityMgr.GetEntity(long)
    private Func<object, object?>? _dictReader;      // reads ZEntityMgr.MonsterDict (getter or backing field)
    private Func<object, object?>? _bossReader;      // reads ZEntityMgr.BossDict (getter or backing field)
    private MethodInfo?   _miGetAttrLong;            // ZEntity.GetAttr<long>(EAttrType, bool)
    private object?       _attrHpBox;
    private object?       _attrMaxHpBox;
    private object?       _attrIdBox;
    private PropertyInfo? _piIsBoss;
    private PropertyInfo? _piIsElite;
    private PropertyInfo? _piModel;                  // ZEntity.Model
    private PropertyInfo? _piModelGoComp;            // ZModel.ModelGoComp
    private PropertyInfo? _piGoPosition;             // GoComp.Position — lazy off the live GoComp type
    private bool          _goPosResolved;
    private FieldInfo?    _fiEntRow;                 // ZEntity.entRow_
    private PropertyInfo? _piBaseId;                 // ZEntity.BaseId
    private Type?         _entRowType;               // last-seen runtime type of entRow_ (lazy re-resolve)
    private PropertyInfo? _piEntRowId;               // <entRow_>.Id
    private FieldInfo?    _fiEntRowBase;             // <entRow_>._base
    private Type?         _monTblType;               // Bokura.MonsterTableBase
    private MethodInfo?   _miMonGetTable;            // static MonsterTableBase.GetTable(bool)
    private PropertyInfo? _piMonName;                // MonsterTableBase.Name
    private object?       _monTblObj;
    private MethodInfo?   _miMonTryGetValue;
    private bool          _monTryGetResolved;

    private bool EnsureApi()
    {
        if (_apiResolved) return _apiOk;
        _apiResolved = true;
        try
        {
            var entMgr   = StellarInterop.FindType("Panda.ZGame.ZEntityMgr");
            var entType  = StellarInterop.FindType("Panda.ZGame.ZEntity");
            var attrEnum = StellarInterop.FindType("Zproto.EAttrType");
            if (entMgr == null || entType == null || attrEnum == null)
            {
                _services.Log.Warning($"[Nearby] type resolve failed entMgr={entMgr != null} ent={entType != null} attrEnum={attrEnum != null}");
                return false;
            }

            _entMgrType = entMgr;
            const BindingFlags pubInst = BindingFlags.Public | BindingFlags.Instance;
            _piEntMgrStaticInstance = entMgr.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            foreach (var m in entMgr.GetMethods(pubInst))
                if (m.Name == "GetEntity" && m.GetParameters() is { Length: 1 } ps && ps[0].ParameterType == typeof(long))
                { _miGetEntity = m; break; }

            _dictReader = ResolveDictReader(entMgr, "MonsterDict", "monsterDict_");
            _bossReader = ResolveDictReader(entMgr, "BossDict",    "bossDict_");

            foreach (var m in entType.GetMethods(pubInst))
            {
                if (m.Name != "GetAttr" || !m.IsGenericMethodDefinition) continue;
                var ps = m.GetParameters();
                if (ps.Length == 2 && ps[0].ParameterType == attrEnum && ps[1].ParameterType == typeof(bool))
                { _miGetAttrLong = m.MakeGenericMethod(typeof(long)); break; }
            }

            _attrHpBox       = Enum.ToObject(attrEnum, AttrHpId);
            _attrMaxHpBox    = Enum.ToObject(attrEnum, AttrMaxHpId);
            _attrIdBox       = Enum.ToObject(attrEnum, AttrIdId);
            _attrTargetIdBox = Enum.ToObject(attrEnum, AttrTargetIdId);   // for the current-target highlight read (see .Target.cs)

            _piIsBoss  = entType.GetProperty("IsBoss",  pubInst);
            _piIsElite = entType.GetProperty("IsElite", pubInst);
            _fiEntRow  = entType.GetField("entRow_", BindingFlags.NonPublic | BindingFlags.Instance);
            _piBaseId  = entType.GetProperty("BaseId", pubInst);

            _piModel = entType.GetProperty("Model", pubInst);
            var zmodel = StellarInterop.FindType("Panda.ZGame.ZModel");
            if (zmodel != null) _piModelGoComp = zmodel.GetProperty("ModelGoComp", pubInst);

            var monTbl = StellarInterop.FindType("Bokura.MonsterTableBase");
            if (monTbl != null)
            {
                _monTblType = monTbl;
                foreach (var m in monTbl.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    if (m.Name == "GetTable" && m.GetParameters() is { Length: 1 } ps && ps[0].ParameterType == typeof(bool))
                    { _miMonGetTable = m; break; }
                _piMonName = monTbl.GetProperty("Name", pubInst);
            }

            _apiOk = _piEntMgrStaticInstance != null && _miGetEntity != null && _miGetAttrLong != null && _dictReader != null;
            _services.Log.Info($"[Nearby] api ok={_apiOk} getEnt={_miGetEntity != null} getAttr={_miGetAttrLong != null} " +
                $"entInst={_piEntMgrStaticInstance != null} monsterDict={_dictReader != null} bossDict={_bossReader != null} " +
                $"isBoss={_piIsBoss != null} isElite={_piIsElite != null} model={_piModel != null} goComp={_piModelGoComp != null} " +
                $"monTbl={_monTblType != null} monGetTable={_miMonGetTable != null} entRow={_fiEntRow != null} baseId={_piBaseId != null}");
            return _apiOk;
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[Nearby] api resolve error: {ex.Message}");
            return false;
        }
    }

    // MonsterDict/BossDict are internal properties. Prefer the getter (public or non-public); fall back to the
    // backing field. Returns a uniform mgr→dict reader, or null on total miss.
    private Func<object, object?>? ResolveDictReader(Type entMgr, string propName, string fieldName)
    {
        const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        try
        {
            var getter = entMgr.GetProperty(propName, any)?.GetGetMethod(true)
                         ?? entMgr.GetMethod("get_" + propName, any);
            if (getter != null) return mgr => getter.Invoke(mgr, null);
        }
        catch { /* fall through to the backing field */ }
        try
        {
            var fi = entMgr.GetField(fieldName, any);
            if (fi != null) return mgr => fi.GetValue(mgr);
        }
        catch { }
        return null;
    }

    private object? GetEntityObj(object mgr, long uuid)
    {
        try { return _miGetEntity!.Invoke(mgr, new object[] { uuid }); }
        catch { return null; }
    }

    private long ReadAttr(object ent, object? attrBox)
    {
        if (attrBox == null || _miGetAttrLong == null) return 0;
        try { return Convert.ToInt64(_miGetAttrLong.Invoke(ent, new object[] { attrBox, true })); }
        catch { return 0; }
    }

    private static bool ReadBool(object ent, PropertyInfo? pi)
    {
        if (pi == null) return false;
        try { return pi.GetValue(ent) is bool b && b; }
        catch { return false; }
    }

    // Entity root world position via ZEntity.Model → ZModel.ModelGoComp → GoComp.Position (mirrors TargetInfoTracker).
    private bool TryGetPosition(object ent, out Vector3 pos)
    {
        pos = default;
        if (_piModel == null || _piModelGoComp == null) return false;
        try
        {
            var model = _piModel.GetValue(ent);
            if (model == null) return false;
            var go = _piModelGoComp.GetValue(model);
            if (go == null) return false;
            if (!_goPosResolved)
            {
                _piGoPosition  = go.GetType().GetProperty("Position", BindingFlags.Public | BindingFlags.Instance);
                _goPosResolved = true;
            }
            if (_piGoPosition?.GetValue(go) is Vector3 p) { pos = p; return true; }
            return false;
        }
        catch { return false; }
    }
}
