using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Stellar.Abstractions.Services;

namespace Stellar.TargetLens;

/// <summary>
/// Harmony capture of the game's general cast/channel ("吟唱/读条") progress, feeding <see cref="TargetInfoTracker"/>'s
/// cast-bar read. This REPLACES both the old boss HP-frame widget scrape (<c>Panda.ZUi.ZUIBossBlood</c>, bosses only)
/// AND the earlier <c>ZStateSkillComp.beginSingGuide()</c> begin/end hook — that method is AOT-inlined in this build, so
/// its standalone body never runs and the patch captured nothing (<c>[CastDiag] activeCasters=0 patched=True</c>).
///
/// <para>PRIMARY PRODUCER: <c>Panda.ZGame.EntityExtensions.SetSingGuide(ZEntity entity, float value, float maxValue,
/// bool forward, ESingGuideType type)</c> — a static extension the game calls EVERY FRAME during a cast to drive the
/// bar (verified non-inlined + patchable: <c>dump.cs:206212</c>, <c>script.json:236201</c>, RVA 0x5180E20, all params
/// by value/pointer → no in/ref-struct → no trampoline-NullRef risk). ⚠️ It is a one-shot SETUP call, NOT a per-frame
/// producer: it fires once at cast start with <c>value == 0</c> and <c>maxValue == total cast seconds</c>, then the
/// game tweens the bar client-side WITHOUT re-firing. So the postfix latches (<c>StartTick</c>, <c>TotalSec</c>,
/// <c>Forward</c>, <c>Danger</c>) keyed by caster uuid = <c>entity.Uuid</c> (the first arg IS the ZEntity) and the read
/// COUNTS UP locally (<c>elapsed = now − StartTick</c>). Because there is no explicit end call, an entry is EXPIRED
/// once it has run its OWN duration (<c>elapsed ≥ TotalSec + <see cref="ExpireGraceSec"/></c>) — the bar runs to
/// completion then drops (interrupts aren't observable without an end signal — accepted for now). A duplicate identical
/// setup call does NOT reset <c>StartTick</c>, so the countdown stays smooth.</para>
///
/// <para>ENRICHMENT (best-effort): to show the real skill name + icon, the SetSingGuide postfix reads the caster's
/// CURRENT skill straight off the ZEntity — <c>ZEntity.GetComponent&lt;ZStateSkillComp&gt;()</c> then its
/// <c>curSkillId_</c> (int) and <c>skillRow_.Name</c> (string). This is guarded and NON-blocking: if the generic
/// interop invoke or a member read fails, <c>SkillId</c> stays 0 / <c>Name</c> "" and the bar shows a generic
/// "Casting…". A second postfix on <c>ZStateSkillComp.beginSingGuide()</c> is KEPT as a fallback enricher (it is
/// AOT-inlined in this build so it never actually fires); when it does fire it only fills the label fields, never the
/// count-up latch.</para>
///
/// <para>Threading: postfixes run on the game/main thread, and the tracker reads the latch on that same thread during
/// window render (one poll per frame) — mirroring <see cref="BuffTrackPatch"/>/<see cref="DbmPatch"/>, so a plain
/// dictionary swap is safe, no lock. A version counter lets the tracker tell a fresh event from a repeat read. The
/// postfixes MUST NEVER THROW (a throw in a game-thread postfix is dangerous) → everything is guarded.</para>
/// </summary>
internal static class CastPatch
{
    /// <summary>One caster's in-flight cast, latched on the <c>SetSingGuide</c> SETUP call and counted up LOCALLY.</summary>
    public struct CastEntry
    {
        public long   Uuid;
        public long   StartTick;  // Environment.TickCount64 at cast (re)start — drives the local count-up
        public float  TotalSec;   // total cast time in SECONDS (SetSingGuide's maxValue) — the count-up denominator
        public bool   Forward;    // true → count UP (elapsed/total); false → count DOWN (1 − elapsed/total)
        public bool   Danger;     // ESingGuideType.MonsterDanger (type==2) → red accent
        public int    SkillId;    // enrichment (off the ZEntity) — drives the overlay icon; 0 when unknown
        public string Name;       // enrichment (off the ZEntity) — skill name; "" when unknown ("Casting…" shown)
    }

    /// <summary>Grace past a cast's own <see cref="CastEntry.TotalSec"/> before the latch is pruned (SetSingGuide
    /// has no explicit end call, so we let the bar run to completion + this slack, then drop it).</summary>
    internal const float ExpireGraceSec = 0.3f;

    // Latch keyed by caster uuid. Written by the postfixes, read by the tracker — all on the main thread, so no lock
    // (matches BuffTrackPatch._activeBuffs / DbmPatch._ids).
    private static readonly Dictionary<long, CastEntry> _casts = new();
    private static int _version;   // bumped on every update so the tracker can gate a fresh event

    /// <summary>True once the SetSingGuide postfix is installed (surfaced in [CastDiag] so we can confirm it fired).</summary>
    public static bool Installed { get; private set; }

    /// <summary>Opt-in event logging (wired from the plugin's "Cast-bar diagnostic" toggle).</summary>
    public static bool Diag;

    /// <summary>Number of casters currently latched (for the diagnostic census).</summary>
    public static int ActiveCount => _casts.Count;

    /// <summary>
    /// Latch lookup for the current target. Returns the live cast for <paramref name="casterUuid"/>, pruning it once
    /// the cast has run its OWN duration (<c>elapsed ≥ TotalSec + <see cref="ExpireGraceSec"/></c>). We expire on the
    /// cast's declared length — NOT on setter-staleness — because <c>SetSingGuide</c> is a one-shot SETUP call
    /// (value=0, max=total seconds) that the game does not re-fire while it tweens the bar client-side; a staleness
    /// prune would drop the bar ~0.3s after cast START. A non-positive <see cref="CastEntry.TotalSec"/> (an
    /// enrichment-only seed before any real setup) is never duration-expired. We can't see interrupts without an end
    /// signal, so an interrupted cast simply runs the bar to completion — accepted for now.
    /// </summary>
    public static bool TryGet(long casterUuid, out CastEntry entry)
    {
        if (_casts.TryGetValue(casterUuid, out entry))
        {
            if (entry.TotalSec > 0f)
            {
                float elapsed = (Environment.TickCount64 - entry.StartTick) / 1000f;
                if (elapsed >= entry.TotalSec + ExpireGraceSec)
                {
                    _casts.Remove(casterUuid);
                    _version++;
                    entry = default;
                    return false;
                }
            }
            return true;
        }
        entry = default;
        return false;
    }

    private static Action<string>? _log;

    internal static bool Install(Harmony harmony, Action<string> log)
    {
        _log = log;

        // PRIMARY: EntityExtensions.SetSingGuide — the per-frame producer that carries the real bar values.
        var ext = StellarInterop.FindType("Panda.ZGame.EntityExtensions");
        if (ext == null) { log("[Cast] EntityExtensions not found — cast capture skipped"); return false; }

        var set = ResolveSetSingGuide(ext);
        if (set == null) { log("[Cast] SetSingGuide not found — cast capture skipped"); return false; }

        try
        {
            harmony.Patch(set, postfix: new HarmonyMethod(typeof(CastPatch), nameof(OnSetSingGuide)));
            Installed = true;
            log("[Cast] SetSingGuide postfix patched (primary cast producer)");
        }
        catch (Exception ex) { log($"[Cast] SetSingGuide patch failed: {ex.Message}"); return false; }

        // ENRICHMENT (optional): beginSingGuide for skill id/name. Inlined in this build → likely never fires; kept so
        // the label/icon light up automatically if a future build stops inlining it. A miss here is non-fatal.
        try
        {
            var sk = StellarInterop.FindType("Panda.ZGame.ZStateSkillComp");
            var begin = sk == null ? null : ResolveMethod0(sk, "beginSingGuide");
            if (begin != null)
            {
                harmony.Patch(begin, postfix: new HarmonyMethod(typeof(CastPatch), nameof(OnBeginSing)));
                log("[Cast] beginSingGuide enrichment postfix patched");
            }
            else log("[Cast] beginSingGuide not resolvable — enrichment skipped (bar shows 'Casting…')");
        }
        catch (Exception ex) { log($"[Cast] beginSingGuide enrichment skipped: {ex.Message}"); }

        return true;
    }

    // Harmony teardown is owned by IHarmonyHost (auto-unpatch on dispose); reset only transient state here.
    internal static void Uninstall()
    {
        _casts.Clear();
        _version = 0;
        _entUuidResolved = false; _piEntUuid = null;
        _getCompResolved = false; _miGetSkillComp = null;
        _reflResolved = false;
        _mCurSkillId = _mSkillRow = _mSkillName = null;
        _skillRowType = null;
        _piHost = _piUuid = null; _hostResolved = false;
        _loggedError = false;
        _firstSetLogged = _firstBeginLogged = false;
    }

    // ── PRIMARY postfix: SetSingGuide(entity, value, maxValue, forward, type) ────────────────────────────────────
    // Static extension → no __instance. Positional injection: __0 = ZEntity, __1 = value, __2 = maxValue,
    // __3 = forward, __4 = type (ESingGuideType, byte-backed). entity IS the ZEntity → read .Uuid directly.
    // SetSingGuide is the one-shot SETUP call (value=0, maxValue=total seconds); we ignore value and count UP locally
    // from StartTick. Fully guarded — a game-thread postfix must never throw.
    private static void OnSetSingGuide(object __0, float __1, float __2, bool __3, object __4)
    {
        try
        {
            if (__0 == null) return;

            long uuid = ReadEntityUuid(__0);

            if (!_firstSetLogged)
            {
                _firstSetLogged = true;
                _log?.Invoke($"[Cast] SetSingGuide FIRED uuid={uuid} val={__1:F2}/{__2:F2} fwd={__3} type={ToInt(__4)}");
            }
            if (uuid == 0) return;

            bool  danger = ToInt(__4) == 2;   // ESingGuideType.MonsterDanger
            float total  = __2;               // maxValue = total cast time in SECONDS (value __1 is always 0 at setup)
            long  now    = Environment.TickCount64;

            // Latch StartTick ONCE per cast. Keep it across a DUPLICATE identical setup call (same un-expired uuid,
            // same TotalSec) so the local count-up stays smooth; re-anchor only when this is a NEW cast (no live entry)
            // or the total changed. Preserve any enrichment (SkillId/Name) already on the entry.
            long   startTick = now;
            int    skillId   = 0;
            string name      = "";
            if (_casts.TryGetValue(uuid, out var prev))
            {
                skillId = prev.SkillId; name = prev.Name ?? "";
                bool sameCast = prev.TotalSec > 0f
                             && Math.Abs(prev.TotalSec - total) < 0.01f
                             && (now - prev.StartTick) / 1000f < prev.TotalSec + ExpireGraceSec;   // still un-expired
                if (sameCast) startTick = prev.StartTick;
            }

            _casts[uuid] = new CastEntry
            {
                Uuid      = uuid,
                StartTick = startTick,
                TotalSec  = total,
                Forward   = __3,
                Danger    = danger,
                SkillId   = skillId,
                Name      = name,
            };
            _version++;

            // Best-effort: read the caster's current skill id/name off the ZEntity so the overlay shows the real name
            // + icon. Never blocks the bar — a miss leaves SkillId=0/Name="" ("Casting…").
            if (skillId == 0) TryEnrichFromEntity(__0, uuid);

            if (Diag)
            {
                int diagSkill = _casts.TryGetValue(uuid, out var d) ? d.SkillId : skillId;   // post-enrichment id
                _log?.Invoke($"[CastDiag] set uuid={uuid} total={total:F2} fwd={__3} danger={danger} " +
                             $"skillId={diagSkill} active={_casts.Count}");
            }
        }
        catch (Exception ex) { LogError("set", ex); }
    }

    // ── Enrichment off the ZEntity: ZEntity.GetComponent<ZStateSkillComp>() → curSkillId_ / skillRow_.Name. ───────
    // Generic interop invoke (MakeGenericMethod) can throw if the AOT binary lacks that instantiation, so the WHOLE
    // path is guarded and non-blocking. On success we fill the latch's label fields in place, never the count-up.
    private static void TryEnrichFromEntity(object entity, long uuid)
    {
        try
        {
            var comp = GetSkillComp(entity);
            if (comp == null) return;
            EnsureRefl(comp.GetType());

            int    skillId = ReadInt(_mCurSkillId, comp);
            string name    = ReadSkillName(comp);
            if (skillId == 0 && string.IsNullOrEmpty(name)) return;

            if (_casts.TryGetValue(uuid, out var e))
            {
                e.SkillId = skillId; e.Name = name;
                _casts[uuid] = e;
                _version++;
            }
        }
        catch (Exception ex) { LogError("enrichEnt", ex); }
    }

    // ── ENRICHMENT postfix: beginSingGuide() — fill SkillId/Name only, never touch the count-up values. ──────────
    private static void OnBeginSing(object __instance)
    {
        try
        {
            if (__instance == null) return;
            EnsureRefl(__instance.GetType());

            long uuid = GetHostUuid(__instance);

            if (!_firstBeginLogged)
            {
                _firstBeginLogged = true;
                _log?.Invoke($"[Cast] beginSingGuide FIRED uuid={uuid} skillId={ReadInt(_mCurSkillId, __instance)}");
            }
            if (uuid == 0) return;

            int    skillId = ReadInt(_mCurSkillId, __instance);
            string name    = ReadSkillName(__instance);

            // Update-or-create: if SetSingGuide already latched this caster, enrich in place; otherwise seed a fresh
            // (non-stale) entry so the label survives until the first SetSingGuide tick populates the bar values.
            if (_casts.TryGetValue(uuid, out var e))
            {
                e.SkillId = skillId; e.Name = name;
                _casts[uuid] = e;
            }
            else
            {
                _casts[uuid] = new CastEntry
                {
                    Uuid = uuid, SkillId = skillId, Name = name,
                    Forward = true, TotalSec = 0f, Danger = false,
                    StartTick = Environment.TickCount64,
                };
            }
            _version++;

            if (Diag)
                _log?.Invoke($"[CastDiag] begin(enrich) uuid={uuid} skillId={skillId} name='{name}' active={_casts.Count}");
        }
        catch (Exception ex) { LogError("begin", ex); }
    }

    // ── Caster uuid off the ZEntity arg (SetSingGuide's first arg IS the entity). ────────────────────────────────
    private static PropertyInfo? _piEntUuid;
    private static bool          _entUuidResolved;

    private static long ReadEntityUuid(object entity)
    {
        if (!_entUuidResolved)
        {
            _entUuidResolved = true;
            _piEntUuid = entity.GetType().GetProperty("Uuid", BindingFlags.Public | BindingFlags.Instance);
            _log?.Invoke($"[Cast] entUuid resolved={_piEntUuid != null}");
        }
        if (_piEntUuid == null) return 0L;
        try { return Convert.ToInt64(_piEntUuid.GetValue(entity) ?? 0L); } catch { return 0L; }
    }

    // ── ZEntity.GetComponent<ZStateSkillComp>() — resolve the closed generic once, then invoke per cast. ─────────
    private static MethodInfo? _miGetSkillComp;
    private static bool        _getCompResolved;

    private static object? GetSkillComp(object entity)
    {
        if (!_getCompResolved)
        {
            _getCompResolved = true;
            try
            {
                var skComp = StellarInterop.FindType("Panda.ZGame.ZStateSkillComp");
                // GetComponent<T>() is declared on the ZEntity base — walk up from the concrete runtime type to find
                // the generic definition (FlattenHierarchy doesn't surface generic-method defs reliably).
                MethodInfo? gen = null;
                var cur = entity.GetType();
                while (cur != null && gen == null)
                {
                    foreach (var mi in cur.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                        if (mi.Name == "GetComponent" && mi.IsGenericMethodDefinition && mi.GetParameters().Length == 0)
                        { gen = mi; break; }
                    cur = cur.BaseType;
                }
                if (skComp != null && gen != null) _miGetSkillComp = gen.MakeGenericMethod(skComp);
                _log?.Invoke($"[Cast] GetComponent<ZStateSkillComp> resolved={_miGetSkillComp != null}");
            }
            catch (Exception ex) { _log?.Invoke($"[Cast] GetComponent resolve failed: {ex.Message}"); }
        }
        if (_miGetSkillComp == null) return null;
        try { return _miGetSkillComp.Invoke(entity, null); } catch { return null; }
    }

    // ── Enrichment reflection (resolved once off the first ZStateSkillComp instance type) ────────────────────────
    private static bool        _reflResolved;
    private static MemberInfo? _mCurSkillId;   // curSkillId_ (int)
    private static MemberInfo? _mSkillRow;     // skillRow_ (SkillTableBase)
    private static Type?       _skillRowType;  // last-seen runtime type of skillRow_ (lazy re-resolve on change)
    private static MemberInfo? _mSkillName;    // SkillTableBase.Name (string)

    private static void EnsureRefl(Type t)
    {
        if (_reflResolved) return;
        _reflResolved = true;
        _mCurSkillId = FindMember(t, "curSkillId_");
        _mSkillRow   = FindMember(t, "skillRow_");
        _log?.Invoke($"[Cast] refl skillId={_mCurSkillId != null} row={_mSkillRow != null}");
    }

    private static string ReadSkillName(object inst)
    {
        var row = ReadMember(_mSkillRow, inst);
        if (row == null) return "";
        var rt = row.GetType();
        if (rt != _skillRowType) { _skillRowType = rt; _mSkillName = FindMember(rt, "Name"); }
        return ReadMember(_mSkillName, row) as string ?? "";
    }

    // ── Caster uuid for the enrichment hook: base-walk ZStateSkillComp → ZComponent.Host (ZEntity) → ZEntity.Uuid.
    //    Mirrors BuffTrackPatch.ResolveHostRefl/GetHostEntityUuid. ─────────────────────────────────────────────
    private static PropertyInfo? _piHost;
    private static PropertyInfo? _piUuid;
    private static bool          _hostResolved;

    private static long GetHostUuid(object comp)
    {
        if (!_hostResolved)
        {
            _hostResolved = true;
            var cur = comp.GetType();
            while (cur != null && _piHost == null)
            {
                _piHost = cur.GetProperty("Host", BindingFlags.NonPublic | BindingFlags.Instance)
                       ?? cur.GetProperty("Host", BindingFlags.Public | BindingFlags.Instance);
                cur = cur.BaseType;
            }
            if (_piHost != null)
                _piUuid = _piHost.PropertyType.GetProperty("Uuid", BindingFlags.Public | BindingFlags.Instance);
            _log?.Invoke($"[Cast] hostRefl host={_piHost != null} uuid={_piUuid != null}");
        }
        if (_piHost == null || _piUuid == null) return 0L;
        try
        {
            var host = _piHost.GetValue(comp);
            return host == null ? 0L : Convert.ToInt64(_piUuid.GetValue(host) ?? 0L);
        }
        catch { return 0L; }
    }

    // ── Small helpers ────────────────────────────────────────────────────────────────────────────────────────

    // Resolve the single public static SetSingGuide(ZEntity, float, float, bool, ESingGuideType) — 5 params, static.
    private static MethodInfo? ResolveSetSingGuide(Type t)
    {
        try
        {
            foreach (var mi in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                if (mi.Name == "SetSingGuide" && mi.GetParameters().Length == 5) return mi;
        }
        catch { }
        return null;
    }

    // Locate a 0-arg method by name (prefers StellarInterop.FindMethod, then a direct NonPublic scan).
    private static MethodInfo? ResolveMethod0(Type t, string name)
    {
        var m = StellarInterop.FindMethod(t, name, 0);
        if (m != null) return m;
        try
        {
            foreach (var mi in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                if (mi.Name == name && mi.GetParameters().Length == 0) return mi;
        }
        catch { }
        return null;
    }

    // Property-then-field lookup across the full hierarchy (IL2CPP members surface as either, by generator).
    private static MemberInfo? FindMember(Type t, string name)
    {
        const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
        try { var p = t.GetProperty(name, F); if (p != null && p.CanRead) return p; } catch { }
        try { var f = t.GetField(name, F); if (f != null) return f; } catch { }
        return null;
    }

    private static object? ReadMember(MemberInfo? m, object target)
    {
        try
        {
            return m switch
            {
                PropertyInfo p => p.GetValue(target),
                FieldInfo    f => f.GetValue(target),
                _              => null,
            };
        }
        catch { return null; }
    }

    private static int ReadInt(MemberInfo? m, object t)
    {
        var v = ReadMember(m, t);
        try { return v == null ? 0 : Convert.ToInt32(v); } catch { return 0; }
    }

    // Boxed enum / numeric → int (ESingGuideType is byte-backed; Convert handles the boxed enum).
    private static int ToInt(object? v)
    {
        try { return v == null ? 0 : Convert.ToInt32(v); } catch { return 0; }
    }

    private static bool _loggedError;
    private static bool _firstSetLogged;
    private static bool _firstBeginLogged;

    private static void LogError(string src, Exception ex)
    {
        if (_loggedError) return;
        _loggedError = true;
        _log?.Invoke($"[Cast] {src}: {ex.InnerException?.Message ?? ex.Message}");
    }
}
