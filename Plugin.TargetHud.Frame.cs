using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.TargetLens;

// "Resonance Glass" boss-bar frame for the Target HUD (user-approved 2026-11-18; spec + geometry:
// _scratch/prototypes/targetlens-bossbar-APPROVED.md and tl-bossbar-art-bpsr/README.md). Drawn at 2× — 150 px tall,
// total width 532 + M where M is the stretched middle (follows the window width).
//
//   LayerStack   base = a 150 px height strut (the stack is as wide as the window content)
//     ├─ portrait      (32, 32, 88×88) circle r 44 — UNDER the frame (the art's portrait opening is clear)
//     ├─ frame art     framework 3-slice: left 184 + middle (stretched) + right 348
//     ├─ badge art     (144, 11, 120×40) gold = BOSS, orange = ELITE, + its text
//     ├─ name          left-centre at x 276 (144 when there is no badge), drop-shadow copy under it
//     ├─ distance      right cap (150, 12, 140×40), right-aligned
//     ├─ HP bar        interior (144, 60, 356+M × 40): gradient + gloss + 10 % ticks + damage trail + striped shield
//     └─ break gauge   interior (144, 112, 56+M × 16), 10 segments, label right of it on the right cap (bosses only)
//
// Every slot is a StackLayer.Rect (framework 2.34.0) measured from the frame's edges, so a width resize moves anchors
// only. Art colours are fixed by design (they don't follow the theme). Getters are ≤10 Hz (window refresh) and
// allocation-free: display strings are cached and rebuilt only when the shown value (or the language) changes.
public sealed partial class Plugin
{
    private const float FrameH = 150f;
    private const float FrameCapsW = 532f;   // left cap 184 + right cap 348

    private static ColorRgba Hex(int rgb, float a = 1f)
        => new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, a);

    private static readonly ColorRgba FrameClear     = new(0f, 0f, 0f, 0f);
    private static readonly ColorRgba NameShadowCol  = new(0f, 0f, 0f, 0.6f);
    private static readonly ColorRgba DistColor      = Hex(0xcfd8e0);
    private static readonly ColorRgba BossBadgeText  = Hex(0xfff3cf);
    private static readonly ColorRgba EliteBadgeText = Hex(0xfff0dd);
    private static readonly ColorRgba HpLowColor     = Hex(0xff6b6b);
    private static readonly ColorRgba BreakLabelCol  = Hex(0x9fc8ff);
    private static readonly ColorRgba BrokenLabelCol = Hex(0xffd166);

    private static readonly GradientStop[] HpGradient =
        { new(0f, Hex(0xff8a7a)), new(0.45f, Hex(0xe5414a)), new(1f, Hex(0xa8172b)) };
    private static readonly GradientStop[] BreakGradient  = { new(0f, Hex(0x8fd0ff)), new(1f, Hex(0x2f86e6)) };
    private static readonly GradientStop[] BrokenGradient = { new(0f, Hex(0xffe08a)), new(1f, Hex(0xf29a2a)) };

    // Embedded art (csproj Resources/resonance_glass_*.png), read once and cached by CachedPng (Plugin.Style.Blocks.cs).
    private static byte[]? FramePng(string piece) => CachedPng("resonance_glass_" + piece + ".png");
    private static readonly System.Func<byte[]?> FrameLeftPng = () => FramePng("left"), FrameMidPng = () => FramePng("mid"),
        FrameRightPng = () => FramePng("right"), BadgeGoldPng = () => FramePng("badge_gold"), BadgeOrangePng = () => FramePng("badge_orange");

    private static StackLayer At(HudElement content, LayerRect rect, System.Func<bool>? visible = null)
        => new(content) { Rect = rect, Visible = visible };

    private HudElement BuildTargetHudFrame() => new LayerStackElement(
        new SpacerElement(Height: FrameH),
        At(new GameTextureElement(() => GetMonsterPortrait(), 88, 88, () => _portraitUv) { CornerRadius = 44 },
            new LayerRect { Left = 32, Top = 32, Width = 88, Height = 88 }),
        new StackLayer(new SlicedImageElement(FrameLeftPng, FrameMidPng, FrameRightPng, (int)FrameH)),
        // Rank badge: art + text (BOSS / ELITE). Normal monsters: no badge, and the name moves left.
        At(new ImageElement(BadgeGoldPng, 120, 40) { Crisp = true }, BadgeRect, TargetHudIsBoss),
        At(new ImageElement(BadgeOrangePng, 120, 40) { Crisp = true }, BadgeRect, TargetHudIsElite),
        At(new TextElement(() => TargetHudRankText(), Color: () => TargetHudIsBoss() ? BossBadgeText : EliteBadgeText,
            Align: TextAlign.Center, FontSize: 20) { Bold = true, CenterCaps = true }, BadgeRect, TargetHudHasRank),
        // Name (white bold). The TMP text path has no shadow, so a dark copy 2 px down-right stands in for it.
        NameLayer(276f, true, shadow: true), NameLayer(276f, true, shadow: false),
        NameLayer(144f, false, shadow: true), NameLayer(144f, false, shadow: false),
        // Distance: right cap (150, 12, 140×40) → 58 px from the frame's right edge.
        At(new TextElement(() => TargetHudDistText(), Color: () => DistColor, Align: TextAlign.Right, FontSize: 22) { Bold = true },
            new LayerRect { Right = 58, Width = 140, Top = 12, Height = 40 }),
        At(BuildFrameHpBar(), new LayerRect { Left = 144, Right = 32, Top = 60, Height = 40 }),
        // Break gauge (bosses): blue while draining, gold + glow while broken (refill tween). Elite/normal: empty row.
        At(BuildFrameBreakBar(BreakGradient, glow: false), BreakRect, () => TargetHudHasBreak() && !TargetHudBroken()),
        At(BuildFrameBreakBar(BrokenGradient, glow: true), BreakRect, () => TargetHudHasBreak() && TargetHudBroken()),
        // Label right of the gauge on the right cap (gauge ends 332 px from the right edge; +16 gap).
        At(new TextElement(() => TargetHudBreakText(), Color: () => TargetHudBroken() ? BrokenLabelCol : BreakLabelCol, FontSize: 20)
            { Bold = true, CenterCaps = true }, new LayerRect { Right = 56, Width = 260, Top = 104, Height = 32 }, TargetHudHasBreak));

    private static readonly LayerRect BadgeRect = new() { Left = 144, Top = 11, Width = 120, Height = 40 };
    private static readonly LayerRect BreakRect = new() { Left = 144, Right = 332, Top = 112, Height = 16 };

    // One name layer: left-centred at x = left on the badge row; stops short of the distance area (206 px from the right).
    private StackLayer NameLayer(float left, bool withRank, bool shadow)
    {
        float d = shadow ? 2f : 0f;
        var text = new TextElement(() => TargetHudNameLine(), Color: shadow ? () => NameShadowCol : null, FontSize: 30)
            { Bold = true, MaxLines = 1 };
        return At(text, new LayerRect { Left = left + d, Right = 206 - d, Top = 11 + d, Height = 40 },
            withRank ? TargetHudHasRank : () => !TargetHudHasRank());
    }

    private HudElement BuildFrameHpBar() => new BarElement(() => TargetHudHpFraction(), Hex(0xffffff), () => TargetHudHpLabel())
    {
        Style = BarStyle.Modern, Height = 40f, FillWidth = true, LabelFontSize = 22,
        LabelAlign = TextAlign.Center, LabelOutline = true, LabelInset = 16f,
        SecondaryLabel = () => TargetHudHpPct(), SecondaryLabelColorFn = () => TargetHudHpFraction() < 0.2f ? HpLowColor : null,
        TrackColorFn = () => FrameClear,   // the art's interior is the track
        FillGradient = HpGradient, Gloss = true,
        Ticks = 10, TickColor = new ColorRgba(0f, 0f, 0f, 0.35f), TickWidth = 2f,
        Trail01 = () => TargetHudTrailFraction(), TrailColor = Hex(0xf6d0c8),
        // Shield: a striped cyan segment right after the HP fill, [hp, hp + shield] / max (same shield value as before).
        OverlayStart01 = () => TargetHudHpFraction(), Overlay01 = () => TargetHudShieldEndFraction(), OverlayInFront = true,
        OverlayColor = Hex(0x7ff0ff), OverlayStripeColor = Hex(0x3fc6e6), OverlayStripeWidth = 8f,
        OverlayGlow = Hex(0x7ff0ff, 0.8f), GlowRadius = 12f,
    };

    private HudElement BuildFrameBreakBar(GradientStop[] gradient, bool glow) => new BarElement(() => TargetHudBreakFraction(), Hex(0xffffff))
    {
        Style = BarStyle.Modern, Height = 16f, FillWidth = true,
        TrackColorFn = () => FrameClear, FillGradient = gradient,
        Ticks = 10, TickColor = new ColorRgba(10f / 255f, 16f / 255f, 30f / 255f, 0.9f), TickWidth = 4f,
        FillGlow = glow ? Hex(0xffb84a, 0.9f) : null, GlowRadius = 12f,
    };

    // ── Damage trail: holds the previous HP ~0.6 s after a drop, then eases down over ~0.4 s (ease-out). Resets on a
    //    target change and on heals. Pure arithmetic on TickCount64 — no allocation. Advances at the window refresh rate.
    private const long TrailHoldMs = 600, TrailEaseMs = 400;
    private long _trailUuid = long.MinValue, _trailHoldUntil;
    private float _trailShown, _trailLastHp, _trailFrom;

    private float TargetHudTrailFraction()
    {
        var s = Cur;
        float hp = TargetHudHpFraction();
        long now = System.Environment.TickCount64;
        if (s.Uuid != _trailUuid || hp >= _trailShown)   // new target, heal, or caught up → no trail
        {
            _trailUuid = s.Uuid; _trailShown = _trailLastHp = hp; _trailHoldUntil = 0;
            return hp;
        }
        if (hp < _trailLastHp) { _trailFrom = _trailShown; _trailHoldUntil = now + TrailHoldMs; }   // a new hit: hold again
        _trailLastHp = hp;
        if (now < _trailHoldUntil) return _trailShown;
        float t = (now - _trailHoldUntil) / (float)TrailEaseMs;
        _trailShown = t >= 1f ? hp : _trailFrom + (hp - _trailFrom) * (1f - (1f - t) * (1f - t));
        return _trailShown;
    }

    // ── Cached display strings (rebuilt only when the shown value changes) ──
    private long _hpLblCur = -1, _hpLblMax = -1; private string _hpLbl = "";
    private int _pctVal = -1; private string _pctLbl = "";
    private int _distVal = -1; private string _distLbl = "";
    private int _breakVal = int.MinValue; private string? _breakTpl; private string _breakLbl = "";

    private string TargetHudHpLabel()   // centre: "cur / max"
    {
        var s = Cur;
        if (s.Hp == _hpLblCur && s.MaxHp == _hpLblMax) return _hpLbl;
        _hpLblCur = s.Hp; _hpLblMax = s.MaxHp;
        _hpLbl = s.MaxHp <= 0 ? (s.Hp > 0 ? s.Hp.ToString("N0") : "?") : $"{s.Hp:N0} / {s.MaxHp:N0}";
        return _hpLbl;
    }

    private string TargetHudHpPct()     // right: percent
    {
        var s = Cur;
        int p = s.MaxHp > 0 ? (int)System.Math.Round(100.0 * s.Hp / s.MaxHp) : -2;
        if (p == _pctVal) return _pctLbl;
        _pctVal = p; _pctLbl = p < 0 ? "" : p + "%";
        return _pctLbl;
    }

    private string TargetHudDistText()  // "35 m"; empty when unknown
    {
        var s = Cur;
        int d = s.HasDistance ? (int)System.Math.Round(s.Distance) : -1;
        if (d == _distVal) return _distLbl;
        _distVal = d; _distLbl = d < 0 ? "" : d + " m";
        return _distLbl;
    }

    // "BREAK 40%" while draining, "BROKEN 5.2s" while broken. Keyed on the shown number (+ state sign) and the
    // localized template, so a language switch re-formats once.
    private string TargetHudBreakText()
    {
        var s = Cur;
        bool broken = TargetHudBroken();
        string tpl = _loc.T(broken ? "tl.hud.broken" : "tl.hud.break_pct");
        int v = broken ? -1 - (int)(_breakGauge.RemainingSec * 10f)
                       : (s.MaxStunned > 0 ? (int)System.Math.Round(100.0 * s.Stunned / s.MaxStunned) : 0);
        if (v == _breakVal && ReferenceEquals(tpl, _breakTpl)) return _breakLbl;
        _breakVal = v; _breakTpl = tpl;
        _breakLbl = broken ? string.Format(tpl, ((-1 - v) / 10f).ToString("F1")) : string.Format(tpl, v);
        return _breakLbl;
    }
}
