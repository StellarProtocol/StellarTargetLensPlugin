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
    private bool           _castDiag;                // config-backed: TEMPORARY cast-read diagnostic logging
    private UvRect         _castUv;                   // atlas rect for the current cast's skill icon (own slot)

    // Cast bar reads best warm + centered: amber for a normal cast, red-orange for the danger variant. BarElement's
    // colour arg is a ColorRgba VALUE (no Func overload), so two mutually-exclusive bars carry the two colours,
    // gated by a ConditionalElement on the Danger flag — the same trick the break bar uses.
    private static readonly ColorRgba CastNormalColor = new(0.95f, 0.80f, 0.30f, 1f); // amber / yellow
    private static readonly ColorRgba CastDangerColor = new(0.92f, 0.32f, 0.20f, 1f); // red-orange (danger cast)

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

        const float CastW = 340f;   // a conventional cast-bar width — mini icon + one row bar (name inside + time)
        const float CastH = 34f;    // one row: bar(18) + column padding(16) → content-fit (buff/boss-timer row style)

        // Conventional cast-bar spot: horizontally centered, upper third of the screen. Expressed off ScreenWidth
        // like the other overlays; the user's own saved drag overrides it.
        float x = (_services.Framework.ScreenWidth - CastW) / 2f;
        float y = 140f;

        _castBarWindow = _services.Windows.Register(new WindowRegistration(
            Spec: new WindowSpec(
                Id:          "targetlens.castbar",
                Title:       "Cast Bar",
                DefaultRect: new WindowRect(x, y, CastW, CastH),
                Category:    WindowCategory.HUD,
                Style:       WindowPanelStyle.Borderless)
            {
                Draggable = true, EditModeDragOnly = true, Closable = false, StartVisible = false,
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

    // Single row = [mini icon] [ count-UP bar: <skill name> ....... <percent> ], exactly the buff-list /
    // boss-timer row style (Plugin.BuffListWindow.cs / Plugin.BossTimerWindow.cs): the skill NAME sits inside-left
    // (ellipsised), the percent inside-right (SecondaryLabel), and the bar FILLS UP (the game's own fraction,
    // 0→1) to match the game's cast bar direction. The normal-amber / danger-red split rides two mutually-
    // exclusive bars gated by a ConditionalElement (BarElement's colour arg is a VALUE, not a Func — same trick the
    // buff-list debuff/buff split uses).
    private HudElement BuildCastBarWindowRoot()
    {
        var normalBar = new BarElement(() => CastFraction(), CastNormalColor, () => CastNameLine())
        {
            Style = BarStyle.Modern, Height = 18f, FillWidth = true, LabelFontSize = 13,
            LabelInside = true, SecondaryLabel = () => CastTimeLabel(),
        };
        var dangerBar = new BarElement(() => CastFraction(), CastDangerColor, () => CastNameLine())
        {
            Style = BarStyle.Modern, Height = 18f, FillWidth = true, LabelFontSize = 13,
            LabelInside = true, SecondaryLabel = () => CastTimeLabel(),
        };
        return new ColumnElement(new HudElement[]
        {
            new RowElement(new HudElement[]
            {
                new CellElement(
                    new GameTextureElement(() => GetCastIcon(), 18, 18, () => _castUv),
                    Width: 22f),
                new CellElement(
                    new ConditionalElement(() => CastCur().Danger, dangerBar, normalBar),
                    Weight: 1f),
            }, Gap: 6f),
        }, Gap: 0f) { Padding = 8 };
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
    private string CastNameLine()
    {
        var n = CastCur().SkillName;
        if (string.IsNullOrEmpty(n)) return "Casting…";
        return Truncate(StripTags(n), CastNameBudget);
    }

    // COUNT UP: the local elapsed/total fill 0..1 (direction-corrected in TargetInfoTracker.Cast.cs).
    private float CastFraction() => Clamp01(CastCur().Fraction);

    // Right-side label = elapsed / total SECONDS, counting up. SetSingGuide's maxValue is confirmed to be the total
    // cast time in seconds, and elapsed is ticked locally, so this is a real "1.6 / 5.3s" readout.
    private string CastTimeLabel()
    {
        var c = CastCur();
        return $"{c.ElapsedSec:F1} / {c.TotalSec:F1}s";
    }
}
