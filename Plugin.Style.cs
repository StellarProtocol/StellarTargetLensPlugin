using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.TargetLens;

// House style (Stellar Design Language, Knowledge Base\Stellar-Design-Language.md) — the palette, colour maths and the
// pill / panel kit the Settings window and its help popover build from. Copied from the StatInspector reference
// (Plugin.Style.cs, itself the Maestro kit + Dungeon Hub's uniform window-opacity fade) and trimmed to what Target Lens
// uses, plus the rule-22 RECESSED disabled look ported from StellarPositionPlugin (the framework's StellarStyle
// DisabledFill / DisabledLabel are framework-internal). Needs SDK >= 2.30.0 (theme status colours) — we're on 2.30.0.
//
// ⚠️ Plugin.cs already has a `MutedColor` PROPERTY (Func<ColorRgba?>, used by the HUD overlays' title rows), so the
// house kit's muted text colour is named MutedText() here.
//
// Toolkit limits behind some choices (WindowBuilder-Patterns.md):
//   • Plain TextElement ignores FontSize on Menu windows (fixed 14px) — only the styled path (Bold/Emphasis) honours it,
//     so small captions are Bold+FontSize while hints stay at the fixed plain size.
//   • Row/Column Gap 0 means the DEFAULT gap (8/12), never zero — every container here passes an explicit Gap.
//   • Bold can't be toggled live → a bold/regular variant is two complete elements swapped by a ConditionalElement.
public sealed partial class Plugin
{
    // ── palette: LIVE from the current framework theme ────────────────────────────────────────────────────────────
    // Derived from _services.Theme.Colors on every call and handed to elements as Funcs, so a theme switch repaints on
    // the next refresh.
    private static readonly ColorRgba PanelShade = new(0f, 0f, 0f, 0.20f);   // panel = background darkened
    private const float TileTextMix      = 0.08f;   // raised tile = panel + 8% MenuText …
    private const float TileAccentMix    = 0.06f;   // … + a 6% accent tint (teal on teal, pink on pink)
    private const float NeutralMix       = 0.22f;   // neutral pill = panel + 22% MenuMuted …
    private const float NeutralAccentMix = 0.08f;   // … + an 8% accent tint, like the tiles
    private const float PillMinContrast  = 4.5f;    // tinted fills (Neutral) keep 4.5:1 labels
    private const float SolidMinContrast = 3.0f;    // accent text on its own wash / white on a solid accent tile: 3:1
    private const float AccentWashAlpha  = 0.22f;   // "on" chip = 22% accent wash
    private const float HoverMix         = 0.10f;   // hover = 10% MenuText over the fill

    private static readonly ColorRgba Transparent = new(0f, 0f, 0f, 0f);
    private static readonly ColorRgba White       = new(1f, 1f, 1f, 1f);

    private IThemeColors Menu => _services.Theme.Colors;

    private ColorRgba TextColor()   => Menu.MenuText;
    private ColorRgba MutedText()   => Menu.MenuMuted;
    private ColorRgba AccentColor() => Menu.MenuAccent;
    private ColorRgba PanelColor()  => Over(PanelShade, Menu.MenuBackground);
    private ColorRgba TileColor()   => Over(Menu.MenuAccent with { A = TileAccentMix },
                                            Over(Menu.MenuText with { A = TileTextMix }, PanelColor()));
    private ColorRgba NeutralFill() => Over(Menu.MenuAccent with { A = NeutralAccentMix },
                                            Over(Menu.MenuMuted with { A = NeutralMix }, PanelColor()));
    private ColorRgba NeutralText() => EnsureContrast(TextColor(), NeutralFill(), PillMinContrast);
    private ColorRgba HoverFill(ColorRgba fill) => Over(Menu.MenuText with { A = HoverMix }, fill);

    // ── window opacity: EVERY coloured fill follows the window opacity, uniformly ──────────────────────────────────
    // The framework fades ONLY the GlassMenu frame Image (Themes → Window Opacity, IChromeStyle.WindowOpacity); every
    // bubble fill is an opaque pre-blended colour, so at a low opacity the panels, chips and pills stayed solid on a
    // see-through frame. So every fill goes through Fade with the frame's factor; only text and pictures stay solid
    // (house rule 2026-11-07, Dungeon Hub). Each colour is composed OPAQUE first and only then gets its alpha — never
    // composite over a transparent background. IChromeStyle isn't on IPluginServices; the shared INamedTheme instance is
    // the framework theme service, which implements it (ChatTools Plugin.WindowOpacity.cs). No cast → 1 (opaque).
    // Polled per refresh: a cast + a property read, no allocation.
    private float WindowOpacity()
    {
        try { return Math.Clamp((_services.NamedTheme as IChromeStyle)?.WindowOpacity ?? 1f, 0.3f, 1f); }
        catch { return 1f; }
    }

    private ColorRgba Fade(ColorRgba c) => c with { A = c.A * WindowOpacity() };

    // ── rule 22: pressable ≠ disabled ─────────────────────────────────────────────────────────────────────────────
    // Disabled = RECESSED: black @28 % over the surface it sits on (darker than the surface, where a pressable fill is
    // lighter), same size and radius, faint label (Text @32 %, raised in 0.04 steps until 2:1). No outline ring (user
    // rejected it, 2026-11-09). Port of the framework kit's DisabledFill / DisabledLabel (via StellarPositionPlugin).
    // The label alpha is a short loop of struct maths — no allocation; it only runs while a row is recessed.
    private static readonly ColorRgba DisabledShade = new(0f, 0f, 0f, 0.28f);

    private static ColorRgba DisabledFill(ColorRgba surface) => Over(DisabledShade, surface);

    private ColorRgba DisabledLabel(ColorRgba surface)
    {
        var bg = DisabledFill(surface);
        float lb = Luminance(bg), a = 0.32f;
        while (a < 1f && ContrastRatio(Luminance(Over(TextColor() with { A = a }, bg)), lb) < 2f) a = MathF.Min(1f, a + 0.04f);
        return TextColor() with { A = a };
    }

    // Nudges fg toward white (dark bg) or black (light bg) in 15% steps until it reaches minRatio against the opaque
    // bg (ChatTools Plugin.Tabs.cs → MultiTool / Maestro Plugin.Style.cs). Pure struct math — no allocation per refresh.
    private static ColorRgba EnsureContrast(ColorRgba fg, ColorRgba bg, float minRatio)
    {
        float lb = Luminance(bg);
        var target = lb < 0.179f ? new ColorRgba(1f, 1f, 1f) : new ColorRgba(0f, 0f, 0f);
        for (int i = 0; i < 12 && ContrastRatio(Luminance(fg), lb) < minRatio; i++) fg = Mix(fg, target, 0.15f);
        return fg with { A = 1f };
    }

    private static float ContrastRatio(float la, float lb)
        => (MathF.Max(la, lb) + 0.05f) / (MathF.Min(la, lb) + 0.05f);
    private static float Luminance(ColorRgba c) => 0.2126f * Lin(c.R) + 0.7152f * Lin(c.G) + 0.0722f * Lin(c.B);
    private static float Lin(float v) => v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);

    // a → b by t (0 = a, 1 = b), opaque.
    private static ColorRgba Mix(ColorRgba a, ColorRgba b, float t)
        => new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t, 1f);

    // Alpha-composite fg (its A = coverage) over an opaque bg.
    private static ColorRgba Over(ColorRgba fg, ColorRgba bg) => Mix(bg, fg with { A = 1f }, fg.A);

    // ── shape ─────────────────────────────────────────────────────────────────────────────────────────────────────
    // The corner radius for a ROUND (capsule / circle) bubble of side `side`: side / 2 − 1, NOT side / 2. The bubble's
    // 9-slice background reports a MIN size of its summed sprite borders (radius + 1 each side), so radius side/2 drew
    // every round control 2 px taller than its content (Maestro, measured in-game 2026-11-06).
    private static float RoundRadius(float side) => side / 2f - 1f;

    private const float PillHeight = 28f;
    private const float PillPadX   = 12f;
    private const float TileRadius = 6f;    // panels, tiles

    // Hover that follows the shape: a Bare selectable (no square wash of its own) flips this flag and the surrounding
    // round Bubble's FillColorFn shows the hover.
    private sealed class HoverFlag { public bool On; }

    // Bold (TMP) labels: centre the CAP-HEIGHT band — a centred bold-13 line sits ~1.5–2 px high (Maestro, measured on
    // "Rename"), so the label sits in a Column under a 2 px spacer, which moves the text down by 1 px.
    private const float BoldLabelDrop = 1f;

    private static HudElement DropBold(HudElement label) => new ColumnElement(new HudElement[]
    {
        new SpacerElement(0f, 2f * BoldLabelDrop),
        label,
    }, Gap: 0.01f);

    // ── pill buttons ──────────────────────────────────────────────────────────────────────────────────────────────
    // ONE build for every labelled button: a fill-tinted Bubble around a Bare SelectableElement, 28 px tall (the 28 px
    // struts either side of a bold-13 label). The label box is FIXED (`width` = the longest label in any locale, house
    // rule 10) so a language switch never resizes the pill.
    private HudElement PillButton(Func<string> label, Func<ColorRgba> fill, Func<ColorRgba> text, Action onClick, float width)
    {
        var hover = new HoverFlag();
        return new BubbleElement(new SelectableElement(
            new RowElement(new HudElement[]
            {
                new SpacerElement(PillPadX, PillHeight),
                DropBold(new TextElement(label, Color: () => text(),
                    Width: width, Align: TextAlign.Center, NoWrap: true, FontSize: 13) { Bold = true, Ellipsize = true }),
                new SpacerElement(PillPadX, PillHeight),
            }, Gap: 0.01f, Justify: RowJustify.Center),
            OnClick: onClick)
            { Bare = true, OnHover = over => hover.On = over })
        {
            FillColorFn = () => Fade(hover.On ? HoverFill(fill()) : fill()),
            CornerRadius = RoundRadius(PillHeight), PaddingX = 0f, PaddingY = 0f,
        };
    }

    private HudElement NeutralButton(Func<string> label, Action onClick, float width) =>
        PillButton(label, NeutralFill, NeutralText, onClick, width);

    // ── section panel ─────────────────────────────────────────────────────────────────────────────────────────────
    // A rounded panel (background darkened, live): bold 12 UPPERCASE caption (Text colour, as in the approved prototype)
    // + right-aligned muted hint + an optional trailing control (the section's On/Off chip), then the children. Directly
    // in a Column it FILLS the width (a Bubble only hugs inside a Row).
    // ⚠️ ONE method with an explicit HudElement[] — deliberately NOT a `params` overload pair: with two params overloads
    // C# binds the first child as `trailing` and draws it inside the caption row (KB Stellar-Design-Language §4).
    private HudElement SectionPanel(Func<string> caption, Func<string> hint, HudElement? trailing, HudElement[] children)
    {
        var items = new HudElement[children.Length + 1];
        // The UPPERCASE caption is re-made only when the localized caption changes (a language switch), not per refresh.
        string? src = null, upper = string.Empty;
        string Caption()
        {
            var c = caption();
            if (!string.Equals(c, src, StringComparison.Ordinal)) { src = c; upper = c.ToUpperInvariant(); }
            return upper;
        }
        var head = new HudElement[trailing != null ? 4 : 3];
        head[0] = new TextElement(Caption, Color: () => TextColor(), NoWrap: true, FontSize: 12) { Bold = true };
        head[1] = new SpacerElement(0f);   // flexible: pushes the hint (and the chip) to the right edge
        head[2] = new TextElement(hint, Color: () => MutedText(), NoWrap: true);
        if (trailing != null) head[3] = trailing;
        items[0] = new RowElement(head, Gap: 6f);
        Array.Copy(children, 0, items, 1, children.Length);
        return new BubbleElement(new ColumnElement(items, Gap: 6f))
        {
            FillColorFn = () => Fade(PanelColor()), CornerRadius = TileRadius, PaddingX = 10f, PaddingY = 8f,
        };
    }
}
