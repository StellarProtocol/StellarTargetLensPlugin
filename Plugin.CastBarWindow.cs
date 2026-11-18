using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.TargetLens;

// Standalone CAST BAR overlay — a separate auto-showing HUD window that appears while the CURRENT target is
// channeling a skill, showing the skill icon + a count-UP bar with the name inside it, and hides the moment the cast ends or is
// interrupted. Mirrors the threat / boss-timer windows (Plugin.ThreatWindow.cs / Plugin.BossTimerWindow.cs): a
// borderless auto-showing HUD overlay gated by ShouldRender, with layout-edit example data so it can be placed
// without a live cast. The cast read is TargetInfoTracker.TryGetCast (see TargetInfoTracker.Cast.cs), now sourced
// from the CastPatch begin/end hooks rather than the boss HP-frame widget.
//
// Scope: hook-sourced off ZStateSkillComp, so the bar shows for ANY target that channels — boss, elite, or normal
// mob — not just bona-fide bosses. The skill id is available, so the overlay shows the skill ICON, and the bar
// COUNTS UP (elapsed 0→Total, matching the in-game cast bar).
public sealed partial class Plugin
{
    private IWindowControl _castBarWindow = null!;   // registered in the ctor; auto-shows while a target casts
    private bool           _castBarOn;               // config-backed: enable the cast-bar overlay (default ON)
    private UvRect         _castUv;                   // atlas rect for the current cast's skill icon (own slot)

    // Cast-bar row-size multiplier. Baked into the element sizes + window dims at build time, so a live change
    // re-registers the window (SetCastScale → RebuildCastBarWindow, debounced). Default 1.0 = unchanged.
    private float          _castScale        = 1f;
    private long           _castScaleDirtyAtMs = -1; // Environment.TickCount64 of the last slider change; -1 = idle

    // Layout-edit sample: a danger cast mid-flight (30% filled) so the icon slot and the name-inside bar have content
    // to size and place against when nothing is actually casting. skillId 0 → no icon resolves, the icon cell still
    // reserves its width.
    private static readonly TargetInfoTracker.CastInfo ExampleCast =
        new(casting: true, skillId: 0, skillName: "Meteor Strike", danger: true,
            elapsedSec: 1.6f, totalSec: 5.3f, fraction: 0.30f);

    private void RegisterCastBarWindow()
    {
        // Persisted toggle drives both initial visibility and the ShouldRender gate (default ON — user asked for it).
        _castBarOn = _cfg.Get<bool>("cast_bar_on", true);
        // Row-size multiplier. Baked into the element sizes + window dims below, so a live change re-registers the
        // window (SetCastScale → RebuildCastBarWindow). Clamped to the slider band.
        _castScale = System.Math.Clamp(_cfg.Get<float>("cast_scale", 1f), 1f, 2.5f);

        // Base (1×) dims scaled by _castScale so the single-row window grows with the bar (not resizable → no band).
        float castW = 340f * _castScale;   // a conventional cast-bar width — mini icon + one row bar (name inside + time)
        float castH = 28f * _castScale + 2f * ChromePad;   // one row (28 px icon) + chrome padding → content-fit anyway

        // Default position tuned in-game (user's saved 2560x1440 layout: x=1377, y=120). Fixed 1440p-calibrated
        // pixel X (NOT ScreenWidth*frac): "reset all HUD" restores DefaultRect from a path where ScreenWidth is 0,
        // so 0*frac collapsed every window to x=0 — an absolute px restores correctly. y absolute. Own drag overrides.
        float x = 1377f;
        float y = 120f;

        _castBarWindow = _services.Windows.Register(new WindowRegistration(
            Spec: new WindowSpec(
                Id:          "targetlens.castbar",
                Title:       _loc.T("tl.window.castbar"),
                DefaultRect: new WindowRect(x, y, castW, castH),
                Category:    WindowCategory.HUD,
                Style:       WindowPanelStyle.Borderless)
            {
                Draggable = true, EditModeDragOnly = true, Closable = false, StartVisible = false,
                BackgroundColor = CrestPlateFill, BackgroundCornerRadius = CrestPlateRadius,   // Crest plate (Plugin.Crest.cs)
                // Auto-show: in-world, no menu/loading/blocking overlay, the toggle on, a live target, AND that
                // target actively casting. Layout-edit mode overrides so the overlay (with example data) can be
                // positioned without a live cast.
                ShouldRender = () => _services.Windows.IsLayoutEditing
                                  || (_services.ClientState.Phase == GamePhase.World
                                      && (_services.ClientState.UiState & (GameUIState.Loading | GameUIState.Blocking | GameUIState.AnyMenu)) == 0
                                      && _castBarOn
                                      && _targetInfo.Current.HasTarget
                                      && LiveCasting()),
            },
            Root:    BuildCastBarWindowRoot(),
            OnClose: () => { }));
        _windows.Add(_castBarWindow);   // Dispose already loops _windows and Remove()s each

        // Initial visibility from the persisted toggle (the settings toggle flips it live via SetVisible).
        _castBarWindow.SetVisible(_castBarOn);
    }

    // Re-apply a new _castScale by rebuilding the window Root at the new scale (element sizes + window dims are baked
    // at build time). Framework-sanctioned Remove()+Register() (mirrors RebuildBuffListWindow): preserve ONLY the
    // rect — NOT IsShown (at a rebuild moment with no live cast it reads false and would strand the window off);
    // RegisterCastBarWindow's SetVisible(_castBarOn) + the ShouldRender gate handle visibility.
    private void RebuildCastBarWindow()
    {
        var rect = _castBarWindow.Rect;
        _windows.Remove(_castBarWindow);
        _castBarWindow.Remove();
        RegisterCastBarWindow();
        if (rect.Width > 0f) _castBarWindow.SetRect(rect);
    }

    // Cast-bar size slider handler: update _castScale live (knob + readout track the finger) and (re)arm the
    // debounce; the deferred TickCastScaleRebuild does the single persist + rebuild once the drag settles.
    private void SetCastScale(float v)
    {
        _castScale = v;
        _castScaleDirtyAtMs = System.Environment.TickCount64;
    }

    // Per-frame (OnTargetHudUpdate) settle check: once the drag has been quiet for ListScaleSettleMs, persist the
    // final value and rebuild the cast-bar window ONCE at the new scale (mirrors TickListScaleRebuild).
    private void TickCastScaleRebuild()
    {
        if (_castScaleDirtyAtMs < 0) return;
        if (System.Environment.TickCount64 - _castScaleDirtyAtMs < ListScaleSettleMs) return;
        _castScaleDirtyAtMs = -1;
        _cfg.Set<float>("cast_scale", _castScale);
        _cfg.Save();
        RebuildCastBarWindow();
    }

    // Crest look (Plugin.Crest.cs), no caption: [28 px icon, 2 px gold ring] [24 px cast-gold bar: skill name left,
    // "2.9 / 5.3s" right] on the plate. A DANGER cast (ESingGuideType.MonsterDanger) swaps the plate ring to 2 px red
    // (the panel can't glow outward), the icon ring to red and the fill to HP red with a red glow, and prefixes "! ".
    // The two fills are bar variants (a gradient is fixed at build) shown by the Danger flag. Sizes scale with _castScale.
    private HudElement BuildCastBarWindowRoot()
    {
        float sc     = _castScale;
        int   iconPx = (int)System.Math.Round(28 * sc);
        float barH   = 24f * sc;
        int   fontPx = (int)System.Math.Round(13 * sc);
        var normal = CrestBar(() => CastFraction(), GradCast, () => CastNameLine(), () => CastTimeLabel(), barH, fontPx, sc);
        var danger = CrestBar(() => CastFraction(), GradHp, () => CastNameLine(), () => CastTimeLabel(), barH, fontPx, sc)
            with { FillGlow = CrestDanger with { A = 0.8f }, GlowRadius = 6f * sc };
        var bars = CrestBarRings(BarVariants(barH, (normal, () => !CastCur().Danger), (danger, () => CastCur().Danger)), sc);
        var icon = CrestIcon(() => GetCastIcon(), () => _castUv, iconPx,
            () => CastCur().Danger ? CrestDanger : CrestRingNeutral, 2f);
        return CrestFrame(new RowElement(new HudElement[]
        {
            new CellElement(icon, Width: iconPx),
            new CellElement(bars, Weight: 1f),
        }, Gap: 6f * sc), danger: () => CastCur().Danger);
    }

    // ── Getters (layout-edit example data wins only when NOT actually casting) ──

    // True when a real cast is in flight (frame-cached read; safe to call repeatedly per frame).
    private bool LiveCasting() => _targetInfo.TryGetCast(out var c) && c.Casting;

    private bool CastShowExample => _services.Windows.IsLayoutEditing && !LiveCasting();

    private TargetInfoTracker.CastInfo CastCur()
    {
        if (CastShowExample) return ExampleCast;
        _targetInfo.TryGetCast(out var c);
        return c;   // Casting == false when idle; the window is hidden then anyway
    }

    // Skill icon for the current cast: imagine/aoyi atlas first, then the regular skill atlas (mirrors the boss-timer
    // + SkillCD idiom). Any miss clears the UV and returns null → the cell shows no icon but keeps its reserved width.
    private object? GetCastIcon()
    {
        int id = CastCur().SkillId;
        if (id <= 0) { _castUv = default; return null; }
        var im = _services.GameAssets.LoadImagineIcon(id, out _castUv);
        if (im != null) return im;
        return _services.GameAssets.LoadSkillIcon(id, out _castUv);
    }

    // Inside-left bar label = skill name, tag-stripped and ellipsised so it can't collide with the right-aligned
    // "elapsed / total" (same Truncate(StripTags(...)) approach the buff-list / boss-timer rows use). The name budget
    // leaves room for the time reserve at the 340 default width.
    private const int CastNameBudget = 30;   // name chars before ellipsis (mirrors BuffListNameBudget's fixed cap)
    private string? _castNameSrc; private bool _castNameDanger; private string _castName = "";
    private string CastNameLine()
    {
        var c = CastCur();
        var n = c.SkillName;
        if (ReferenceEquals(n, _castNameSrc) && c.Danger == _castNameDanger && _castName.Length > 0) return _castName;
        _castNameSrc = n; _castNameDanger = c.Danger;
        var core = string.IsNullOrEmpty(n) ? _loc.T("tl.cast.casting") : Truncate(StripTags(n), CastNameBudget);
        _castName = c.Danger ? "! " + core : core;   // danger prefix (Crest spec)
        return _castName;
    }

    // COUNT UP: the local elapsed/total fill 0..1 (direction-corrected in TargetInfoTracker.Cast.cs).
    private float CastFraction() => Clamp01(CastCur().Fraction);

    // Right-side label = elapsed / total SECONDS, counting up. SetSingGuide's maxValue is confirmed to be the total
    // cast time in seconds, and elapsed is ticked locally, so this is a real "1.6 / 5.3s" readout.
    private int _castTimeE = -1, _castTimeT = -1; private string _castTime = "";
    private string CastTimeLabel()
    {
        var c = CastCur();
        int e = (int)(c.ElapsedSec * 10f), t = (int)(c.TotalSec * 10f);
        if (e == _castTimeE && t == _castTimeT) return _castTime;   // rebuilt only when a shown tenth changes
        _castTimeE = e; _castTimeT = t;
        _castTime = $"{e / 10f:F1} / {t / 10f:F1}s";
        return _castTime;
    }
}
