using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.TargetLens;

/// <summary>
/// Opening the Settings window and the effect picker, house style (Stellar Design Language §5 "Open = OpenCentred"):
/// show → <c>BringToFront()</c> → centre for a short burst of ticks. Settings opens from the desktop launcher (outside the
/// plugin) → centred on the SCREEN; "Select effects…" opens the picker centred on the Settings window it came from.
/// Always fronted: the picker may already be visible behind Settings when its button is pressed (a visible window
/// doesn't remount, so it wouldn't come up on its own — CLAUDE.md Rule 5 exception), and the help popover is fronted on
/// every open, so every open path fronts too.
/// The burst is the Dungeon Confirm / StatInspector recipe: a SetRect right after SetVisible(true) is ignored on the first
/// open (no token yet) and the mount then restores the saved rect, so re-centre every tick for <see cref="CentreTicks"/>
/// ticks — but stop for good the moment the window is no longer where we left it (the user dragged it), or the burst
/// snaps the first drag back.
/// </summary>
public sealed partial class Plugin
{
    private const int CentreTicks = 30;   // plugin ticks (≥ 1 s at 30 Hz) — covers mount + deferred apply + layout settle
    // Settings is auto-fit (DefaultRect height 0): ≈ title 32 + padding 24 + header 42 + gap 10 + right column ≈ 496.
    // Only used for the centre maths until the window reports its real height.
    private const float SettingsApproxHeight = 600f;

    // One centring burst for one window. `anchor` = the window to centre on (null / hidden → the screen).
    private sealed class CentreBurst
    {
        private readonly IWindowControl _win;
        private readonly Func<IWindowControl?> _anchor;
        private readonly IFramework _fw;
        private readonly float _fallbackH;   // until an auto-fit window reports its laid-out height (0 = wait for it)
        private int _left;
        private (float X, float Y)? _placed;   // where we last left the MOUNTED window (null = not placed yet)

        public CentreBurst(IWindowControl win, Func<IWindowControl?> anchor, IFramework fw, float fallbackH)
        { _win = win; _anchor = anchor; _fw = fw; _fallbackH = fallbackH; }

        public void Start() { _placed = null; _left = CentreTicks; Step(); }
        public void Stop() => _left = 0;

        public void Tick()
        {
            if (_left <= 0) return;   // the common case: one compare per frame
            _left--;
            if (_win.IsShown && _placed is { } p)
            {
                var cur = _win.Rect;
                if (Math.Abs(cur.X - p.X) >= 0.5f || Math.Abs(cur.Y - p.Y) >= 0.5f) { _left = 0; return; }   // user's now
            }
            Step();
        }

        // Centre on the anchor's rect (or the screen), canvas units, clamped on-screen and floored to whole units.
        // SetRect only when off target; read the live pose back (SetRect clamps), so a drag is detected reliably.
        private void Step()
        {
            if (!_win.IsShown) return;   // not mounted yet: Rect is the DefaultRect, SetRect would be ignored
            float cw = _fw.CanvasWidth, ch = _fw.CanvasHeight;
            if (cw <= 1f || ch <= 1f) return;
            var r = _win.Rect;
            float h = r.Height > 1f ? r.Height : _fallbackH;
            if (r.Width <= 1f || h <= 1f) return;   // not laid out yet — try again next tick
            float cx = cw / 2f, cy = ch / 2f;
            var a = _anchor();
            if (a != null && a.IsShown) { var ar = a.Rect; cx = ar.X + ar.Width / 2f; cy = ar.Y + ar.Height / 2f; }
            float x = MathF.Floor(MathF.Max(0f, MathF.Min(cx - r.Width / 2f, cw - r.Width)));
            float y = MathF.Floor(MathF.Max(0f, MathF.Min(cy - h / 2f, ch - h)));
            if (Math.Abs(r.X - x) >= 0.5f || Math.Abs(r.Y - y) >= 0.5f) _win.SetRect(new WindowRect(x, y, r.Width, r.Height));   // keep the declared (auto-fit) height
            var after = _win.Rect;
            _placed = (after.X, after.Y);
        }
    }

    private CentreBurst? _settingsCentre;
    private CentreBurst? _selectCentre;

    // Launcher tile → Settings, centred on the screen.
    private void OpenSettings()
    {
        _settingsWindow.SetVisible(true);
        _settingsWindow.BringToFront();
        (_settingsCentre ??= new CentreBurst(_settingsWindow, () => null, _services.Framework, SettingsApproxHeight)).Start();
    }

    // The title-bar ✕: also closes the help popover that belongs to it.
    private void CloseSettings()
    {
        _settingsWindow.SetVisible(false);
        _settingsCentre?.Stop();
        CloseHelp();
    }

    // "Select effects…" → the existing effect picker (Plugin.SelectWindow.cs, unchanged), centred on Settings + front.
    private void OpenSelectEffects()
    {
        _selectWindow.SetVisible(true);
        _selectWindow.BringToFront();
        (_selectCentre ??= new CentreBurst(_selectWindow, () => _settingsWindow, _services.Framework, 0f)).Start();
    }

    // OnTargetHudUpdate: the help popover re-anchor + both centring bursts. Each is a single int compare when idle.
    private void TickSettingsUi()
    {
        HelpRepositionTick();
        _settingsCentre?.Tick();
        _selectCentre?.Tick();
    }
}
