using System;
using System.Collections.Generic;
using System.Reflection;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.TargetLens;

// Effect-picker table loader + search filter. Splits BuffTableBase rows into debuffs (BuffType==0) vs buffs
// (else) with Id/Name/Icon/Desc, loaded lazily on first tab visit. Ported from CooldownBar's
// Plugin.Settings.Tables.cs (Skills tab removed).
//
// Rows are GROUPED by resolved display Name (same-name variants collapse into one row, mirroring CooldownBar):
// _dtIds/_dtNames/_dtDescs hold one representative per group (first-seen member), _dtMembers holds every id
// sharing that Name. Selection stays per-id — a group toggle just writes all member ids — so the HUD display
// path (TargetEffectSelection.ShouldShow, per baseId) is unchanged.
public sealed partial class Plugin
{
    private const int SettingsPoolSize = 14;

    // ── Debuffs tab raw + filtered data ──────────────────────────────────────
    private int[]    _dtIds       = Array.Empty<int>();     // representative id per group (icon + tooltip)
    private string[] _dtNames     = Array.Empty<string>();
    private string[] _dtDescs     = Array.Empty<string>();
    private int[][]  _dtMembers   = Array.Empty<int[]>();   // all ids sharing each group's Name
    private int      _dtCount     = 0;
    private int[]    _dtFiltIds   = Array.Empty<int>();
    private string[] _dtFiltNames = Array.Empty<string>();
    private string[] _dtFiltDescs = Array.Empty<string>();
    private int[][]  _dtFiltMembers = Array.Empty<int[]>();
    private int      _dtFiltCount = 0;
    private string   _dtFilter    = "";
    private int      _dtOffset    = 0;
    private readonly UvRect[] _dtUv = new UvRect[SettingsPoolSize];

    // ── Buffs tab raw + filtered data ─────────────────────────────────────────
    // Grouped by display Name — see the Debuffs block above.
    private int[]    _btIds       = Array.Empty<int>();     // representative id per group
    private string[] _btNames     = Array.Empty<string>();
    private string[] _btDescs     = Array.Empty<string>();
    private int[][]  _btMembers   = Array.Empty<int[]>();   // all ids sharing each group's Name
    private int      _btCount     = 0;
    private int[]    _btFiltIds   = Array.Empty<int>();
    private string[] _btFiltNames = Array.Empty<string>();
    private string[] _btFiltDescs = Array.Empty<string>();
    private int[][]  _btFiltMembers = Array.Empty<int[]>();
    private int      _btFiltCount = 0;
    private string   _btFilter    = "";
    private int      _btOffset    = 0;
    private readonly UvRect[] _btUv = new UvRect[SettingsPoolSize];
    private bool     _buffTabLoaded = false;

    // ── Lazy loader (idempotent: called from VirtualListElement Count func) ──

    private void EnsureBuffTabLoaded()
    {
        if (_buffTabLoaded) return;
        LoadSelectBuffTable();
        _buffTabLoaded = _dtCount > 0 || _btCount > 0;
        if (_dtCount > 0) ApplyDebuffTabFilter(_dtFilter);
        if (_btCount > 0) ApplyBuffTabFilter(_btFilter);
    }

    // ── Table loader ────────────────────────────────────────────────────────

    private void LoadSelectBuffTable()
    {
        try
        {
            var t = StellarInterop.FindType("Bokura.BuffTableBase") ?? StellarInterop.FindType("BuffTableBase");
            if (t == null) { _services.Log.Warning("[Select] BuffTableBase not found"); return; }

            MethodInfo? miGet = null;
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name != "GetTable") continue;
                var ps = m.GetParameters();
                if (ps.Length == 1 && ps[0].ParameterType == typeof(bool)) { miGet = m; break; }
            }
            if (miGet == null) { _services.Log.Warning("[Select] BuffTableBase.GetTable not found"); return; }

            var tbl    = miGet.Invoke(null, new object[] { false });
            if (tbl == null) return;
            var values = tbl.GetType().GetProperty("Values", BindingFlags.Public | BindingFlags.Instance)
                             ?.GetValue(tbl) ?? tbl;

            // Names come from the plugin's embedded English-translation JSON (same source the HUD tiles use),
            // so load it once before the enumeration loop.
            TranslatedBuffText.EnsureLoaded(_services.Log.Info);

            // Group by resolved display Name (exact match, first-seen order); variants collapse, distinct names stay.
            var dGroups = new BuffGroupAccumulator();
            var bGroups = new BuffGroupAccumulator();
            PropertyInfo? piKvp = null, piId = null, piName = null, piType = null, piDesc = null, piIcon = null;

            foreach (var item in SettingsReflectEnumerate(values))
            {
                if (item == null) continue;
                object? row = item;
                if (piKvp == null && piId == null)
                {
                    var kv = item.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
                    if (kv != null) piKvp = kv;
                }
                if (piKvp != null) row = piKvp.GetValue(item);
                if (row == null) continue;
                if (piId == null)
                {
                    var rt = row.GetType();
                    piId   = rt.GetProperty("Id",       BindingFlags.Public | BindingFlags.Instance);
                    piName = rt.GetProperty("Name",     BindingFlags.Public | BindingFlags.Instance);
                    piType = rt.GetProperty("BuffType", BindingFlags.Public | BindingFlags.Instance);
                    piDesc = rt.GetProperty("Desc",     BindingFlags.Public | BindingFlags.Instance);
                    piIcon = rt.GetProperty("Icon",     BindingFlags.Public | BindingFlags.Instance);
                }
                int id = (int)(piId?.GetValue(row) ?? 0);
                if (id <= 0) continue;
                string gameName = (string?)(piName?.GetValue(row)) ?? "";
                // Show-hidden OFF (default): exclude the ~8000 internal/no-icon rows by filtering on the RAW game
                // Name/Icon — same gate as CooldownBar. ON: keep every id>0 row. The DISPLAY label below still runs
                // through the EffectOverrides → translation → gameName → "#id" resolver regardless.
                if (!_showHidden)
                {
                    string icon = (string?)(piIcon?.GetValue(row)) ?? "";
                    if (string.IsNullOrEmpty(gameName) || string.IsNullOrEmpty(icon)) continue;
                }
                string desc = (string?)(piDesc?.GetValue(row)) ?? "";
                // Display name priority (mirrors the HUD's ResolveEffectName, including the manual override dict):
                // EffectOverrides → English-translation JSON → game Name → "#<id>". This resolved label is BOTH the
                // row label and the grouping key, so same-name variants collapse into one row.
                string name;
                if (EffectOverrides.TryGetValue(id, out var ov) && !string.IsNullOrEmpty(ov.Name))     name = ov.Name;
                else if (TranslatedBuffText.TryGet(id, out var tn, out _) && !string.IsNullOrEmpty(tn)) name = tn;
                else if (!string.IsNullOrEmpty(gameName))                                              name = gameName;
                else                                                                                   name = $"#{id}";
                int buffType = (int)(piType?.GetValue(row) ?? -1);
                (buffType == 0 ? dGroups : bGroups).Add(name, id, desc);
            }
            dGroups.Export(out _dtIds, out _dtNames, out _dtDescs, out _dtMembers, out _dtCount);
            bGroups.Export(out _btIds, out _btNames, out _btDescs, out _btMembers, out _btCount);
            _services.Log.Info($"[Select] loaded {_dtCount} debuff groups, {_btCount} buff groups");
        }
        catch (Exception ex)
        {
            _services.Log.Warning($"[Select] LoadBuffTable: {ex.InnerException?.Message ?? ex.Message}");
        }
    }

    // ── Filters ───────────────────────────────────────────────────────────────

    private void ApplyDebuffTabFilter(string text)
    {
        _dtFilter = text; _dtOffset = 0;
        SettingsFilterGroups(text, _dtIds, _dtNames, _dtDescs, _dtMembers, _dtCount, _selection.IsDebuffTracked,
            out _dtFiltIds, out _dtFiltNames, out _dtFiltDescs, out _dtFiltMembers, out _dtFiltCount);
    }

    private void ApplyBuffTabFilter(string text)
    {
        _btFilter = text; _btOffset = 0;
        SettingsFilterGroups(text, _btIds, _btNames, _btDescs, _btMembers, _btCount, _selection.IsBuffTracked,
            out _btFiltIds, out _btFiltNames, out _btFiltDescs, out _btFiltMembers, out _btFiltCount);
    }

    // Grouped filter (Buffs / Debuffs): search + tracked-floats-to-top behaviour, but each row is a Name-group.
    // A group counts as "tracked" only when EVERY member id is tracked, and that predicate — not a single id —
    // drives the sort. The member arrays are carried through so the toggle/tooltip can reach every id in the group.
    private static void SettingsFilterGroups(string text, int[] srcIds, string[] srcNames, string[] srcDescs,
        int[][] srcMembers, int srcCount, Func<int, bool> isTracked,
        out int[] ids, out string[] names, out string[] descs, out int[][] members, out int count)
    {
        var q = text?.Trim() ?? "";
        var ri = new List<int>(); var rn = new List<string>(); var rd = new List<string>(); var rm = new List<int[]>();
        for (int i = 0; i < srcCount; i++)
        {
            if (q.Length > 0 && srcNames[i].IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
            ri.Add(srcIds[i]); rn.Add(srcNames[i]); rd.Add(srcDescs[i]); rm.Add(srcMembers[i]);
        }
        // Stable sort: fully-tracked groups float to the top.
        var tId = new List<int>(); var tN = new List<string>(); var tD = new List<string>(); var tM = new List<int[]>();
        var xId = new List<int>(); var xN = new List<string>(); var xD = new List<string>(); var xM = new List<int[]>();
        for (int i = 0; i < ri.Count; i++)
        {
            bool all = GroupAllTracked(rm[i], isTracked);
            if (all) { tId.Add(ri[i]); tN.Add(rn[i]); tD.Add(rd[i]); tM.Add(rm[i]); }
            else     { xId.Add(ri[i]); xN.Add(rn[i]); xD.Add(rd[i]); xM.Add(rm[i]); }
        }
        tId.AddRange(xId); tN.AddRange(xN); tD.AddRange(xD); tM.AddRange(xM);
        ids = tId.ToArray(); names = tN.ToArray(); descs = tD.ToArray(); members = tM.ToArray(); count = ids.Length;
    }

    // A group is "tracked" (toggle ON) only when every member id is tracked. A partially-tracked group — only
    // reachable from a pre-existing per-id config — reads OFF, so one tap then selects the whole group.
    private static bool GroupAllTracked(int[] members, Func<int, bool> isTracked)
    {
        if (members.Length == 0) return false;
        for (int i = 0; i < members.Length; i++) if (!isTracked(members[i])) return false;
        return true;
    }

    // ── IL2CPP duck-typed enumerator ─────────────────────────────────────────

    private static IEnumerable<object?> SettingsReflectEnumerate(object collection)
    {
        var t  = collection.GetType();
        var mi = t.GetMethod("GetEnumerator", BindingFlags.Public | BindingFlags.Instance);
        if (mi == null) yield break;
        var enumerator = mi.Invoke(collection, null);
        if (enumerator == null) yield break;
        var et     = enumerator.GetType();
        var miNext = et.GetMethod("MoveNext",  BindingFlags.Public | BindingFlags.Instance);
        var piCur  = et.GetProperty("Current", BindingFlags.Public | BindingFlags.Instance);
        if (miNext == null || piCur == null) yield break;
        while ((bool)(miNext.Invoke(enumerator, null) ?? false))
            yield return piCur.GetValue(enumerator);
    }

    // Collapses per-id buff/debuff rows into Name-groups in first-seen order. The first row of a Name becomes the
    // group's representative (id + desc, used for the icon and click-tooltip); later rows just add their id.
    private sealed class BuffGroupAccumulator
    {
        private readonly Dictionary<string, int> _index = new();   // Name → group slot
        private readonly List<int>       _ids   = new();           // representative id per group
        private readonly List<string>    _names = new();
        private readonly List<string>    _descs = new();           // representative desc per group
        private readonly List<List<int>> _members = new();         // all ids sharing the Name

        public void Add(string name, int id, string desc)
        {
            if (_index.TryGetValue(name, out int gi)) { _members[gi].Add(id); return; }
            _index[name] = _ids.Count;
            _ids.Add(id); _names.Add(name); _descs.Add(desc); _members.Add(new List<int> { id });
        }

        public void Export(out int[] ids, out string[] names, out string[] descs, out int[][] members, out int count)
        {
            ids = _ids.ToArray(); names = _names.ToArray(); descs = _descs.ToArray();
            var m = new int[_members.Count][];
            for (int i = 0; i < _members.Count; i++) m[i] = _members[i].ToArray();
            members = m; count = _ids.Count;
        }
    }
}
