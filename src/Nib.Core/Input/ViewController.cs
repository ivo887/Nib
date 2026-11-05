using System;
using System.Numerics;
using Nib.Core.Geometry;
using Nib.Core.Model;

namespace Nib.Core.Input;

/// <summary>Maps page units to surface DIPs: screen = page × Zoom + Offset.</summary>
public struct ViewState : IEquatable<ViewState>
{
    public float Zoom;
    public float OffsetX;
    public float OffsetY;

    public ViewState(float zoom, float offsetX, float offsetY) { Zoom = zoom; OffsetX = offsetX; OffsetY = offsetY; }

    public readonly Vector2 ToScreen(float x, float y) => new(x * Zoom + OffsetX, y * Zoom + OffsetY);
    public readonly Vector2 ToPage(float sx, float sy) => new((sx - OffsetX) / Zoom, (sy - OffsetY) / Zoom);
    public readonly Matrix3x2 PageToScreen => new(Zoom, 0, 0, Zoom, OffsetX, OffsetY);
    public readonly RectF VisiblePageRect(float viewportW, float viewportH) =>
        new((0 - OffsetX) / Zoom, (0 - OffsetY) / Zoom, (viewportW - OffsetX) / Zoom, (viewportH - OffsetY) / Zoom);

    public readonly bool Equals(ViewState o) => Zoom == o.Zoom && OffsetX == o.OffsetX && OffsetY == o.OffsetY;
    public override readonly bool Equals(object? obj) => obj is ViewState v && Equals(v);
    public override readonly int GetHashCode() => HashCode.Combine(Zoom, OffsetX, OffsetY);
}

public enum ZoomPreset { PageWidth, Actual, FitPage }

/// <summary>
/// Pan / zoom state and rules for one surface: clamping to fixed pages, zoom presets, keeping the
/// reading position stable across window resizes and rotation. Pure math, unit-tested.
/// </summary>
public sealed class ViewController
{
    public const float MinZoom = 0.1f;
    public const float MaxZoom = 8f;
    /// <summary>Gap between page and viewport edge, DIPs.</summary>
    public float Margin { get; set; } = 24f;

    public ViewState State;
    public float ViewportWidth { get; private set; } = 1;
    public float ViewportHeight { get; private set; } = 1;
    public PageLayout Layout { get; private set; } = new();
    /// <summary>The preset the user last chose; reapplied on resize until they zoom manually.</summary>
    public ZoomPreset? StickyPreset { get; private set; } = ZoomPreset.PageWidth;

    public ViewController() { State = new ViewState(1, 0, 0); }

    public RectF VisiblePageRect => State.VisiblePageRect(ViewportWidth, ViewportHeight);

    /// <summary>Switches to a page. Pass a remembered view for that page to restore its position.</summary>
    public void SetPage(PageLayout layout, ViewState? remembered)
    {
        Layout = layout;
        if (remembered is { } v && v.Zoom > 0)
        {
            State = v;
            StickyPreset = null;
            Clamp();
        }
        else
        {
            ApplyPreset(Layout.Mode == PageMode.Fixed ? ZoomPreset.PageWidth : ZoomPreset.Actual);
            State.OffsetY = Margin;
            Clamp();
        }
    }

    public void UpdateLayout(PageLayout layout)
    {
        Layout = layout;
        Clamp();
    }

    /// <summary>
    /// Called on resize, rotation or taskbar/keyboard changes. Re-applies the sticky preset or keeps the
    /// page point under the viewport's top-center fixed, so text the user was reading stays in place.
    /// </summary>
    public void SetViewport(float width, float height)
    {
        width = MathF.Max(width, 1);
        height = MathF.Max(height, 1);
        if (width == ViewportWidth && height == ViewportHeight) return;
        var anchor = State.ToPage(ViewportWidth * 0.5f, 0);
        ViewportWidth = width;
        ViewportHeight = height;
        if (StickyPreset is { } preset)
        {
            float oldTopPage = anchor.Y;
            ApplyPreset(preset, keepSticky: true);
            State.OffsetY = -oldTopPage * State.Zoom;
        }
        else
        {
            State.OffsetX = width * 0.5f - anchor.X * State.Zoom;
            State.OffsetY = -anchor.Y * State.Zoom;
        }
        Clamp();
    }

    public void Pan(float dx, float dy)
    {
        State.OffsetX += dx;
        State.OffsetY += dy;
        Clamp();
    }

    /// <summary>Zooms by <paramref name="factor"/> keeping the screen point (cx, cy) fixed.</summary>
    public void ZoomAt(float factor, float cx, float cy)
    {
        float z = Math.Clamp(State.Zoom * factor, MinZoom, MaxZoom);
        float f = z / State.Zoom;
        State.OffsetX = cx - (cx - State.OffsetX) * f;
        State.OffsetY = cy - (cy - State.OffsetY) * f;
        State.Zoom = z;
        StickyPreset = null;
        Clamp();
    }

    public void ApplyPreset(ZoomPreset preset) => ApplyPreset(preset, keepSticky: true);

    private void ApplyPreset(ZoomPreset preset, bool keepSticky)
    {
        float pageW = Layout.Mode == PageMode.Fixed ? Layout.SheetWidth : 816f; // 8.5" for infinite canvases
        float sheetH = Layout.Mode == PageMode.Fixed ? Layout.SheetHeight : 1056f;
        float availW = MathF.Max(ViewportWidth - 2 * Margin, 50);
        float availH = MathF.Max(ViewportHeight - 2 * Margin, 50);
        float topPage = State.ToPage(0, 0).Y;
        float z = preset switch
        {
            ZoomPreset.PageWidth => availW / pageW,
            ZoomPreset.FitPage => MathF.Min(availW / pageW, availH / sheetH),
            _ => 1f,
        };
        State.Zoom = Math.Clamp(z, MinZoom, MaxZoom);
        State.OffsetX = (ViewportWidth - pageW * State.Zoom) * 0.5f;
        if (preset == ZoomPreset.FitPage)
        {
            // Show the whole sheet that was at the top of the view.
            int sheet = Layout.Mode == PageMode.Fixed ? Math.Clamp((int)MathF.Floor(MathF.Max(topPage, 0) / sheetH), 0, Layout.SheetCount - 1) : 0;
            State.OffsetY = (ViewportHeight - sheetH * State.Zoom) * 0.5f - sheet * sheetH * State.Zoom;
        }
        else
        {
            State.OffsetY = -topPage * State.Zoom;
        }
        StickyPreset = keepSticky ? preset : null;
        Clamp();
    }

    /// <summary>Scrolls minimally so <paramref name="pageRect"/> is visible (e.g. caret above the touch keyboard).</summary>
    public void EnsureVisible(RectF pageRect, float paddingDips = 16)
    {
        var tl = State.ToScreen(pageRect.Left, pageRect.Top);
        var br = State.ToScreen(pageRect.Right, pageRect.Bottom);
        float dx = 0, dy = 0;
        if (br.Y > ViewportHeight - paddingDips) dy = ViewportHeight - paddingDips - br.Y;
        if (tl.Y + dy < paddingDips) dy = paddingDips - tl.Y;
        if (br.X > ViewportWidth - paddingDips) dx = ViewportWidth - paddingDips - br.X;
        if (tl.X + dx < paddingDips) dx = paddingDips - tl.X;
        if (dx != 0 || dy != 0) Pan(dx, dy);
    }

    /// <summary>Keeps fixed pages in reach: centered when narrower than the view, never scrolled past their edges.</summary>
    public void Clamp()
    {
        State.Zoom = Math.Clamp(State.Zoom, MinZoom, MaxZoom);
        if (Layout.Mode != PageMode.Fixed) return;
        var b = Layout.Bounds;
        float w = b.Width * State.Zoom, h = b.Height * State.Zoom;
        if (w + 2 * Margin <= ViewportWidth) State.OffsetX = (ViewportWidth - w) * 0.5f;
        else State.OffsetX = Math.Clamp(State.OffsetX, ViewportWidth - w - Margin, Margin);
        if (h + 2 * Margin <= ViewportHeight) State.OffsetY = Math.Clamp(State.OffsetY, Margin, ViewportHeight - h - Margin);
        else State.OffsetY = Math.Clamp(State.OffsetY, ViewportHeight - h - Margin, Margin);
    }
}

/// <summary>Decelerating fling after a pan, frame-rate independent.</summary>
public sealed class InertiaAnimator
{
    private const float TimeConstant = 0.325f; // seconds, iOS-like feel
    private const float StopSpeed = 12f;        // DIPs per second
    private float _vx, _vy;

    public bool IsActive { get; private set; }

    public void Start(float vx, float vy)
    {
        float speed = MathF.Sqrt(vx * vx + vy * vy);
        if (speed < 80f) { Stop(); return; }
        const float maxSpeed = 9000f;
        if (speed > maxSpeed) { vx *= maxSpeed / speed; vy *= maxSpeed / speed; }
        _vx = vx; _vy = vy;
        IsActive = true;
    }

    public void Stop() { IsActive = false; _vx = _vy = 0; }

    /// <summary>Advances by <paramref name="dt"/> seconds; returns the displacement to apply.</summary>
    public bool Step(float dt, out float dx, out float dy)
    {
        dx = dy = 0;
        if (!IsActive) return false;
        dt = Math.Clamp(dt, 0f, 0.1f);
        float decay = MathF.Exp(-dt / TimeConstant);
        // Exact integral of v·e^(−t/τ) over the step.
        float k = TimeConstant * (1f - decay);
        dx = _vx * k;
        dy = _vy * k;
        _vx *= decay;
        _vy *= decay;
        if (MathF.Sqrt(_vx * _vx + _vy * _vy) < StopSpeed) Stop();
        return true;
    }
}
