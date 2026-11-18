using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.TargetLens;

// "Celestial Crest" boss-bar frame for the Target HUD (user-chosen 2026-11-18, direction A — replaced Resonance Glass;
// art + geometry: _scratch/prototypes/tl-bossbar-art-v2/README.md, same slots as v1). Two sizes, picked in Settings →
// Target HUD → "Boss bar size" (config "hud_bar_size": 0 = Slim, the default; 1 = Large):
//   Slim  = the 1× art (@1x PNGs), frame 75 px tall, total width 266 + M  — the "slim" ~9:1 look of the prototype.
//   Large = the 2× art, frame 150 px tall, total width 532 + M.
// M is the stretched middle (follows the window width). Geometry below is written in 2× px and halved for Slim (G),
// so both sizes share one layout:
//
//   LayerStack   base = a frame-height strut (the stack is as wide as the window content)
//     ├─ portrait      (32, 32, 88×88) circle r 44 — UNDER the frame (the art's portrait socket is transparent)
//     ├─ frame art     framework 3-slice: left 184 + middle (stretched) + right 348
//     ├─ badge art     (144, 11, 120×40) gold = BOSS, orange = ELITE, + its text
//     ├─ name row      name left-centred at x 276 (144 with no badge) + the distance, right-aligned, ending 58 px
//     │                from the right edge (inside the right cap's distance area); dark drop-shadow copy under it
//     ├─ HP bar        interior (144, 60, 356+M × 40): gradient + gloss + 10 % ticks + damage trail + striped shield
//     └─ break gauge   interior (144, 112, 56+M × 16), 10 segments, label right of it on the right cap (MaxStunned > 0)
//
// Every slot is a StackLayer.Rect (framework 2.34.0) measured from the frame's edges, so a width resize moves anchors
// only. Art colours are fixed by design (they don't follow the theme). Getters are ≤10 Hz (window refresh) and
// allocation-free: display strings are cached and rebuilt only when the shown value (or the language) changes.
// Text: every label is TMP (SDF, sharp at any size) except the HP bar's two labels, which are uGUI dynamic-font Text
// rasterised at their real pixel size (Scaled(size)) — never a bitmap scaled up — so the 12 px Slim labels stay crisp.
public sealed partial class Plugin
{
    private bool _hudLarge;   // config "hud_bar_size" == 1 → Large (2×); default Slim (1×). Baked into the window at build.

    private float FrameH => _hudLarge ? 150f : 75f;
    private float FrameCapsW => _hudLarge ? 532f : 266f;   // left cap + right cap at native width

    // 2× px → this size's px (Slim halves; .5 rounds away from zero, matching the README's 1× table: 11 → 6).
    private float G(float px2x) => _hudLarge ? px2x : System.MathF.Round(px2x * 0.5f, System.MidpointRounding.AwayFromZero);
    private int Fs(int slim, int large) => _hudLarge ? large : slim;   // font size per size (prototype CSS for Slim)

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

    // Embedded art (csproj Resources/celestial_crest_*.png, 2× and @1x), read once and cached by CachedPng
    // (Plugin.Style.Blocks.cs). The Func is chosen at build, so the size's PNG is resolved once per window build.
    private System.Func<byte[]?> Art(string piece)
    {
        var file = "celestial_crest_" + piece + (_hudLarge ? ".png" : "@1x.png");
        return () => CachedPng(file);
    }

    private static StackLayer At(HudElement content, LayerRect rect, System.Func<bool>? visible = null)
        => new(content) { Rect = rect, Visible = visible };

    private HudElement BuildTargetHudFrame()
    {
        var badgeRect = new LayerRect { Left = G(144), Top = G(11), Width = G(120), Height = G(40) };
        var breakRect = new LayerRect { Left = G(144), Right = G(332), Top = G(112), Height = G(16) };
        float portrait = G(88);
        return new LayerStackElement(
            new SpacerElement(Height: FrameH),
            At(new GameTextureElement(() => GetMonsterPortrait(), (int)portrait, (int)portrait, () => _portraitUv) { CornerRadius = (int)(portrait / 2f) },
                new LayerRect { Left = G(32), Top = G(32), Width = portrait, Height = portrait }),
            new StackLayer(new SlicedImageElement(Art("left"), Art("mid"), Art("right"), (int)FrameH)),
            // Rank badge: art + text (BOSS / ELITE). Normal monsters: no badge, and the name moves left.
            At(new ImageElement(Art("badge_gold"), (int)G(120), (int)G(40)) { Crisp = true }, badgeRect, TargetHudIsBoss),
            At(new ImageElement(Art("badge_orange"), (int)G(120), (int)G(40)) { Crisp = true }, badgeRect, TargetHudIsElite),
            At(new TextElement(() => TargetHudRankText(), Color: () => TargetHudIsBoss() ? BossBadgeText : EliteBadgeText,
                Align: TextAlign.Center, FontSize: Fs(11, 20)) { Bold = true, CenterCaps = true }, badgeRect, TargetHudHasRank),
            // Name (white bold) + distance share one row per rank variant, so the name gets every px up to the distance
            // text and is cut with "…" only when it really doesn't fit. The TMP text path has no shadow, so a dark copy
            // 1–2 px down-right (same row width → the same cut) stands in for it.
            NameRow(G(276), true, shadow: true), NameRow(G(276), true, shadow: false),
            NameRow(G(144), false, shadow: true), NameRow(G(144), false, shadow: false),
            At(BuildFrameHpBar(), new LayerRect { Left = G(144), Right = G(32), Top = G(60), Height = G(40) }),
            // Break gauge (any target with one, MaxStunned > 0): blue while draining, gold + glow while broken (refill tween).
            At(BuildFrameBreakBar(BreakGradient, glow: false), breakRect, () => TargetHudHasBreak() && !TargetHudBroken()),
            At(BuildFrameBreakBar(BrokenGradient, glow: true), breakRect, () => TargetHudHasBreak() && TargetHudBroken()),
            // Label right of the gauge on the right cap (gauge ends 332 px from the right edge; +16 gap).
            At(new TextElement(() => TargetHudBreakText(), Color: () => TargetHudBroken() ? BrokenLabelCol : BreakLabelCol, FontSize: Fs(11, 20))
                { Bold = true, CenterCaps = true }, new LayerRect { Right = G(56), Width = G(260), Top = G(104), Height = G(32) }, TargetHudHasBreak));
    }

    // One name row on the badge line (2×: y 11..51, centred at 31): name left-centred at x = left, filling the row
    // (Weight) up to a fixed-width right-aligned distance cell ending 58 px (2×) from the frame's right edge. The
    // distance cell's min = preferred width, so when the row is short the NAME gives way, and Ellipsize cuts it per
    // character only past the real row width. (A separate name layer ending at the distance AREA + MaxLines = 1 cut
    // whole words → "Enemy Training..." with ~390 px free, in game 2026-11-18.) Shadow rows: same geometry, spacer.
    private StackLayer NameRow(float left, bool withRank, bool shadow)
    {
        float d = shadow ? G(2) : 0f, distW = G(80);
        var name = new TextElement(() => TargetHudNameLine(), Color: shadow ? () => NameShadowCol : null, FontSize: Fs(15, 30), NoWrap: true)
            { Bold = true, Ellipsize = true };
        // Distance: small muted regular weight. RichEmoji routes it through the TMP path, the only one honouring FontSize
        // without bold (plain Menu Text is fixed 14 px); "35 m" has no sprite tags, so it renders as plain TMP text.
        HudElement dist = shadow ? new SpacerElement(Width: distW)
            : new CellElement(new TextElement(() => TargetHudDistText(), Color: () => DistColor, Align: TextAlign.Right, FontSize: Fs(12, 18))
                { RichEmoji = true }, Width: distW);
        var row = new RowElement(new HudElement[] { new CellElement(name, Weight: 1f), dist }, Gap: G(8));
        return At(row, new LayerRect { Left = left + d, Right = G(58) - d, Top = G(11) + d, Height = G(40) },
            withRank ? TargetHudHasRank : () => !TargetHudHasRank());
    }

    private HudElement BuildFrameHpBar() => new BarElement(() => TargetHudHpFraction(), Hex(0xffffff), () => TargetHudHpLabel())
    {
        Style = BarStyle.Modern, Height = G(40), FillWidth = true, LabelFontSize = Fs(12, 20),
        LabelAlign = TextAlign.Center, LabelOutline = true, LabelInset = G(16),
        SecondaryLabel = () => TargetHudHpPct(), SecondaryLabelColorFn = () => TargetHudHpFraction() < 0.2f ? HpLowColor : null,
        TrackColorFn = () => FrameClear,   // the art's interior is the track
        FillGradient = HpGradient, Gloss = true,
        Ticks = 10, TickColor = new ColorRgba(0f, 0f, 0f, 0.2f), TickWidth = 1f,   // barely-there 10 % ticks (concept), 1 px at both sizes
        Trail01 = () => TargetHudTrailFraction(), TrailColor = Hex(0xf6d0c8),
        // Shield: a striped cyan segment right after the HP fill, [hp, hp + shield] / max (same shield value as before).
        OverlayStart01 = () => TargetHudHpFraction(), Overlay01 = () => TargetHudShieldEndFraction(), OverlayInFront = true,
        OverlayColor = Hex(0x7ff0ff), OverlayStripeColor = Hex(0x3fc6e6), OverlayStripeWidth = G(8),
        OverlayGlow = Hex(0x7ff0ff, 0.8f), GlowRadius = G(12),
    };

    private HudElement BuildFrameBreakBar(GradientStop[] gradient, bool glow) => new BarElement(() => TargetHudBreakFraction(), Hex(0xffffff))
    {
        Style = BarStyle.Modern, Height = G(16), FillWidth = true,
        TrackColorFn = () => FrameClear, FillGradient = gradient,
        Ticks = 10, TickColor = new ColorRgba(10f / 255f, 16f / 255f, 30f / 255f, 0.9f), TickWidth = G(4),
        FillGlow = glow ? Hex(0xffb84a, 0.9f) : null, GlowRadius = G(12),
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
