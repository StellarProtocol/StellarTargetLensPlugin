using System;
using System.Collections.Generic;
using System.Reflection;
using Stellar.Abstractions.Domain;
using UnityEngine;

namespace Stellar.TargetLens;

/// <summary>
/// Per-player THREAT / AGGRO read off the current monster target. The threat table is the repeated entity
/// attribute <c>EAttrType.AttrHateList = 474</c> — a LIST of <c>Zproto.HateInfo</c> (<c>long Uuid; uint HateVal</c>).
/// A row's share of aggro is <c>HateVal / Σ HateVal</c>.
///
/// <para>This is the SAME acquisition problem the shield read solved (<c>TargetInfoTracker.Shield.cs</c>):
/// <c>ZEntity.GetLuaAttr(int)</c> hands back a bare <c>Zproto.IAttr</c> interface wrapper, so the concrete
/// <c>RepeatedField&lt;HateInfo&gt;</c> is reached via three techniques tried in order and cached (see
/// <c>TargetInfoTracker.ThreatRead.cs</c>). Unlike <c>ShieldInfo</c>, <c>RepeatedField&lt;HateInfo&gt;</c> IS
/// instantiated on this build, so Technique&nbsp;1 (typed <c>GetAttr</c>) is expected to win — but all three are
/// still implemented, and which one actually wins can only be confirmed in-game.</para>
///
/// <para>Every step is guarded — a miss/failed read yields an empty list and never throws from the poll/render
/// path. Result is frame-cached (one read per rendered frame), like the shield read.</para>
/// </summary>
internal sealed partial class TargetInfoTracker
{
    private const int AttrHateListId = 474; // EAttrType.AttrHateList — repeated HateInfo

    /// <summary>One player's slice of the target's threat table.</summary>
    public readonly struct ThreatEntry
    {
        public readonly long   Uuid;
        public readonly string Name;
        public readonly long   HateVal; // raw aggro magnitude (uint on the wire, widened to long for the sum)
        public readonly float  Pct;     // HateVal / Σ HateVal, 0..1
        public readonly bool   IsLocal; // Uuid == local player entity id
        public readonly string NameSrc; // diag only — which resolution step produced Name (attrName/party/…)
        public ThreatEntry(long uuid, string name, long hateVal, float pct, bool isLocal, string nameSrc = "")
        {
            Uuid = uuid; Name = name; HateVal = hateVal; Pct = pct; IsLocal = isLocal; NameSrc = nameSrc;
        }
    }

    // ── HateInfo element member read (struct: long Uuid; uint HateVal; Il2CppInterop may surface either as a
    //    property or a field, so resolve BOTH and read whichever exists). Own cache — separate from the shield
    //    element cache so the two reads never thrash each other's handles. ──
    private Type?       _hateInfoType;
    private MemberInfo? _hateUuidMember;
    private MemberInfo? _hateValMember;
    private bool        _hateElemProbed;   // one-shot [ThreatProbe] on the first element

    // ── Concrete list walk (Count + indexer), own cache (re-resolve on type change) ──
    private PropertyInfo? _piHateCount;
    private MethodInfo?   _miHateGetItem;
    private Type?         _hateListType;

    // ── Frame-gated result cache ────────────────────────────────────────────────
    private int                _threatFrame = -1;
    private readonly List<ThreatEntry> _threatList = new();

    // ── [ThreatDiag] opt-in flag + change-gate (mirrors BreakDiag) ──────────────
    public  bool   ThreatDiag;
    private long   _threatDiagUuid = long.MinValue;
    private string _threatDiagSig  = "";

    /// <summary>
    /// The target's (<see cref="LastTargetEntity"/>) threat table, sorted DESC by <see cref="ThreatEntry.HateVal"/>
    /// with per-row <see cref="ThreatEntry.Pct"/> filled in and the local player's row flagged. Frame-cached:
    /// computes once per frame, returns the cache on repeat calls. <c>entries</c> is the shared backing list — do
    /// not mutate it. Returns <c>true</c> when the list is non-empty.
    /// </summary>
    internal bool TryGetThreatList(out IReadOnlyList<ThreatEntry> entries)
    {
        int f = Time.frameCount;
        if (f == _threatFrame) { entries = _threatList; return _threatList.Count > 0; }
        _threatFrame = f;
        _threatList.Clear();

        int count = 0, tech = 0;
        _h1Out = _h2Out = _h3Out = "-";
        try
        {
            var ent = LastTargetEntity;
            if (ent != null)
            {
                object? list = AcquireHateList(ent, out tech);
                if (list != null && ResolveHateListHandles(list))
                {
                    count = _piHateCount!.GetValue(list) is int n ? n : 0;

                    long localUuid = 0;
                    try { localUuid = _services.CombatSnapshot.LocalEntityId.Value; } catch { localUuid = 0; }

                    // First pass — gather (uuid, hate) and the total, so each row's Pct can be computed.
                    var raw = new List<(long uuid, long hate)>(count);
                    long sum = 0;
                    for (int i = 0; i < count; i++)
                    {
                        var hi = _miHateGetItem!.Invoke(list, new object[] { i });
                        if (hi == null) continue;
                        if (i == 0) ProbeHateElement(hi);
                        if (!ResolveHateFields(hi)) continue;
                        long u = ReadLongMember(_hateUuidMember, hi);
                        long h = ReadLongMember(_hateValMember, hi);
                        raw.Add((u, h));
                        sum += h;
                    }

                    foreach (var (u, h) in raw)
                    {
                        float pct    = sum > 0 ? (float)h / sum : 0f;
                        bool  local  = localUuid != 0 && u == localUuid;
                        string name  = ResolveThreatName(u, localUuid, out string src);
                        _threatList.Add(new ThreatEntry(u, name, h, pct, local, src));
                    }

                    // Highest aggro first — the display Top-N and the local-append both rely on this order.
                    _threatList.Sort((a, b) => b.HateVal.CompareTo(a.HateVal));
                }
            }
        }
        catch { _threatList.Clear(); }

        LogThreatDiag(tech, count);
        entries = _threatList; return _threatList.Count > 0;
    }

    // Player-name resolution ladder (Change 1). First non-empty wins; `src` records which step produced the name
    // for the [ThreatDiag] readout. HateInfo.Uuid is the FULL 64-bit uuid for entity/attr lookups; uuid>>16 is the
    // CharId/roleId (PartyRoster key + last-resort label). Never throws — worst case returns "Player <roleId>".
    private string ResolveThreatName(long uuid, long localUuid, out string src)
    {
        // 1. Primary — AttrName off the live entity (exactly what the game's damage-list UI reads).
        var ent = GetEntityObj(uuid);
        if (ent != null)
        {
            string an = ReadAttrString(ent, _attrNameBox);          // technique (a): GetAttr<object>(1).ToString()
            if (string.IsNullOrEmpty(an)) an = ReadAttrNameNative(ent); // technique (b): native get_Value on GetLuaAttr(1)
            if (!string.IsNullOrEmpty(an)) { src = "attrName"; return an; }
        }

        // 2. Fallback A — PartyRoster (party/raid members even when outside AOI), keyed by CharId = uuid>>16.
        string pr = ResolvePartyName(uuid >> 16);
        if (!string.IsNullOrEmpty(pr)) { src = "party"; return pr; }

        // 3. Fallback B — CombatLookup (AOI-scoped; may be empty for hate-list players not in it).
        string cl = ResolveName(uuid);
        if (!string.IsNullOrEmpty(cl)) { src = "combatLookup"; return cl; }

        // 4. Fallback C — self (the local player's own row).
        if (localUuid != 0 && (uuid >> 16) == (localUuid >> 16))
        {
            string self = ResolveSelfName();
            if (!string.IsNullOrEmpty(self)) { src = "self"; return self; }
        }

        // 5. Last resort — the roleId, so a row always carries a label.
        src = "roleId";
        return "Player " + (uuid >> 16);
    }

    // PartyRoster.Members match by CharId (uuid>>16) → member display name. Guarded: the service may be absent on
    // an older framework, or a slot may be sparsely synced with an empty name.
    private string ResolvePartyName(long charId)
    {
        try
        {
            var roster = _services.PartyRoster;
            var members = roster?.Members;
            if (members == null) return "";
            foreach (var m in members)
                if (m.CharId == charId && !string.IsNullOrEmpty(m.Name)) return m.Name;
        }
        catch { /* service missing / not populated — fall through */ }
        return "";
    }

    // Local player's own display name via PlayerState (blank in social areas). Guarded.
    private string ResolveSelfName()
    {
        try
        {
            var ps = _services.PlayerState;
            if (ps != null && !string.IsNullOrEmpty(ps.Name)) return ps.Name;
        }
        catch { /* service missing — fall through */ }
        return "";
    }

    // HateInfo.Uuid / .HateVal off the runtime element type, cached. Struct members surface as a property or a
    // field depending on the Il2CppInterop generator, so resolve whichever exists (property preferred).
    private bool ResolveHateFields(object hi)
    {
        var t = hi.GetType();
        if (t != _hateInfoType)
        {
            _hateInfoType   = t;
            _hateUuidMember = FindMember(t, "Uuid");
            _hateValMember  = FindMember(t, "HateVal");
        }
        return _hateUuidMember != null && _hateValMember != null;
    }

    // Count (property) + indexer (get_Item) off the runtime list type, cached (re-resolve on type change).
    private bool ResolveHateListHandles(object list)
    {
        var t = list.GetType();
        if (t != _hateListType)
        {
            _hateListType  = t;
            _piHateCount   = t.GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
            _miHateGetItem = null;
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                if (m.Name == "get_Item" && m.GetParameters() is { Length: 1 } ps && ps[0].ParameterType == typeof(int))
                { _miHateGetItem = m; break; }
        }
        return _piHateCount != null && _miHateGetItem != null;
    }

    // ── Diagnostics ─────────────────────────────────────────────────────────────

    // One-time probe on the first hate element: confirms the element type and whether Uuid/HateVal resolved as a
    // property or a field (the key struct-read unknown). Ungated + one-shot, so it's cheap and always in the log.
    private void ProbeHateElement(object hi)
    {
        if (_hateElemProbed) return;
        _hateElemProbed = true;
        try
        {
            var t = hi.GetType();
            _services.Log.Info($"[ThreatProbe] elem={t.FullName} isValueType={t.IsValueType} " +
                               $"uuidVia={MemberKind(FindMember(t, "Uuid"))} hateVia={MemberKind(FindMember(t, "HateVal"))}");
        }
        catch { /* diagnostic must never throw */ }
    }

    // Change-gated [ThreatDiag] line (opt-in via the ThreatDiag flag). Logs which technique won plus each
    // technique's outcome, the list count, and every entry (uuid name hateVal pct%) — so a FAILED read stays
    // unambiguously diagnosable without spamming the log every frame.
    private void LogThreatDiag(int tech, int count)
    {
        if (!ThreatDiag) return;
        try
        {
            var sb = new System.Text.StringBuilder();
            foreach (var e in _threatList)
                sb.Append(" [").Append(e.Uuid).Append(' ').Append(e.Name).Append("<-").Append(e.NameSrc)
                  .Append(' ').Append(e.HateVal).Append(' ').Append((int)(e.Pct * 100f)).Append("%]");
            string entries = sb.ToString();

            // Change-gate on target uuid + the full technique/count/entries signature so a moving value logs but a
            // steady one doesn't spam.
            string sig = $"{tech}|{_h1Out}|{_h2Out}|{_h3Out}|{count}|{entries}";
            if (LastTargetUuid == _threatDiagUuid && sig == _threatDiagSig) return;
            _threatDiagUuid = LastTargetUuid;
            _threatDiagSig  = sig;
            _services.Log.Info(
                $"[ThreatDiag] uuid={LastTargetUuid} tech={tech} T1={_h1Out} T2={_h2Out} T3={_h3Out} " +
                $"listCount={count} entries={entries}");
        }
        catch { /* diagnostic must never throw */ }
    }
}
