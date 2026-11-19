using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.TargetLens;

// "Celestial Crest" v3 boss-bar frame for the Target HUD (user-approved 2026-11-18: direction A + the taller name strip;
// art + every slot rect: _scratch/prototypes/tl-bossbar-art-v3/README.md). Three sizes, picked in Settings → Target HUD →
// "Boss bar size" (config "boss_bar_size": 0 = Slim 1×, 1 = Medium 1.5× — the default, 2 = Large 2×):
//
//   size     frame H   total width   PNG suffix
//   Slim       88      266 + M       @1x
//   Medium    132      399 + M       @1.5x
//   Large     176      532 + M       (none)
//
// M is the stretched middle (follows the window width). Layer order (README "Assembly"):
//   LayerStack   base = a frame-height strut (the stack is as wide as the window content)
//     ├─ portrait disc  deep royal-blue radial backing (Resources/portrait-disc-<px>.png) — portraits with a transparent
//     │                 background would otherwise show the scene through the medallion
//     ├─ portrait       the monster's game portrait, circle — UNDER the frame (the art's socket is transparent)
//     ├─ frame art      framework 3-slice: left cap + middle (stretched) + right cap
//     ├─ badge art      gold = BOSS, orange = ELITE, + its text
//     ├─ name row       name left-centred in the name strip (moves left with no badge) + the distance, right-aligned in
//     │                 the right cap's distance slot; dark drop-shadow copy under it
//     ├─ HP bar         gradient + gloss + 10 % ticks + damage trail + striped shield
//     └─ break gauge    10 segments, label right of it in the right cap's break-label area (MaxStunned > 0)
//
// Every slot is a StackLayer.Rect (framework 2.34.0) measured from the frame's edges (right-cap slots as insets from the
// right edge), so a width resize moves anchors only. Art colours are fixed by design (they don't follow the theme).
// Getters are ≤10 Hz (window refresh) and allocation-free: display strings are cached and rebuilt only when the shown
// value (or the language) changes. Text: every label is TMP (SDF, sharp at any size) except the HP bar's two labels,
// which are uGUI dynamic-font Text rasterised at their real pixel size — never a bitmap scaled up.
public sealed partial class Plugin
{
    // One size's geometry, straight from the v3 README (px, frame-local). Right* = inset from the frame's RIGHT edge,
    // derived as (right cap width − local x − width) for right-cap slots and (caps − x − fixed width) for the gauges.
    private sealed record FrameGeo(
        string Suffix, float H, float CapsW, float MidMin, float DefaultW,
        float HpL, float HpT, float HpH, float HpR,
        float BrL, float BrT, float BrH, float BrR,
        float PortX, float PortY, float PortD,
        float BadgeX, float BadgeY, float BadgeW, float BadgeH,
        float NameX, float StripT, float StripH,
        float DistR, float DistW, float LblR, float LblW, float LblT, float LblH,
        int NameFs, int HpFs, int DistFs, int BadgeFs, int LblFs,
        float Unit);   // 1 / 1.5 / 2 — scales the small effects (shadow, gaps, glow, stripes, segment gaps)

    private static readonly FrameGeo[] FrameGeos =
    {
        // Slim 1×: HP (72,36,178+M,20) · break (72,62,28+M,8) · portrait (16,22,44) · badge (72,8,60,20) · name x 138,
        // centre y 18 · distance cap-local (76,8,70,20) · break label cap-local (15,58,135,12) · right cap 174.
        new("@1x", 88, 266, 60, 485, 72, 36, 20, 16, 72, 62, 8, 166, 16, 22, 44, 72, 8, 60, 20, 138, 8, 20,
            28, 40, 24, 135, 58, 12, 15, 12, 12, 11, 11, 1f),
        // Medium 1.5×: HP (108,54,267+M,30) · break (108,93,42+M,12) · portrait (24,33,66) · badge (108,12,90,30) ·
        // name x 207, centre y 27 · distance (114,12,105,30) · break label (22,87,203,18) · right cap 261.
        new("@1.5x", 132, 399, 90, 728, 108, 54, 30, 24, 108, 93, 12, 249, 24, 33, 66, 108, 12, 90, 30, 207, 12, 30,
            42, 60, 36, 203, 87, 18, 22, 16, 15, 15, 15, 1.5f),
        // Large 2×: HP (144,72,356+M,40) · break (144,124,56+M,16) · portrait (32,44,88) · badge (144,16,120,40) ·
        // name x 276, centre y 36 · distance (152,16,140,40) · break label (30,116,270,24) · right cap 348.
        new("", 176, 532, 120, 971, 144, 72, 40, 32, 144, 124, 16, 332, 32, 44, 88, 144, 16, 120, 40, 276, 16, 40,
            56, 80, 48, 270, 116, 24, 30, 20, 18, 20, 20, 2f),
    };

    private int _hudSize = 1;                          // 0 Slim / 1 Medium / 2 Large; baked into the window at build
    private FrameGeo Geo => FrameGeos[_hudSize];
    private float FrameH => Geo.H;
    private float FrameCapsW => Geo.CapsW;

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

    // Embedded art (csproj Resources/celestial_crest_*.png at 2× / @1.5x / @1x, portrait-disc-<px>.png), read once and
    // cached by CachedPng (Plugin.Style.Blocks.cs). The Func is chosen at build, so a size's PNG resolves once per build.
    private System.Func<byte[]?> Art(string piece)
    {
        var file = "celestial_crest_" + piece + Geo.Suffix + ".png";
        return () => CachedPng(file);
    }

    private static StackLayer At(HudElement content, LayerRect rect, System.Func<bool>? visible = null)
        => new(content) { Rect = rect, Visible = visible };

    private HudElement BuildTargetHudFrame()
    {
        var g = Geo;
        var badgeRect = new LayerRect { Left = g.BadgeX, Top = g.BadgeY, Width = g.BadgeW, Height = g.BadgeH };
        var breakRect = new LayerRect { Left = g.BrL, Right = g.BrR, Top = g.BrT, Height = g.BrH };
        var portRect = new LayerRect { Left = g.PortX, Top = g.PortY, Width = g.PortD, Height = g.PortD };
        int d = (int)g.PortD;
        var discFile = "portrait-disc-" + d + ".png";
        return new LayerStackElement(
            new SpacerElement(Height: g.H),
            At(new ImageElement(() => CachedPng(discFile), d, d) { Crisp = true }, portRect),
            At(new GameTextureElement(() => GetMonsterPortrait(), d, d, () => _portraitUv) { CornerRadius = d / 2 }, portRect),
            new StackLayer(new SlicedImageElement(Art("left"), Art("mid"), Art("right"), (int)g.H)),
            // Rank badge: art + text (BOSS / ELITE). Normal monsters: no badge, and the name moves left.
            At(new ImageElement(Art("badge_gold"), (int)g.BadgeW, (int)g.BadgeH) { Crisp = true }, badgeRect, TargetHudIsBoss),
            At(new ImageElement(Art("badge_orange"), (int)g.BadgeW, (int)g.BadgeH) { Crisp = true }, badgeRect, TargetHudIsElite),
            At(new TextElement(() => TargetHudRankText(), Color: () => TargetHudIsBoss() ? BossBadgeText : EliteBadgeText,
                Align: TextAlign.Center, FontSize: g.BadgeFs) { Bold = true, CenterCaps = true }, badgeRect, TargetHudHasRank),
            // Name (white bold) + distance share one row per rank variant, so the name gets every px up to the distance
            // text and is cut with "…" only when it really doesn't fit. The TMP text path has no shadow, so a dark copy
            // 1–2 px down-right (same row width → the same cut) stands in for it.
            NameRow(g.NameX, true, shadow: true), NameRow(g.NameX, true, shadow: false),
            NameRow(g.BadgeX, false, shadow: true), NameRow(g.BadgeX, false, shadow: false),
            At(BuildFrameHpBar(), new LayerRect { Left = g.HpL, Right = g.HpR, Top = g.HpT, Height = g.HpH }),
            // Break gauge (any target with one, MaxStunned > 0): blue while draining, gold + glow while broken (refill tween).
            At(BuildFrameBreakBar(BreakGradient, glow: false), breakRect, () => TargetHudHasBreak() && !TargetHudBroken()),
            At(BuildFrameBreakBar(BrokenGradient, glow: true), breakRect, () => TargetHudHasBreak() && TargetHudBroken()),
            // Label in the right cap's break-label area, right of the gauge.
            At(new TextElement(() => TargetHudBreakText(), Color: () => TargetHudBroken() ? BrokenLabelCol : BreakLabelCol, FontSize: g.LblFs)
                { Bold = true, CenterCaps = true }, new LayerRect { Right = g.LblR, Width = g.LblW, Top = g.LblT, Height = g.LblH }, TargetHudHasBreak));
    }

    // One name row = the whole name strip band (badge-height, centred on the strip's centreline): the name left-centred
    // at x = left, filling the row (Weight) up to a fixed-width right-aligned distance cell that ends at the right edge
    // of the right cap's distance slot. The distance cell's min = preferred width, so when the row is short the NAME
    // gives way, and Ellipsize cuts it per character only past the real row width. (A separate name layer ending at the
    // distance AREA + MaxLines = 1 cut whole words → "Enemy Training..." with ~390 px free, in game 2026-11-18.)
    // Shadow rows: same geometry with a spacer for the distance.
    private StackLayer NameRow(float left, bool withRank, bool shadow)
    {
        var g = Geo;
        float d = shadow ? System.MathF.Max(1f, System.MathF.Round(g.Unit)) : 0f;
        var name = new TextElement(() => TargetHudNameLine(), Color: shadow ? () => NameShadowCol : null, FontSize: g.NameFs, NoWrap: true)
            { Bold = true, Ellipsize = true, CenterCaps = true };
        // Distance: small muted regular weight. RichEmoji routes it through the TMP path, the only one honouring FontSize
        // without bold (plain Menu Text is fixed 14 px); "35 m" has no sprite tags, so it renders as plain TMP text.
        HudElement dist = shadow ? new SpacerElement(Width: g.DistW)
            : new CellElement(new TextElement(() => TargetHudDistText(), Color: () => DistColor, Align: TextAlign.Right, FontSize: g.DistFs)
                { RichEmoji = true, CenterCaps = true }, Width: g.DistW);
        var row = new RowElement(new HudElement[] { new CellElement(name, Weight: 1f), dist }, Gap: 4f * g.Unit);
        return At(row, new LayerRect { Left = left + d, Right = g.DistR - d, Top = g.StripT + d, Height = g.StripH },
            withRank ? TargetHudHasRank : () => !TargetHudHasRank());
    }

    private HudElement BuildFrameHpBar() => new BarElement(() => TargetHudHpFraction(), Hex(0xffffff), () => TargetHudHpLabel())
    {
        Style = BarStyle.Modern, Height = Geo.HpH, FillWidth = true, LabelFontSize = Geo.HpFs,
        LabelAlign = TextAlign.Center, LabelOutline = true, LabelInset = 8f * Geo.Unit,
        SecondaryLabel = () => TargetHudHpPct(), SecondaryLabelColorFn = () => TargetHudHpFraction() < 0.2f ? HpLowColor : null,
        TrackColorFn = () => FrameClear,   // the art's interior is the track
        FillGradient = HpGradient, Gloss = true,
        Ticks = 10, TickColor = new ColorRgba(0f, 0f, 0f, 0.2f), TickWidth = 1f,   // barely-there 10 % ticks (concept), 1 px at every size
        Trail01 = () => TargetHudTrailFraction(), TrailColor = Hex(0xf6d0c8),
        // Shield: a striped cyan segment right after the HP fill, [hp, hp + shield] / max (same shield value as before).
        OverlayStart01 = () => TargetHudHpFraction(), Overlay01 = () => TargetHudShieldEndFraction(), OverlayInFront = true,
        OverlayColor = Hex(0x7ff0ff), OverlayStripeColor = Hex(0x3fc6e6), OverlayStripeWidth = 4f * Geo.Unit,
        OverlayGlow = Hex(0x7ff0ff, 0.8f), GlowRadius = 6f * Geo.Unit,
    };

    private HudElement BuildFrameBreakBar(GradientStop[] gradient, bool glow) => new BarElement(() => TargetHudBreakFraction(), Hex(0xffffff))
    {
        Style = BarStyle.Modern, Height = Geo.BrH, FillWidth = true,
        TrackColorFn = () => FrameClear, FillGradient = gradient,
        Ticks = 10, TickColor = new ColorRgba(10f / 255f, 16f / 255f, 30f / 255f, 0.9f), TickWidth = 2f * Geo.Unit,
        FillGlow = glow ? Hex(0xffb84a, 0.9f) : null, GlowRadius = 6f * Geo.Unit,
    };

    // ── Damage trail (the usual game style): the first hit after the trail has caught up starts ONE ~0.6 s hold; after
    //    it the trail chases the CURRENT HP — closing ~35 % of the gap per 100 ms (exponential, time-based so the refresh
    //    rate doesn't matter) with a floor speed — and hits during the hold or the chase never restart the hold. Under
    //    sustained damage it stays a little behind the fill; once hits stop it catches up in ~0.4 s. A new hold starts
    //    only after a full catch-up (gap < 0.2 %). A target change or a heal resets it. Pure arithmetic on TickCount64,
    //    no allocation. (First build re-armed the hold on every drop → under continuous hits the trail never moved
    //    until the player stopped attacking; in game 2026-11-18.)
    private const long TrailHoldMs = 600;
    private const float TrailKeepPer100Ms = 0.65f;   // gap left after each 100 ms of chasing
    private const float TrailMinSpeed = 0.15f;       // floor: share of the bar per second (finishes small gaps)
    private const float TrailCaughtUp = 0.002f;      // < 0.2 % = caught up
    private long _trailUuid = long.MinValue, _trailHoldUntil, _trailLastTick;
    private float _trailShown;
    private bool _trailActive;                       // a hold or chase is running (between catch-ups)

    private float TargetHudTrailFraction()
    {
        var s = Cur;
        float hp = TargetHudHpFraction();
        long now = System.Environment.TickCount64;
        float dt = System.Math.Clamp((now - _trailLastTick) / 1000f, 0f, 0.25f);
        _trailLastTick = now;
        if (s.Uuid != _trailUuid || hp >= _trailShown - TrailCaughtUp)   // new target, heal, or caught up → no trail
        {
            _trailUuid = s.Uuid; _trailShown = hp; _trailActive = false;
            return hp;
        }
        if (!_trailActive) { _trailActive = true; _trailHoldUntil = now + TrailHoldMs; }   // first hit after a catch-up
        if (now < _trailHoldUntil) return _trailShown;
        float gap = _trailShown - hp;
        float step = System.MathF.Max(gap * (1f - System.MathF.Pow(TrailKeepPer100Ms, dt * 10f)), TrailMinSpeed * dt);
        _trailShown = System.MathF.Max(hp, _trailShown - step);
        if (_trailShown - hp < TrailCaughtUp) { _trailShown = hp; _trailActive = false; }
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
