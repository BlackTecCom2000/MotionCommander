using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Win11CopyDialog.Models;

namespace Win11CopyDialog.Helpers;

/// <summary>
/// Кинетический движок ультра-плавного скроллинга (Super Smooth Kinetic Scroll Engine).
/// Реализует 120+ FPS плавность с экспоненциальным демпфированием (Motion.Damp),
/// субпиксельной точностью (ScrollUnit=Pixel), динамическими пресетами и обратной связью.
/// </summary>
public static class SmoothScroll
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(SmoothScroll),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

    private static readonly DependencyProperty ControllerProperty =
        DependencyProperty.RegisterAttached(
            "Controller",
            typeof(SmoothScrollController),
            typeof(SmoothScroll),
            new PropertyMetadata(null));

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FrameworkElement element)
        {
            if ((bool)e.NewValue)
            {
                if (element.IsLoaded)
                {
                    Attach(element);
                }
                else
                {
                    element.Loaded += OnElementLoaded;
                }
            }
            else
            {
                element.Loaded -= OnElementLoaded;
                Detach(element);
            }
        }
    }

    private static void OnElementLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            element.Loaded -= OnElementLoaded;
            if (GetIsEnabled(element))
            {
                Attach(element);
            }
        }
    }

    private static void Attach(FrameworkElement element)
    {
        if (element.GetValue(ControllerProperty) != null) return;

        VirtualizingPanel.SetScrollUnit(element, ScrollUnit.Pixel);

        if (element is ScrollViewer scrollViewer)
        {
            var controller = new SmoothScrollController(scrollViewer);
            element.SetValue(ControllerProperty, controller);
        }
        else
        {
            element.PreviewMouseWheel += OnContainerPreviewMouseWheel;

            var innerSv = FindVisualChild<ScrollViewer>(element);
            if (innerSv != null && innerSv.GetValue(ControllerProperty) == null)
            {
                VirtualizingPanel.SetScrollUnit(innerSv, ScrollUnit.Pixel);
                var controller = new SmoothScrollController(innerSv);
                innerSv.SetValue(ControllerProperty, controller);
            }
        }
    }

    private static void Detach(FrameworkElement element)
    {
        element.PreviewMouseWheel -= OnContainerPreviewMouseWheel;
        if (element.GetValue(ControllerProperty) is SmoothScrollController controller)
        {
            controller.Dispose();
            element.ClearValue(ControllerProperty);
        }
    }

    private static void OnContainerPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled) return;
        if (!AppSettings.Instance.SmoothScrollEnabled) return;

        if (sender is DependencyObject d)
        {
            var sv = FindVisualChild<ScrollViewer>(d);
            if (sv != null)
            {
                var controller = sv.GetValue(ControllerProperty) as SmoothScrollController;
                if (controller == null)
                {
                    VirtualizingPanel.SetScrollUnit(sv, ScrollUnit.Pixel);
                    controller = new SmoothScrollController(sv);
                    sv.SetValue(ControllerProperty, controller);
                }
                controller.HandleWheel(e);
            }
        }
    }

    public static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent == null) return null;
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild) return typedChild;
            var descendant = FindVisualChild<T>(child);
            if (descendant != null) return descendant;
        }
        return null;
    }
}

internal sealed class SmoothScrollController : IDisposable
{
    private readonly ScrollViewer _sv;
    private double _targetVertical;
    private double _targetHorizontal;
    private double _lastAnimatedV = -1;
    private double _lastAnimatedH = -1;
    private bool _isAnimating;
    private long _lastRenderTicks;
    private DateTime _lastWheelTime = DateTime.MinValue;
    private double _velocityMultiplier = 1.0;

    public SmoothScrollController(ScrollViewer sv)
    {
        _sv = sv;
        _targetVertical = _sv.VerticalOffset;
        _targetHorizontal = _sv.HorizontalOffset;
        _sv.PreviewMouseWheel += OnPreviewMouseWheel;
        _sv.ScrollChanged += OnScrollChanged;
        _sv.Unloaded += OnUnloaded;
    }

    public void Dispose()
    {
        StopAnimation();
        _sv.PreviewMouseWheel -= OnPreviewMouseWheel;
        _sv.ScrollChanged -= OnScrollChanged;
        _sv.Unloaded -= OnUnloaded;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        StopAnimation();
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_isAnimating)
        {
            // Проверяем, вызвано ли изменение нашей собственной анимацией
            bool isOwnV = Math.Abs(e.VerticalOffset - _lastAnimatedV) < 1.0 || Math.Abs(e.VerticalOffset - _targetVertical) < 1.0;
            bool isOwnH = Math.Abs(e.HorizontalOffset - _lastAnimatedH) < 1.0 || Math.Abs(e.HorizontalOffset - _targetHorizontal) < 1.0;

            if (isOwnV && isOwnH)
            {
                return; // Не прерываем собственную анимацию
            }

            // Внешний скролл (ручное перетаскивание ползунка или клик страницы)
            _targetVertical = _sv.VerticalOffset;
            _targetHorizontal = _sv.HorizontalOffset;
            StopAnimation();
            return;
        }

        _targetVertical = _sv.VerticalOffset;
        _targetHorizontal = _sv.HorizontalOffset;
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        HandleWheel(e);
    }

    public void HandleWheel(MouseWheelEventArgs e)
    {
        if (e.Handled) return;

        // Если сглаживание отключено пользователем в Настройках — используем стандартный скролл
        if (!AppSettings.Instance.SmoothScrollEnabled) return;

        bool canScrollV = _sv.ScrollableHeight > 0;
        bool canScrollH = _sv.ScrollableWidth > 0;

        if (!canScrollV && !canScrollH) return;

        bool shiftHeld = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        bool isHorizontal = shiftHeld || (!canScrollV && canScrollH);

        double delta = e.Delta;

        // Проверка граничных условий для всплытия события родителю
        if (!isHorizontal)
        {
            if (delta > 0 && _sv.VerticalOffset <= 0.001) return;
            if (delta < 0 && _sv.VerticalOffset >= _sv.ScrollableHeight - 0.001) return;
        }
        else
        {
            if (delta > 0 && _sv.HorizontalOffset <= 0.001) return;
            if (delta < 0 && _sv.HorizontalOffset >= _sv.ScrollableWidth - 0.001) return;
        }

        // Тактильный щелчок при прокрутке колесика (если включен)
        if (AppSettings.Instance.ScrollHapticEnabled)
        {
            HapticAudio.PlayScrollTick();
        }

        var now = DateTime.UtcNow;
        var elapsed = (now - _lastWheelTime).TotalMilliseconds;
        _lastWheelTime = now;

        // Адаптивное кинетическое ускорение при быстром вращении колесика
        if (AppSettings.Instance.ScrollInertiaEnabled && elapsed < 160)
        {
            _velocityMultiplier = Math.Min(2.6, _velocityMultiplier + 0.35);
        }
        else
        {
            _velocityMultiplier = 1.0;
        }

        // Пользовательский шаг прокрутки из настроек (по умолчанию 110 px)
        double baseStep = AppSettings.Instance.ScrollStepSize;
        double step = (delta / 120.0) * baseStep * _velocityMultiplier;

        if (isHorizontal)
        {
            if (!_isAnimating) _targetHorizontal = _sv.HorizontalOffset;
            _targetHorizontal = Math.Clamp(_targetHorizontal - step, 0, _sv.ScrollableWidth);
        }
        else
        {
            if (!_isAnimating) _targetVertical = _sv.VerticalOffset;
            _targetVertical = Math.Clamp(_targetVertical - step, 0, _sv.ScrollableHeight);
        }

        e.Handled = true;
        StartAnimation();
    }

    private void StartAnimation()
    {
        if (!_isAnimating)
        {
            _isAnimating = true;
            _lastRenderTicks = Stopwatch.GetTimestamp();
            CompositionTarget.Rendering += OnRendering;
        }
    }

    private void StopAnimation()
    {
        if (_isAnimating)
        {
            _isAnimating = false;
            CompositionTarget.Rendering -= OnRendering;
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!_isAnimating) return;

        long now = Stopwatch.GetTimestamp();
        double dt = (double)(now - _lastRenderTicks) / Stopwatch.Frequency;
        _lastRenderTicks = now;

        if (dt > 0.05) dt = 0.05;
        if (dt <= 0) return;

        double currentV = _sv.VerticalOffset;
        double currentH = _sv.HorizontalOffset;

        bool doneV = true;
        bool doneH = true;

        double rate = AppSettings.Instance.ScrollDampingRate;

        if (_sv.ScrollableHeight > 0)
        {
            double nextV = Motion.Damp(currentV, _targetVertical, rate, dt);
            if (Math.Abs(nextV - _targetVertical) < 0.35)
            {
                nextV = _targetVertical;
            }
            else
            {
                doneV = false;
            }
            _lastAnimatedV = nextV;
            _sv.ScrollToVerticalOffset(nextV);
        }

        if (_sv.ScrollableWidth > 0)
        {
            double nextH = Motion.Damp(currentH, _targetHorizontal, rate, dt);
            if (Math.Abs(nextH - _targetHorizontal) < 0.35)
            {
                nextH = _targetHorizontal;
            }
            else
            {
                doneH = false;
            }
            _lastAnimatedH = nextH;
            _sv.ScrollToHorizontalOffset(nextH);
        }

        if (doneV && doneH)
        {
            StopAnimation();
        }
    }
}
