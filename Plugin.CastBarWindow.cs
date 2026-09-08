using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.TargetLens;

// Standalone CAST BAR overlay — a separate auto-showing HUD window that appears while the CURRENT target is
// channeling a skill, showing the skill icon + name + a count-UP bar, and hides the moment the cast ends or is
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

    // Layout-edit sample: a danger cast mid-flight (count-up: 1.2s into a 4.0s cast) so the icon slot, name line and
    // bar all have content to size and place against when nothing is actually casting. skillId 0 → no icon resolves,
    // the icon cell still reserves its width.
    private static readonly TargetInfoTracker.CastInfo ExampleCast =
        new(casting: true, skillId: 0, skillName: "Meteor Strike", totalSec: 4.0f, elapsedSec: 1.2f, danger: true);

    private void RegisterCastBarWindow()
    {
        // Persisted toggle drives both initial visibility and the ShouldRender gate (default ON — user asked for it).
        _castBarOn = _cfg.Get<bool>("cast_bar_on", true);

        const float CastW = 340f;   // a conventional cast-bar width — icon + one name line + one bar
        const float CastH = 66f;    // icon(34) row; name(~18)+gap(4)+bar(20) column; +padding(16) → content-fit

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

    // Skill icon on the left, then a name line + a single count-UP bar. The fill is the ELAPSED fraction (starts ~0,
    // grows to 1) and the SecondaryLabel is the "elapsed / total" seconds — matching the game's cast bar direction.
    private HudElement BuildCastBarWindowRoot() => new ColumnElement(new HudElement[]
    {
        new RowElement(new HudElement[]
        {
            new CellElement(
                new GameTextureElement(() => GetCastIcon(), 34, 34, () => _castUv),
                Width: 40f),
            new CellElement(new ColumnElement(new HudElement[]
            {
                new TextElement(() => CastNameLine(), Emphasis: true, FontSize: 15, NoWrap: true),
                new ConditionalElement(() => !CastCur().Danger,
                    new BarElement(() => CastFraction(), CastNormalColor, () => "")
                    {
                        Style = BarStyle.Modern, Height = 20f, FillWidth = true, LabelFontSize = 13,
                        SecondaryLabel = () => CastTimeLabel(),
                    }),
                new ConditionalElement(() => CastCur().Danger,
                    new BarElement(() => CastFraction(), CastDangerColor, () => "")
                    {
                        Style = BarStyle.Modern, Height = 20f, FillWidth = true, LabelFontSize = 13,
                        SecondaryLabel = () => CastTimeLabel(),
                    }),
            }, Gap: 4f), Weight: 1f),
        }, Gap: 8f),
    }, Gap: 0f) { Padding = 8 };

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

    private string CastNameLine()
    {
        var n = CastCur().SkillName;
        return string.IsNullOrEmpty(n) ? "Casting…" : n;
    }

    // COUNT UP: elapsed fraction 0..1 (starts ~0, grows to 1). Clamp01 is defined in Plugin.TargetHud.cs (same class).
    private float CastFraction()
    {
        var c = CastCur();
        return c.TotalSec > 0f ? Clamp01(c.ElapsedSec / c.TotalSec) : 0f;
    }

    // Compact "elapsed / total" one-decimal readout — elapsed rises toward total, matching the in-game bar.
    private string CastTimeLabel()
    {
        var c = CastCur();
        float e = c.ElapsedSec < 0f ? 0f : c.ElapsedSec;
        return $"{e:F1} / {c.TotalSec:F1}s";
    }
}
