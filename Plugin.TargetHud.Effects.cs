using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.TargetLens;

// Target HUD effect tiles (Classic effects mode) + their per-slot getters, split out of Plugin.TargetHud.cs (500-LoC gate).
public sealed partial class Plugin
{
    private const float TileStride = 48f;              // ≈ CooldownBar CdTileIcon (44) + 4px gap

    // Single row of CooldownTileElements (same element the CooldownBar plugin uses) plus a trailing "+N"
    // overflow label. Fixed 12-slot pool with a captured idx per tile; only the tiles that both have a live
    // effect AND fit at the current window width render (EffTilesPerLine), the rest collapse via
    // ConditionalElement. Tiles render at the WindowBuilder's default CooldownTileElement size.
    private HudElement BuildTargetHudEffectTiles()
    {
        var cells = new HudElement[HudBuffSlots + 1];
        for (int c = 0; c < HudBuffSlots; c++)
        {
            int idx = c;
            var tile = new CooldownTileElement(
                Icon:        () => GetHudEffectIcon(idx),
                Uv:          () => _hudEffUv[idx],
                Fill01:      () => HudEffectFraction(idx),
                Seconds:     () => HudEffectSeconds(idx),
                Accent:      () => HudEffectAccent(idx),
                IsImagine:   () => HudEffectIsMine(idx),
                ChargeCount: () => HudEffectCharge(idx))
            { OnClick = () => OnTargetEffectClick(idx), FallbackLabel = () => HudEffectLabel(idx) };
            cells[c] = new ConditionalElement(() => HudEffectTileVisible(idx), tile);
        }
        // Trailing "+N" overflow label — shown when more effects exist than tiles fit on the line.
        cells[HudBuffSlots] = new ConditionalElement(
            () => CurEffects().Count > EffTilesPerLine(),
            new TextElement(() => $"+{CurEffects().Count - EffTilesPerLine()}", Color: MutedColor, Emphasis: true));
        return new RowElement(cells, Gap: 4f);
    }

    // Tiles that fit on one line at the current window width (reserves ~34px for the "+N" label; min 1, max pool).
    private int EffTilesPerLine()
    {
        float w = _targetHudWindow != null ? _targetHudWindow.Rect.Width : 0f;
        if (w < 1f) w = 1000f;                                  // pre-mount fallback = default width
        int fit = (int)((w - 2f * HudPad - 34f) / TileStride);  // 2·HudPad = chrome padding, 34 = +N reserve
        return System.Math.Clamp(fit, 1, HudBuffSlots);
    }

    // ── Combined effect slots (Ensure() first so the poll runs; frame-gated) ────

    // A tile slot renders only when it has a live effect AND fits on the single line at the current width.
    private bool HudEffectTileVisible(int idx)
    {
        return idx < CurEffects().Count && idx < EffTilesPerLine();
    }

    // Tile-path icon: resolve into this slot's UV cell. Shared priority logic lives in ResolveEffectIcon
    // (Plugin.BuffList.cs) so the List window's rows resolve icons identically.
    private object? GetHudEffectIcon(int idx)
    {
        var list = CurEffects();
        if (idx >= list.Count) { _hudEffUv[idx] = default; return null; }
        return ResolveEffectIcon(list[idx], out _hudEffUv[idx]);
    }

    // ★-badge predicate: true when the effect was cast by the LOCAL PLAYER (self-cast). The star now marks
    // "yours" — effects whose caster (FireUuid) matches the local entity id.
    private bool HudEffectIsMine(int idx)
    {
        var list = CurEffects();
        if (idx >= list.Count) return false;
        long local = _services.CombatSnapshot.LocalEntityId.Value;
        return local != 0 && list[idx].FireUuid == local;
    }

    // 2-letter tile: the framework draws this (FallbackLabel) only when an effect has no icon. This plugin opts
    // into the feature simply by wiring it here, so it's always on. Letters come from the same resolved display
    // name the tooltip shows.
    private string HudEffectLabel(int idx)
    {
        var list = CurEffects();
        if (idx >= list.Count) return "";
        return Abbrev(ResolveEffectName(list[idx]));
    }

    // Display-name priority: manual override → English-translated table → tracker's live (Chinese) name.
    private string ResolveEffectName(TargetBuffRow r)
    {
        if (EffectOverrides.TryGetValue(r.BaseId, out var ov) && !string.IsNullOrEmpty(ov.Name)) return ov.Name;
        TranslatedBuffText.EnsureLoaded(_services.Log.Info);
        if (TranslatedBuffText.TryGet(r.BaseId, out var tn, out _) && !string.IsNullOrEmpty(tn)) return tn;
        return r.Name ?? "";
    }

    // First two letter/digit characters of the (tag-stripped) name, uppercased.
    private static string Abbrev(string name)
    {
        name = StripTags(name);
        char a = '\0', b = '\0';
        foreach (var c in name)
        {
            if (!char.IsLetterOrDigit(c)) continue;
            if (a == '\0') a = char.ToUpperInvariant(c);
            else { b = char.ToUpperInvariant(c); break; }
        }
        if (a == '\0') return "";
        return b == '\0' ? a.ToString() : new string(new[] { a, b });
    }

    private float HudEffectFraction(int idx)
    {
        var list = CurEffects();
        if (idx >= list.Count) return 0f;
        var r = list[idx];
        return r.Duration > 0 ? Clamp01(r.RemainSec / (r.Duration / 1000f)) : 1f;   // permanent = full
    }

    // Tile caption: remaining whole seconds; blank for permanent effects (RemainSec < 0).
    private string HudEffectSeconds(int idx)
    {
        var list = CurEffects();
        if (idx >= list.Count) return "";
        var r = list[idx];
        return r.RemainSec >= 0f ? $"{r.RemainSec:F0}s" : "";
    }

    // Accent (tile outline + fill + caption tint): debuff → red/orange, buff → green.
    private ColorRgba HudEffectAccent(int idx)
    {
        var list = CurEffects();
        if (idx >= list.Count) return EffectBarColor;   // neutral for out-of-range slots
        return list[idx].BuffType == 0 ? DebuffAccent : BuffAccent;
    }

    // Charge/stack badge: shows ×N when Layer > 1.
    private int HudEffectCharge(int idx)
    {
        var list = CurEffects();
        if (idx >= list.Count) return 0;
        return list[idx].Layer;
    }
}
