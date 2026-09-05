using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.TargetLens;

// Standalone BOSS CAST BAR overlay — a separate auto-showing HUD window that appears ONLY while the current
// (boss) target is chanting a skill, showing the skill name + a shrinking countdown bar, and hides the moment the
// cast ends. Mirrors the threat / buff-list windows (Plugin.ThreatWindow.cs / Plugin.BuffListWindow.cs): a
// borderless auto-showing HUD overlay gated by ShouldRender, with layout-edit example data so it can be placed
// without a live cast. The cast read is TargetInfoTracker.TryGetCast (see TargetInfoTracker.Cast.cs).
//
// Scope: the underlying ZUIBossBlood widget exists only for bona-fide bosses, so this overlay simply won't show
// for normal/elite mobs that cast. No skill id is exposed by the widget → NAME + bar + countdown only (no icon).
public sealed partial class Plugin
{
    private IWindowControl _castBarWindow = null!;   // registered in the ctor; auto-shows while a boss casts
    private bool           _castBarOn;               // config-backed: enable the cast-bar overlay (default ON)
    private bool           _castDiag;                // config-backed: TEMPORARY cast-read diagnostic logging

    // Cast bar reads best warm + centered: amber for a normal cast, red-orange for the danger variant. BarElement's
    // colour arg is a ColorRgba VALUE (no Func overload), so two mutually-exclusive bars carry the two colours,
    // gated by a ConditionalElement on the Danger flag — the same trick the break bar uses.
    private static readonly ColorRgba CastNormalColor = new(0.95f, 0.80f, 0.30f, 1f); // amber / yellow
    private static readonly ColorRgba CastDangerColor = new(0.92f, 0.32f, 0.20f, 1f); // red-orange (danger cast)

    // Layout-edit sample: a danger cast mid-flight so both the name line and the countdown bar have content to
    // size and place against when nothing is actually casting.
    private static readonly TargetInfoTracker.CastInfo ExampleCast =
        new(casting: true, skillName: "Meteor Strike", totalSec: 4.0f, remainSec: 3.2f, danger: true);

    private void RegisterCastBarWindow()
    {
        // Persisted toggle drives both initial visibility and the ShouldRender gate (default ON — user asked for it).
        _castBarOn = _cfg.Get<bool>("cast_bar_on", true);

        const float CastW = 320f;   // a conventional cast-bar width — one name line + one bar
        const float CastH = 62f;    // name (~18) + gap (4) + bar (20) + padding (16); non-resizable → content-fit

        // Conventional cast-bar spot: horizontally centered, upper third of the screen. Expressed off ScreenWidth
        // like the other overlays; the user's own saved drag overrides it.
        float x = (_services.Framework.ScreenWidth - CastW) / 2f;
        float y = 140f;

        _castBarWindow = _services.Windows.Register(new WindowRegistration(
            Spec: new WindowSpec(
                Id:          "targetlens.castbar",
                Title:       "Boss Cast",
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

    // Name line on top, then two mutually-exclusive countdown bars (normal amber / danger red-orange) — the fill
    // is the REMAINING fraction (starts ~1, shrinks to 0) and the SecondaryLabel is the seconds countdown.
    private HudElement BuildCastBarWindowRoot() => new ColumnElement(new HudElement[]
    {
        new TextElement(() => CastNameLine(), Emphasis: true, FontSize: 15, NoWrap: true),
        new ConditionalElement(() => !CastCur().Danger,
            new BarElement(() => CastFraction(), CastNormalColor, () => "")
            {
                Style = BarStyle.Modern, Height = 20f, FillWidth = true, LabelFontSize = 13,
                SecondaryLabel = () => CastRemainLabel(),
            }),
        new ConditionalElement(() => CastCur().Danger,
            new BarElement(() => CastFraction(), CastDangerColor, () => "")
            {
                Style = BarStyle.Modern, Height = 20f, FillWidth = true, LabelFontSize = 13,
                SecondaryLabel = () => CastRemainLabel(),
            }),
    }, Gap: 4f) { Padding = 8 };

    // ── Getters (layout-edit example data wins only when NOT actually casting) ──

    // True when a real boss cast is in flight (frame-cached read; safe to call repeatedly per frame).
    private bool LiveCasting() => _targetInfo.TryGetCast(out var c) && c.Casting;

    private bool CastShowExample => _services.Windows.IsLayoutEditing && !LiveCasting();

    private TargetInfoTracker.CastInfo CastCur()
    {
        if (CastShowExample) return ExampleCast;
        _targetInfo.TryGetCast(out var c);
        return c;   // Casting == false when idle; the window is hidden then anyway
    }

    private string CastNameLine()
    {
        var n = CastCur().SkillName;
        return string.IsNullOrEmpty(n) ? "Casting…" : n;
    }

    // Remaining fraction 0..1 (starts ~1, shrinks to 0). Clamp01 is defined in Plugin.TargetHud.cs (same class).
    private float CastFraction()
    {
        var c = CastCur();
        return c.TotalSec > 0f ? Clamp01(c.RemainSec / c.TotalSec) : 0f;
    }

    // Compact one-decimal countdown, clamped at 0.
    private string CastRemainLabel()
    {
        var c = CastCur();
        float r = c.RemainSec < 0f ? 0f : c.RemainSec;
        return $"{r:F1}s";
    }
}
