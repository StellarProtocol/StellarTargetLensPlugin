using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.TargetLens;

// Top-right combat-target HUD overlay. Auto-shows in-world whenever the game has a locked target,
// mirroring the draggable "Target / Monster Info" menu window's data (portrait / name / HP / distance /
// effects) in a compact panel. Reuses _targetInfo + _targetBuff — no new data layer. Registered from the
// ctor right after the trackers are built; toggled on/off from BuildTargetRoot().
//
// Redesign: a semi-transparent dark PanelElement background (readability over the world), a header row of
// monster portrait + name + Lv/Rank + HP bar, a muted distance line, then ONE combined effects list (buffs
// and debuffs merged) with bigger icons. Each effect shows its parent SKILL icon when it has one, else the
// buff icon; a per-row cooldown bar plus a ▲/▼ label tag keeps buffs vs debuffs distinguishable.
//
// v2.0.0 note: WindowSpec has NO HideUntilInWorld / AutoHideBehindGameMenus flags (they existed in v1.5.1).
// Both are omitted; the ShouldRender predicate below already gates to Phase==World and not-Loading.
public sealed partial class Plugin
{
    private IWindowControl _targetHudWindow = null!;   // registered in the ctor; auto-shows on target
    private bool           _targetHudOn;               // config-backed master enable (default on)

    private MonsterIconLoader _monIcon = null!;        // head-portrait loader (guarded; null icon on any failure)
    private UvRect            _portraitUv;             // atlas sub-rect for the current portrait

    private const int HudBuffSlots = 12;               // combined buff+debuff row pool
    private readonly UvRect[] _hudEffUv = new UvRect[HudBuffSlots];

    // BarElement's colour arg is a ColorRgba VALUE (no Func overload in v2.0.0), so a single neutral bar colour
    // is used for the merged list — buff/debuff is distinguished by the ▲/▼ label tag instead.
    private static readonly ColorRgba EnemyHpColor   = new(0.85f, 0.25f, 0.25f, 1f);
    private static readonly ColorRgba EffectBarColor = new(0.45f, 0.55f, 0.70f, 1f); // neutral out-of-range tile accent
    private static readonly ColorRgba BreakBarColor  = new(0.35f, 0.65f, 0.95f, 1f); // blue, distinct from the red HP bar
    private static readonly ColorRgba BreakRecoverColor = new(0.95f, 0.65f, 0.20f, 1f); // amber while broken/refilling

    // Local break-gauge fill tween — the raw attr snaps back to full after the recovery window; this reproduces
    // the game HUD's smooth 0→full refill over the monster's BreakingContinueTime.
    private readonly BreakGauge _breakGauge = new();

    // CooldownTile accents: debuff = red/orange, buff = green.
    private static readonly ColorRgba DebuffAccent = new(0.90f, 0.40f, 0.25f, 1f);
    private static readonly ColorRgba BuffAccent   = new(0.35f, 0.75f, 0.45f, 1f);

    // Manual per-effect overrides for buffs the game data resolves poorly (unnamed / no icon).
    // baseId → (display name for the tooltip, imagine skill id whose icon to draw). IconSkill 3948 =
    // "Arcane! Divine Reliance" imagine icon.
    private static readonly Dictionary<int, (string Name, int IconSkill)> EffectOverrides = new()
    {
        { 2110135, ("Rolora - Active Timer", 3948) },
        { 2110111, ("Rolora - Spell",        3948) },
    };

    private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;

    private void RegisterTargetHud()
    {
        _monIcon = new MonsterIconLoader(_services);

        const float HudW = 420f;         // MIN width (drag floor); the out-of-box default width is wider (below)
        const float HudDefaultW = 732f;  // default width tuned in-game (user widened from 420) — still ≥ HudW min
        // Resizable disables the content-auto-fit and fixes the height, so set an explicit height and lock it
        // below (MinHeight==MaxHeight) — width stays resizable, height cannot be dragged. The threat/aggro list is
        // its OWN window now (see Plugin.ThreatWindow.cs), so this HUD keeps its original fixed height.
        //
        // The height is engine-locked for a Resizable window (MinHeight==MaxHeight can't change at runtime — see
        // WindowBuilder-Patterns.md), so it's chosen ONCE here from the PERSISTED buff style. Read straight from
        // _cfg (not _buffStyle) so it doesn't depend on which partial's ctor sets that field first.
        //   Classic (0): full 240 — header + HP + break bars + the effect-tile grid.
        //   List (1) / Off (2): compact — no tile grid in the HUD (List renders effects in the standalone Target
        //                Effects window; Off shows nothing), so drop that band. Only Classic keeps the tall height.
        //                The break/stagger gauge MUST still fit (bosses show it in List/Off mode too).
        // Compact derivation from BuildTargetHudRoot (ColumnElement Gap 4, Padding 8): 8 pad-top + 52 header
        // (52px portrait) + 4 + 28 HP bar + 4 + 18 break bar + 8 pad-bottom = 122 core; +18 margin to absorb
        // any gaps the collapsed conditional slots still reserve and font metrics → 140.
        // A live style toggle re-fits this height only on the next reload/relog (accepted; no live height resize).
        int style = _cfg.Get<int>("buff_style", 1);
        float HudH = style == 0 ? 240f : 140f;
        // Default position tuned in-game (user's saved 2560x1440 layout: x=984, y=0, width widened to 732).
        // X is expressed as a fraction of ScreenWidth (0.3844*2560≈984) so it holds across resolutions; y absolute.
        // A user's own saved drag still overrides this.
        float x = _services.Framework.ScreenWidth * 0.3844f;
        float y = 0f;

        _targetHudWindow = _services.Windows.Register(new WindowRegistration(
            Spec: new WindowSpec(
                Id:          "targetlens.hud",
                Title:       _loc.T("tl.window.hud"),
                DefaultRect: new WindowRect(x, y, HudDefaultW, HudH),
                Category:    WindowCategory.HUD,
                Style:       WindowPanelStyle.Borderless)
            {
                Draggable = true, EditModeDragOnly = true, Closable = false, StartVisible = false,
                // Width-resizable (min = default width); height LOCKED via MinHeight==MaxHeight so it can't be dragged.
                Resizable = true, MinWidth = HudW, MinHeight = HudH, MaxHeight = HudH,
                // Auto-show: only in-world, only when a target exists, and hidden while any menu/blocking
                // overlay or loading screen is up (replaces the v1.5.1 AutoHideBehindGameMenus flag). Layout-edit
                // mode is an override so the overlay (with example data) can be positioned without a live target.
                ShouldRender = () => _services.Windows.IsLayoutEditing
                                  || (_services.ClientState.Phase == GamePhase.World
                                      && (_services.ClientState.UiState & (GameUIState.Loading | GameUIState.Blocking | GameUIState.AnyMenu)) == 0
                                      && _targetInfo.Current.HasTarget),
            },
            Root:    BuildTargetHudRoot(),
            OnClose: () => { }));
        _windows.Add(_targetHudWindow);   // Dispose already loops _windows and Remove()s each

        // The toggle is the master enable — it controls whether the HUD may show at all.
        _targetHudOn = _cfg.Get<bool>("target_hud_on", true);
        _targetHudWindow.SetVisible(_targetHudOn);
    }

    // The window paints a fixed 80%-black backdrop (WindowSpec.BackgroundOpacity); the column just pads content.
    private HudElement BuildTargetHudRoot() => new ColumnElement(new HudElement[]
        {
            // Header: bigger portrait + large-font name on top, full-width HP bar below.
            new RowElement(new HudElement[]
            {
                new CellElement(
                    new GameTextureElement(() => GetMonsterPortrait(), 52, 52, () => _portraitUv) { CornerRadius = 26 },
                    Width: 56f),
                new CellElement(
                    new RowElement(new HudElement[]
                    {
                        // Rank tag — bold + coloured (Boss = gold, Elite = orange), hidden for Normal.
                        new ConditionalElement(() => TargetHudHasRank(),
                            new TextElement(() => TargetHudRankText(), Color: () => TargetHudRankColor(), Emphasis: true, FontSize: 22)),
                        new TextElement(() => TargetHudNameLine(), Emphasis: true, FontSize: 22),
                        // Distance inline after the name (compact "35 m"); empty when unknown so nothing shows.
                        new TextElement(() => TargetHudDistText(), Color: MutedColor),
                    }, Gap: 6f),
                    Weight: 1f),
            }, Gap: 8f),
            // CombatMeter-style HP bar: flat role-coloured fill + sweeping sheen, HP value on the left and
            // percent on the right (meter style overlays both texts). Taller + row-filling.
            new BarElement(() => TargetHudHpFraction(), EnemyHpColor, () => TargetHudHpLabel())
            {
                Style = BarStyle.Modern, Sheen = true,
                Height = 28f, FillWidth = true, LabelFontSize = 16,
                SecondaryLabel = () => TargetHudHpPct(),
                // Monster/boss shield ("armor" / 护盾) drawn as a translucent cyan overlay ON the HP bar
                // (shield current over HP max — the band shrinks as the shield drains; 0 ⇒ invisible).
                Overlay01      = () => TargetHudShieldOverlayFraction(),
                OverlayColor   = ShieldOverlayColor,
                OverlayInFront = true,
            },

            // Boss/elite break (stagger) gauge — only shown when the target actually has one (MaxStunned > 0).
            // Two mutually-exclusive bars so the fill colour can differ by state (BarElement's colour arg is a
            // ColorRgba VALUE, no Func overload): blue live gauge while draining, amber refill tween while broken.
            // Live bar (draining): blue, current/max values + percent.
            new ConditionalElement(() => TargetHudHasBreak() && !TargetHudBroken(),
                new BarElement(() => TargetHudBreakFraction(), BreakBarColor, () => TargetHudBreakLabel())
                {
                    Style = BarStyle.Modern, Height = 18f, FillWidth = true, LabelFontSize = 14,
                    SecondaryLabel = () => TargetHudBreakPct(),
                }),
            // Break/recovery bar: amber, local fill tween + seconds-remaining countdown.
            new ConditionalElement(() => TargetHudHasBreak() && TargetHudBroken(),
                new BarElement(() => TargetHudBreakFraction(), BreakRecoverColor, () => _loc.T("tl.hud.break"))
                {
                    Style = BarStyle.Modern, Height = 18f, FillWidth = true, LabelFontSize = 14,
                    SecondaryLabel = () => TargetHudBreakRemainLabel(),
                }),

            // One combined buff + debuff grid of CooldownBar-style tiles (skill icon when the effect has a
            // parent skill, else the buff icon); debuff = red/orange accent, buff = green. Shown ONLY in
            // Classic style (buff_style == 0); in List style the standalone Target Effects window renders
            // these instead (see Plugin.BuffListWindow.cs), so the classic grid collapses away here.
            new ConditionalElement(() => _buffStyle == 0, BuildTargetHudEffectTiles()),
        }, Gap: 4f) { Padding = 8 };

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
        if (w < 1f) w = 420f;                                   // pre-mount fallback = default width
        int fit = (int)((w - 16f - 34f) / TileStride);          // 16 = column padding (8*2), 34 = +N reserve
        return System.Math.Clamp(fit, 1, HudBuffSlots);
    }

    // ── Layout-edit example data ────────────────────────────────────────────────
    // While the layout editor (Shift+`) is active and no monster is locked, feed the getters representative
    // sample data so the overlay has visible content to size and position against. A real live target during
    // editing still wins — ShowExample is false whenever an actual target exists.
    private bool ShowExample => _services.Windows.IsLayoutEditing && !_targetInfo.Current.HasTarget;

    private static readonly TargetInfoTracker.Snapshot ExampleSnapshot = new(
        has: true, uuid: 0, name: "Example Boss", hp: 720000, maxHp: 1000000,
        stunned: 350, maxStunned: 500, level: 90, rank: "Boss", configId: 0,
        hasDistance: true, distance: 35f);

    // Three tiles that exercise every icon path: a real buff-table icon, an override (Reliance ★), a letter fallback.
    private static readonly TargetBuffRow[] ExampleEffects =
    {
        new() { BaseId = 2110137, Name = "Void Corruption Power", Duration = 20000, SnapRemain = 14f, BuffType = 1, Layer = 3 },
        new() { BaseId = 2110135, Name = "Rolora - Active Timer", Duration = 20000, SnapRemain = 8f,  BuffType = 1 },
        new() { BaseId = 990001,  Name = "Sample Effect",         Duration = 30000, SnapRemain = 22f, BuffType = 0 },
    };

    private TargetInfoTracker.Snapshot Cur => ShowExample ? ExampleSnapshot : _targetInfo.Current;

    private IReadOnlyList<TargetBuffRow> CurEffects()
    {
        if (!ShowExample) { _targetBuff.Ensure(); return _targetBuff.All; }
        long now = System.Environment.TickCount64;   // re-stamp so the sample captions read steady while editing
        foreach (var e in ExampleEffects) e.SnapTick = now;
        return ExampleEffects;
    }

    // ── Header / portrait / HP / distance ──────────────────────────────────────

    // Best-effort head portrait. Returns null → header still renders name + HP fine.
    private object? GetMonsterPortrait()
        => _monIcon.GetIcon((int)Cur.ConfigId, out _portraitUv);

    // Name only — the rank is shown separately as a coloured tag (below). Level is intentionally omitted
    // (AttrLevel never populates for monsters).
    private string TargetHudNameLine()
    {
        var s = Cur;
        if (!s.HasTarget) return _loc.T("tl.hud.unknown");
        return string.IsNullOrEmpty(s.Name) ? _loc.T("tl.hud.unknown") : s.Name;
    }

    // Rank tag: bold + coloured (Boss = gold, Elite = orange); shown only for Boss/Elite, never Normal.
    private static readonly ColorRgba BossRankColor  = new(0.98f, 0.80f, 0.30f, 1f); // gold
    private static readonly ColorRgba EliteRankColor = new(0.95f, 0.55f, 0.25f, 1f); // orange

    private bool TargetHudHasRank()
    {
        var s = Cur;
        return s.HasTarget && !string.IsNullOrEmpty(s.Rank) && s.Rank != "Normal";
    }

    // Rank is game data ("Boss"/"Elite"/"Normal") kept as-is for the logic compares above; only the DISPLAYED
    // tag is localized. Any other rank value falls back to the uppercased raw string.
    private string TargetHudRankText()
    {
        if (!TargetHudHasRank()) return "";
        var rank = Cur.Rank;
        if (rank == "Boss")  return _loc.T("tl.rank.boss");
        if (rank == "Elite") return _loc.T("tl.rank.elite");
        return rank.ToUpperInvariant();
    }

    private ColorRgba? TargetHudRankColor()
    {
        var s = Cur;
        return s.Rank == "Boss" ? BossRankColor : s.Rank == "Elite" ? EliteRankColor : (ColorRgba?)null;
    }

    private float TargetHudHpFraction()
    {
        var s = Cur;
        return s.MaxHp > 0 ? Clamp01((float)s.Hp / s.MaxHp) : 0f;
    }

    private string TargetHudHpLabel()   // left overlay: HP values (+ shield in parens when shielded)
    {
        var s = Cur;
        _targetInfo.TryGetCurrentShield(out var shield, out _);
        string sh = shield > 0 ? $" ({shield:N0})" : "";
        if (s.MaxHp <= 0) return s.Hp > 0 ? $"{s.Hp:N0}{sh}" : "?";
        return $"{s.Hp:N0} / {s.MaxHp:N0}{sh}";
    }

    private string TargetHudHpPct()     // right overlay: percent
    {
        var s = Cur;
        return s.MaxHp > 0 ? $"{100f * s.Hp / s.MaxHp:F0}%" : "";
    }

    // ── Break / stagger gauge (bosses + elites; MaxStunned > 0 is the presence signal) ──
    // NOTE: fill-vs-deplete DIRECTION is unverified — rendered as current/max. Confirm in-game.
    private bool TargetHudHasBreak()
    {
        var s = Cur;
        return s.HasTarget && s.MaxStunned > 0;
    }

    // Per-frame driver for the local break tween. Safe to call repeatedly — BreakGauge frame-gates internally.
    private void UpdateBreakGaugeState()
    {
        var s = Cur;
        float dur = _targetInfo.TryGetBreakingContinueTime(s.ConfigId, out var sec) ? sec : 8f; // fallback 8s on table miss
        _breakGauge.Update(s.Uuid, s.Stunned, s.MaxStunned, dur, System.Environment.TickCount64);
    }

    private float TargetHudBreakFraction()
    {
        UpdateBreakGaugeState();
        return _breakGauge.Fill;
    }

    // True during the local recovery window (gauge broken → refilling); drives which of the two bars renders.
    private bool TargetHudBroken()
    {
        UpdateBreakGaugeState();
        return _breakGauge.Broken;
    }

    // Countdown label shown on the amber recovery bar (seconds remaining until the gauge snaps back to full).
    private string TargetHudBreakRemainLabel() => $"{_breakGauge.RemainingSec:F1}s";

    private string TargetHudBreakLabel()   // left overlay: values only (no "BREAK" word)
    {
        var s = Cur;
        return s.MaxStunned > 0 ? $"{s.Stunned:N0} / {s.MaxStunned:N0}" : "";
    }

    private string TargetHudBreakPct()     // right overlay: percent
    {
        var s = Cur;
        return s.MaxStunned > 0 ? $"{100f * s.Stunned / s.MaxStunned:F0}%" : "";
    }

    // Compact distance shown inline after the name (e.g. "35 m"); empty when unknown so nothing renders.
    private string TargetHudDistText()
    {
        var s = Cur;
        return s.HasDistance ? $"{s.Distance:F0} m" : "";
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
