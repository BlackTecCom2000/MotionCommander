using System.Windows;
using System.Windows.Media;
using Win11CopyDialog.Helpers;
using Win11CopyDialog.Models;

namespace Win11CopyDialog.Controls;

/// <summary>
/// Fluid-прогресс: сглаженная заливка, бегущий блик, мягкое свечение,
/// indeterminate-режим для состояния "Подготовка".
/// </summary>
public sealed class FluidProgressBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(double), typeof(FluidProgressBar),
            new FrameworkPropertyMetadata(0.0));

    public static readonly DependencyProperty IsIndeterminateProperty =
        DependencyProperty.Register(nameof(IsIndeterminate), typeof(bool), typeof(FluidProgressBar),
            new FrameworkPropertyMetadata(false));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public bool IsIndeterminate
    {
        get => (bool)GetValue(IsIndeterminateProperty);
        set => SetValue(IsIndeterminateProperty, value);
    }

    private double _display;
    private DateTime _last = DateTime.Now;
    private double _time;
    private bool _running;
    private Brush _accent = new SolidColorBrush(Color.FromRgb(0, 120, 212));
    private Brush _track = new SolidColorBrush(Color.FromRgb(227, 227, 227));

    public FluidProgressBar()
    {
        Height = 8;
        Loaded += (_, _) =>
        {
            RefreshBrushes();
            ThemeManager.Instance.PropertyChanged += OnThemeChanged;
            _last = DateTime.Now;
            _running = true;
            CompositionTarget.Rendering += OnRendering;
        };
        Unloaded += (_, _) =>
        {
            _running = false;
            CompositionTarget.Rendering -= OnRendering;
            ThemeManager.Instance.PropertyChanged -= OnThemeChanged;
        };
    }

    private void OnThemeChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e) =>
        Dispatcher.Invoke(RefreshBrushes);

    private void RefreshBrushes()
    {
        var r = Application.Current?.Resources;
        if (r == null) return;
        if (r["AccentBrush"] is Brush a) _accent = a;
        if (r["ProgressTrackBrush"] is Brush t) _track = t;
    }

    private void OnRendering(object? s, EventArgs e)
    {
        if (!_running) return;
        var now = DateTime.Now;
        double dt = Math.Min(0.05, (now - _last).TotalSeconds);
        _last = now;
        _time += dt;
        _display = Motion.Damp(_display, Motion.Clamp01(Value / 100.0), 8, dt);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double w = ActualWidth, h = ActualHeight;
        if (w < 4 || h < 2) return;
        double rad = h / 2;

        var ac = ((SolidColorBrush)_accent).Color;

        // трек
        dc.DrawRoundedRectangle(_track, null, new Rect(0, 0, w, h), rad, rad);

        if (IsIndeterminate)
        {
            // бегущий блок подготовки
            double bw = Math.Max(60, w * 0.25);
            double x = ((_time * 160) % (w + bw)) - bw;
            var grad = new LinearGradientBrush(
                Color.FromArgb(0, ac.R, ac.G, ac.B), ac, 0.5);
            grad.Freeze();
            dc.DrawRoundedRectangle(grad, null, new Rect(x, 0, bw, h), rad, rad);
            return;
        }

        double fw = w * Motion.Clamp01(_display);
        if (fw < 0.5) return;

        // свечение под заливкой
        var glow = new SolidColorBrush(Color.FromArgb(44, ac.R, ac.G, ac.B));
        glow.Freeze();
        dc.DrawRoundedRectangle(glow, null, new Rect(0, 0, fw, h), rad, rad);

        // заливка с градиентом
        var light = Color.FromRgb(
            (byte)Math.Min(255, ac.R + 46), (byte)Math.Min(255, ac.G + 46), (byte)Math.Min(255, ac.B + 46));
        var fill = new LinearGradientBrush(ac, light, new Point(0, 0.5), new Point(1, 0.5));
        fill.Freeze();
        dc.DrawRoundedRectangle(fill, null, new Rect(0, 0, fw, h), rad, rad);

        // бегущий блик по заливке
        double hw = 54;
        double hx = ((_time * 120) % (fw + hw * 2)) - hw;
        if (hx > -hw && hx < fw)
        {
            var clip = new RectangleGeometry(new Rect(0, 0, fw, h), rad, rad);
            clip.Freeze();
            dc.PushClip(clip);
            var shine = new LinearGradientBrush(
                Color.FromArgb(0, 255, 255, 255),
                Color.FromArgb(110, 255, 255, 255), new Point(0, 0.5), new Point(1, 0.5));
            shine.Freeze();
            dc.DrawRectangle(shine, null, new Rect(hx, 0, hw, h));
            dc.Pop();
        }
    }
}
