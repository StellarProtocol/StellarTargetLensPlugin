using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.TargetLens;

// "Celestial Crest family" kit for the five TargetLens overlays (Threat, Cast bar, Boss skills, Target effects List,
// Nearby) — user-approved 2026-11-18 (_scratch/prototypes/targetlens-overlays-APPROVED.md + targetlens-overlays-crest.html).
// Fixed colours, the boss bar's family; they do NOT follow the theme.
//
//   Plate   = the window's own rounded backdrop (WindowSpec.BackgroundColor, radius 6, SOLID #111a36 — the framework has
//             no fill gradient) + a 1 px gold ring. The ring is a transparent BubbleElement in a LayerStack overlay placed
//             12 px OUTSIDE the content (StackLayer.Rect with negative insets = the Borderless chrome's padding), so ring
//             and fill both follow the window rect at every size. Both fade with the "Overlay background" slider
//             (0 % = floating, as before the redesign; rows keep their tracks).
//   Caption = gold diamond + gold UPPERCASE bold caption + a live hint right-aligned, then a gold hairline (3-piece
//             stretch strip) with 3 px above / 6 px below. Captions keep their fixed size (only rows scale).
//   Bars    = Modern BarElement, rounded (CornerRadius, fw 2.34), vertical gradient (light → mid at 55 % → dark) + gloss,
//             dark track, bold outlined white labels, plus a faint gold edge ring (a ring bubble layered over the bar).
//   Icons   = game texture with rounded corners + a 1.5 px ring bubble over it.
//
// Glyph PNGs (diamond, hairline, medallions, pills) are pre-drawn per row-size step by tools/gen_crest_icons.py; the
// overlays' 1.0–2.5 size sliders snap to the nearest CrestSteps entry. All Funcs below are allocation-free.
public sealed partial class Plugin
{
    private static ColorRgba CrestHex(int rgb, float a = 1f)
        => new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, a);

    private static readonly ColorRgba CrestTrack    = CrestHex(0x060914);
    private static readonly ColorRgba CrestEdge     = new(216f / 255f, 180f / 255f, 106f / 255f, 0.22f);
    private static readonly ColorRgba CrestGoldHi   = CrestHex(0xf3dca0);
    private static readonly ColorRgba CrestSoft     = CrestHex(0xb9c2da);
    private static readonly ColorRgba CrestDanger   = CrestHex(0xff5a4a);
    private static readonly ColorRgba CrestDebuffRing = CrestHex(0xff8a55);
    private static readonly ColorRgba CrestBuffRing   = CrestHex(0x6fdc84);
    private static readonly ColorRgba CrestRingNeutral = new(216f / 255f, 180f / 255f, 106f / 255f, 0.62f);
    private static readonly ColorRgba CrestClear    = new(0f, 0f, 0f, 0f);

    private static GradientStop[] CrestGrad(int light, int mid, int dark)
        => new[] { new GradientStop(0f, CrestHex(light)), new GradientStop(0.55f, CrestHex(mid)), new GradientStop(1f, CrestHex(dark)) };

    private static readonly GradientStop[] GradHp     = CrestGrad(0xff8a7a, 0xe5414a, 0xa8172b);
    private static readonly GradientStop[] GradAmber  = CrestGrad(0xffd08a, 0xf0962e, 0xa85a12);
    private static readonly GradientStop[] GradDebuff = CrestGrad(0xffa27a, 0xe0582e, 0x9c3216);
    private static readonly GradientStop[] GradBuff   = CrestGrad(0x9eecaa, 0x3fae5c, 0x1f6e36);
    private static readonly GradientStop[] GradCast   = CrestGrad(0xffe8a0, 0xf2b632, 0xa8700e);
    private static readonly GradientStop[] GradBlue   = CrestGrad(0xa6dcff, 0x2f86e6, 0x164f9c);
    private static readonly GradientStop[] GradElite  = CrestGrad(0xffc27a, 0xf28c26, 0xa8520e);
    private static readonly GradientStop[] GradGreen  = CrestGrad(0xa8eaa0, 0x5cbf5a, 0x2a7a2e);

    // ── Overlay background (Settings → Target HUD; config "overlay_bg", 0..1, default 1 = opaque plate) ──
    private float _overlayBg = 1f;
    private long  _overlayBgDirtyAtMs = -1;

    private void LoadOverlayBg() => _overlayBg = Math.Clamp(_cfg.Get<float>("overlay_bg", 1f), 0f, 1f);

    // Live while dragging (the plates repaint at the next refresh); persisted once the drag settles (no Save per frame).
    private void SetOverlayBg(float v)
    {
        _overlayBg = Math.Clamp(v, 0f, 1f);
        _overlayBgDirtyAtMs = Environment.TickCount64;
    }

    private void TickOverlayBgSave()
    {
        if (_overlayBgDirtyAtMs < 0 || (_overlayBgDirtyAtMs > 0 && Environment.TickCount64 - _overlayBgDirtyAtMs < ListScaleSettleMs)) return;
        _overlayBgDirtyAtMs = -1;
        _cfg.Set<float>("overlay_bg", _overlayBg);
        _cfg.Save();
    }

    // Window backdrop = the plate fill (WindowSpec.BackgroundColor; alpha 0 = not drawn).
    private ColorRgba CrestPlateFill() => new(0x11 / 255f, 0x1a / 255f, 0x36 / 255f, _overlayBg);
    private const int CrestPlateRadius = 6;
    private const float ChromePad = 12f;   // Borderless chrome content padding (WindowBuilder.BuildBorderless)

    // The window content wrapped with the plate ring (1 px gold), or — for the cast bar — gold / 2 px red by `danger`.
    private HudElement CrestFrame(HudElement content, Func<bool>? danger = null)
    {
        var gold = PlateRing(() => CrestRingNeutral with { A = 0.62f * _overlayBg }, 1f, danger == null ? null : () => !danger());
        if (danger == null) return new LayerStackElement(content, gold);
        var red = PlateRing(() => CrestDanger with { A = _overlayBg }, 2f, danger);
        return new LayerStackElement(content, gold, red);
    }

    private static StackLayer PlateRing(Func<ColorRgba?> color, float width, Func<bool>? visible) =>
        new(RingBubble(color, width, CrestPlateRadius))
        {
            Rect = new LayerRect { Left = -ChromePad, Right = -ChromePad, Top = -ChromePad, Bottom = -ChromePad },
            Visible = visible,
        };

    // A transparent rounded bubble used only for its ring (stretched over whatever layer it sits in).
    private static BubbleElement RingBubble(Func<ColorRgba?> color, float width, float radius) =>
        new(new SpacerElement()) { FillColor = CrestClear, CornerRadius = radius, PaddingX = 0f, PaddingY = 0f, OutlineColorFn = color, OutlineWidth = width };

    // ── Caption ──
    private static readonly Func<byte[]?> DiamondPng = () => CachedPng("crest-diamond-7.png");
    private static readonly Func<byte[]?> HairL = () => CachedPng("crest-hair-l.png"), HairM = () => CachedPng("crest-hair-m.png"),
        HairR = () => CachedPng("crest-hair-r.png");
    private const float CaptionReserve = 20f + 3f + 1f + 6f;   // caption row + hairline block, px (not scaled)

    // Caption (UPPERCASED from the localized key, re-cased only on a language change) + live hint + hairline; the
    // whole block collapses with the overlay's "Show title" toggle.
    private HudElement CrestCaption(string captionKey, Func<string> hint, Func<bool> show)
    {
        string? src = null, upper = string.Empty;
        string Caption()
        {
            var c = _loc.T(captionKey);
            if (!ReferenceEquals(c, src)) { src = c; upper = c.ToUpperInvariant(); }
            return upper;
        }
        var row = new RowElement(new HudElement[]
        {
            new SpacerElement(Height: 20f),
            new ImageElement(DiamondPng, 7, 7) { Crisp = true },
            new TextElement(Caption, Color: () => CrestGoldHi, FontSize: 12, NoWrap: true) { Bold = true, CenterCaps = true },
            // Hint: soft, regular weight — RichEmoji = the TMP path that honours FontSize without bold.
            new CellElement(new TextElement(hint, Color: () => CrestSoft, Align: TextAlign.Right, FontSize: 11, NoWrap: true)
                { RichEmoji = true, CenterCaps = true }, Weight: 1f),
        }, Gap: 6f);
        return new ConditionalElement(show, new ColumnElement(new HudElement[]
        {
            row,
            new SpacerElement(Height: 3f),
            new SlicedImageElement(HairL, HairM, HairR, 1),
            new SpacerElement(Height: 6f),
        }, Gap: 0.01f));
    }

    // ── Bars ──
    // A Crest bar: rounded gradient + gloss fill on the dark track, bold outlined labels (name left, value right), the
    // faint gold edge ring, and optionally a highlight ring (your threat row / your current target) shown by `hiOn`.
    private BarElement CrestBar(Func<float> frac, GradientStop[] grad, Func<string>? left, Func<string>? right, float h, int font,
        float sc, Func<ColorRgba?>? tint = null) =>
        new BarElement(frac, new ColorRgba(1f, 1f, 1f, 1f), left)
        {
            Style = BarStyle.Modern, Height = h, FillWidth = true, LabelFontSize = font, LabelOutline = true, LabelInset = 8f * sc,
            SecondaryLabel = right, CornerRadius = 3f * sc, FillGradient = grad, Gloss = true, FillColorFn = tint,
            TrackColorFn = () => CrestTrack,
        };

    // Edge ring (+ optional highlight ring) over a bar (or a stack of bar variants) of height h.
    private static HudElement CrestBarRings(HudElement bars, float sc, Func<bool>? hiOn = null, ColorRgba? hiColor = null)
    {
        var edge = new StackLayer(RingBubble(() => CrestEdge, 1f, 3f * sc));
        if (hiOn == null) return new LayerStackElement(bars, edge);
        var c = hiColor ?? CrestGoldHi;
        return new LayerStackElement(bars, edge, new StackLayer(RingBubble(() => c, 1.5f, 3f * sc)) { Visible = hiOn });
    }

    // Several bar variants in one slot (a gradient is fixed at build, so a row whose colour changes shows one of them):
    // base = a height strut, each variant a layer shown by its predicate (a show/hide never re-lays-out the row).
    private static HudElement BarVariants(float h, params (HudElement Bar, Func<bool> When)[] variants)
    {
        var layers = new StackLayer[variants.Length];
        for (var i = 0; i < variants.Length; i++) layers[i] = new StackLayer(variants[i].Bar) { Visible = variants[i].When };
        return new LayerStackElement(new SpacerElement(Height: h), layers);
    }

    // ── Icons: rounded game texture + a 1.5 px ring (colour per refresh) ──
    private static HudElement CrestIcon(Func<object?> tex, Func<UvRect> uv, int px, Func<ColorRgba?> ring, float ringW = 1.5f)
        => new LayerStackElement(new GameTextureElement(tex, px, px, uv) { CornerRadius = (int)MathF.Round(px / 5f) },
            new StackLayer(RingBubble(ring, ringW, MathF.Round(px / 5f))));

    // ── Row-size steps for the pre-drawn glyphs (must match tools/gen_crest_icons.py STEPS) ──
    private static readonly float[] CrestSteps = { 1.0f, 1.1f, 1.2f, 1.35f, 1.5f, 1.7f, 2.0f, 2.25f, 2.5f };

    private static int CrestStep(float sc)
    {
        int best = 0;
        for (int i = 1; i < CrestSteps.Length; i++)
            if (MathF.Abs(CrestSteps[i] - sc) < MathF.Abs(CrestSteps[best] - sc)) best = i;
        return best;
    }

    private static int MedalPx(float sc) => (int)MathF.Round(18f * CrestSteps[CrestStep(sc)]);

    // A count hint ("4 players"): rebuilt only when the count or the language template changes.
    private sealed class CountHint
    {
        private int _n = int.MinValue; private string? _tpl; private string _s = "";
        public string Get(string tpl, int n)
        {
            if (n == _n && ReferenceEquals(tpl, _tpl)) return _s;
            _n = n; _tpl = tpl; _s = string.Format(tpl, n);
            return _s;
        }
    }
}
